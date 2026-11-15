using System.Security.Cryptography;
using System.Threading.Channels;
using Meitou.Diagnostics;
using Meitou.Networking;

namespace Meitou.Server;

/// <summary>
/// A per-player batch result, including explicit refusal/timeout/failure and relative artifact name.
/// </summary>
public sealed record DiagnosticPeerCapture(DiagnosticScope Scope, string Status, string? File = null, string? RuntimeLog = null);

/// <summary>
/// Operator-only package receiver for one authenticated connection. Game ingress only queues bounded frames here.
/// </summary>
public sealed class ServerDiagnosticPeer : IDisposable
{
    /// <summary>
    /// Serializes transfer admission, ingress and connection teardown.
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// Small metadata sender using the existing authenticated control lane.
    /// </summary>
    readonly Func<WireFrame, CancellationToken, Task> _send;
    /// <summary>
    /// Removes this exact registration without disturbing a replacement connection.
    /// </summary>
    readonly Action<ServerDiagnosticPeer>? _unregister;
    /// <summary>
    /// Overall transfer deadline, capped at the production three-minute budget.
    /// </summary>
    readonly TimeSpan _timeout;
    /// <summary>
    /// Cancels all work when the authenticated connection closes or is replaced.
    /// </summary>
    readonly CancellationTokenSource _closed = new();
    /// <summary>
    /// Only one batch may receive from this connection at a time.
    /// </summary>
    Transfer? _active;
    /// <summary>
    /// Teardown is idempotent across replacement and normal transport shutdown.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// Authenticated identity supplied by admission; client metadata cannot change it.
    /// </summary>
    public DiagnosticScope Scope { get; }

    /// <summary>
    /// Immutable correlation and bounded ingress for one collection attempt.
    /// </summary>
    sealed record Transfer(Guid Report)
    {
        /// <summary>
        /// At most an offer plus one outstanding pull response; excess frames fail only this transfer.
        /// </summary>
        public Channel<Incoming> Inbox { get; } = Channel.CreateBounded<Incoming>(2);
    }

    /// <summary>
    /// Exactly one typed offer or bounded bulk frame.
    /// </summary>
    sealed record Incoming(DiagnosticTransferOffer? Offer, ReadOnlyMemory<byte> Chunk);

    /// <summary>
    /// Binds transport callbacks to an already authenticated connection.
    /// </summary>
    public ServerDiagnosticPeer(DiagnosticScope scope, Func<WireFrame, CancellationToken, Task> send,
        Action<ServerDiagnosticPeer>? unregister = null, TimeSpan? timeout = null)
    {
        Scope = scope;
        _send = send;
        _unregister = unregister;
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(3)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <summary>
    /// Accepts the fixed-size response only for this connection's current server-issued batch ID.
    /// </summary>
    public void ReceiveOffer(WireFrame frame)
    {
        var offer = DiagnosticTransferProtocol.ReadOffer(frame);
        Enqueue(offer.Report, new(offer, default));
    }

    /// <summary>
    /// Validates bounded bulk framing before retaining it for the disk worker.
    /// </summary>
    public void ReceiveChunk(ReadOnlyMemory<byte> payload)
    {
        var chunk = DiagnosticTransferProtocol.ReadChunk(payload);
        Enqueue(chunk.Report, new(null, payload));
    }

    /// <summary>
    /// Collects one package, with an overall timeout independent of world simulation progress.
    /// </summary>
    public async Task<DiagnosticPeerCapture> CollectAsync(Guid report, string directory, CancellationToken cancellationToken = default,
        SemaphoreSlim? uploadSlots = null)
    {
        var transfer = new Transfer(report);
        lock (_gate)
        {
            if (_disposed) return new(Scope, "Disconnected");
            if (_active is not null) return new(Scope, "Busy");
            _active = transfer;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        deadline.CancelAfter(_timeout);
        DiagnosticPeerCapture result;
        try
        {
            await _send(DiagnosticTransferProtocol.Collect(report), deadline.Token).ConfigureAwait(false);
            var first = await transfer.Inbox.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            var offer = first.Offer ?? throw new InvalidDataException("Expected a diagnostic offer.");
            result = offer.Status == DiagnosticTransferStatus.Ready
                ? await SaveWhenAdmitted(transfer, offer, directory, uploadSlots, deadline.Token).ConfigureAwait(false)
                : new(Scope, offer.Status.ToString());
        }
        catch (OperationCanceledException)
        {
            string reason = _closed.IsCancellationRequested ? "Disconnected"
                : cancellationToken.IsCancellationRequested ? "Cancelled"
                : "Timeout";
            result = new(Scope, reason);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            result = new(Scope, "Failed:" + error.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, transfer)) _active = null;
                transfer.Inbox.Writer.TryComplete();
            }
        }

        await Finish(report, result.Status == "Saved").ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// All peers freeze their evidence immediately; only file transmission waits for a process-wide upload slot.
    /// </summary>
    async Task<DiagnosticPeerCapture> SaveWhenAdmitted(Transfer transfer, DiagnosticTransferOffer offer, string directory,
        SemaphoreSlim? slots, CancellationToken token)
    {
        if (slots is not null) await slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Save(transfer, offer, directory, token).ConfigureAwait(false);
        }
        finally
        {
            slots?.Release();
        }
    }

    /// <summary>
    /// Copies ordered chunks to a private temporary file and publishes only after length, digest and identity validation.
    /// </summary>
    async Task<DiagnosticPeerCapture> Save(Transfer transfer, DiagnosticTransferOffer offer, string directory, CancellationToken token)
    {
        string name = $"client-{Scope.Connection}-{Scope.Generation}.zip";
        string destination = Path.Combine(directory, name);
        string temporary = destination + ".part";
        try
        {
            await ReceiveFile(transfer, offer, temporary, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var snapshot = DiagnosticPackage.Read(temporary);
            if (snapshot.Report != transfer.Report || snapshot.Scope != Scope || snapshot.Role != "client")
                throw new InvalidDataException("Diagnostic package belongs to another report or connection.");
            File.Move(temporary, destination);
            return new(Scope, "Saved", name, snapshot.RuntimeLogStatus);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Stop-and-wait flow control admits one chunk at a time; SHA-256 covers the complete file before durable acknowledgement.
    /// </summary>
    async Task ReceiveFile(Transfer transfer, DiagnosticTransferOffer offer, string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int offset = 0; offset < offer.Length;)
        {
            await _send(DiagnosticTransferProtocol.Pull(transfer.Report, offset), token).ConfigureAwait(false);
            var incoming = await transfer.Inbox.Reader.ReadAsync(token).ConfigureAwait(false);
            var chunk = DiagnosticTransferProtocol.ReadChunk(incoming.Chunk);
            int expected = Math.Min(DiagnosticTransferProtocol.ChunkBytes, offer.Length - offset);
            if (chunk.Report != transfer.Report || chunk.Offset != offset || chunk.Bytes.Length != expected)
                throw new InvalidDataException("Diagnostic chunk does not match the outstanding pull.");
            hash.AppendData(chunk.Bytes.Span);
            await file.WriteAsync(chunk.Bytes, token).ConfigureAwait(false);
            offset += chunk.Bytes.Length;
        }

        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), offer.Digest)) throw new InvalidDataException("Diagnostic digest mismatch.");
        await file.FlushAsync(token).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Ingress is bounded; stale replies are ignored after cancellation or completion.
    /// </summary>
    void Enqueue(Guid report, Incoming incoming)
    {
        lock (_gate)
        {
            if (_active is not { } transfer || transfer.Report != report) return;
            if (!transfer.Inbox.Writer.TryWrite(incoming)) transfer.Inbox.Writer.TryComplete(new InvalidDataException("Diagnostic ingress exceeded its bound."));
        }
    }

    /// <summary>
    /// Best-effort completion releases client buffers; a lost acknowledgement keeps the client's local backup.
    /// </summary>
    async Task Finish(Guid report, bool saved)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _send(DiagnosticTransferProtocol.Finished(report, saved), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Removes only this generation from the online registry and cancels pending receipt.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _closed.Cancel();
        }

        _unregister?.Invoke(this);
    }
}
