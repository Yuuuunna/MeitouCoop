using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Locks;

public sealed partial class FixtureRegistry
{
    // Placement metadata only: registering a town never starts all its automatic producers.
    readonly Dictionary<string, (PopulationData Data, int Site, TownBuilding Building, int Owner)> _townMachines = new(StringComparer.Ordinal);

    /// <summary>The fixture-resolved owner of an ordinary town placement, including placements without lockable fixtures.</summary>
    public int? TownMachineOwner(string building) => _townMachines.TryGetValue(building, out var source) ? source.Owner : null;

    void RegisterTownMachine(World world, PopulationData data, int site, TownBuilding building, int owner)
    {
        _townMachines[building.Key] = (data, site, building, owner);
        if (world.Buildings.Find(building.Key) is { LevelMachine: true } known)
        {
            if (known.Owner != owner) TransferTownMachine(world, building.Key, owner);
            if (known.Record != building.BuildingId)
            {
                known.Record = building.BuildingId;
                world.Buildings.Changed();
                ClearTownWorkers(world, building.Key);
            }
        }
    }

    /// <summary>The ordinary level machine's canonical site, created only when first used. Ownership comes from fixture registration.</summary>
    public BuildSite? GetTownMachineSite(World world, TownBuilding building)
    {
        if (world.Buildings.Find(building.Key) is { } existing) return existing;
        if (!_townMachines.TryGetValue(building.Key, out var source) || source.Building.BuildingId != building.BuildingId
            || source.Owner < 0 || source.Owner >= source.Data.Factions.Count
            || source.Data.Db.Find(building.BuildingId) is not { } record) return null;
        building = source.Building;
        var saved = Saved?.GetValueOrDefault(building.Key);
        bool destroyed = saved?.Bools.GetValueOrDefault("destroyed", building.Destroyed) ?? building.Destroyed;
        bool complete = saved?.Bools.GetValueOrDefault("is complete", true) ?? building.LevelState?.GetBool("is complete", true) ?? true;
        if (destroyed || !complete) return null;
        if (world.SystemOf<Production.ProductionSystem>()?.Data(building.BuildingId) is null) return null;
        var build = BuildingBuildData.From(record);
        var placement = building.Resolved?.Placement ?? new BuildingPlacement(building.Zone, building.InstanceId, building.BuildingId,
            building.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, building.Yaw), building.LevelState?.StringId, building.Position.Y);
        var site = new BuildSite
        {
            Id = building.Key,
            Record = building.BuildingId,
            Name = building.Name,
            LevelMachine = true,
            LevelPlacement = placement,
            LevelTownName = source.Data.Sites[source.Site].Town.Name,
            Position = building.Position,
            Rotation = placement.Rotation,
            Owner = source.Owner,
            Complete = true,
            Target = build.Target,
            Progress = build.Target,
            Rate = build.Rate,
            Materials = [],
        };
        world.Buildings.Add(site);
        return site;
    }

    void TransferTownMachine(World world, string key, int owner)
    {
        if (_townMachines.TryGetValue(key, out var source)) _townMachines[key] = (source.Data, source.Site, source.Building, owner);
        if (world.Buildings.Find(key) is { LevelPlacement: not null } machine) { machine.Owner = owner; world.Buildings.Touch(); }
        ClearTownWorkers(world, key);
    }

    static void ClearTownWorkers(World world, string key)
    {
        for (int i = 0; i < world.Characters.HighWater; i++)
            if (world.Characters.Cold(i)?.Ai is { } mind && (mind.WorkBuilding?.Key == key || mind.WorkOperate?.Key == key || mind.CollectFrom?.Key == key))
            {
                mind.WorkOperate = null; mind.CollectFrom = null; mind.WorkBuilding = null; mind.Target = null;
            }
    }
}