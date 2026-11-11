using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Where a zone's mesh came from.
/// </summary>
public enum NavMeshOrigin
{
    Cache,
    Built
}

/// <summary>
/// What a worker reports for one finished zone.
/// </summary>
public sealed record NavZoneReady(ZoneCoordinate Zone, NavMeshOrigin Origin, double Milliseconds, int Polygons);
/// <summary>
/// Builds zone meshes on a worker from immutable building and foliage inputs. Deferred mode keeps prepared results private until
/// CommitPrepared publishes them at the serialized world boundary; immediate mode commits on worker completion. Publication atomically replaces
/// the walkability world. Request generations and world incarnations fence stale completions, unloads and replacement worlds.
/// </summary>
internal sealed class NavMeshService : IDisposable
{
    /// <summary>
    /// The game install the gatherer reads the terrain and collision files from.
    /// </summary>
    readonly GameInstall? _install;
    /// <summary>
    /// The game data the gatherer reads the records from.
    /// </summary>
    readonly GameDatabase? _db;
    /// <summary>
    /// The level data whose placed buildings the zones are gathered with.
    /// </summary>
    readonly WorldLevelData? _levels;
    /// <summary>
    /// The collision files read so far, shared by every gather.
    /// </summary>
    readonly CollisionCache? _collisions;
    /// <summary>
    /// The player's buildings to gather with the level data's; null: none.
    /// </summary>
    readonly NavRuntimeBuildings? _runtime;
    /// <summary>
    /// The zones waiting for the worker, in request order; a zone is added once while it is pending.
    /// </summary>
    readonly BlockingCollection<Attempt> _queue = [];
    /// <summary>
    /// The task of each requested zone until it is published, unloaded or fails; a zone not in it is not wanted any more.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, RequestState> _pending = [];
    /// <summary>
    /// The worker thread that loads and builds the queued zones (<see cref = "Work"/>), below normal priority.
    /// </summary>
    readonly Thread _worker;
    /// <summary>
    /// The loaded zones' meshes with their pruned polygons, for resolving the kept regions (worker thread, KeepAt and Unload, under _gate).
    /// </summary>
    readonly Dictionary<ZoneCoordinate, ZoneNavMesh> _full = [];
    /// <summary>
    /// The kept sets the loaded world was published with, and the points whose regions KeepAt keeps.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, bool[]> _live = [];
    /// <summary>
    /// The points by zone whose regions <see cref = "KeepAt"/> was asked to keep, resolved again with every publish.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, List<Vector3>> _standing = [];
    /// <summary>
    /// Set by <see cref = "Dispose"/>: the worker stops at its next zone.
    /// </summary>
    bool _disposed;
    readonly Func<ZoneCoordinate, NavRuntimeBuildings?, NavBuildResult>? _build;
    readonly bool _deferred;
    readonly object _gate = new();
    readonly Dictionary<ZoneCoordinate, RequestState> _failed = [];
    readonly HashSet<ZoneCoordinate> _dirty = [], _removed = [];
    readonly HashSet<Vector3> _keepRequests = [];
    readonly Dictionary<Vector3, long> _keepChecked = [];
    long _generation, _incarnation, _publication;
    bool _keptDirty;
    Exception? _failedStartup;
    public NavMeshService(GameInstall install, GameDatabase db, WorldLevelData levels, NavmeshWalkability walkability, NavBuildSettings? settings = null, NavMeshCache? cache = null, NavRuntimeBuildings? runtime = null, bool deferPublication = false)
    {
        this._install = install;
        this._db = db;
        this._levels = levels;
        this._runtime = runtime;
        _collisions = new(install);
        Walkability = walkability;
        Settings = settings ?? new();
        Cache = cache ?? new();
        _deferred = deferPublication;
        _worker = Start();
    }

    internal NavMeshService(NavmeshWalkability walkability, Func<ZoneCoordinate, NavRuntimeBuildings?, NavBuildResult> build, bool deferPublication = true, NavRuntimeBuildings? runtime = null)
    {
        Walkability = walkability;
        this._build = build;
        this._runtime = runtime;
        Settings = new();
        Cache = new();
        _deferred = deferPublication;
        _worker = Start();
    }

    /// <summary>
    /// The walkability the finished zones are published to.
    /// </summary>
    public NavmeshWalkability Walkability { get; }
    /// <summary>
    /// The build settings the zones are built and cached with (thread count aside, <see cref = "BuildSettings"/>).
    /// </summary>
    public NavBuildSettings Settings { get; }
    /// <summary>
    /// A loading screen covers the game (the host sets it): a zone's tiles are built on every core but one. Uncovered, on a quarter of them: the tile
    /// builds run on the shared pool at normal priority and allocate gigabytes a zone, and on every core they starved the simulation and the render
    /// thread for 100-200 ms frames.
    /// </summary>
    public bool Covered { get; set; } = true;

    /// <summary>
    /// <see cref = "Settings"/> with a quarter of the cores while not <see cref = "Covered"/> and the settings leave the thread count to the build.
    /// </summary>
    NavBuildSettings BuildSettings
    {
        get
        {
            if (Covered || Settings.Threads > 0)
                return Settings;
            return Settings with
            {
                Threads = Math.Max(1, Environment.ProcessorCount / 4)
            };
        }
    }

    /// <summary>
    /// The cache the worker loads zones from and saves built ones to.
    /// </summary>
    public NavMeshCache Cache { get; }

    /// <summary>
    /// Raised by the committer after publication; the worker only in immediate mode.
    /// </summary>
    public event Action<NavZoneReady>? ZoneReady;
    /// <summary>
    /// Queues a zone (once); the task completes when its mesh is published.
    /// </summary>
    public Task<NavZoneReady> Request(ZoneCoordinate zone) => Request(zone, prepared: false);
    /// <summary>
    /// Builds a zone again although its mesh is loaded (the buildings round it changed, <see cref = "NavRuntimeBuildings"/>): the new mesh replaces the
    /// old one when it is published, which is when the task completes. A zone not loaded is not built (it is built with the overlay when it is asked
    /// for).
    /// </summary>
    public Task<NavZoneReady> Rebuild(ZoneCoordinate zone)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_removed.Contains(zone))
                return Task.FromCanceled<NavZoneReady>(new CancellationToken(true));
            bool retry = _failed.Remove(zone);
            if (_pending.TryGetValue(zone, out var request))
            {
                request.Generation = checked(++_generation);
                request.Result = null;
                if (request.Prepared.Task.IsCompleted)
                    request.Prepared = Completion();
                Queue(request);
            }
            else if (retry || Walkability.World.Contains(zone))
                request = Issue(zone);
            else
                return Task.FromCanceled<NavZoneReady>(new CancellationToken(true));
            _dirty.Add(zone);
            return request.Published.Task;
        }
    }

    /// <summary>
    /// Keeps the region of a loaded zone's pruned polygons that <paramref name = "at"/> stands on, and what it reaches across the loaded borders, and
    /// publishes it: a walker on ground its zone's own seeds do not reach, whose way to a seed leads through a zone that is not loaded (a save loaded
    /// in the middle of a zone, docs/game/pathfinding.md "Zone borders"). Not cached: the cache holds the zone's own pruning. True when the kept set
    /// grew.
    /// </summary>
    public bool KeepAt(Vector3 at) => KeepAt(at, Incarnation);
    /// <summary>
    /// Drops a zone's mesh from the loaded world (the cache file stays). A queued build of it is skipped and a running one is cached but not
    /// published; their task is cancelled.
    /// </summary>
    public void Unload(ZoneCoordinate zone) => Unload([zone]);
    /// <summary>
    /// Drops several zones at once: the world is published again once (the combined mesh is rebuilt on each publish, tens of milliseconds), not
    /// per zone.
    /// </summary>
    public void Unload(IReadOnlyCollection<ZoneCoordinate> zones)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var zone in zones)
            {
                if (_pending.Remove(zone, out var request))
                    Cancel(request);
                _failed.Remove(zone);
                _removed.Add(zone);
                _dirty.Remove(zone);
            }
        }

        if (!_deferred)
            CommitPrepared();
    }

    /// <summary>
    /// The worker loop: for each queued zone still pending, the cached mesh or a gathered and built one (saved to the cache), published under
    /// <see cref="_gate"/> with its neighbours' kept regions resolved again; queued again when a <see cref = "Rebuild"/> came while it
    /// was built.
    /// </summary>
    void Work()
    {
        try
        {
            using var gatherer = _build is null ? new ZoneGeometryGatherer(_install!, _db!, _levels!, _collisions!) : null;
            uint settingsHash = _build is null ? NavMeshCache.SettingsHash(Settings) * 16777619 ^ NavMeshCache.ContentHash(_install!, _db!) : 0;
            foreach (var attempt in _queue.GetConsumingEnumerable())
            {
                NavRuntimeBuildings? snapshot;
                lock (_gate)
                {
                    if (!Current(attempt))
                        continue;
                    snapshot = _runtime?.Snapshot();
                }

                try
                {
                    var watch = Stopwatch.StartNew();
                    NavBuildResult result;
                    if (_build is not null)
                        result = _build(attempt.Request.Zone, snapshot);
                    else
                    {
                        gatherer!.UseRuntimeSnapshot(snapshot);
                        var zone = attempt.Request.Zone;
                        uint hash = gatherer.BuildingHash(zone);
                        var mesh = Cache.TryLoad(zone.X, zone.Y, hash, settingsHash);
                        var origin = NavMeshOrigin.Cache;
                        if (mesh is null)
                        {
                            var geometry = gatherer.Gather(zone, buildingHash: hash);
                            mesh = NavMeshPipeline.BuildZone(gatherer, geometry, BuildSettings, out _);
                            origin = NavMeshOrigin.Built;
                            try
                            {
                                Cache.Save(mesh, hash, settingsHash);
                            }
                            catch (IOException)
                            {
                            }
                        }

                        mesh.Terrain = gatherer.ZoneHeights(zone);
                        result = new(mesh, origin, watch.Elapsed.TotalMilliseconds);
                    }

                    lock (_gate)
                    {
                        if (!Current(attempt))
                            continue;
                        attempt.Request.Result = result;
                        attempt.Request.Prepared.TrySetResult(new(attempt.Request.Zone, result.Origin, result.Milliseconds, result.Mesh.PolygonCount));
                    }

                    if (!_deferred)
                        CommitPrepared();
                }
                catch (Exception error)
                {
                    lock (_gate)
                    {
                        if (!Current(attempt))
                            continue;
                        _pending.Remove(attempt.Request.Zone);
                        _failed[attempt.Request.Zone] = attempt.Request;
                        attempt.Request.Prepared.TrySetException(error);
                        attempt.Request.Published.TrySetException(error);
                    }
                }
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _failedStartup = error;
                foreach (var request in _pending.Values)
                {
                    request.Prepared.TrySetException(error);
                    request.Published.TrySetException(error);
                }

                _pending.Clear();
            }
        }
    }

    /// <summary>
    /// Stops the worker (waiting up to 30 s for its zone) and cancels the zones still pending.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var request in _pending.Values)
                Cancel(request);
            _pending.Clear();
            _queue.CompleteAdding();
        }

        if (Thread.CurrentThread != _worker)
            _worker.Join();
        _queue.Dispose();
    }

    sealed class RequestState(ZoneCoordinate zone, long generation, long incarnation)
    {
        public readonly ZoneCoordinate Zone = zone;
        public long Generation = generation;
        public readonly long Incarnation = incarnation;
        public readonly TaskCompletionSource<NavZoneReady> Published = Completion();
        public TaskCompletionSource<NavZoneReady> Prepared = Completion();
        public NavBuildResult? Result;
    }

    readonly record struct Attempt(RequestState Request, long Generation, long Incarnation);
    Thread Start()
    {
        var thread = new Thread(Work)
        {
            Name = "NavMeshBuilder",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        thread.Start();
        return thread;
    }

    static TaskCompletionSource<NavZoneReady> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Incarnation
    {
        get
        {
            lock (_gate)
                return _incarnation;
        }
    }

    public IReadOnlyCollection<ZoneCoordinate> TrackedZones
    {
        get
        {
            lock (_gate)
                return _full.Keys.Concat(_pending.Keys).Concat(_failed.Keys).Distinct().ToArray();
        }
    }

    public bool IsReady(ZoneCoordinate zone)
    {
        lock (_gate)
            return !_dirty.Contains(zone) && !_removed.Contains(zone) && Walkability.World.Contains(zone);
    }

    public Task<NavZoneReady> Prepare(ZoneCoordinate zone) => Request(zone, prepared: true);
    Task<NavZoneReady> Request(ZoneCoordinate zone, bool prepared)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failedStartup is { } failure)
                return Task.FromException<NavZoneReady>(failure);
            if (_failed.TryGetValue(zone, out var stopped))
                return prepared ? stopped.Prepared.Task : stopped.Published.Task;
            if (!_dirty.Contains(zone) && !_removed.Contains(zone) && Walkability.World.Find(zone)is { } loaded)
                return Task.FromResult(new NavZoneReady(zone, NavMeshOrigin.Cache, 0, loaded.PolygonCount));
            _removed.Remove(zone);
            if (!_pending.TryGetValue(zone, out var request))
                request = Issue(zone);
            return prepared ? request.Prepared.Task : request.Published.Task;
        }
    }

    RequestState Issue(ZoneCoordinate zone)
    {
        var request = new RequestState(zone, checked(++_generation), _incarnation);
        _pending.Add(zone, request);
        Queue(request);
        return request;
    }

    void Queue(RequestState request) => _queue.Add(new(request, request.Generation, request.Incarnation));
    bool Current(Attempt attempt) => !_disposed && _incarnation == attempt.Incarnation && _pending.TryGetValue(attempt.Request.Zone, out var current) && ReferenceEquals(current, attempt.Request) && current.Generation == attempt.Generation;
    /// <summary>
    /// Invalidate old-world tickets while retaining unchanged published static meshes.
    /// </summary>
    public void NewIncarnation(bool requeuePending = true)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _incarnation = checked(_incarnation + 1);
            var wanted = requeuePending ? _pending.Keys.Concat(_failed.Keys).Distinct().ToArray() : [];
            foreach (var request in _pending.Values)
                Cancel(request);
            _pending.Clear();
            _failed.Clear();
            _keepRequests.Clear();
            _keepChecked.Clear();
            _standing.Clear();
            _live.Clear();
            _keptDirty = true;
            foreach (var zone in wanted)
                Issue(zone);
        }
    }

    static void Cancel(RequestState request)
    {
        request.Prepared.TrySetCanceled();
        request.Published.TrySetCanceled();
    }

    public bool KeepAt(Vector3 at, long expectedIncarnation)
    {
        lock (_gate)
        {
            if (_disposed || expectedIncarnation != _incarnation || !IsReady(WorldLayout.ZoneOf(at.X, at.Z)) || _keepChecked.GetValueOrDefault(at, -1) == _publication)
                return false;
            _keepRequests.Add(at);
        }

        return _deferred || CommitPrepared().Count > 0;
    }

    /// <summary>
    /// Only this method changes published meshes, including unloads and kept-region expansion.
    /// </summary>
    public IReadOnlySet<ZoneCoordinate> CommitPrepared()
    {
        var changed = new HashSet<ZoneCoordinate>();
        var completed = new List<(RequestState Request, NavBuildResult Result)>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var world = Walkability.World;
            foreach (var zone in _removed.Concat(_deferred ? _dirty : []).Distinct())
            {
                if (world.Contains(zone))
                {
                    world = world.Without(zone);
                    changed.Add(zone);
                }

                _keptDirty |= _full.Remove(zone);
                _live.Remove(zone);
                _standing.Remove(zone);
            }

            _removed.Clear();
            foreach (var request in _pending.Values.Where(r => r.Result is not null).OrderBy(r => r.Zone.X).ThenBy(r => r.Zone.Y).ToArray())
            {
                var result = request.Result!;
                _full[request.Zone] = result.Mesh;
                _keptDirty = true;
                _live.Remove(request.Zone);
                _dirty.Remove(request.Zone);
                _pending.Remove(request.Zone);
                completed.Add((request, result));
            }

            foreach (var at in _keepRequests)
            {
                var zone = WorldLayout.ZoneOf(at.X, at.Z);
                if (!_full.TryGetValue(zone, out var mesh) || !world.Contains(zone) || !_live.TryGetValue(zone, out var kept))
                    continue;
                var test = (bool[])kept.Clone();
                if (!SeedPruner.Extend(mesh, test, [at], Settings))
                    continue;
                if (!_standing.TryGetValue(zone, out var points))
                    _standing[zone] = points = [];
                points.Add(at);
                _keptDirty = true;
            }

            // Most boundaries have no navigation changes. Re-solving every loaded border
            // here monopolizes the world owner even when the published mesh is unchanged.
            if (_keptDirty)
            {
                foreach (var(zone, keep)in ZoneKeptClosure.Resolve(_full, Settings, _standing))
                {
                    if (_live.TryGetValue(zone, out var before) && before.AsSpan().SequenceEqual(keep) && world.Contains(zone))
                        continue;
                    _live[zone] = keep;
                    world = world.With(_full[zone].WithKept(keep).WithoutPruned());
                    changed.Add(zone);
                }

                _keptDirty = false;
            }

            if (changed.Count > 0)
            {
                Walkability.SetWorld(world);
                _publication = checked(_publication + 1);
                _keepChecked.Clear();
            }

            foreach (var at in _keepRequests)
                _keepChecked[at] = _publication;
            _keepRequests.Clear();
        }

        foreach (var(request, result)in completed)
        {
            var ready = new NavZoneReady(request.Zone, result.Origin, result.Milliseconds, Walkability.World.Find(request.Zone)?.PolygonCount ?? result.Mesh.PolygonCount);
            request.Published.TrySetResult(ready);
            ZoneReady?.Invoke(ready);
        }

        return changed;
    }
}

internal sealed record NavBuildResult(ZoneNavMesh Mesh, NavMeshOrigin Origin, double Milliseconds = 0);