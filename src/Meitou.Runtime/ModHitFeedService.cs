using System.Collections.Concurrent;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Runtime;

/// <summary>Per-world display feed, delivered only at serialized runtime boundaries; never part of simulation state.</summary>
sealed class ModHitFeedService(ModHost mods) : IRuntimeWorldService
{
    readonly ConcurrentQueue<CombatHit> queue = new();
    readonly List<CombatHit> hits = [];
    World? owner;

    public void Synchronize(World world)
    {
        if (owner is null)
        {
            owner = world;
            world.CombatHitFeed = queue;
        }
        else if (!ReferenceEquals(owner, world)) throw new InvalidOperationException("A mod hit feed belongs to another world.");
        hits.Clear();
        while (queue.TryDequeue(out var hit)) hits.Add(hit);
        mods.DeliverCombatHits(hits);
    }

    public void Suspend(World world)
    {
        if (ReferenceEquals(world.CombatHitFeed, queue)) world.CombatHitFeed = null;
        queue.Clear();
        hits.Clear();
        owner = null;
    }
}
