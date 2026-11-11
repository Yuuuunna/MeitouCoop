using Meitou.Simulation.Player;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class NavigationWaitingTests
{
    sealed class WaitingGround(bool prefix) : IWalkability
    {
        public bool Ready;
        public float GroundHeight(float x, float z) => 300;
        public bool IsWalkable(float x, float z) => true;
        public PathResult FindPath(Vector3 from, Vector3 to) => Ready ? new([from, to])
            : prefix ? new([from, new(20, 300, 0), to]) { Exact = 2, WaitingForNavigation = true }
            : new([]) { WaitingForNavigation = true };
    }
    static CharacterId Actor(SimWorld world) => world.Characters.Spawn(new CharacterHot
        { Position = new(0, 300, 0), MaxSpeed = 60, WalkSpeed = 30, Task = (byte)CharacterTask.Idle }, new CharacterCold { Faction = 2, IsPlayer = true }, 0);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WaitingRetainsTheOrderAndNeverWalksBeyondItsVerifiedPrefix(bool prefix)
    {
        var ground = new WaitingGround(prefix);
        using var movement = new MovementSystem(new PathService(ground, true));
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, ground, [new PlayerSystem(), movement]);
        world.Player.Faction = 2; var actor = Actor(world);
        world.Commands.Enqueue(new MoveOrder([actor], new(80, 300, 0)) { Tick = 0 });
        world.RunTicks(120);
        var cold = world.Characters.Cold(actor.Slot)!;
        Assert.True(cold.NavigationWaiting); Assert.False(cold.PathFailed);
        Assert.Equal((byte)CharacterTask.GoTo, world.Characters.Previous[actor.Slot].Task);
        Assert.InRange(world.Characters.Previous[actor.Slot].Position.X, 0, prefix ? 20.1f : 0);
        Assert.True(movement.Paths.Submitted < 10);
        ground.Ready = true;
        world.InvalidateNavigation(new HashSet<ZoneCoordinate> { WorldLayout.ZoneOf(0, 0) });
        world.RunTicks(240);
        Assert.False(cold.NavigationWaiting); Assert.False(cold.PathFailed);
        Assert.InRange(world.Characters.Previous[actor.Slot].Position.X, 78, 81);
    }

    [Fact]
    public void StrictNavigationDoesNotTreatAnUnloadedZoneAsOpenGroundOrDropAnActorsHeight()
    {
        var nav = new NavmeshWalkability((_, _) => 0) { AllowUnloadedFallback = false };
        Assert.False(nav.IsWalkable(0, 0));
        Assert.False(nav.TryStand(0, 0, 60, out _));
        Assert.Equal(60, nav.GroundHeight(0, 0, 60));
    }

    [Fact]
    public void RouteLoadingHintsFromThePreviousWorldAreIgnored()
    {
        long incarnation = 0;
        var nav = new NavmeshWalkability((_, _) => 0) { AllowUnloadedFallback = false, NavigationIncarnation = () => incarnation };
        Assert.True(nav.FindPath(new(10, 0, 10), new(300, 0, 300)).WaitingForNavigation);
        Assert.NotEmpty(nav.RouteZones(TimeSpan.FromMinutes(1)));
        incarnation++;
        Assert.Empty(nav.RouteZones(TimeSpan.FromMinutes(1)));
    }

    sealed class DelayedGround : IWalkability, IDisposable
    {
        public readonly TaskCompletionSource First = new(TaskCreationOptions.RunContinuationsAsynchronously), Second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim ReleaseFirst = new(), ReleaseSecond = new();
        int _calls;
        public float GroundHeight(float x, float z) => 300;
        public bool IsWalkable(float x, float z) => true;
        public PathResult FindPath(Vector3 from, Vector3 to)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call == 1) { First.TrySetResult(); if (!ReleaseFirst.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); return new([from, to]); }
            if (call == 2) { Second.TrySetResult(); if (!ReleaseSecond.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); }
            return new([from, new(0, 300, 50), to]);
        }
        public void Dispose() { ReleaseFirst.Set(); ReleaseSecond.Set(); }
    }

    [Fact]
    public async Task APathCalculatedBeforeNavigationInvalidationCannotBeAcceptedAsAnOlderUsefulAnswer()
    {
        using var ground = new DelayedGround();
        using var movement = new MovementSystem(new PathService(ground, false));
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, ground, [new PlayerSystem(), movement]);
        world.Player.Faction = 2; var actor = Actor(world);
        var token = TestContext.Current.CancellationToken;
        try
        {
            world.Commands.Enqueue(new MoveOrder([actor], new(100, 300, 0)) { Tick = 0 }); world.RunTick();
            await ground.First.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            world.InvalidateNavigation(new HashSet<ZoneCoordinate> { WorldLayout.ZoneOf(0, 0) }); world.RunTick();
            await ground.Second.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            ground.ReleaseFirst.Set();
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => movement.Paths.Pending == 1, TimeSpan.FromSeconds(15)), token));
            world.RunTick(); Assert.Empty(world.Characters.Cold(actor.Slot)!.Path);
            ground.ReleaseSecond.Set();
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => movement.Paths.Pending == 0, TimeSpan.FromSeconds(15)), token));
            world.RunTick(); Assert.Contains(new Vector3(0, 300, 50), world.Characters.Cold(actor.Slot)!.Path);
        }
        finally { ground.ReleaseFirst.Set(); ground.ReleaseSecond.Set(); }
    }
}
