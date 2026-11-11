using System.Collections.ObjectModel;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation;

[Flags]
public enum RegionReason { PlayerRole = 1, Combat = 2, Work = 4, BaseActivity = 8, Preparation = 16, LoadedEntity = 32 }
public readonly record struct RegionDemand(int Sources, RegionReason Reasons);

/// <summary>
/// World-owned simulation demand. Cameras/subscriptions do not grant simulation regions in explicit multiplayer mode.
/// </summary>
public sealed class WorldRegions
{
    readonly World _world;
    readonly object _gate = new();
    readonly Dictionary<ulong, (ZoneCoordinate[] Zones, bool Simulation)> _holds = [];
    ulong _nextHold;
    internal WorldRegions(World world) => this._world = world;

    /// <summary>
    /// Trusted host preparation lease, not a remote request. May be released from an asynchronous loader.
    /// </summary>
    public IDisposable Retain(IEnumerable<ZoneCoordinate> zones, bool simulation = true)
    {
        var owned = zones.Distinct().OrderBy(z => z.X).ThenBy(z => z.Y).ToArray();
        if (owned.Length > 4096 || owned.Any(z => !z.IsInsideGrid)) throw new ArgumentOutOfRangeException(nameof(zones));
        lock (_gate)
        {
            ulong id = checked(++_nextHold); _holds.Add(id, (owned, simulation));
            return new Lease(this, id);
        }
    }
    sealed class Lease(WorldRegions regions, ulong id) : IDisposable
    {
        WorldRegions? _owner = regions;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } current) return;
            lock (current._gate) current._holds.Remove(id);
        }
    }

    /// <summary>
    /// Capture at a serial world boundary. Each source contributes once per zone; overlapping users never subtract one another.
    /// </summary>
    public IReadOnlyDictionary<ZoneCoordinate, RegionDemand> Capture(bool simulation = true, int ring = 0)
    {
        if (ring is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(ring));
        var demand = new Dictionary<ZoneCoordinate, RegionDemand>();
        var area = new HashSet<ZoneCoordinate>();
        void Add(ZoneCoordinate zone, RegionReason reason)
        {
            if (!zone.IsInsideGrid) return;
            var previous = demand.GetValueOrDefault(zone);
            demand[zone] = new(previous.Sources + 1, previous.Reasons | reason);
        }
        void Around(Vector3 at, RegionReason reason)
        {
            if (!float.IsFinite(at.X) || !float.IsFinite(at.Z) || !WorldLayout.ZoneOf(at.X, at.Z).IsInsideGrid) return;
            area.Clear(); ZoneActivation.AddZonesAround(at, ring, area);
            foreach (var zone in area) Add(zone, reason);
        }
        var table = _world.Characters;
        if (_world.StoredPlayerRoles is { } stored)
            foreach (var position in stored.PendingPositions) Around(position, RegionReason.PlayerRole);
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Current[i].Alive || table.Cold(i) is not { DiedTick: < 0 } cold || cold.Medical?.Dead == true) continue;
            var at = table.Current[i].Position;
            if (cold.IsPlayer)
            {
                if (table.TryResolveNext(cold.CarriedBy, out int carrier)) at = table.Current[carrier].Position;
                else if (_world.StoredPlayerRoles is { } savedRoles) at = savedRoles.CarryPosition(cold, at);
                Around(at, RegionReason.PlayerRole);
            }
            if (!simulation) Around(at, RegionReason.LoadedEntity);
            if (cold.InCombat) Around(at, RegionReason.Combat);
            if ((CharacterTask)table.Current[i].Task is not (CharacterTask.Build or CharacterTask.Dismantle or CharacterTask.DeliverMaterials
                or CharacterTask.FetchMaterials or CharacterTask.Operate or CharacterTask.Haul)) continue;
            foreach (string id in new[] { cold.BuildSite, cold.OperateSite, cold.HaulSite, cold.HaulTo, cold.FetchSite }.OfType<string>().Distinct())
                if (_world.Buildings.Find(id) is { } site && _world.IsPlayerFaction(site.Owner))
                {
                    Around(at, RegionReason.Work);
                    Around(site.Position, RegionReason.Work);
                }
        }
        var activeBases = new HashSet<(int Owner, int Town)>();
        foreach (var machine in _world.Machines.All)
        {
            if (_world.Buildings.Find(machine.Site) is not { Complete: true } site || !_world.IsPlayerFaction(site.Owner) || machine.Broken) continue;
            bool producing = machine.Data.IsAutomatic && machine.Data.IsProducer && !machine.Data.IsFurnace && machine.Active && machine.PowerOn && !machine.Closed
                && machine.Output is { Full: false } && (machine.Inputs.Length == 0 || machine.Inputs.Any(i => i.Count > 0 || i.Level > 0));
            if (machine.Burning || machine.Operators > 0 || machine.Craft is { Queue.Count: > 0 } || producing) activeBases.Add((site.Owner, site.Town));
        }
        foreach (var site in _world.Buildings.All)
            if (activeBases.Contains((site.Owner, site.Town))) Around(site.Position, RegionReason.BaseActivity);
        lock (_gate)
            foreach (var hold in _holds.Values)
                if (!simulation || hold.Simulation)
                    foreach (var zone in hold.Zones) Add(zone, RegionReason.Preparation);
        return new ReadOnlyDictionary<ZoneCoordinate, RegionDemand>(demand);
    }
}