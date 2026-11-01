using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>A building's collision footprint on the ground plane (docs/game/buildings-production.md "Placement"): the convex hull of its collision shapes' X/Z, in the building's own space.</summary>
/// <param name="Hull">Counter-clockwise, scale applied, no rotation or position.</param>
public sealed record BuildingFootprint(Vector2[] Hull, float YMin, float YMax)
{
    /// <summary>The smallest box (X, Z) round <see cref="Hull"/>.</summary>
    public (Vector2 Min, Vector2 Max) Bounds
    {
        get
        {
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var p in Hull) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            return (min, max);
        }
    }

    /// <summary>The hull in the world for a building at <paramref name="position"/> turned by <paramref name="rotation"/> (only its effect on X and Z counts).</summary>
    public Vector2[] Place(Vector3 position, Quaternion rotation)
    {
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var result = new Vector2[Hull.Length];
        for (int i = 0; i < Hull.Length; i++)
        {
            var p = Vector3.Transform(new Vector3(Hull[i].X, 0, Hull[i].Y), q);
            result[i] = new Vector2(p.X + position.X, p.Z + position.Z);
        }
        return result;
    }

    /// <summary>The X/Z distance from <paramref name="point"/> to the hull of a building at <paramref name="position"/> turned by <paramref name="rotation"/>, 0 inside it (the hull is convex).</summary>
    public float Gap(Vector3 position, Quaternion rotation, Vector2 point)
    {
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var local3 = Vector3.Transform(new Vector3(point.X - position.X, 0, point.Y - position.Z), Quaternion.Conjugate(q));
        var p = new Vector2(local3.X, local3.Z);
        float nearest = float.MaxValue;
        int left = 0, right = 0;
        for (int i = 0; i < Hull.Length; i++)
        {
            var a = Hull[i];
            var e = Hull[(i + 1) % Hull.Length] - a;
            float cross = e.X * (p.Y - a.Y) - e.Y * (p.X - a.X);
            if (cross > 0) left++; else if (cross < 0) right++;
            float t = e.LengthSquared() > 1e-12f ? Math.Clamp(Vector2.Dot(p - a, e) / e.LengthSquared(), 0, 1) : 0;
            nearest = MathF.Min(nearest, Vector2.Distance(p, a + t * e));
        }
        return left == 0 || right == 0 ? 0 : nearest;
    }
}

/// <summary>A placed building's footprint in the world.</summary>
public readonly record struct PlacedFootprint(string Id, Vector2[] Hull);

/// <summary>A level-data building with the footprint of its record (in its own space), for picking; <paramref name="Plot"/> when it is the <see cref="BuildingFootprints.Plot"/> stand-in.</summary>
public readonly record struct PlacedBuilding(BuildingPlacement Placement, BuildingFootprint Footprint, bool Plot = false);

/// <summary>
/// Footprints of the BUILDING records (from their collision files, the way the navmesh gatherer places them) and of the level data's placed buildings, for the host's placement
/// check. A record is read on a worker the first time it is asked for; until then <see cref="Of"/> says null and the buildings that need it are left out.
/// </summary>
public sealed class BuildingFootprints
{
    readonly GameInstall install;
    readonly GameDatabase db;
    readonly CollisionCache collision;
    readonly Lazy<Dictionary<ZoneCoordinate, List<BuildingPlacement>>> index;
    readonly ConcurrentDictionary<string, Task<BuildingFootprint?>> byRecord = new(StringComparer.Ordinal);

    public BuildingFootprints(GameInstall install, GameDatabase db, WorldLevelData? levels)
    {
        this.install = install;
        this.db = db;
        collision = new CollisionCache(install);
        index = new(() =>
        {
            var map = new Dictionary<ZoneCoordinate, List<BuildingPlacement>>();
            if (levels is null) return map;
            foreach (var b in levels.Buildings())
            {
                if (!map.TryGetValue(b.Zone, out var list)) map[b.Zone] = list = [];
                list.Add(b);
            }
            return map;
        });
    }

    /// <summary>The footprint of a BUILDING; null while it is being read, and for a record with no collision.</summary>
    public BuildingFootprint? Of(string buildingId) => Request(buildingId) is { IsCompletedSuccessfully: true } done ? done.Result : null;

    /// <summary>Whether the footprint has been worked out (it may be none).</summary>
    public bool Ready(string buildingId) => Request(buildingId).IsCompleted;

    /// <summary>The footprint of a BUILDING, read now if it was not yet (null: no collision).</summary>
    public BuildingFootprint? Wait(string buildingId) => Request(buildingId).GetAwaiter().GetResult();

    readonly ConcurrentDictionary<string, Lazy<bool>> verifiedCollision = new(StringComparer.Ordinal);

    /// <summary>Authority must distinguish a record with no collision from a declared collision that could not be read.</summary>
    public BuildingFootprint? Require(string buildingId)
    {
        _ = verifiedCollision.GetOrAdd(buildingId, id => new Lazy<bool>(() =>
        {
            if (db.Find(id) is not { } building) throw new InvalidDataException($"Unknown building record: {id}");
            foreach (var part in WorldObjectLayout.Building(db, building, "placement", Vector3.Zero, Quaternion.Identity, new BuildingState()))
            {
                string path = part.Source.GetPath("xml collision");
                if (path.Length > 0 && collision.Get(path) is null) throw new InvalidDataException($"Required collision unavailable for building {id}: {path}");
            }
            return true;
        })).Value;
        return Wait(buildingId);
    }

    /// <summary>
    /// The placed footprints of the buildings that stand in a walker's way: the long-range grid's obstacles (<see cref="CoarseRoute"/>). Left out (engine choice): IGNORE
    /// <c>path mode</c>, gateways, destroyed ones, and WALKABLE ones walked across rather than round: features (Shield Bridge, Swamp Walkboard) and those whose top is
    /// below the agent height (pontoons, swamp platforms). WALKABLE walls and houses stay: you can stand on them, not walk through. Reads every record's footprint (on
    /// the pool) before it returns.
    /// </summary>
    public IReadOnlyList<Vector2[]> Obstacles(IEnumerable<BuildingPlacement> placements, WorldLevelData levels, Func<double, double, float> terrainHeight)
    {
        var placed = new List<ResolvedBuilding>();
        foreach (var b in placements)
        {
            if (b.TargetsZoneRecord || BuildingPlacements.Resolve(b, db, levels, terrainHeight) is not { Destroyed: false } r) continue;
            var mode = (PathMode)r.Record.GetInt("path mode", (int)PathMode.Obstacle);
            if (mode == PathMode.Ignore || r.Record.GetBool("is gateway") || mode == PathMode.Walkable && r.Record.GetBool("is feature")) continue;
            placed.Add(r);
            _ = Request(r.Record.StringId);
        }
        float agentHeight = new NavBuildSettings().AgentHeight;
        var result = new List<Vector2[]>(placed.Count);
        foreach (var r in placed)
            if (Wait(r.Record.StringId) is { } footprint && !((PathMode)r.Record.GetInt("path mode", (int)PathMode.Obstacle) == PathMode.Walkable && footprint.YMax < agentHeight))
                result.Add(footprint.Place(r.Position, r.Placement.Rotation));
        return result;
    }

    /// <summary>
    /// The triangles of a building record's collision shapes in its own space (scale applied, as the hull is): what a click on a building is tested against after its hull. Read from the
    /// cache on demand, not kept; empty without a database record or collision files.
    /// </summary>
    public IReadOnlyList<(Vector3[] Vertices, int[] Indices)> Mesh(string buildingId)
    {
        var result = new List<(Vector3[], int[])>();
        if (db.Find(buildingId) is not { } building) return result;
        float scale = building.GetFloat("scale", 1);
        if (scale <= 0) scale = 1;
        foreach (var mesh in WorldObjectLayout.Building(db, building, "footprint", Vector3.Zero, Quaternion.Identity, new BuildingState()))
        {
            string path = mesh.Source.GetPath("xml collision");
            if (path.Length == 0 || collision.Get(path) is not { } prepared) continue;
            foreach (var shape in prepared.Shapes) result.Add(([.. shape.Vertices.Select(v => v * scale)], shape.Indices));
        }
        return result;
    }

    readonly ConcurrentDictionary<string, BuildingFootprint?> plots = new(StringComparer.Ordinal);

    /// <summary>
    /// The pick stand-in of a BUILDING without collision (outdoor farms, tents, carpets: docs/formats/collision.md): the hull of its parts' render-mesh bounds boxes, scale and part
    /// placement applied. Only for picking: placement and the navmesh never see it. Null when the record has collision, or no part's bounds can be read.
    /// </summary>
    public BuildingFootprint? Plot(string buildingId) => plots.GetOrAdd(buildingId, id =>
    {
        if (Wait(id) is not null || db.Find(id) is not { } building) return null;
        var points = new List<Vector3>();
        foreach (var mesh in WorldObjectLayout.Building(db, building, "footprint", Vector3.Zero, Quaternion.Identity, new BuildingState()))
        {
            if (Meitou.Data.Physics.CollisionPaths.Resolve(install, mesh.MeshPath) is not { } path || !Meitou.Data.Ogre.OgreMeshReader.TryReadBounds(path, out var b)) continue;
            for (int corner = 0; corner < 8; corner++)
                points.Add(Vector3.Transform(new Vector3((corner & 1) == 0 ? b.Min.X : b.Max.X, (corner & 2) == 0 ? b.Min.Y : b.Max.Y, (corner & 4) == 0 ? b.Min.Z : b.Max.Z), mesh.Transform));
        }
        if (points.Count < 3) return null;
        var hull = NavGeometry.Footprint(points, 0, 0, 0).Polygon;
        return hull.Length < 3 ? null : new BuildingFootprint(hull, points.Min(p => p.Y), points.Max(p => p.Y));
    });

    /// <summary>Whether a BUILDING is a farm (it names a <c>farm data</c>): the level buildings without collision that a click still selects, by their <see cref="Plot"/>.</summary>
    bool IsFarm(string buildingId) => db.Find(buildingId)?.GetReferences("farm data") is [_, ..];

    Task<BuildingFootprint?> Request(string buildingId) => byRecord.GetOrAdd(buildingId, id => Task.Run(() => Compute(id)));

    BuildingFootprint? Compute(string buildingId)
    {
        if (db.Find(buildingId) is not { } building) return null;
        float scale = building.GetFloat("scale", 1);
        if (scale <= 0) scale = 1;
        var points = new List<Vector3>();
        foreach (var mesh in WorldObjectLayout.Building(db, building, "footprint", Vector3.Zero, Quaternion.Identity, new BuildingState()))
        {
            string path = mesh.Source.GetPath("xml collision");
            if (path.Length == 0 || collision.Get(path) is not { } prepared) continue;
            foreach (var shape in prepared.Shapes)
                foreach (var v in shape.Vertices) points.Add(v * scale);
        }
        if (points.Count < 3) return null;
        var hull = NavGeometry.Footprint(points, 0, 0, 0).Polygon;
        return hull.Length < 3 ? null : new BuildingFootprint(hull, points.Min(p => p.Y), points.Max(p => p.Y));
    }

    /// <summary>
    /// The level data's buildings whose placement lies within reach of the ray's ground track: the candidates a click picks from. Their footprints are read now (in parallel) if they
    /// were not yet, so the first click on a building picks it. A farm, which has no collision, comes with its <see cref="Plot"/>.
    /// </summary>
    public IEnumerable<PlacedBuilding> Along(Vector3 origin, Vector3 direction, float maxDistance)
    {
        var near = Near(origin, direction, maxDistance).ToList();
        foreach (var b in near) _ = Request(b.BuildingId);
        foreach (var b in near)
            if (Wait(b.BuildingId) is { } footprint) yield return new PlacedBuilding(b, footprint);
            else if (IsFarm(b.BuildingId) && Plot(b.BuildingId) is { } plot) yield return new PlacedBuilding(b, plot, Plot: true);
    }

    IEnumerable<BuildingPlacement> Near(Vector3 origin, Vector3 direction, float maxDistance)
    {
        var map = index.Value;
        const float Reach = 200;
        var from = new Vector2(origin.X, origin.Z);
        var track = new Vector2(direction.X, direction.Z) * maxDistance;
        float trackLength = track.Length();
        var zones = new HashSet<ZoneCoordinate>();
        int samples = (int)MathF.Ceiling(trackLength / (WorldLayout.ZoneSize / 2f)) + 1;
        for (int i = 0; i <= samples; i++)
        {
            var at = from + track * (i / (float)samples);
            var home = WorldLayout.ZoneOf(at.X, at.Y);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++) zones.Add(new ZoneCoordinate(home.X + dx, home.Y + dz));
        }
        float lengthSquared = track.LengthSquared();
        foreach (var zone in zones)
        {
            if (!map.TryGetValue(zone, out var list)) continue;
            foreach (var b in list)
            {
                if (b.Position == Vector3.Zero || b.TargetsZoneRecord) continue;
                var p = new Vector2(b.Position.X, b.Position.Z);
                float s = lengthSquared < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - from, track) / lengthSquared, 0, 1);
                if (Vector2.DistanceSquared(p, from + track * s) > Reach * Reach) continue;
                yield return b;
            }
        }
    }

    /// <summary>The footprints (those already read; the others are being read) of the level data's buildings with a placement within <paramref name="radius"/> of <paramref name="centre"/> (X, Z).</summary>
    public IEnumerable<PlacedFootprint> StaticAround(Vector2 centre, float radius)
    {
        var map = index.Value;
        const float Reach = 200;   // a building's own extent beyond its placement point
        var home = WorldLayout.ZoneOf(centre.X, centre.Y);
        int span = (int)MathF.Ceiling((radius + Reach) / WorldLayout.ZoneSize);
        for (int dz = -span; dz <= span; dz++)
            for (int dx = -span; dx <= span; dx++)
            {
                if (!map.TryGetValue(new ZoneCoordinate(home.X + dx, home.Y + dz), out var list)) continue;
                foreach (var b in list)
                {
                    if (b.Position == Vector3.Zero || Vector2.DistanceSquared(new Vector2(b.Position.X, b.Position.Z), centre) > (radius + Reach) * (radius + Reach)) continue;
                    if (Of(b.BuildingId) is { } footprint) yield return new PlacedFootprint(b.Key, footprint.Place(b.Position, b.Rotation));
                }
            }
    }
}
