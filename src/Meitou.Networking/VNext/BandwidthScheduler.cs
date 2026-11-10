namespace Meitou.Networking.VNext;

public enum ScheduledLane { Control, Lifecycle, State, Bulk }
public enum ScheduleOutcome { Queued, Coalesced, Busy, ReduceSubscription, CloseSlowPeer }
public sealed record ScheduledSend(ulong Reservation, ulong Peer, ScheduledLane Lane, ulong Key, byte[] Payload);

/// <summary>
/// Single-owner deterministic queue admission, state coalescing and native send reservation tracking.
/// Does not artificially rate-limit or throttle application traffic.
/// </summary>
public sealed class BandwidthScheduler
{
    public const int MaximumStateKeys = 4096;
    public const int MaximumReliableQueuedItems = 256;
    public const int MaximumQueuedBytes = 256 * 1024;
    public const int MaximumOutstandingSends = 64;
    public const int MaximumLaneOutstandingSends = 24;
    readonly Func<TimeSpan> now;
    readonly SortedDictionary<ulong, Peer> peers = new();
    readonly Dictionary<ulong, ScheduledSend> reservations = new();
    TimeSpan last;
    ulong nextReservation = 1;
    ulong cursor;

    sealed class Peer
    {
        public readonly List<Work> Pending = [];
        public int Outstanding;
        public readonly int[] OutstandingByLane = new int[4];
    }
    sealed record Work(ScheduledLane Lane, ulong Key, int MaximumBytes, Func<byte[]> Serialize, TimeSpan Created);

    public BandwidthScheduler(Func<TimeSpan> monotonicNow)
    {
        ArgumentNullException.ThrowIfNull(monotonicNow);
        now = monotonicNow;
        last = now();
    }

    public void RegisterPeer(ulong peer)
    {
        Advance();
        if (peer == 0 || !peers.TryAdd(peer, new())) throw new ArgumentException("Peer must be unique and nonzero.", nameof(peer));
    }

    public void RemovePeer(ulong peer)
    {
        if (peers.TryGetValue(peer, out var state) && state.Outstanding != 0)
            throw new InvalidOperationException("Complete outstanding sends before removing a peer.");
        peers.Remove(peer);
    }

    /// <summary>
    /// Releases all unsent closures on withdrawal. Already admitted native buffers remain
    /// accounted until completion. This is not a cross-lane transport or authorization fence.
    /// </summary>
    public void DiscardPending(ulong peer) => peers[peer].Pending.Clear();

    /// <summary>Discards one unsent state key; admitted buffers and other lanes are unchanged.</summary>
    public bool CancelState(ulong peer, ulong key) =>
        peers[peer].Pending.RemoveAll(w => w.Lane == ScheduledLane.State && w.Key == key) != 0;

    /// <summary>
    /// State key is an opaque collision-free entity/component/generation identity assigned by the caller.
    /// Descriptor counts and declared payload bytes are bounded independently. Callers must bound
    /// serializer capture storage; arbitrary delegate captures are not covered by these limits.
    /// </summary>
    public ScheduleOutcome Enqueue(ulong peer, ScheduledLane lane, ulong key, int maximumApplicationBytes, Func<byte[]> serialize)
    {
        Advance();
        ArgumentNullException.ThrowIfNull(serialize);
        if (!Enum.IsDefined(lane)) throw new ArgumentOutOfRangeException(nameof(lane));
        int limit = lane switch { ScheduledLane.Control => 16 * 1024, ScheduledLane.State => 1150, _ => 64 * 1024 };
        if (maximumApplicationBytes <= 0 || maximumApplicationBytes > limit)
            throw new ArgumentOutOfRangeException(nameof(maximumApplicationBytes));
        var state = peers[peer];
        Expire(state);
        var old = lane == ScheduledLane.State ? state.Pending.Find(w => w.Lane == lane && w.Key == key) : null;
        int bytes = state.Pending.Where(w => w.Lane == lane).Sum(w => w.MaximumBytes) - (old?.MaximumBytes ?? 0);
        int count = state.Pending.Count(w => w.Lane == lane) - (old is null ? 0 : 1);
        bool full = bytes + maximumApplicationBytes > MaximumQueuedBytes ||
            (lane == ScheduledLane.Control && count >= 64) || (lane == ScheduledLane.State && count >= MaximumStateKeys) ||
            (lane is ScheduledLane.Lifecycle or ScheduledLane.Bulk && count >= MaximumReliableQueuedItems);
        if (full)
        {
            if (lane == ScheduledLane.State)
            {
                // Old unsent state cannot become a replay backlog. Caller must reduce cadence/subscription.
                state.Pending.RemoveAll(w => w.Lane == ScheduledLane.State);
                return ScheduleOutcome.ReduceSubscription;
            }
            return lane == ScheduledLane.Lifecycle ? ScheduleOutcome.CloseSlowPeer : ScheduleOutcome.Busy;
        }
        if (old is not null) state.Pending.Remove(old);
        state.Pending.Add(new(lane, key, maximumApplicationBytes, serialize, last));
        return old is null ? ScheduleOutcome.Queued : ScheduleOutcome.Coalesced;
    }

    public bool TryAdmit(out ScheduledSend? send)
    {
        Advance();
        send = null;
        foreach (ulong id in peers.Keys.Where(id => id > cursor).Concat(peers.Keys.Where(id => id <= cursor)))
        {
            var peer = peers[id];
            Expire(peer);

            var work = peer.Pending
                .Where(w => w.Lane == ScheduledLane.Control
                    ? peer.Outstanding < MaximumOutstandingSends
                    : peer.Outstanding < MaximumOutstandingSends - 16 && peer.OutstandingByLane[(int)w.Lane] < MaximumLaneOutstandingSends)
                .OrderBy(w => w.Lane)
                .FirstOrDefault();
            if (work is null) continue;
            if (nextReservation == ulong.MaxValue) throw new InvalidOperationException("Reservation IDs exhausted.");
            peer.Pending.Remove(work);
            byte[] payload = work.Serialize() ?? throw new InvalidDataException("Serializer returned null.");
            if (payload.Length > work.MaximumBytes)
                throw new InvalidDataException("Serializer exceeded admitted size.");
            send = new(nextReservation++, id, work.Lane, work.Key, payload);
            reservations.Add(send.Reservation, send);
            peer.Outstanding++;
            peer.OutstandingByLane[(int)work.Lane]++;
            cursor = id;
            return true;
        }
        return false;
    }

    /// <summary>Releases native submission ownership.</summary>
    public void CompleteSend(ScheduledSend send)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (!reservations.TryGetValue(send.Reservation, out var admitted) || !ReferenceEquals(send, admitted))
            throw new InvalidOperationException("Unknown or already completed reservation.");
        reservations.Remove(send.Reservation);
        peers[send.Peer].Outstanding--;
        peers[send.Peer].OutstandingByLane[(int)send.Lane]--;
    }

    public (int Keys, int Bytes) PendingState(ulong peer)
    {
        Advance();
        var state = peers[peer];
        Expire(state);
        var work = state.Pending.Where(w => w.Lane == ScheduledLane.State);
        return (work.Count(), work.Sum(w => w.MaximumBytes));
    }

    void Advance()
    {
        TimeSpan current = now();
        if (current < last) throw new InvalidOperationException("Monotonic clock moved backwards.");
        last = current;
    }

    void Expire(Peer peer) => peer.Pending.RemoveAll(w => w.Lane == ScheduledLane.State && last - w.Created >= TimeSpan.FromMilliseconds(100));
}
