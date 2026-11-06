using Meitou.Navigation;
using Meitou.Simulation;
using Meitou.Data.World;

namespace Meitou.Runtime;

/// <summary>Publishes generated navigation only while bound to the active runtime's serialized boundary.</summary>
public sealed class WorldNavigation : IRuntimeWorldService
{
    readonly NavSystem navigation;
    public WorldNavigation(NavSystem navigation)
    {
        if (!navigation.DeferredPublication) throw new ArgumentException("Runtime navigation requires deferred publication.", nameof(navigation));
        this.navigation = navigation;
    }
    World? active;
    NavWorld? lastPublished;
    long lastIncarnation;
    readonly HashSet<ZoneCoordinate> seenZones = [];
    public void Synchronize(World world)
    {
        if (active is not null && !ReferenceEquals(active, world)) throw new InvalidOperationException("Navigation is still bound to another world.");
        active = world;
        var before = navigation.Walkability.World;
        long incarnation = navigation.Incarnation;
        // Only an observed publication within one incarnation can prove that a zone is newly added.
        bool continuous = lastPublished is not null && lastIncarnation == incarnation && ReferenceEquals(lastPublished, before);
        var changed = navigation.CommitPrepared().ToHashSet();
        bool keep = false;
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Current[i].Alive || table.Cold(i) is not { IsPlayer: true, DiedTick: < 0 }) continue;
            var at = table.Current[i].Position;
            if (!navigation.Walkability.IsWalkable(at.X, at.Z, at.Y)) keep |= navigation.KeepAt(at);
        }
        if (keep) changed.UnionWith(navigation.CommitPrepared());
        var after = navigation.Walkability.World;
        if (continuous && ReferenceEquals(before, after) && changed.Count == 0) return;
        // Include geometry changes even if they came from an earlier owner-boundary service or removed a mesh before this commit.
        foreach (var zone in before.Zones.Concat(after.Zones).Concat(lastPublished?.Zones ?? []).Distinct())
            if (!ReferenceEquals(before.Find(zone), after.Find(zone)) || lastPublished is not null && !ReferenceEquals(lastPublished.Find(zone), before.Find(zone)))
                changed.Add(zone);
        if (lastPublished is not null && lastIncarnation != incarnation) changed.UnionWith(lastPublished.Zones);
        var added = new HashSet<ZoneCoordinate>();
        var hard = new HashSet<ZoneCoordinate>();
        foreach (var zone in changed)
            if (continuous && !seenZones.Contains(zone) && before.Find(zone) is null && after.Find(zone) is not null && navigation.IsReady(zone)) added.Add(zone);
            else hard.Add(zone);
        // A route crossing both groups must be dropped before a newly loaded zone can request continuation.
        if (hard.Count > 0) world.InvalidateNavigation(hard);
        if (added.Count > 0) world.InvalidateNavigation(added, onlyAddedZones: true);
        seenZones.UnionWith(before.Zones); seenZones.UnionWith(after.Zones);
        lastPublished = after;
        lastIncarnation = navigation.Incarnation;
    }
    public void Suspend(World world)
    {
        if (!ReferenceEquals(active, world)) return;
        navigation.NewIncarnation(requeuePending: false); active = null;
        lastPublished = null; seenZones.Clear();
    }
}
