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
    /// <summary>
    /// Projectile changes use the live authority source because headless worlds do not publish local snapshots.
    /// </summary>
    readonly ProjectileWatch _projectiles = new();
    /// <summary>
    /// Reused delivery buffer, cleared at every serialized publication.
    /// </summary>
    readonly List<ProjectileEvent> _changes = [];
    /// <summary>
    /// Last tick sampled; multiple boundary callbacks within a tick do not repeat a projectile change.
    /// </summary>
    long _projectileTick = -1;
    World? _owner;
    public void Synchronize(World world)
    {
        if (_owner is null)
        {
            _owner = world;
            if (mods.WantsCombatHits) world.CombatHitFeed = _queue;
            _projectiles.Reset(world.SystemOf<IProjectileSource>()?.PublishProjectiles() ?? []);
            _projectileTick = world.Tick;
        }
        else if (!ReferenceEquals(_owner, world))
            throw new InvalidOperationException("A mod hit feed belongs to another world.");
        _hits.Clear();
        while (_queue.TryDequeue(out var hit))
            _hits.Add(hit);
        mods.DeliverCombatHits(_hits);
        PublishProjectiles(world);
    }

    /// <summary>
    /// Samples settled projectile state once per tick, outside the parallel simulation phases.
    /// </summary>
    void PublishProjectiles(World world)
    {
        if (!mods.WantsProjectiles || _projectileTick == world.Tick) return;

        _projectileTick = world.Tick;
        _changes.Clear();
        _projectiles.Look(world.SystemOf<IProjectileSource>()?.PublishProjectiles() ?? [], _changes);
        mods.DeliverProjectiles(_changes);
    }

    public void Suspend(World world)
    {
        if (ReferenceEquals(world.CombatHitFeed, _queue))
            world.CombatHitFeed = null;
        _queue.Clear();
        _hits.Clear();
        _projectiles.Reset([]);
        _changes.Clear();
        _projectileTick = -1;
        _owner = null;
    }
}