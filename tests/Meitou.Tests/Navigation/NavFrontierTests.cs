using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Player;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Navigation;

/// <summary>
/// Two disconnected regions can meet through an unloaded neighbour. A server retains that uncertainty without walking the unverified tail or
/// allowing a retired world's route request to erase a current loading request.
/// </summary>
public class NavFrontierTests
{
    /// <summary>
    /// The loaded zone and the missing neighbour that can connect both strips along its north border.
    /// </summary>
    static readonly ZoneCoordinate Zone = new(32, 32), Neighbour = new(32, 33);

    /// <summary>
    /// A point relative to a zone's south-west corner, on its synthetic ground.
    /// </summary>
    static Vector3 Point(ZoneCoordinate zone, float x, float z)
    {
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        return new((float)ox + x, 300, (float)oz + z);
    }

    /// <summary>
    /// Isolated rectangular regions with unlinked edges. Publication supplies the cross-zone links when the neighbour arrives.
    /// </summary>
    static ZoneNavMesh Mesh(ZoneCoordinate zone, params (float Left, float Bottom, float Right, float Top)[] rectangles)
    {
        var vertices = new List<Vector3>();
        var polygons = new List<int[]>();
        foreach (var r in rectangles)
        {
            int first = vertices.Count;
            vertices.AddRange([Point(zone, r.Left, r.Bottom), Point(zone, r.Right, r.Bottom),
                Point(zone, r.Right, r.Top), Point(zone, r.Left, r.Top)]);
            polygons.Add([first, first + 1, first + 2, first + 3]);
        }
        var origin = Point(zone, 0, 0);
        return new ZoneNavMesh
        {
            ZoneX = zone.X, ZoneZ = zone.Y, Vertices = [.. vertices], Polygons = [.. polygons],
            Neighbours = rectangles.Select(_ => new[] { -1, -1, -1, -1 }).ToArray(),
            Areas = rectangles.Select(_ => NavArea.Ground).ToArray(), Kept = rectangles.Select(_ => true).ToArray(),
            BoundsMin = new(origin.X, origin.Z), BoundsMax = new(origin.X + WorldLayout.ZoneSize, origin.Z + WorldLayout.ZoneSize),
        };
    }

    /// <summary>
    /// Separate strips whose goal region either reaches the missing neighbour or is closed inside the loaded zone.
    /// </summary>
    static ZoneNavMesh Strips(bool goalOpen = true) => Mesh(Zone,
        (100, WorldLayout.ZoneSize - 200, 300, WorldLayout.ZoneSize),
        (500, WorldLayout.ZoneSize - 200, 700, WorldLayout.ZoneSize - (goalOpen ? 0 : 50)));

    /// <summary>
    /// A real frontier requires streaming and open ground at both ends; a closed destination remains a proven failure.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void OnlyAnOpenStreamingFrontierDefersAnUnreachableDecision(bool streaming, bool goalOpen, bool waiting)
    {
        var nav = new NavmeshWalkability((_, _) => 300) { AllowUnloadedFallback = false, StreamsZones = streaming };
        nav.SetWorld(NavWorld.Empty.With(Strips(goalOpen)));
        var from = Point(Zone, 200, WorldLayout.ZoneSize - 100);
        var goal = Point(Zone, 600, WorldLayout.ZoneSize - 100);
        var path = nav.FindPath(from, goal);
        Assert.Equal(waiting, path.WaitingForNavigation);
        if (waiting)
        {
            Assert.True(path.IsProvisional);
            Assert.Equal(path.Points.Count - 1, path.Exact);
            Assert.Equal(goal, path.Points[^1]);
            Assert.Contains(Neighbour, nav.RouteZones(TimeSpan.FromMinutes(1)));
            Assert.True(nav.FindPath(from, goal).WaitingForNavigation);

            // Once the verified prefix is exhausted there is no partial corridor left, but the missing neighbour must still be requested.
            var atEnd = nav.FindPath(path.Points[path.Exact - 1], goal);
            Assert.True(atEnd.WaitingForNavigation);
            Assert.Equal(1, atEnd.Exact);
            Assert.Equal(goal, atEnd.Points[^1]);
            Assert.Contains(Neighbour, nav.RouteZones(TimeSpan.FromMinutes(1)));
        }
        else
        {
            Assert.False(path.Found);
            Assert.True(path.Unreachable);
        }
    }

    /// <summary>
    /// The actual navmesh result drives movement only to its verified prefix; publishing the bridge lets the retained order finish.
    /// </summary>
    [Fact]
    public void FrontierMovementWaitsAtItsVerifiedPrefixAndResumesAfterPublication()
    {
        var nav = new NavmeshWalkability((_, _) => 300) { AllowUnloadedFallback = false, StreamsZones = true };
        var loaded = NavWorld.Empty.With(Strips());
        nav.SetWorld(loaded);
        var from = Point(Zone, 200, WorldLayout.ZoneSize - 100);
        var goal = Point(Zone, 600, WorldLayout.ZoneSize - 100);
        using var movement = new MovementSystem(new PathService(nav, true));
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, nav, [new PlayerSystem(), movement]);
        world.Player.Faction = 2;
        var actor = world.Characters.Spawn(new CharacterHot
        {
            Position = from, MaxSpeed = 60, WalkSpeed = 60, Task = (byte)CharacterTask.Idle,
        }, new CharacterCold { Faction = 2, IsPlayer = true }, 0);
        world.Commands.Enqueue(new MoveOrder([actor], goal) { Tick = 0 });
        world.RunTicks(180);
        var cold = world.Characters.Cold(actor.Slot)!;
        Assert.True(cold.NavigationWaiting);
        Assert.False(cold.PathFailed);
        Assert.DoesNotContain(goal, cold.Path);
        Assert.True(world.Characters.Current[actor.Slot].Position.X <= Point(Zone, 302, 0).X);

        nav.SetWorld(loaded.With(Mesh(Neighbour, (100, 0, 700, 200))));
        world.InvalidateNavigation(new HashSet<ZoneCoordinate> { Zone, Neighbour });
        var ready = nav.FindPath(world.Characters.Current[actor.Slot].Position, goal);
        Assert.True(ready.Found);
        Assert.False(ready.WaitingForNavigation);
        world.RunTicks(900);
        Assert.False(cold.NavigationWaiting);
        Assert.False(cold.PathFailed);
        Assert.True(Vector3.Distance(world.Characters.Current[actor.Slot].Position, goal) < 3);
    }

    /// <summary>
    /// A query paused while sampling its old world incarnation completes after a new query, but must not overwrite the new loading hint.
    /// </summary>
    [Fact]
    public async Task AnOlderFrontierQueryCannotEraseTheCurrentWorldLoadingHint()
    {
        long incarnation = 0;
        int samples = 0;
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var nav = new NavmeshWalkability((_, _) => 300)
        {
            AllowUnloadedFallback = false, StreamsZones = true,
            NavigationIncarnation = () =>
            {
                long value = Volatile.Read(ref incarnation);
                if (Interlocked.Increment(ref samples) == 1)
                {
                    sampled.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                }
                return value;
            },
        };
        nav.SetWorld(NavWorld.Empty.With(Strips()));
        var from = Point(Zone, 200, WorldLayout.ZoneSize - 100);
        var goal = Point(Zone, 600, WorldLayout.ZoneSize - 100);
        var token = TestContext.Current.CancellationToken;
        var retired = Task.Run(() => nav.FindPath(from, goal), token);
        try
        {
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            Volatile.Write(ref incarnation, 1);
            nav.SetWorld(NavWorld.Empty.With(Strips()));
            Assert.True(nav.FindPath(from, goal).WaitingForNavigation);
            Assert.Contains(Neighbour, nav.RouteZones(TimeSpan.FromMinutes(1)));
            release.Set();
            await retired.WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.Contains(Neighbour, nav.RouteZones(TimeSpan.FromMinutes(1)));
        }
        finally { release.Set(); }
    }
}
