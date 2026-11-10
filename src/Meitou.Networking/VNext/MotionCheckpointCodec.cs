namespace Meitou.Networking.VNext;

/// <summary>Candidate reliable lifecycle body; outer reliable framing is charged separately by the adapter.</summary>
public static class MotionCheckpointCodec
{
    public const int EncodedBytes = 4 + MotionDatagramCodec.HeaderBytes + 2 + 2 + MotionDatagramCodec.RecordBytes;
    public static byte[] Encode(MotionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        byte[] body = MotionDatagramCodec.Encode(new(new(checkpoint.SessionEpoch, checkpoint.ViewEpoch,
            checkpoint.Id, checkpoint.CaptureTick, checkpoint.AuthorityPublication), [checkpoint.Record]));
        byte[] result = new byte[4 + body.Length];
        "MCP1"u8.CopyTo(result);
        body.CopyTo(result, 4);
        return result;
    }

    public static MotionCheckpoint Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedBytes || !bytes[..4].SequenceEqual("MCP1"u8))
            throw new InvalidDataException("Invalid motion checkpoint body.");
        var packet = MotionDatagramCodec.Decode(bytes[4..]);
        if (packet.Records.Count != 1) throw new InvalidDataException("Checkpoint requires one absolute record.");
        return new(packet.Header.PacketSequence, packet.Header.SessionEpoch, packet.Header.ViewEpoch,
            packet.Header.ServerTick, packet.Records[0], packet.Header.AuthorityPublication);
    }
}
