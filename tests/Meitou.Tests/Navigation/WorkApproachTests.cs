using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// Authoritative approaches stop on real navigation when a workplace's centre cannot be walked on.
/// </summary>
public class WorkApproachTests
{
    /// <summary>
    /// A loaded zone with a reachable floor ending before the requested workplace centre.
    /// </summary>
    static NavmeshWalkability Floor()
    {
        var mesh = new ZoneNavMesh
        {
            ZoneX = 32,
            ZoneZ = 32,
            Vertices = [new(0, 0, 0), new(100, 0, 0), new(100, 0, 100), new(0, 0, 100)],
            Polygons = [[0, 1, 2, 3]],
            Neighbours = [[-1, -1, -1, -1]],
            Areas = [NavArea.Ground],
            Kept = [true],
            BoundsMin = Vector2.Zero,
            BoundsMax = new(100),
        };
        var walk = new NavmeshWalkability((_, _) => 0) { AllowUnloadedFallback = false };
        walk.SetWorld(NavWorld.Empty.With(mesh));
        return walk;
    }

    /// <summary>
    /// An obstacle at the goal must not discard the complete approach or append an unverified walk into the obstacle.
    /// </summary>
    [Theory]
    [InlineData(160, true)]
    [InlineData(460, false)]
    public void LoadedWorkplaceKeepsItsReachableApproach(float goalX, bool goalSnaps)
    {
        var walk = Floor();
        var start = new Vector3(10, 0, 50);
        var goal = new Vector3(goalX, 0, 50);
        Assert.Equal(goalSnaps, new NavQuery(walk.World).FindPath(start, goal).Found);

        var path = walk.FindPath(start, goal);

        Assert.True(path.Found);
        Assert.False(path.WaitingForNavigation);
        Assert.False(path.IsProvisional);
        Assert.InRange(path.Points[^1].X, 90, 100);
        Assert.All(path.Points, point => Assert.InRange(point.X, 0, 100));
        Assert.DoesNotContain(goal, path.Points);
    }

    /// <summary>
    /// A genuinely unloaded destination still waits; only its verified prefix can be consumed by movement.
    /// </summary>
    [Fact]
    public void UnloadedDestinationStillWaitsForNavigation()
    {
        var walk = Floor();
        var goal = new Vector3(WorldLayout.ZoneSize + 100, 0, 50);

        var path = walk.FindPath(new Vector3(10, 0, 50), goal);

        Assert.True(path.WaitingForNavigation);
        Assert.All(path.Points.Take(Math.Min(path.Exact, path.Points.Count)), point => Assert.InRange(point.X, 0, 100));
    }
}
