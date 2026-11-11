using Meitou.Simulation.Navigation;
using Meitou.Simulation;
using Meitou.Data.World;

namespace Meitou.Runtime;

/// <summary>
/// Publishes generated navigation only while bound to the active runtime's serialized boundary.
/// </summary>
public sealed class WorldNavigation : IRuntimeWorldService
{
    readonly NavSystem _navigation;
    World? _active;
    NavWorld? _lastPublished;
    long _lastIncarnation;
    readonly HashSet<ZoneCoordinate> _seenZones = [];
    public WorldNavigation(NavSystem navigation)
    {
        if (!navigation.DeferredPublication)
            throw new ArgumentException("Runtime navigation requires deferred publication.", nameof(navigation));
        this._navigation = navigation;
    }

    public void Synchronize(World world)
    {
        if (_active is not null && !ReferenceEquals(_active, world))
            throw new InvalidOperationException("Navigation is still bound to another world.");
        _active = world;
        var before = _navigation.Walkability.World;
        long incarnation = _navigation.Incarnation;
        // Only an observed publication within one incarnation can prove that a zone is newly added.
        bool continuous = _lastPublished is not null && _lastIncarnation == incarnation && ReferenceEquals(_lastPublished, before);
        var changed = _navigation.CommitPrepared().ToHashSet();
        bool keep = false;
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Current[i].Alive || table.Cold(i)is not { IsPlayer: true, DiedTick: < 0 })
                continue;
            var at = table.Current[i].Position;
            if (!_navigation.Walkability.IsWalkable(at.X, at.Z, at.Y))
                keep |= _navigation.KeepAt(at);
        }

        if (keep)
            changed.UnionWith(_navigation.CommitPrepared());
        var after = _navigation.Walkability.World;
        if (continuous && ReferenceEquals(before, after) && changed.Count == 0)
            return;
        // Include geometry changes even if they came from an earlier owner-boundary service or removed a mesh before this commit.
        foreach (var zone in before.Zones.Concat(after.Zones).Concat(_lastPublished?.Zones ?? []).Distinct())
            if (!ReferenceEquals(before.Find(zone), after.Find(zone)) || _lastPublished is not null && !ReferenceEquals(_lastPublished.Find(zone), before.Find(zone)))
                changed.Add(zone);
        if (_lastPublished is not null && _lastIncarnation != incarnation)
            changed.UnionWith(_lastPublished.Zones);
        var added = new HashSet<ZoneCoordinate>();
        var hard = new HashSet<ZoneCoordinate>();
        foreach (var zone in changed)
            if (continuous && !_seenZones.Contains(zone) && before.Find(zone)is null && after.Find(zone)is not null && _navigation.IsReady(zone))
                added.Add(zone);
            else
                hard.Add(zone);
        // A route crossing both groups must be dropped before a newly loaded zone can request continuation.
        if (hard.Count > 0)
            world.InvalidateNavigation(hard);
        if (added.Count > 0)
            world.InvalidateNavigation(added, onlyAddedZones: true);
        _seenZones.UnionWith(before.Zones);
        _seenZones.UnionWith(after.Zones);
        _lastPublished = after;
        _lastIncarnation = _navigation.Incarnation;
    }

    public void Suspend(World world)
    {
        if (!ReferenceEquals(_active, world))
            return;
        _navigation.NewIncarnation(requeuePending: false);
        _active = null;
        _lastPublished = null;
        _seenZones.Clear();
    }
}