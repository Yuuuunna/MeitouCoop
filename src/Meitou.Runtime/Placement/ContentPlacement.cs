using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Construction;

namespace Meitou.Runtime.Placement;

/// <summary>CPU content adapters for authoritative placement. All collision loads complete before deciding a request.</summary>
internal sealed class ContentPlacement
{
    readonly GameDatabase db;
    readonly PopulationData population;
    readonly Func<float, float, float> ground;
    readonly BuildingFootprints footprints;
    readonly InteriorCollisions collisions;
    readonly ResourceField resources;
    readonly Lazy<(ResolvedBuilding Building, BuildingFootprint? Footprint)[]> levelBuildings;
    readonly Dictionary<string, MachineData?> machines = new(StringComparer.Ordinal);
    readonly Dictionary<(string Record, string Id, Vector3 Position, Quaternion Rotation), FloorSurfaces> floors = [];

    public ContentPlacement(GameInstall install, GameDatabase db, PopulationData population, WorldLevelData? levels,
        BuildingFootprints footprints, Func<float, float, float> ground, Func<Vector3, bool> ready)
    {
        this.db = db; this.population = population; this.ground = ground; this.footprints = footprints;
        collisions = new(install);
        resources = new(db, population.Biomes, population.ResourceNodes);
        levelBuildings = new(() => levels is null ? [] : levels.Buildings().Where(b => !b.TargetsZoneRecord)
            .Select(b => BuildingPlacements.Resolve(b, db, levels, (x, z) => ground((float)x, (float)z)))
            .Where(b => b is not null).Select(b => (b!.Value, footprints.Wait(b.Value.Record.StringId))).ToArray());
        Validator = new WorldPlacement(db, Services, ParentSurface, ready,
            faction => (uint)faction < (uint)population.Factions.Count ? population.Factions[faction].Id : "",
            () => population.Sites.Select(s => new PlacementTown(s.Town, new(s.Position.X, s.Position.Z))).ToArray());
    }

    public WorldPlacement Validator { get; }

    PlacementServices Services(World world) => new()
    {
        Ground = ground,
        Footprint = footprints.Require,
        StaticAround = (at, radius) => Static(world, at, radius),
        Readout = (id, position) =>
        {
            if (!machines.TryGetValue(id, out var machine)) machines[id] = machine = db.Find(id) is { } record ? MachineData.From(db, record) : null;
            return PlacementRules.Readout(machine, resources, position);
        },
    };

    IEnumerable<PlacedFootprint> Static(World world, Vector2 at, float radius)
    {
        foreach (var (building, footprint) in levelBuildings.Value)
        {
            if (world.Buildings.Find(building.Placement.Key) is not null) continue;
            if (footprint is null)
            {
                if (Vector2.Distance(new(building.Position.X, building.Position.Z), at) <= radius + 200) _ = footprints.Require(building.Record.StringId);
                continue;
            }
            if (footprint.Gap(building.Position, building.Placement.Rotation, at) > radius) continue;
            _ = footprints.Require(building.Record.StringId);
            yield return new(building.Placement.Key, footprint.Place(building.Position, building.Placement.Rotation));
        }
    }

    PlacementSurface? ParentSurface(World world, int faction, PlannedBuilding plan)
    {
        string id = plan.Parent!;
        GameRecord record;
        Vector3 position; Quaternion rotation;
        bool level;
        if (world.Buildings.Find(id) is { } site)
        {
            if (!site.Complete || site.Destroyed || site.Owner != faction || db.Find(site.Record) is not { } found) return null;
            record = found; position = site.Position; rotation = site.Rotation; level = false;
        }
        else
        {
            var parent = levelBuildings.Value.FirstOrDefault(b => b.Building.Placement.Key == id);
            if (parent.Building.Record is null || parent.Building.Destroyed || !world.Fixtures.OwnsBuilding(id, faction)) return null;
            record = parent.Building.Record; position = parent.Building.Position; rotation = parent.Building.Placement.Rotation; level = true;
        }
        var footprint = footprints.Require(record.StringId);
        if (footprint is null || footprint.Gap(position, rotation, new(plan.Position.X, plan.Position.Z)) > 0.1f) return null;
        var key = (record.StringId, id, position, rotation);
        if (!floors.TryGetValue(key, out var surface))
        {
            float scale = record.GetFloat("scale", 1);
            var node = WorldObjectLayout.InstanceTransform(position, rotation, new Vector3(scale > 0 ? scale : 1));
            floors[key] = surface = FloorSurfaces.For(db, record, id, position, rotation, node, collisions);
        }
        if (surface.HighestUpTo(plan.Position.X, plan.Position.Z, plan.Floor) is not { } height) return null;
        var feet = new Vector3(plan.Position.X, height, plan.Position.Z);
        int actualFloor = surface.FloorAt(feet);
        return new(height, new GhostFloor(id, footprint.Place(position, rotation), actualFloor, level ? id : null, Owned: true)
            { Ceiling = surface.CeilingAbove(feet.X, feet.Z, feet.Y + InteriorVolume.CoverClearance) });
    }
}
