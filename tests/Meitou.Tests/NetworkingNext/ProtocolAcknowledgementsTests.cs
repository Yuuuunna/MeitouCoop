using System.Numerics;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Tests.Engine;
namespace Meitou.Tests.NetworkingNext;

public sealed class ProtocolAcknowledgementsTests
{
    [Fact]
    public void CheckpointBurstUsesOneCumulativeKeyAndCreditPrecedesReady()
    {
        var acknowledgements = new ProtocolAcknowledgements();
        for (ulong id = 1; id <= 10_000; id++) acknowledgements.Enqueue(GameViewProtocol.Epoch(WireMessage.MotionCheckpointAck, 11, 22, id | (1ul << 63)));
        Assert.Equal(1, acknowledgements.Count);
        var identity = new BulkTransferIdentity(11, 22, 4, 9, 7);
        acknowledgements.Enqueue(GameViewProtocol.Ack(identity, 4096)); acknowledgements.Enqueue(GameViewProtocol.Ack(identity, 8192)); acknowledgements.Enqueue(GameViewProtocol.Ack(identity, 4096));
        acknowledgements.Enqueue(GameViewProtocol.Epoch(WireMessage.ViewReady, 11, 22, 7));
        acknowledgements.Enqueue(GameViewProtocol.Epoch(WireMessage.BulkManifestAck, 11, 22, 4));
        acknowledgements.Flush(_ => false); Assert.Equal(4, acknowledgements.Count);
        var sent = new List<WireFrame>(); acknowledgements.Flush(frame => { sent.Add(frame); return true; });
        Assert.Equal(new[] { WireMessage.BulkManifestAck, WireMessage.BulkAck, WireMessage.ViewReady, WireMessage.MotionCheckpointAck }, sent.Select(frame => frame.Kind));
        Assert.Equal(8192, GameViewProtocol.ReadAck(sent[1]).Offset);
        Assert.Equal(10_000ul | (1ul << 63), GameViewProtocol.ReadEpoch(sent[3]).Publication); Assert.Equal(0, acknowledgements.Count);
        acknowledgements.Enqueue(GameViewProtocol.Epoch(WireMessage.MotionCheckpointAck, 11, 22, 5)); acknowledgements.Fence(11, 23); Assert.Equal(0, acknowledgements.Count);
    }
    [Fact]
    public void CumulativeCheckpointCreditCannotReleaseUnsubmittedOrNewerEpochRecords()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero); scheduler.RegisterPeer(1);
        var producer = new MotionReplicationProducer(scheduler, 1, 1, 11, 22, () => TimeSpan.Zero);
        var root = new MotionRecord(42, 1, 1, Vector3.Zero, Vector3.Zero, 0, MotionFlags.None);
        producer.Capture(11, 22, 1, root, MotionRelevance.Controlled, 1);
        producer.Capture(11, 22, 2, root with { Revision = 2, Flags = MotionFlags.Moving, Velocity = Vector3.UnitX }, MotionRelevance.Controlled, 2);
        producer.Capture(11, 22, 3, root with { Revision = 3 }, MotionRelevance.Controlled, 3);
        Assert.Equal(3, producer.PendingCheckpoints.Count);
        Assert.Equal(1, producer.AcknowledgeCheckpointsThrough(11, 22, 3, new HashSet<ulong> { 1 }));
        Assert.Equal(new ulong[] { 2, 3 }, producer.PendingCheckpoints.Select(checkpoint => checkpoint.Id));
        Assert.Equal(0, producer.AcknowledgeCheckpointsThrough(11, 21, 3, new HashSet<ulong> { 2, 3 }));
        Assert.Equal(0, producer.AcknowledgeCheckpointsThrough(11, 22, 1, new HashSet<ulong> { 2, 3 }));
        Assert.Equal(1, producer.AcknowledgeCheckpointsThrough(11, 22, 2, new HashSet<ulong> { 2, 3 }));
        Assert.Equal(3ul, Assert.Single(producer.PendingCheckpoints).Id);
    }
    [Fact]
    public void CriticalAndFullCaptureOfTheSameMovingTickPreserveMotionRevisionAndVelocity()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        view = view with { Snapshot = view.Snapshot with { Tick = 0, Characters = view.Snapshot.Characters.Select(character => character with { Animations = [] }).ToArray() } };
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22); publisher.Capture(view);
        var moving = view with { Publication = view.Publication + 1, Snapshot = view.Snapshot with { Tick = 3, Characters = view.Snapshot.Characters.Select(character => character with { Position = character.Position + Vector3.UnitX }).ToArray() } };
        var critical = Assert.Single(publisher.CaptureCritical(moving).First().Motion).Record;
        var full = Assert.Single(publisher.Capture(moving).Motion).Record;
        Assert.NotEqual(Vector3.Zero, critical.Velocity); Assert.Equal(critical, full);
    }
}
