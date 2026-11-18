using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Ragdolls;
using CellMap = System.Collections.Generic.Dictionary<(int, int), System.Collections.Generic.List<System.Numerics.Vector3>>;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Terrain and per-zone building collision for ragdolls and sight. Workers read immutable runtime snapshots; a loaded zone keeps its previous
/// triangles until a current replacement is ready. Zone loading waits for the first gather, and held zones are not evicted.
/// </summary>
public sealed class RagdollGeometry : IRagdollEnvironment, IDisposable
{
    #region State

    /// <summary>
    /// Entries kept besides held zones; least recently used entries go first.
    /// </summary>
    const int MaxZones = 24;
    /// <summary>
    /// How far a building's collision may reach from its placement.
    /// </summary>
    const float Reach = 600;
    /// <summary>
    /// The gatherer, used only under the gather lock.
    /// </summary>
    readonly ZoneGeometryGatherer _gatherer;
    /// <summary>
    /// Serializes gathers because their heightmap reader is not thread-safe.
    /// </summary>
    readonly object _gatherLock = new();
    /// <summary>
    /// Serializes snapshot capture, cache publication and disposal.
    /// </summary>
    readonly object _cacheLock = new();
    /// <summary>
    /// Published or initially pending collision, plus at most one replacement per entry.
    /// </summary>
    readonly Dictionary<(ZoneCoordinate Zone, bool Roofs), ZoneEntry> _zones = [];
    /// <summary>
    /// Last use of each entry, for eviction.
    /// </summary>
    readonly Dictionary<(ZoneCoordinate Zone, bool Roofs), long> _used = [];
    /// <summary>
    /// All unfinished jobs, including entries evicted while a replacement was running.
    /// </summary>
    readonly List<Task> _pending = [];
    /// <summary>
    /// The runtime placements whose snapshots each gather reads.
    /// </summary>
    readonly NavRuntimeBuildings? _runtime;
    /// <summary>
    /// The full-resolution terrain used by ragdolls.
    /// </summary>
    readonly RagdollGround _ground;
    /// <summary>
    /// Loaded zones retained regardless of cache pressure.
    /// </summary>
    HashSet<ZoneCoordinate> _held = [];
    /// <summary>
    /// The latest runtime snapshot captured at a reader boundary.
    /// </summary>
    NavRuntimeBuildings? _runtimeSnapshot;
    /// <summary>
    /// Revision of the captured runtime snapshot.
    /// </summary>
    long _cachedRevision = long.MinValue;
    /// <summary>
    /// Increments when replacement geometry is published, invalidating cached collision tiles.
    /// </summary>
    long _generation;
    /// <summary>
    /// Completion notifications make unchanged collision queries skip scanning the zone cache.
    /// </summary>
    long _completionRevision;
    /// <summary>
    /// The completion counter last observed at a reader boundary.
    /// </summary>
    long _observedCompletionRevision = -1;
    /// <summary>
    /// Monotonic use counter for eviction.
    /// </summary>
    long _clock;
    /// <summary>
    /// No new work can start once disposal begins.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// One entry's published gather and the revision last attempted. Failed replacements retain the previous usable collision.
    /// </summary>
    sealed class ZoneEntry(Task<CellMap> current, long revision)
    {
        /// <summary>
        /// Current triangles, or the first gather still pending.
        /// </summary>
        public Task<CellMap> Current = current;
        /// <summary>
        /// The latest completed attempt's revision.
        /// </summary>
        public long Revision = revision;
        /// <summary>
        /// One pending replacement, coalescing further changes until it finishes.
        /// </summary>
        public Refresh? Replacement;
    }

    /// <summary>
    /// A gather's captured revision and result.
    /// </summary>
    sealed record Refresh(long Revision, Task<CellMap> Result);

    /// <summary>
    /// Gathers collision on the calling thread, including replacements, for deterministic headless runs.
    /// </summary>
    public bool Synchronous { get; }

    /// <summary>
    /// Gathers collision from the install and captured runtime placements; the supplied terrain height is the missing-heightmap fallback.
    /// </summary>
    public RagdollGeometry(GameInstall install, GameDatabase db, WorldLevelData levels, Func<float, float, float> terrainHeight,
        NavRuntimeBuildings? runtime = null, bool synchronous = false)
    {
        Synchronous = synchronous;
        _gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install)) { IncludeFoliage = false };
        _runtime = runtime;
        _ground = new RagdollGround(install, terrainHeight);
    }

    #endregion

    #region Queries

    /// <inheritdoc/>
    public long Generation
    {
        get
        {
            lock (_cacheLock)
            {
                if (!_disposed) RefreshRuntime();
                return _generation;
            }
        }
    }

    /// <summary>
    /// The terrain at full resolution, called from the simulation thread.
    /// </summary>
    public float GroundHeight(float x, float z, float nearY) => _ground.HeightAt(x, z);
    /// <inheritdoc/>
    public bool Buildings(Vector3 min, Vector3 max, List<Vector3> triangles) => Collect(min, max, triangles, roofs: false);
    /// <summary>
    /// Collision groups 23..26, kept independently of building collision.
    /// </summary>
    public bool Roofs(Vector3 min, Vector3 max, List<Vector3> triangles) => Collect(min, max, triangles, roofs: true);

    /// <summary>
    /// Whether a zone's first building gather finished. A failed gather counts as loaded without collision, as in the upstream loader.
    /// </summary>
    public bool ZoneRead(ZoneCoordinate zone)
    {
        lock (_cacheLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshRuntime();
            return Ask(zone, roofs: false).IsCompleted;
        }
    }

    /// <summary>
    /// Keeps loaded zones' entries from eviction.
    /// </summary>
    public void HoldZones(IReadOnlyCollection<ZoneCoordinate> zones)
    {
        lock (_cacheLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _held = [.. zones];
            Evict();
        }
    }

    /// <summary>
    /// Collects cells from every zone whose buildings can reach the box; missing first gathers report not ready.
    /// </summary>
    bool Collect(Vector3 min, Vector3 max, List<Vector3> triangles, bool roofs)
    {
        lock (_cacheLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshRuntime();
            return CollectZones(min, max, triangles, roofs);
        }
    }

    /// <summary>
    /// Adds the requested cells from the current entries, starting first gathers as needed.
    /// </summary>
    bool CollectZones(Vector3 min, Vector3 max, List<Vector3> triangles, bool roofs)
    {
        bool ready = true;
        int cx0 = Cell(min.X), cx1 = Cell(max.X - 1e-3f), cz0 = Cell(min.Z), cz1 = Cell(max.Z - 1e-3f);
        var first = WorldLayout.ZoneOf(min.X - Reach, min.Z - Reach);
        var last = WorldLayout.ZoneOf(max.X + Reach, max.Z + Reach);
        for (int zy = first.Y; zy <= last.Y; zy++)
        {
            for (int zx = first.X; zx <= last.X; zx++)
            {
                var zone = new ZoneCoordinate(zx, zy);
                if (!zone.IsInsideGrid) continue;

                var task = Ask(zone, roofs);
                if (!task.IsCompletedSuccessfully)
                {
                    ready &= !task.IsFaulted && task.IsCompleted;
                    continue;
                }

                AddCells(task.Result, cx0, cx1, cz0, cz1, triangles);
            }
        }

        Evict();
        return ready;
    }

    /// <summary>
    /// Adds cells in the requested rectangle from one gathered zone.
    /// </summary>
    static void AddCells(CellMap cells, int cx0, int cx1, int cz0, int cz1, List<Vector3> triangles)
    {
        for (int x = cx0; x <= cx1; x++)
            for (int z = cz0; z <= cz1; z++)
                if (cells.TryGetValue((x, z), out var cell)) triangles.AddRange(cell);
    }

    /// <summary>
    /// The 32-unit cell containing a coordinate.
    /// </summary>
    static int Cell(float v) => (int)MathF.Floor(v / RagdollSystem.TileSize);

    #endregion

    #region Cache lifecycle

    /// <summary>
    /// Captures new placements and publishes only replacements for that snapshot. One job per entry coalesces intervening revisions.
    /// </summary>
    void RefreshRuntime()
    {
        long completed = Volatile.Read(ref _completionRevision);
        if (_cachedRevision == (_runtime?.Revision ?? 0) && _observedCompletionRevision == completed) return;

        _observedCompletionRevision = completed;
        _pending.RemoveAll(ObserveFinished);
        if (_cachedRevision != (_runtime?.Revision ?? 0))
        {
            _runtimeSnapshot = _runtime?.Snapshot();
            _cachedRevision = _runtimeSnapshot?.Revision ?? 0;
        }

        foreach (var (key, entry) in _zones)
            RefreshEntry(key, entry);
    }

    /// <summary>
    /// Completes one replacement and starts the newest one when its old snapshot became obsolete.
    /// </summary>
    void RefreshEntry((ZoneCoordinate Zone, bool Roofs) key, ZoneEntry entry)
    {
        if (entry.Replacement is { } replacement)
        {
            if (!replacement.Result.IsCompleted) return;

            entry.Replacement = null;
            if (replacement.Revision == _cachedRevision)
            {
                entry.Revision = replacement.Revision;
                if (replacement.Result.IsCompletedSuccessfully)
                {
                    entry.Current = replacement.Result;
                    _generation++;
                }
            }
        }

        if (entry.Revision == _cachedRevision) return;

        var next = StartGather(key.Zone, key.Roofs);
        if (Synchronous)
        {
            entry.Current = next;
            entry.Revision = _cachedRevision;
            _generation++;
        }
        else entry.Replacement = new Refresh(_cachedRevision, next);
    }

    /// <summary>
    /// Observes failures before a finished task leaves the disposal list.
    /// </summary>
    static bool ObserveFinished(Task task)
    {
        if (!task.IsCompleted) return false;

        _ = task.Exception;
        return true;
    }

    /// <summary>
    /// The current gather for one entry, starting it once when absent.
    /// </summary>
    Task<CellMap> Ask(ZoneCoordinate zone, bool roofs)
    {
        var key = (zone, roofs);
        _used[key] = ++_clock;
        if (!_zones.TryGetValue(key, out var entry))
        {
            entry = new ZoneEntry(StartGather(zone, roofs), _cachedRevision);
            _zones.Add(key, entry);
        }

        return entry.Current;
    }

    /// <summary>
    /// Starts a gather with the immutable captured placements and tracks it until completion or disposal.
    /// </summary>
    Task<CellMap> StartGather(ZoneCoordinate zone, bool roofs)
    {
        var snapshot = _runtimeSnapshot;
        if (Synchronous) return Task.FromResult(Gather(zone, roofs, snapshot));

        var task = Task.Run(() => Gather(zone, roofs, snapshot));
        _pending.Add(task);
        _ = task.ContinueWith(_ => Interlocked.Increment(ref _completionRevision), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// Drops the least recently used completed entries beyond the spare budget, leaving every held zone.
    /// </summary>
    void Evict()
    {
        int spare = _zones.Keys.Count(k => !_held.Contains(k.Zone)) - MaxZones;
        if (spare <= 0) return;

        foreach (var (key, _) in _used.Where(u => !_held.Contains(u.Key.Zone)).OrderBy(u => u.Value).Take(spare).ToArray())
        {
            if (!_zones.TryGetValue(key, out var entry) || !entry.Current.IsCompleted) continue;

            _zones.Remove(key);
            _used.Remove(key);
        }
    }

    /// <summary>
    /// Waits for all workers before disposing the shared readers, including obsolete or evicted jobs.
    /// </summary>
    public void Dispose()
    {
        Task[] tasks;
        lock (_cacheLock)
        {
            if (_disposed) return;

            _disposed = true;
            tasks = _pending.ToArray();
        }

        foreach (var task in tasks)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Disposal cannot publish failed or obsolete geometry.
            }
        }

        _ground.Dispose();
        lock (_gatherLock)
            _gatherer.Dispose();
    }

    #endregion

    #region Gathering

    /// <summary>
    /// Gathers one snapshot's triangles and files each under the cells its plan bounds cover.
    /// </summary>
    CellMap Gather(ZoneCoordinate zone, bool roofs, NavRuntimeBuildings? snapshot)
    {
        var all = new List<Vector3>();
        lock (_gatherLock)
        {
            _gatherer.UseRuntimeSnapshot(snapshot);
            if (roofs) _gatherer.RoofTriangles(zone, all);
            else _gatherer.RagdollTriangles(zone, all);
        }

        var cells = new CellMap();
        for (int t = 0; t + 2 < all.Count; t += 3)
        {
            Vector3 a = all[t], b = all[t + 1], c = all[t + 2];
            var lo = Vector3.Min(a, Vector3.Min(b, c));
            var hi = Vector3.Max(a, Vector3.Max(b, c));
            for (int x = Cell(lo.X); x <= Cell(hi.X); x++)
            {
                for (int z = Cell(lo.Z); z <= Cell(hi.Z); z++)
                {
                    if (!cells.TryGetValue((x, z), out var list)) cells[(x, z)] = list = [];

                    list.Add(a);
                    list.Add(b);
                    list.Add(c);
                }
            }
        }

        return cells;
    }

    #endregion
}
