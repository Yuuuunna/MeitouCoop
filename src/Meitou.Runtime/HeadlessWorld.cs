using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Saving;
using Meitou.Networking;

namespace Meitou.Runtime;

/// <summary>Real-content CPU host. Calls that touch Runtime/World are serialized by the server's world loop.</summary>
public sealed class HeadlessWorld : IDisposable
{
    bool disposed;
    readonly LoadOrder loadOrder;
    readonly StandardSystemOptions systemOptions;

    HeadlessWorld(TerrainSource terrain, WorldContent content, WorldRuntime runtime, LoadOrder loadOrder, StandardSystemOptions systemOptions)
    {
        Terrain = terrain;
        Content = content;
        Runtime = runtime;
        this.loadOrder = loadOrder;
        this.systemOptions = systemOptions;
    }

    public TerrainSource Terrain { get; }
    public WorldContent Content { get; }
    public SimulationWeather Weather => World.Systems.OfType<SimulationWeather>().Single();
    public WorldRuntime Runtime { get; }
    public World World => Runtime.World;
    public PopulationData Population => World.Systems.OfType<PopulationSystem>().SingleOrDefault()?.Data ?? Content.Population;

    /// <summary>The host supplies its release identity, authoritative settings and complete gameplay asset dependency list.</summary>
    public CompatibilityHello CreateCompatibilityHello(string engineBuild, IReadOnlyDictionary<string, string> rules, IEnumerable<ContentFile> gameplayAssets)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var manifest = ContentManifest.ReadLoadOrder(loadOrder, gameplayAssets);
        return new CompatibilityHello(CompatibilityHandshake.Protocol, engineBuild, ContentManifest.RulesDigest(rules), manifest.Digest);
    }

    public WorldCheckpointCoordinator CreateCheckpointCoordinator(CheckpointStore store, LoadedSave? loaded = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new WorldCheckpointCoordinator(Runtime, Population, store, new SaveCaptureOptions
        {
            DataFiles = Population.Db.Files, Levels = Content.Levels,
            Placements = Content.Levels?.Towns().ToArray(), Seed = World.Seed,
        }, loaded);
    }

    /// <summary>Serialized startup/maintenance restore; connection and policy services must bind to the returned coordinator/new world.</summary>
    public CheckpointRestoreResult RestoreCheckpoint(WorldCheckpointCoordinator coordinator, StoredCheckpoint checkpoint, bool deferPlayerRoles = true)
    {
        coordinator.VerifyOwner(Runtime);
        var candidate = Content.CreatePopulationData();
        var candidateWeather = Weather.CreateSibling();
        return coordinator.Restore(checkpoint, candidate, data => Content.BuildSystems(systemOptions with { Environment = candidateWeather }, data).Systems,
            new SaveCaptureOptions { DataFiles = candidate.Db.Files, Levels = Content.Levels, Placements = Content.Levels?.Towns().ToArray() },
            candidate.Appearances, deferPlayerRoles);
    }

    public static HeadlessWorld Open(GameInstall install, WorldSettings? settings = null, StandardSystemOptions? options = null)
    {
        var order = LoadOrder.FromInstall(install);
        var db = GameDatabase.Load(order);
        var levels = WorldLevelData.Load(install, includeInteriors: true);
        var terrain = TerrainSource.Open(install);
        WorldContent? content = null;
        WorldRuntime? runtime = null;
        try
        {
            content = WorldContent.Load(install, db, new GameAssets(install), terrain.HeightAt, levels);
            var weather = SimulationWeather.Load(install, db, terrain.HeightAt);
            var configured = (options ?? new StandardSystemOptions()) with
            {
                Environment = weather,
                Construction = options?.Construction ?? new Meitou.Simulation.Construction.ConstructionSettings { EdgeGap = content.SiteEdgeGap },
                Production = options?.Production ?? new Meitou.Simulation.Production.ProductionSettings { EdgeGap = content.SiteEdgeGap },
            };
            var systems = content.BuildSystems(configured);
            var constants = content.Population.Bodies.Constants;
            var worldSettings = settings ?? new WorldSettings
            {
                Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
                Sunrise = constants.Sunrise, Sunset = constants.Sunset,
            };
            runtime = new WorldRuntime(worldSettings, content.Walkability, systems.Systems);
            return new HeadlessWorld(terrain, content, runtime, order, configured);
        }
        catch
        {
            try { runtime?.Dispose(); }
            finally { try { content?.Dispose(); } finally { terrain.Dispose(); } }
            throw;
        }
    }

    /// <summary>Prepare authoritative navigation before spawning or accepting movement in an arrival region.</summary>
    public async Task PrepareRegionAsync(Vector3 focus, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var centre = WorldLayout.ZoneOf(focus.X, focus.Z);
        if (!centre.IsInsideGrid) throw new ArgumentOutOfRangeException(nameof(focus));
        var zones = new List<ZoneCoordinate>();
        for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++)
            {
                var zone = new ZoneCoordinate(centre.X + x, centre.Y + z);
                if (zone.IsInsideGrid) zones.Add(zone);
            }
        using var preparation = World.Regions.Retain(zones, simulation: false);
        Runtime.Synchronize();
        do
        {
            await Task.WhenAll(zones.Select(Content.Navigation!.PrepareZone)).WaitAsync(cancellationToken).ConfigureAwait(false);
            Runtime.Synchronize();
        } while (zones.Any(z => !Content.Navigation!.IsReady(z)));
    }

    public int Advance(double seconds, int maxTicks = 8)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Runtime.Advance(seconds, maxTicks);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Runtime.Dispose(); }
        finally { try { Content.Dispose(); } finally { Terrain.Dispose(); } }
    }
}
