using Meitou.Data;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Construction;

namespace Meitou.Runtime;

public interface IRuntimeBuildingSink
{
    void Add(BuildingPlacement placement);
    void Remove(string identity);
}

public sealed class NavigationBuildingSink(NavSystem navigation) : IRuntimeBuildingSink
{
    public void Add(BuildingPlacement placement) => navigation.AddRuntimeBuilding(placement);
    public void Remove(string identity) => navigation.RemoveRuntimeBuilding(identity);
}

/// <summary>Authority-owned navigation overlay, independent of publication, camera and renderer frames.</summary>
public sealed class WorldBuildings(GameDatabase db, IRuntimeBuildingSink sink) : IRuntimeWorldService
{
    readonly Dictionary<string, BuildingBuildData?> data = new(StringComparer.Ordinal);
    readonly Dictionary<string, BuildingPlacement> published = new(StringComparer.Ordinal);
    World? active;
    long revision = -1, nestsRevision = -1, ruinsRevision = -1, emptiedRevision = -1;
    bool force = true;

    public void Synchronize(World world)
    {
        if (active is not null && !ReferenceEquals(active, world)) throw new InvalidOperationException("Suspend the previous world before reusing its building service.");
        active = world;
        if (!force && revision == world.Buildings.Revision && nestsRevision == world.NestBuildings.Revision
            && ruinsRevision == world.Fixtures.RuinsRevision && emptiedRevision == world.Fixtures.EmptiedRevision) return;
        var current = new Dictionary<string, BuildingPlacement>(StringComparer.Ordinal);
        foreach (var ruin in world.Fixtures.PublishedRuins())
            if (ruin.Building.Resolved is { } resolved && Navigable(resolved.Record.StringId))
                current[ruin.Building.Key] = resolved.Placement with { DestroyedOverride = ruin.Destroyed };
        foreach (var building in world.Fixtures.PublishedEmptied())
            if (building.Resolved is { } resolved && Navigable(resolved.Record.StringId))
                current[building.Key] = resolved.Placement with { DestroyedOverride = false, WithoutInterior = true };
        foreach (var nest in world.NestBuildings.Published())
            if (Navigable(nest.Record)) current[nest.Id] = new(WorldLayout.ZoneOf(nest.Position.X, nest.Position.Z), nest.Id, nest.Record,
                nest.Position, nest.Rotation, null, nest.Position.Y) { Key = nest.Id };
        foreach (var site in world.Buildings.All)
        {
            if (site.LevelMachine) continue;
            if (!data.TryGetValue(site.Record, out var build)) data[site.Record] = build = db.Find(site.Record) is { Type: Meitou.Data.Fcs.FcsRecordType.BUILDING } r ? BuildingBuildData.From(r) : null;
            if (build is null || build.PathMode == 0 || site.LevelPlacement is null && !site.Complete && !site.Destroyed && build.BuildThreshold != 0) continue;
            current[site.Id] = Placement(site);
        }
        foreach (string id in published.Keys.Where(id => !current.ContainsKey(id)).ToArray())
        {
            sink.Remove(id);
            published.Remove(id);
        }
        foreach (var (id, placement) in current)
        {
            if (!force && published.TryGetValue(id, out var before) && before == placement) continue;
            // Remember attempted writes too: a sink may fail after making its change.
            published[id] = placement;
            sink.Add(placement);
        }
        revision = world.Buildings.Revision;
        nestsRevision = world.NestBuildings.Revision; ruinsRevision = world.Fixtures.RuinsRevision; emptiedRevision = world.Fixtures.EmptiedRevision;
        force = false;
    }

    bool Navigable(string record)
    {
        if (!data.TryGetValue(record, out var build)) data[record] = build = db.Find(record) is { Type: Meitou.Data.Fcs.FcsRecordType.BUILDING } r ? BuildingBuildData.From(r) : null;
        return build is { PathMode: not 0 };
    }

    public void Suspend(World world)
    {
        if (!ReferenceEquals(active, world)) return;
        revision = -1; force = true;
        List<Exception>? failures = null;
        foreach (string id in published.Keys.ToArray())
            try { sink.Remove(id); published.Remove(id); } catch (Exception e) { (failures ??= []).Add(e); }
        if (failures is not null) throw new AggregateException("Could not remove the world's navigation overlay.", failures);
        active = null;
    }

    static BuildingPlacement Placement(BuildSite site) => site.LevelPlacement is { } level
        ? level with { Key = site.Id, WorldY = site.Position.Y, DestroyedOverride = site.Destroyed }
        : new(WorldLayout.ZoneOf(site.Position.X, site.Position.Z), site.Id, site.Record, site.Position, site.Rotation, null, site.Position.Y)
            { Key = site.Id, Contained = site.Parent is not null, ContainerId = site.Parent, DestroyedOverride = site.Destroyed };
}
