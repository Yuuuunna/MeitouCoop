using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Meitou.Networking.VNext;
using Meitou.Networking.VNext.Transport;

namespace Meitou.Server;

/// <summary>Bounded native QUIC game listener. Each accepted endpoint has one independent owner.</summary>
public sealed class QuicGameServer : IAsyncDisposable
{
    readonly NativeQuicEndpoint listener;
    readonly SecureServerPeer handler;
    readonly CancellationTokenSource stop;
    readonly ConcurrentDictionary<long, TaskCompletionSource> peers = new();
    readonly int maximumConnections;
    readonly Lock lifecycle = new();
    Task? shutdown;
    long nextId, rejected, failures;
    string? temporaryCertificate;
    public Task Completion { get; }
    public IPEndPoint Endpoint { get; }
    public int ActiveConnections => peers.Count;
    public long RejectedConnections => Interlocked.Read(ref rejected);
    public long FailedConnections => Interlocked.Read(ref failures);
    public string? LastFailureType { get; private set; }
    public QuicGameServer(IPEndPoint address, QuicGameTransportOptions options, string certificateFile,
        SecureServerPeer handler, int maximumConnections = 32)
        : this(address, options.NativeDirectory, options.ShimSha256, certificateFile, handler, maximumConnections) { }
    public QuicGameServer(IPEndPoint address, X509Certificate2 certificate, SecureServerPeer handler, int maximumConnections = 32)
        : this(address, Export(certificate), handler, maximumConnections) { }
    QuicGameServer(IPEndPoint address, (string Directory, string Hash, string Certificate) config, SecureServerPeer handler, int maximumConnections)
        : this(address, config.Directory, config.Hash, config.Certificate, handler, maximumConnections)
        => temporaryCertificate = config.Certificate;
    static (string, string, string) Export(X509Certificate2 certificate)
    {
        var deployment = QuicDeployment.ResolveNative();
        string path = Path.Combine(Path.GetTempPath(), "Meitou.QuicCertificate-" + Guid.NewGuid().ToString("N") + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, ""));
        return (deployment.Directory, deployment.ShimSha256, path);
    }
    public QuicGameServer(IPEndPoint address, string nativeDirectory, string shimSha256, string certificateFile,
        SecureServerPeer handler, int maximumConnections = 32)
    {
        if (maximumConnections is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumConnections));

        this.maximumConnections = maximumConnections; this.handler = handler;
        stop = CancellationTokenSource.CreateLinkedTokenSource(handler.Stopping);
        listener = new(nativeDirectory, shimSha256, true, certificateFile);
        try { listener.Listen(address.Address, checked((ushort)address.Port)); Endpoint = new(address.Address, listener.LocalPort); Completion = Task.Run(Accept); }
        catch { listener.Dispose(); stop.Dispose(); throw; }
    }
    async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                while (listener.TryAccept(out var endpoint))
                {
                    if (peers.Count >= maximumConnections) { Interlocked.Increment(ref rejected); endpoint!.Dispose(); continue; }
                    long id = checked(++nextId);
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    peers.TryAdd(id, completion); _ = Serve(id, endpoint!, completion);
                }
                await Task.Delay(2, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false); listener.Dispose();
            await Task.WhenAll(peers.Values.Select(p => p.Task)).ConfigureAwait(false);
        }
    }
    async Task Serve(long id, NativeQuicEndpoint endpoint, TaskCompletionSource completion)
    {
        try { await handler.RunAsync(endpoint, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { LastFailureType = error.GetType().Name; Interlocked.Increment(ref failures); Console.Error.WriteLine($"QUIC peer failure: {error}"); }
        finally { endpoint.Dispose(); peers.TryRemove(id, out _); completion.TrySetResult(); }
    }
    public ValueTask DisposeAsync() { lock (lifecycle) return new(shutdown ??= StopAsync()); }
    async Task StopAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        try { await Completion.ConfigureAwait(false); } finally { stop.Dispose(); if (temporaryCertificate is { } path) File.Delete(path); }
    }
}
