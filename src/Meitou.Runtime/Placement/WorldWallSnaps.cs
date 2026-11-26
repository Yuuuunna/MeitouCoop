using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation;
using Meitou.Simulation.Construction;

namespace Meitou.Runtime.Placement;

/// <summary>
/// Resolves wall link points from current authority geometry. Submitted flags or positions alone never establish a snapped wall.
/// </summary>
static class WorldWallSnaps
{
    /// <summary>
    /// The preview's level-wall search reach; all live sites also contribute their link points.
    /// </summary>
    const float LevelReach = 300;

    /// <summary>
    /// The current link points at an exact requested joint. The cursor before snapping is not transmitted, so the authority verifies the node
    /// and direction rather than repeating a nearest-cursor search at the already snapped position.
    /// </summary>
    internal static IEnumerable<WallSnap> At(World world, PlacementServices services, Func<string, BuildingBuildData?> data, Vector3 point,
        int mask = 3)
    {
        var known = world.Buildings.All.Select(site => site.Id).ToHashSet(StringComparer.Ordinal);
        var walls = world.Buildings.All.Select(site => (Data: data(site.Record), site.Position, site.Rotation))
            .Concat(services.LinkedAround(new(point.X, point.Z), LevelReach)
                .Where(wall => !known.Contains(wall.Key)).Select(wall => (Data: data(wall.Record), wall.Position, wall.Rotation)))
            .Where(wall => wall.Data is { LinksTogether: true })
            .Select(wall => (wall.Data!, wall.Position, wall.Rotation));
        var found = new HashSet<WallSnap>();
        foreach (var link in WallLine.Links(walls))
        {
            var start = new WallSnap(link.Start, link.StartDirection, link.JoinsAtStart == 0 ? 2 : 3);
            var end = new WallSnap(link.End, link.EndDirection, link.JoinsAtEnd == 0 ? 1 : 3);
            if ((start.Code & mask) != 0 && Near(start.Point, point) && found.Add(start)) yield return start;
            if ((end.Code & mask) != 0 && Near(end.Point, point) && found.Add(end)) yield return end;
        }
    }

    /// <summary>
    /// Whether an ordinary wall ghost's complete transform matches snapping to an actual current link point.
    /// </summary>
    internal static bool MatchesGhost(World world, PlacementServices services, Func<string, BuildingBuildData?> data,
        BuildingBuildData building, PlannedBuilding plan)
    {
        if (building is not { LinksTogether: true, LinkType: 0 or 3 } || plan.Parent is not null || plan.Floor != 0 || plan.Outside)
            return false;

        var (start, end) = WallLine.Ends(building, plan.Position, plan.Rotation);
        foreach (var snap in At(world, services, data, start).Concat(At(world, services, data, end)))
        {
            var rotation = WallLine.LookRotation(snap.Direction);
            var position = snap.Code == 2 ? snap.Point - Vector3.Transform(new(0, 0, building.LinkLength), rotation) : snap.Point;
            if (Near(plan.Position, position) && SameTurn(plan.Rotation, rotation)) return true;
        }
        return false;
    }

    /// <summary>
    /// A small floating-point calculation tolerance, not a snap radius or network quantization.
    /// </summary>
    internal static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) <= 0.0001f;

    /// <summary>
    /// Equal rotations, including the two quaternion signs representing the same turn.
    /// </summary>
    internal static bool SameTurn(Quaternion a, Quaternion b) =>
        MathF.Min((a - b).LengthSquared(), (a + b).LengthSquared()) <= 0.00000001f;
}
