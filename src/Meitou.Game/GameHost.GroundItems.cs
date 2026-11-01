using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game;

/// <summary>
/// Loose items on the ground (docs/simulation.md "Ground items"): the snapshot's <see cref="WorldSnapshot.GroundItems"/> are diffed by id whenever
/// <see cref="WorldSnapshot.GroundItemsRevision"/> changes, each one a runtime placement of the world objects (its record's mesh, <c>WorldObjectLayout.Item</c>).
/// </summary>
sealed partial class GameHost
{
    long groundRevision = -1;
    readonly HashSet<int> shownGround = [];

    static string GroundId(int id) => "ground:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Once a frame with <see cref="UpdateBuildings"/>, after a new world's runtime placements were cleared.</summary>
    void UpdateGroundItems(WorldSnapshot snapshot)
    {
        if (session is Meitou.Engine.WorldSession local) local.World.GroundItems.Limit = config.GroundItemLimit;
        if (snapshot.GroundItemsRevision == groundRevision) return;
        groundRevision = snapshot.GroundItemsRevision;
        var current = snapshot.GroundItems.Select(g => g.Id).ToHashSet();
        foreach (int id in shownGround.Where(id => !current.Contains(id)).ToArray())
        {
            ActiveObjects?.RemoveRuntime(GroundId(id));
            shownGround.Remove(id);
            falls.RemoveAll(f => f.Ground.Id == id);
            restLimbs.Remove(id);
        }
        foreach (var g in snapshot.GroundItems)
        {
            if (!shownGround.Add(g.Id)) continue;
            var (rotation, y) = g.Dropped ? LaidFlat(g) : (g.Rotation, g.Position.Y);
            if (LimbLookOf(g.Record) is not null)
            {
                // A severed limb is drawn by the debris renderer (its SeveredLimb look: skin tone and blood), falling and at rest.
                if (g.Fall is { } cut && StartFall(g, cut, rotation, y)) continue;
                RestLimb(g, rotation, y);
                continue;
            }
            if (g.Fall is { } stump && StartFall(g, stump, rotation, y)) continue;   // shown once it has landed (UpdateFalls)
            ShowGround(g, rotation, y);
        }
    }

    /// <summary>The placement of a ground item: its record's mesh at its place, turned by <paramref name="rotation"/> and set at height <paramref name="y"/>.</summary>
    void ShowGround(GroundItemSnapshot g, Quaternion rotation, float y) =>
        ActiveObjects?.AddRuntime(new BuildingPlacement(WorldLayout.ZoneOf(g.Position.X, g.Position.Z), GroundId(g.Id), g.Record, g.Position, rotation, null, y) { Key = GroundId(g.Id) });

    readonly Dictionary<string, OgreBounds?> groundBounds = [];

    /// <summary>The bounds of a record's item mesh (its ground mesh, else its mesh), read once; null when it has none.</summary>
    OgreBounds? BoundsOf(string record)
    {
        if (groundBounds.TryGetValue(record, out var bounds)) return bounds;
        bounds = null;
        if (scene.Database?.Find(record) is { } item && assets.Find(WorldObjectLayout.ItemMesh(item)) is { } file)
        {
            try { bounds = OgreMeshReader.TryReadBounds(file, out var b) ? b : OgreMeshReader.ReadFile(file).Bounds; }
            catch (Exception e) when (e is IOException or OgreFormatException) { }
        }
        return groundBounds[record] = bounds;
    }

    /// <summary>
    /// A dropped item as it would come to rest (engine choice; the original lets a physics body settle it): its mesh's thinnest bounds axis turned upright, then its yaw, then tilted to the
    /// slope of the terrain under it, and set down so that no point of its bounds is below the ground at that point. A weapon (modelled along +Y) lies on the flat of its blade; a limb
    /// on a slope lies along it instead of hanging by its downhill end. Where the simulation's ground is not the terrain (a floor of a building) the ground is flat at the drop point.
    /// </summary>
    (Quaternion Rotation, float Y) LaidFlat(GroundItemSnapshot g)
    {
        var yaw = new Quaternion(0, g.Rotation.Y, 0, g.Rotation.W);
        if (BoundsOf(g.Record) is not { } box) return (yaw, g.Position.Y + 0.5f);
        var size = box.Max - box.Min;
        var flat = size.X <= size.Y && size.X <= size.Z ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)   // X up
            : size.Z < size.Y ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)                           // Z up
            : Quaternion.Identity;
        var rotation = Quaternion.Normalize(Quaternion.Concatenate(flat, yaw));
        var at = g.Position;
        // The terrain only where the drop point lies on it (a building's floor is flat for this).
        bool onTerrain = MathF.Abs(ActiveGround(at.X, at.Z) - at.Y) < 3;
        float Ground(float x, float z) => onTerrain ? ActiveGround(x, z) : at.Y;
        float reach = MathF.Max(MathF.Max(size.X, size.Z) * 0.5f, 1);
        var slope = Vector3.Normalize(new Vector3(Ground(at.X - reach, at.Z) - Ground(at.X + reach, at.Z), 2 * reach, Ground(at.X, at.Z - reach) - Ground(at.X, at.Z + reach)));
        float tilt = MathF.Acos(Math.Clamp(slope.Y, -1, 1));
        if (tilt > 1e-3f)
            rotation = Quaternion.Normalize(Quaternion.Concatenate(rotation, Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, slope)), tilt)));
        // Every point of a lattice over its bounds must be at or above the ground below it (a limb on a crest touches the crest, not only the dips round it).
        float y = float.MinValue;
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                for (int k = 0; k < 5; k++)
                {
                    var local = box.Min + size * new Vector3(i, j, k) / 4;
                    var w = Vector3.Transform(local, rotation);
                    y = MathF.Max(y, Ground(at.X + w.X, at.Z + w.Z) - w.Y);
                }
        return (rotation, y);
    }

    /// <summary>
    /// A severed limb drops from its stump (docs/simulation.md "Ground items", docs/formats/collision.md "Item physics"): <see cref="UpdateFalls"/> lets it fall from the
    /// stump's bone, sampled once its character is drawn, with gravity from rest, drifts it across to where the simulation put it and tips it flat on landing. Engine choice:
    /// the drift and the hanging pose are not the original's physics. False when the item has no mesh to fall with (it is then shown at rest).
    /// </summary>
    bool StartFall(GroundItemSnapshot g, FallSource stump, Quaternion lying, float y)
    {
        if (scene.Database?.Find(g.Record) is not { } record || WorldObjectLayout.ItemMesh(record) is not { Length: > 0 } mesh) return false;
        if (gpu.Debris?.Extent(mesh, null, LimbLookOf(g.Record)) is null) return false;   // the debris renderer draws only meshes it has loaded
        // Hanging: the mesh's long axis down, turned by the item's yaw (the same yaw the rest pose keeps).
        var yaw = new Quaternion(0, g.Rotation.Y, 0, g.Rotation.W);
        var hang = BoundsOf(g.Record) is { } box ? Turn(LongAxis(box.Max - box.Min), -Vector3.UnitY) : Quaternion.Identity;
        falls.Add(new FallingLimb
        {
            Ground = g, Mesh = mesh, Look = ToneOf(LimbLookOf(g.Record), g), Stump = stump, Rest = new Vector3(g.Position.X, y, g.Position.Z), RestY = y,
            Hanging = Quaternion.Normalize(Quaternion.Concatenate(hang, yaw)), Lying = lying,
        });
        return true;
    }

    /// <summary>The axis of a box's longest extent (a unit vector).</summary>
    static Vector3 LongAxis(Vector3 size) => size.X >= size.Y && size.X >= size.Z ? Vector3.UnitX : size.Y >= size.Z ? Vector3.UnitY : Vector3.UnitZ;

    void ForgetGroundItems()
    {
        shownGround.Clear();
        falls.Clear();
        restLimbs.Clear();
        groundRevision = -1;
    }
}
