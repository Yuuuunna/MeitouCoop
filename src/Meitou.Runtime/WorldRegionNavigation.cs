using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using System.Diagnostics;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;
using Meitou.Simulation;

namespace Meitou.Runtime;
public interface IRegionNavigation
{
    IReadOnlyCollection<ZoneCoordinate> Tracked { get; }

    IEnumerable<ZoneCoordinate> RouteHints { get; }

    bool Ready(ZoneCoordinate zone);
    void Request(ZoneCoordinate zone);
    void Unload(ZoneCoordinate zone);
}

public sealed class NavigationRegions(NavSystem navigation) : IRegionNavigation
{
    public IReadOnlyCollection<ZoneCoordinate> Tracked => navigation.TrackedZones;
    public IEnumerable<ZoneCoordinate> RouteHints => navigation.Walkability.RouteZones(TimeSpan.FromSeconds(30));

    public bool Ready(ZoneCoordinate zone) => navigation.IsReady(zone);
    public void Request(ZoneCoordinate zone) => _ = navigation.LoadZone(zone);
    public void Unload(ZoneCoordinate zone) => navigation.UnloadZone(zone);
}

/// <summary>
/// One navigation retention policy for every host. Interest is the union of simulation, role/activity/preparation and route demand.
/// </summary>
public sealed class WorldRegionNavigation : IRuntimeWorldService
{
    readonly IRegionNavigation _navigation;
    readonly Func<double> _clock;
    readonly double _idleSeconds;
    readonly Dictionary<ZoneCoordinate, double> _lastWanted = [];
    World? _active;
    public WorldRegionNavigation(IRegionNavigation navigation, double idleSeconds = 30, Func<double>? clock = null)
    {
        if (!double.IsFinite(idleSeconds) || idleSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(idleSeconds));
        this._navigation = navigation;
        this._idleSeconds = idleSeconds;
        this._clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    public void Synchronize(World world)
    {
        if (_active is not null && !ReferenceEquals(_active, world))
            throw new InvalidOperationException("Region retention is bound to another world.");
        _active = world;
        double now = _clock();
        var wanted = world.Regions.Capture(simulation: false).Where(p => world.Players.RequiresExplicitControl || (p.Value.Reasons & (RegionReason.PlayerRole | RegionReason.LoadedEntity | RegionReason.Preparation)) != 0).Select(p => p.Key).ToHashSet();
        if (world.ActiveZones is { } simulation)
            wanted.UnionWith(simulation);
        if (world.Systems.OfType<PopulationSystem>().FirstOrDefault()is { } population)
            wanted.UnionWith(population.ActiveZones);
        wanted.UnionWith(_navigation.RouteHints.Where(z => z.IsInsideGrid));
        foreach (var zone in _navigation.Tracked)
            _lastWanted.TryAdd(zone, now);
        foreach (var zone in wanted.OrderBy(z => z.X).ThenBy(z => z.Y))
        {
            _lastWanted[zone] = now;
            if (!_navigation.Ready(zone))
                _navigation.Request(zone);
        }

        foreach (var pair in _lastWanted.Where(p => !wanted.Contains(p.Key) && now - p.Value >= _idleSeconds).ToArray())
        {
            _navigation.Unload(pair.Key);
            _lastWanted.Remove(pair.Key);
        }

        if (world.AtTickBoundary && !world.HasPendingDurableMutation)
            world.StoredPlayerRoles?.RestoreReady(position => _navigation.Ready(WorldLayout.ZoneOf(position.X, position.Z)));
    }

    public void Suspend(World world)
    {
        if (!ReferenceEquals(_active, world))
            return;
        // Navigation belongs to WorldContent. Its next world adopts ready static zones and expires unused ones.
        _lastWanted.Clear();
        _active = null;
    }
}