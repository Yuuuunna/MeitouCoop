using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;

namespace Meitou.Runtime;

/// <summary>CPU gameplay content shared by graphical and dedicated hosts. Dispose after every world using it.</summary>
public sealed class WorldContent : IDisposable
{
    WorldContent() { }
    public required PopulationData Population { get; init; }
    public required IWalkability Walkability { get; init; }
    public NavSystem? Navigation { get; init; }
    public RagdollGeometry? RagdollGeometry { get; init; }
    public required CharacterRigs Rigs { get; init; }
    public required AnimationLengths AnimationLengths { get; init; }
    public required AnimationLibrary AnimationLibrary { get; init; }
    public WorldLevelData? Levels { get; init; }
    public required IReadOnlyList<TownPlacement> Towns { get; init; }
    BuildingFootprints? footprints;
    GameInstall install = null!;
    GameDatabase database = null!;
    Func<float, float, float> ground = null!;
    bool disposed;

    /// <summary>Independent mutable faction/population state over the same immutable content and CPU services.</summary>
    public PopulationData CreatePopulationData()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var layouts = Levels is null ? null : new BuildingLayouts(database, Levels.Interiors);
        var buildings = Levels is null ? null : TownBuildings.Build(database, Levels, layouts!, (x, z) => ground((float)x, (float)z));
        var data = PopulationData.Create(database, Towns, Population.Appearances, Population.BodyOptions, Population.Biomes,
            buildings: buildings, roads: Levels?.Roads(), townZones: Levels?.TownZones(), level: Levels?.Level);
        data.Sight = Levels is null ? null : new BuildingSight(install, database, Levels, layouts!);
        data.ResourceNodes = Population.ResourceNodes;
        return data;
    }

    public StandardSystemSet BuildSystems(StandardSystemOptions? options = null, PopulationData? population = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var data = population ?? Population;
        var construction = options?.Construction;
        var placement = new Placement.ContentPlacement(install, database, data, Levels, footprints!, ground,
            p => Navigation is null || Navigation.IsReady(WorldLayout.ZoneOf(p.X, p.Z)));
        var configured = (options ?? new StandardSystemOptions()) with
        {
            Construction = new Meitou.Simulation.Construction.ConstructionSettings
            {
                BuildingSpeed = construction?.BuildingSpeed ?? 1, Footprint = construction?.Footprint,
                EdgeGap = construction?.EdgeGap ?? SiteEdgeGap, Placement = construction?.Placement ?? placement.Validator,
            },
            AnimationLengths = AnimationLengths, AnimationLibrary = AnimationLibrary,
            AnimationBlendRate = data.Bodies.Constants.AnimationBlendRate,
            Ragdolls = Rigs, RagdollEnvironment = RagdollGeometry,
        };
        var result = StandardSystems.Build(data, Walkability, configured);
        if (Navigation is not null && result.Population is { } system)
            system.ZoneGate = Navigation.IsReady;
        return Navigation is null ? result : result with
        {
            Systems = [.. result.Systems, new WorldBuildings(database, new NavigationBuildingSink(Navigation)),
                new WorldRegionNavigation(new NavigationRegions(Navigation)), new WorldNavigation(Navigation), new WorldGateRegions(Navigation)],
        };
    }

    public float? SiteEdgeGap(Meitou.Simulation.Construction.BuildSite site, System.Numerics.Vector2 point)
    {
        if (footprints is null || !footprints.Ready(site.Record)) return null;
        return (footprints.Of(site.Record) ?? footprints.Plot(site.Record))?.Gap(site.Position, site.Rotation, point);
    }

    /// <summary>Null levels make an explicit sandbox with no population geography; ordinary worlds supply full level data.</summary>
    public static WorldContent Load(GameInstall install, GameDatabase db, GameAssets assets, Func<float, float, float> height,
        WorldLevelData? levels, BodyOptions? bodyOptions = null, BuildingLayouts? layouts = null, Action<string>? log = null)
    {
        NavSystem? nav = null;
        RagdollGeometry? geometry = null;
        try
        {
            IWalkability walkability = new OpenGroundWalkability(height);
            if (levels is not null)
            {
                nav = new NavSystem(install, db, levels, height, deferPublication: true);
                geometry = new RagdollGeometry(install, db, levels, height);
                walkability = new NavAdapter(nav.Walkability);
            }
            var towns = levels?.Towns().ToList() ?? [];
            BiomeMap? biomes = null;
            Dictionary<string, List<TownBuilding>>? buildings = null;
            if (levels is not null)
            {
                biomes = BiomeMap.Create(db, WeatherAreas.Load(install));
                layouts ??= new BuildingLayouts(db, levels.Interiors);
                buildings = TownBuildings.Build(db, levels, layouts, (x, z) => height((float)x, (float)z));
                log?.Invoke($"homes     {buildings.Values.Sum(l => l.Count)} town buildings, {buildings.Values.Sum(l => l.Sum(b => b.Beds))} beds");
            }
            var data = PopulationData.Create(db, towns, new GeneratedAppearances(db, install.Root),
                bodyOptions: bodyOptions ?? BodyOptions.Default, biomes: biomes, buildings: buildings,
                roads: levels?.Roads(), townZones: levels?.TownZones(), level: levels?.Level);
            if (levels is not null)
            {
                data.Sight = new BuildingSight(install, db, levels, layouts!);
                nav!.Walkability.Roads = data.Roads;
                var catalog = FoliageCatalog.Load(db);
                var cache = new FoliageLayoutCache(install, catalog, FoliageWorld.ReadTowns(db, levels));
                data.ResourceNodes = new Meitou.Data.Gameplay.Construction.FoliageResourceNodes(
                    new FoliageWorld(install, db, levels, catalog, cache: cache), db, assets.Find);
            }
            return new WorldContent
            {
                Population = data, Walkability = walkability, Navigation = nav, RagdollGeometry = geometry,
                Rigs = assets.CreateRigs(db), AnimationLengths = AnimationLengths.Load(install.Root, db, assets.Find),
                AnimationLibrary = AnimationLibrary.FromDatabase(db), Levels = levels, Towns = towns,
                footprints = new BuildingFootprints(install, db, levels),
                install = install, database = db, ground = height,
            };
        }
        catch
        {
            try { nav?.Dispose(); } finally { geometry?.Dispose(); }
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Navigation?.Dispose(); } finally { RagdollGeometry?.Dispose(); }
    }
}
