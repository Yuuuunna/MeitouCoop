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
    readonly TcpListener _listener;
    readonly X509Certificate2 _certificate;
    readonly WireFrame _information;
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentDictionary<long, TaskCompletionSource> _peers = new();
    readonly int _maximumConnections;
    long _nextPeer;
    Task? _disposal;
    public TcpDiscoveryServer(IPEndPoint address, X509Certificate2 certificate, ServerInformation information, int maximumConnections = 32)
    {
        if (maximumConnections is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        this._maximumConnections = maximumConnections;
        this._certificate = certificate;
        this._information = ServerDiscovery.Encode(information);
        _listener = new(address);
        try
        {
            _listener.Start();
            Completion = Accept();
        }
        catch
        {
            _listener.Stop();
            _stop.Dispose();
            throw;
        }
    }

    public Task Completion { get; }
    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

    async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (_peers.Count >= _maximumConnections)
                {
                    socket.Dispose();
                    continue;
                }

                long id = checked(++_nextPeer);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _peers.TryAdd(id, completion);
                _ = Serve(socket, id, completion);
            }
        }
        catch (Exception error)when (_stop.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            await Task.WhenAll(_peers.Values.Select(p => p.Task)).ConfigureAwait(false);
        }
    }

    async Task Serve(TcpClient socket, long id, TaskCompletionSource completion)
    {
        using (socket)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await using var tls = await GameTls.AcceptAsync(socket.GetStream(), _certificate, deadline.Token).ConfigureAwait(false);
                var request = await WireFrame.ReadAsync(tls, deadline.Token).ConfigureAwait(false);
                if (request is { Kind: WireMessage.ServerInformationQuery, Payload.IsEmpty: true })
                    await WireFrame.WriteAsync(tls, _information, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error)when (error is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
            }
            finally
            {
                _peers.TryRemove(id, out _);
                completion.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stop)
            return new(_disposal ??= Stop());
    }

    async Task Stop()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await Completion.ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
        }
    }
}