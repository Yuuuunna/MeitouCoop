using Meitou.Networking.VNext.Transport;

namespace Meitou.Networking.VNext;

/// <summary>
/// Batches reliable motion transitions without changing their capture clocks or cumulative acknowledgement semantics.
/// Transient native queue pressure leaves checkpoints in the producer for the next publication.
/// </summary>
public sealed class MotionCheckpointSender
{
    /// <summary>
    /// A checkpoint batch uses the existing motion datagram body, also understood by the reliable lifecycle reader.
    /// </summary>
    const int MaximumRecords = (MotionDatagramCodec.MaximumDatagramBytes - MotionDatagramCodec.HeaderBytes - 2)
        / (MotionDatagramCodec.RecordBytes + 2);

    /// <summary>
    /// The connection whose scheduler lock serializes admission and checkpoint acknowledgement.
    /// </summary>
    readonly NativeGameConnection _transport;

    /// <summary>
    /// The authenticated session and recipient view that own this delivery ledger.
    /// </summary>
    readonly ulong _session, _view;

    /// <summary>
    /// Checkpoint identities admitted to the ordered lifecycle stream and awaiting the peer's cumulative acknowledgement.
    /// </summary>
    readonly HashSet<ulong> _submitted = [];

    /// <summary>
    /// Creates a ledger for one recipient epoch; a replacement baseline receives a fresh ledger.
    /// </summary>
    public MotionCheckpointSender(NativeGameConnection transport, ulong sessionEpoch, ulong viewEpoch)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (sessionEpoch == 0 || viewEpoch == 0) throw new ArgumentOutOfRangeException(nameof(viewEpoch));

        _transport = transport;
        _session = sessionEpoch;
        _view = viewEpoch;
    }

    /// <summary>
    /// Reports successful queue admission, before the scheduler lock permits native submission.
    /// </summary>
    public Action<MotionDatagram>? PacketQueued { get; set; }

    /// <summary>
    /// Reports native submission and buffer release; release is not an acknowledgement by the peer.
    /// </summary>
    public Action<MotionDatagram, bool>? SendObserved { get; set; }

    /// <summary>
    /// Releases only checkpoints actually admitted to this epoch's ordered reliable stream.
    /// </summary>
    public int Acknowledge(MotionReplicationProducer producer, ulong sessionEpoch, ulong viewEpoch, ulong highWater)
    {
        if (sessionEpoch != _session || viewEpoch != _view) return 0;

        return _transport.WithScheduler(_ =>
        {
            int released = producer.AcknowledgeCheckpointsThrough(sessionEpoch, viewEpoch, highWater, _submitted);
            _submitted.RemoveWhere(id => id <= highWater);
            return released;
        });
    }

    /// <summary>
    /// Attempts all unsent transitions in order. False means the next batch remains retained until queue capacity is available.
    /// </summary>
    public bool Flush(MotionReplicationProducer producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        return _transport.WithScheduler(_ => FlushPending(producer.PendingCheckpoints));
    }

    /// <summary>
    /// Stops at the first unadmitted batch, so a later cumulative acknowledgement cannot pass an unsent transition.
    /// </summary>
    bool FlushPending(IReadOnlyList<MotionCheckpoint> pending)
    {
        // Withdrawn generations no longer need an acknowledgement; repeated visibility changes must not grow this ledger.
        _submitted.IntersectWith(pending.Select(checkpoint => checkpoint.Id));
        int index = 0;
        while (index < pending.Count)
        {
            if (_submitted.Contains(pending[index].Id))
            {
                index++;
                continue;
            }

            var batch = TakeBatch(pending, ref index);
            if (!TrySendBatch(batch)) return false;
        }

        return true;
    }

    /// <summary>
    /// Packs consecutive pending records only when their original tick and authority publication match.
    /// </summary>
    List<MotionCheckpoint> TakeBatch(IReadOnlyList<MotionCheckpoint> pending, ref int index)
    {
        var first = pending[index];
        var batch = new List<MotionCheckpoint>(MaximumRecords);
        while (index < pending.Count && batch.Count < MaximumRecords)
        {
            var checkpoint = pending[index];
            if (_submitted.Contains(checkpoint.Id))
            {
                index++;
                continue;
            }

            if (checkpoint.SessionEpoch != _session || checkpoint.ViewEpoch != _view)
                throw new InvalidDataException("Motion checkpoint belongs to another recipient epoch.");
            if (checkpoint.CaptureTick != first.CaptureTick || checkpoint.AuthorityPublication != first.AuthorityPublication) break;

            batch.Add(checkpoint);
            index++;
        }

        return batch;
    }

    /// <summary>
    /// The existing checkpoint header carries the batch's highest identity; the client acknowledges after decoding all its records.
    /// </summary>
    bool TrySendBatch(IReadOnlyList<MotionCheckpoint> batch)
    {
        var last = batch[^1];
        var header = new MotionHeader(_session, _view, last.Id | (1UL << 63), last.CaptureTick, last.AuthorityPublication);
        var packet = new MotionDatagram(header, batch.Select(checkpoint => checkpoint.Record).ToArray());
        byte[] bytes = MotionDatagramCodec.Encode(packet);
        if (!_transport.TrySend(QuicLane.Lifecycle, bytes,
            submitted: () => Observe(() => SendObserved?.Invoke(packet, false)),
            released: () => Observe(() => SendObserved?.Invoke(packet, true)))) return false;

        foreach (var checkpoint in batch) _submitted.Add(checkpoint.Id);
        Observe(() => PacketQueued?.Invoke(packet));
        return true;
    }

    /// <summary>
    /// Diagnostics cannot interrupt reliable checkpoint admission or acknowledgement.
    /// </summary>
    static void Observe(Action observer)
    {
        try
        {
            observer();
        }
        catch
        {
            // Diagnostics never change delivery.
        }
    }
}
