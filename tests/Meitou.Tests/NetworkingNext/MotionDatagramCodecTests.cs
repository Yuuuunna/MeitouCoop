using System.Buffers.Binary;
using System.Numerics;
using Meitou.Networking.VNext;

namespace Meitou.Tests.NetworkingNext;

public class MotionDatagramCodecTests
{
    internal static MotionRecord Motion(ulong entity = 5, uint generation = 6, uint revision = 7, float x = 1) =>
        new(entity, generation, revision, new(x, 2, 3), new(4, 5, 6), 0.5f, MotionFlags.Moving | MotionFlags.Swimming, .75f);
    internal static readonly MotionHeader Header = new(1, 2, 3, 4, 9);
    static byte[] One() => MotionDatagramCodec.Encode(new(Header, [Motion()]));

    [Fact]
    public void FixedLittleEndianFixtureDefinesHeaderAndMotionLayout()
    {
        byte[] expected = Convert.FromHexString(
            "4D544E3215000100" + "0100000000000000" + "0200000000000000" + "0300000000000000" + "0400000000000000" +
            "0900000000000000" + "3600" + "0100" + "3200" + "0500000000000000" + "06000000" + "07000000" +
            "0000803F0000004000004040" + "000080400000A0400000C040" + "0000003F" + "0300" + "0000403F");
        Assert.Equal(104, expected.Length);
        Assert.Equal(expected, One());
        var decoded = MotionDatagramCodec.Decode(expected);
        Assert.Equal(Header, decoded.Header);
        Assert.Equal(Motion(), Assert.Single(decoded.Records));
    }

    [Theory]
    [InlineData(0, 0)] // magic
    [InlineData(4, 20)] // old protocol
    [InlineData(6, 2)] // unknown kind
    [InlineData(7, 1)] // header flags
    [InlineData(8, 0)] // session
    [InlineData(16, 0)] // view
    [InlineData(24, 0)] // packet sequence
    [InlineData(48, 49)] // body length
    [InlineData(50, 2)] // count
    [InlineData(52, 45)] // record length
    [InlineData(54, 0)] // entity
    [InlineData(62, 0)] // interest generation
    [InlineData(66, 0)] // component revision
    [InlineData(98, 32)] // motion flags
    public void MalformedEnvelopeAndRecordsReject(int offset, byte value)
    {
        byte[] bytes = One();
        bytes[offset] = value;
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Decode(bytes));
    }

    [Theory]
    [InlineData(70)] [InlineData(74)] [InlineData(78)]
    [InlineData(82)] [InlineData(86)] [InlineData(90)] [InlineData(94)]
    public void AllFloatFieldsRejectNanAndBothInfinities(int offset)
    {
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            byte[] bytes = One();
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), invalid);
            Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Decode(bytes));
        }
    }

    [Fact]
    public void LengthsAndAllocationCountsRejectBeforeTruncatedOrExcessDataCanBeAccepted()
    {
        byte[] valid = One();
        for (int size = 0; size < valid.Length; size++)
        {
            byte[] truncated = valid[..size];
            Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Decode(truncated));
        }
        byte[] excess = [.. valid, 0];
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Decode(excess));
        BinaryPrimitives.WriteUInt16LittleEndian(valid.AsSpan(50), ushort.MaxValue);
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Decode(valid));
    }

    [Fact]
    public void SplitUsesWholeIndependentDatagramsAndNegotiatedMtu()
    {
        var records = Enumerable.Range(1, 50).Select(i => Motion((ulong)i)).ToArray();
        var packets = MotionDatagramCodec.EncodeSplit(Header, records);
        Assert.Equal(new[] { 21, 21, 8 }, packets.Select(p => MotionDatagramCodec.Decode(p).Records.Count));
        Assert.All(packets, p => Assert.InRange(p.Length, 1, 1150));
        Assert.Equal(records, packets.SelectMany(p => MotionDatagramCodec.Decode(p).Records));
        Assert.Equal(new ulong[] { 3, 4, 5 }, packets.Select(p => MotionDatagramCodec.Decode(p).Header.PacketSequence));
        var small = MotionDatagramCodec.EncodeSplit(Header, records, 156);
        Assert.Equal(25, small.Count);
        Assert.All(small, p => Assert.Equal(156, p.Length));
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Encode(new(Header, records)));
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.EncodeSplit(Header, records, 99));
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Decode(packets[0], 1143));
        Assert.Equal(21, MotionDatagramCodec.Decode(packets[0], 65535).Records.Count);
    }

    [Fact]
    public void SenderRejectsSequenceOverflowAndInvalidRecordsBeforeProducingDatagrams()
    {
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.EncodeSplit(Header with { PacketSequence = ulong.MaxValue }, [Motion(), Motion(8)], 104));
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Encode(new(Header, [Motion(revision: 0)])));
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Encode(new(Header, [Motion() with { Velocity = new(float.PositiveInfinity) }])));
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Encode(new(Header with { ViewEpoch = 0 }, [Motion()])));
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.EncodeSplit(Header, [], 51));
    }

    [Fact]
    public void EqualRevisionRequiresIdenticalPayloadBitsAndDecoderRejectsConflictingBatch()
    {
        var first = Motion(x: 0);
        var duplicate = MotionDatagramCodec.Encode(new(Header, [first, first]));
        Assert.Equal(2, MotionDatagramCodec.Decode(duplicate).Records.Count);
        // Change the second record's X from +0 to -0: numeric equality does not permit revision reuse.
        BinaryPrimitives.WriteInt32LittleEndian(duplicate.AsSpan(122), int.MinValue);
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Decode(duplicate));
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Encode(new(Header, [first, first with { Yaw = 1 }])));
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Encode(new(Header, [first, first with { DrawYaw = 1 }])));
    }

    [Fact]
    public void DrawYawAllowsAbsentFallbackButRejectsInfinityAndRevisionBitConflicts()
    {
        var absent = Motion() with { DrawYaw = float.NaN };
        Assert.True(float.IsNaN(Assert.Single(MotionDatagramCodec.Decode(MotionDatagramCodec.Encode(new(Header, [absent]))).Records).DrawYaw));
        foreach (float infinity in new[] { float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Encode(new(Header, [absent with { DrawYaw = infinity }])));
        var otherNan = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00001));
        Assert.Throws<InvalidDataException>(() => MotionDatagramCodec.Encode(new(Header, [absent, absent with { DrawYaw = otherNan }])));
    }
}
