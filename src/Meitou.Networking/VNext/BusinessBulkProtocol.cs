using System.Buffers.Binary;

namespace Meitou.Networking.VNext;

/// <summary>
/// Only whitelisted, strictly decoded business replies may use the shared bounded bulk lane.
/// </summary>
public static class BusinessBulkProtocol
{
    public static Guid Query(WireFrame reply, WorldViewCodec codec) => reply.Kind switch
    {
        WireMessage.CreationReply => CreationProtocol.ReadReply(reply, codec).Query,
        WireMessage.InvitationReply => InvitationProtocol.ReadReply(reply).Query,
        WireMessage.TalkHintReply => InteractionProtocol.ReadReply(reply).Query,
        _ => throw new InvalidDataException("This business message cannot use bulk."),
    };
    public static WireFrame Manifest(WireMessage kind, Guid query, BulkTransferManifest manifest)
    {
        byte[] bytes = new byte[94];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)kind);
        query.TryWriteBytes(bytes.AsSpan(2));
        GameViewProtocol.Manifest(manifest).Payload.Span.CopyTo(bytes.AsSpan(18));
        return new(WireMessage.BusinessBulkManifest, bytes);
    }

    public static (WireMessage Kind, Guid Query, BulkTransferManifest Manifest) ReadManifest(WireFrame frame)
    {
        if (frame.Payload.Length != 94)
            throw new InvalidDataException("Invalid business bulk manifest.");
        var kind = (WireMessage)BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.Span);
        Validate(kind);
        return (kind, new Guid(frame.Payload.Span.Slice(2, 16)), GameViewProtocol.ReadManifest(new(WireMessage.BulkManifest, frame.Payload[18..])));
    }

    public static WireFrame Cancel(WireMessage kind, Guid query)
    {
        byte[] bytes = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)kind);
        query.TryWriteBytes(bytes.AsSpan(2));
        return new(WireMessage.BusinessBulkCancelled, bytes);
    }

    public static (WireMessage Kind, Guid Query) ReadCancel(WireFrame frame)
    {
        if (frame.Payload.Length != 18)
            throw new InvalidDataException("Invalid business bulk cancellation.");
        var kind = (WireMessage)BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.Span);
        Validate(kind);
        return (kind, new Guid(frame.Payload.Span[2..]));
    }

    static void Validate(WireMessage kind)
    {
        if (kind is not (WireMessage.CreationReply or WireMessage.InvitationReply or WireMessage.TalkHintReply))
            throw new InvalidDataException("Unrecognized business bulk type.");
    }
}