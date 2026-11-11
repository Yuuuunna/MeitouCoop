using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meitou.Networking.VNext.Transport;

namespace Meitou.Tests;

public sealed class NativeQuicTransportTests
{
    [Fact, Trait("Category", "Slow")]
    public void PinnedNativeAdapter_TwoPeersAllLanesAndListenerLifetime()
    {
        var directory = Environment.GetEnvironmentVariable("MEITOU_QUIC_NATIVE_DIRECTORY");
        var hash = Environment.GetEnvironmentVariable("MEITOU_QUIC_SHIM_SHA256");
        Assert.SkipWhen(!OperatingSystem.IsWindows() || directory is null || hash is null,
            "Build QuicTransport and supply trusted artifact directory/hash to run the native integration test.");
        using var credentials = new Credentials();
        using var monitor = new NativeQuicEndpoint(directory!, hash!, true, credentials.Pfx);
        var baseline = monitor.GetLiveResources();
        using var listener = new NativeQuicEndpoint(directory!, hash!, true, credentials.Pfx);
        using var client1 = new NativeQuicEndpoint(directory!, hash!, false, credentials.Pem);
        using var client2 = new NativeQuicEndpoint(directory!, hash!, false, credentials.Pem);
        var port = FreePort(); listener.Listen(port);
        client1.Connect(IPAddress.Loopback, port, "localhost");
        client2.Connect(IPAddress.Loopback, port, "localhost");
        var peers = new List<NativeQuicEndpoint>();
        try
        {
            Until(() => { while (listener.TryAccept(out var peer)) peers.Add(peer!); return peers.Count == 2; });
            var endpoints = new[] { client1, client2, peers[0], peers[1] };
            var connected = new HashSet<NativeQuicEndpoint>();
            Until(() => { foreach (var endpoint in endpoints) Drain(endpoint, e => { if (e.Kind == QuicTransportEventKind.Connected) connected.Add(endpoint); }); return connected.Count == 4; });
            client1.OpenReliableLanes(); client2.OpenReliableLanes();
            // Claimed connections own their shared native registration after listener disposal.
            listener.Dispose();
            byte[] payload = [21, 42, 63, 84];
            foreach (var client in new[] { client1, client2 })
                foreach (var lane in new[] { QuicLane.Control, QuicLane.Lifecycle, QuicLane.Bulk })
                    Assert.True(client.TrySend(lane, payload, (ulong)lane + 10));
            var received = new HashSet<(NativeQuicEndpoint, QuicLane)>();
            var streamBytes = new Dictionary<(NativeQuicEndpoint, QuicLane), List<byte>>();
            void Receive(NativeQuicEndpoint endpoint)
            {
                var buffer = new byte[NativeQuicEndpoint.MaximumChunkBytes];
                while (endpoint.TryPoll(buffer, out var e))
                {
                    if (e.Kind is not (QuicTransportEventKind.StreamBytes or QuicTransportEventKind.Datagram)) continue;
                    var key = (endpoint, e.Lane);
                    if (!streamBytes.TryGetValue(key, out var bytes)) streamBytes[key] = bytes = [];
                    bytes.AddRange(buffer.AsSpan(0, e.Length).ToArray());
                    if (bytes.Count >= payload.Length) { Assert.Equal(payload, bytes); received.Add(key); }
                }
            }
            Until(() => { foreach (var peer in peers) Receive(peer); return received.Count == 6; });
            foreach (var peer in peers)
                foreach (var lane in new[] { QuicLane.Control, QuicLane.Lifecycle, QuicLane.Bulk })
                    Assert.True(peer.TrySend(lane, payload, (ulong)lane + 20));
            Until(() => { Receive(client1); Receive(client2); return received.Count == 12; });
            Until(() => endpoints.All(e => e.GetStatistics().MaxDatagramLength >= payload.Length));
            foreach (var endpoint in endpoints) Assert.True(endpoint.TrySend(QuicLane.State, payload, 30));
            Until(() => { foreach (var endpoint in endpoints) Receive(endpoint); return received.Count == 16; });
            Until(() => endpoints.All(e => e.GetStatistics().NativeOutstandingSends == 0));
            foreach (var endpoint in endpoints)
            {
                var statistics = endpoint.GetStatistics();
                Assert.True(statistics.SentUdpPayloadBytes > 0);
                Assert.True(statistics.ReceivedUdpPayloadBytes > 0);
                Assert.Equal(0ul, statistics.NativeOutstandingBytes);
            }
            // Destruction with native work outstanding exercises cancellation callbacks/barriers.
            foreach (var endpoint in endpoints) Assert.True(endpoint.TrySend(QuicLane.Bulk, new byte[65536], 40));
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
        client1.Dispose(); client2.Dispose();
        Assert.Equal(baseline, monitor.GetLiveResources());
    }

    [Theory, Trait("Category", "Slow")]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedNativeAdapter_RejectsWrongServerIdentityOrCa(bool wrongCa)
    {
        var directory = Environment.GetEnvironmentVariable("MEITOU_QUIC_NATIVE_DIRECTORY");
        var hash = Environment.GetEnvironmentVariable("MEITOU_QUIC_SHIM_SHA256");
        Assert.SkipWhen(!OperatingSystem.IsWindows() || directory is null || hash is null, "Native integration artifact not configured.");
        using var credentials = new Credentials();
        using var listener = new NativeQuicEndpoint(directory!, hash!, true, credentials.Pfx);
        using var differentCredentials = new Credentials();
        using var client = new NativeQuicEndpoint(directory!, hash!, false, wrongCa ? differentCredentials.Pem : credentials.Pem);
        var port = FreePort(); listener.Listen(port); client.Connect(IPAddress.Loopback, port, wrongCa ? "localhost" : "incorrect.invalid");
        bool closed = false; uint failure = 0;
        NativeQuicEndpoint? peer = null;
        try { Until(() =>
        {
            if (peer is null) listener.TryAccept(out peer);
            Drain(client, e => { Assert.NotEqual(QuicTransportEventKind.Connected, e.Kind); closed |= e.Kind == QuicTransportEventKind.Closed; if (e.Kind == QuicTransportEventKind.ShutdownInitiated) failure = e.NativeStatus; });
            return closed;
        });
            // Pinned Windows QUIC_STATUS_TLS_ALERT: unknown_ca(48) / bad_certificate(42), not timeout.
            Assert.Equal(wrongCa ? 0x80410130u : 0x8041012au, failure); } finally { peer?.Dispose(); }
    }

    [Fact, Trait("Category", "Slow")]
    public void PinnedNativeAdapter_OverflowIsBoundedAndImmediatelyTerminal()
    {
        var directory = Environment.GetEnvironmentVariable("MEITOU_QUIC_NATIVE_DIRECTORY");
        var hash = Environment.GetEnvironmentVariable("MEITOU_QUIC_SHIM_SHA256");
        Assert.SkipWhen(!OperatingSystem.IsWindows() || directory is null || hash is null, "Native integration artifact not configured.");
        using var credentials = new Credentials();
        using var listener = new NativeQuicEndpoint(directory!, hash!, true, credentials.Pfx);
        using var client = new NativeQuicEndpoint(directory!, hash!, false, credentials.Pem);
        var port = FreePort(); listener.Listen(port); client.Connect(IPAddress.Loopback, port, "localhost");
        NativeQuicEndpoint? peer = null;
        try
        {
            bool connected = false;
            Until(() =>
            {
                if (peer is null) listener.TryAccept(out peer);
                Drain(client, e => connected |= e.Kind == QuicTransportEventKind.Connected);
                return connected;
            });
            client.OpenReliableLanes();
            // Do not drain the client's completion events. Each send owns one bounded context.
            for (ulong token = 1; token <= 129; ++token)
            {
                Assert.True(client.TrySend(QuicLane.Control, new byte[] { 21 }, token));
                Until(() => client.GetStatistics().NativeOutstandingSends == 0);
            }
            // The callback updates counters before enqueueing: wait until its overflow flag is visible
            // via a rejected submission (without consuming any of the already queued events).
            Until(() =>
            {
                try { client.TrySend(QuicLane.Control, new byte[] { 21 }, 130); return false; }
                catch (IOException) { return true; }
            });
            var stats = client.GetStatistics();
            Assert.Equal(128ul, stats.QueuedEvents);
            Assert.True(stats.NativeOutstandingBytes <= 262144);
            Assert.Throws<IOException>(() => client.TryPoll(new byte[65536], out _));
        }
        finally { peer?.Dispose(); }
    }

    private static void Drain(NativeQuicEndpoint endpoint, Action<QuicTransportEvent> action)
    {
        var buffer = new byte[NativeQuicEndpoint.MaximumChunkBytes];
        while (endpoint.TryPoll(buffer, out var e)) action(e);
    }
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Native QUIC integration did not complete."); Thread.Sleep(1); }
    }
    private static ushort FreePort() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return checked((ushort)((IPEndPoint)socket.Client.LocalEndPoint!).Port); }
    private sealed class Credentials : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Meitou.QuicTransport-" + Guid.NewGuid().ToString("N"));
        public string Pfx => Path.Combine(_directory, "server.pfx");
        public string Pem => Path.Combine(_directory, "ca.pem");
        public Credentials()
        {
            Directory.CreateDirectory(_directory);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            File.WriteAllBytes(Pfx, certificate.Export(X509ContentType.Pkcs12, ""));
            File.WriteAllText(Pem, certificate.ExportCertificatePem());
        }
        public void Dispose() { File.Delete(Pfx); File.Delete(Pem); Directory.Delete(_directory); }
    }
}
