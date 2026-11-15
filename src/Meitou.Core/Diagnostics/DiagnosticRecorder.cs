using System.Diagnostics;

namespace Meitou.Diagnostics;

/// <summary>
/// Bounded in-memory flight recorder. Disabling keeps existing evidence and rejects all new samples and events.
/// </summary>
public sealed class DiagnosticRecorder : ILatencyTraceSink
{
    /// <summary>
    /// Protects state transitions and ring snapshots; callers never perform disk IO.
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// Recent events, samples and sanitized console lines.
    /// </summary>
    readonly Queue<LatencyTraceEvent> _events = new();
    /// <summary>
    /// Process health history, at most three minutes at one sample per second.
    /// </summary>
    readonly Queue<DiagnosticSample> _samples = new();
    /// <summary>
    /// Bounded text context from existing console logging.
    /// </summary>
    readonly Queue<string> _log = new();
    /// <summary>
    /// Bounded network samples and per-connection sampling clocks.
    /// </summary>
    readonly Queue<DiagnosticNetworkSample> _network = new();
    /// <summary>
    /// At most 256 recent connection clocks, independently bounded from event history.
    /// </summary>
    readonly Dictionary<DiagnosticScope, long> _networkClocks = new();
    /// <summary>
    /// Maximum number of retained trace events.
    /// </summary>
    readonly int _capacity;
    /// <summary>
    /// Collection state; all changes synchronize with emission.
    /// </summary>
    bool _enabled;
    /// <summary>
    /// Switch generation prevents sidecars from joining requests across a disabled interval.
    /// </summary>
    long _epoch;
    /// <summary>
    /// Events removed by capacity or age, reported explicitly in artifacts.
    /// </summary>
    long _evicted;

    /// <summary>
    /// Creates a recorder with fixed event capacity; tracing starts only when enabled.
    /// </summary>
    public DiagnosticRecorder(int capacity = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 8192);
        _capacity = capacity;
    }

    /// <summary>
    /// Changes collection immediately without discarding already captured evidence.
    /// </summary>
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set
        {
            lock (_gate)
            {
                if (_enabled == value) return;
                _enabled = value;
                Interlocked.Increment(ref _epoch);
            }
        }
    }

    /// <summary>
    /// Current switch generation, read by existing latency sidecars.
    /// </summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>
    /// Copies an existing structured trace event into the bounded recent history.
    /// </summary>
    public void Emit(in LatencyTraceEvent value)
    {
        lock (_gate)
        {
            if (!_enabled) return;

            long now = Stopwatch.GetTimestamp();
            while (_events.Count > 0 && (_events.Count >= _capacity || Stopwatch.GetElapsedTime(_events.Peek().Timestamp, now).TotalMinutes > 3))
            {
                _events.Dequeue();
                _evicted++;
            }

            _events.Enqueue(value.Timestamp == 0 ? value with { Timestamp = now } : value);
        }
    }

    /// <summary>
    /// Records a bounded process sample only while collection is enabled.
    /// </summary>
    public void Sample(DiagnosticSample sample)
    {
        lock (_gate)
        {
            if (!_enabled) return;
            if (_samples.Count >= 180) _samples.Dequeue();
            _samples.Enqueue(sample);
        }
    }

    /// <summary>
    /// Adds a sanitized, length-bounded line of existing log output.
    /// </summary>
    public void Log(string line)
    {
        lock (_gate)
        {
            if (!_enabled) return;
            if (_log.Count >= 512) _log.Dequeue();
            _log.Enqueue(DiagnosticRedaction.Clean(line));
        }
    }

    /// <summary>
    /// Samples each authenticated connection at most once a second without introducing a transport polling thread.
    /// </summary>
    public void Network(DiagnosticNetworkSample sample)
    {
        lock (_gate)
        {
            if (!_enabled) return;
            if (_networkClocks.TryGetValue(sample.Scope, out long previous) && Stopwatch.GetElapsedTime(previous, sample.Time).TotalSeconds < 1) return;
            if (_networkClocks.Count >= 256 && !_networkClocks.ContainsKey(sample.Scope)) _networkClocks.Remove(_networkClocks.First().Key);
            _networkClocks[sample.Scope] = sample.Time;
            if (_network.Count >= 2048) _network.Dequeue();
            _network.Enqueue(sample);
        }
    }

    /// <summary>
    /// Captures immutable evidence. Connection-scoped reports exclude all other connections' trace events.
    /// </summary>
    public DiagnosticSnapshot Capture(Guid report, Guid run, string role, string build, DiagnosticScope scope, string serverCapture)
    {
        if (report == Guid.Empty) throw new ArgumentException("A report ID is required.", nameof(report));

        lock (_gate)
        {
            var events = _events.Where(e => scope.Connection == 0 ||
                e.World == scope.World && e.Connection == scope.Connection && e.Generation == scope.Generation).ToArray();
            return new(1, report, Guid.NewGuid(), run, role, build, DateTimeOffset.UtcNow, Stopwatch.Frequency,
                _enabled, scope, serverCapture, _evicted, LatencyTrace.FailedEmissions, events, [.. _samples], [.. _log])
            {
                Network = _network.Where(n => scope.Connection == 0 || n.Scope == scope).ToArray()
            };
        }
    }
}
