using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Ragdolls;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// What ragdolls fall onto in the world: the terrain heightmap, and the collision of the buildings and map features
/// (<see cref = "ZoneGeometryGatherer.RagdollTriangles"/>), gathered per zone on a worker the first time a ragdoll moves near it and kept in 32-unit
/// cells (<see cref = "RagdollPhysics"/>' tiles). Until a zone is gathered its buildings are reported not ready, and the tile asks again.
/// </summary>
public sealed class RagdollGeometry : IRagdollEnvironment, IDisposable
{
    /// <summary>
    /// Zones kept at most; the least recently asked for goes first (engine choice).
    /// </summary>
    const int MaxZones = 24;
    /// <summary>
    /// How far a building's collision may reach from its placement (the navmesh gatherer's reach).
    /// </summary>
    const float Reach = 600;
    /// <summary>
    /// Gathers the zones' building and feature collision (foliage left out); used under <see cref = "_gatherLock"/>.
    /// </summary>
    readonly ZoneGeometryGatherer _gatherer;
    /// <summary>
    /// Each zone's gathered triangles by 32-unit cell, buildings and roofs apart, as the task of a worker's gather; dropped by a run-time building
    /// change and by <see cref = "Evict"/>.
    /// </summary>
    readonly ConcurrentDictionary<(ZoneCoordinate Zone, bool Roofs), Task<Dictionary<(int, int), List<Vector3>>>> _zones = new();
    /// <summary>
    /// One gather at a time: the gatherer is not thread-safe.
    /// </summary>
    readonly object _gatherLock = new();
    /// <summary>
    /// When each gathered zone was last asked for (a tick of <see cref = "_clock"/>), for <see cref = "Evict"/>.
    /// </summary>
    readonly ConcurrentDictionary<(ZoneCoordinate Zone, bool Roofs), long> _used = new();
    /// <summary>
    /// Counts the asks; the newest has the highest number.
    /// </summary>
    long _clock;
    /// <summary>
    /// The buildings placed at run time, whose changes drop the zones they touch; null: none.
    /// </summary>
    readonly NavRuntimeBuildings? _runtime;
    /// <summary>
    /// The terrain bodies land on.
    /// </summary>
    readonly RagdollGround _ground;
    readonly object _cacheLock = new();
    readonly List<Task> _pending = [];
    NavRuntimeBuildings? _runtimeSnapshot;
    long _cachedRevision = long.MinValue;
    bool _disposed;
    /// <param name = "terrainHeight">
    /// The simulation's ground height, used only where the full heightmap cannot be opened: it is the coarse world grid away from the loaded area.
    /// </param>
    /// <param name = "runtime">
    /// The buildings placed at run time (built, bought or ruined houses), which the zones' collision includes as the navmesh does; a change drops the
    /// zone's gathered collision and bumps <see cref = "Generation"/>.
    /// </param>
    public RagdollGeometry(GameInstall install, GameDatabase db, WorldLevelData levels, Func<float, float, float> terrainHeight, NavRuntimeBuildings? runtime = null)
    {
        _gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install))
        {
            IncludeFoliage = false
        };
        _runtime = runtime;
        _ground = new RagdollGround(install, terrainHeight);
    }

    /// <inheritdoc/>
    public long Generation => _runtime?.Revision ?? 0;

    /// <summary>
    /// The terrain at full resolution (<see cref = "RagdollGround.HeightAt"/>). Called from the simulation thread only.
    /// </summary>
    public float GroundHeight(float x, float z, float nearY) => _ground.HeightAt(x, z);
    /// <inheritdoc/>
    public bool Buildings(Vector3 min, Vector3 max, List<Vector3> triangles) => Collect(min, max, triangles, roofs: false);
    /// <summary>
    /// The groups 23..26 triangles (<see cref = "ZoneGeometryGatherer.RoofTriangles"/>), gathered and kept per zone like <see cref = "Buildings"/>.
    /// </summary>
    public bool Roofs(Vector3 min, Vector3 max, List<Vector3> triangles) => Collect(min, max, triangles, roofs: true);
    /// <summary>
    /// Adds the gathered triangles of the cells between the corners, from every zone within <see cref = "Reach"/> of them, starting a gather for a zone
    /// not asked for yet; false while one of those zones is still being gathered.
    /// </summary>
    bool Collect(Vector3 min, Vector3 max, List<Vector3> triangles, bool roofs)
    {
        lock (_cacheLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.RemoveAll(t => t.IsCompleted);
            if (_cachedRevision != Generation)
            {
                _runtimeSnapshot = _runtime?.Snapshot();
                _cachedRevision = _runtimeSnapshot?.Revision ?? 0;
                _zones.Clear();
                _used.Clear();
            }

            return CollectZones(min, max, triangles, roofs) && _cachedRevision == Generation;
        }
    }

    /// <summary>
    /// Adds the triangles of the cells in the box (cell columns <paramref name = "cx0"/> to <paramref name = "cx1"/>, rows <paramref name = "cz0"/> to
    /// <paramref name = "cz1"/>) from one gathered zone.
    /// </summary>
    static void AddCells(Dictionary<(int, int), List<Vector3>> cells, int cx0, int cx1, int cz0, int cz1, List<Vector3> triangles)
    {
        for (int x = cx0; x <= cx1; x++)
        {
            for (int z = cz0; z <= cz1; z++)
            {
                if (cells.TryGetValue((x, z), out var cell))
                    triangles.AddRange(cell);
            }
        }
    }

    /// <summary>
    /// The 32-unit cell (<see cref = "RagdollSystem.TileSize"/>) of a world X or Z.
    /// </summary>
    static int Cell(float v) => (int)MathF.Floor(v / RagdollSystem.TileSize);
    /// <summary>
    /// Drops the least recently asked for gathered zones past <see cref = "MaxZones"/> (only finished ones).
    /// </summary>
    void Evict()
    {
        if (_zones.Count <= MaxZones)
            return;
        foreach (var(zone, _)in _used.OrderBy(u => u.Value).Take(_zones.Count - MaxZones).ToList())
        {
            if (_zones.TryGetValue(zone, out var task) && task.IsCompleted && _zones.TryRemove(zone, out _))
                _used.TryRemove(zone, out _);
        }
    }

    /// <summary>
    /// Unhooks from the run-time buildings and waits for the gathers still running.
    /// </summary>
    public void Dispose()
    {
        Task[] tasks;
        lock (_cacheLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            tasks = _pending.ToArray();
        }

        // Invalidated jobs can still be queued behind gatherLock; wait before disposing their reader too.
        foreach (var task in tasks)
            try
            {
                task.Wait();
            }
            catch (AggregateException)
            {
            }

        _ground.Dispose();
        lock (_gatherLock)
            _gatherer.Dispose();
    }

    bool CollectZones(Vector3 min, Vector3 max, List<Vector3> triangles, bool roofs)
    {
        bool ready = true;
        int cx0 = Cell(min.X), cx1 = Cell(max.X - 1e-3f), cz0 = Cell(min.Z), cz1 = Cell(max.Z - 1e-3f);
        // A building's triangles are kept with the zone it is placed in and may reach into the next one: the zones within a building's reach of the box.
        var first = WorldLayout.ZoneOf(min.X - Reach, min.Z - Reach);
        var last = WorldLayout.ZoneOf(max.X + Reach, max.Z + Reach);
        for (int zy = first.Y; zy <= last.Y; zy++)
            for (int zx = first.X; zx <= last.X; zx++)
            {
                var zone = new ZoneCoordinate(zx, zy);
                if (!zone.IsInsideGrid)
                    continue;
                _used[(zone, roofs)] = Interlocked.Increment(ref _clock);
                if (!_zones.TryGetValue((zone, roofs), out var task))
                {
                    var snapshot = _runtimeSnapshot;
                    task = Task.Run(() => Gather(zone, roofs, snapshot));
                    _zones[(zone, roofs)] = task;
                    _pending.Add(task);
                }

                if (!task.IsCompletedSuccessfully)
                {
                    ready &= !task.IsFaulted && task.IsCompleted;
                    continue;
                }

                for (int x = cx0; x <= cx1; x++)
                    for (int z = cz0; z <= cz1; z++)
                        if (task.Result.TryGetValue((x, z), out var cell))
                            triangles.AddRange(cell);
            }

        Evict();
        return ready;
    }

    Dictionary<(int, int), List<Vector3>> Gather(ZoneCoordinate zone, bool roofs, NavRuntimeBuildings? snapshot)
    {
        var all = new List<Vector3>();
        lock (_gatherLock)
        {
            _gatherer.UseRuntimeSnapshot(snapshot);
            if (roofs)
                _gatherer.RoofTriangles(zone, all);
            else
                _gatherer.RagdollTriangles(zone, all);
        }

        var cells = new Dictionary<(int, int), List<Vector3>>();
        for (int t = 0; t + 2 < all.Count; t += 3)
        {
            Vector3 a = all[t], b = all[t + 1], c = all[t + 2];
            var lo = Vector3.Min(a, Vector3.Min(b, c));
            var hi = Vector3.Max(a, Vector3.Max(b, c));
            for (int x = Cell(lo.X); x <= Cell(hi.X); x++)
                for (int z = Cell(lo.Z); z <= Cell(hi.Z); z++)
                {
                    if (!cells.TryGetValue((x, z), out var list))
                        cells[(x, z)] = list = [];
                    list.Add(a);
                    list.Add(b);
                    list.Add(c);
                }
        }

        return cells;
    }
}