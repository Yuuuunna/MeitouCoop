using System.Numerics;
using Meitou.Modding;
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
    readonly GameInstall install;
    readonly IReadOnlySet<string>? trustedCodeMods;
    readonly IReadOnlyDictionary<string, Dictionary<string, string>>? modSettings;
    readonly StandardSystemOptions systemOptions;
    readonly GameplayRules? gameplay;

    HeadlessWorld(TerrainSource terrain, WorldContent content, WorldRuntime runtime, LoadOrder loadOrder, StandardSystemOptions systemOptions, ModHost? mods, GameInstall install, IReadOnlySet<string>? trustedCodeMods,
        IReadOnlyDictionary<string, Dictionary<string, string>>? modSettings, GameplayRules? gameplay)
    {
        Terrain = terrain;
        Content = content;
        Runtime = runtime;
        this.loadOrder = loadOrder;
        this.systemOptions = systemOptions;
        Mods = mods;
        this.install = install;
        this.trustedCodeMods = trustedCodeMods;
        this.modSettings = modSettings;
        this.gameplay = gameplay;
    }

    public ModHost? Mods { get; }
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
        var manifest = ContentManifest.ReadLoadOrder(loadOrder, gameplayAssets.Concat(ServerCompatibility.CodeEvidence(install, trustedCodeMods)));
        var effective = rules.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (gameplay is not null)
        {
            foreach (string key in effective.Keys.Where(k => k.StartsWith("gameplay/", StringComparison.Ordinal)).ToArray()) effective.Remove(key);
            foreach (var setting in System.Text.Json.JsonSerializer.SerializeToElement(GameplayRules.Capture(World)).EnumerateObject())
                effective.Add("gameplay/" + setting.Name, setting.Value.GetRawText());
        }
        return new CompatibilityHello(CompatibilityHandshake.Protocol, engineBuild,
            ContentManifest.RulesDigest(ServerCompatibility.WithCodeSettings(effective, install, trustedCodeMods, modSettings)), manifest.Digest);
    }

    public WorldCheckpointCoordinator CreateCheckpointCoordinator(CheckpointStore store, LoadedSave? loaded = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new WorldCheckpointCoordinator(Runtime, Population, store, new SaveCaptureOptions
        {
            DataFiles = Population.Db.Files, Levels = Content.Levels,
            Placements = Content.Levels?.Towns().ToArray(), Seed = World.Seed,
        }, loaded, Mods);
    }

    /// <summary>Serialized startup/maintenance restore; connection and policy services must bind to the returned coordinator/new world.</summary>
    public CheckpointRestoreResult RestoreCheckpoint(WorldCheckpointCoordinator coordinator, StoredCheckpoint checkpoint, bool deferPlayerRoles = true)
    {
        coordinator.VerifyOwner(Runtime);
        var candidate = Content.CreatePopulationData().WithBodyOptions(Population.BodyOptions with { });
        var candidateWeather = Weather.CreateSibling();
        return coordinator.Restore(checkpoint, candidate, data => WithModFeeds(Content.BuildSystems(systemOptions with { Environment = candidateWeather }, data).Systems, Mods),
            new SaveCaptureOptions { DataFiles = candidate.Db.Files, Levels = Content.Levels, Placements = Content.Levels?.Towns().ToArray() },
            candidate.Appearances, deferPlayerRoles, gameplay is null ? null : world => ApplyWorldRules(world, gameplay));
    }

    public static HeadlessWorld Open(GameInstall install, WorldSettings? settings = null, StandardSystemOptions? options = null, IReadOnlySet<string>? trustedCodeMods = null,
        IReadOnlyDictionary<string, Dictionary<string, string>>? modSettings = null, GameplayRules? gameplay = null)
    {
        var order = LoadOrder.FromInstall(install);
        var db = GameDatabase.Load(order);
        var levels = WorldLevelData.Load(install, includeInteriors: true);
        var terrain = TerrainSource.Open(install);
        WorldContent? content = null;
        WorldRuntime? runtime = null;
        try
        {
            content = WorldContent.Load(install, db, new GameAssets(install), terrain.HeightAt, levels,
                gameplay is null ? null : new Meitou.Simulation.Bodies.BodyOptions
                {
                    Dismemberment = gameplay.Dismemberment, Decapitation = gameplay.Decapitation, AdvancedRagdolls = gameplay.AdvancedRagdolls,
                    GlobalDamageMultiplier = gameplay.GlobalDamageMultiplier, HungerTime = gameplay.HungerTime, ChanceOfDeath = gameplay.ChanceOfDeath,
                });
            var mods = ModHost.Load(new ModHostOptions(ModHost.EnabledCodeMods(install), trustedCodeMods ?? new HashSet<string>(StringComparer.Ordinal),
                db, content.Population, () => runtime?.World, id => modSettings?.GetValueOrDefault(id), StrictSystems: true));
            if (mods.Refused.Keys.Any(name => trustedCodeMods?.Contains(name) == true))
                throw new InvalidDataException("A trusted authoritative code mod failed to initialize.");
            var weather = SimulationWeather.Load(install, db, terrain.HeightAt);
            var ruleOptions = gameplay is null ? options : ConfigureGameplay(options, gameplay, content);
            var configured = (ruleOptions ?? new StandardSystemOptions()) with
            {
                Environment = weather,
                Extensions = MergeExtensions(ruleOptions?.Extensions, mods.Simulation),
                Construction = ruleOptions?.Construction ?? new Meitou.Simulation.Construction.ConstructionSettings { EdgeGap = content.SiteEdgeGap },
                Production = ruleOptions?.Production ?? new Meitou.Simulation.Production.ProductionSettings { EdgeGap = content.SiteEdgeGap },
            };
            var systems = content.BuildSystems(configured);
            var constants = content.Population.Bodies.Constants;
            var worldSettings = settings ?? new WorldSettings
            {
                Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
                Sunrise = constants.Sunrise, Sunset = constants.Sunset,
            };
            runtime = new WorldRuntime(worldSettings, content.Walkability, WithModFeeds(systems.Systems, mods));
            if (gameplay is not null) ApplyWorldRules(runtime.World, gameplay);
            return new HeadlessWorld(terrain, content, runtime, order, configured, mods, install, trustedCodeMods, modSettings, gameplay);
        }
        catch
        {
            try { runtime?.Dispose(); }
            finally { try { content?.Dispose(); } finally { terrain.Dispose(); } }
            throw;
        }
    }

    static StandardSystemOptions ConfigureGameplay(StandardSystemOptions? options, GameplayRules rules, WorldContent content) =>
        ConfigureGameplay(options, rules, content.SiteEdgeGap);

    internal static StandardSystemOptions ConfigureGameplay(StandardSystemOptions? options, GameplayRules rules,
        Func<Meitou.Simulation.Construction.BuildSite, System.Numerics.Vector2, float?>? siteEdgeGap = null) =>
        (options ?? new StandardSystemOptions()) with
        {
            Combat = new Meitou.Simulation.Combat.CombatOptions
            {
                SelfApproach = false, TickMedical = false, AttackSlots = rules.AttackSlots, StuckLimit = rules.HarpoonLimit,
                LateDefences = rules.LateDefences, GunnerFacesBarrel = rules.GunnerFacesBarrel, PunchReachFix = rules.PunchReachFix,
            },
            Population = new PopulationSettings
            {
                SquadSizeMultiplier = rules.SquadSize, PopulationMultiplier = rules.Population,
                RaidSizeMult = rules.RaidSize, RaidFrequencyMult = rules.RaidFrequency, NestsMultiplier = rules.NestsMultiplier,
                FastZoneHopping = rules.FastZoneHopping, KeepRoamers = rules.KeepRoamers, OffscreenFights = rules.OffscreenFights,
                OffscreenHealing = rules.OffscreenHealing, CampaignAncestorsOnce = rules.CampaignAncestorsOnce,
                KeepWild = rules.KeepWild, ResidentSchedule = rules.ResidentSchedule,
            },
            Ai = new Meitou.Simulation.Ai.AiSettings { TownAttacks = rules.TownAttacks },
            Dialogue = options?.Dialogue ?? new Meitou.Simulation.Dialogue.DialogueSettings { AmbientEvents = true },
            Construction = new Meitou.Simulation.Construction.ConstructionSettings { BuildingSpeed = rules.BuildingSpeed, EdgeGap = siteEdgeGap },
            Production = new Meitou.Simulation.Production.ProductionSettings
            {
                ProductionOption = rules.ProductionSpeed, ResearchOption = rules.ResearchSpeed,
                UnloadedProduction = rules.UnloadedProduction, EdgeGap = siteEdgeGap,
            },
            DoorsCloseBehind = rules.DoorsCloseBehind,
        };

    static void ApplyWorldRules(World world, GameplayRules rules)
    {
        world.BenchFacingFix = rules.BenchFacingFix;
        world.Luckyren = rules.Luckyren;
        world.GroundItems.Limit = rules.GroundItemLimit;
        world.Sight.InteriorLampsAllDay = rules.InteriorLampsAllDay;
        world.AuthorityAutoSaveSeconds = rules.AutoSaveSeconds;
    }

    static IEnumerable<ITickSystem> WithModFeeds(IEnumerable<ITickSystem> systems, ModHost? mods) =>
        mods is { WantsCombatHits: true } ? systems.Append(new ModHitFeedService(mods)) : systems;

    static SimulationExtensions? MergeExtensions(SimulationExtensions? configured, SimulationExtensions? mods)
    {
        if (configured is null) return mods;
        if (mods is null) return configured;
        var result = new SimulationExtensions();
        result.Systems.AddRange(configured.Systems);
        result.Systems.AddRange(mods.Systems);
        foreach (var (code, handler) in configured.DialogueConditions.Concat(mods.DialogueConditions)) result.DialogueConditions.Add(code, handler);
        foreach (var (code, handler) in configured.DialogueEffects.Concat(mods.DialogueEffects)) result.DialogueEffects.Add(code, handler);
        return result;
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
