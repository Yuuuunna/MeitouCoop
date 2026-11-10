namespace Meitou.Networking.VNext;

public enum MotionRelevance { Controlled, Interactive, Near, Far }
public enum MotionCaptureOutcome { Accepted, Unchanged, ReduceSubscription, CheckpointBackpressure }
public enum MotionPumpOutcome { Idle, Queued, ReducedCadence }
public sealed record MotionCheckpoint(ulong Id, ulong SessionEpoch, ulong ViewEpoch, ulong CaptureTick, MotionRecord Record, ulong AuthorityPublication = 0);

/// <summary>Single-owner, already-authorized absolute motion capture. No transport or authority decisions.</summary>
public sealed class MotionReplicationProducer
{
    public const int MaximumKeys = 4096;
    public const int MaximumRetainedBytes = 256 * 1024;
    const int Capacity = (MotionDatagramCodec.MaximumDatagramBytes - MotionDatagramCodec.HeaderBytes - 2) / (MotionDatagramCodec.RecordBytes + 2);
    readonly BandwidthScheduler scheduler;
    readonly ulong peer, schedulerKey;
    readonly Func<TimeSpan> now;
    readonly Dictionary<ulong, Entry> entries = new();
    readonly Dictionary<ulong, uint> retired = new();
    readonly Dictionary<ulong, MotionCheckpoint> checkpoints = new();
    ulong session, view, sequence = 1, nextCheckpoint = 1, order, latestCaptureTick;
    sealed class Entry(MotionRecord record, MotionRelevance relevance, ulong order, TimeSpan capturedAt)
    {
        public MotionRecord Record = record;
        public readonly TimeSpan CapturedAt = capturedAt;
        public MotionRelevance Relevance = relevance;
        public TimeSpan LastSent = TimeSpan.MinValue;
        public ulong Order = order;
        public MotionRecord? LastSentRecord;
    }

    public MotionReplicationProducer(BandwidthScheduler scheduler, ulong peer, ulong schedulerKey,
        ulong sessionEpoch, ulong viewEpoch, Func<TimeSpan> monotonicNow)
    {
        this.scheduler = scheduler;
        this.peer = peer;
        this.schedulerKey = schedulerKey;
        now = monotonicNow;
        BeginEpoch(sessionEpoch, viewEpoch);
    }

    public int RetainedKeys => entries.Count + retired.Count;
    /// <summary>Diagnostic notification of the exact immutable batch queued; coalescing may discard it later.</summary>
    public Action<MotionDatagram>? PacketQueued { get; set; }
    public int RetainedBytes => (entries.Count + checkpoints.Count) * MotionDatagramCodec.RecordBytes + retired.Count * 12;
    // Caller sends these on the reliable lifecycle lane, with epochs, and ACKs only after peer receipt.
    public IReadOnlyList<MotionCheckpoint> PendingCheckpoints => checkpoints.Values.OrderBy(c => c.Id).ToArray();
    public bool AcknowledgeCheckpoint(ulong sessionEpoch, ulong viewEpoch, ulong id) =>
        sessionEpoch == session && viewEpoch == view && checkpoints.Remove(id);

    /// <summary>Release only records submitted to the ordered lifecycle lane, never merely captured
    /// records or a newer epoch. A late cumulative ACK cannot clear higher checkpoint IDs.</summary>
    public int AcknowledgeCheckpointsThrough(ulong sessionEpoch, ulong viewEpoch, ulong highWater, IReadOnlySet<ulong> submitted)
    {
        if (sessionEpoch != session || viewEpoch != view) return 0;
        var released = checkpoints.Keys.Where(id => id <= highWater && submitted.Contains(id)).ToArray();
        foreach (ulong id in released) checkpoints.Remove(id);
        return released.Length;
    }
    public void BeginEpoch(ulong sessionEpoch, ulong viewEpoch)
    {
        if (sessionEpoch == 0 || viewEpoch == 0 ||
            (session != 0 && (sessionEpoch != session || viewEpoch <= view)))
            throw new ArgumentOutOfRangeException(nameof(viewEpoch));
        if (session != 0) scheduler.CancelState(peer, schedulerKey);
        session = sessionEpoch; view = viewEpoch; sequence = 1; latestCaptureTick = 0;
        entries.Clear(); checkpoints.Clear(); retired.Clear();
    }

    public void Withdraw(ulong entityId, uint generation)
    {
        scheduler.CancelState(peer, schedulerKey);
        if (entries.TryGetValue(entityId, out var entry) && entry.Record.InterestGeneration == generation)
            { entries.Remove(entityId); retired[entityId] = generation; }
        foreach (var id in checkpoints.Where(p => p.Value.Record.EntityId == entityId &&
                     p.Value.Record.InterestGeneration == generation).Select(p => p.Key).ToArray()) checkpoints.Remove(id);
    }

    public MotionCaptureOutcome Capture(ulong sessionEpoch, ulong viewEpoch, ulong captureTick, MotionRecord record, MotionRelevance relevance, ulong authorityPublication = 0)
    {
        if (sessionEpoch != session || viewEpoch != view) throw new InvalidOperationException("Stale capture epoch.");
        if (captureTick < latestCaptureTick) throw new InvalidOperationException("Capture tick regressed.");
        MotionDatagramCodec.ValidateRecord(record);
        if (!Enum.IsDefined(relevance)) throw new ArgumentOutOfRangeException(nameof(relevance));
        entries.TryGetValue(record.EntityId, out var previous);
        if (retired.TryGetValue(record.EntityId, out uint retiredGeneration) && record.InterestGeneration <= retiredGeneration)
            throw new InvalidOperationException("Withdrawn generation cannot re-enter.");
        if (previous is not null)
        {
            if (record.InterestGeneration != previous.Record.InterestGeneration)
                throw new InvalidOperationException("Withdraw the old interest generation before replacing it.");
            if (record.Revision < previous.Record.Revision) throw new InvalidDataException("Motion revision regressed.");
            if (record.Revision == previous.Record.Revision)
            {
                MotionDatagramCodec.ValidateDuplicate(previous.Record, record);
                scheduler.CancelState(peer, schedulerKey);
                latestCaptureTick = captureTick;
                previous.Relevance = relevance;
                return MotionCaptureOutcome.Unchanged;
            }
        }
        bool checkpoint = previous is null || ((previous.Record.Flags ^ record.Flags) &
            (MotionFlags.Moving | MotionFlags.Carried | MotionFlags.Ragdoll)) != 0 ||
            (record.Flags & MotionFlags.Discontinuity) != 0;
        int additional = (previous is null ? 1 : 0) + (checkpoint ? 1 : 0);
        if (previous is null && !retired.ContainsKey(record.EntityId) && RetainedKeys >= MaximumKeys) return MotionCaptureOutcome.ReduceSubscription;
        if (checkpoints.Count >= MaximumKeys || RetainedBytes + additional * MotionDatagramCodec.RecordBytes > MaximumRetainedBytes)
            return MotionCaptureOutcome.CheckpointBackpressure;
        latestCaptureTick = captureTick;
        scheduler.CancelState(peer, schedulerKey);
        retired.Remove(record.EntityId);
        if (checkpoint)
        {
            if (nextCheckpoint >= long.MaxValue) throw new InvalidOperationException("Checkpoint identity exhausted.");
            checkpoints.Add(nextCheckpoint, new(nextCheckpoint++, session, view, captureTick, record, authorityPublication));
        }
        if (previous is null) entries.Add(record.EntityId, new(record, relevance, order++, now()));
        else { previous.Record = record; scheduler.CancelState(peer, schedulerKey);
                latestCaptureTick = captureTick;
                previous.Relevance = relevance; }
        return MotionCaptureOutcome.Accepted;
    }

    /// <summary>Tick identifies the coherent capture boundary of all preceding Capture calls. Capture cancels unsent batches.</summary>
    public MotionPumpOutcome Pump(ulong serverTick, ulong authorityPublication = 0)
    {
        if (serverTick != latestCaptureTick) throw new InvalidOperationException("Packet tick must match the latest capture boundary.");
        var time = now();
        var due = entries.Values.Where(e => Due(e, time)).OrderBy(e => e.LastSent).ThenBy(e => e.Order).ToArray();
        if (due.Length == 0) return MotionPumpOutcome.Idle;
        var selected = due.Take(Capacity).ToArray();
        ulong capturedSession = session, capturedView = view;
        var records = selected.Select(e => e.Record).ToArray();
        if (sequence >= long.MaxValue) throw new InvalidOperationException("Motion sequence exhausted; start a new session.");
        var packet = new MotionDatagram(new(session, view, sequence++, serverTick, authorityPublication), records);
        var outcome = scheduler.Enqueue(peer, ScheduledLane.State, schedulerKey, MotionDatagramCodec.HeaderBytes + 2 + selected.Length * (MotionDatagramCodec.RecordBytes + 2), () =>
        {
            if (capturedSession != session || capturedView != view) throw new InvalidOperationException("Canceled epoch closure invoked.");
            var live = selected.Where(e => entries.TryGetValue(e.Record.EntityId, out var current) && ReferenceEquals(current, e)).ToArray();
            if (live.Length != selected.Length || live.Where((e, i) => e.Record != records[i]).Any())
                throw new InvalidOperationException("Canceled motion capture closure invoked.");
            byte[] bytes = MotionDatagramCodec.Encode(packet);
            foreach (var entry in live) { entry.LastSent = now(); entry.LastSentRecord = entry.Record; entry.Order = order++; }
            return bytes;
        });
        if (outcome is ScheduleOutcome.Queued or ScheduleOutcome.Coalesced)
            try { PacketQueued?.Invoke(packet); } catch { /* Diagnostics never change scheduling. */ }
        bool overdue = due.Any(e => time - (e.LastSent == TimeSpan.MinValue ? e.CapturedAt : e.LastSent) > Cadence(e) + TimeSpan.FromMilliseconds(100));
        return outcome == ScheduleOutcome.ReduceSubscription || overdue ? MotionPumpOutcome.ReducedCadence : MotionPumpOutcome.Queued;
    }

    static bool Due(Entry entry, TimeSpan time) => entry.LastSent == TimeSpan.MinValue || time - entry.LastSent >= Cadence(entry);
    static TimeSpan Cadence(Entry entry) => TimeSpan.FromMilliseconds(
        entry.LastSentRecord is { } sent && MotionDatagramCodec.SamePayload(sent with { Revision = entry.Record.Revision }, entry.Record) ? 1000 :
        entry.Relevance switch { MotionRelevance.Controlled or MotionRelevance.Interactive => 50, MotionRelevance.Near => 200, _ => 500 });
}
