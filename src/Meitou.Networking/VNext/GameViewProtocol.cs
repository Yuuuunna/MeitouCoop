using System.Buffers.Binary;

namespace Meitou.Networking.VNext;
public static class GameViewProtocol
{
    public static WireFrame Epoch(WireMessage kind, ulong session, ulong view, ulong publication = 0)
    {
        byte[] bytes = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, session);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), view);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), publication);
        return new(kind, bytes);
    }

    public static (ulong Session, ulong View, ulong Publication) ReadEpoch(WireFrame frame)
    {
        var bytes = frame.Payload.Span;
        if (bytes.Length != 24)
            throw new InvalidDataException("Invalid view epoch message.");
        var result = (BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]));
        if (result.Item1 == 0 || result.Item2 == 0)
            throw new InvalidDataException("Zero view epoch.");
        return result;
    }

    public static WireFrame Manifest(BulkTransferManifest manifest)
    {
        byte[] bytes = new byte[76];
        WriteIdentity(bytes, manifest.Identity);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), manifest.TotalBytes);
        manifest.Digest.Span.CopyTo(bytes.AsSpan(44));
        return new(WireMessage.BulkManifest, bytes);
    }

    public static BulkTransferManifest ReadManifest(WireFrame frame)
    {
        if (frame.Payload.Length != 76)
            throw new InvalidDataException("Invalid bulk manifest.");
        var bytes = frame.Payload.Span;
        return new(ReadIdentity(bytes), BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]), bytes[44..]);
    }

    public static WireFrame Ack(BulkTransferIdentity identity, int offset)
    {
        byte[] bytes = new byte[44];
        WriteIdentity(bytes, identity);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), offset);
        return new(WireMessage.BulkAck, bytes);
    }

    public static (BulkTransferIdentity Identity, int Offset) ReadAck(WireFrame frame)
    {
        if (frame.Payload.Length != 44)
            throw new InvalidDataException("Invalid bulk ACK.");
        return (ReadIdentity(frame.Payload.Span), BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.Span[40..]));
    }

    public static byte[] Pack(IReadOnlyList<byte[]> frames)
    {
        long length = frames.Sum(f => 4L + f.Length);
        if (length <= 0 || length > BulkTransferSender.MaximumTransferBytes)
            throw new InvalidDataException("Baseline exceeds bounds.");
        byte[] result = new byte[(int)length];
        int offset = 0;
        foreach (var frame in frames)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), frame.Length);
            frame.CopyTo(result, offset + 4);
            offset += frame.Length + 4;
        }

        return result;
    }

    public static void Unpack(ReadOnlySpan<byte> bytes, Action<ReadOnlyMemory<byte>> apply)
    {
        while (!bytes.IsEmpty)
        {
            if (bytes.Length < 4)
                throw new InvalidDataException("Truncated baseline frame.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (length <= 0 || length > 64 * 1024 - 4 || length > bytes.Length - 4)
                throw new InvalidDataException("Invalid baseline frame size.");
            apply(bytes.Slice(4, length).ToArray());
            bytes = bytes[(4 + length)..];
        }
    }

    static void WriteIdentity(Span<byte> bytes, BulkTransferIdentity id)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, id.SessionEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], id.ViewEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], id.BaselineId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], id.CaptureTick);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], id.LifecycleWatermark);
    }

    static BulkTransferIdentity ReadIdentity(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]));
}