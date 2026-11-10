using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Meitou.Networking;

namespace Meitou.Server;

/// <summary>Public bootstrap only. This listener cannot authenticate accounts, execute commands,
/// or publish state. TCP and QUIC may share a port number without sharing a transport connection.</summary>
public sealed class TcpDiscoveryServer : IAsyncDisposable
{
    readonly TcpListener listener;
    readonly X509Certificate2 certificate;
    readonly WireFrame information;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<long, TaskCompletionSource> peers = new();
    readonly int maximumConnections;
    long nextPeer;
    Task? disposal;
    public Task Completion { get; }
    public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;

    public TcpDiscoveryServer(IPEndPoint address, X509Certificate2 certificate, ServerInformation information, int maximumConnections = 32)
    {
        if (maximumConnections is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        this.maximumConnections = maximumConnections;
        this.certificate = certificate;
        this.information = ServerDiscovery.Encode(information);
        listener = new(address);
        try { listener.Start(); Completion = Accept(); }
        catch { listener.Stop(); stop.Dispose(); throw; }
    }

    async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var socket = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                if (peers.Count >= maximumConnections) { socket.Dispose(); continue; }
                long id = checked(++nextPeer);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                peers.TryAdd(id, completion);
                _ = Serve(socket, id, completion);
            }
        }
        catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            await Task.WhenAll(peers.Values.Select(p => p.Task)).ConfigureAwait(false);
        }
    }

    async Task Serve(TcpClient socket, long id, TaskCompletionSource completion)
    {
        using (socket)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await using var tls = await GameTls.AcceptAsync(socket.GetStream(), certificate, deadline.Token).ConfigureAwait(false);
                var request = await WireFrame.ReadAsync(tls, deadline.Token).ConfigureAwait(false);
                if (request is { Kind: WireMessage.ServerInformationQuery, Payload.IsEmpty: true })
                    await WireFrame.WriteAsync(tls, information, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
            finally { peers.TryRemove(id, out _); completion.TrySetResult(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (stop) return new(disposal ??= Stop());
    }
    async Task Stop()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        try { await Completion.ConfigureAwait(false); } finally { stop.Dispose(); }
    }
}
