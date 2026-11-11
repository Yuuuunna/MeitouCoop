using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Meitou.Networking.VNext;
using Meitou.Networking.VNext.Transport;

namespace Meitou.Server;

/// <summary>
/// Bounded native QUIC game listener. Each accepted endpoint has one independent owner.
/// </summary>
public sealed class QuicGameServer : IAsyncDisposable
{
    readonly NativeQuicEndpoint _listener;
    readonly SecureServerPeer _handler;
    readonly CancellationTokenSource _stop;
    readonly ConcurrentDictionary<long, TaskCompletionSource> _peers = new();
    readonly int _maximumConnections;
    readonly Lock _lifecycle = new();
    Task? _shutdown;
    long _nextId, _rejected, _failures;
    string? _temporaryCertificate;
    public QuicGameServer(IPEndPoint address, QuicGameTransportOptions options, string certificateFile, SecureServerPeer handler, int maximumConnections = 32) : this(address, options.NativeDirectory, options.ShimSha256, certificateFile, handler, maximumConnections)
    {
    }

    public QuicGameServer(IPEndPoint address, X509Certificate2 certificate, SecureServerPeer handler, int maximumConnections = 32) : this(address, Export(certificate), handler, maximumConnections)
    {
    }

    QuicGameServer(IPEndPoint address, (string Directory, string Hash, string Certificate) config, SecureServerPeer handler, int maximumConnections) : this(address, config.Directory, config.Hash, config.Certificate, handler, maximumConnections) => _temporaryCertificate = config.Certificate;
    public QuicGameServer(IPEndPoint address, string nativeDirectory, string shimSha256, string certificateFile, SecureServerPeer handler, int maximumConnections = 32)
    {
        if (maximumConnections is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        this._maximumConnections = maximumConnections;
        this._handler = handler;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(handler.Stopping);
        _listener = new(nativeDirectory, shimSha256, true, certificateFile);
        try
        {
            _listener.Listen(address.Address, checked((ushort)address.Port));
            Endpoint = new(address.Address, _listener.LocalPort);
            Completion = Task.Run(Accept);
        }
        catch
        {
            _listener.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    public Task Completion { get; }
    public IPEndPoint Endpoint { get; }
    public int ActiveConnections => _peers.Count;
    public long RejectedConnections => Interlocked.Read(ref _rejected);
    public long FailedConnections => Interlocked.Read(ref _failures);
    public string? LastFailureType { get; private set; }

    static (string, string, string) Export(X509Certificate2 certificate)
    {
        var deployment = QuicDeployment.ResolveNative();
        string path = Path.Combine(Path.GetTempPath(), "Meitou.QuicCertificate-" + Guid.NewGuid().ToString("N") + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, ""));
        return (deployment.Directory, deployment.ShimSha256, path);
    }

    async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                while (_listener.TryAccept(out var endpoint))
                {
                    if (_peers.Count >= _maximumConnections)
                    {
                        Interlocked.Increment(ref _rejected);
                        endpoint!.Dispose();
                        continue;
                    }

                    long id = checked(++_nextId);
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _peers.TryAdd(id, completion);
                    _ = Serve(id, endpoint!, completion);
                }

                await Task.Delay(2, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Dispose();
            await Task.WhenAll(_peers.Values.Select(p => p.Task)).ConfigureAwait(false);
        }
    }

    async Task Serve(long id, NativeQuicEndpoint endpoint, TaskCompletionSource completion)
    {
        try
        {
            await _handler.RunAsync(endpoint, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            LastFailureType = error.GetType().Name;
            Interlocked.Increment(ref _failures);
            Console.Error.WriteLine($"QUIC peer failure: {error}");
        }
        finally
        {
            endpoint.Dispose();
            _peers.TryRemove(id, out _);
            completion.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
            return new(_shutdown ??= StopAsync());
    }

    async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Completion.ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
            if (_temporaryCertificate is { } path)
                File.Delete(path);
        }
    }
}