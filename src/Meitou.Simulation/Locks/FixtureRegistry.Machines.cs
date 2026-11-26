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

    /// <summary>
    /// Observed targets before use. The same site and revision are promoted by ProductionSystem.Ensure; observation never changes Buildings.
    /// </summary>
    readonly Dictionary<string, BuildSite> _townWorkTargets = new(StringComparer.Ordinal);

    /// <summary>The fixture-resolved owner of an ordinary town placement, including placements without lockable fixtures.</summary>
    public int? TownMachineOwner(string building) => _townMachines.TryGetValue(building, out var source) ? source.Owner : null;

    void RegisterTownMachine(World world, PopulationData data, int site, TownBuilding building, int owner)
    {
        if (_properties.TryGetValue(building.Key, out var property)) owner = property.Owner;
        if (_townMachines.TryGetValue(building.Key, out var old) && !old.Building.LayoutBuildings.SequenceEqual(building.LayoutBuildings))
        {
            world.SavedMachines?.InvalidateLayout(old.Building);
            SavedPieces?.InvalidateHouse(building.Key);
        }
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
        bool destroyed = IsDestroyed(building);
        bool complete = saved?.Bools.GetValueOrDefault("is complete", true) ?? building.LevelState?.GetBool("is complete", true) ?? true;
        if (destroyed || !complete) return null;
        if (world.SystemOf<Production.ProductionSystem>()?.Data(building.BuildingId) is null) return null;
        if (_townWorkTargets.TryGetValue(building.Key, out var target) && target.Record == building.BuildingId
            && target.Position == building.Position && target.Owner == source.Owner) return target;
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
            LevelTown = source.Data.Sites[source.Site].Town,
            Position = building.Position,
            Rotation = placement.Rotation,
            Owner = source.Owner,
            Complete = true,
            Target = build.Target,
            Progress = build.Target,
            Rate = build.Rate,
            Materials = [],
        };
        _townWorkTargets[site.Id] = site;
        return site;
    }

    /// <summary>
    /// A production-capable layout piece uses its canonical piece id and the registered parent's current owner.
    /// </summary>
    public BuildSite? GetTownPieceMachineSite(World world, TownBuilding building, TownPiece piece)
    {
        var layout = building.LayoutBuildings.FirstOrDefault(p => p.Id == piece.Id)
            ?? new TownLayoutBuilding(piece.Id, piece.RecordId, false, piece.Position, piece.Rotation) { Floor = piece.Floor, InteriorOnly = !piece.Outside };
        return GetTownPieceMachineSite(world, building, layout);
    }

    /// <summary>
    /// Every machine-capable layout object uses the same authoritative identity as selectable furniture, including batteries and exterior pieces.
    /// </summary>
    public BuildSite? GetTownPieceMachineSite(World world, TownBuilding building, TownLayoutBuilding piece)
    {
        if (IsEmptied(building.Key)) return null;
        if (world.Buildings.Find(piece.Id) is { } known) return known;
        if (!_townMachines.TryGetValue(building.Key, out var source) || IsDestroyed(source.Building)
            || !source.Building.LayoutBuildings.Contains(piece) && !source.Building.Pieces.Any(p => p.Id == piece.Id && p.RecordId == piece.RecordId)
            || source.Data.Db.Find(piece.RecordId) is not { } record
            || world.SystemOf<Production.ProductionSystem>()?.Data(piece.RecordId) is null) return null;
        if (_townWorkTargets.TryGetValue(piece.Id, out var target) && target.Record == piece.RecordId
            && target.Position == piece.Position && target.Rotation == piece.Rotation && target.Owner == source.Owner) return target;

        var build = BuildingBuildData.From(record);
        var site = new BuildSite
        {
            Id = piece.Id,
            Record = piece.RecordId,
            Name = record.Name,
            LevelMachine = true,
            Parent = piece.Outdoors ? null : building.Key,
            LevelPlacement = new BuildingPlacement(building.Zone, piece.Id, piece.RecordId, piece.Position, piece.Rotation, null, piece.Position.Y),
            LevelTown = source.Data.Sites[source.Site].Town,
            Position = piece.Position,
            Rotation = piece.Rotation,
            Floor = piece.Floor,
            Owner = source.Owner,
            Complete = true,
            Target = build.Target,
            Progress = build.Target,
            Rate = build.Rate,
            Materials = [],
        };
        _townWorkTargets[site.Id] = site;
        return site;
    }

    /// <summary>
    /// The house that owns a canonical layout object, including an exterior piece whose spatial parent is null.
    /// </summary>
    internal static string? TownMachineParent(BuildSite site) =>
        site.LevelMachine && site.Id.IndexOf('/') is > 0 and var slash ? site.Id[..slash] : site.Parent;

    /// <summary>
    /// A materialized worksite still names the registered placement or layout piece, its current owner and an intact parent.
    /// </summary>
    internal bool CurrentTownMachine(BuildSite site)
    {
        if (!site.LevelMachine) return true;
        string? layout = TownMachineParent(site);
        string parent = layout ?? site.Id;
        if (!_townMachines.TryGetValue(parent, out var source) || source.Owner != site.Owner || IsDestroyed(source.Building)
            || layout is not null && IsEmptied(parent)) return false;
        if ((uint)source.Site >= (uint)source.Data.Sites.Count || !source.Data.Sites[source.Site].Buildings.Contains(source.Building)) return false;
        return layout is null ? source.Building.BuildingId == site.Record
            : source.Building.LayoutBuildings.Any(piece => piece.Id == site.Id && piece.RecordId == site.Record
                && piece.Position == site.Position && piece.Rotation == site.Rotation)
                || source.Building.Pieces.Any(piece => piece.Id == site.Id && piece.RecordId == site.Record
                    && piece.Position == site.Position && piece.Rotation == site.Rotation);
    }

    /// <summary>
    /// Purchased houses discard their layout targets together with the furniture; future observation cannot resurrect them.
    /// </summary>
    void ForgetTownInterior(World world, string building, bool spill, TownBuilding? layout = null)
    {
        layout ??= _townMachines.TryGetValue(building, out var source) ? source.Building : null;
        if (layout is not null) world.SavedMachines?.InvalidateLayout(layout);
        SavedPieces?.InvalidateHouse(building);
        foreach (var target in _townWorkTargets.Values.Where(s => TownMachineParent(s) == building).ToArray()) _townWorkTargets.Remove(target.Id);
        world.SystemOf<Production.ProductionSystem>()?.RemoveTownInterior(world, building, spill);
    }

    /// <summary>
    /// Drops a removed layout object's observed target so a later object receives a fresh revision and current placement.
    /// </summary>
    internal void ForgetTownWorkTarget(World world, string key)
    {
        _townWorkTargets.Remove(key);
        ClearTownWorkers(world, key);
    }

    void TransferTownMachine(World world, string key, int owner)
    {
        if (_townMachines.TryGetValue(key, out var source)) _townMachines[key] = (source.Data, source.Site, source.Building, owner);
        if (world.Buildings.Find(key) is { LevelPlacement: not null } machine) { machine.Owner = owner; world.Buildings.Touch(); }
        foreach (var piece in world.Buildings.All.Where(s => s.LevelMachine && TownMachineParent(s) == key))
        {
            piece.Owner = owner;
            ClearTownWorkers(world, piece.Id);
        }
        world.Buildings.Touch();
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