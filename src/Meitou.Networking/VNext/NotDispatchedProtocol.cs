using System.Buffers.Binary;
namespace Meitou.Networking.VNext;

public enum NotDispatchedReason : byte { StaleView = 1 }
public sealed record OperationNotDispatched(WireMessage Kind, Guid Query, ulong Request, NotDispatchedReason Reason)
{
    public WireMessage ReplyKind => Kind switch
    {
        WireMessage.Command => WireMessage.CommandReceipt,
        WireMessage.CreationCatalogQuery or WireMessage.CreationPreview or WireMessage.CreationCommit or WireMessage.CreationLookup => WireMessage.CreationReply,
        WireMessage.InvitationRequest => WireMessage.InvitationReply,
        WireMessage.TalkHintQuery => WireMessage.TalkHintReply,
        _ => Kind,
    };
}
public sealed class RemoteOperationNotDispatchedException() : IOException("This submission was not dispatched because the authorized view changed. An earlier durable operation with the same identity may already have completed; query its outcome before retrying.");
public static class NotDispatchedProtocol
{
    public static WireFrame Reply(WireFrame request)
    {
        var result = request.Kind switch
        {
            WireMessage.Command => new OperationNotDispatched(request.Kind, default, CommandProtocol.Decode(request).Request, NotDispatchedReason.StaleView),
            WireMessage.EnterWorld => new(request.Kind, default, SessionProtocol.ReadEnter(request).FocusRole.Value, NotDispatchedReason.StaleView),
            WireMessage.Observe => Observation(request),
            WireMessage.CreationCatalogQuery => new(request.Kind, CreationProtocol.ReadCatalog(request).Query, 0, NotDispatchedReason.StaleView),
            WireMessage.CreationPreview or WireMessage.CreationCommit => new(request.Kind, CreationProtocol.ReadDraft(request).Query, 0, NotDispatchedReason.StaleView),
            WireMessage.CreationLookup => new(request.Kind, CreationProtocol.ReadLookup(request).Query, 0, NotDispatchedReason.StaleView),
            WireMessage.InvitationRequest => new(request.Kind, InvitationProtocol.ReadRequest(request).Query, 0, NotDispatchedReason.StaleView),
            WireMessage.TalkHintQuery => new(request.Kind, InteractionProtocol.ReadQuery(request).Query, 0, NotDispatchedReason.StaleView),
            _ => throw new InvalidDataException("Unexpected stale request kind."),
        };
        byte[] bytes = new byte[27]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)result.Kind); bytes[2] = (byte)result.Reason;
        result.Query.TryWriteBytes(bytes.AsSpan(3)); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(19), result.Request);
        return new(WireMessage.OperationNotDispatched, bytes);
    }
    static OperationNotDispatched Observation(WireFrame request)
    { _ = SessionProtocol.ReadObservation(request); return new(request.Kind, default, 0, NotDispatchedReason.StaleView); }
    public static OperationNotDispatched Read(WireFrame frame)
    {
        if (frame.Payload.Length != 27) throw new InvalidDataException("Invalid not-dispatched correlation.");
        var value = new OperationNotDispatched((WireMessage)BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.Span),
            new Guid(frame.Payload.Span.Slice(3, 16)), BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.Span[19..]), (NotDispatchedReason)frame.Payload.Span[2]);
        bool valid = value.Kind switch
        {
            WireMessage.Command or WireMessage.EnterWorld => value.Request != 0 && value.Query == Guid.Empty,
            WireMessage.Observe => value.Request == 0 && value.Query == Guid.Empty,
            WireMessage.CreationCatalogQuery or WireMessage.CreationPreview or WireMessage.CreationCommit or WireMessage.CreationLookup or WireMessage.InvitationRequest or WireMessage.TalkHintQuery => value.Query != Guid.Empty && value.Request == 0,
            _ => false,
        };
        if (!valid || value.Reason != NotDispatchedReason.StaleView) throw new InvalidDataException("Invalid not-dispatched identity or reason.");
        return value;
    }
}
