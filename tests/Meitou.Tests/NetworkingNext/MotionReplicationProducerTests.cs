using System.Numerics;
using Meitou.Networking.VNext;

namespace Meitou.Tests.NetworkingNext;

public class MotionReplicationProducerTests(ITestOutputHelper output)
{
    [Fact]
    public void StationaryTurningUsesInteractiveCadenceInsteadOfIdleHeartbeat()
    {
        TimeSpan time = TimeSpan.Zero;
        var scheduler = new BandwidthScheduler(() => time);
        scheduler.RegisterPeer(1);
        var producer = new MotionReplicationProducer(scheduler, 1, 99, 1, 1, () => time);
        var idle = new MotionRecord(7, 1, 1, Vector3.Zero, Vector3.Zero, 0, MotionFlags.None, 0);
        producer.Capture(1, 1, 1, idle, MotionRelevance.Controlled, 1);
        producer.Pump(1, 1);
        Assert.True(scheduler.TryAdmit(out var initial));
        scheduler.CompleteSend(initial!);
        time = TimeSpan.FromMilliseconds(50);
        producer.Capture(1, 1, 2, idle with { Revision = 2, DrawYaw = 1 }, MotionRelevance.Controlled, 2);
        producer.Pump(2, 2);
        Assert.True(scheduler.TryAdmit(out var turned));
        var decoded = Assert.Single(MotionDatagramCodec.Decode(turned!.Payload).Records);
        Assert.Equal(Vector3.Zero, decoded.Position);
        Assert.Equal(1, decoded.DrawYaw);
        scheduler.CompleteSend(turned);
        time = TimeSpan.FromMilliseconds(100);
        producer.Pump(2, 2);
        Assert.False(scheduler.TryAdmit(out _)); // Unchanged pose still refreshes at the idle rate.
    }

    [Fact]
    public void CandidateApplicationScheduleRuns120SecondsWith124EntitiesAndFinalConvergence()
    {
        TimeSpan time = TimeSpan.Zero;
        var scheduler = new BandwidthScheduler(() => time);
        scheduler.RegisterPeer(1);
        var producer = new MotionReplicationProducer(scheduler, 1, 99, 1, 1, () => time);
        var received = new Dictionary<ulong, MotionRecord>();
        var lastReceived = new Dictionary<ulong, TimeSpan>();
        long charged = 0;
        var queuedCheckpoints = new HashSet<ulong>();
        double activeMaximumGap = 0;
        for (int step = 0; step < 2400; step++)
        {
            time = TimeSpan.FromMilliseconds(step * 50);
            for (ulong id = 1; id <= 124; id++)
            {
                bool moving = step < 2360;
                var record = new MotionRecord(id, 1, (uint)(step + 1), new Vector3(moving ? step : 2359, id, 0),
                    moving ? Vector3.UnitX : Vector3.Zero, 0, moving ? MotionFlags.Moving : MotionFlags.None);
                Assert.Equal(MotionCaptureOutcome.Accepted, producer.Capture(1, 1, (ulong)step, record,
                    id <= 4 ? MotionRelevance.Controlled : id <= 12 ? MotionRelevance.Interactive :
                    id <= 60 ? MotionRelevance.Near : MotionRelevance.Far));
            }
            foreach (var checkpoint in producer.PendingCheckpoints)
            {
                if (queuedCheckpoints.Add(checkpoint.Id))
                    scheduler.Enqueue(1, ScheduledLane.Lifecycle, checkpoint.Id, MotionCheckpointCodec.EncodedBytes + 4,
                        () => MotionCheckpointCodec.Encode(checkpoint));
            }
            for (int batch = 0; batch < 8; batch++)
            {
                producer.Pump((ulong)step);
                if (!scheduler.TryAdmit(out var send)) break;
                charged += send!.ReservedWireBytes;
                Assert.InRange(send.Payload.Length, 1, 1150);
                if (send.Lane == ScheduledLane.Lifecycle)
                {
                    var checkpoint = MotionCheckpointCodec.Decode(send.Payload);
                    received[checkpoint.Record.EntityId] = checkpoint.Record;
                    Assert.True(producer.AcknowledgeCheckpoint(1, 1, checkpoint.Id));
                    scheduler.CompleteSend(send);
                    continue;
                }
                foreach (var record in MotionDatagramCodec.Decode(send.Payload).Records)
                {
                    if (record.EntityId <= 12 && step < 2360 && lastReceived.TryGetValue(record.EntityId, out var previous))
                        activeMaximumGap = Math.Max(activeMaximumGap, (time - previous).TotalMilliseconds);
                    lastReceived[record.EntityId] = time;
                    received[record.EntityId] = record;
                }
                scheduler.CompleteSend(send);
            }
        }
        output.WriteLine($"Candidate declared charges: {charged} bytes /120s = {charged / 120.0:F2} bytes/s; active maximum gap {activeMaximumGap}ms.");
        Assert.True(charged <= 120L * BandwidthScheduler.ApplicationWireBytesPerSecond + BandwidthScheduler.PeerBurstBytes);
        Assert.InRange(activeMaximumGap, 1, 100);
        Assert.Equal(124, received.Count);
        Assert.All(received.Values, r => { Assert.Equal(MotionFlags.None, r.Flags); Assert.Equal(2359, r.Position.X); });
        Assert.Empty(producer.PendingCheckpoints);
        Assert.InRange(producer.RetainedBytes, 1, MotionReplicationProducer.MaximumRetainedBytes);
        // Declared scheduler charges prove candidate application admission only, not the actual 500 kbit/s wire gate.
    }

    [Fact]
    public void LatestCaptureStrictRevisionsCheckpointAckAndEpochFences()
    {
        TimeSpan time = TimeSpan.Zero;
        var scheduler = new BandwidthScheduler(() => time);
        scheduler.RegisterPeer(1);
        var producer = new MotionReplicationProducer(scheduler, 1, 99, 1, 1, () => time);
        var first = new MotionRecord(1, 1, 1, Vector3.Zero, Vector3.UnitX, 0, MotionFlags.Moving);
        producer.Capture(1, 1, 0, first, MotionRelevance.Controlled);
        producer.Pump(0);
        var newer = first with { Revision = 2, Position = Vector3.One };
        producer.Capture(1, 1, 11, newer, MotionRelevance.Controlled);
        Assert.False(scheduler.TryAdmit(out _));
        Assert.Throws<InvalidOperationException>(() => producer.Pump(10));
        Assert.Throws<InvalidOperationException>(() => producer.Pump(12));
        producer.Pump(11);
        Assert.True(scheduler.TryAdmit(out var send));
        Assert.Equal(11UL, MotionDatagramCodec.Decode(send!.Payload).Header.ServerTick);
        Assert.Equal(newer, Assert.Single(MotionDatagramCodec.Decode(send!.Payload).Records));
        scheduler.CompleteSend(send);
        Assert.Throws<InvalidDataException>(() => producer.Capture(1, 1, 11, newer with { Position = Vector3.Zero }, MotionRelevance.Near));
        Assert.Throws<InvalidDataException>(() => producer.Capture(1, 1, 11, first, MotionRelevance.Near));
        var stop = newer with { Revision = 3, Flags = MotionFlags.None, Velocity = Vector3.Zero };
        producer.Capture(1, 1, 11, stop, MotionRelevance.Controlled);
        var checkpoints = producer.PendingCheckpoints;
        Assert.Equal(2, checkpoints.Count);
        Assert.True(producer.AcknowledgeCheckpoint(1, 1, checkpoints[0].Id));
        Assert.Equal(stop, Assert.Single(producer.PendingCheckpoints).Record);
        producer.BeginEpoch(1, 2);
        Assert.False(producer.AcknowledgeCheckpoint(1, 1, checkpoints[1].Id));
        Assert.Empty(producer.PendingCheckpoints);
        Assert.Throws<InvalidOperationException>(() => producer.Capture(1, 1, 11, stop, MotionRelevance.Controlled));
    }
    [Fact]
    public void WithdrawalCancelsUnsentAndUnchangedPhysicalPayloadOnlyRefreshesAtOneSecond()
    {
        TimeSpan time = TimeSpan.Zero;
        var scheduler = new BandwidthScheduler(() => time);
        scheduler.RegisterPeer(1);
        var producer = new MotionReplicationProducer(scheduler, 1, 99, 1, 1, () => time);
        var record = new MotionRecord(1, 1, 1, Vector3.Zero, Vector3.UnitX, 0, MotionFlags.Moving);
        producer.Capture(1, 1, 0, record, MotionRelevance.Controlled);
        producer.Pump(0);
        producer.Withdraw(1, 1);
        Assert.False(scheduler.TryAdmit(out _));
        Assert.Empty(producer.PendingCheckpoints);
        producer.Capture(1, 1, 0, record with { InterestGeneration = 2 }, MotionRelevance.Controlled);
        producer.Pump(0);
        Assert.True(scheduler.TryAdmit(out var send));
        scheduler.CompleteSend(send!);
        time = TimeSpan.FromMilliseconds(50);
        producer.Capture(1, 1, 0, record with { InterestGeneration = 2, Revision = 2 }, MotionRelevance.Controlled);
        Assert.Equal(MotionPumpOutcome.Idle, producer.Pump(0));
        time = TimeSpan.FromSeconds(1);
        Assert.Equal(MotionPumpOutcome.Queued, producer.Pump(0));
        Assert.True(scheduler.TryAdmit(out send));
        Assert.Equal(2U, Assert.Single(MotionDatagramCodec.Decode(send!.Payload).Records).Revision);
        scheduler.CompleteSend(send);
        time += TimeSpan.FromSeconds(1);
        producer.Pump(0);
        producer.BeginEpoch(1, 2);
        Assert.False(scheduler.TryAdmit(out _));
    }

    [Fact]
    public void ReliableCheckpointBackpressureIsBoundedAndRejectsBeforeMutation()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        var producer = new MotionReplicationProducer(scheduler, 1, 99, 1, 1, () => TimeSpan.Zero);
        for (uint revision = 1; revision <= MotionReplicationProducer.MaximumKeys; revision++)
            Assert.Equal(MotionCaptureOutcome.Accepted, producer.Capture(1, 1, 0,
                new(1, 1, revision, Vector3.Zero, Vector3.Zero, 0, MotionFlags.Discontinuity), MotionRelevance.Controlled));
        Assert.Equal(MotionCaptureOutcome.CheckpointBackpressure, producer.Capture(1, 1, 0,
            new(1, 1, 4097, Vector3.One, Vector3.Zero, 0, MotionFlags.None), MotionRelevance.Controlled));
        Assert.Equal(4096, producer.PendingCheckpoints.Count);
        Assert.InRange(producer.RetainedBytes, 1, MotionReplicationProducer.MaximumRetainedBytes);
        producer.Pump(0);
        Assert.True(scheduler.TryAdmit(out var send));
        Assert.Equal(4096U, Assert.Single(MotionDatagramCodec.Decode(send!.Payload).Records).Revision);
        scheduler.CompleteSend(send);
    }    [Fact]
    public void WireDebtReportsReducedCadenceAndRetainsImportantState()
    {
        TimeSpan time = TimeSpan.Zero;
        var scheduler = new BandwidthScheduler(() => time);
        scheduler.RegisterPeer(1);
        var producer = new MotionReplicationProducer(scheduler, 1, 99, 1, 1, () => time);
        var record = new MotionRecord(1, 1, 1, Vector3.One, Vector3.Zero, 0, MotionFlags.Moving);
        producer.Capture(1, 1, 0, record, MotionRelevance.Controlled);
        scheduler.ObserveWireTotals(1, 0, 100_000);
        time = TimeSpan.FromMilliseconds(200);
        Assert.Equal(MotionPumpOutcome.ReducedCadence, producer.Pump(0));
        Assert.False(scheduler.TryAdmit(out _));
        Assert.Single(producer.PendingCheckpoints);
        Assert.Equal(1, producer.RetainedKeys);
        time = TimeSpan.FromSeconds(3);
        producer.Pump(0);
        Assert.True(scheduler.TryAdmit(out var send));
        Assert.Equal(record, Assert.Single(MotionDatagramCodec.Decode(send!.Payload).Records));
        scheduler.CompleteSend(send);
    }    [Fact]
    public void CheckpointCodecRejectsMalformedBodyAndRoundTripsIdentity()
    {
        var checkpoint = new MotionCheckpoint(5, 1, 2, 8,
            new(3, 4, 6, Vector3.One, Vector3.Zero, 0, MotionFlags.Ragdoll));
        var bytes = MotionCheckpointCodec.Encode(checkpoint);
        Assert.Equal(checkpoint, MotionCheckpointCodec.Decode(bytes));
        Assert.Throws<InvalidDataException>(() => MotionCheckpointCodec.Decode(bytes.AsSpan(1)));
        bytes[0] = 0;
        Assert.Throws<InvalidDataException>(() => MotionCheckpointCodec.Decode(bytes));
    }}
