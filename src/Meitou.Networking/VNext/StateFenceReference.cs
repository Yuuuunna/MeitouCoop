using System.Security.Cryptography;

namespace Meitou.Networking.VNext;

/// <summary>Implemented by the existing authenticated authority boundary, never by packet fields.</summary>
public interface IStateViewAuthorization
{
    bool IsViewAuthorized(ulong sessionEpoch, ulong viewEpoch);
    bool IsEntityAuthorized(ulong sessionEpoch, ulong viewEpoch, ulong entityId, uint interestGeneration);
}

public readonly record struct ViewFence(ulong SessionEpoch, ulong ViewEpoch);
public readonly record struct BaselineFence(ulong Id, ulong CaptureTick, ulong LifecycleSequence);
public readonly record struct ReadyFence(ulong BaselineId, ulong LifecycleWatermark);
public enum ReferenceViewPhase { Withdrawn, Staging, Ready, Active, Abandoned }
public enum FenceResult { Ignored, Applied, Duplicate, Ready, Abandoned }
public enum MotionLifecycleKind { Spawn, Despawn }
public readonly record struct MotionLifecycleChange(ulong Sequence, MotionLifecycleKind Kind,
    ulong EntityId, uint InterestGeneration, MotionRecord? InitialMotion = null);

/// <summary>
/// Serial, bounded reference model for motion fencing, not a production replica or a control/bulk codec.
/// All typed lifecycle, baseline, commit and Active inputs must come from validated authenticated ingress.
/// Its fixed session is externally authenticated; reconnect requires a new instance. It cannot issue grants.
/// The entry/history caps are reference-model storage budgets, not additional wire protocol limits.
/// </summary>
public sealed class StateFenceReference
{
    readonly ulong session;
    readonly IStateViewAuthorization authorization;
    readonly int maximumEntries;
    readonly int maximumHistory;
    Dictionary<ulong, MotionRecord> motion = [];
    Dictionary<ulong, uint> generations = [];
    byte[]? baselineDigest;
    readonly Dictionary<ulong, uint> revokedThrough = [];
    readonly SortedDictionary<ulong, MotionLifecycleChange> history = [];
    ulong view;
    BaselineFence baselineFence;
    ulong lastLifecycle;
    ReadyFence? commit;
    ReferenceViewPhase phase;

    public StateFenceReference(ulong authenticatedSessionEpoch, IStateViewAuthorization authorization,
        int maximumEntries = 4096, int maximumLifecycleHistory = 4096)
    {
        if (authenticatedSessionEpoch == 0) throw new ArgumentOutOfRangeException(nameof(authenticatedSessionEpoch));
        ArgumentNullException.ThrowIfNull(authorization);
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumLifecycleHistory <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLifecycleHistory));
        session = authenticatedSessionEpoch;
        this.authorization = authorization;
        this.maximumEntries = maximumEntries;
        maximumHistory = maximumLifecycleHistory;
    }

    public ReferenceViewPhase Phase { get { CheckAuthorization(); return phase; } }

    /// <summary>Only an explicit externally authorized ViewBegin can replace the view and clear old state.</summary>
    public FenceResult BeginAuthorizedView(ulong authorizedViewEpoch, BaselineFence capture)
    {
        if (authorizedViewEpoch == 0 || capture.Id == 0) throw new InvalidDataException("Invalid view or baseline ID.");
        CheckAuthorization();
        if (authorizedViewEpoch <= view || !authorization.IsViewAuthorized(session, authorizedViewEpoch)) return FenceResult.Ignored;
        Clear();
        view = authorizedViewEpoch;
        baselineFence = capture;
        lastLifecycle = capture.LifecycleSequence;
        phase = ReferenceViewPhase.Staging;
        return FenceResult.Applied;
    }

    public FenceResult WithdrawView(ViewFence fence)
    {
        if (fence != new ViewFence(session, view)) return FenceResult.Ignored;
        Clear();
        phase = ReferenceViewPhase.Withdrawn;
        return FenceResult.Applied;
    }

    /// <summary>Validated bulk completion remains staged; it cannot expose entities or grant Active.</summary>
    public FenceResult StageBaseline(ViewFence fence, BaselineFence capture, IReadOnlyList<MotionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (!Matches(fence) || capture != baselineFence) return FenceResult.Ignored;
        if (records.Count > maximumEntries) throw new InvalidDataException("Reference baseline storage budget exceeded.");
        MotionDatagramCodec.ValidateRecords(records);
        var candidateBaseline = new Dictionary<ulong, MotionRecord>();
        var seen = new HashSet<ulong>();
        foreach (var record in records)
        {
            if (!seen.Add(record.EntityId)) throw new InvalidDataException("Baseline contains a repeated entity.");
            if (Authorized(record)) candidateBaseline.Add(record.EntityId, record);
        }
        byte[] digest = BaselineDigest(records);
        if (baselineDigest is not null)
        {
            if (!baselineDigest.AsSpan().SequenceEqual(digest))
                throw new InvalidDataException("A baseline ID cannot name different baseline contents.");
            return FenceResult.Duplicate; // Completion never reconstructs or rolls back an already completed baseline.
        }
        var candidate = new Dictionary<ulong, MotionRecord>(candidateBaseline);
        var candidateGenerations = candidate.ToDictionary(p => p.Key, p => p.Value.InterestGeneration);
        foreach (var change in history.Values) ApplyLifecycleTo(candidate, candidateGenerations, change);
        foreach (var record in motion.Values) Merge(candidate, record);
        foreach (var pair in generations)
            if (candidateGenerations.GetValueOrDefault(pair.Key) < pair.Value) candidateGenerations[pair.Key] = pair.Value;
        if (candidateGenerations.Count > maximumEntries) return Abandon();
        baselineDigest = digest;
        motion = candidate;
        generations = candidateGenerations;
        return UpdateReady() ? FenceResult.Ready : FenceResult.Applied;
    }

    /// <summary>Ordered, already authenticated lifecycle input. Overflow withdraws the entire view.</summary>
    public FenceResult ApplyLifecycle(ViewFence fence, MotionLifecycleChange change)
    {
        ValidateLifecycle(change);
        if (!Matches(fence)) return FenceResult.Ignored;
        if (history.TryGetValue(change.Sequence, out var previous))
        {
            if (!SameLifecycle(previous, change)) throw new InvalidDataException("Conflicting duplicate lifecycle sequence.");
            return FenceResult.Duplicate;
        }
        if (change.Sequence <= lastLifecycle) return FenceResult.Ignored;
        if (lastLifecycle == ulong.MaxValue || change.Sequence != lastLifecycle + 1)
            throw new InvalidDataException("Lifecycle input must be contiguous and cannot wrap.");
        if (history.Count == maximumHistory) return Abandon();
        var candidate = new Dictionary<ulong, MotionRecord>(motion);
        var candidateGenerations = new Dictionary<ulong, uint>(generations);
        ApplyLifecycleTo(candidate, candidateGenerations, change);
        if (candidateGenerations.Count > maximumEntries) return Abandon();
        motion = candidate;
        generations = candidateGenerations;
        // Do not retain unauthorized spawn payloads. The generation fence still survives in `generations`.
        if (change.InitialMotion is not { } spawn || Authorized(spawn)) history.Add(change.Sequence, change);
        lastLifecycle = change.Sequence;
        return UpdateReady() ? FenceResult.Ready : FenceResult.Applied;
    }

    /// <summary>ViewCommit waits for bulk completion and contiguous lifecycle delivery through its watermark.</summary>
    public FenceResult CommitView(ViewFence fence, ReadyFence ready)
    {
        if (!Matches(fence) || ready.BaselineId != baselineFence.Id) return FenceResult.Ignored;
        if (ready.LifecycleWatermark < baselineFence.LifecycleSequence)
            throw new InvalidDataException("Commit watermark precedes baseline capture.");
        if (commit is { } old && old != ready) throw new InvalidDataException("Conflicting baseline commit.");
        if (commit == ready) return FenceResult.Duplicate;
        commit = ready;
        return UpdateReady() ? FenceResult.Ready : FenceResult.Applied;
    }

    public bool TryGetReady(out ReadyFence ready)
    {
        CheckAuthorization();
        ready = commit.GetValueOrDefault();
        return phase == ReferenceViewPhase.Ready;
    }

    /// <summary>An externally authenticated server Active decision, after Ready ACK; never inferred from motion.</summary>
    public FenceResult ActivateAuthorizedView(ViewFence fence, ReadyFence acknowledgedReady)
    {
        if (!Matches(fence) || commit != acknowledgedReady) return FenceResult.Ignored;
        if (phase == ReferenceViewPhase.Active) return FenceResult.Duplicate;
        if (phase != ReferenceViewPhase.Ready) return FenceResult.Ignored;
        phase = ReferenceViewPhase.Active;
        return FenceResult.Applied;
    }

    public FenceResult ApplyDatagram(ReadOnlySpan<byte> bytes, int negotiatedLimit = MotionDatagramCodec.MaximumDatagramBytes) =>
        ApplyDatagram(MotionDatagramCodec.Decode(bytes, negotiatedLimit));

    public FenceResult ApplyDatagram(MotionDatagram packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(packet.Records);
        MotionDatagramCodec.ValidateHeader(packet.Header);
        if (packet.Records.Count > (MotionDatagramCodec.MaximumDatagramBytes - MotionDatagramCodec.HeaderBytes - 2) /
            (MotionDatagramCodec.RecordBytes + 2)) throw new InvalidDataException("Oversized typed datagram.");
        MotionDatagramCodec.ValidateRecords(packet.Records);
        if (!Matches(new(packet.Header.SessionEpoch, packet.Header.ViewEpoch)) || phase != ReferenceViewPhase.Active)
            return FenceResult.Ignored;
        var candidate = new Dictionary<ulong, MotionRecord>(motion);
        bool changed = false;
        foreach (var record in packet.Records)
        {
            // Also compare against the pre-batch revision: an earlier new revision must not conceal
            // a later conflicting duplicate of the currently committed revision.
            if (Authorized(record) && motion.TryGetValue(record.EntityId, out var previous))
                MotionDatagramCodec.ValidateDuplicate(previous, record);
            changed |= Merge(candidate, record);
        }
        motion = candidate; // A conflict anywhere above leaves the current state intact.
        return changed ? FenceResult.Applied : FenceResult.Duplicate;
    }

    /// <summary>Same absolute component/revision rule as datagrams; unknown entities are never buffered.</summary>
    public FenceResult ApplyReliableCheckpoint(ViewFence fence, MotionRecord record)
    {
        MotionDatagramCodec.ValidateRecord(record);
        if (!Matches(fence)) return FenceResult.Ignored;
        return Merge(motion, record) ? FenceResult.Applied : FenceResult.Duplicate;
    }

    public bool TryGetMotion(ulong entityId, out MotionRecord record)
    {
        CheckAuthorization();
        record = default;
        if (phase != ReferenceViewPhase.Active || !motion.TryGetValue(entityId, out var found)) return false;
        if (!Authorized(found)) { motion.Remove(entityId); return false; }
        record = found;
        return true;
    }

    bool Merge(Dictionary<ulong, MotionRecord> target, MotionRecord record)
    {
        if (!Authorized(record) || !target.TryGetValue(record.EntityId, out var previous) ||
            previous.InterestGeneration != record.InterestGeneration) return false;
        MotionDatagramCodec.ValidateDuplicate(previous, record);
        if (record.Revision <= previous.Revision) return false;
        target[record.EntityId] = record;
        return true;
    }

    void ApplyLifecycleTo(Dictionary<ulong, MotionRecord> target, Dictionary<ulong, uint> known, MotionLifecycleChange change)
    {
        known.TryGetValue(change.EntityId, out uint previousGeneration);
        if (change.Kind == MotionLifecycleKind.Despawn)
        {
            if (change.InterestGeneration < previousGeneration) return;
            known[change.EntityId] = change.InterestGeneration;
            target.Remove(change.EntityId);
            return;
        }
        if (change.InterestGeneration <= previousGeneration)
            throw new InvalidDataException("Re-entry requires a new interest generation.");
        known[change.EntityId] = change.InterestGeneration;
        var record = change.InitialMotion!.Value;
        target.Remove(record.EntityId);
        if (Authorized(record)) target.Add(record.EntityId, record);
    }

    bool Matches(ViewFence fence)
    {
        CheckAuthorization();
        return fence == new ViewFence(session, view) && phase is ReferenceViewPhase.Staging or ReferenceViewPhase.Ready or ReferenceViewPhase.Active;
    }
    bool Authorized(MotionRecord record) => authorization.IsViewAuthorized(session, view) &&
        authorization.IsEntityAuthorized(session, view, record.EntityId, record.InterestGeneration) &&
        (!revokedThrough.TryGetValue(record.EntityId, out uint revoked) || record.InterestGeneration > revoked);
    void CheckAuthorization()
    {
        if (view != 0 && !authorization.IsViewAuthorized(session, view))
        {
            Clear();
            phase = ReferenceViewPhase.Withdrawn;
            return;
        }
        foreach (var record in motion.Values.Concat(history.Values.Where(c => c.InitialMotion.HasValue).Select(c => c.InitialMotion!.Value)))
            if (!authorization.IsEntityAuthorized(session, view, record.EntityId, record.InterestGeneration))
                revokedThrough[record.EntityId] = Math.Max(revokedThrough.GetValueOrDefault(record.EntityId), record.InterestGeneration);
        foreach (ulong entity in motion.Where(p => !Authorized(p.Value)).Select(p => p.Key).ToArray()) motion.Remove(entity);
        foreach (ulong sequence in history.Where(p => revokedThrough.TryGetValue(p.Value.EntityId, out uint revoked) &&
                     p.Value.InterestGeneration <= revoked).Select(p => p.Key).ToArray()) history.Remove(sequence);
    }
    bool UpdateReady()
    {
        if (phase != ReferenceViewPhase.Staging || baselineDigest is null || commit is not { } ready || lastLifecycle < ready.LifecycleWatermark)
            return false;
        phase = ReferenceViewPhase.Ready;
        return true;
    }
    FenceResult Abandon() { Clear(); phase = ReferenceViewPhase.Abandoned; return FenceResult.Abandoned; }
    void Clear() { motion.Clear(); generations.Clear(); baselineDigest = null; revokedThrough.Clear(); history.Clear(); commit = null; }
    static byte[] BaselineDigest(IReadOnlyList<MotionRecord> records)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[MotionDatagramCodec.RecordBytes];
        foreach (var record in records.OrderBy(r => r.EntityId))
        {
            MotionDatagramCodec.WriteRecord(bytes, record);
            hash.AppendData(bytes);
        }
        return hash.GetHashAndReset();
    }
    static bool SameLifecycle(MotionLifecycleChange first, MotionLifecycleChange second) =>
        first.Sequence == second.Sequence && first.Kind == second.Kind && first.EntityId == second.EntityId &&
        first.InterestGeneration == second.InterestGeneration && (first.InitialMotion is { } a && second.InitialMotion is { } b
            ? MotionDatagramCodec.SamePayload(a, b) : first.InitialMotion is null && second.InitialMotion is null);
    static void ValidateLifecycle(MotionLifecycleChange change)
    {
        if (change.Sequence == 0 || change.EntityId == 0 || change.InterestGeneration == 0 || !Enum.IsDefined(change.Kind))
            throw new InvalidDataException("Invalid lifecycle identity or sequence.");
        if (change.Kind == MotionLifecycleKind.Spawn)
        {
            if (change.InitialMotion is not { } record || record.EntityId != change.EntityId || record.InterestGeneration != change.InterestGeneration)
                throw new InvalidDataException("Spawn requires matching initial motion.");
            MotionDatagramCodec.ValidateRecord(record);
        }
        else if (change.InitialMotion is not null) throw new InvalidDataException("Despawn cannot contain motion.");
    }
}
