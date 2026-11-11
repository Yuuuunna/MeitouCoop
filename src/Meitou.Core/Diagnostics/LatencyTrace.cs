using System.Diagnostics;

namespace Meitou.Diagnostics;

public enum LatencyStage
{
    Intent, ClientEnqueue, ClientWriteComplete, ReceiptReceived, ServerFrameReceived, ServerDispatch,
    AuthorityQueued, AuthorityExecute, AuthorityReceipt, MovementAccepted, MovementDeferred,
    PathQueued, PathStarted, PathFinished, PathApplied, NavigationWait, AuthorityRootChanged,
    CaptureStarted, CaptureFinished, EncodeStarted, EncodeFinished, PublicationWriteComplete,
    PublicationApplied, ClientRootChanged, RendererRootSubmitted, Terminal, TraceSummary, PublicationContainsMotion, PathBound,
    PublicationSendQueued, PublicationWriteStarted, MotionSendQueued, MotionWriteStarted, MotionWriteComplete, PublicationRetentionRejected, ServerBusinessDequeued, PresentationClockResynchronized,
}

public enum LatencyOutcome
{
    None, Moved, Rejected, Superseded, NoPath, AlreadyAtTarget, Deferred, Censored,
    NavigationWaiting, StalePath, NotStationary, Capacity, Faulted,
}

public enum LatencyEndpoint { Unspecified, Authority, Client }

/// <summary>Opaque correlation only: never command payload, positions, identity names or credentials.
/// Connection disambiguates per-player generation counters in protocol 20. Times are process-local.</summary>
public readonly record struct LatencyTraceEvent(
    LatencyStage Stage, Guid World = default, ulong Connection = 0, ulong Generation = 0,
    ulong Request = 0, ulong Entity = 0, Guid PathService = default, long Path = 0,
    ulong Publication = 0, long Tick = 0, ulong Revision = 0, long Frame = 0,
    double PresentationTick = 0, LatencyOutcome Outcome = LatencyOutcome.None,
    long Value = 0, long Timestamp = 0, LatencyEndpoint Endpoint = LatencyEndpoint.Unspecified);

/// <summary>Implementations must be nonblocking and must not call gameplay code.</summary>
public interface ILatencyTraceSink
{
    void Emit(in LatencyTraceEvent value);
}

public static class LatencyTrace
{
    /// <summary>Optional process sink. Null by default; lifecycle belongs to the host.</summary>
    static ILatencyTraceSink? s_sink;
    public static ILatencyTraceSink? Sink { get => Volatile.Read(ref s_sink); set => Volatile.Write(ref s_sink, value); }

    public static void Emit(ILatencyTraceSink? sink, in LatencyTraceEvent value)
    {
        if (sink is null) return;
        // Diagnostics must not fault authority, transport or presentation, even for a broken custom sink.
        try { sink.Emit(value.Timestamp == 0 ? value with { Timestamp = Stopwatch.GetTimestamp() } : value); }
        catch { Interlocked.Increment(ref s_failed); }
    }

    static long s_failed;
    public static long FailedEmissions => Interlocked.Read(ref s_failed);
}
