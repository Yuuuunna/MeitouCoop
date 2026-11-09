using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Navigation;

public enum NavMeshOrigin { Cache, Built }
public sealed record NavZoneReady(ZoneCoordinate Zone, NavMeshOrigin Origin, double Milliseconds, int Polygons);
internal sealed record NavBuildResult(ZoneNavMesh Mesh, NavMeshOrigin Origin, double Milliseconds = 0);

/// <summary>Versioned background generation. Deferred mode publishes only through CommitPrepared on the world thread.</summary>
internal sealed class NavMeshService : IDisposable
{
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
    readonly GameInstall? install;
    readonly GameDatabase? db;
    readonly WorldLevelData? levels;
    readonly CollisionCache? collisions;
    readonly NavRuntimeBuildings? runtime;
    readonly Func<ZoneCoordinate, NavRuntimeBuildings?, NavBuildResult>? build;
    readonly bool deferred;
    readonly object gate = new();
    readonly BlockingCollection<Attempt> queue = [];
    readonly Dictionary<ZoneCoordinate, RequestState> pending = [];
    readonly Dictionary<ZoneCoordinate, RequestState> failed = [];
    readonly Dictionary<ZoneCoordinate, ZoneNavMesh> full = [];
    readonly HashSet<ZoneCoordinate> dirty = [], removed = [];
    readonly HashSet<Vector3> keepRequests = [];
    readonly Dictionary<ZoneCoordinate, bool[]> live = [];
    readonly Dictionary<ZoneCoordinate, List<Vector3>> standing = [];
    readonly Dictionary<Vector3, long> keepChecked = [];
    readonly Thread worker;
    long generation, incarnation, publication;
    bool keptDirty;
    bool disposed;
    Exception? failedStartup;

    public NavMeshService(GameInstall install, GameDatabase db, WorldLevelData levels, NavmeshWalkability walkability,
        NavBuildSettings? settings = null, NavMeshCache? cache = null, NavRuntimeBuildings? runtime = null, bool deferPublication = false)
    {
        this.install = install; this.db = db; this.levels = levels; this.runtime = runtime;
        collisions = new(install); Walkability = walkability; Settings = settings ?? new(); Cache = cache ?? new(); deferred = deferPublication;
        worker = Start();
    }

    internal NavMeshService(NavmeshWalkability walkability, Func<ZoneCoordinate, NavRuntimeBuildings?, NavBuildResult> build,
        bool deferPublication = true, NavRuntimeBuildings? runtime = null)
    {
        Walkability = walkability; this.build = build; this.runtime = runtime;
        Settings = new(); Cache = new(); deferred = deferPublication; worker = Start();
    }

    Thread Start()
    {
        var thread = new Thread(Work) { Name = "NavMeshBuilder", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        thread.Start(); return thread;
    }
    static TaskCompletionSource<NavZoneReady> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public NavmeshWalkability Walkability { get; }
    public NavBuildSettings Settings { get; }

    /// <summary>
    /// A loading screen covers the game (the host sets it): a zone's tiles are built on every core but one. Uncovered, on a quarter of them: the tile builds run on
    /// the shared pool at normal priority and allocate gigabytes a zone, and on every core they starved the simulation and the render thread for 100-200 ms frames.
    /// </summary>
    public bool Covered { get; set; } = true;

    NavBuildSettings BuildSettings => Covered || Settings.Threads > 0 ? Settings : Settings with { Threads = Math.Max(1, Environment.ProcessorCount / 4) };
    public NavMeshCache Cache { get; }
    public event Action<NavZoneReady>? ZoneReady;
    public long Incarnation { get { lock (gate) return incarnation; } }
    public IReadOnlyCollection<ZoneCoordinate> TrackedZones
    {
        get { lock (gate) return full.Keys.Concat(pending.Keys).Concat(failed.Keys).Distinct().ToArray(); }
    }

    public bool IsReady(ZoneCoordinate zone)
    {
        lock (gate) return !dirty.Contains(zone) && !removed.Contains(zone) && Walkability.World.Contains(zone);
    }

    public Task<NavZoneReady> Request(ZoneCoordinate zone) => Request(zone, prepared: false);
    public Task<NavZoneReady> Prepare(ZoneCoordinate zone) => Request(zone, prepared: true);

    Task<NavZoneReady> Request(ZoneCoordinate zone, bool prepared)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (failedStartup is { } failure) return Task.FromException<NavZoneReady>(failure);
            if (failed.TryGetValue(zone, out var stopped)) return prepared ? stopped.Prepared.Task : stopped.Published.Task;
            if (!dirty.Contains(zone) && !removed.Contains(zone) && Walkability.World.Find(zone) is { } loaded)
                return Task.FromResult(new NavZoneReady(zone, NavMeshOrigin.Cache, 0, loaded.PolygonCount));
            removed.Remove(zone);
            if (!pending.TryGetValue(zone, out var request)) request = Issue(zone);
            return prepared ? request.Prepared.Task : request.Published.Task;
        }
    }

    RequestState Issue(ZoneCoordinate zone)
    {
        var request = new RequestState(zone, checked(++generation), incarnation);
        pending.Add(zone, request); Queue(request); return request;
    }
    void Queue(RequestState request) => queue.Add(new(request, request.Generation, request.Incarnation));
    bool Current(Attempt attempt) => !disposed && incarnation == attempt.Incarnation
        && pending.TryGetValue(attempt.Request.Zone, out var current) && ReferenceEquals(current, attempt.Request)
        && current.Generation == attempt.Generation;

    public Task<NavZoneReady> Rebuild(ZoneCoordinate zone)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (removed.Contains(zone)) return Task.FromCanceled<NavZoneReady>(new CancellationToken(true));
            bool retry = failed.Remove(zone);
            if (pending.TryGetValue(zone, out var request))
            {
                request.Generation = checked(++generation); request.Result = null;
                if (request.Prepared.Task.IsCompleted) request.Prepared = Completion();
                Queue(request);
            }
            else if (retry || Walkability.World.Contains(zone)) request = Issue(zone);
            else return Task.FromCanceled<NavZoneReady>(new CancellationToken(true));
            dirty.Add(zone); return request.Published.Task;
        }
    }

    public void Unload(ZoneCoordinate zone) => Unload([zone]);

    public void Unload(IReadOnlyCollection<ZoneCoordinate> zones)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var zone in zones)
            {
                if (pending.Remove(zone, out var request)) Cancel(request);
                failed.Remove(zone);
                removed.Add(zone); dirty.Remove(zone);
            }
        }
        if (!deferred) CommitPrepared();
    }

    /// <summary>Invalidate old-world tickets while retaining unchanged published static meshes.</summary>
    public void NewIncarnation(bool requeuePending = true)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            incarnation = checked(incarnation + 1);
            var wanted = requeuePending ? pending.Keys.Concat(failed.Keys).Distinct().ToArray() : [];
            foreach (var request in pending.Values) Cancel(request);
            pending.Clear(); failed.Clear(); keepRequests.Clear(); keepChecked.Clear(); standing.Clear(); live.Clear();
            keptDirty = true;
            foreach (var zone in wanted) Issue(zone);
        }
    }
    static void Cancel(RequestState request) { request.Prepared.TrySetCanceled(); request.Published.TrySetCanceled(); }

    public bool KeepAt(Vector3 at) => KeepAt(at, Incarnation);

    public bool KeepAt(Vector3 at, long expectedIncarnation)
    {
        lock (gate)
        {
            if (disposed || expectedIncarnation != incarnation || !IsReady(WorldLayout.ZoneOf(at.X, at.Z)) || keepChecked.GetValueOrDefault(at, -1) == publication) return false;
            keepRequests.Add(at);
        }
        return deferred || CommitPrepared().Count > 0;
    }

    /// <summary>Only this method changes published meshes, including unloads and kept-region expansion.</summary>
    public IReadOnlySet<ZoneCoordinate> CommitPrepared()
    {
        var changed = new HashSet<ZoneCoordinate>();
        var completed = new List<(RequestState Request, NavBuildResult Result)>();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var world = Walkability.World;
            foreach (var zone in removed.Concat(deferred ? dirty : []).Distinct())
            {
                if (world.Contains(zone)) { world = world.Without(zone); changed.Add(zone); }
                keptDirty |= full.Remove(zone); live.Remove(zone); standing.Remove(zone);
            }
            removed.Clear();
            foreach (var request in pending.Values.Where(r => r.Result is not null).OrderBy(r => r.Zone.X).ThenBy(r => r.Zone.Y).ToArray())
            {
                var result = request.Result!;
                full[request.Zone] = result.Mesh;
                keptDirty = true;
                live.Remove(request.Zone);
                dirty.Remove(request.Zone); pending.Remove(request.Zone); completed.Add((request, result));
            }
            foreach (var at in keepRequests)
            {
                var zone = WorldLayout.ZoneOf(at.X, at.Z);
                if (!full.TryGetValue(zone, out var mesh) || !world.Contains(zone) || !live.TryGetValue(zone, out var kept)) continue;
                var test = (bool[])kept.Clone();
                if (!SeedPruner.Extend(mesh, test, [at], Settings)) continue;
                if (!standing.TryGetValue(zone, out var points)) standing[zone] = points = [];
                points.Add(at);
                keptDirty = true;
            }
            // Most boundaries have no navigation changes. Re-solving every loaded border
            // here monopolizes the world owner even when the published mesh is unchanged.
            if (keptDirty)
            {
                foreach (var (zone, keep) in ZoneKeptClosure.Resolve(full, Settings, standing))
                {
                    if (live.TryGetValue(zone, out var before) && before.AsSpan().SequenceEqual(keep) && world.Contains(zone)) continue;
                    live[zone] = keep;
                    world = world.With(full[zone].WithKept(keep).WithoutPruned());
                    changed.Add(zone);
                }
                keptDirty = false;
            }
            if (changed.Count > 0) { Walkability.SetWorld(world); publication = checked(publication + 1); keepChecked.Clear(); }
            foreach (var at in keepRequests) keepChecked[at] = publication;
            keepRequests.Clear();
        }
        foreach (var (request, result) in completed)
        {
            var ready = new NavZoneReady(request.Zone, result.Origin, result.Milliseconds,
                Walkability.World.Find(request.Zone)?.PolygonCount ?? result.Mesh.PolygonCount);
            request.Published.TrySetResult(ready);
            ZoneReady?.Invoke(ready);
        }
        return changed;
    }

    void Work()
    {
        try
        {
            using var gatherer = build is null ? new ZoneGeometryGatherer(install!, db!, levels!, collisions!) : null;
            uint settingsHash = build is null ? NavMeshCache.SettingsHash(Settings) * 16777619 ^ NavMeshCache.ContentHash(install!, db!) : 0;
            foreach (var attempt in queue.GetConsumingEnumerable())
            {
                NavRuntimeBuildings? snapshot;
                lock (gate) { if (!Current(attempt)) continue; snapshot = runtime?.Snapshot(); }
                try
                {
                    var watch = Stopwatch.StartNew();
                    NavBuildResult result;
                    if (build is not null) result = build(attempt.Request.Zone, snapshot);
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
                            mesh = NavMeshPipeline.BuildZone(gatherer, geometry, BuildSettings, out _); origin = NavMeshOrigin.Built;
                            try { Cache.Save(mesh, hash, settingsHash); } catch (IOException) { }
                        }
                        mesh.Terrain = gatherer.ZoneHeights(zone);
                        result = new(mesh, origin, watch.Elapsed.TotalMilliseconds);
                    }
                    lock (gate)
                    {
                        if (!Current(attempt)) continue;
                        attempt.Request.Result = result;
                        attempt.Request.Prepared.TrySetResult(new(attempt.Request.Zone, result.Origin, result.Milliseconds, result.Mesh.PolygonCount));
                    }
                    if (!deferred) CommitPrepared();
                }
                catch (Exception error)
                {
                    lock (gate)
                    {
                        if (!Current(attempt)) continue;
                        pending.Remove(attempt.Request.Zone);
                        failed[attempt.Request.Zone] = attempt.Request;
                        attempt.Request.Prepared.TrySetException(error); attempt.Request.Published.TrySetException(error);
                    }
                }
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                failedStartup = error;
                foreach (var request in pending.Values) { request.Prepared.TrySetException(error); request.Published.TrySetException(error); }
                pending.Clear();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; foreach (var request in pending.Values) Cancel(request); pending.Clear(); queue.CompleteAdding();
        }
        if (Thread.CurrentThread != worker) worker.Join();
        queue.Dispose();
    }
}
