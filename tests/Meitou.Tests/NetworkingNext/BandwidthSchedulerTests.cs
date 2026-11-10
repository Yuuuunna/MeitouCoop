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
    public void PriorityFairnessAndActualWireDebtGateAdmission()
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
        scheduler.ObserveWireTotals(1, 1000, 100_000);
        scheduler.CompleteSend(first);
        Assert.Throws<InvalidOperationException>(() => scheduler.CompleteSend(first));
        Assert.True(scheduler.TryAdmit(out var second));
        Assert.Equal(2UL, second!.Peer);
        scheduler.CompleteSend(second);
        Assert.True(scheduler.TryAdmit(out _)); // inbound debt does not consume another peer budget
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
        Assert.True(scheduler.TryAdmit(out var send));
        Assert.False(scheduler.TryAdmit(out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.ObserveWireTotals(1, -1, 0));
        scheduler.CompleteSend(send!);
        Assert.True(scheduler.TryAdmit(out _));
    }
    [Fact]
    public void CounterResetRejectsAndUnusedEstimateNeverRefundsOrHidesFutureTraffic()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        scheduler.Enqueue(1, ScheduledLane.Control, 1, 1000, () => [1]);
        Assert.True(scheduler.TryAdmit(out var send));
        scheduler.CompleteSend(send!);
        scheduler.ObserveWireTotals(1, 1, 0);
        scheduler.ObserveWireTotals(1, 1, 0); // duplicate observation
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.ObserveWireTotals(1, 0, 0));
        scheduler.ObserveWireTotals(1, 1, 8000); // expired overestimate cannot cover new ingress
        scheduler.Enqueue(1, ScheduledLane.Control, 2, 1, () => [1]);
        Assert.False(scheduler.TryAdmit(out _));
    }

    [Fact]
    public void SustainedNonzeroServiceIsPacedWithBoundedInitialBurst()
    {
        TimeSpan time = TimeSpan.Zero;
        var scheduler = new BandwidthScheduler(() => time);
        scheduler.RegisterPeer(1);
        long bytes = 0;
        for (int step = 0; step < 1000; step++)
        {
            time = TimeSpan.FromMilliseconds(step * 10);
            scheduler.Enqueue(1, ScheduledLane.State, 1, 1000, () => new byte[1000]);
            if (!scheduler.TryAdmit(out var send)) continue;
            bytes += send!.ReservedWireBytes;
            scheduler.CompleteSend(send);
            scheduler.ObserveWireTotals(1, bytes, 0);
        }
        Assert.InRange(bytes, 300_000, 8_000 + 10 * 40_000);
    }

    [Fact]
    public void PendingBulkOwnershipDoesNotBlockControl()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        scheduler.Enqueue(1, ScheduledLane.Bulk, 1, 1000, () => new byte[1000]);
        Assert.True(scheduler.TryAdmit(out var bulk));
        scheduler.Enqueue(1, ScheduledLane.Control, 2, 1000, () => new byte[1000]);
        Assert.True(scheduler.TryAdmit(out var control));
        Assert.Equal(ScheduledLane.Control, control!.Lane);
        scheduler.CompleteSend(control);
        scheduler.CompleteSend(bulk!);
    }
    [Fact]
    public void GlobalSaturationResumesAtNextPeerAfterRefill()
    {
        TimeSpan time = TimeSpan.Zero;
        var scheduler = new BandwidthScheduler(() => time);
        for (ulong peer = 1; peer <= 100; peer++)
        {
            scheduler.RegisterPeer(peer);
            scheduler.Enqueue(peer, ScheduledLane.State, 1, 1000, () => new byte[1000]);
        }
        long bytes = 0;
        ulong lastPeer = 0;
        while (scheduler.TryAdmit(out var send))
        {
            Assert.Equal(lastPeer + 1, send!.Peer);
            lastPeer = send.Peer;
            bytes += send.ReservedWireBytes;
            scheduler.CompleteSend(send);
        }
        Assert.InRange(lastPeer, 40UL, 80UL);
        Assert.InRange(bytes, BandwidthScheduler.GlobalBurstBytes - 4000, BandwidthScheduler.GlobalBurstBytes);
        time += TimeSpan.FromMilliseconds(10);
        Assert.True(scheduler.TryAdmit(out var resumed));
        Assert.Equal(lastPeer + 1, resumed!.Peer);
        bytes += resumed.ReservedWireBytes;
        Assert.True(bytes <= BandwidthScheduler.GlobalBurstBytes + BandwidthScheduler.GlobalWireBytesPerSecond * 0.01);
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
    public void SustainedIngressAboveBudgetStopsStateAndClockRollbackRejects()
    {
        TimeSpan time = TimeSpan.Zero;
        var scheduler = new BandwidthScheduler(() => time);
        scheduler.RegisterPeer(1);
        for (int second = 1; second <= 5; second++)
        {
            time = TimeSpan.FromSeconds(second);
            scheduler.ObserveWireTotals(1, 0, second * 80_000L);
            scheduler.Enqueue(1, ScheduledLane.State, 1, 1000, () => new byte[1000]);
            Assert.False(scheduler.TryAdmit(out _));
        }
        time -= TimeSpan.FromMilliseconds(1);
        Assert.Throws<InvalidOperationException>(() => scheduler.TryAdmit(out _));
    }

    [Fact]
    public void RemovingPeerPreservesItsActualGlobalEgressDebt()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        scheduler.ObserveWireTotals(1, 100_000, 0);
        scheduler.RemovePeer(1);
        scheduler.RegisterPeer(2);
        scheduler.Enqueue(2, ScheduledLane.Control, 1, 1, () => [1]);
        Assert.False(scheduler.TryAdmit(out _));
    }

    [Fact]
    public void MaximumAcceptedBulkItemFitsBurstAndPriorityReserve()
    {
        var scheduler = new BandwidthScheduler(() => TimeSpan.Zero);
        scheduler.RegisterPeer(1);
        int maximum = BandwidthScheduler.PeerBurstBytes - BandwidthScheduler.PriorityReserveBytes - BandwidthScheduler.EstimatedOverheadBytes;
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Enqueue(1, ScheduledLane.Bulk, 1, maximum + 1, () => []));
        Assert.Equal(ScheduleOutcome.Queued, scheduler.Enqueue(1, ScheduledLane.Bulk, 1, maximum, () => new byte[maximum]));
        Assert.True(scheduler.TryAdmit(out var bulk));
        Assert.Equal(maximum, bulk!.Payload.Length);
        scheduler.Enqueue(1, ScheduledLane.Control, 2, 1000, () => new byte[1000]);
        Assert.True(scheduler.TryAdmit(out _));
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
