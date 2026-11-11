using System.Numerics;
using Meitou.Diagnostics;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation;

/// <summary>
/// Bounded observation sidecar. Never allocates entity IDs or changes simulation state.
/// </summary>
public sealed class MovementTraceObserver : IDisposable
{
    const int Capacity = 4096;
    readonly World _world;
    readonly ILatencyTraceSink _sink;
    readonly Dictionary<int, Sample> _active = [];
    readonly Dictionary<ulong, LatencyTraceEvent> _executing = [];
    LatencyTraceEvent _context;

    sealed class Sample(LatencyTraceEvent key, CharacterId character, Vector3 start, bool stationary)
    {
        public LatencyTraceEvent Key = key;
        public LatencyTraceEvent AppliedKey;
        public readonly CharacterId Character = character;
        public readonly Vector3 Start = start;
        public readonly bool Stationary = stationary;
        public bool Moved, PathApplied;
        public long MotionTick;
    }

    internal MovementTraceObserver(World world, ILatencyTraceSink sink)
    {
        this._world = world; this._sink = sink;
        world.Characters.MovementTrace = this;
        foreach (var movement in world.Systems.OfType<MovementSystem>()) movement.Paths.TraceSink = sink;
    }

    internal void Emit(LatencyTraceEvent e) => LatencyTrace.Emit(_sink, e with { Tick = _world.Tick, Endpoint = LatencyEndpoint.Authority });
    internal LatencyTraceEvent Key(AuthorityConnection connection, ulong request) =>
        new(LatencyStage.AuthorityExecute, connection.World, connection.Player.Value, connection.Generation, request);

    internal void Begin(AuthorityConnection connection, ulong request, SimCommand command, AuthorityContext authority)
    {
        _context = Key(connection, request); Emit(_context);
        _executing.Clear();
        if (command is not MoveOrder) return;
        foreach (var grant in authority.Characters)
        {
            var key = _context with { Entity = grant.Entity.Value };
            if (_executing.Count < Capacity) _executing.TryAdd(grant.Entity.Value, key);
            else Emit(key with { Stage = LatencyStage.Terminal, Outcome = LatencyOutcome.Capacity });
        }
    }

    internal void End()
    {
        foreach (var key in _executing.Values)
        {
            Emit(key with { Stage = LatencyStage.MovementDeferred, Outcome = LatencyOutcome.Deferred });
            Emit(key with { Stage = LatencyStage.Terminal, Outcome = LatencyOutcome.Deferred });
        }
        _executing.Clear(); _context = default;
    }

    internal void GoTo(int slot)
    {
        if (_active.Remove(slot, out var old) && !old.Moved) Terminal(old, LatencyOutcome.Superseded);
        var table = _world.Characters;
        var cold = table.Cold(slot)!;
        if (!_executing.Remove(cold.Entity.Value, out var key)) return;
        ref var hot = ref table.Next[slot];
        bool stationary = hot.Velocity.LengthSquared() < 1e-8f && hot.Task == (byte)CharacterTask.Idle
            && (hot.Flags & (ushort)MoveFlags.HasPath) == 0;
        Emit(key with { Stage = LatencyStage.MovementAccepted, Outcome = stationary ? LatencyOutcome.None : LatencyOutcome.NotStationary });
        if (_active.Count >= Capacity) { Emit(key with { Stage = LatencyStage.Terminal, Outcome = LatencyOutcome.Capacity }); return; }
        _active[slot] = new Sample(key, table.IdOf(slot), hot.Position, stationary);
    }

    internal void Stop(int slot)
    {
        if (_active.Remove(slot, out var sample) && !sample.Moved) Terminal(sample, LatencyOutcome.Censored);
    }

    internal void PathQueued(int slot, PathService paths, long path)
    {
        if (!_active.TryGetValue(slot, out var sample) || sample.Moved) return;
        sample.Key = sample.Key with { PathService = paths.TraceIncarnation, Path = path };
        Emit(sample.Key with { Stage = LatencyStage.PathBound });
    }

    internal bool Tracks(int slot) => _active.TryGetValue(slot, out var sample) && !sample.Moved;

    internal void Close(ulong connection, ulong generation)
    {
        foreach (var (slot, sample) in _active.Where(p => p.Value.Key.Connection == connection && p.Value.Key.Generation == generation).ToArray())
        {
            if (!sample.Moved) Terminal(sample, LatencyOutcome.Censored);
            _active.Remove(slot);
        }
    }

    internal void PathApplied(int slot, PathService paths, PathAnswer answer)
    {
        if (!_active.TryGetValue(slot, out var sample) || sample.Moved) return;
        // A reused old path can move a held-steering actor, but is not evidence for this request.
        if (sample.Key.PathService != paths.TraceIncarnation || sample.Key.Path != answer.Id) return;
        sample.PathApplied = !answer.Path.WaitingForNavigation;
        if (sample.PathApplied) sample.AppliedKey = sample.Key;
        Emit(sample.Key with
        {
            Stage = answer.Path.WaitingForNavigation ? LatencyStage.NavigationWait : LatencyStage.PathApplied,
            Outcome = answer.Path.WaitingForNavigation ? LatencyOutcome.NavigationWaiting : !answer.Path.Found ? LatencyOutcome.NoPath : LatencyOutcome.None
        });
    }

    internal void AfterTick()
    {
        foreach (var (slot, sample) in _active.ToArray())
        {
            if (sample.Moved) continue;
            var table = _world.Characters;
            if (!table.IsAlive(sample.Character)) { Terminal(sample, LatencyOutcome.Censored); _active.Remove(slot); continue; }
            var cold = table.Cold(slot)!;
            var hot = table.Previous[slot];
            if (sample.PathApplied && hot.Position != sample.Start)
            {
                if (!sample.Stationary) { Terminal(sample, LatencyOutcome.NotStationary); _active.Remove(slot); continue; }
                sample.Moved = true; sample.MotionTick = _world.Tick;
                sample.Key = sample.AppliedKey;
                Emit(sample.Key with { Stage = LatencyStage.AuthorityRootChanged, Revision = _world.StateRevision });
                Terminal(sample, LatencyOutcome.Moved);
            }
            else if (sample.PathApplied && cold.PathFailed) { Terminal(sample, LatencyOutcome.NoPath); _active.Remove(slot); }
            else if (hot.Task != (byte)CharacterTask.GoTo && !cold.NavigationWaiting)
            {
                Terminal(sample, cold.WalkEnd == WalkEnd.Finished ? LatencyOutcome.AlreadyAtTarget : LatencyOutcome.Censored);
                _active.Remove(slot);
            }
        }
    }

    internal void Publication(AuthorityConnection connection, PlayerWorldView view)
    {
        foreach (var sample in _active.Values)
        {
            if (!sample.Moved || sample.Key.Connection != connection.Player.Value || sample.Key.Generation != connection.Generation) continue;
            var character = view.Snapshot.Characters.FirstOrDefault(c => c.Id == sample.Character);
            if (character is null || character.Position == sample.Start) continue;
            // Explicit containment evidence, never inferred merely from publication tick ordering.
            Emit(sample.Key with
            {
                Stage = LatencyStage.PublicationContainsMotion,
                Publication = view.Publication,
                Revision = _world.StateRevision,
                Value = sample.MotionTick
            });
        }
    }

    void Terminal(Sample sample, LatencyOutcome outcome) => Emit(sample.Key with { Stage = LatencyStage.Terminal, Outcome = outcome });

    public void Dispose()
    {
        foreach (var sample in _active.Values) if (!sample.Moved) Terminal(sample, LatencyOutcome.Censored);
        _active.Clear(); _executing.Clear(); _world.Characters.MovementTrace = null;
        foreach (var movement in _world.Systems.OfType<MovementSystem>()) movement.Paths.TraceSink = null;
    }
}