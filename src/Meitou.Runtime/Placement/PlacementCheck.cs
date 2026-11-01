using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Construction;

namespace Meitou.Runtime.Placement;

/// <summary>A ghost waiting for Confirm: what it is and where it stands, with its footprint in the world.</summary>
public sealed record QueuedGhost(BuildingBuildData Building, Vector3 Position, Quaternion Rotation, int Floor, Vector2[]? Hull, string? Parent = null);

/// <summary>
/// A ghost standing on a building's floor: the building (its placement id, a player site's own id), its indoor hull in the world, and the floor (0..3). A level building's
/// <paramref name="Level"/> is its instance id (what the placed building names as its parent) and <paramref name="Owned"/> whether the player's faction owns it.
/// </summary>
public sealed record GhostFloor(string Building, Vector2[] Footprint, int Floor, string? Level = null, bool Owned = false)
{
    /// <summary>The height of the building's ceiling over the floor point, null when nothing is over it (what a <c>ceiling placement</c> ghost hangs from).</summary>
    public float? Ceiling { get; init; }

    /// <summary>The parent the placed building records: the site's id, or the level building's instance id.</summary>
    public string Parent => Level ?? Building;
}

/// <summary>What the geometric checks read from the world: the terrain, the water, the footprints (<see cref="BuildingFootprints"/>) and the parts of a building.</summary>
public sealed class PlacementServices
{
    /// <summary>The ground height at (x, z).</summary>
    public required Func<float, float, float> Ground { get; init; }
    /// <summary>Water level (Observed 98, docs/game/buildings-production.md "Placement").</summary>
    public float WaterLevel { get; init; } = 98;
    /// <summary>A BUILDING's collision footprint; null while it is read, or when it has none.</summary>
    public required Func<string, BuildingFootprint?> Footprint { get; init; }
    /// <summary>The level data's buildings round a point (X, Z) within a radius, with their footprints in the world (those read so far).</summary>
    public required Func<Vector2, float, IEnumerable<PlacedFootprint>> StaticAround { get; init; }
    /// <summary>The level data's pickable buildings along a ray (origin, direction, length) with their record footprints; empty without level data.</summary>
    public Func<Vector3, Vector3, float, IEnumerable<PlacedBuilding>> StaticAlong { get; init; } = (_, _, _) => [];
    /// <summary>The collision triangles of a building record in its own space (a click on a building is tested against them); empty without level data.</summary>
    public Func<string, IReadOnlyList<(Vector3[] Vertices, int[] Indices)>> CollisionMesh { get; init; } = _ => [];
    /// <summary>A building without collision's pick stand-in (<see cref="BuildingFootprints.Plot"/>): a click on the ground inside it selects the building. Never a placement footprint.</summary>
    public Func<string, BuildingFootprint?> Plot { get; init; } = _ => null;
    /// <summary>Where a ray (origin, direction, length) first meets a loaded building's floor at or below the viewed floor, and that floor; null for none.</summary>
    public Func<Vector3, Vector3, float, (Vector3 Position, GhostFloor Floor)?> FloorAlong { get; init; } = (_, _, _) => null;
    /// <summary>What a BUILDING's ghost reads of the world's resources at a position (<see cref="PlacementRules.Readout"/>); null without them.</summary>
    public Func<string, Vector3, PlacementReadout?> Readout { get; init; } = (_, _) => null;
}

/// <summary>Everything one evaluation reads (the query is pure over it and the services).</summary>
/// <param name="Parts">The building's parts laid out at the origin with no rotation (<c>WorldObjectLayout.Building</c>), for the ground-type check.</param>
public sealed record PlacementQuery(BuildingBuildData Building, Vector3 Position, Quaternion Rotation, float Yaw, int Floor, IReadOnlyList<PlacedMesh> Parts,
    IReadOnlyList<QueuedGhost> Queued, IReadOnlyList<CharacterSnapshot> Characters, IReadOnlyList<BuildingSnapshot> Sites,
    IReadOnlyList<PlacementTown> Towns, string PlayerFactionId, PlacementServices Services)
{
    /// <summary>A piece of a wall line (docs/game/buildings-production.md "Walls"): its slope is its own pitch, not the ground under its box.</summary>
    public bool WallPiece { get; init; }
    /// <summary>Sites and queued ghosts (record, position, rotation) that never block this one: the walls at a snapped joint, connectors and wedges.</summary>
    public Func<string, Vector3, Quaternion, bool>? Ignore { get; init; }
    public Func<BuildingSnapshot, bool>? IgnoreSite { get; init; }
    /// <summary>The building floor the ghost stands on; null on the terrain.</summary>
    public GhostFloor? On { get; init; }
}

/// <summary>
/// The host's half of the placement checks (docs/simulation.md "Buildings", "Placement validation"; the messages and their order are docs/game/buildings-production.md
/// "Placement"): the ground, the footprints and the characters. It runs <see cref="PlacementRules.TownBlock"/> and the resource row first and returns the first failure in the
/// original's order. Not decided (so never failed): snap nodes, usage nodes and the floor number above floor 0; a ceiling ghost fails only without a ceiling. The ghost
/// stands on the terrain or on a building's floor (<see cref="PlacementQuery.On"/>), a ceiling ghost hangs under the ceiling over that floor.
/// </summary>
public static class PlacementCheck
{
    /// <summary>A person's radius for the characters-in-the-way test (engine choice; the original's node box test is <b>Observed</b> only).</summary>
    public const float CharacterRadius = 2.5f;
    /// <summary>The slope the original refuses outright (the ground's normal Y below 0.2).</summary>
    const float SteepestNormalY = 0.2f;

    public static PlacementFailure Evaluate(PlacementQuery q)
    {
        var b = q.Building;
        var at = new Vector2(q.Position.X, q.Position.Z);
        var town = q.On is { Owned: true } ? PlacementFailure.None : PlacementRules.TownBlock(b, at, q.Towns, q.PlayerFactionId);
        if (town != PlacementFailure.None) return town;

        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, q.Yaw);
        var footprint = q.Services.Footprint(b.Id);
        var hull = footprint?.Place(q.Position, turned);
        // On a floor: the building must be ours, a level building the player's faction owns or a finished site.
        string? parent = q.On?.Building;
        if (q.On is { Owned: false }) return PlacementFailure.NotOwnedBuilding;
        if (q.On is { Level: null } && q.Sites.FirstOrDefault(s => s.Id == parent) is null) return PlacementFailure.NotOwnedBuilding;
        // The resource row comes after the first row's "don't own" text and before "incomplete".
        var resources = PlacementRules.ResourceBlock(q.Services.Readout(b.Id, q.Position));
        if (resources != PlacementFailure.None) return resources;
        // A site of ours, or an owned town house being repaired (a level building that is also a site).
        if (q.On is not null && q.Sites.FirstOrDefault(s => s.Id == parent) is { Complete: false }) return PlacementFailure.InsideIncomplete;
        // A building under the ghost's centre (the one it stands in aside): another town's or the level's (not ours), or an unfinished site of ours.
        foreach (var site in q.Sites)
            if (site.Id != parent && !(q.IgnoreSite?.Invoke(site) ?? q.Ignore?.Invoke(site.Record, site.Position, site.Rotation) ?? false) && q.Services.Footprint(site.Record) is { } siteFootprint
                && Contains(siteFootprint.Place(site.Position, site.Rotation), at))
                return site.Complete ? PlacementFailure.TooCloseToBuilding : PlacementFailure.InsideIncomplete;
        foreach (var other in q.Services.StaticAround(at, 20))
            if (other.Id != parent && Contains(other.Hull, at)) return PlacementFailure.NotOwnedBuilding;

        // Interior-only furniture must stand on a building's floor; what may also stand outside (exterior furniture or a node) goes either way.
        if (parent is null && b.IsInteriorFurniture && !b.IsExteriorFurniture && !b.IsNode) return PlacementFailure.MustBeInside;

        if (hull is not null)
        {
            // Engine stand-in for the original's node boxes against the building's walls: on a floor the footprint stays within the building's indoor hull.
            if (q.On is { } on && hull.Any(p => Distance(on.Footprint, p) > 0)) return PlacementFailure.TooCloseToBuilding;
            float reach = hull.Max(p => Vector2.Distance(p, at)) + 150;
            foreach (var other in q.Services.StaticAround(at, reach))
                if (other.Id != parent && Overlap(hull, other.Hull)) return PlacementFailure.TooCloseToBuilding;
            foreach (var site in q.Sites)
                if (site.Id != parent && !(q.IgnoreSite?.Invoke(site) ?? q.Ignore?.Invoke(site.Record, site.Position, site.Rotation) ?? false) && q.Services.Footprint(site.Record) is { } f
                    && Overlap(hull, f.Place(site.Position, site.Rotation))) return PlacementFailure.TooCloseToBuilding;
            foreach (var queued in q.Queued)
                if (queued.Hull is not null && q.Ignore?.Invoke(queued.Building.Id, queued.Position, queued.Rotation) != true && Overlap(hull, queued.Hull))
                    return PlacementFailure.TooCloseToBuilding;
            foreach (var c in q.Characters)
            {
                if (c.Body is { Dead: true } || !c.CarriedBy.IsNone) continue;
                // Floor-separated actors do not occupy this ghost's collision height interval.
                if (footprint is not null && (c.Position.Y + CharacterRadius * 2 < q.Position.Y + footprint.YMin
                    || c.Position.Y > q.Position.Y + footprint.YMax)) continue;
                if (Distance(hull, new Vector2(c.Position.X, c.Position.Z)) < CharacterRadius) return PlacementFailure.CharactersInTheWay;
            }
        }

        // Parts that need land (BUILDING_PART `ground type` LAND, `affects footprint`): their node must not be below the water.
        var world = Matrix4x4.CreateFromQuaternion(Normalised(q.Rotation)) * Matrix4x4.CreateTranslation(q.Position);
        foreach (var part in q.Parts)
        {
            var source = part.Source;
            if (!source.GetBool("affects footprint") || source.GetInt("ground type") != 1) continue;
            float nodeY = Vector3.Transform(part.Transform.Translation, world).Y + source.GetFloat("footprint vertical");
            if (nodeY < q.Services.WaterLevel) return PlacementFailure.InvalidGround;
        }
        // A ceiling ghost finds no ceiling over it: the floor-number text.
        if (b.CeilingPlacement && q.On?.Ceiling is null) return PlacementFailure.InvalidFloor;

        // A wall piece pitches with the ground: its own up axis must be within `max slope` of the vertical (Verified, FUN_140429510).
        if (q.WallPiece)
            return b.MaxSlope > 0 && Vector3.Transform(Vector3.UnitY, Normalised(q.Rotation)).Y < MathF.Cos(b.MaxSlope * MathF.PI / 180) ? PlacementFailure.NotLevel : PlacementFailure.None;
        // A building's floor is taken as level (the original's one indoor ray and its 5 degree surface test are not read).
        if (parent is not null) return PlacementFailure.None;
        var bounds = q.Services.Footprint(b.Id)?.Bounds ?? (new Vector2(-4), new Vector2(4));
        float slope = SlopeDegrees(q.Services.Ground, at, q.Yaw, bounds, out float normalY);
        if (normalY < SteepestNormalY || b.MaxSlope > 0 && slope > b.MaxSlope) return PlacementFailure.NotLevel;
        return PlacementFailure.None;
    }

    /// <summary>
    /// The ground's slope under the footprint's box (engine reading of the original's four rays at the rotated bounding-box corners): the angle from the vertical of the plane
    /// through the four corner heights, and its normal's Y.
    /// </summary>
    public static float SlopeDegrees(Func<float, float, float> ground, Vector2 centre, float yaw, (Vector2 Min, Vector2 Max) box, out float normalY)
    {
        var corners = new Vector3[4];
        var local = new[] { new Vector2(box.Min.X, box.Min.Y), new Vector2(box.Max.X, box.Min.Y), new Vector2(box.Max.X, box.Max.Y), new Vector2(box.Min.X, box.Max.Y) };
        float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
        for (int i = 0; i < 4; i++)
        {
            // Yaw about +Y as Quaternion.CreateFromAxisAngle turns a point (x, z): x' = x cos + z sin, z' = -x sin + z cos.
            float x = local[i].X * cos + local[i].Y * sin + centre.X, z = -local[i].X * sin + local[i].Y * cos + centre.Y;
            corners[i] = new Vector3(x, ground(x, z), z);
        }
        var normal = Vector3.Cross(corners[2] - corners[0], corners[3] - corners[1]);
        if (normal.LengthSquared() < 1e-9f) { normalY = 1; return 0; }
        normal = Vector3.Normalize(normal);
        if (normal.Y < 0) normal = -normal;
        normalY = normal.Y;
        return MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * 180 / MathF.PI;
    }

    /// <summary>The ground normal at <paramref name="centre"/> from the box's corners (up when flat), for tilting a ghost to the ground.</summary>
    public static Vector3 GroundNormal(Func<float, float, float> ground, Vector2 centre, float yaw, (Vector2 Min, Vector2 Max) box)
    {
        var corners = new Vector3[4];
        var local = new[] { new Vector2(box.Min.X, box.Min.Y), new Vector2(box.Max.X, box.Min.Y), new Vector2(box.Max.X, box.Max.Y), new Vector2(box.Min.X, box.Max.Y) };
        float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
        for (int i = 0; i < 4; i++)
        {
            float x = local[i].X * cos + local[i].Y * sin + centre.X, z = -local[i].X * sin + local[i].Y * cos + centre.Y;
            corners[i] = new Vector3(x, ground(x, z), z);
        }
        var normal = Vector3.Cross(corners[2] - corners[0], corners[3] - corners[1]);
        if (normal.LengthSquared() < 1e-9f) return Vector3.UnitY;
        normal = Vector3.Normalize(normal);
        return normal.Y < 0 ? -normal : normal;
    }

    /// <summary>The text of a failure (<see cref="PlacementRules.Message"/>).</summary>
    public static string Message(PlacementFailure failure) => PlacementRules.Message(failure);

    // ---- plane geometry on convex polygons ----

    static Quaternion Normalised(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);

    /// <summary>The turn about +Y of a rotation (the angle <c>Quaternion.CreateFromAxisAngle(UnitY, a)</c> would give for its effect on a point's X and Z).</summary>
    public static float YawOf(Quaternion rotation)
    {
        var forward = Vector3.Transform(Vector3.UnitX, Normalised(rotation));
        // CreateFromAxisAngle(UnitY, a) takes (1, 0, 0) to (cos a, 0, -sin a).
        return MathF.Atan2(-forward.Z, forward.X);
    }

    static bool Contains(Vector2[] polygon, Vector2 point) => Distance(polygon, point) <= 0;

    /// <summary>The distance from a point to a convex polygon, 0 inside it (either winding).</summary>
    static float Distance(Vector2[] polygon, Vector2 point)
    {
        float area = 0;
        for (int i = 0; i < polygon.Length; i++) area += polygon[i].X * polygon[(i + 1) % polygon.Length].Y - polygon[(i + 1) % polygon.Length].X * polygon[i].Y;
        float sign = area >= 0 ? 1 : -1, outside = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var e = polygon[(i + 1) % polygon.Length] - a;
            float length = e.Length();
            if (length < 1e-6f) continue;
            outside = Math.Max(outside, -sign * (e.X * (point.Y - a.Y) - e.Y * (point.X - a.X)) / length);
        }
        return outside;
    }

    /// <summary>Whether two convex polygons overlap (the separating axis test on their edge normals).</summary>
    public static bool Overlap(Vector2[] a, Vector2[] b)
    {
        foreach (var polygon in (Vector2[][])[a, b])
            for (int i = 0; i < polygon.Length; i++)
            {
                var edge = polygon[(i + 1) % polygon.Length] - polygon[i];
                var axis = new Vector2(-edge.Y, edge.X);
                if (axis.LengthSquared() < 1e-9f) continue;
                Project(a, axis, out float aMin, out float aMax);
                Project(b, axis, out float bMin, out float bMax);
                if (aMax <= bMin || bMax <= aMin) return false;
            }
        return true;
    }

    static void Project(Vector2[] polygon, Vector2 axis, out float min, out float max)
    {
        min = float.MaxValue;
        max = float.MinValue;
        foreach (var p in polygon)
        {
            float d = Vector2.Dot(p, axis);
            min = Math.Min(min, d);
            max = Math.Max(max, d);
        }
    }
}
