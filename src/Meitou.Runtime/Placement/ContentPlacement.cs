using Meitou.Simulation.Population;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Construction;

namespace Meitou.Runtime.Placement;

/// <summary>
/// CPU content adapters for authoritative placement. All collision loads complete before deciding a request.
/// </summary>
internal sealed class ContentPlacement
{
    readonly GameDatabase _db;
    readonly PopulationData _population;
    readonly Func<float, float, float> _ground;
    readonly BuildingFootprints _footprints;
    readonly InteriorCollisions _collisions;
    readonly ResourceField _resources;
    readonly Lazy<(ResolvedBuilding Building, BuildingFootprint? Footprint)[]> _levelBuildings;
    readonly Dictionary<string, MachineData?> _machines = new(StringComparer.Ordinal);
    readonly Dictionary<(string Record, string Id, Vector3 Position, Quaternion Rotation), FloorSurfaces> _floors = [];
    public ContentPlacement(GameInstall install, GameDatabase db, PopulationData population, WorldLevelData? levels, BuildingFootprints footprints, Func<float, float, float> ground, Func<Vector3, bool> ready)
    {
        this._db = db;
        this._population = population;
        this._ground = ground;
        this._footprints = footprints;
        _collisions = new(install);
        _resources = new(db, population.Biomes, population.ResourceNodes);
        _levelBuildings = new(() => levels is null ? [] : levels.Buildings().Where(b => !b.TargetsZoneRecord).Select(b => BuildingPlacements.Resolve(b, db, levels, (x, z) => ground((float)x, (float)z))).Where(b => b is not null).Select(b => (b!.Value, footprints.Wait(b.Value.Record.StringId))).ToArray());
        Validator = new WorldPlacement(db, Services, ParentSurface, ready, faction => (uint)faction < (uint)population.Factions.Count ? population.Factions[faction].Id : "", () => population.Sites.Select(s => new PlacementTown(s.Town, new(s.Position.X, s.Position.Z))).ToArray());
    }

    public WorldPlacement Validator { get; }

    PlacementServices Services(World world) => new()
    {
        Ground = _ground,
        Footprint = _footprints.Require,
        PartBounds = _footprints.PartBounds,
        StaticAround = (at, radius) => Static(world, at, radius),
        Readout = (id, position) =>
        {
            if (!_machines.TryGetValue(id, out var machine))
                _machines[id] = machine = _db.Find(id)is { } record ? MachineData.From(_db, record) : null;
            return PlacementRules.Readout(machine, _resources, position);
        },
    };
    IEnumerable<PlacedFootprint> Static(World world, Vector2 at, float radius)
    {
        foreach (var(building, footprint)in _levelBuildings.Value)
        {
            if (world.Buildings.Find(building.Placement.Key)is not null)
                continue;
            if (footprint is null)
            {
                if (Vector2.Distance(new(building.Position.X, building.Position.Z), at) <= radius + 200)
                    _ = _footprints.Require(building.Record.StringId);
                continue;
            }

            if (footprint.Gap(building.Position, building.Placement.Rotation, at) > radius)
                continue;
            _ = _footprints.Require(building.Record.StringId);
            yield return new(building.Placement.Key, footprint.Place(building.Position, building.Placement.Rotation));
        }
    }

    PlacementSurface? ParentSurface(World world, int faction, PlannedBuilding plan)
    {
        string id = plan.Parent!;
        GameRecord record;
        Vector3 position;
        Quaternion rotation;
        bool level, owned = true, destroyed = false, ownerNotReal = false;
        if (world.Buildings.Find(id)is { } site)
        {
            if (!site.Complete || site.Destroyed || site.Owner != faction || _db.Find(site.Record)is not { } found)
                return null;
            record = found;
            position = site.Position;
            rotation = site.Rotation;
            level = false;
        }
        else
        {
            var parent = _levelBuildings.Value.FirstOrDefault(b => b.Building.Placement.Key == id);
            if (parent.Building.Record is null || _db.Find(plan.Record)is not { } planned)
                return null;
            owned = world.Fixtures.OwnsBuilding(id, faction);
            destroyed = world.Fixtures.PublishedRuins().FirstOrDefault(r => r.Building.Key == id)?.Destroyed ?? parent.Building.Destroyed;
            var home = _population.Sites.SelectMany(s => s.Buildings.Select(b => (Site: s, Building: b))).FirstOrDefault(p => p.Building.Key == id);
            string? owner = home.Building?.OwnerFactionId;
            int townOwner = home.Site is not null && home.Site.Index < _population.SiteFactions.Count ? _population.SiteFactions[home.Site.Index] : -1;
            var ownerData = owner is not null ? _population.Factions.FirstOrDefault(f => f.Id == owner) : (uint)townOwner < (uint)_population.Factions.Count ? _population.Factions[townOwner] : null;
            ownerNotReal = ownerData is null || ownerData.NotReal;
            if (!PlacementRules.MayBuildIn(BuildingBuildData.From(planned), owned, destroyed, ownerNotReal))
                return null;
            record = parent.Building.Record;
            position = parent.Building.Position;
            rotation = parent.Building.Placement.Rotation;
            level = true;
        }

        var footprint = _footprints.Require(record.StringId);
        if (footprint is null || footprint.Gap(position, rotation, new(plan.Position.X, plan.Position.Z)) > 0.1f)
            return null;
        var key = (record.StringId, id, position, rotation);
        if (!_floors.TryGetValue(key, out var surface))
        {
            float scale = record.GetFloat("scale", 1);
            var node = WorldObjectLayout.InstanceTransform(position, rotation, new Vector3(scale > 0 ? scale : 1));
            _floors[key] = surface = FloorSurfaces.For(_db, record, id, position, rotation, node, _collisions);
        }

        if (surface.HighestUpTo(plan.Position.X, plan.Position.Z, plan.Floor)is not { } height)
            return null;
        var feet = new Vector3(plan.Position.X, height, plan.Position.Z);
        int actualFloor = surface.FloorAt(feet);
        return new(height, new GhostFloor(id, footprint.Place(position, rotation), actualFloor, level ? id : null, Owned: owned) { Destroyed = destroyed, OwnerNotReal = ownerNotReal, Ceiling = surface.CeilingAbove(feet.X, feet.Z, feet.Y + InteriorVolume.CoverClearance) });
    }
}