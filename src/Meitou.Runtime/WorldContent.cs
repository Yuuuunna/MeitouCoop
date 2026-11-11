using Meitou.Simulation.Construction;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Population;
using Meitou.Simulation.Perception;

namespace Meitou.Runtime;

/// <summary>
/// CPU gameplay content shared by graphical and dedicated hosts. Dispose after every world using it.
/// </summary>
public sealed class WorldContent : IDisposable
{
    BuildingFootprints? _footprints;
    GameInstall _install = null!;
    GameDatabase _database = null!;
    Func<float, float, float> _ground = null!;
    bool _disposed;
    WorldContent()
    {
    }

    public required PopulationData Population { get; init; }
    public required IWalkability Walkability { get; init; }
    public NavSystem? Navigation { get; init; }
    public RagdollGeometry? RagdollGeometry { get; init; }
    public required CharacterRigs Rigs { get; init; }
    public required AnimationLengths AnimationLengths { get; init; }
    public required AnimationLibrary AnimationLibrary { get; init; }
    public WorldLevelData? Levels { get; init; }
    public required IReadOnlyList<TownPlacement> Towns { get; init; }

    /// <summary>
    /// Independent mutable faction/population state over the same immutable content and CPU services.
    /// </summary>
    public PopulationData CreatePopulationData()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var layouts = Levels is null ? null : new BuildingLayouts(_database, Levels.Interiors);
        if (layouts is not null)
            Levels!.ConfigurePreparedLayouts(layouts);
        var buildings = Levels is null ? null : TownBuildings.Build(_database, Levels, layouts!, (x, z) => _ground((float)x, (float)z));
        var data = PopulationData.Create(_database, Towns, Population.Appearances, Population.BodyOptions, Population.Biomes, buildings: buildings, roads: Levels?.Roads(), townZones: Levels?.TownZones(), level: Levels?.Level);
        data.Layouts = layouts;
        data.Sight = Levels is null ? null : new BuildingSight(_install, _database, Levels, layouts!);
        data.ResourceNodes = Population.ResourceNodes;
        return data;
    }

    public StandardSystemSet BuildSystems(StandardSystemOptions? options = null, PopulationData? population = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var data = population ?? Population;
        var construction = options?.Construction;
        var placement = new Placement.ContentPlacement(_install, _database, data, Levels, _footprints!, _ground, p => Navigation is null || Navigation.IsReady(WorldLayout.ZoneOf(p.X, p.Z)));
        var configured = (options ?? new StandardSystemOptions())with
        {
            Construction = new Meitou.Simulation.Construction.ConstructionSettings
            {
                BuildingSpeed = construction?.BuildingSpeed ?? 1,
                Footprint = construction?.Footprint,
                EdgeGap = construction?.EdgeGap ?? SiteEdgeGap,
                Placement = construction?.Placement ?? placement.Validator,
            },
            AnimationLengths = AnimationLengths,
            AnimationLibrary = AnimationLibrary,
            AnimationBlendRate = data.Bodies.Constants.AnimationBlendRate,
            Ragdolls = Rigs,
            RagdollEnvironment = RagdollGeometry,
        };
        var result = StandardSystems.Build(data, Walkability, configured);
        if (Navigation is not null && result.Population is { } system)
            system.ZoneGate = Navigation.IsReady;
        return Navigation is null ? result : result with
        {
            Systems = [..result.Systems, new WorldFoliageOwnership(Levels!, data, Navigation), new WorldBuildings(_database, new NavigationBuildingSink(Navigation)), new WorldRegionNavigation(new NavigationRegions(Navigation)), new WorldNavigation(Navigation), new WorldGateRegions(Navigation)],
        };
    }

    public float? SiteEdgeGap(Meitou.Simulation.Construction.BuildSite site, System.Numerics.Vector2 point)
    {
        if (_footprints is null || !_footprints.Ready(site.Record))
            return null;
        return (_footprints.Of(site.Record) ?? _footprints.Plot(site.Record))?.Gap(site.Position, site.Rotation, point);
    }

    /// <summary>
    /// Null levels make an explicit sandbox with no population geography; ordinary worlds supply full level data.
    /// </summary>
    public static WorldContent Load(GameInstall install, GameDatabase db, GameAssets assets, Func<float, float, float> height, WorldLevelData? levels, BodyOptions? bodyOptions = null, BuildingLayouts? layouts = null, Action<string>? log = null)
    {
        NavSystem? nav = null;
        RagdollGeometry? geometry = null;
        try
        {
            if (levels is not null)
            {
                layouts ??= WorldBoot.LevelLayouts(db, levels);
                levels.ApplyBuildingSwaps(db, layouts);
            }

            var boot = WorldBoot.Load(new WorldBootOptions(install, db, height, assets) { Levels = levels, Layouts = layouts, Bodies = bodyOptions ?? BodyOptions.Default, DeferredNavigation = true, });
            nav = boot.Nav;
            geometry = boot.RagdollGeometry;
            return new WorldContent
            {
                Population = boot.Data,
                Walkability = boot.Walkability,
                Navigation = nav,
                RagdollGeometry = geometry,
                Rigs = boot.Rigs,
                AnimationLengths = boot.AnimationLengths,
                AnimationLibrary = boot.AnimationLibrary,
                Levels = levels,
                Towns = boot.Towns,
                _footprints = new BuildingFootprints(install, db, levels),
                _install = install,
                _database = db,
                _ground = height,
            };
        }
        catch
        {
            try
            {
                nav?.Dispose();
            }
            finally
            {
                geometry?.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            Navigation?.Dispose();
        }
        finally
        {
            RagdollGeometry?.Dispose();
        }
    }
}