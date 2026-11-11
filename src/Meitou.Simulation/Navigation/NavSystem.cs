using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The world's navigation, doors and background zone preparation. Queries read immutable meshes from any thread.
/// Deferred hosts publish only through CommitPrepared at their serialized world boundary and wait for missing navigation.
/// Standalone immediate mode publishes completed builds on the worker and permits its open-ground stand-in.
/// LoadZone, LoadRing and UnloadZone are thread-safe requests; zone readiness belongs to the current incarnation.
/// </summary>
public sealed class NavSystem : IDisposable
{
    /// <summary>
    /// The background zone builder that feeds <see cref = "Walkability"/>.
    /// </summary>
    readonly NavMeshService _service;
    /// <summary>
    /// Footprints for the long-range grid's obstacles, the level data's and the player's (one record cache for both).
    /// </summary>
    readonly Lazy<BuildingFootprints> _footprints;
    /// <summary>
    /// The level data the gates' records and the long-range grid's obstacles come from.
    /// </summary>
    readonly WorldLevelData _levels;
    /// <summary>
    /// The game data the gates' BUILDING records are looked up in.
    /// </summary>
    readonly GameDatabase _db;
    /// <summary>
    /// The terrain height for a placement that has no height of its own.
    /// </summary>
    readonly Func<double, double, float> _placementHeight;
    /// <summary>
    /// Runs the player-route updates one at a time.
    /// </summary>
    readonly object _playerRouteGate = new();
    /// <summary>
    /// One gate labelling at a time.
    /// </summary>
    readonly object _labelGate = new();
    /// <summary>
    /// The <c>functionality</c> records looked up by the gate test, cached by id across labellings.
    /// </summary>
    readonly Dictionary<string, GameRecord?> _functions = [];
    /// <summary>
    /// The level data's buildings by placement key (what the door polygons are named by), their BUILDING record ids. Made on first use and again when
    /// the level data was edited (WorldLevelData.EditRevision).
    /// </summary>
    (int Revision, Dictionary<string, string> Map)? _levelRecords;
    NavWorld? _gateInputWorld;
    long _gateInputRevision = -1;
    int _gateInputEditRevision = -1;
    GateRegionBuild? _gateInput;
    readonly object _routeResultGate = new();
    readonly HashSet<ZoneCoordinate> _routeChanges = [];
    (long Revision, IReadOnlyList<System.Numerics.Vector2[]> Obstacles)? _preparedRoute;
    long _routeRevision;
    volatile bool _disposed;
    public NavSystem(GameInstall install, GameDatabase db, WorldLevelData levels, Func<float, float, float> terrainHeight, NavBuildSettings? settings = null, NavMeshCache? cache = null, bool deferPublication = false)
    {
        this._levels = levels;
        this._db = db;
        DeferredPublication = deferPublication;
        _placementHeight = (x, z) => terrainHeight((float)x, (float)z);
        _footprints = new(() => new BuildingFootprints(install, db, levels));
        // The long-range grid reads every second heightmap sample (36 units) when it is first needed: the simulation's own grid is coarse far from the start.
        Walkability = new NavmeshWalkability(terrainHeight, routeTerrain: () =>
        {
            using var map = TerrainHeightmap.Open(install);
            var raw = map.Downsample(2, out int size);
            var grid = new HeightWindow(0, 0, 2, size, size, raw);
            return (x, z) => grid.TryHeightAt(x, z, out float h) ? h : terrainHeight(x, z);
        }, routeObstacles: () => [.._footprints.Value.Obstacles(levels.Buildings(), levels, _placementHeight), .._footprints.Value.FeatureObstacles(_placementHeight)]);
        _service = new NavMeshService(install, db, levels, Walkability, settings, cache, Runtime, deferPublication);
        Walkability.AllowUnloadedFallback = !deferPublication;
        Walkability.KeepStanding = _service.KeepAt;
        Walkability.NavigationIncarnation = () => _service.Incarnation;
        Walkability.DoorGeometry = new NavDoorGeometry(install, db);
    }

    /// <summary>
    /// The walkability to hand to the simulation: the loaded zones' navmesh, the open-ground stand-in elsewhere and the long-range grid.
    /// </summary>
    public NavmeshWalkability Walkability { get; }
    /// <summary>
    /// A loading screen covers the game: zone builds take every core (<see cref = "NavMeshService.Covered"/>).
    /// </summary>
    public bool Covered { get => _service.Covered; set => _service.Covered = value; }
    /// <summary>
    /// Open or close a door by its building's instance id: <c>Doors.Close(id)</c>. No rebuild; the next query sees it.
    /// </summary>
    public NavDoors Doors => Walkability.Doors;

    /// <summary>
    /// Raised by the committer when a zone's mesh goes live; the worker only in immediate mode.
    /// </summary>
    public event Action<NavZoneReady>? ZoneReady { add => _service.ZoneReady += value; remove => _service.ZoneReady -= value; }

    /// <summary>
    /// Asks for a zone's mesh (from the cache, else built); the task completes when it is live. Asking twice is harmless.
    /// </summary>
    public Task<NavZoneReady> LoadZone(ZoneCoordinate zone) => _service.Request(zone);
    /// <summary>
    /// Asks for a zone and its neighbours within <paramref name = "radius"/> (1 = the 3 × 3 ring around the player), nearest first.
    /// </summary>
    public void LoadRing(ZoneCoordinate centre, int radius = 1)
    {
        var zones = new List<ZoneCoordinate>();
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                var z = new ZoneCoordinate(centre.X + dx, centre.Y + dz);
                if (z.IsInsideGrid)
                    zones.Add(z);
            }
        }

        foreach (var z in zones.OrderBy(z => Math.Abs(z.X - centre.X) + Math.Abs(z.Y - centre.Y)))
            _ = _service.Request(z);
    }

    /// <summary>
    /// Drops a zone's mesh (its cache file stays). Paths already handed out stay valid as lists of points.
    /// </summary>
    public void UnloadZone(ZoneCoordinate zone) => _service.Unload(zone);
    /// <summary>
    /// Drops several zones' meshes with one republish of the world (callable off the main thread).
    /// </summary>
    public void UnloadZones(IReadOnlyCollection<ZoneCoordinate> zones) => _service.Unload(zones);
    /// <summary>
    /// Builds a zone again if its mesh is loaded (a zone that is not is built with the current buildings when it is asked for).
    /// </summary>
    public void RebuildZone(ZoneCoordinate zone)
    {
        _ = _service.Rebuild(zone);
    }

    /// <summary>
    /// The long-range grid's terrain: the heightmap downsampled by 2, read once, falling back to <paramref name = "terrainHeight"/> off its window.
    /// </summary>
    static Func<float, float, float> ReadRouteTerrain(GameInstall install, Func<float, float, float> terrainHeight)
    {
        using var map = TerrainHeightmap.Open(install);
        var raw = map.Downsample(2, out int size);
        var grid = new HeightWindow(0, 0, 2, size, size, raw);
        return (x, z) => grid.TryHeightAt(x, z, out float h) ? h : terrainHeight(x, z);
    }

    /// <summary>
    /// The buildings the player placed, which every zone build sees (<see cref = "AddRuntimeBuilding"/>).
    /// </summary>
    public NavRuntimeBuildings Runtime { get; } = new();

    /// <summary>
    /// A building the player placed (or finished) takes part in the navmesh from the next build of the zones it reaches; the zones loaded now are
    /// built again, and <see cref = "ZoneReady"/> fires for each when it goes live.
    /// </summary>
    public void AddRuntimeBuilding(BuildingPlacement placement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = Runtime.Replace(placement);
        if (previous == placement)
            return;
        var changed = NavRuntimeBuildings.ZonesReached(placement.Position).ToHashSet();
        if (previous is not null)
            changed.UnionWith(NavRuntimeBuildings.ZonesReached(previous.Position));
        foreach (var zone in changed)
            RebuildZone(zone);
        UpdatePlayerRoute(changed);
    }

    /// <summary>
    /// The level data's buildings changed near <paramref name = "position"/> (the level editor placed or cleared one): the zones it reaches are built
    /// again from the level data as it is now.
    /// </summary>
    public void LevelChanged(System.Numerics.Vector3 position)
    {
        foreach (var zone in NavRuntimeBuildings.ZonesReached(position))
            RebuildZone(zone);
    }

    /// <summary>
    /// The building is gone (dismantled, cancelled, a world replaced): the zones it shaped are built again without it.
    /// </summary>
    public void RemoveRuntimeBuilding(string instanceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Runtime.Remove(instanceId)is not { } removed)
            return;
        var changed = NavRuntimeBuildings.ZonesReached(removed.Position).ToArray();
        foreach (var zone in changed)
            RebuildZone(zone);
        UpdatePlayerRoute(changed);
    }

    /// <summary>
    /// The gate codes of the meshes loaded now (<see cref = "NavGateRegions"/>). Slow (a flood over the walled regions): call it off the simulation
    /// thread. One labelling at a time; each reads the world and the player's buildings as they are then.
    /// </summary>
    public NavGateRegions LabelGates() => (NavGateRegions)CaptureGateRegions().Build(CancellationToken.None);
    /// <summary>
    /// Whether a door owner (a runtime or level building instance id) is a gate: its BUILDING record is one.
    /// </summary>
    bool IsGateId(Dictionary<string, string> runtime, string id)
    {
        if (!runtime.TryGetValue(id, out var record) && !LevelRecords().TryGetValue(id, out record))
            return false;
        return _db.Find(record)is { } building && TownBuildings.IsGate(_db, building, _functions);
    }

    /// <summary>
    /// The level data's buildings' record ids by placement key, made again when <see cref = "WorldLevelData.EditRevision"/> moved on.
    /// </summary>
    Dictionary<string, string> LevelRecords()
    {
        int revision = _levels.EditRevision;
        if (_levelRecords is { } known && known.Revision == revision)
            return known.Map;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in _levels.Buildings())
            map.TryAdd(b.Key, b.BuildingId);
        _levelRecords = (revision, map);
        return map;
    }

    /// <summary>
    /// Stops the zone builder.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.Dispose();
        lock (_playerRouteGate)
        {
        }
    }

    public bool DeferredPublication { get; }

    /// <summary>
    /// Completes after current geometry is built; deferred hosts must then synchronize at their world boundary.
    /// </summary>
    public Task<NavZoneReady> PrepareZone(ZoneCoordinate zone) => _service.Prepare(zone);
    public bool IsReady(ZoneCoordinate zone) => _service.IsReady(zone);
    public IReadOnlyCollection<ZoneCoordinate> TrackedZones => _service.TrackedZones;
    public long Incarnation => _service.Incarnation;

    public bool KeepAt(System.Numerics.Vector3 position) => _service.KeepAt(position);
    public IReadOnlySet<ZoneCoordinate> CommitPrepared()
    {
        var changed = _service.CommitPrepared().ToHashSet();
        lock (_routeResultGate)
        {
            if (_preparedRoute is { } ready && ready.Revision == _routeRevision)
            {
                Walkability.Coarse.SetPlayerObstacles(ready.Obstacles);
                changed.UnionWith(_routeChanges);
                _routeChanges.Clear();
                _preparedRoute = null;
            }
        }

        return changed;
    }

    public void NewIncarnation(bool requeuePending = true)
    {
        _gateInput = null;
        _service.NewIncarnation(requeuePending);
        UpdatePlayerRoute();
    }

    /// <summary>
    /// Gives the long-range grid the player's buildings now, on the pool (their footprints may need reading). Runs one at a time, each from the overlay as it is
    /// then, so the last one leaves the newest set.
    /// </summary>
    void UpdatePlayerRoute(IEnumerable<ZoneCoordinate>? zones = null)
    {
        long revision;
        lock (_routeResultGate)
        {
            revision = checked(++_routeRevision);
            _preparedRoute = null;
            if (zones is not null)
                _routeChanges.UnionWith(zones);
            if (Runtime.IsEmpty && !Walkability.HasCoarseRoute)
            {
                _routeChanges.Clear();
                return;
            }
        }

        _ = Task.Run(() =>
        {
            lock (_playerRouteGate)
            {
                if (_disposed || revision != Volatile.Read(ref _routeRevision))
                    return;
                var obstacles = _footprints.Value.Obstacles(Runtime.All(), _levels, _placementHeight);
                _ = Walkability.Coarse; // warm immutable terrain/content on the worker, not the commit boundary
                lock (_routeResultGate)
                {
                    if (_disposed || revision != _routeRevision)
                        return;
                    if (DeferredPublication)
                        _preparedRoute = (revision, obstacles);
                    else
                    {
                        Walkability.Coarse.SetPlayerObstacles(obstacles);
                        _routeChanges.Clear();
                    }
                }
            }
        });
    }

    /// <summary>
    /// Capture on the serialized navigation owner, after prepared meshes and building overlays are committed.
    /// </summary>
    public GateRegionBuild CaptureGateRegions()
    {
        var mesh = Walkability.World;
        long revision = Runtime.Revision;
        if (_gateInput is not null && ReferenceEquals(_gateInputWorld, mesh) && _gateInputRevision == revision && _gateInputEditRevision == _levels.EditRevision)
            return _gateInput;
        var runtime = Runtime.All().ToDictionary(p => p.Key, p => p.BuildingId, StringComparer.Ordinal);
        var records = LevelRecords();
        _gateInputEditRevision = _levels.EditRevision;
        _gateInputWorld = mesh;
        _gateInputRevision = revision;
        return _gateInput = new(new object (), cancellation =>
        {
            lock (_labelGate)
            {
                cancellation.ThrowIfCancellationRequested();
                return NavGateRegions.Label(mesh, id => (runtime.TryGetValue(id, out var record) || records.TryGetValue(id, out record)) && _db.Find(record)is { } building && TownBuildings.IsGate(_db, building, _functions), cancellation);
            }
        });
    }
}