using System.Buffers.Binary;
using System.Security.Cryptography;
using Meitou.Networking.VNext;

namespace Meitou.Tests.NetworkingNext;

public class BulkTransferTests
{
    static readonly BulkTransferIdentity Identity = new(1, 2, 3, 4, 5);

    [Fact]
    public void ExactChunkFrameRoundtripAndMalformedLengthsReject()
    {
        var chunk = new BulkChunk(Identity, 4096, new byte[] { 7, 8 });
        byte[] frame = BulkChunkCodec.Encode(chunk);
        Assert.Equal(54, frame.Length);
        Assert.Equal("4D5442321500010000000000000002000000000000000300000000000000040000000000000005000000000000000010000002000708",
            Convert.ToHexString(frame));
        var decoded = BulkChunkCodec.Decode(frame);
        Assert.Equal(Identity, decoded.Identity);
        Assert.Equal(4096, decoded.Offset);
        Assert.Equal(new byte[] { 7, 8 }, decoded.Payload.ToArray());
        Assert.Throws<InvalidDataException>(() => BulkChunkCodec.Decode(frame.AsSpan(0, frame.Length - 1)));
        frame[4] = 20;
        Assert.Throws<InvalidDataException>(() => BulkChunkCodec.Decode(frame));
        frame[4] = 21;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(50), 4097);
        Assert.Throws<InvalidDataException>(() => BulkChunkCodec.Decode(frame));
    }

    [Fact]
    public void NativeOwnershipNeverReleasesApplicationCreditAndAckBoundariesAreComputed()
    {
        byte[] source = new byte[BulkTransferSender.MaximumUnacknowledgedBytes + 1];
        var sender = new BulkTransferSender(1, 2);
        Assert.Equal(BulkTransferOutcome.Accepted, sender.Begin(Identity, source, out _));
        for (int i = 0; i < 64; i++) Assert.True(sender.TryTakeChunk(out _));
        Assert.Equal(256 * 1024, sender.UnacknowledgedBytes);
        Assert.False(sender.TryTakeChunk(out _));
        Assert.Equal(BulkTransferOutcome.NotReady, sender.Finish());
        Assert.Equal(BulkTransferOutcome.InvalidLength, sender.Acknowledge(Identity, 1));
        Assert.Equal(BulkTransferOutcome.InvalidLength, sender.Acknowledge(Identity, source.Length));
        Assert.Equal(BulkTransferOutcome.Accepted, sender.Acknowledge(Identity, 4096));
        Assert.Equal(BulkTransferOutcome.Duplicate, sender.Acknowledge(Identity, 4096));
        Assert.True(sender.TryTakeChunk(out var final));
        Assert.Single(final!.Payload.ToArray());
        Assert.Equal(BulkTransferOutcome.Accepted, sender.Acknowledge(Identity, source.Length));
        Assert.Equal(BulkTransferOutcome.Accepted, sender.Finish());
        Assert.False(sender.Active);
    }

    [Fact]
    public void ReceiverStagesOnlyCompleteDigestVerifiedBytes()
    {
        byte[] source = Enumerable.Range(0, 9000).Select(i => (byte)i).ToArray();
        byte[] expected = source.ToArray();
        var sender = new BulkTransferSender(1, 2);
        var receiver = new BulkTransferReceiver(1, 2);
        Assert.Equal(BulkTransferOutcome.Accepted, sender.Begin(Identity, source, out var manifest));
        Array.Fill(source, (byte)0); // sender retained an independent authorized copy
        Assert.Equal(BulkTransferOutcome.Accepted, receiver.Begin(manifest!));
        Assert.Equal(BulkTransferOutcome.NotReady, receiver.TryComplete(out _));
        while (sender.TryTakeChunk(out var chunk))
        {
            Assert.Equal(BulkTransferOutcome.Accepted, receiver.AcceptChunk(chunk!));
            Assert.Equal(BulkTransferOutcome.Duplicate, receiver.AcceptChunk(chunk!));
            Assert.Equal(BulkTransferOutcome.Accepted, sender.Acknowledge(Identity, receiver.ContiguousBytes));
        }
        Assert.Equal(BulkTransferOutcome.Accepted, receiver.TryComplete(out var staged));
        Assert.Equal(expected, staged!.Bytes.ToArray());
        Assert.Equal(Identity, staged.Identity);
        Assert.Equal(BulkTransferOutcome.NotReady, receiver.TryComplete(out _));
        Assert.Equal(BulkTransferOutcome.Duplicate, receiver.Begin(manifest!));
    }

    [Fact]
    public void ViewWithdrawalFencesLateChunksAndAcknowledgements()
    {
        var sender = new BulkTransferSender(1, 2);
        var receiver = new BulkTransferReceiver(1, 2);
        sender.Begin(Identity, new byte[5000], out var oldManifest);
        receiver.Begin(oldManifest!);
        Assert.True(sender.TryTakeChunk(out var oldChunk));
        receiver.AcceptChunk(oldChunk!);
        Assert.Equal(BulkTransferOutcome.Accepted, sender.AdvanceView(3));
        Assert.Equal(BulkTransferOutcome.Accepted, receiver.AdvanceView(3));
        Assert.False(sender.TryTakeChunk(out _));
        Assert.Equal(0, receiver.ContiguousBytes);
        Assert.Equal(BulkTransferOutcome.StaleEpoch, receiver.AcceptChunk(oldChunk!));
        Assert.Equal(BulkTransferOutcome.NotReady, receiver.TryComplete(out _));
        var newer = Identity with { ViewEpoch = 3 };
        sender.Begin(newer, new byte[5000], out var newManifest);
        receiver.Begin(newManifest!);
        Assert.True(sender.TryTakeChunk(out _));
        Assert.Equal(BulkTransferOutcome.StaleEpoch, sender.Acknowledge(Identity, 4096));
        Assert.Equal(4096, sender.UnacknowledgedBytes);
        sender.Withdraw();
        receiver.Withdraw();
        Assert.False(sender.Active);
        Assert.False(receiver.Active);
    }

    [Fact]
    public void DuplicateBeginPreservesProgressAndConflictDoesNotReset()
    {
        var sender = new BulkTransferSender(1, 2);
        var receiver = new BulkTransferReceiver(1, 2);
        byte[] source = new byte[5000];
        sender.Begin(Identity, source, out var manifest);
        receiver.Begin(manifest!);
        sender.TryTakeChunk(out var first);
        receiver.AcceptChunk(first!);
        Assert.Equal(BulkTransferOutcome.Duplicate, sender.Begin(Identity, source, out var duplicateManifest));
        Assert.Same(manifest, duplicateManifest);
        Assert.Equal(BulkTransferOutcome.Duplicate, receiver.Begin(manifest!));
        source[0] = 1;
        Assert.Equal(BulkTransferOutcome.Conflict, sender.Begin(Identity, source, out _));
        Assert.Equal(BulkTransferOutcome.Conflict, receiver.Begin(new(Identity, source.Length, SHA256.HashData(source))));
        Assert.Equal(4096, receiver.ContiguousBytes);
        Assert.Equal(4096, sender.UnacknowledgedBytes);
        Assert.Equal(BulkTransferOutcome.Busy, sender.Begin(Identity with { BaselineId = 4 }, source, out _));
    }

    [Fact]
    public void GapsShortChunksAndChecksumMismatchNeverYieldReady()
    {
        var receiver = new BulkTransferReceiver(1, 2);
        var manifest = new BulkTransferManifest(Identity, 5000, SHA256.HashData(new byte[5000]));
        receiver.Begin(manifest);
        Assert.Equal(BulkTransferOutcome.OutOfOrder, receiver.AcceptChunk(new(Identity, 4096, new byte[904])));
        Assert.Equal(0, receiver.ContiguousBytes);
        Assert.Equal(BulkTransferOutcome.InvalidLength, receiver.AcceptChunk(new(Identity, 0, new byte[2])));
        receiver.AcceptChunk(new(Identity, 0, new byte[4096]));
        byte[] corrupt = new byte[904];
        corrupt[0] = 1;
        receiver.AcceptChunk(new(Identity, 4096, corrupt));
        Assert.Equal(BulkTransferOutcome.DigestMismatch, receiver.TryComplete(out var result));
        Assert.Null(result);
        Assert.False(receiver.Active);
    }

    [Fact]
    public void TransferBoundsAndSessionValidationPrecedeAllocation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BulkTransferManifest(Identity, 0, new byte[32]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BulkTransferManifest(Identity, BulkTransferSender.MaximumTransferBytes + 1, new byte[32]));
        Assert.Throws<ArgumentException>(() => new BulkTransferManifest(Identity, 1, new byte[31]));
        Assert.Throws<ArgumentException>(() => new BulkTransferManifest(Identity with { SessionEpoch = 0 }, 1, new byte[32]));
        var sender = new BulkTransferSender(1, 2);
        Assert.Equal(BulkTransferOutcome.InvalidLength, sender.Begin(Identity, [], out _));
        Assert.Equal(BulkTransferOutcome.StaleEpoch, sender.Begin(Identity with { SessionEpoch = 99 }, new byte[1], out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => sender.ResetSession(0, 2));
    }
    [Fact]
    public void DetachedStageRequiresDisposalAndSameSessionCannotResetFences()
    {
        var receiver = new BulkTransferReceiver(1, 2);
        byte[] payload = [1, 2, 3];
        var manifest = new BulkTransferManifest(Identity, payload.Length, SHA256.HashData(payload));
        receiver.Begin(manifest);
        receiver.AcceptChunk(new(Identity, 0, payload));
        Assert.Equal(BulkTransferOutcome.Accepted, receiver.TryComplete(out var staged));
        ReadOnlyMemory<byte> held = staged!.Bytes;
        staged.Dispose();
        Assert.Equal(new byte[3], held.ToArray());
        Assert.Throws<ObjectDisposedException>(() => staged.Bytes);
        staged.Dispose();
        Assert.Throws<ArgumentException>(() => receiver.ResetSession(1, 2));
        Assert.Throws<ArgumentException>(() => new BulkTransferSender(1, 2).ResetSession(1, 2));
        receiver.ResetSession(99, 1);
        Assert.Equal(BulkTransferOutcome.StaleEpoch, receiver.Begin(manifest));
    }
}
