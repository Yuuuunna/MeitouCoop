using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Diagnostics;

namespace Meitou.Simulation.Movement;

/// <summary>
/// A path wanted by one character.
/// </summary>
public readonly record struct PathRequest(
    long Id, CharacterId Character, Vector3 From, Vector3 To, float Radius = 0, WaterCost Water = default, float RoadPreference = 0);

/// <summary>
/// The answer to a <see cref="PathRequest"/>: the path (empty when none).
/// </summary>
public readonly record struct PathAnswer(long Id, CharacterId Character, PathResult Path);

/// <summary>
/// Runs path queries off the tick (docs/simulation.md, "Threading": asynchronous services). In <c>synchronous</c> mode a query runs inside
/// <see cref="Submit"/> and its answer is waiting at once, which is what the determinism tests use; otherwise <see cref="Threads"/> threads of its
/// own answer, and an answer is taken in at the first tick after it is ready, so timing can differ between runs (as for the original, whose queries
/// go to a NavMesh thread). Queued requests coalesce per character without losing their original place. Player queries have bounded priority over
/// NPC work; one worker stays reserved for players, so slow NPC searches cannot occupy every worker. Running searches finish normally.
/// <see cref="IWalkability.FindPath"/> must be safe to call from several threads at once.
/// </summary>
public sealed class PathService : IDisposable
{
    /// <summary>
    /// The surface the paths are found on (its <c>FindPath</c> runs on the path threads).
    /// </summary>
    readonly IWalkability _walkability;
    /// <summary>
    /// The requests waiting for a path thread; null for a synchronous service.
    /// </summary>
    readonly PathRequestQueue? _requests;
    /// <summary>
    /// The answers ready, taken by <see cref="Drain"/>.
    /// </summary>
    readonly ConcurrentQueue<PathAnswer> _answers = new();
    /// <summary>
    /// Bounded path IDs whose queue, execution and terminal stages are being sampled.
    /// </summary>
    readonly ConcurrentDictionary<long, byte> _traced = new();

    /// <summary>
    /// The path threads (none for a synchronous service).
    /// </summary>
    readonly Thread[] _threads = [];
    /// <summary>
    /// The last request id handed out (ids count from 1), and the requests dropped as replaced.
    /// </summary>
    long _nextId, _skipped;
    /// <summary>
    /// Requests asked and not answered or dropped yet.
    /// </summary>
    int _pending;
    /// <summary>
    /// Set by <see cref="Dispose"/>, which runs once.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// The path threads of an asynchronous service (engine choice: a quarter of the cores, 2 to 4; the simulation's own workers take half).
    /// </summary>
    public static int Threads => Math.Clamp(Environment.ProcessorCount / 4, 2, 4);

    /// <summary>
    /// A path service over <paramref name="walkability"/>: <paramref name="synchronous"/> answers each request at once on the caller's thread;
    /// otherwise <see cref="Threads"/> background threads answer them.
    /// </summary>
    public PathService(IWalkability walkability, bool synchronous)
    {
        this._walkability = walkability;
        IsSynchronous = synchronous;
        if (synchronous) return;

        _requests = new PathRequestQueue();
        _threads = new Thread[Threads];
        for (int i = 0; i < _threads.Length; i++)
        {
            bool playerOnly = i == 0;
            _threads[i] = new Thread(() => Work(playerOnly)) { IsBackground = true, Name = $"Meitou.Sim paths {i + 1}" };
            _threads[i].Start();
        }
    }

    /// <summary>
    /// Whether requests are answered at once on the caller's thread (no path threads).
    /// </summary>
    public bool IsSynchronous { get; }

    /// <summary>
    /// Queries asked so far.
    /// </summary>
    public long Submitted => Interlocked.Read(ref _nextId);

    /// <summary>
    /// Requests dropped because the same character asked again before a thread took them up.
    /// </summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    /// <summary>
    /// Requests asked and not answered or dropped yet (queued or being searched).
    /// </summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>
    /// Asks for a path; returns the request id.
    /// </summary>
    public long Submit(CharacterId character, Vector3 from, Vector3 to, float radius = 0, WaterCost water = default, float roadPreference = 0)
    => SubmitObserved(character, from, to, radius, water, roadPreference, observeLatency: false);

    /// <summary>
    /// The path for one request: through <see cref="IAgentWalkability"/> when the request has a radius, water costs or a road preference and the
    /// surface takes them, else the plain query.
    /// </summary>
    PathAnswer Answer(PathRequest r)
    {
        Trace(LatencyStage.PathStarted, r.Id);
        try
        {
            var path = (r.Radius > 0 || r.Water.IsSet || r.RoadPreference > 0) && _walkability is IAgentWalkability agent
                ? agent.FindPath(r.From, r.To, r.Radius, r.Water, r.RoadPreference) : _walkability.FindPath(r.From, r.To);
            var outcome = path.WaitingForNavigation ? LatencyOutcome.NavigationWaiting
                : !path.Found ? LatencyOutcome.NoPath
                : LatencyOutcome.None;
            Trace(LatencyStage.PathFinished, r.Id, outcome);
            return new(r.Id, r.Character, path);
        }
        finally { _traced.TryRemove(r.Id, out _); }
    }

    /// <summary>
    /// A path thread's loop: answers the latest queued payload, optionally reserving this worker for players only.
    /// </summary>
    void Work(bool playerOnly)
    {
        while (_requests!.Take(playerOnly) is { } request)
        {
            _answers.Enqueue(Answer(request));
            Interlocked.Decrement(ref _pending);
        }
    }

    /// <summary>
    /// The answers ready now, by request id.
    /// </summary>
    public List<PathAnswer> Drain()
    {
        var list = new List<PathAnswer>();
        while (_answers.TryDequeue(out var a))
            list.Add(a);

        list.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    /// <summary>
    /// Stops the threads; safe to call twice (the owning system and the host may both dispose it).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _requests?.Complete();
        foreach (var thread in _threads)
            thread.Join();
    }

    /// <summary>
    /// Distinguishes requests from previous path-service lifetimes in diagnostic traces.
    /// </summary>
    public Guid TraceIncarnation { get; } = Guid.NewGuid();

    /// <summary>
    /// Optional nonblocking observer; never controls scheduling or search results.
    /// </summary>
    public ILatencyTraceSink? TraceSink { get; set; }

    /// <summary>
    /// Emits stages only for explicitly sampled path requests.
    /// </summary>
    void Trace(LatencyStage stage, long id, LatencyOutcome outcome = LatencyOutcome.None)
    {
        if (TraceSink is { } sink && _traced.ContainsKey(id))
            LatencyTrace.Emit(sink, new(stage, PathService: TraceIncarnation, Path: id, Outcome: outcome));
    }

    /// <summary>
    /// Submits a query with optional latency observation and player priority; synchronous queries retain caller-thread ordering.
    /// </summary>
    internal long SubmitObserved(CharacterId character, Vector3 from, Vector3 to, float radius, WaterCost water, float roadPreference,
        bool observeLatency, bool urgent = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long id = Interlocked.Increment(ref _nextId);
        var request = new PathRequest(id, character, from, to, radius, water, roadPreference);
        if (observeLatency && TraceSink is not null)
        {
            if (_traced.Count < 4096) _traced.TryAdd(id, 0);
            else LatencyTrace.Emit(TraceSink, new(LatencyStage.Terminal, PathService: TraceIncarnation, Path: id, Outcome: LatencyOutcome.Capacity));
        }
        Trace(LatencyStage.PathQueued, id);
        if (IsSynchronous) { _answers.Enqueue(Answer(request)); return id; }
        Interlocked.Increment(ref _pending);
        if (_requests!.Enqueue(request, urgent) is { } replaced)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _skipped);
            Trace(LatencyStage.Terminal, replaced.Id, LatencyOutcome.Superseded);
            _traced.TryRemove(replaced.Id, out _);
        }
        return id;
    }
}