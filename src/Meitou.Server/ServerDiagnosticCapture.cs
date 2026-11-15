using System.Diagnostics;
using Meitou.Diagnostics;
using Meitou.Networking;

namespace Meitou.Server;

/// <summary>
/// Per-connection admission: requests cannot select another player's scope or enable server collection.
/// </summary>
public sealed class ServerDiagnosticCapture(DiagnosticSession? diagnostics, DiagnosticScope scope)
{
    /// <summary>
    /// Process-wide bound on concurrent compression/storage.
    /// </summary>
    static readonly SemaphoreSlim CaptureSlots = new(2, 2);
    /// <summary>
    /// Up to thirty-two successful reports per connection, replayed without creating another artifact.
    /// </summary>
    readonly HashSet<Guid> _saved = [];
    /// <summary>
    /// Monotonic thirty-second per-connection capture budget.
    /// </summary>
    long _lastAttempt;

    /// <summary>
    /// Saves matching evidence privately and returns only its acknowledgement.
    /// </summary>
    public async Task<DiagnosticReply> CaptureAsync(Guid report)
    {
        if (report == Guid.Empty) throw new InvalidDataException("Empty diagnostic report ID.");
        if (_saved.Contains(report)) return new(report, scope, DiagnosticCaptureStatus.Saved);
        if (diagnostics is null || !diagnostics.Recorder.Enabled) return new(report, scope, DiagnosticCaptureStatus.Disabled);
        if (_saved.Count >= 32) return new(report, scope, DiagnosticCaptureStatus.Busy);
        long now = Stopwatch.GetTimestamp();
        if (_lastAttempt != 0 && Stopwatch.GetElapsedTime(_lastAttempt, now).TotalSeconds < 30)
            return new(report, scope, DiagnosticCaptureStatus.Busy);
        _lastAttempt = now;
        if (!await CaptureSlots.WaitAsync(0).ConfigureAwait(false)) return new(report, scope, DiagnosticCaptureStatus.Busy);

        try
        {
            await diagnostics.SaveAsync(diagnostics.Snapshot(report, scope, "server-local")).ConfigureAwait(false);
            _saved.Add(report);
            return new(report, scope, DiagnosticCaptureStatus.Saved);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(report, scope, DiagnosticCaptureStatus.Failed);
        }
        finally
        {
            CaptureSlots.Release();
        }
    }
}
