using System.Collections.Concurrent;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Runtime;

/// <summary>
/// Per-world display feed, delivered only at serialized runtime boundaries; never part of simulation state.
/// </summary>
sealed class ModHitFeedService(ModHost mods) : IRuntimeWorldService
{
    readonly ConcurrentQueue<CombatHit> _queue = new();
    readonly List<CombatHit> _hits = [];
    World? _owner;
    public void Synchronize(World world)
    {
        if (_owner is null)
        {
            _owner = world;
            world.CombatHitFeed = _queue;
        }
        else if (!ReferenceEquals(_owner, world))
            throw new InvalidOperationException("A mod hit feed belongs to another world.");
        _hits.Clear();
        while (_queue.TryDequeue(out var hit))
            _hits.Add(hit);
        mods.DeliverCombatHits(_hits);
    }

    public void Suspend(World world)
    {
        if (ReferenceEquals(world.CombatHitFeed, _queue))
            world.CombatHitFeed = null;
        _queue.Clear();
        _hits.Clear();
        _owner = null;
    }
}