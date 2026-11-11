using System.Buffers.Binary;
using System.Numerics;

namespace Meitou.Networking.VNext;
[Flags]
public enum MotionFlags : ushort
{
    None = 0,
    Moving = 1,
    Swimming = 2,
    Carried = 4,
    Ragdoll = 8,
    Discontinuity = 16,
}

public readonly record struct MotionRecord(ulong EntityId, uint InterestGeneration, uint Revision, Vector3 Position, Vector3 Velocity, float Yaw, MotionFlags Flags, float DrawYaw = float.NaN);
public readonly record struct MotionHeader(ulong SessionEpoch, ulong ViewEpoch, ulong PacketSequence, ulong ServerTick, ulong AuthorityPublication = 0);
public sealed record MotionDatagram(MotionHeader Header, IReadOnlyList<MotionRecord> Records);
/// <summary>
/// Isolated protocol 21 motion codec. It performs no transport, authorization or replica mutation.
/// </summary>
public static class MotionDatagramCodec
{
    public const ushort Version = 21;
    public const byte MotionKind = 1;
    public const int HeaderBytes = 50;
    public const int RecordBytes = 50;
    public const int MaximumDatagramBytes = 1150;
    const MotionFlags KnownFlags = MotionFlags.Moving | MotionFlags.Swimming | MotionFlags.Carried | MotionFlags.Ragdoll | MotionFlags.Discontinuity;
    public static byte[] Encode(MotionDatagram packet, int negotiatedLimit = MaximumDatagramBytes)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(packet.Records);
        ValidateHeader(packet.Header);
        int limit = Limit(negotiatedLimit);
        int count = packet.Records.Count;
        if (count > ushort.MaxValue || count > (limit - HeaderBytes - 2) / (RecordBytes + 2))
            throw new InvalidDataException("Motion records exceed the datagram limit.");
        ValidateRecords(packet.Records);
        byte[] bytes = new byte[HeaderBytes + 2 + count * (RecordBytes + 2)];
        WriteHeader(bytes, packet.Header);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(HeaderBytes), (ushort)count);
        for (int i = 0; i < count; i++)
        {
            int offset = HeaderBytes + 2 + i * (RecordBytes + 2);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), RecordBytes);
            WriteRecord(bytes.AsSpan(offset + 2), packet.Records[i]);
        }

        return bytes;
    }

    /// <summary>
    /// Splits only between complete records; every returned datagram is independently decodable.
    /// </summary>
    public static IReadOnlyList<byte[]> EncodeSplit(MotionHeader firstHeader, IReadOnlyList<MotionRecord> records, int negotiatedLimit = MaximumDatagramBytes)
    {
        ArgumentNullException.ThrowIfNull(records);
        ValidateHeader(firstHeader);
        int limit = Limit(negotiatedLimit);
        int capacity = (limit - HeaderBytes - 2) / (RecordBytes + 2);
        if (records.Count != 0 && capacity == 0)
            throw new InvalidDataException("A complete motion record does not fit the negotiated datagram limit.");
        ValidateRecords(records);
        int packets = records.Count == 0 ? 1 : (records.Count - 1) / capacity + 1;
        if ((ulong)(packets - 1) > ulong.MaxValue - firstHeader.PacketSequence)
            throw new InvalidDataException("Packet sequences cannot wrap within an epoch.");
        var result = new List<byte[]>(packets);
        for (int p = 0, offset = 0; p < packets; p++)
        {
            int count = Math.Min(capacity, records.Count - offset);
            var batch = new MotionRecord[count];
            for (int i = 0; i < count; i++)
                batch[i] = records[offset + i];
            result.Add(Encode(new(firstHeader with { PacketSequence = firstHeader.PacketSequence + (ulong)p }, batch), limit));
            offset += count;
        }

        return result;
    }

    public static MotionDatagram Decode(ReadOnlySpan<byte> bytes, int negotiatedLimit = MaximumDatagramBytes)
    {
        int limit = Limit(negotiatedLimit);
        if (bytes.Length < HeaderBytes + 2 || bytes.Length > limit)
            throw new InvalidDataException("Invalid motion datagram length.");
        if (!bytes[..4].SequenceEqual("MTN2"u8) || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Version || bytes[6] != MotionKind || bytes[7] != 0)
            throw new InvalidDataException("Unknown motion datagram magic, version, kind or flags.");
        var header = new MotionHeader(ReadU64(bytes[8..]), ReadU64(bytes[16..]), ReadU64(bytes[24..]), ReadU64(bytes[32..]), ReadU64(bytes[40..]));
        ValidateHeader(header);
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[48..]) != bytes.Length - HeaderBytes)
            throw new InvalidDataException("Motion body length does not match the datagram.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[HeaderBytes..]);
        if (2 + count * (RecordBytes + 2) != bytes.Length - HeaderBytes)
            throw new InvalidDataException("Motion record count does not match the body length.");
        // Lengths, values and conflicting duplicates are checked before allocating the record array.
        for (int i = 0; i < count; i++)
        {
            int offset = HeaderBytes + 2 + i * (RecordBytes + 2);
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]) != RecordBytes)
                throw new InvalidDataException("Unknown motion record length.");
            MotionRecord record = ReadRecord(bytes[(offset + 2)..]);
            ValidateRecord(record);
            for (int j = 0; j < i; j++)
                ValidateDuplicate(ReadRecord(bytes[(HeaderBytes + 4 + j * (RecordBytes + 2))..]), record);
        }

        var records = new MotionRecord[count];
        for (int i = 0; i < count; i++)
            records[i] = ReadRecord(bytes[(HeaderBytes + 4 + i * (RecordBytes + 2))..]);
        return new(header, records);
    }

    internal static void ValidateHeader(MotionHeader header)
    {
        if (header.SessionEpoch == 0 || header.ViewEpoch == 0 || header.PacketSequence == 0)
            throw new InvalidDataException("Session, view and packet sequence must be nonzero.");
    }

    internal static void ValidateRecord(MotionRecord record)
    {
        if (record.EntityId == 0 || record.InterestGeneration == 0 || record.Revision == 0 || !Finite(record.Position) || !Finite(record.Velocity) || !float.IsFinite(record.Yaw) || float.IsInfinity(record.DrawYaw) || (record.Flags & ~KnownFlags) != 0)
            throw new InvalidDataException("Invalid motion identity, revision, coordinates or flags.");
    }

    internal static void ValidateRecords(IReadOnlyList<MotionRecord> records)
    {
        var revisions = new Dictionary<(ulong, uint, uint), MotionRecord>();
        foreach (var record in records)
        {
            ValidateRecord(record);
            var key = (record.EntityId, record.InterestGeneration, record.Revision);
            if (revisions.TryGetValue(key, out var previous))
                ValidateDuplicate(previous, record);
            else
                revisions.Add(key, record);
        }
    }

    internal static void ValidateDuplicate(MotionRecord previous, MotionRecord record)
    {
        if (previous.EntityId == record.EntityId && previous.InterestGeneration == record.InterestGeneration && previous.Revision == record.Revision && !SamePayload(previous, record))
            throw new InvalidDataException("Conflicting motion payloads use the same component revision.");
    }

    // Compare IEEE bits as well: equal revision means equal bytes, including signed zero.
    internal static bool SamePayload(MotionRecord a, MotionRecord b) => a.EntityId == b.EntityId && a.InterestGeneration == b.InterestGeneration && a.Revision == b.Revision && a.Flags == b.Flags && Same(a.Position.X, b.Position.X) && Same(a.Position.Y, b.Position.Y) && Same(a.Position.Z, b.Position.Z) && Same(a.Velocity.X, b.Velocity.X) && Same(a.Velocity.Y, b.Velocity.Y) && Same(a.Velocity.Z, b.Velocity.Z) && Same(a.Yaw, b.Yaw) && Same(a.DrawYaw, b.DrawYaw);
    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static int Limit(int limit)
    {
        int value = Math.Min(MaximumDatagramBytes, limit);
        if (value < HeaderBytes + 2)
            throw new InvalidDataException("Negotiated datagram limit cannot fit a motion header.");
        return value;
    }

    static ulong ReadU64(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    static MotionRecord ReadRecord(ReadOnlySpan<byte> bytes) => new(ReadU64(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]), new(BinaryPrimitives.ReadSingleLittleEndian(bytes[16..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[20..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[24..])), new(BinaryPrimitives.ReadSingleLittleEndian(bytes[28..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[32..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[36..])), BinaryPrimitives.ReadSingleLittleEndian(bytes[40..]), (MotionFlags)BinaryPrimitives.ReadUInt16LittleEndian(bytes[44..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[46..]));
    static void WriteHeader(Span<byte> bytes, MotionHeader header)
    {
        "MTN2"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], Version);
        bytes[6] = MotionKind;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], header.SessionEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], header.ViewEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], header.PacketSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], header.ServerTick);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], header.AuthorityPublication);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[48..], checked((ushort)(bytes.Length - HeaderBytes)));
    }

    internal static void WriteRecord(Span<byte> bytes, MotionRecord record)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, record.EntityId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], record.InterestGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], record.Revision);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[16..], record.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[20..], record.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[24..], record.Position.Z);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[28..], record.Velocity.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[32..], record.Velocity.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[36..], record.Velocity.Z);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[40..], record.Yaw);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[44..], (ushort)record.Flags);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[46..], record.DrawYaw);
    }
}