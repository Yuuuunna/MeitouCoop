namespace Meitou.Networking.VNext;
public enum MotionRelevance
{
    Controlled,
    Interactive,
    Near,
    Far
}

public enum MotionCaptureOutcome
{
    Accepted,
    Unchanged,
    ReduceSubscription,
    CheckpointBackpressure
}

public enum MotionPumpOutcome
{
    Idle,
    Queued,
    ReducedCadence
}

public sealed record MotionCheckpoint(ulong Id, ulong SessionEpoch, ulong ViewEpoch, ulong CaptureTick, MotionRecord Record, ulong AuthorityPublication = 0);
/// <summary>
/// Single-owner, already-authorized absolute motion capture. No transport or authority decisions.
/// </summary>
public sealed class MotionReplicationProducer
{
    public const int MaximumKeys = 4096;
    public const int MaximumRetainedBytes = 256 * 1024;
    const int Capacity = (MotionDatagramCodec.MaximumDatagramBytes - MotionDatagramCodec.HeaderBytes - 2) / (MotionDatagramCodec.RecordBytes + 2);
    readonly BandwidthScheduler _scheduler;
    readonly ulong _peer, _schedulerKey;
    readonly Func<TimeSpan> _now;
    readonly Dictionary<ulong, Entry> _entries = new();
    readonly Dictionary<ulong, uint> _retired = new();
    readonly Dictionary<ulong, MotionCheckpoint> _checkpoints = new();
    ulong _session, _view, _sequence = 1, _nextCheckpoint = 1, _order, _latestCaptureTick;
    bool _baselineSeeded;
    public MotionReplicationProducer(BandwidthScheduler scheduler, ulong peer, ulong schedulerKey, ulong sessionEpoch, ulong viewEpoch, Func<TimeSpan> monotonicNow)
    {
        this._scheduler = scheduler;
        this._peer = peer;
        this._schedulerKey = schedulerKey;
        _now = monotonicNow;
        BeginEpoch(sessionEpoch, viewEpoch);
    }

    sealed class Entry(MotionRecord record, MotionRelevance relevance, ulong order, TimeSpan capturedAt)
    {
        public MotionRecord Record = record;
        public readonly TimeSpan CapturedAt = capturedAt;
        public MotionRelevance Relevance = relevance;
        public TimeSpan LastSent = TimeSpan.MinValue;
        public ulong Order = order;
        public MotionRecord? LastSentRecord;
    }

    public int RetainedKeys => _entries.Count + _retired.Count;
    /// <summary>
    /// Diagnostic notification of the exact immutable batch queued; coalescing may discard it later.
    /// </summary>
    public Action<MotionDatagram>? PacketQueued { get; set; }
    public int RetainedBytes => (_entries.Count + _checkpoints.Count) * MotionDatagramCodec.RecordBytes + _retired.Count * 12;
    // Caller sends these on the reliable lifecycle lane, with epochs, and ACKs only after peer receipt.
    public IReadOnlyList<MotionCheckpoint> PendingCheckpoints => _checkpoints.Values.OrderBy(c => c.Id).ToArray();

    public bool AcknowledgeCheckpoint(ulong sessionEpoch, ulong viewEpoch, ulong id) => sessionEpoch == _session && viewEpoch == _view && _checkpoints.Remove(id);
    /// <summary>Release only records submitted to the ordered lifecycle lane, never merely captured
    /// records or a newer epoch. A late cumulative ACK cannot clear higher checkpoint IDs.</summary>
    public int AcknowledgeCheckpointsThrough(ulong sessionEpoch, ulong viewEpoch, ulong highWater, IReadOnlySet<ulong> submitted)
    {
        if (sessionEpoch != _session || viewEpoch != _view)
            return 0;
        var released = _checkpoints.Keys.Where(id => id <= highWater && submitted.Contains(id)).ToArray();
        foreach (ulong id in released)
            _checkpoints.Remove(id);
        return released.Length;
    }

    public void BeginEpoch(ulong sessionEpoch, ulong viewEpoch)
    {
        if (sessionEpoch == 0 || viewEpoch == 0 || (_session != 0 && (sessionEpoch != _session || viewEpoch <= _view)))
            throw new ArgumentOutOfRangeException(nameof(viewEpoch));
        if (_session != 0)
            _scheduler.CancelState(_peer, _schedulerKey);
        _session = sessionEpoch;
        _view = viewEpoch;
        _sequence = 1;
        _latestCaptureTick = 0;
        _baselineSeeded = false;
        _entries.Clear();
        _checkpoints.Clear();
        _retired.Clear();
    }

    /// <summary>
    /// Initializes a fresh producer from the exact roots and interest generations already encoded in its reliable baseline.
    /// The caller waits for that baseline's readiness acknowledgement before using this producer. No checkpoint ACK is fabricated.
    /// </summary>
    public void SeedReliableBaseline(ulong sessionEpoch, ulong viewEpoch, ulong captureTick,
        IReadOnlyList<(MotionRecord Record, MotionRelevance Relevance)> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (sessionEpoch != _session || viewEpoch != _view || _baselineSeeded || _entries.Count != 0
            || _retired.Count != 0 || _checkpoints.Count != 0 || _sequence != 1)
            throw new InvalidOperationException("Reliable baseline requires a fresh producer in the matching epoch.");
        if (roots.Count > MaximumKeys || (long)roots.Count * MotionDatagramCodec.RecordBytes > MaximumRetainedBytes)
            throw new InvalidDataException("Reliable baseline motion exceeds retention bounds.");
        var identities = new HashSet<ulong>();
        foreach (var (record, relevance) in roots)
        {
            MotionDatagramCodec.ValidateRecord(record);
            if (!Enum.IsDefined(relevance) || !identities.Add(record.EntityId))
                throw new InvalidDataException("Invalid reliable baseline motion identity or relevance.");
        }

        var capturedAt = _now();
        foreach (var (record, relevance) in roots)
            _entries.Add(record.EntityId, new(record, relevance, _order++, capturedAt)
            { LastSent = capturedAt, LastSentRecord = record });
        _latestCaptureTick = captureTick;
        _baselineSeeded = true;
    }

    public void Withdraw(ulong entityId, uint generation)
    {
        _scheduler.CancelState(_peer, _schedulerKey);
        if (_entries.TryGetValue(entityId, out var entry) && entry.Record.InterestGeneration == generation)
        {
            _entries.Remove(entityId);
            _retired[entityId] = generation;
        }

        foreach (var id in _checkpoints.Where(p => p.Value.Record.EntityId == entityId && p.Value.Record.InterestGeneration == generation).Select(p => p.Key).ToArray())
            _checkpoints.Remove(id);
    }

    public MotionCaptureOutcome Capture(ulong sessionEpoch, ulong viewEpoch, ulong captureTick, MotionRecord record, MotionRelevance relevance, ulong authorityPublication = 0)
    {
        if (sessionEpoch != _session || viewEpoch != _view)
            throw new InvalidOperationException("Stale capture epoch.");
        if (captureTick < _latestCaptureTick)
            throw new InvalidOperationException("Capture tick regressed.");
        MotionDatagramCodec.ValidateRecord(record);
        if (!Enum.IsDefined(relevance))
            throw new ArgumentOutOfRangeException(nameof(relevance));
        _entries.TryGetValue(record.EntityId, out var previous);
        if (_retired.TryGetValue(record.EntityId, out uint retiredGeneration) && record.InterestGeneration <= retiredGeneration)
            throw new InvalidOperationException("Withdrawn generation cannot re-enter.");
        if (previous is not null)
        {
            if (record.InterestGeneration != previous.Record.InterestGeneration)
                throw new InvalidOperationException("Withdraw the old interest generation before replacing it.");
            if (record.Revision < previous.Record.Revision)
                throw new InvalidDataException("Motion revision regressed.");
            if (record.Revision == previous.Record.Revision)
            {
                MotionDatagramCodec.ValidateDuplicate(previous.Record, record);
                _scheduler.CancelState(_peer, _schedulerKey);
                _latestCaptureTick = captureTick;
                previous.Relevance = relevance;
                return MotionCaptureOutcome.Unchanged;
            }
        }

        bool checkpoint = previous is null || ((previous.Record.Flags ^ record.Flags) & (MotionFlags.Moving | MotionFlags.Carried | MotionFlags.Ragdoll)) != 0 || (record.Flags & MotionFlags.Discontinuity) != 0;
        int additional = (previous is null ? 1 : 0) + (checkpoint ? 1 : 0);
        if (previous is null && !_retired.ContainsKey(record.EntityId) && RetainedKeys >= MaximumKeys)
            return MotionCaptureOutcome.ReduceSubscription;
        if (_checkpoints.Count >= MaximumKeys || RetainedBytes + additional * MotionDatagramCodec.RecordBytes > MaximumRetainedBytes)
            return MotionCaptureOutcome.CheckpointBackpressure;
        _latestCaptureTick = captureTick;
        _scheduler.CancelState(_peer, _schedulerKey);
        _retired.Remove(record.EntityId);
        if (checkpoint)
        {
            if (_nextCheckpoint >= long.MaxValue)
                throw new InvalidOperationException("Checkpoint identity exhausted.");
            _checkpoints.Add(_nextCheckpoint, new(_nextCheckpoint++, _session, _view, captureTick, record, authorityPublication));
        }

        if (previous is null)
            _entries.Add(record.EntityId, new(record, relevance, _order++, _now()));
        else
        {
            previous.Record = record;
            _scheduler.CancelState(_peer, _schedulerKey);
            _latestCaptureTick = captureTick;
            previous.Relevance = relevance;
        }

        return MotionCaptureOutcome.Accepted;
    }

    /// <summary>
    /// Tick identifies the coherent capture boundary of all preceding Capture calls. Capture cancels unsent batches.
    /// </summary>
    public MotionPumpOutcome Pump(ulong serverTick, ulong authorityPublication = 0)
    {
        if (serverTick != _latestCaptureTick)
            throw new InvalidOperationException("Packet tick must match the latest capture boundary.");
        var time = _now();
        var due = _entries.Values.Where(e => Due(e, time)).OrderBy(e => e.LastSent).ThenBy(e => e.Order).ToArray();
        if (due.Length == 0)
            return MotionPumpOutcome.Idle;
        var selected = due.Take(Capacity).ToArray();
        ulong capturedSession = _session, capturedView = _view;
        var records = selected.Select(e => e.Record).ToArray();
        if (_sequence >= long.MaxValue)
            throw new InvalidOperationException("Motion sequence exhausted; start a new session.");
        var packet = new MotionDatagram(new(_session, _view, _sequence++, serverTick, authorityPublication), records);
        var outcome = _scheduler.Enqueue(_peer, ScheduledLane.State, _schedulerKey, MotionDatagramCodec.HeaderBytes + 2 + selected.Length * (MotionDatagramCodec.RecordBytes + 2), () =>
        {
            if (capturedSession != _session || capturedView != _view)
                throw new InvalidOperationException("Canceled epoch closure invoked.");
            var live = selected.Where(e => _entries.TryGetValue(e.Record.EntityId, out var current) && ReferenceEquals(current, e)).ToArray();
            if (live.Length != selected.Length || live.Where((e, i) => e.Record != records[i]).Any())
                throw new InvalidOperationException("Canceled motion capture closure invoked.");
            byte[] bytes = MotionDatagramCodec.Encode(packet);
            foreach (var entry in live)
            {
                entry.LastSent = _now();
                entry.LastSentRecord = entry.Record;
                entry.Order = _order++;
            }

            return bytes;
        });
        if (outcome is ScheduleOutcome.Queued or ScheduleOutcome.Coalesced)
            try
            {
                PacketQueued?.Invoke(packet);
            }
            catch
            { /* Diagnostics never change scheduling. */
            }

        bool overdue = due.Any(e => time - (e.LastSent == TimeSpan.MinValue ? e.CapturedAt : e.LastSent) > Cadence(e) + TimeSpan.FromMilliseconds(100));
        return outcome == ScheduleOutcome.ReduceSubscription || overdue ? MotionPumpOutcome.ReducedCadence : MotionPumpOutcome.Queued;
    }

    static bool Due(Entry entry, TimeSpan time) => entry.LastSent == TimeSpan.MinValue || time - entry.LastSent >= Cadence(entry);
    static TimeSpan Cadence(Entry entry) => TimeSpan.FromMilliseconds(entry.LastSentRecord is { } sent && MotionDatagramCodec.SamePayload(sent with { Revision = entry.Record.Revision }, entry.Record) ? 1000 : entry.Relevance switch
    {
        MotionRelevance.Controlled or MotionRelevance.Interactive => 50,
        MotionRelevance.Near => 200,
        _ => 500
    });
}