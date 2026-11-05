using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Meitou.Server;

/// <summary>Bounded TLS listener using a shared world loop. Caller owns the certificate and runtime and shuts this listener down before disposing either.</summary>
public sealed class TcpGameServer : IAsyncDisposable
{
    sealed class Peer(TcpClient client)
    {
        public TcpClient Client { get; } = client;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    readonly TcpListener listener;
    readonly CancellationTokenSource stop;
    readonly SecureServerPeer handler;
    readonly X509Certificate2 certificate;
    readonly int maximumConnections;
    readonly Lock lifecycle = new();
    Task? shutdown;
    readonly ConcurrentDictionary<long, Peer> peers = new();
    long nextId, rejected, failures;
    public Task Completion { get; }
    public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;
    public int ActiveConnections => peers.Count;
    public long RejectedConnections => Interlocked.Read(ref rejected);
    public long FailedConnections => Interlocked.Read(ref failures);
    public string? LastFailureType { get; private set; }
    public TcpGameServer(IPEndPoint endpoint, X509Certificate2 certificate, SecureServerPeer handler, int maximumConnections = 32)
    {
        if (maximumConnections is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        this.maximumConnections = maximumConnections; this.certificate = certificate; this.handler = handler;
        stop = CancellationTokenSource.CreateLinkedTokenSource(handler.Stopping);
        listener = new TcpListener(endpoint);
        try { listener.Start(Math.Max(8, maximumConnections)); Completion = Task.Run(Accept); }
        catch { stop.Dispose(); listener.Stop(); throw; }
    }
    async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                if (peers.Count >= maximumConnections) { Interlocked.Increment(ref rejected); client.Dispose(); continue; }
                client.NoDelay = true;
                long id = checked(++nextId); var peer = new Peer(client); peers.TryAdd(id, peer);
                _ = Serve(id, peer);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        finally
        {
            stop.Cancel(); listener.Stop();
            await Task.WhenAll(peers.Values.Select(p => p.Completion.Task)).ConfigureAwait(false);
        }
    }
    async Task Serve(long id, Peer peer)
    {
        try { await handler.RunAsync(peer.Client, certificate, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e)
        {
            LastFailureType = e.GetType().Name; Interlocked.Increment(ref failures);
            Console.Error.WriteLine($"network   {DateTime.UtcNow:O} peer failed: {e}");
        }
        finally
        {
            peer.Client.Dispose(); peers.TryRemove(id, out _); peer.Completion.TrySetResult();
        }
    }
    public ValueTask DisposeAsync() { lock (lifecycle) return new(shutdown ??= StopAsync()); }
    async Task StopAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false); listener.Stop();
        try { await Completion.ConfigureAwait(false); } finally { stop.Dispose(); }
    }
}
