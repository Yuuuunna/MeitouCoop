namespace Meitou.Networking.VNext;

/// <summary>Bounded credit bookkeeping, independent of accepted business requests. Checkpoint ACKs are
/// cumulative because checkpoint records share one ordered reliable stream; bulk offsets already are.</summary>
public sealed class ProtocolAcknowledgements
{
    readonly Dictionary<(WireMessage Kind, ulong Session, ulong View, ulong Id), WireFrame> pending = [];
    public int Count => pending.Count;
    public void Enqueue(WireFrame frame)
    {
        (WireMessage Kind, ulong Session, ulong View, ulong Id) key;
        ulong progress;
        if (frame.Kind == WireMessage.BulkAck)
        {
            var ack = GameViewProtocol.ReadAck(frame); key = (frame.Kind, ack.Identity.SessionEpoch, ack.Identity.ViewEpoch, ack.Identity.BaselineId); progress = (ulong)ack.Offset;
        }
        else
        {
            var ack = GameViewProtocol.ReadEpoch(frame);
            if (frame.Kind is not (WireMessage.MotionCheckpointAck or WireMessage.BulkManifestAck or WireMessage.ViewReady)) throw new InvalidDataException("Unexpected acknowledgement kind.");
            key = (frame.Kind, ack.Session, ack.View, 0); progress = ack.Publication;
        }
        if (pending.TryGetValue(key, out var previous))
        {
            ulong old = frame.Kind == WireMessage.BulkAck ? (ulong)GameViewProtocol.ReadAck(previous).Offset : GameViewProtocol.ReadEpoch(previous).Publication;
            if (progress <= old) return;
        }
        else if (pending.Count == 16) throw new IOException("Transport acknowledgement identity bound exceeded.");
        pending[key] = frame;
    }
    public void Flush(Func<WireFrame, bool> submit)
    {
        foreach (var pair in pending.OrderBy(pair => Priority(pair.Key.Kind)).ThenBy(pair => pair.Key.Id).ToArray())
        {
            if (!submit(pair.Value)) break;
            pending.Remove(pair.Key);
        }
    }
    static int Priority(WireMessage kind) => kind switch { WireMessage.BulkManifestAck => 0, WireMessage.BulkAck => 1, WireMessage.ViewReady => 2, _ => 3 };
    public void Fence(ulong session, ulong view)
    { foreach (var key in pending.Keys.Where(key => key.Session != session || key.View != view).ToArray()) pending.Remove(key); }
}
