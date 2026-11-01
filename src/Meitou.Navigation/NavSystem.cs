using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// The navigation of a running game in one object, for the simulation host: the <see cref="Walkability"/> to hand to the simulation (replacing the
/// open-ground stand-in), the <see cref="Doors"/> table, and the zone load/unload calls that feed the background builder.
/// <para>Thread-safety: <see cref="Walkability"/> (<c>IsWalkable</c>, <c>GroundHeight</c>, <c>FindPath</c>) and <see cref="Doors"/> may be used from any
/// thread at any time. Deferred mode publishes at CommitPrepared boundaries and requires real navigation; standalone legacy mode publishes on completion and permits its stand-in.
/// <see cref="LoadZone"/>, <see cref="LoadRing"/> and <see cref="UnloadZone"/> may be called from any thread and return at once.</para>
/// </summary>
public sealed class NavSystem : IDisposable
{
    readonly NavMeshService service;

    /// <summary>The buildings the player placed, which every zone build sees (<see cref="AddRuntimeBuilding"/>).</summary>
    public NavRuntimeBuildings Runtime { get; } = new();

    // Footprints for the long-range grid's obstacles, the level data's and the player's (one record cache for both).
    readonly Lazy<BuildingFootprints> footprints;
    readonly WorldLevelData levels;
    readonly GameDatabase db;
    // The level data's buildings by placement key (what the door polygons are named by), their BUILDING record ids.
    readonly Lazy<Dictionary<string, string>> levelRecords;
    readonly Dictionary<string, GameRecord?> functions = [];
    readonly object labelGate = new();
    NavWorld? gateInputWorld;
    long gateInputRevision = -1;
    GateRegionBuild? gateInput;
    readonly Func<double, double, float> placementHeight;
    readonly object playerRouteGate = new();
    readonly object routeResultGate = new();
    readonly HashSet<ZoneCoordinate> routeChanges = [];
    (long Revision, IReadOnlyList<System.Numerics.Vector2[]> Obstacles)? preparedRoute;
    long routeRevision;
    volatile bool disposed;
    public bool DeferredPublication { get; }

    public NavSystem(GameInstall install, GameDatabase db, WorldLevelData levels, Func<float, float, float> terrainHeight, NavBuildSettings? settings = null, NavMeshCache? cache = null, bool deferPublication = false)
    {
        this.levels = levels;
        this.db = db;
        DeferredPublication = deferPublication;
        levelRecords = new(() =>
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var b in levels.Buildings()) map.TryAdd(b.Key, b.BuildingId);
            return map;
        });
        placementHeight = (x, z) => terrainHeight((float)x, (float)z);
        footprints = new(() => new BuildingFootprints(install, db, levels));
        // The long-range grid reads every second heightmap sample (36 units) when it is first needed: the simulation's own grid is coarse far from the start.
        Walkability = new NavmeshWalkability(terrainHeight, routeTerrain: () =>
        {
            using var map = TerrainHeightmap.Open(install);
            var raw = map.Downsample(2, out int size);
            var grid = new HeightWindow(0, 0, 2, size, size, raw);
            return (x, z) => grid.TryHeightAt(x, z, out float h) ? h : terrainHeight(x, z);
        }, routeObstacles: () => footprints.Value.Obstacles(levels.Buildings(), levels, placementHeight));
        service = new NavMeshService(install, db, levels, Walkability, settings, cache, Runtime, deferPublication);
        Walkability.AllowUnloadedFallback = !deferPublication;
        Walkability.KeepStanding = service.KeepAt;
        Walkability.NavigationIncarnation = () => service.Incarnation;
        Walkability.DoorGeometry = new NavDoorGeometry(install, db);
    }

    public NavmeshWalkability Walkability { get; }

    /// <summary>Open or close a door by its building's instance id: <c>Doors.Close(id)</c>. No rebuild; the next query sees it.</summary>
    public NavDoors Doors => Walkability.Doors;

    /// <summary>Raised by the committer when a mesh goes live (the worker only in standalone immediate mode).</summary>
    public event Action<NavZoneReady>? ZoneReady
    {
        add => service.ZoneReady += value;
        remove => service.ZoneReady -= value;
    }

    /// <summary>Asks for a zone's mesh (from the cache, else built); the task completes when it is live. Asking twice is harmless.</summary>
    public Task<NavZoneReady> LoadZone(ZoneCoordinate zone) => service.Request(zone);
    /// <summary>Completes after current geometry is built; deferred hosts must then synchronize at their world boundary.</summary>
    public Task<NavZoneReady> PrepareZone(ZoneCoordinate zone) => service.Prepare(zone);
    public bool IsReady(ZoneCoordinate zone) => service.IsReady(zone);
    public IReadOnlyCollection<ZoneCoordinate> TrackedZones => service.TrackedZones;
    public bool KeepAt(System.Numerics.Vector3 position) => service.KeepAt(position);

    public IReadOnlySet<ZoneCoordinate> CommitPrepared()
    {
        var changed = service.CommitPrepared().ToHashSet();
        lock (routeResultGate)
        {
            if (preparedRoute is { } ready && ready.Revision == routeRevision)
            {
                Walkability.Coarse.SetPlayerObstacles(ready.Obstacles);
                changed.UnionWith(routeChanges); routeChanges.Clear(); preparedRoute = null;
            }
        }
        return changed;
    }

    public void NewIncarnation(bool requeuePending = true)
    {
        gateInput = null;
        service.NewIncarnation(requeuePending);
        UpdatePlayerRoute();
    }

    /// <summary>Asks for a zone and its neighbours within <paramref name="radius"/> (1 = the 3 × 3 ring around the player), nearest first.</summary>
    public void LoadRing(ZoneCoordinate centre, int radius = 1)
    {
        var zones = new List<ZoneCoordinate>();
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                var z = new ZoneCoordinate(centre.X + dx, centre.Y + dz);
                if (z.IsInsideGrid) zones.Add(z);
            }
        foreach (var z in zones.OrderBy(z => Math.Abs(z.X - centre.X) + Math.Abs(z.Y - centre.Y))) _ = service.Request(z);
    }

    /// <summary>Drops a zone's mesh (its cache file stays). Paths already handed out stay valid as lists of points.</summary>
    public void UnloadZone(ZoneCoordinate zone) => service.Unload(zone);

    /// <summary>
    /// A building the player placed (or finished) takes part in the navmesh from the next build of the zones it reaches; the zones loaded now are built again, and
    /// <see cref="ZoneReady"/> fires for each when it goes live.
    /// </summary>
    public void AddRuntimeBuilding(BuildingPlacement placement)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var previous = Runtime.Replace(placement);
        if (previous == placement) return;
        var changed = NavRuntimeBuildings.ZonesReached(placement.Position).ToHashSet();
        if (previous is not null) changed.UnionWith(NavRuntimeBuildings.ZonesReached(previous.Position));
        foreach (var zone in changed) RebuildZone(zone);
        UpdatePlayerRoute(changed);
    }

    /// <summary>The building is gone (dismantled, cancelled, a world replaced): the zones it shaped are built again without it.</summary>
    public void RemoveRuntimeBuilding(string instanceId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Runtime.Remove(instanceId) is not { } removed) return;
        var changed = NavRuntimeBuildings.ZonesReached(removed.Position).ToArray();
        foreach (var zone in changed) RebuildZone(zone);
        UpdatePlayerRoute(changed);
    }

    /// <summary>
    /// Gives the long-range grid the player's buildings now, on the pool (their footprints may need reading). Runs one at a time, each from the overlay as it is
    /// then, so the last one leaves the newest set.
    /// </summary>
    void UpdatePlayerRoute(IEnumerable<ZoneCoordinate>? zones = null)
    {
        long revision;
        lock (routeResultGate)
        {
            revision = checked(++routeRevision); preparedRoute = null;
            if (zones is not null) routeChanges.UnionWith(zones);
            if (Runtime.IsEmpty && !Walkability.HasCoarseRoute) { routeChanges.Clear(); return; }
        }
        _ = Task.Run(() =>
        {
            lock (playerRouteGate)
            {
                if (disposed || revision != Volatile.Read(ref routeRevision)) return;
                var obstacles = footprints.Value.Obstacles(Runtime.All(), levels, placementHeight);
                _ = Walkability.Coarse; // warm immutable terrain/content on the worker, not the commit boundary
                lock (routeResultGate)
                {
                    if (disposed || revision != routeRevision) return;
                    if (DeferredPublication) preparedRoute = (revision, obstacles);
                    else { Walkability.Coarse.SetPlayerObstacles(obstacles); routeChanges.Clear(); }
                }
            }
        });
    }

    /// <summary>
    /// The gate codes of the meshes loaded now (<see cref="NavGateRegions"/>). Slow (a flood over the walled regions): call it off the simulation thread. One labelling at a
    /// time; each reads the world and the player's buildings as they are then.
    /// </summary>
    public NavGateRegions LabelGates() => (NavGateRegions)CaptureGateRegions().Build(CancellationToken.None);

    /// <summary>Capture on the serialized navigation owner, after prepared meshes and building overlays are committed.</summary>
    public GateRegionBuild CaptureGateRegions()
    {
        var mesh = Walkability.World;
        long revision = Runtime.Revision;
        if (gateInput is not null && ReferenceEquals(gateInputWorld, mesh) && gateInputRevision == revision) return gateInput;
        var runtime = Runtime.All().ToDictionary(p => p.Key, p => p.BuildingId, StringComparer.Ordinal);
        gateInputWorld = mesh; gateInputRevision = revision;
        return gateInput = new(new object(), cancellation =>
        {
            lock (labelGate)
            {
                cancellation.ThrowIfCancellationRequested();
                return NavGateRegions.Label(mesh, id =>
                    (runtime.TryGetValue(id, out var record) || levelRecords.Value.TryGetValue(id, out record)) && db.Find(record) is { } building
                    && TownBuildings.IsGate(db, building, functions), cancellation);
            }
        });
    }

    /// <summary>Invalidates a loaded or requested zone; an unknown zone is built with current buildings when requested.</summary>
    public void RebuildZone(ZoneCoordinate zone)
    {
        _ = service.Rebuild(zone);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        service.Dispose();
        // A running obstacle calculation may still read terrain/content; queued ones now skip it.
        lock (playerRouteGate) { }
    }
}
