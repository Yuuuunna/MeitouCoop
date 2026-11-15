using System.Text.Json.Serialization;

namespace Meitou.Diagnostics;

/// <summary>
/// Opaque authenticated scope shared by client and server. Empty scope denotes a local session.
/// </summary>
public sealed record DiagnosticScope(Guid World = default, ulong Connection = 0, ulong Generation = 0);

/// <summary>
/// One second of process and host health. Host values are last observed; frame values cover this interval.
/// </summary>
public sealed record DiagnosticSample(long Time, long WorkingSet, long ManagedBytes, double CpuMilliseconds,
    int Gen0, int Gen1, int Gen2, int Frames, double FrameMaxMilliseconds, long Tick, double WorkMilliseconds,
    double TickMilliseconds, int QueueLength, double SimulationDebt, double PresentationTick);

/// <summary>
/// Cumulative QUIC UDP payload counters (excluding IP overhead), measured RTT and native queue pressure for one authenticated connection.
/// </summary>
public sealed record DiagnosticNetworkSample(long Time, DiagnosticScope Scope, ulong SentBytes, ulong ReceivedBytes,
    ulong RttMicroseconds, ulong OutstandingBytes, ulong QueuedReceiveBytes, long ReceivedTick, double PresentationTick);

/// <summary>
/// Versioned private incident artifact; process-local timestamps must not be subtracted across hosts.
/// </summary>
public sealed record DiagnosticSnapshot(int Schema, Guid Report, Guid Artifact, Guid Run, string Role, string Build,
    DateTimeOffset CapturedUtc, long Frequency, bool Enabled, DiagnosticScope Scope, string ServerCapture,
    long EvictedEvents, long FailedEmissions, LatencyTraceEvent[] Events, DiagnosticSample[] Samples, string[] Log)
{
    /// <summary>
    /// Player-supplied description, private with the incident package.
    /// </summary>
    public string Description { get; init; } = "";
    /// <summary>
    /// Load-order names only; no original content is copied.
    /// </summary>
    public string[] Mods { get; init; } = [];
    /// <summary>
    /// Runtime platform without machine/user names.
    /// </summary>
    public string Platform { get; init; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    /// <summary>
    /// Connection-scoped network health at most once per second per connection.
    /// </summary>
    public DiagnosticNetworkSample[] Network { get; init; } = [];
    /// <summary>
    /// Whether a host-selected runtime log was attached, sanitized, truncated or unavailable.
    /// </summary>
    public string RuntimeLogStatus { get; init; } = "not-requested";
}

/// <summary>
/// Trimming-safe serialization shared by the launcher, game and server.
/// </summary>
[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(DiagnosticSnapshot))]
public sealed partial class DiagnosticJson : JsonSerializerContext;
