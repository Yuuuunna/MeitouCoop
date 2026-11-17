using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.World;

namespace Meitou.Data.Gameplay.Construction;
/// <summary>
/// A rock, the building the foliage placer makes (docs/game/buildings-production.md "Resources"): where it stands, its <c>world resource
/// mining</c> (0: none), its radius and its BUILDING record; with its mesh's <paramref name="Shape"/>, placed by <paramref name="Transform"/>, a
/// click hits the rock as drawn.
/// </summary>
public readonly record struct ResourceNode(Vector3 Position, int Resource, float Radius, string Record = "", Vector3? Centre = null, RockShape? Shape = null, Matrix4x4 Transform = default)
{
    /// <summary>
    /// The <c>function</c> of the BUILDING's first functionality (-1: none): the pointer over the rock is <c>mine</c> for 1 and 27, else <c>use</c>
    /// (docs/game/ui-input.md, the machine classes' slot 130).
    /// </summary>
    public int Function { get; init; } = -1;

    /// <summary>
    /// Whether characters can operate the BUILDING (<see cref="MachineData.Operable"/>). Every rock BUILDING of the base game and Genesis is a mine
    /// (<b>Observed</b>, probe <c>record</c>); engine choice: a rock of another class is not picked, its own pointer and order not modelled.
    /// </summary>
    public bool Operable { get; init; }

    /// <summary>
    /// The ray distance to the rock (a unit <paramref name="direction"/>): its mesh's triangles, or without them a sphere of its radius round its
    /// centre; null: missed.
    /// </summary>
    public float? RayHit(Vector3 origin, Vector3 direction)
    {
        // The local ray is the world one mapped by the inverse transform, so its parameter is the world distance.
        if (Shape is { } shape && Matrix4x4.Invert(Transform, out var inverse))
            return shape.Hit(Vector3.Transform(origin, inverse), Vector3.TransformNormal(direction, inverse));
        var to = (Centre ?? Position) - origin;
        float along = Vector3.Dot(to, direction);
        float miss = to.LengthSquared() - along * along, r2 = Radius * Radius;
        if (miss > r2) return null;
        float t = along - MathF.Sqrt(r2 - miss);
        return t >= 0 ? t : null;
    }
}

/// <summary>
/// A rock mesh's triangles in mesh space (three corners each) and their bounds box.
/// </summary>
public sealed class RockShape(Vector3[] corners, Vector3 min, Vector3 max)
{
    /// <summary>
    /// The footprint hull, made on the first ask of <see cref="Outline"/>.
    /// </summary>
    Vector2[]? _outline;

    /// <summary>
    /// The low corner of the triangles' bounds box (mesh space).
    /// </summary>
    public Vector3 Min => min;

    /// <summary>
    /// The high corner of the triangles' bounds box (mesh space).
    /// </summary>
    public Vector3 Max => max;

    /// <summary>
    /// The ray parameter of the nearest triangle the ray meets (either side), null: none.
    /// </summary>
    public float? Hit(Vector3 origin, Vector3 direction)
    {
        if (!FloorSurfaces.RayBox(origin, direction, min, max, float.MaxValue)) return null;
        float? best = null;
        for (int i = 0; i + 2 < corners.Length; i += 3)
            if (FloorSurfaces.RayTriangle(origin, direction, corners[i], corners[i + 1], corners[i + 2]) is { } t && (best is null || t < best))
                best = t;
        return best;
    }

    /// <summary>
    /// The mesh's footprint: the convex hull of its corners' X/Z, counter-clockwise (made on first use).
    /// </summary>
    public Vector2[] Outline => _outline ??= Hull([.. corners.Select(c => new Vector2(c.X, c.Z))]);

    /// <summary>
    /// Whether the mesh-space point lies inside the footprint.
    /// </summary>
    public bool InsideXz(Vector2 p)
    {
        var hull = Outline;
        if (hull.Length < 3) return false;
        for (int i = 0; i < hull.Length; i++)
            if (Cross(hull[i], hull[(i + 1) % hull.Length], p) < 0) return false;
        return true;
    }

    /// <summary>
    /// The footprint's point nearest the mesh-space point: itself when inside.
    /// </summary>
    public Vector2 NearestXz(Vector2 p)
    {
        var hull = Outline;
        if (hull.Length == 0 || InsideXz(p)) return p;
        Vector2 best = hull[0];
        float bestDistance = float.MaxValue;
        for (int i = 0; i < hull.Length; i++)
        {
            Vector2 a = hull[i], b = hull[(i + 1) % hull.Length], ab = b - a;
            float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            var q = a + t * ab;
            float d = Vector2.DistanceSquared(p, q);
            if (d < bestDistance) (best, bestDistance) = (q, d);
        }
        return best;
    }

    /// <summary>
    /// The cross product of (a - o) and (b - o): positive when b lies left of the line o to a.
    /// </summary>
    static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>
    /// Andrew's monotone chain: the convex hull, counter-clockwise, without collinear points.
    /// </summary>
    static Vector2[] Hull(Vector2[] points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length < 3) return sorted;
        var hull = new Vector2[2 * sorted.Length];
        int k = 0;
        foreach (var p in sorted)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p) <= 0) k--;
            hull[k++] = p;
        }
        for (int i = sorted.Length - 2, lower = k + 1; i >= 0; i--)
        {
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], sorted[i]) <= 0) k--;
            hull[k++] = sorted[i];
        }
        return hull[..(k - 1)];
    }

    /// <summary>
    /// The triangle lists of <paramref name="mesh"/>'s submeshes; null when it has none.
    /// </summary>
    public static RockShape? Of(OgreMesh mesh)
    {
        return Of(mesh.TriangleCorners());
    }

    /// <summary>
    /// A shape from a triangle list; null for an empty list.
    /// </summary>
    internal static RockShape? Of(Vector3[] corners)
    {
        if (corners.Length == 0) return null;
        Vector3 min = corners[0], max = corners[0];
        foreach (var c in corners) (min, max) = (Vector3.Min(min, c), Vector3.Max(max, c));
        return new RockShape(corners, min, max);
    }

    /// <summary>
    /// Appends this mesh's triangles in the frame of a placed level building.
    /// </summary>
    internal void AppendCorners(List<Vector3> target, Matrix4x4 transform)
    {
        foreach (var corner in corners) target.Add(Vector3.Transform(corner, transform));
    }
}

/// <summary>
/// Where the world's mineable rocks are.
/// </summary>
public interface IResourceNodes
{
    /// <summary>
    /// The rocks of <paramref name="resource"/> (0: of any) within <paramref name="range"/> (horizontally) of a point.
    /// </summary>
    IReadOnlyList<ResourceNode> Near(int resource, float x, float z, float range);
}

/// <summary>
/// Generated rocks whose FOLIAGE_MESH has collision and a BUILDING, plus level-placed resource BUILDINGs flagged <c>is foliage</c>. Both sources
/// expose resource, function and operability with canonical positions. Cached zones invalidate on level edits or foliage ownership/outpost changes.
/// Thread-safe.
/// </summary>
public sealed class FoliageResourceNodes(FoliageWorld world, GameDatabase db, Func<string, string?>? findMesh = null) : IResourceNodes, IDisposable
{
    /// <summary>
    /// The smallest radius of a rock: the original takes the rock mesh's bounds and at least 25 (<c>uses_resources</c>); without the mesh file a rock
    /// is this.
    /// </summary>
    public const float NodeRadius = 25;

    /// <summary>
    /// Each rock mesh's bounds sphere and shape by path (ignoring case); null for a mesh that could not be read.
    /// </summary>
    readonly Dictionary<string, (Vector3 Centre, float Radius, RockShape Shape)?> _meshBounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rocks of each placed zone, kept.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, List<ResourceNode>> _zones = [];

    /// <summary>
    /// The level edit revision used for the cached zones.
    /// </summary>
    int _levelRevision = -1;

    /// <summary>
    /// Each rock BUILDING's resource, function and operability by string id (<see cref="What"/>), cached.
    /// </summary>
    readonly Dictionary<string, (int Resource, int Function, bool Operable)> _machines = new(StringComparer.Ordinal);

    /// <summary>
    /// The lock <see cref="Near"/> takes; the caches are read and filled under it.
    /// </summary>
    readonly Lock _gate = new();

    /// <summary>
    /// Replaces the authority's outpost placement inputs and invalidates resource zones placed with another town list.
    /// </summary>
    public void SetPlayerTowns(IReadOnlyList<FoliageTown> towns)
    {
        lock (_gate)
        {
            if (world.ExtraTowns.SequenceEqual(towns)) return;

            world.ExtraTowns = towns.ToArray();
            _zones.Clear();
        }
    }

    /// <summary>
    /// A rock mesh's shape and its bounds sphere (the triangles' box middle, and its longest half-extent as the radius, before scaling); null without
    /// <c>findMesh</c>, the file or triangles.
    /// </summary>
    (Vector3 Centre, float Radius, RockShape Shape)? Bounds(string meshPath)
    {
        if (findMesh is null) return null;
        if (_meshBounds.TryGetValue(meshPath, out var known)) return known;
        RockShape? shape = null;
        if ((findMesh(meshPath) ?? findMesh(Path.GetFileName(meshPath.Replace('\\', '/')))) is { } path)
        {
            try
            {
                shape = RockShape.Of(OgreMeshReader.ReadFile(path));
            }
            catch (Exception e) when (e is IOException or OgreFormatException or NotSupportedException)
            {
            }
        }
        if (shape is null) return _meshBounds[meshPath] = null;
        // The longest half-extent of the bounds box, not its half-diagonal (a rock of 71 x 72 x 66 is 36 across, not 60: the diagonal made the radius
        // nearly twice the rock).
        var size = shape.Max - shape.Min;
        return _meshBounds[meshPath] = ((shape.Min + shape.Max) / 2, Math.Max(size.X, Math.Max(size.Y, size.Z)) / 2, shape);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ResourceNode> Near(int resource, float x, float z, float range)
    {
        var found = new List<ResourceNode>();
        lock (_gate)
        {
            if (world.RefreshOwnership() || _levelRevision != world.LevelRevision)
            {
                _zones.Clear();
                _levelRevision = world.LevelRevision;
            }
            var first = WorldLayout.ZoneOf(x - range, z - range);
            var last = WorldLayout.ZoneOf(x + range, z + range);
            for (int zx = first.X; zx <= last.X; zx++)
                for (int zy = first.Y; zy <= last.Y; zy++)
                    foreach (var node in Zone(new ZoneCoordinate(zx, zy)))
                        if ((resource == 0 || node.Resource == resource) && Vector2.DistanceSquared(new(node.Position.X, node.Position.Z), new(x, z)) <= range * range)
                            found.Add(node);
        }

        return found;
    }

    /// <summary>
    /// The generated and directly placed rocks of <paramref name="zone"/>, sized by their meshes' bounds (at least <see cref="NodeRadius"/>).
    /// </summary>
    List<ResourceNode> Zone(ZoneCoordinate zone)
    {
        if (_zones.TryGetValue(zone, out var list)) return list;

        list = [];
        if (zone.IsInsideGrid)
        {
            foreach (var instance in world.Zone(zone).Instances)
                if (RockBuilding(instance.Mesh) is { } building) list.Add(Rock(instance, building));
            foreach (var placed in world.ResourceBuildings(zone))
                if (!list.Any(n => n.Record == placed.Record.StringId && n.Position == placed.Position))
                    list.Add(LevelNode(placed));
        }

        _zones[zone] = list;
        return list;
    }

    /// <summary>
    /// A level resource uses its rendered building parts for picking and working distance, with the stable placement origin as its natural-site id.
    /// </summary>
    ResourceNode LevelNode(ResolvedBuilding placed)
    {
        var (resource, function, operable) = What(placed.Record);
        var corners = new List<Vector3>();
        var origin = Matrix4x4.CreateTranslation(-placed.Position);
        var meshes = WorldObjectLayout.Building(db, placed.Record, placed.Placement.InstanceId, placed.Position, placed.Placement.Rotation,
            new BuildingState { Identity = placed.Placement.Key });
        foreach (var mesh in meshes)
            if (Bounds(mesh.MeshPath) is { } bounds) bounds.Shape.AppendCorners(corners, mesh.Transform * origin);

        if (RockShape.Of(corners.ToArray()) is not { } shape)
            return new(placed.Position, resource, NodeRadius, placed.Record.StringId) { Function = function, Operable = operable };

        var size = shape.Max - shape.Min;
        float radius = Math.Max(NodeRadius, Math.Max(size.X, Math.Max(size.Y, size.Z)) / 2);
        var transform = Matrix4x4.CreateTranslation(placed.Position);
        return new(placed.Position, resource, radius, placed.Record.StringId,
            Vector3.Transform((shape.Min + shape.Max) / 2, transform), shape, transform) { Function = function, Operable = operable };
    }

    /// <summary>
    /// The BUILDING the placer makes for a copy of <paramref name="mesh"/> (<b>Verified</b>, <c>FUN_1406d2410</c>): the first entry of its
    /// <c>building type</c> list, only when the mesh has a <c>collision</c> file (without one the original logs "has no physics file" and makes
    /// none). Neither the placer nor the factory (<c>FUN_14057cc70</c>) reads the functionality, so a rock whose mine needs no resource (Godsend's
    /// Resources sets <c>Ore mine</c> and <c>copper mine</c> to 0, #852) is still a rock, of yield 1.
    /// </summary>
    GameRecord? RockBuilding(FoliageMesh mesh)
    {
        if (mesh.Record.GetReferences("building type") is not [var first, ..] || mesh.Record.GetPath("collision").Length == 0) return null;

        return db.Find(first.TargetStringId) is { Type: FcsRecordType.Building } building ? building : null;
    }

    /// <summary>
    /// The rock of <paramref name="instance"/>, sized by its mesh's bounds scaled by the instance (at least <see cref="NodeRadius"/>), centred on the
    /// bounds' middle, not on the origin the mesh sits on; it is picked by the mesh's triangles where the instance places them.
    /// </summary>
    ResourceNode Rock(FoliageInstance instance, GameRecord building)
    {
        var (resource, function, operable) = What(building);
        var bounds = Bounds(instance.Mesh.MeshPath);
        float radius = bounds is { } b ? Math.Max(NodeRadius, b.Radius * instance.Scale) : NodeRadius;
        var rock = bounds is { } c
            ? new ResourceNode(instance.Position, resource, radius, building.StringId, Vector3.Transform(c.Centre, instance.Transform), c.Shape,
                instance.Transform)
            : new ResourceNode(instance.Position, resource, radius, building.StringId);

        return rock with { Function = function, Operable = operable };
    }

    /// <summary>
    /// <paramref name="building"/>'s machine as a rock needs it, cached: its first functionality's <c>world resource mining</c> (0: none) and
    /// <c>function</c> (-1: none), and whether characters can operate it.
    /// </summary>
    (int Resource, int Function, bool Operable) What(GameRecord building)
    {
        if (_machines.TryGetValue(building.StringId, out var known)) return known;

        var data = MachineData.From(db, building);
        var what = (data?.Resource ?? 0, data?.Function ?? -1, data is { Operable: true });
        return _machines[building.StringId] = what;
    }

    /// <summary>
    /// Disposes the foliage world the rocks are placed from.
    /// </summary>
    public void Dispose() => world.Dispose();
}
