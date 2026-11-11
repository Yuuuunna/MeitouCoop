using System.Numerics;
using Meitou.Game.Host;
using Meitou.Runtime;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Engine;

public class WorldRuntimeTests
{
    static OpenGroundWalkability Ground() => new((_, _) => 0);

    [Fact]
    public void DedicatedSchedulerRetainsWorkBeyondItsPerCallBudget()
    {
        var counter = new Counter();
        using var runtime = new WorldRuntime(new WorldSettings(), Ground(), [counter]);
        Assert.Equal(8, runtime.Advance(1));
        Assert.Equal(8, counter.Ticks);
        Assert.True(runtime.PendingGameSeconds > 0.7);
        Assert.Equal(8, runtime.Advance(0));
        Assert.Equal(8, runtime.Advance(0));
        Assert.Equal(6, runtime.Advance(0));
        Assert.Equal(30, counter.Ticks);
        Assert.Equal(30, runtime.World.Tick);
        Assert.InRange(runtime.PendingGameSeconds, 0, 1e-9);
    }

    [Fact]
    public void PauseDoesNotAccumulateOfflineTimeOrDrainExistingDebt()
    {
        using var runtime = new WorldRuntime(new WorldSettings(), Ground());
        runtime.Advance(1, 1);
        double owed = runtime.PendingGameSeconds;
        runtime.Speed = 0;
        Assert.Equal(0, runtime.Advance(600));
        Assert.Equal(owed, runtime.PendingGameSeconds);
        runtime.Speed = 2;
        Assert.Equal(29, runtime.Advance(0, 100));
        Assert.Equal(60, runtime.Advance(1, 100));
        Assert.Equal(90, runtime.World.Tick);
    }

    [Fact]
    public void FailedTickCannotBeContinuedUntilWorldReplacement()
    {
        var counter = new Counter { Throw = true };
        using var runtime = new WorldRuntime(new WorldSettings(), Ground(), [counter]);
        Assert.Throws<InvalidDataException>(() => runtime.Step());
        Assert.True(runtime.Faulted);
        Assert.Throws<InvalidOperationException>(() => runtime.Advance(1));
        var replacement = new Counter();
        runtime.ReplaceWorld([replacement]);
        Assert.Equal(1, counter.Disposals);
        Assert.False(runtime.Faulted);
        runtime.Step();
        Assert.Equal(1, replacement.Ticks);
    }

    [Fact]
    public void FailedLoadKeepsRunningWorldAndDisposesCandidateServices()
    {
        var original = new Counter();
        using var runtime = new WorldRuntime(new WorldSettings(), Ground(), [original]);
        var oldWorld = runtime.World;
        var candidate = new Counter();
        Assert.Throws<InvalidDataException>(() => runtime.ReplaceWorld([candidate], _ => throw new InvalidDataException("bad save")));
        Assert.Same(oldWorld, runtime.World);
        Assert.Equal(1, candidate.Disposals);
        Assert.Equal(0, original.Disposals);
        runtime.Step();
        Assert.Equal(1, original.Ticks);
    }

    [Fact]
    public void FailedReplacementCannotPublishDoorsIntoTheRunningWorldNavigation()
    {
        var ground = new DoorGround();
        using var runtime = new WorldRuntime(new WorldSettings(), ground);
        ground.SetDoorsBlocked("existing", true);
        Assert.Throws<InvalidDataException>(() => runtime.ReplaceWorld([], next =>
        {
            next.Fixtures.Doors?.SetDoorsBlocked("candidate", true);
            throw new InvalidDataException("bad save");
        }));
        Assert.Equal("existing", Assert.Single(ground.Blocked));
        Assert.Same(ground, runtime.World.Fixtures.Doors);
        runtime.ReplaceWorld([]);
        Assert.Empty(ground.Blocked);
        Assert.Same(ground, runtime.World.Fixtures.Doors);
    }

    [Fact]
    public void GraphicalSessionAndRuntimeExecuteTheSameTickPipeline()
    {
        var settings = new WorldSettings { StartHours = 37 };
        using var runtime = new WorldRuntime(settings, Ground());
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0,
            startHour: 13, simulation: settings);
        runtime.World.Characters.Spawn(new CharacterHot { Position = Vector3.One }, new CharacterCold(), 0);
        session.World.Characters.Spawn(new CharacterHot { Position = Vector3.One }, new CharacterCold(), 0);
        for (int i = 0; i < 15; i++)
        {
            runtime.Step();
            session.AdvanceSimulation(WorldRuntime.TickSeconds);
            Assert.Equal(runtime.World.StateHash(), session.World.StateHash());
            Assert.Equal(runtime.World.Daytime, session.World.Daytime);
        }
        session.BeginTickAhead();
        session.EndTickAhead();
        runtime.Step();
        session.AdvanceSimulation(WorldRuntime.TickSeconds);
        Assert.Equal(runtime.World.StateHash(), session.World.StateHash());
    }

    [Fact]
    public void DisposalContinuesAfterOneServiceFailsAndDoesNotRunTwice()
    {
        var bad = new Counter { ThrowOnDispose = true };
        var other = new Counter();
        var runtime = new WorldRuntime(new WorldSettings(), Ground(), [bad, other]);
        Assert.Throws<AggregateException>(() => runtime.Dispose());
        Assert.Equal(1, bad.Disposals);
        Assert.Equal(1, other.Disposals);
        runtime.Dispose();
        Assert.Equal(1, other.Disposals);
        Assert.Throws<ObjectDisposedException>(() => runtime.Step());
    }

    sealed class DoorGround : IWalkability, Meitou.Simulation.Locks.IDoorGate
    {
        public HashSet<string> Blocked { get; } = [];
        public float GroundHeight(float x, float z) => 0;
        public bool IsWalkable(float x, float z) => true;
        public PathResult FindPath(Vector3 from, Vector3 to) => new([from, to]);
        public void SetDoorsBlocked(string buildingInstance, bool blocked)
        {
            if (blocked) Blocked.Add(buildingInstance); else Blocked.Remove(buildingInstance);
        }
        public void ResetDoors() => Blocked.Clear();
    }

    sealed class Counter : ITickSystem, IDisposable
    {
        public int Ticks, Disposals;
        public bool Throw, ThrowOnDispose;
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands)
        {
            if (Throw) throw new InvalidDataException("failed tick");
            Ticks++;
        }
        public void Dispose()
        {
            Disposals++;
            if (ThrowOnDispose) throw new InvalidDataException("failed dispose");
        }
    }
}
