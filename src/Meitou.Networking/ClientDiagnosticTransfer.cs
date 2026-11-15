using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Meitou.Diagnostics;
using Meitou.Networking.VNext;
using Meitou.Networking.VNext.Transport;

namespace Meitou.Networking;

/// <summary>
/// One bounded background uploader per authenticated connection. Operator requests never block the replica or owner thread.
/// </summary>
public sealed class ClientDiagnosticTransfer : IAsyncDisposable
{
    /// <summary>
    /// Existing game transport and immutable authenticated scope.
    /// </summary>
    readonly NativeGameConnection _connection;
    /// <summary>
    /// Identity assigned by admission, not taken from a collection request.
    /// </summary>
    readonly DiagnosticScope _scope;
    /// <summary>
    /// Host-owned package factory; null result means collection is disabled.
    /// </summary>
    readonly Func<Guid, DiagnosticScope, CancellationToken, Task<string?>> _capture;
    /// <summary>
    /// Bounds incoming operator metadata independently of gameplay operations.
    /// </summary>
    readonly Channel<WireFrame> _commands = Channel.CreateBounded<WireFrame>(4);
    /// <summary>
    /// Cancels preparation and sends when this connection ends.
    /// </summary>
    readonly CancellationTokenSource _stop;
    /// <summary>
    /// Single owner of transfer buffers and lifecycle.
    /// </summary>
    readonly Task _worker;
    /// <summary>
    /// At most one compressed package, bounded to sixteen MiB.
    /// </summary>
    byte[]? _bytes;
    /// <summary>
    /// Exact locally saved artifact, retained if the transfer fails.
    /// </summary>
    string? _path;
    /// <summary>
    /// Active batch ID and sequential send offset.
    /// </summary>
    Guid _report;
    /// <summary>
    /// Bytes already submitted; each server pull admits just the next chunk.
    /// </summary>
    int _offset;
    /// <summary>
    /// Monotonic capture and upload pacing clocks.
    /// </summary>
    long _lastCapture, _lastSend;

    /// <summary>
    /// Installs a client-only host factory by default; embedded/test hosts may supply their own local factory.
    /// </summary>
    public ClientDiagnosticTransfer(NativeGameConnection connection, DiagnosticScope scope, CancellationToken lifetime,
        Func<Guid, DiagnosticScope, CancellationToken, Task<string?>>? capture = null)
    {
        _connection = connection;
        _scope = scope;
        _capture = capture ?? CaptureLocal;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _worker = Task.Run(Run);
    }

    /// <summary>
    /// Hands diagnostic metadata to a bounded worker without waiting on disk, compression or network writes.
    /// </summary>
    public bool Handle(WireFrame frame)
    {
        switch (frame.Kind)
        {
            case WireMessage.DiagnosticCollect:
                _ = DiagnosticTransferProtocol.ReadCollect(frame);
                break;
            case WireMessage.DiagnosticPull:
                _ = DiagnosticTransferProtocol.ReadPull(frame);
                break;
            case WireMessage.DiagnosticFinished:
                _ = DiagnosticTransferProtocol.ReadFinished(frame);
                break;
            default:
                return false;
        }

        _commands.Writer.TryWrite(frame);
        return true;
    }

    /// <summary>
    /// Serializes capture and chunks; idle transfer buffers expire even if the server never sends completion.
    /// </summary>
    async Task Run()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                WireFrame frame;
                try
                {
                    frame = await _commands.Reader.ReadAsync(_stop.Token).AsTask().WaitAsync(TimeSpan.FromMinutes(4), _stop.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Clear();
                    continue;
                }

                await Process(frame).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Diagnostic transfer stopped: " + error.GetType().Name);
        }
        finally
        {
            Clear();
        }
    }

    /// <summary>
    /// Keeps capture failures separate from the game connection's lifetime.
    /// </summary>
    async Task Process(WireFrame frame)
    {
        try
        {
            if (frame.Kind == WireMessage.DiagnosticCollect)
                await Collect(DiagnosticTransferProtocol.ReadCollect(frame)).ConfigureAwait(false);
            else if (frame.Kind == WireMessage.DiagnosticPull)
                await Pull(frame).ConfigureAwait(false);
            else Finish(frame);
        }
        catch (Exception error) when (!_stop.IsCancellationRequested &&
            error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            Guid report = frame.Kind == WireMessage.DiagnosticCollect ? DiagnosticTransferProtocol.ReadCollect(frame) : _report;
            Clear();
            try
            {
                if (report != Guid.Empty) await Refuse(report, DiagnosticTransferStatus.Failed).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or OperationCanceledException or InvalidOperationException)
            {
            }
            Console.Error.WriteLine("Diagnostic capture/upload failed: " + error.GetType().Name);
        }
    }

    /// <summary>
    /// Creates one immutable report, with a thirty-second capture limit even if the server sends repeated commands.
    /// </summary>
    async Task Collect(Guid report)
    {
        if (_report != Guid.Empty)
        {
            await Refuse(report, DiagnosticTransferStatus.Busy).ConfigureAwait(false);
            return;
        }
        long now = Stopwatch.GetTimestamp();
        if (_lastCapture != 0 && Stopwatch.GetElapsedTime(_lastCapture, now).TotalSeconds < 30)
        {
            await Refuse(report, DiagnosticTransferStatus.Busy).ConfigureAwait(false);
            return;
        }

        _lastCapture = now;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        string? path = await _capture(report, _scope, deadline.Token).ConfigureAwait(false);
        if (path is null)
        {
            await Refuse(report, DiagnosticTransferStatus.Disabled).ConfigureAwait(false);
            return;
        }

        await OfferPackage(report, path, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads one bounded immutable ZIP and announces its exact size and digest before any bulk bytes are sent.
    /// </summary>
    async Task OfferPackage(Guid report, string path, CancellationToken token)
    {
        if (new FileInfo(path).Length is <= 0 or > DiagnosticStore.MaximumBytes) throw new IOException("Diagnostic package exceeds upload limit.");
        _bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        if (_bytes.Length <= 0 || _bytes.Length > DiagnosticStore.MaximumBytes) throw new IOException("Diagnostic package changed size.");
        _path = path;
        _report = report;
        _offset = 0;
        _lastSend = 0;
        await SendControl(DiagnosticTransferProtocol.Offer(new(report, DiagnosticTransferStatus.Ready, _bytes.Length, SHA256.HashData(_bytes)))).ConfigureAwait(false);
        Console.WriteLine($"diagnostic server requested report {report:N}; uploading package and runtime log.");
    }

    /// <summary>
    /// Sends a single bounded chunk at no more than 256 KiB/s, on the existing low-priority bulk lane.
    /// </summary>
    async Task Pull(WireFrame frame)
    {
        var pull = DiagnosticTransferProtocol.ReadPull(frame);
        if (pull.Report != _report || _bytes is null) return;
        if (pull.Offset != _offset || _offset >= _bytes.Length) throw new InvalidDataException("Unexpected diagnostic offset.");
        int length = Math.Min(DiagnosticTransferProtocol.ChunkBytes, _bytes.Length - _offset);
        if (_lastSend != 0)
        {
            double remaining = DiagnosticTransferProtocol.ChunkBytes / (256.0 * 1024) - Stopwatch.GetElapsedTime(_lastSend).TotalSeconds;
            if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining), _stop.Token).ConfigureAwait(false);
        }

        byte[] chunk = DiagnosticTransferProtocol.Chunk(_report, _offset, _bytes.AsSpan(_offset, length));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!_connection.TrySend(QuicLane.Bulk, chunk)) await Task.Delay(10, deadline.Token).ConfigureAwait(false);
        _offset += length;
        _lastSend = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Only the server's successful durable acknowledgement marks the local package delivered.
    /// </summary>
    void Finish(WireFrame frame)
    {
        var finished = DiagnosticTransferProtocol.ReadFinished(frame);
        if (finished.Report != _report) return;
        if (finished.Saved && _path is not null && _bytes is not null && _offset == _bytes.Length)
        {
            new DiagnosticStore(Path.GetDirectoryName(_path)!).MarkDelivered(_path);
            Console.WriteLine($"diagnostic report {_report:N} saved by server.");
        }

        Clear();
    }

    /// <summary>
    /// Sends a small typed refusal without creating a file.
    /// </summary>
    Task Refuse(Guid report, DiagnosticTransferStatus status) =>
        SendControl(DiagnosticTransferProtocol.Offer(new(report, status, 0, new byte[32])));

    /// <summary>
    /// Leaves control-lane credit for gameplay and bounds waits on a stalled connection.
    /// </summary>
    async Task SendControl(WireFrame frame)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!_connection.TrySendProtocolControl(frame)) await Task.Delay(10, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases in-memory bytes; failed or unacknowledged files remain in the local outbox.
    /// </summary>
    void Clear()
    {
        _bytes = null;
        _path = null;
        _report = Guid.Empty;
        _offset = 0;
    }

    /// <summary>
    /// Uses the same private recorder and fixed host log path as manual reports; no screenshots or saves are taken remotely.
    /// </summary>
    static async Task<string?> CaptureLocal(Guid report, DiagnosticScope scope, CancellationToken token)
    {
        if (DiagnosticSession.Current is not { Role: "client", Recorder.Enabled: true } session) return null;
        var snapshot = session.Snapshot(report, scope, "server-requested");
        var log = await Task.Run(() => DiagnosticRuntimeLog.Read(session.RuntimeLogPath), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!session.Recorder.Enabled) return null;
        return await session.SaveAsync(snapshot with { RuntimeLogStatus = log.Status }, runtimeLog: log.Content).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the uploader and joins it before the connection is disposed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
