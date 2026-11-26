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
        var links = world.Buildings.All.Select(site => (Data: data(site.Record), site.Position, site.Rotation))
            .Concat(services.LinkedAround(new(point.X, point.Z), LevelReach)
                .Where(wall => !known.Contains(wall.Key)).Select(wall => (Data: data(wall.Record), wall.Position, wall.Rotation)))
            .Where(wall => wall.Data is { LinksTogether: true })
            .Select(wall => WallLine.Link(wall.Data!, wall.Position, wall.Rotation)).ToArray();
        var candidates = new List<(int Wall, WallSnap Snap)>();
        for (int i = 0; i < links.Length; i++)
        {
            var link = links[i];
            if (Near(link.Start, point)) candidates.Add((i, new WallSnap(link.Start, link.StartDirection, 2)));
            if (Near(link.End, point)) candidates.Add((i, new WallSnap(link.End, link.EndDirection, 1)));
        }
        if (candidates.Count == 0) yield break;

        // Every candidate is within 0.01 of point, so two different candidate walls necessarily join. Otherwise at most the one wall's two
        // endpoints need a linear neighbor scan. This preserves the original's free/joined codes without an all-walls quadratic join graph.
        bool multipleWalls = candidates.Any(candidate => candidate.Wall != candidates[0].Wall);
        var found = new HashSet<WallSnap>();
        foreach (var (wall, candidate) in candidates)
        {
            bool joined = multipleWalls || links.Where((_, index) => index != wall).Any(other =>
                Vector3.DistanceSquared(candidate.Point, other.Start) < WallLine.JoinDistanceSquared
                || Vector3.DistanceSquared(candidate.Point, other.End) < WallLine.JoinDistanceSquared);
            var snap = candidate with { Code = joined ? 3 : candidate.Code };
            if ((snap.Code & mask) != 0 && found.Add(snap)) yield return snap;
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
