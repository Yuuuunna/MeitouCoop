namespace Meitou.Networking.VNext;

public enum ScheduledLane { Control, Lifecycle, State, Bulk }
public enum ScheduleOutcome { Queued, Coalesced, Busy, ReduceSubscription, CloseSlowPeer }
public sealed record ScheduledSend(ulong Reservation, ulong Peer, ScheduledLane Lane, ulong Key, byte[] Payload,
    int ReservedWireBytes);

/// <summary>
/// Single-owner deterministic admission before encoding. This is a conservative application pacer,
/// not a wire-rate measurement. The adapter must report ALL received/sent UDP/IP bytes (including
/// ACKs, encryption, retries and handshake) exactly once, via cumulative ObserveWireTotals observations.
/// Native buffer completion is not a wire-byte observation. Debt stops further admission.
/// </summary>
public sealed class BandwidthScheduler
{
    public const int MaximumStateKeys = 4096;
    public const int MaximumReliableQueuedItems = 256;
    public const int MaximumQueuedBytes = 256 * 1024;
    public const int PeerWireBytesPerSecond = 62_500;
    public const int GlobalWireBytesPerSecond = 3_125_000;
    // Leave 36% headroom for inbound traffic, ACKs and retransmissions before reconciliation.
    public const int ApplicationWireBytesPerSecond = 40_000;
    public const int PeerBurstBytes = 8_000;
    public const int GlobalBurstBytes = 64_000;
    public const int PriorityReserveBytes = 2_000;
    public const int EstimatedOverheadBytes = 128;
    readonly Func<TimeSpan> now;
    readonly SortedDictionary<ulong, Peer> peers = new();
    readonly Dictionary<ulong, ScheduledSend> reservations = new();
    TimeSpan last;
    double globalCredit = GlobalBurstBytes;
    ulong nextReservation = 1;
    ulong cursor;

    sealed class Peer
    {
        public double Credit = PeerBurstBytes;
        public readonly List<Work> Pending = [];
        public int Outstanding;
        public readonly int[] OutstandingByLane = new int[4];
        public long Sent, Received;
        public double PeerReserved, EgressReserved;
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
        // Reservations remain bounded and reconcilable until the adapter supplies terminal accounting.
        if (peers.TryGetValue(peer, out var state) && state.Outstanding != 0)
            throw new InvalidOperationException("Complete outstanding sends before removing a peer.");
        peers.Remove(peer);
    }

    /// <summary>
    /// Releases all unsent closures on withdrawal. Already admitted native buffers and wire debt remain
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
        // Frames/chunks must be split before this scheduler: no item can exceed the bounded burst.
        int reserve = lane >= ScheduledLane.State ? PriorityReserveBytes : 0;
        if (maximumApplicationBytes <= 0 || maximumApplicationBytes + (long)EstimatedOverheadBytes + reserve > PeerBurstBytes || maximumApplicationBytes > limit)
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
        // One native submission per lane bounds ownership without letting bulk block control.
        foreach (ulong id in peers.Keys.Where(id => id > cursor).Concat(peers.Keys.Where(id => id <= cursor)))
        {
            var peer = peers[id];
            Expire(peer);

            var work = peer.Pending.Where(w => peer.OutstandingByLane[(int)w.Lane] == 0).OrderBy(w => w.Lane).FirstOrDefault();
            if (work is null) continue;
            int charge = work.MaximumBytes + EstimatedOverheadBytes;
            int reserve = work.Lane >= ScheduledLane.State ? PriorityReserveBytes : 0;
            if (peer.Credit - charge < reserve || globalCredit - charge < reserve) continue;
            if (nextReservation == ulong.MaxValue) throw new InvalidOperationException("Reservation IDs exhausted.");
            peer.Credit -= charge;
            peer.PeerReserved += charge;
            peer.EgressReserved += charge;
            globalCredit -= charge;
            peer.Pending.Remove(work);
            // Charge first, even if a serializer violates its contract or throws.
            byte[] payload;
            try { payload = work.Serialize() ?? throw new InvalidDataException("Serializer returned null."); }
            catch { peer.PeerReserved -= charge; peer.EgressReserved -= charge; throw; }
            if (payload.Length > work.MaximumBytes)
            {
                peer.PeerReserved -= charge; peer.EgressReserved -= charge;
                throw new InvalidDataException("Serializer exceeded admitted size.");
            }
            send = new(nextReservation++, id, work.Lane, work.Key, payload, charge);
            reservations.Add(send.Reservation, send);
            peer.Outstanding++;
            peer.OutstandingByLane[(int)work.Lane]++;
            cursor = id;
            return true;
        }
        return false;
    }

    /// <summary>Releases native submission credit only; never refunds bandwidth credit.</summary>
    public void CompleteSend(ScheduledSend send)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (!reservations.TryGetValue(send.Reservation, out var admitted) || !ReferenceEquals(send, admitted))
            throw new InvalidOperationException("Unknown or already completed reservation.");
        reservations.Remove(send.Reservation);
        peers[send.Peer].Outstanding--;
        peers[send.Peer].OutstandingByLane[(int)send.Lane]--;
    }

    /// <summary>
    /// Cumulative UDP/IP bytes from registration, both directions, including all transport traffic.
    /// Counters cannot reset within a connection. Duplicate observations are harmless. Outstanding
    /// reservations offset actual bytes in the next observation once; unused estimates expire.
    /// Excess creates debt, never an estimated-byte refund. Polling before delayed sends are counted
    /// can conservatively double-charge them; batch observations at the adapter accounting boundary.
    /// Global capacity accounts server egress only. Supply final totals before unregistering.
    /// </summary>
    public void ObserveWireTotals(ulong peer, long sentBytes, long receivedBytes)
    {
        Advance();
        var state = peers[peer];
        if (sentBytes < state.Sent || receivedBytes < state.Received)
            throw new ArgumentOutOfRangeException(nameof(sentBytes), "Wire counters must be monotonic and nonnegative.");
        if (sentBytes == state.Sent && receivedBytes == state.Received) return;
        double sent = sentBytes - state.Sent;
        double received = receivedBytes - state.Received;
        state.Sent = sentBytes;
        state.Received = receivedBytes;
        double peerExcess = Math.Max(0, sent + received - state.PeerReserved);
        double globalExcess = Math.Max(0, sent - state.EgressReserved);
        state.PeerReserved = 0;
        state.EgressReserved = 0;
        state.Credit -= peerExcess;
        globalCredit -= globalExcess;
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
        double seconds = (current - last).TotalSeconds;
        globalCredit = Math.Min(GlobalBurstBytes, globalCredit + seconds * GlobalWireBytesPerSecond);
        foreach (var peer in peers.Values)
            peer.Credit = Math.Min(PeerBurstBytes, peer.Credit + seconds * ApplicationWireBytesPerSecond);
        last = current;
    }

    void Expire(Peer peer) => peer.Pending.RemoveAll(w => w.Lane == ScheduledLane.State && last - w.Created >= TimeSpan.FromMilliseconds(100));
}
