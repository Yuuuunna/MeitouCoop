using Meitou.Networking.VNext;

namespace Meitou.Tests.NetworkingNext;

public class BandwidthSchedulerTests
{
    [Fact]
    public void CoalescesBeforeEncodingAndExpiresAtDeadline()
    {
        TimeSpan time = TimeSpan.Zero;
        var scheduler = new BandwidthScheduler(() => time);
        scheduler.RegisterPeer(1);
        int calls = 0;
        scheduler.Enqueue(1, ScheduledLane.State, 9, 20, () => { calls++; return [1]; });
        Assert.Equal(ScheduleOutcome.Coalesced, scheduler.Enqueue(1, ScheduledLane.State, 9, 20, () => { calls++; return [2]; }));
        Assert.Equal((1, 20), scheduler.PendingState(1));
        Assert.Equal(0, calls);
        time += TimeSpan.FromMilliseconds(100);
        Assert.False(scheduler.TryAdmit(out _));
        Assert.Equal((0, 0), scheduler.PendingState(1));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PriorityAndPeerRoundRobinFairness()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        foreach (ulong peer in new ulong[] { 1, 2 })
        {
            scheduler.RegisterPeer(peer);
            scheduler.Enqueue(peer, ScheduledLane.Bulk, 1, 100, () => [1]);
            scheduler.Enqueue(peer, ScheduledLane.Control, 2, 100, () => [2]);
        }
        Assert.True(scheduler.TryAdmit(out var first));
        Assert.Equal(1UL, first!.Peer);
        Assert.Equal(ScheduledLane.Control, first.Lane);
        scheduler.CompleteSend(first);
        Assert.Throws<InvalidOperationException>(() => scheduler.CompleteSend(first));
        Assert.True(scheduler.TryAdmit(out var second));
        Assert.Equal(2UL, second!.Peer);
        Assert.Equal(ScheduledLane.Control, second.Lane);
        scheduler.CompleteSend(second);
    }

    [Fact]
    public void ExplicitOverflowAndBoundedNativeReservations()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        for (ulong key = 0; key < BandwidthScheduler.MaximumStateKeys; key++)
            Assert.Equal(ScheduleOutcome.Queued, scheduler.Enqueue(1, ScheduledLane.State, key, 1, () => [1]));
        Assert.Equal(ScheduleOutcome.ReduceSubscription, scheduler.Enqueue(1, ScheduledLane.State, 9999, 1, () => [1]));
        Assert.Equal((0, 0), scheduler.PendingState(1));
        for (ulong key = 0; key < 64; key++)
            scheduler.Enqueue(1, ScheduledLane.Control, key, 1, () => [1]);
        Assert.Equal(ScheduleOutcome.Busy, scheduler.Enqueue(1, ScheduledLane.Control, 100, 1, () => [1]));
        var sends = new List<ScheduledSend>();
        for (int i = 0; i < BandwidthScheduler.MaximumOutstandingSends; i++)
        {
            Assert.True(scheduler.TryAdmit(out var send));
            sends.Add(send!);
        }
        Assert.False(scheduler.TryAdmit(out _));
        foreach (var send in sends) scheduler.CompleteSend(send);
    }

    [Fact]
    public void PendingBulkOwnershipDoesNotBlockControl()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        for (ulong key = 1; key <= BandwidthScheduler.MaximumLaneOutstandingSends; key++)
        {
            scheduler.Enqueue(1, ScheduledLane.Bulk, key, 1000, () => new byte[1000]);
            Assert.True(scheduler.TryAdmit(out _));
        }
        scheduler.Enqueue(1, ScheduledLane.Control, 99, 1000, () => new byte[1000]);
        Assert.True(scheduler.TryAdmit(out var control));
        Assert.Equal(ScheduledLane.Control, control!.Lane);
        scheduler.CompleteSend(control);
    }

    [Fact]
    public void ReliableDescriptorOverflowHasExplicitOutcomes()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        foreach (var lane in new[] { ScheduledLane.Lifecycle, ScheduledLane.Bulk })
        {
            for (ulong key = 0; key < BandwidthScheduler.MaximumReliableQueuedItems; key++)
                Assert.Equal(ScheduleOutcome.Queued, scheduler.Enqueue(1, lane, key, 1, () => [1]));
            Assert.Equal(lane == ScheduledLane.Lifecycle ? ScheduleOutcome.CloseSlowPeer : ScheduleOutcome.Busy,
                scheduler.Enqueue(1, lane, 9999, 1, () => [1]));
        }
    }

    [Fact]
    public void ClockRollbackRejects()
    {
        TimeSpan time = TimeSpan.FromSeconds(1);
        var scheduler = new BandwidthScheduler(() => time);
        scheduler.RegisterPeer(1);
        time -= TimeSpan.FromMilliseconds(1);
        Assert.Throws<InvalidOperationException>(() => scheduler.TryAdmit(out _));
    }

    [Fact]
    public void MaximumAcceptedBulkItemFitsLaneBound()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        const int maximum = 64 * 1024;
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Enqueue(1, ScheduledLane.Bulk, 1, maximum + 1, () => []));
        Assert.Equal(ScheduleOutcome.Queued, scheduler.Enqueue(1, ScheduledLane.Bulk, 1, maximum, () => new byte[maximum]));
        Assert.True(scheduler.TryAdmit(out var bulk));
        Assert.Equal(maximum, bulk!.Payload.Length);
        scheduler.Enqueue(1, ScheduledLane.Control, 2, 1000, () => new byte[1000]);
        Assert.True(scheduler.TryAdmit(out _));
        scheduler.CompleteSend(bulk);
    }

    [Fact]
    public void DiscardPendingReleasesOldClosuresAndPreservesNativeCompletion()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        int oldEncoded = 0;
        scheduler.Enqueue(1, ScheduledLane.Bulk, 1, 1000, () => new byte[1000]);
        Assert.True(scheduler.TryAdmit(out var admitted));
        scheduler.Enqueue(1, ScheduledLane.State, 2, 1000, () => { oldEncoded++; return new byte[1000]; });
        scheduler.Enqueue(1, ScheduledLane.Lifecycle, 3, 1000, () => { oldEncoded++; return new byte[1000]; });
        scheduler.DiscardPending(1);
        Assert.Equal((0, 0), scheduler.PendingState(1));
        Assert.False(scheduler.TryAdmit(out _));
        scheduler.Enqueue(1, ScheduledLane.Control, 4, 1000, () => new byte[1000]);
        Assert.True(scheduler.TryAdmit(out var newer));
        Assert.Equal(4UL, newer!.Key);
        scheduler.CompleteSend(admitted!);
        scheduler.CompleteSend(newer);
        Assert.Equal(0, oldEncoded);
    }

    [Fact]
    public void CancellingStateKeyPreservesControlAndOtherState()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        scheduler.Enqueue(1, ScheduledLane.Control, 1, 10, () => [1]);
        scheduler.Enqueue(1, ScheduledLane.State, 1, 10, () => throw new InvalidOperationException("Canceled serializer ran."));
        scheduler.Enqueue(1, ScheduledLane.State, 2, 10, () => [2]);
        Assert.True(scheduler.CancelState(1, 1));
        Assert.False(scheduler.CancelState(1, 1));
        Assert.Equal((1, 10), scheduler.PendingState(1));
        Assert.True(scheduler.TryAdmit(out var control));
        Assert.Equal(ScheduledLane.Control, control!.Lane);
        Assert.True(scheduler.TryAdmit(out var state));
        Assert.Equal(2UL, state!.Key);
    }
}
