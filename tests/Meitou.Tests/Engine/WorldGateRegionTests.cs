using System.Numerics;
using Meitou.Simulation.Navigation;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Locks;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Engine;

public class WorldGateRegionTests
{
    sealed class Regions(bool split) : IGateRegions
    {
        public int CodeAt(Vector3 at) => split && at.X > 0 ? 1 : 0;
        public IReadOnlyList<GateLink> Gates => [];
    }
    sealed class Work
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<IGateRegions> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GateRegionBuild Input { get; }
        public Work() => Input = new(new object(), cancellation =>
        {
            Started.TrySetResult();
            try { return Finished.Task.WaitAsync(cancellation).GetAwaiter().GetResult(); }
            finally { Exited.TrySetResult(); }
        });
        public async Task Complete(IGateRegions regions)
        {
            await Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Finished.SetResult(regions);
            await Exited.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }
    static LockSystem Locks() => new(BaseGame, BodyOptions.Default);
    static OpenGroundWalkability Ground() => new((_, _) => 0);
    static bool Passes(LockSystem locks, Meitou.Simulation.World world) => locks.NotBlocked(world, -Vector3.UnitX, Vector3.UnitX);
    static async Task Publish(WorldRuntime runtime, LockSystem locks)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (locks.GateRegionsPending)
        {
            runtime.Synchronize();
            if (locks.GateRegionsPending) await Task.Delay(1, timeout.Token);
        }
    }

    [Fact]
    public async Task PausedWorldPublishesOnlyAtBoundariesAndInvalidatesLabelsBeforeRebuild()
    {
        var first = new Work(); var second = new Work(); var input = first.Input; var locks = Locks();
        using var runtime = new WorldRuntime(new WorldSettings(), Ground(), [locks, new WorldGateRegions(() => input)]);
        runtime.Speed = 0;
        Assert.True(locks.GateRegionsPending); Assert.False(Passes(locks, runtime.World));
        var open = new Regions(false); await first.Complete(open);
        Assert.Null(locks.Regions); Assert.True(locks.GateRegionsPending);
        await Publish(runtime, locks); Assert.Same(open, locks.Regions); Assert.True(Passes(locks, runtime.World)); Assert.Equal(0, runtime.World.Tick);
        input = second.Input; runtime.Synchronize();
        Assert.Null(locks.Regions); Assert.True(locks.GateRegionsPending); Assert.False(Passes(locks, runtime.World));
        var closed = new Regions(true); await second.Complete(closed); await Publish(runtime, locks);
        Assert.Same(closed, locks.Regions); Assert.False(Passes(locks, runtime.World)); Assert.Equal(0, runtime.World.Tick);
    }

    [Fact]
    public async Task CompletedObsoleteResultCannotReplaceNewGeometry()
    {
        var first = new Work(); var second = new Work(); var input = first.Input; var locks = Locks();
        using var runtime = new WorldRuntime(new WorldSettings(), Ground(), [locks, new WorldGateRegions(() => input)]);
        await first.Complete(new Regions(false)); input = second.Input;
        runtime.Synchronize(); Assert.Null(locks.Regions); Assert.False(Passes(locks, runtime.World));
        var closed = new Regions(true); await second.Complete(closed); await Publish(runtime, locks);
        Assert.Same(closed, locks.Regions); Assert.False(runtime.Faulted);
    }

    [Fact]
    public async Task ReplacingTheWorldCancelsOldWorkAndOnlyPublishesTheNewOwnersResult()
    {
        var first = new Work(); var oldLocks = Locks();
        using var runtime = new WorldRuntime(new WorldSettings(), Ground(), [oldLocks, new WorldGateRegions(() => first.Input)]);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = new Work(); var newLocks = Locks();
        runtime.ReplaceWorld([newLocks, new WorldGateRegions(() => second.Input)]);
        Assert.True(first.Exited.Task.IsCompleted); Assert.Null(newLocks.Regions); Assert.False(Passes(newLocks, runtime.World));
        var regions = new Regions(false); await second.Complete(regions); await Publish(runtime, newLocks);
        Assert.Same(regions, newLocks.Regions); Assert.Null(oldLocks.Regions);
    }

    [Fact]
    public async Task ActiveBuildFailureFaultsTheRuntimeInsteadOfPermittingUnclassifiedRoutes()
    {
        var work = new Work(); var locks = Locks();
        using var runtime = new WorldRuntime(new WorldSettings(), Ground(), [locks, new WorldGateRegions(() => work.Input)]);
        await work.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        work.Finished.SetException(new InvalidDataException("gate build failed"));
        await work.Exited.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => Publish(runtime, locks));
        Assert.True(runtime.Faulted); Assert.Null(locks.Regions); Assert.False(Passes(locks, runtime.World));
    }
}
