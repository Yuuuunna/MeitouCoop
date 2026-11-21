using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;
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

/// <summary>
/// Real-content CPU host. Calls that touch Runtime/World are serialized by the server's world loop.
/// </summary>
public sealed class HeadlessWorld : IDisposable
{
    bool _disposed;
    readonly LoadOrder _loadOrder;
    readonly GameInstall _install;
    readonly IReadOnlySet<string>? _trustedCodeMods;
    readonly IReadOnlyDictionary<string, Dictionary<string, string>>? _modSettings;
    readonly StandardSystemOptions _systemOptions;
    readonly GameplayRules? _gameplay;
    HeadlessWorld(TerrainSource terrain, WorldContent content, WorldRuntime runtime, LoadOrder loadOrder, StandardSystemOptions systemOptions, ModHost? mods, GameInstall install, IReadOnlySet<string>? trustedCodeMods, IReadOnlyDictionary<string, Dictionary<string, string>>? modSettings, GameplayRules? gameplay)
    {
        Terrain = terrain;
        Content = content;
        Runtime = runtime;
        this._loadOrder = loadOrder;
        this._systemOptions = systemOptions;
        Mods = mods;
        this._install = install;
        this._trustedCodeMods = trustedCodeMods;
        this._modSettings = modSettings;
        this._gameplay = gameplay;
    }

    public ModHost? Mods { get; }
    public TerrainSource Terrain { get; }
    public WorldContent Content { get; }
    public SimulationWeather Weather => World.Systems.OfType<SimulationWeather>().Single();
    public WorldRuntime Runtime { get; }
    public World World => Runtime.World;
    public PopulationData Population => World.Systems.OfType<PopulationSystem>().SingleOrDefault()?.Data ?? Content.Population;

    /// <summary>
    /// The host supplies its release identity, authoritative settings and complete gameplay asset dependency list.
    /// </summary>
    public CompatibilityHello CreateCompatibilityHello(string engineBuild, IReadOnlyDictionary<string, string> rules, IEnumerable<ContentFile> gameplayAssets)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var manifest = ContentManifest.ReadLoadOrder(_loadOrder, ServerCompatibility.GameplayEvidence(_install, gameplayAssets, _trustedCodeMods));
        var effective = rules.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (_gameplay is not null)
        {
            foreach (string key in effective.Keys.Where(k => k.StartsWith("gameplay/", StringComparison.Ordinal)).ToArray())
                effective.Remove(key);
            foreach (var setting in System.Text.Json.JsonSerializer.SerializeToElement(GameplayRules.Capture(World)).EnumerateObject())
                effective.Add("gameplay/" + setting.Name, setting.Value.GetRawText());
        }

        return new CompatibilityHello(CompatibilityHandshake.Protocol, engineBuild, ContentManifest.RulesDigest(ServerCompatibility.WithCodeSettings(effective, _install, _trustedCodeMods, _modSettings, Population.Db.CodeEdits)), manifest.Digest);
    }

    public WorldCheckpointCoordinator CreateCheckpointCoordinator(CheckpointStore store, LoadedSave? loaded = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new WorldCheckpointCoordinator(Runtime, Population, store, new SaveCaptureOptions { DataFiles = Population.Db.Files, Levels = Content.Levels, Placements = Content.Levels?.Towns().ToArray(), Seed = World.Seed, }, loaded, Mods);
    }

    /// <summary>
    /// Serialized startup/maintenance restore; connection and policy services must bind to the returned coordinator/new world.
    /// </summary>
    public CheckpointRestoreResult RestoreCheckpoint(WorldCheckpointCoordinator coordinator, StoredCheckpoint checkpoint, bool deferPlayerRoles = true)
    {
        coordinator.VerifyOwner(Runtime);
        var candidate = Content.CreatePopulationData().WithBodyOptions(Population.BodyOptions with { });
        var candidateWeather = Weather.CreateSibling();
        return coordinator.Restore(checkpoint, candidate, data => WithModFeeds(Content.BuildSystems(_systemOptions with { Environment = candidateWeather }, data).Systems, Mods), new SaveCaptureOptions { DataFiles = candidate.Db.Files, Levels = Content.Levels, Placements = Content.Levels?.Towns().ToArray() }, candidate.Appearances, deferPlayerRoles, _gameplay is null ? null : world => ApplyWorldRules(world, _gameplay));
    }

    public static HeadlessWorld Open(GameInstall install, WorldSettings? settings = null, StandardSystemOptions? options = null, IReadOnlySet<string>? trustedCodeMods = null, IReadOnlyDictionary<string, Dictionary<string, string>>? modSettings = null, GameplayRules? gameplay = null, bool notifyReady = true)
    {
        HeightmapRepair.Configure(install, gameplay?.GroundRepair ?? true);
        var order = LoadOrder.FromInstall(install);
        var allowed = trustedCodeMods ?? new HashSet<string>(StringComparer.Ordinal);
        var dataPhase = new ModDataPhase(install, allowed, ModHost.RunningVersion());
        var db = dataPhase.LoadData();
        var levels = WorldLevelData.Load(install, includeInteriors: true);
        var terrain = TerrainSource.Open(install);
        WorldContent? content = null;
        WorldRuntime? runtime = null;
        try
        {
            content = WorldContent.Load(install, db, new GameAssets(install), terrain.HeightAt, levels,
                gameplay is null ? null : new Meitou.Simulation.Bodies.BodyOptions
                {
                    Dismemberment = gameplay.Dismemberment,
                    Decapitation = gameplay.Decapitation,
                    AdvancedRagdolls = gameplay.AdvancedRagdolls,
                    GlobalDamageMultiplier = gameplay.GlobalDamageMultiplier,
                    HungerTime = gameplay.HungerTime,
                    ChanceOfDeath = gameplay.ChanceOfDeath,
                    SquadXp = gameplay.SquadXp,
                    OthersXp = gameplay.OthersXp,
                });
            var (enabled, disabled) = dataPhase.CodeMods();
            BuiltInMods.SkipCovered(enabled, disabled);
            var modOptions = new ModHostOptions(enabled, allowed, db, content.Population, () => runtime?.World,
                id => modSettings?.GetValueOrDefault(id), GameVersion: ModHost.RunningVersion(), Disabled: disabled, StrictSystems: true);
            var mods = dataPhase.Mods;
            if (mods is null) mods = ModHost.Load(modOptions);
            else mods.Start(modOptions);
            if (mods.Refused.Keys.Any(name => trustedCodeMods?.Contains(name) == true))
                throw new InvalidDataException("A trusted authoritative code mod failed to initialize.");
            var weather = SimulationWeather.Load(install, db, terrain.HeightAt);
            var ruleOptions = gameplay is null ? options : ConfigureGameplay(options, gameplay, content);
            var configured = (ruleOptions ?? new StandardSystemOptions())with
            {
                Environment = weather,
                Extensions = MergeExtensions(ruleOptions?.Extensions, mods.Simulation),
                Construction = ruleOptions?.Construction ?? new Meitou.Simulation.Construction.ConstructionSettings
                {
                    EdgeGap = content.SiteEdgeGap
                },
                Production = ruleOptions?.Production ?? new Meitou.Simulation.Production.ProductionSettings
                {
                    EdgeGap = content.SiteEdgeGap
                },
            };
            var systems = content.BuildSystems(configured);
            var constants = content.Population.Bodies.Constants;
            var worldSettings = settings ?? new WorldSettings
            {
                Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
                Sunrise = constants.Sunrise,
                Sunset = constants.Sunset,
            };
            runtime = new WorldRuntime(worldSettings, content.Walkability, WithModFeeds(systems.Systems, mods));
            if (gameplay is not null)
                ApplyWorldRules(runtime.World, gameplay);
            if (notifyReady) mods.TellReady(runtime.World, fromSave: false);
            return new HeadlessWorld(terrain, content, runtime, order, configured, mods, install, trustedCodeMods, modSettings, gameplay);
        }
        catch
        {
            try
            {
                runtime?.Dispose();
            }
            finally
            {
                try
                {
                    content?.Dispose();
                }
                finally
                {
                    terrain.Dispose();
                }
            }

            throw;
        }
    }

    static StandardSystemOptions ConfigureGameplay(StandardSystemOptions? options, GameplayRules rules, WorldContent content) => ConfigureGameplay(options, rules, content.SiteEdgeGap);
    internal static StandardSystemOptions ConfigureGameplay(StandardSystemOptions? options, GameplayRules rules, Func<Meitou.Simulation.Construction.BuildSite, System.Numerics.Vector2, float?>? siteEdgeGap = null) => (options ?? new StandardSystemOptions())with
    {
        Combat = new Meitou.Simulation.Combat.CombatOptions
        {
            SelfApproach = false,
            TickMedical = false,
            AttackSlots = rules.AttackSlots,
            StuckLimit = rules.HarpoonLimit,
            LateDefences = rules.LateDefences,
            GunnerFacesBarrel = rules.GunnerFacesBarrel,
            PunchReachFix = rules.PunchReachFix,
            AnimalMovesFix = rules.AnimalMovesFix,
        },
        Population = new PopulationSettings
        {
            SquadSizeMultiplier = rules.SquadSize,
            PopulationMultiplier = rules.Population,
            RaidSizeMult = rules.RaidSize,
            RaidFrequencyMult = rules.RaidFrequency,
            NestsMultiplier = rules.NestsMultiplier,
            FastZoneHopping = rules.FastZoneHopping,
            KeepRoamers = rules.KeepRoamers,
            OffscreenFights = rules.OffscreenFights,
            OffscreenHealing = rules.OffscreenHealing,
            CampaignAncestorsOnce = rules.CampaignAncestorsOnce,
            KeepWild = rules.KeepWild,
            ResidentSchedule = rules.ResidentSchedule,
            OffscreenOverrides = rules.OffscreenOverrides,
            KeepCampaignClocks = rules.KeepCampaignClocks,
        },
        Ai = new Meitou.Simulation.Ai.AiSettings
        {
            TownAttacks = rules.TownAttacks
        },
        Dialogue = options?.Dialogue ?? new Meitou.Simulation.Dialogue.DialogueSettings
        {
            AmbientEvents = true
        },
        Construction = new Meitou.Simulation.Construction.ConstructionSettings
        {
            BuildingSpeed = rules.BuildingSpeed,
            EdgeGap = siteEdgeGap
        },
        Production = new Meitou.Simulation.Production.ProductionSettings
        {
            ProductionOption = rules.ProductionSpeed,
            ResearchOption = rules.ResearchSpeed,
            UnloadedProduction = rules.UnloadedProduction,
            EdgeGap = siteEdgeGap,
        },
        Captives = new Meitou.Simulation.Kidnap.CaptiveSettings { SpareOwnFaction = rules.CagesSpareOwn },
        Arrest = new Meitou.Simulation.Crime.ArrestSettings { LiftFix = rules.ArrestLiftFix },
        AnimalsPickUp = rules.AnimalsPickUp,
    };
    static void ApplyWorldRules(World world, GameplayRules rules)
    {
        world.BenchFacingFix = rules.BenchFacingFix;
        world.PrisonPoseFix = rules.PrisonPoseFix;
        world.CagedAnimalFix = rules.CagedAnimalFix;
        world.GroundRepair = rules.GroundRepair;
        world.BagSearchFix = rules.BagSearchFix;
        world.Luckyren = rules.Luckyren;
        world.GroundItems.Limit = rules.GroundItemLimit;
        world.Sight.InteriorLampsAllDay = rules.InteriorLampsAllDay;
        world.AuthorityAutoSaveSeconds = rules.AutoSaveSeconds;
    }

    static IEnumerable<ITickSystem> WithModFeeds(IEnumerable<ITickSystem> systems, ModHost? mods) => mods is { WantsCombatHits: true } ? systems.Append(new ModHitFeedService(mods)) : systems;
    static SimulationExtensions? MergeExtensions(SimulationExtensions? configured, SimulationExtensions? mods)
    {
        if (configured is null)
            return mods;
        if (mods is null)
            return configured;
        var result = new SimulationExtensions();
        result.Systems.AddRange(configured.Systems);
        result.Systems.AddRange(mods.Systems);
        result.CombatMoves.AddRange(configured.CombatMoves);
        result.CombatMoves.AddRange(mods.CombatMoves);
        foreach (var(code, handler)in configured.DialogueConditions.Concat(mods.DialogueConditions))
            result.DialogueConditions.Add(code, handler);
        foreach (var(code, handler)in configured.DialogueEffects.Concat(mods.DialogueEffects))
            result.DialogueEffects.Add(code, handler);
        foreach (var (record, handler) in configured.ItemUses.Concat(mods.ItemUses))
            result.ItemUses.Add(record, handler);
        foreach (var (row, name) in configured.JobTypes.Concat(mods.JobTypes)) result.JobTypes.Add(row, name);
        foreach (var (row, goal) in configured.GoalTypes.Concat(mods.GoalTypes)) result.GoalTypes.Add(row, goal);
        foreach (var (row, owner) in configured.TaskOwners.Concat(mods.TaskOwners)) result.TaskOwners.Add(row, owner);
        return result;
    }

    /// <summary>
    /// Prepare authoritative navigation before spawning or accepting movement in an arrival region.
    /// </summary>
    public async Task PrepareRegionAsync(Vector3 focus, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var centre = WorldLayout.ZoneOf(focus.X, focus.Z);
        if (!centre.IsInsideGrid)
            throw new ArgumentOutOfRangeException(nameof(focus));
        var zones = new List<ZoneCoordinate>();
        for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++)
            {
                var zone = new ZoneCoordinate(centre.X + x, centre.Y + z);
                if (zone.IsInsideGrid)
                    zones.Add(zone);
            }

        using var preparation = World.Regions.Retain(zones, simulation: false);
        Runtime.Synchronize();
        do
        {
            await Task.WhenAll(zones.Select(Content.Navigation!.PrepareZone)).WaitAsync(cancellationToken).ConfigureAwait(false);
            Runtime.Synchronize();
        }
        while (zones.Any(z => !Content.Navigation!.IsReady(z)));
    }

    public int Advance(double seconds, int maxTicks = 8)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Runtime.Advance(seconds, maxTicks);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            Runtime.Dispose();
        }
        finally
        {
            try
            {
                Content.Dispose();
            }
            finally
            {
                Terrain.Dispose();
            }
        }
    }
}
