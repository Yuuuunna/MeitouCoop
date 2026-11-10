using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Meitou.Networking.VNext.Transport;

public readonly record struct QuicNativeResourceCounts(ulong Endpoints, ulong Runtimes, ulong Connections, ulong Streams, ulong SendBuffers);

public enum QuicLane : uint { Control, Lifecycle, Bulk, State }
public enum QuicTransportEventKind : uint
{
    Connected = 1, StreamBytes, Datagram, SendBufferReleased, DatagramTerminal,
    Closed, StreamReceiveEnded, ShutdownInitiated,
}

/// <summary>Stream bytes may be fragmented/coalesced. Framing and application ACKs belong above this adapter.</summary>
public readonly record struct QuicTransportEvent(QuicTransportEventKind Kind, QuicLane Lane,
    uint NativeStatus, ulong SendToken, int Length);

/// <summary>Byte counters include encryption, ACK and retransmission UDP payloads. Packet counters are QUIC packets,
/// not UDP datagrams: packet coalescing prevents exact IP/UDP header accounting from these statistics.</summary>
public readonly record struct QuicTransportStatistics(ulong SentUdpPayloadBytes, ulong ReceivedUdpPayloadBytes,
    ulong SentQuicPackets, ulong ReceivedQuicPackets, ulong RttMicroseconds, ushort MaxDatagramLength,
    ulong NativeOutstandingBytes, ulong NativeOutstandingSends, ulong QueuedReceiveBytes, ulong QueuedEvents);

/// <summary>
/// A client/accepted connection, or a listener accepting up to 32 independently owned connections.
/// Connections have three client-opened reliable streams and one unreliable datagram lane.
/// TLS authenticates the server. Application admission/authentication must complete before gameplay.
/// No .NET QUIC OS dependency, early data, certificate bypass or fallback transport.
/// </summary>
public sealed unsafe class NativeQuicEndpoint : IDisposable
{
    public const string MsQuicSha256 = "290b6c8716ac09eea043913f86164e0b2f5096c666c9f1ca81c0c5c1864e1cb0";
    public const int MaximumChunkBytes = 65536;
    private readonly object gate = new();
    private readonly NativeHandle handle;
    private readonly StartDelegate start;
    private readonly OpenLanesDelegate openLanes;
    private readonly SendDelegate send;
    private readonly PollDelegate poll;
    private readonly StatisticsDelegate statistics;
    private readonly AcceptDelegate accept;
    private readonly ListenPortDelegate listenPort;
    private readonly ResourcesDelegate resources;
    private readonly DestroyDelegate destroyEndpoint;
    private readonly string shimPath;
    private bool started, lanesOpened;
    public bool IsServer { get; }
    public bool IsListener { get; }

    /// <param name="trustedShimSha256">Expected hash from a trusted build/deployment manifest, never an untrusted download.</param>
    /// <param name="credentialPath">Server: PKCS12 file. Client: trusted CA PEM file.</param>
    public NativeQuicEndpoint(string nativeDirectory, string trustedShimSha256, bool server,
        string credentialPath, string privateKeyPassword = "")
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The pinned transport artifact supports Windows x64 only.");
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        ArgumentNullException.ThrowIfNull(privateKeyPassword);
        ValidateText(credentialPath, nameof(credentialPath));
        if (privateKeyPassword.Contains('\0')) throw new ArgumentException("Embedded NUL.", nameof(privateKeyPassword));
        var directory = Path.GetFullPath(nativeDirectory);
        var shim = Path.Combine(directory, "Meitou.QuicTransport.dll");
        shimPath = shim;
        var msquic = Path.Combine(directory, "msquic.dll");
        VerifyHash(shim, trustedShimSha256);
        VerifyHash(msquic, MsQuicSha256);
        credentialPath = Path.GetFullPath(credentialPath);
        if (!File.Exists(credentialPath)) throw new FileNotFoundException("Transport credentials missing.", credentialPath);
        var module = NativeLibrary.Load(shim);
        nint endpoint = 0;
        DestroyDelegate? destroy = null;
        try
        {
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
            destroy = Export<DestroyDelegate>("mtq_destroy");
            destroyEndpoint = destroy;
            var create = Export<CreateDelegate>("mtq_create");
            start = Export<StartDelegate>("mtq_start");
            openLanes = Export<OpenLanesDelegate>("mtq_open_lanes");
            send = Export<SendDelegate>("mtq_send");
            poll = Export<PollDelegate>("mtq_poll");
            statistics = Export<StatisticsDelegate>("mtq_statistics");
            accept = Export<AcceptDelegate>("mtq_accept");
            listenPort = Export<ListenPortDelegate>("mtq_listener_port");
            resources = Export<ResourcesDelegate>("mtq_resources");
            Check(create(msquic, server ? 1 : 0, credentialPath, privateKeyPassword, out endpoint), "create");
            if (endpoint == 0) throw new IOException("Native QUIC create returned no endpoint.");
            handle = new NativeHandle(endpoint, module, destroy);
            IsServer = server; IsListener = server;
        }
        catch
        {
            if (endpoint != 0) destroy?.Invoke(endpoint);
            NativeLibrary.Free(module);
            throw;
        }
    }

    private NativeQuicEndpoint(NativeQuicEndpoint listener, nint endpoint)
    {
        // Each accepted SafeHandle holds its own OS reference to the shim. The native runtime
        // separately shares registration/configuration until the final peer is destroyed.
        var module = NativeLibrary.Load(listener.shimPath);
        destroyEndpoint = listener.destroyEndpoint;
        try { handle = new NativeHandle(endpoint, module, destroyEndpoint); }
        catch { NativeLibrary.Free(module); throw; }
        shimPath = listener.shimPath;
        start = listener.start; openLanes = listener.openLanes; send = listener.send;
        poll = listener.poll; statistics = listener.statistics; accept = listener.accept; resources = listener.resources;
        listenPort = listener.listenPort;
        IsServer = true; started = true;
    }

    /// <summary>Accepted peers outlive the listener and must each be disposed. The native listener
    /// owns unclaimed peers; total live peers per listener is capped at 32.</summary>
    public bool TryAccept(out NativeQuicEndpoint? peer)
    {
        lock (gate)
        {
            RequireOpen();
            if (!IsListener) throw new InvalidOperationException("Only a listener accepts peers.");
            var endpoint = accept(handle.DangerousGetHandle());
            if (endpoint == 0) { peer = null; return false; }
            try { peer = new NativeQuicEndpoint(this, endpoint); return true; }
            catch
            {
                destroyEndpoint(endpoint);
                throw;
            }
        }
    }

    public void Listen(ushort port) => Listen(IPAddress.Any, port);

    public void Listen(IPAddress address, ushort port)
    {
        ArgumentNullException.ThrowIfNull(address);
        lock (gate)
        {
            RequireOpen();
            if (!IsServer || started) throw new InvalidOperationException("A server listens once.");
            started = true;
            Check(start(handle.DangerousGetHandle(), address.ToString(), null, port), "listen");
        }
    }

    public ushort LocalPort
    {
        get
        {
            lock (gate)
            {
                RequireOpen();
                Check(listenPort(handle.DangerousGetHandle(), out ushort port), "listener address");
                return port;
            }
        }
    }

    /// <summary>Numeric destination is separate from the DNS server identity validated by TLS.</summary>
    public void Connect(IPAddress destination, ushort port, string serverName)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ValidateText(serverName, nameof(serverName));
        lock (gate)
        {
            RequireOpen();
            if (IsServer || started || port == 0) throw new InvalidOperationException("A client connects once on a nonzero UDP port.");
            started = true;
            Check(start(handle.DangerousGetHandle(), destination.ToString(), serverName, port), "connect");
        }
    }

    /// <summary>Call once on the client after Connected. Server receives lane handles as streams arrive.</summary>
    public void OpenReliableLanes()
    {
        lock (gate)
        {
            RequireOpen();
            if (IsServer || lanesOpened) throw new InvalidOperationException("Only the client opens the fixed lanes, once.");
            lanesOpened = true;
            Check(openLanes(handle.DangerousGetHandle()), "open lanes");
        }
    }

    /// <summary>Copies before return; false means bounded native capacity or a not-yet-arrived server lane.
    /// SendBufferReleased is ownership completion, never application/peer receipt or bulk ACK credit.</summary>
    public bool TrySend(QuicLane lane, ReadOnlySpan<byte> bytes, ulong sendToken)
    {
        if (!Enum.IsDefined(lane)) throw new ArgumentOutOfRangeException(nameof(lane));
        if (bytes.IsEmpty || bytes.Length > (lane == QuicLane.State ? 1150 : MaximumChunkBytes))
            throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (gate)
        {
            RequireOpen();
            fixed (byte* pointer = bytes)
            {
                var status = send(handle.DangerousGetHandle(), (uint)lane, pointer, (uint)bytes.Length, sendToken);
                if (status == 2) return false;
                Check(status, "send");
                return true;
            }
        }
    }

    /// <summary>Copy/enqueue callbacks never decode application data. Queue overflow closes the connection and throws;
    /// a caller must treat any transport exception as terminal and dispose this endpoint.</summary>
    public bool TryPoll(Span<byte> destination, out QuicTransportEvent transportEvent)
    {
        lock (gate)
        {
            RequireOpen();
            Span<uint> metadata = stackalloc uint[4];
            ulong token = 0;
            // Native ABI requires a nonnull destination even for empty events.
            byte empty = 0;
            fixed (byte* buffer = destination)
            fixed (uint* fields = metadata)
            {
                var status = poll(handle.DangerousGetHandle(), fields, &token,
                    destination.IsEmpty ? &empty : buffer, (uint)destination.Length);
                transportEvent = default;
                if (status == 0) return false;
                if (status == -3) throw new ArgumentException($"Receive buffer needs {metadata[3]} bytes; event retained.", nameof(destination));
                if (status < 0) throw new IOException("Native transport event queue overflow or ABI failure; connection failed closed.");
                transportEvent = new((QuicTransportEventKind)metadata[0], (QuicLane)metadata[1], metadata[2], token, checked((int)metadata[3]));
                return true;
            }
        }
    }

    public QuicTransportStatistics GetStatistics()
    {
        lock (gate)
        {
            RequireOpen();
            Span<ulong> values = stackalloc ulong[10];
            fixed (ulong* pointer = values) Check(statistics(handle.DangerousGetHandle(), pointer), "statistics");
            return new(values[0], values[1], values[2], values[3], values[4], checked((ushort)values[5]), values[6], values[7], values[8], values[9]);
        }
    }

    /// <summary>Process-local shim diagnostics for leak checks; concurrent endpoints share these counts.</summary>
    public QuicNativeResourceCounts GetLiveResources()
    {
        lock (gate)
        {
            RequireOpen();
            Span<ulong> counts = stackalloc ulong[5];
            fixed (ulong* pointer = counts) resources(pointer);
            return new(counts[0], counts[1], counts[2], counts[3], counts[4]);
        }
    }

    public void Dispose() { lock (gate) handle.Dispose(); }
    private void RequireOpen() => ObjectDisposedException.ThrowIf(handle.IsClosed, this);
    private static void Check(uint status, string operation)
    {
        // MsQuic QUIC_STATUS on Windows is HRESULT; positive success codes are allowed.
        if (status is 1 or 2 || unchecked((int)status) < 0) throw new IOException($"Native QUIC {operation} failed (0x{status:x8}).");
    }
    private static void ValidateText(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Contains('\0')) throw new ArgumentException("Embedded NUL.", name);
    }
    private static void VerifyHash(string file, string expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);
        var digest = Convert.FromHexString(expected);
        using var stream = File.OpenRead(file);
        if (digest.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), digest))
            throw new InvalidDataException("Native transport artifact SHA256 mismatch.");
    }

    private sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly nint library;
        private readonly DestroyDelegate close;
        public NativeHandle(nint value, nint module, DestroyDelegate destroy) : base(true)
        {
            library = module;
            close = destroy;
            SetHandle(value);
        }
        protected override bool ReleaseHandle() { close(handle); NativeLibrary.Free(library); return true; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint CreateDelegate([MarshalAs(UnmanagedType.LPWStr)] string dll, int server,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string credential, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, out nint endpoint);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(nint endpoint);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint StartDelegate(nint endpoint,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? host, [MarshalAs(UnmanagedType.LPUTF8Str)] string? serverName, ushort port);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint OpenLanesDelegate(nint endpoint);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint SendDelegate(nint endpoint, uint lane, byte* bytes, uint length, ulong token);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PollDelegate(nint endpoint, uint* metadata, ulong* token, byte* bytes, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint StatisticsDelegate(nint endpoint, ulong* values);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint AcceptDelegate(nint endpoint);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ListenPortDelegate(nint endpoint, out ushort port);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResourcesDelegate(ulong* counts);
}
