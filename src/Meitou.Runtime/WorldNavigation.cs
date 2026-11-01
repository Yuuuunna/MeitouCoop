using Meitou.Navigation;
using Meitou.Simulation;

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
    public void Synchronize(World world)
    {
        if (active is not null && !ReferenceEquals(active, world)) throw new InvalidOperationException("Navigation is still bound to another world.");
        active = world;
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
        if (changed.Count > 0) world.InvalidateNavigation(changed);
    }
    public void Suspend(World world)
    {
        if (!ReferenceEquals(active, world)) return;
        navigation.NewIncarnation(requeuePending: false); active = null;
    }
}
