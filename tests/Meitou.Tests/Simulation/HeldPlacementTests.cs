using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Locks;
namespace Meitou.Tests.Simulation;

public sealed class HeldPlacementTests
{
    [Fact]
    public void RestoredPrisonerFacingCorrectionPreservesTaskAndPathThroughTickInvariant()
    {
        using var world = new Meitou.Simulation.World(new WorldSettings(), new OpenGroundWalkability((_, _) => 150));
        world.Fixtures.RegisterSite(world, ArrestTests.Town(), 0);
        var cage = world.Fixtures.All.First(f => f.Kind == FixtureKind.Cage);
        var cold = new CharacterCold { InSomething = HeldState.Imprisoned, InWhat = cage.Id, Path = [Vector3.One, Vector3.One * 2] };
        var id = world.Characters.Spawn(new CharacterHot { Position = cage.Position + Vector3.One, Yaw = cage.Yaw + 1, Health = 100,
            Task = (byte)CharacterTask.Follow, TaskTime = 3, Flags = (ushort)MoveFlags.AnyPath, PathCursor = 1, Goal = new(12, 34) }, cold, 0);
        cage.Occupant = id;
        var table = world.Characters; var before = table.Current[id.Slot];
        table.BeginTick(); table.Phase = TablePhase.SlowWorld;
        world.Fixtures.HoldCaged(world);
        TaskStart.CheckStarts(world);
        var after = table.Next[id.Slot];
        Assert.Equal(cage.Position, after.Position); Assert.Equal(cage.Yaw, after.Yaw);
        Assert.Equal(before.Task, after.Task); Assert.Equal(before.TaskTime, after.TaskTime);
        Assert.Equal(before.Flags, after.Flags); Assert.Equal(before.PathCursor, after.PathCursor); Assert.Equal(before.Goal, after.Goal);
        Assert.Equal(new[] { Vector3.One, Vector3.One * 2 }, cold.Path);
        table.EndTick();
    }
}
