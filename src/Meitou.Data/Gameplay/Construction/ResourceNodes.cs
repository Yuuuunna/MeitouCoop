using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;

namespace Meitou.Data.Gameplay.Construction;
/// <summary>
/// A mineable rock (docs/game/buildings-production.md "Resources"): where it stands, its <c>world resource mining</c>, its radius and its BUILDING
/// record; with its mesh's <paramref name = "Shape"/>, placed by <paramref name = "Transform"/>, a click hits the rock as drawn.
/// </summary>
public readonly record struct ResourceNode(Vector3 Position, int Resource, float Radius, string Record = "", Vector3? Centre = null, RockShape? Shape = null, Matrix4x4 Transform = default)
{
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
/// Generated rocks whose FOLIAGE_MESH names a BUILDING and level-placed resource BUILDINGs flagged <c>is foliage</c>. The building's functionality
/// supplies <c>world resource mining</c>; both sources share canonical positions, picking and production lookup. Zone results are cached until
/// level edits or foliage ownership/outpost inputs change. Thread-safe.
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
    /// The <c>world resource mining</c> of each rock BUILDING by string id (0: none), cached.
    /// </summary>
    readonly Dictionary<string, int> _resourceOf = new(StringComparer.Ordinal);

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
    /// The rocks of <paramref name="zone"/>, generated or directly placed in the level, sized by their meshes' bounds (at least
    /// <see cref="NodeRadius"/>).
    /// </summary>
    List<ResourceNode> Zone(ZoneCoordinate zone)
    {
        if (_zones.TryGetValue(zone, out var list)) return list;
        list = [];
        if (zone.IsInsideGrid)
        {
            foreach (var instance in world.Zone(zone).Instances)
                if (instance.Mesh.BuildingType is { } building && ResourceOf(building) is > 0 and var resource)
                {
                    // The rock's size is its mesh's bounds scaled by the instance (at least NodeRadius), centred on the bounds' middle, not on the
                    // origin the mesh sits on; it is picked by the mesh's triangles where the instance places them.
                    var bounds = Bounds(instance.Mesh.MeshPath);
                    float radius = bounds is { } b ? Math.Max(NodeRadius, b.Radius * instance.Scale) : NodeRadius;
                    list.Add(bounds is { } c
                        ? new ResourceNode(instance.Position, resource, radius, building.StringId,
                            Vector3.Transform(c.Centre, instance.Transform), c.Shape, instance.Transform)
                        : new ResourceNode(instance.Position, resource, radius, building.StringId));
                }
            foreach (var placed in world.ResourceBuildings(zone))
                if (ResourceOf(placed.Record) is > 0 and var resource
                    && !list.Any(n => n.Record == placed.Record.StringId && n.Position == placed.Position))
                    list.Add(LevelNode(placed, resource));
        }
        _zones[zone] = list;
        return list;
    }

    /// <summary>
    /// A level resource uses its rendered building parts for picking and working distance, with the stable placement origin as its natural-site id.
    /// </summary>
    ResourceNode LevelNode(ResolvedBuilding placed, int resource)
    {
        var corners = new List<Vector3>();
        var origin = Matrix4x4.CreateTranslation(-placed.Position);
        var meshes = WorldObjectLayout.Building(db, placed.Record, placed.Placement.InstanceId, placed.Position, placed.Placement.Rotation,
            new BuildingState { Identity = placed.Placement.Key });
        foreach (var mesh in meshes)
            if (Bounds(mesh.MeshPath) is { } bounds) bounds.Shape.AppendCorners(corners, mesh.Transform * origin);

        if (RockShape.Of(corners.ToArray()) is not { } shape)
            return new(placed.Position, resource, NodeRadius, placed.Record.StringId);

        var size = shape.Max - shape.Min;
        float radius = Math.Max(NodeRadius, Math.Max(size.X, Math.Max(size.Y, size.Z)) / 2);
        var transform = Matrix4x4.CreateTranslation(placed.Position);
        return new(placed.Position, resource, radius, placed.Record.StringId,
            Vector3.Transform((shape.Min + shape.Max) / 2, transform), shape, transform);
    }

    /// <summary>
    /// The <c>world resource mining</c> of <paramref name="building"/>'s first functionality (0 for none), cached.
    /// </summary>
    int ResourceOf(GameRecord building)
    {
        if (_resourceOf.TryGetValue(building.StringId, out int r)) return r;
        r = building.GetReferences("functionality") is [var link, ..] && db.Find(link.TargetStringId) is { } function
            ? function.GetInt("world resource mining")
            : 0;
        return _resourceOf[building.StringId] = r;
    }

    /// <summary>
    /// Disposes the foliage world the rocks are placed from.
    /// </summary>
    public void Dispose() => world.Dispose();
}
