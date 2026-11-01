using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Engine;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;
using Meitou.Runtime;
using Meitou.Networking;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Display;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Engine.Input.Key;
using EngineButton = Meitou.Engine.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;

namespace Meitou.Game;

/// <summary>
/// The game loop: a real-time control tick (<see cref="FixedStepClock"/>, 30 Hz by default) runs the input actions, the camera rig
/// and pause/speed; a game-time simulation tick (<see cref="SimulationClock"/>) advances the game clock; each displayed frame draws the camera interpolated between the last two ticks at the display rate.
/// </summary>
sealed partial class GameHost(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, GameOptions g, UserConfig config, Meitou.Data.Gameplay.NewGameStart? start)
{
    /// <summary>The save the run starts in (<c>--load</c>), loaded at the end of <see cref="Boot"/> through the menu's load path; null for a new world.</summary>
    public Meitou.Data.Save.SaveGame? StartSave { get; init; }
    /// <summary>Reads the world in the interactive run, on a worker behind the loading screen (the host owns the scene then); null: the scene was given.</summary>
    public Func<WorldScene?>? LoadScene { get; init; }
    WorldScene? ownedScene;
    /// <summary>The native splash shown since the program started; closed when the game window first shows.</summary>
    public IDisposable? Splash { get; init; }
    IClientSession session = null!;   // disposed with the host (Run)
    public Meitou.Networking.RemoteGameSession? OnlineConnection { get; init; }
    IReadOnlyList<ITickSystem> LocalSystems => session is WorldSession local ? local.World.Systems : [];
    WorldSession LocalSession => session as WorldSession ?? throw new InvalidOperationException("This operation requires a local simulation session.");
    Meitou.Simulation.PopulationSystem? population;
    /// <summary>The fights started by faction hostility (printed to the log); null in the sandbox and without the population.</summary>
    Meitou.Simulation.AggroSystem? aggro;
    // The game data the world was built from, kept to build the next world of a quickload (GameHost.Saving.cs).
    Meitou.Simulation.PopulationData? popData;
    IReadOnlyList<Meitou.Data.World.TownPlacement> townPlacements = [];
    // The level data, for the zone files and town handles of a save (null in the sandbox and the duel).
    Meitou.Data.World.WorldLevelData? saveLevels;
    Meitou.Simulation.IWalkability? simWalkability;
    Meitou.Data.Characters.CharacterRigs? characterRigs;
    Meitou.Navigation.RagdollGeometry? ragdollGeometry;
    Meitou.Navigation.NavSystem? nav;
    /// <summary>Zones whose mesh went live since the last <see cref="Meitou.Simulation.RepathCommand"/> (filled on the nav thread, under its own lock).</summary>
    /// <summary>Seconds between <see cref="Meitou.Simulation.RepathCommand"/>s at least: zones go live in runs along a long walk, and are sent together (engine choice).</summary>
    PlayerInterface player = null!;
    Meitou.Simulation.Squad? playerSquad;
    WorldFrame.Gpu gpu = null!;
    /// <summary>This run uses the native Metal loop: the shared world object exists, but its Vulkan post/loading path does not drive it.</summary>
    bool metalRenderer = false;   // set by the Metal loop (GameHost.Metal.cs, macOS builds only)
    // Fallback services for a host whose renderers are not in WorldFrame.Gpu yet (the shared title, audio and save code asks through these).
    WorldWeather? fallbackWeather = null;
    WorldObjectRenderer? hostObjects = null;
    FoliageRenderer? hostFoliage = null;
    CharacterRenderer? hostCharacters = null;
    WorldLamps? hostLamps = null;
    Func<float, float, float>? hostGround = null;
    WorldWeather? ActiveWeather => gpu is null ? fallbackWeather : gpu.Weather ?? fallbackWeather;
    WorldObjectRenderer? ActiveObjects => gpu is null ? hostObjects : gpu.Objects ?? hostObjects;
    FoliageRenderer? ActiveFoliage => gpu is null ? hostFoliage : gpu.Foliage ?? hostFoliage;
    CharacterRenderer? ActiveCharacters => gpu is null ? hostCharacters : gpu.Characters ?? hostCharacters;
    WorldLamps? ActiveLamps => gpu is null ? hostLamps : gpu.Lamps ?? hostLamps;
    float ActiveGround(float x, float z) => hostGround?.Invoke(x, z) ?? (gpu is null ? 0 : gpu.Terrain.HeightAt(x, z));
    WorldCamera camera = null!;
    WorldRenderOptions render = null!;
    readonly Stopwatch realTime = Stopwatch.StartNew();
    Meitou.Rendering.Upscalers.Streamline? streamline;
    Meitou.Rendering.Upscalers.Ngx? ngx;
    bool screenshotRequested;
    // The interactive run's animation clock (foliage sway, clouds, water): the frame steps, which are exact while the cinema records; off, the real time.
    double animSeconds;
    bool animClock;
    // Draw-list state (GameHost.Characters.cs): the previous snapshot indexed by id, and the last camera focus sent to the world.
    WorldSnapshot? indexedFor;
    readonly Dictionary<CharacterId, CharacterSnapshot> previousById = [];
    Meitou.Data.Gameplay.AnimationLengths? animationLengths;
    Meitou.Data.Gameplay.AnimationLibrary? animationLibrary;
    Vector3 lastFocus;
    bool sentFocus;
    // Profile of the interactive run (printed by --quit-after).
    double advanceTotal, fillTotal;
    long fillCalls;
    // --quit-after keeps the exposed menu's frame pacing separate from covered loading work.
    double menuFrameSeconds, menuLongestFrame;
    long menuFrames, menuSlowFrames;

    public int Run()
    {
        GameHost host = this;
        while (true)
        {
            int result = host.RunOnce();
            if (host.NextOnline is null && !host.ReturnToTitle) return result;
            var next = host.NextOnline;
            if (next is not null)
            {
                var view = next.State.View;
                var roles = view?.Roles.Where(r => r.Available && !r.Dead).Select(r => r.Character).ToHashSet();
                var focus = view?.Snapshot.Characters.FirstOrDefault(c => roles!.Contains(c.Id))?.Position;
                if (focus is null)
                {
                    next.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    next = null;
                }
                else
                {
                    (o.X, o.Z, o.Town, o.Zone) = (focus.Value.X, focus.Value.Z, null, null);
                    o.Day = (int)Math.Floor(view!.GameHours / 24); o.Hour = (float)(view.GameHours % 24);
                }
            }
            host = new GameHost(install, null!, assets, o, g, config, null)
            { OnlineConnection = next, LoadScene = () => WorldFrame.Load(install, o) };
        }
    }

    RemoteGameSession? NextOnline;
    bool ReturnToTitle;

    int RunOnce()
    {
        try
        {
#if MEITOU_METAL
            if (g.Renderer == "metal") return Metal();
#endif
            if (g.Renderer != "vulkan")
            {
                Console.Error.WriteLine($"the {g.Renderer} renderer is macOS only.");
                return 2;
            }
            return o.Screenshot is not null ? Screenshot() : Interactive();
        }
        finally
        {
            player?.DisposeOnline();
            DisposeAudio(); DisposeTitleScene(); DisposeClientSession();
            if (session is not RemotePlayerSession) OnlineConnection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            simulationContent?.Dispose(); ownedScene?.Dispose();
        }
    }

    void DisposeClientSession()
    {
        if (session is IAsyncDisposable asynchronous) asynchronous.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else if (session is IDisposable local) local.Dispose();
    }

    /// <summary>The start opens on the title (no start, save, sandbox or duel was asked for).</summary>
    bool OpensOnTitle(bool interactive) => OnlineConnection is null && (g.TitlePreview || interactive && start is null && StartSave is null && !IsSandbox && !IsDuel && !IsBattle && !g.NoPopulation);

    void Boot(VulkanDisplay display, bool interactive)
    {
        if (OnlineConnection is { } online) { BootOnline(display, interactive, online); return; }
        var context = display.Context;
        o.Day ??= (int)GameClock.DefaultStartDay;   // the weather schedule starts on the clock's first day (--day moves both)
        // --crowd in the game: simulated characters (SpawnCrowd), not the viewer's posed harness, which would take the renderer's place.
        crowd = o.Crowd;
        o.Crowd = 0;
        // The simulation's data (the navmesh system, the towns' homes, the population's data and systems) is read on a worker while the renderer is made here,
        // a step at a time with a loading frame between the steps (docs/engine.md "Loading").
        var simulation = Task.Run(() =>
        {
            var grid = new Meitou.Data.World.GroundHeights(scene.Window, scene.Coarse, scene.CoarseSize, WorldFrame.CoarseStep);
            return (Grid: grid, Parts: BootSimulation(grid, interactive));
        });
        gpu = WorldFrame.CreateGpu(context, install, scene, assets, o, interactive, LoadingStep, AwaitLoading);
        if (!o.NoParticles) titleParticleFactory = () => ParticleRenderer.Create(context, install, gpu.Particles?.Library);
        if (gpu.Post is { } vendorPost) vendorPost.UpscalerFactory = Meitou.Rendering.Upscalers.VendorUpscalers.Factory(display.Context, streamline, ngx);
        (camera, render) = WorldFrame.Setup(scene, o);
        render.Grid = IsFlat;
        LoadingStep("people");
        var (heights, (walkability, systems, constants)) = AwaitLoading(simulation);
        LoadingStep("world");
        var clock = GameClockFor(constants ??= scene.Database is { } clockDb ? Meitou.Data.Gameplay.GameConstants.FromDatabase(clockDb) : null, o.Hour, o.Day ?? GameClock.DefaultStartDay);
        session = new WorldSession(scene.Focus, (scene.X0, scene.Z0, scene.X1, scene.Z1), gpu.Terrain.HeightAt, o.Hour, config.TickRate,
            clock: clock,
            simulation: worldSettings = new Meitou.Simulation.WorldSettings
            {
                Seed = g.Seed, MinPartitionSize = 32, Threads = Math.Max(1, config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)),
                Sunrise = (float)clock.Sunrise, Sunset = (float)clock.Sunset,
            },
            systems: systems, walkability: walkability);
        BootPlayer(display, interactive);
    }

    /// <summary>
    /// The simulation's side of <see cref="Boot"/>, on a worker (it reads the game data and the scene's level data and makes no GPU resource): the navmesh system, the
    /// population's data and its systems. Returns the walkability, the systems and the game constants for the session.
    /// </summary>
    WorldContent? simulationContent;

    (Meitou.Simulation.IWalkability Walkability, IReadOnlyList<Meitou.Simulation.ITickSystem> Systems, Meitou.Data.Gameplay.GameConstants? Constants) BootSimulation(
        Meitou.Data.World.GroundHeights heights, bool interactive)
    {
        if (g.NoPopulation || scene.Database is not { } gameDb)
            return (new Meitou.Simulation.OpenGroundWalkability(heights.HeightAt), [], null);
        var levels = IsSandbox || IsDuel || IsBattle ? null
            : scene.Objects?.Levels ?? Meitou.Data.World.WorldLevelData.Load(install, includeInteriors: true);
        simulationContent = WorldContent.Load(install, gameDb, assets, heights.HeightAt, levels,
            Meitou.Simulation.Bodies.BodyOptions.Default with { Dismemberment = Gameplay.Dismemberment },
            scene.Objects?.Layouts, Console.WriteLine);
        nav = simulationContent.Navigation;
        ragdollGeometry = simulationContent.RagdollGeometry;
        characterRigs = simulationContent.Rigs;
        animationLengths = simulationContent.AnimationLengths;
        animationLibrary = simulationContent.AnimationLibrary;
        popData = simulationContent.Population;
        townPlacements = simulationContent.Towns;
        saveLevels = levels;
        simWalkability = simulationContent.Walkability;
        simulationWeather = levels is not null ? SimulationWeather.Load(install, gameDb, heights.HeightAt) : null;
        var built = BuildSystems(interactive);
        population = built.Population;
        aggro = built.Aggro;
        if (nav is not null) population!.ZoneGate = nav.IsReady;
        return (simWalkability, built.Systems, popData.Bodies.Constants);
    }

    /// <summary>
    /// The callbacks the interface asks the host for: the game data, the map, the build services, the tooltips and the character editor. Shared by the Vulkan
    /// and Metal loops so the two expose the same interaction layer.
    /// </summary>
    void ConfigurePlayerInterface(PlayerInterface ui)
    {
        ui.FixLoadButton = config.FixLoadButton;
        ui.SaveInUse = folder => session.CanSaveWorld && session.HasPlayer && saveInUse is { } used && string.Equals(Path.GetFullPath(used), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
        ui.DeleteSave = folder =>
        {
            try { Directory.Delete(folder, recursive: true); return null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Failed to delete save: " + e.Message; }
        };
        // The region (BIOME_GROUP) under a point, for the biome banner.
        ui.RegionAt = (x, z) => ActiveWeather?.World.RegionAt(x, z).Def is { } def ? (def.StringId, def.Name) : null;
        ui.BuildHost = MakeBuildServices();
        ConfigureFactionPresentation(ui);
        // A BUILDING's construction target (its Condition rows): the sum of its materials' required amounts.
        ui.BuildTarget = record => popData?.Db.Find(record) is { Type: Meitou.Data.Fcs.FcsRecordType.BUILDING } r
            ? Meitou.Data.Gameplay.Construction.BuildingBuildData.From(r).Materials.Sum(m => m.Required) : null;
        // The upgrade chain of a BUILDING (the panel's [UPGRADE] / [DOWNGRADE]) and its build data (their tooltips' materials).
        ui.UpgradeOf = record => popData is { } d ? Meitou.Simulation.Construction.BuildingUpgrades.UpgradeOf(d.Db, record) : null;
        ui.DowngradeOf = record => popData is { } d ? Meitou.Simulation.Construction.BuildingUpgrades.DowngradeOf(d.Db, record) : null;
        ui.BuildingData = record => popData?.Db.Find(record) is { Type: Meitou.Data.Fcs.FcsRecordType.BUILDING } r ? Meitou.Data.Gameplay.Construction.BuildingBuildData.From(r) : null;
        ui.PowerOutput = record => popData?.Db.Find(record)?.GetInt("power output");
        ui.SquadLimits = () => popData?.Bodies.Constants;
        ui.Rocks = () => popData?.ResourceNodes;
        ui.MapTowns = () => popData?.Sites ?? [];
        ui.ArmourOf = record => popData?.Combat.Armour(record);
        ui.RecordName = id => popData?.Db.Find(id)?.Name;
        Meitou.Data.Gameplay.Economy.PriceConstants? priceConstants = null;
        Meitou.Simulation.Trading.Market? shelf = null;
        ui.PricesOf = tip =>
        {
            if (popData is not { } d || d.Db.Find(tip.Record) is not { } r) return null;
            if (r.Type is Meitou.Data.Fcs.FcsRecordType.ARMOUR or Meitou.Data.Fcs.FcsRecordType.WEAPON or Meitou.Data.Fcs.FcsRecordType.CROSSBOW)
                return Meitou.Simulation.Trading.Market.GearPrices(d.Db, priceConstants ??= Meitou.Data.Gameplay.Economy.PriceConstants.FromDatabase(d.Db), tip.Record, tip.Level, tip.Company) is { } gear
                    ? (gear.Value, gear.Sell, null) : null;
            // No trader, so no town or culture factor (the seed only rolls those). StolenFrom is set only when the item is stolen for its holder; holder -1 keeps that.
            var m = shelf ??= new Meitou.Simulation.Trading.Market(d, 0);
            var item = new Meitou.Simulation.Items.ItemInstance
            {
                Record = tip.Record, Type = d.Db.Find(tip.Company) is { Type: Meitou.Data.Fcs.FcsRecordType.RESEARCH } ? Meitou.Data.Fcs.FcsRecordType.BLUEPRINT : r.Type, Quality = tip.Quality, Level = tip.Level, CompanyId = tip.Company, Charges = tip.Charges, MaxCharges = tip.MaxCharges,
                Quantity = tip.Quantity, StolenFrom = tip.StolenFrom,
            };
            var none = Meitou.Simulation.Trading.Trader.None;
            return (m.UnitPrice(item, false, none, -1), m.UnitPrice(item, true, none, -1), m.AveragePrice(item));
        };
        ui.ArtifactOf = id => popData?.Db.Find(id)?.GetBool("artifact") == true;
        ui.ItemRecordOf = id => popData?.Db.Find(id);
        ui.CombatOf = () => popData?.Combat;
        ui.MapItemTowns = record =>
        {
            if (session.IsRemote) return null; // exact map discoveries require the server's hidden world seed
            if (popData is not { } d || d.Db.Find(record) is not { Type: Meitou.Data.Fcs.FcsRecordType.MAP_ITEM } map) return null;
            return [.. Meitou.Simulation.Items.MapItems.Reveals(d, map, session.CurrentSnapshot.Towns, LocalSession.World.Seed).Select(i => d.Sites[i].Town.Name)];
        };
        ConfigureEditor(ui);
    }

    /// <summary>The rest of <see cref="Boot"/> once the session exists: bindings, the player's interface, the start (a new game's squad, the sandbox, the duel), the camera, a <c>--load</c> save.</summary>
    void BootPlayer(VulkanDisplay display, bool interactive)
    {
        var context = display.Context;
        var target = camera.Target;
        foreach (var problem in session.Bindings.Apply(config.Bindings)) Console.Error.WriteLine($"config    binding skipped: {problem}");
        // Right is the command button now; older saved configs bound it to orbit.
        session.Bindings.Set(InputAction.Orbit, [.. session.Bindings.Get(InputAction.Orbit).Where(b => !(b.IsMouse && b.Button == EngineButton.Right))]);
        // Ctrl orbits as the middle button does; older saved configs had the middle button alone.
        if (session.Bindings.Get(InputAction.Orbit).SequenceEqual([Binding.Of(EngineButton.Middle)])) session.Bindings.Set(InputAction.Orbit, EngineButton.Middle, EngineKey.LeftControl, EngineKey.RightControl);
        // F12 is the profiler now (as in the viewer); older saved configs took it for the screenshot.
        if (session.Bindings.Get(InputAction.Screenshot).SequenceEqual([Binding.Of(EngineKey.F12)])) session.Bindings.Set(InputAction.Screenshot, EngineKey.F8, EngineKey.PrintScreen);
        // The profiler and debug menu were briefly on F10 and Insert; configs saved then go back to F12 and F10.
        if (session.Bindings.Get(InputAction.CycleProfiler).SequenceEqual([Binding.Of(EngineKey.F10)])
            && session.Bindings.Get(InputAction.ToggleDebugMenu).SequenceEqual([Binding.Of(EngineKey.Insert)]))
        { session.Bindings.Set(InputAction.CycleProfiler, EngineKey.F12); session.Bindings.Set(InputAction.ToggleDebugMenu, EngineKey.F10); }
        // F9 is the game's quickload now (F5 quicksave); older saved configs took F9 for the fps counter, which is on Ctrl+F9 and unbound here.
        if (session.Bindings.Get(InputAction.ToggleFps).SequenceEqual([Binding.Of(EngineKey.F9)])) session.Bindings.Set(InputAction.ToggleFps);
        // Tab is the game's change_squad, not the settings (they open from the Esc menu); older saved configs bound Tab to them.
        if (session.Bindings.Get(InputAction.ToggleSettings).SequenceEqual([Binding.Of(EngineKey.Tab)])) session.Bindings.Set(InputAction.ToggleSettings);
        LoadingStep("interface");
        // Picks against the drawn terrain (the streamed fine window): the boot window's heights fall back to a 144-unit grid away from the start.
        player = new PlayerInterface(session, gpu.Terrain.HeightAt, DrawnPosition);
        player.LoadUi(g.UiMode ?? config.UiMode, assets.Find);
        player.SpeedUnit = config.SpeedUnit;
        player.Floaters = config.Floaters;
        player.FixLoadButton = config.FixLoadButton;
        // The region (BIOME_GROUP) under a point, for the biome banner.
        player.RegionAt = (x, z) => gpu.Weather?.World.RegionAt(x, z).Def is { } def ? (def.StringId, def.Name) : null;
        player.Markers = gpu.Markers ??= MarkerRenderer.Create(context, assets.Find);
        SetUpBlood(context);
        gpu.HoverOutline ??= new HoverOutlineRenderer(context);
        gpu.NavmeshOverlay ??= new NavmeshOverlayRenderer(context);
        navOverlayRenderer = gpu.NavmeshOverlay;
        player.MeshHit = (id, origin, direction) => gpu.Characters?.RayHit(DrawKey(id), origin, direction);
        player.Floor = (() => viewedFloor, StepFloor);
        player.HiddenInWorld = hiddenIndoors.Contains;
        player.BuildingMoveHit = (origin, direction, distance) => gpu.Objects?.MoveHit(origin, direction, viewedFloor, distance);
        player.SurfaceMoveHit = SurfaceMoveHit;
        player.ClosedBuildingHit = (origin, direction, distance) => gpu.Objects?.ClosedBuildingHit(origin, direction, distance);
        ConfigurePlayerInterface(player);
        if (IsSandbox) player.ReservedKeys.UnionWith([Meitou.Engine.Input.Key.Z, Meitou.Engine.Input.Key.X, Meitou.Engine.Input.Key.C, Meitou.Engine.Input.Key.Y, Meitou.Engine.Input.Key.P, Meitou.Engine.Input.Key.G]);
        player.ConfigureFrontend(scene.Database,
            () => Meitou.Data.Save.SaveCatalog.Scan(Meitou.Simulation.Saving.QuickSave.DefaultRoot, install.SaveDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kenshi", "save")),
            assets.Find("credits.txt") is { } creditsFile ? File.ReadAllText(creditsFile) : "");
        if (OpensOnTitle(interactive)) player.ShowTitle();
        if (nav is not null && (start is not null || !interactive))
        {
            // The squad is placed on the mesh, and a still has its town at once: wait for the zones round the start (a cold build is about 2 s, then cached).
            var watch = Stopwatch.StartNew();
            var zones = Meitou.Simulation.ZoneActivation.ZonesAround(scene.Focus, 0);
            using var preparation = LocalSession.World.Regions.Retain(zones, simulation: false);
            LoadingStep("navmesh");
            AwaitLoading(Task.WhenAll(zones.Select(z => (Task)nav.PrepareZone(z))));
            LocalSession.SynchronizeWorld();
            Console.WriteLine($"navmesh   {zones.Count} zone(s) round the start ready in {watch.ElapsedMilliseconds} ms");
        }
        if (start is not null && population is not null)
        {
            playerSquad = population.StartPlayer(LocalSession.World, start);
            var lead = LocalSession.World.Characters.Previous[playerSquad.Leader.Slot].Position;
            target = lead;
            Console.WriteLine($"player    {playerSquad.Members.Count} characters, leader at {lead.X:0}, {lead.Z:0}");
            // --new-game is a shortcut past the menus: the start's characters stay as rolled (the menu's New game opens the character editor); a still keeps the creation
            // open for the showcase's editor pictures.
            if (interactive) LocalSession.World.Commands.Enqueue(new Meitou.Simulation.Editing.FinishCreation { Tick = LocalSession.World.Tick });
        }
        if (IsSandbox && population is not null) target = SandboxSpawn();
        if (IsDuel && population is not null) target = DuelStart();
        if (IsBattle && population is not null) target = BattleStart();
        if (!IsSandbox && crowd > 0 && population is not null) SpawnCrowd(target, crowd);
        LoadingStep("characters");
        if (scene.Database is { } characterDb && !g.NoPopulation && gpu.Characters is null)
            gpu.Characters = new CharacterRenderer(context, install, characterDb, assets, characterRigs) { Source = FillDrawList, Guard = gpu.Guard, GeneratedLod = o.CharacterLod, SkinShading = o.SkinShading, SkinRaces = config.SkinRaces, EnhancedIcons = o.MeitouIcons,
                PoseClock = () => animClock ? animSeconds : realTime.Elapsed.TotalSeconds };   // the frame clock DrawWorld gives the foliage sway: a recorded flight poses the same every take
        var rig = session.Camera;
        rig.Place(new Vector2(target.X, target.Z), (o.Yaw ?? 30) * MathF.PI / 180,
            (o.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180, o.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance);
        SendFocus();
        ApplyCamera(session.Camera.Current);
        if (TitleVisible) ApplyTitleCamera();
        if (StartSave is { } save && g.Load is { } folder)
        {
            readingFolder = folder;
            Task navmesh;
            string summary;
            try { navmesh = SwapInSave(Task.FromResult(save), interactive, out summary); }
            catch (SceneLoadFailed e) { throw new InvalidDataException($"--load {folder}: {e.Message}"); }
            LoadingStep("navmesh");
            AwaitLoading(navmesh);
            LocalSession.SynchronizeWorld();
            SettleNewWorld();
            FinishLoadNotice(summary);
        }
        if (interactive)
        {
            LoadingStep("finishing");
            WorldFrame.FinishLoading(context);
            ReportLoading("start");
        }
    }

    /// <summary>The game clock with sunrise, sunset and days per year from the CONSTANTS record (defaults without data).</summary>
    static GameClock GameClockFor(Meitou.Data.Gameplay.GameConstants? c, double startHour, long startDay)
    {
        if (c is null) return new GameClock(startHour, startDay);
        return new GameClock(startHour, startDay, sunrise: c.Sunrise, sunset: c.Sunset, daysPerYear: c.DaysPerYear);
    }

    void ApplyCamera(CameraState s)
    {
        camera.Target = s.Target;
        camera.Yaw = s.Yaw;
        camera.Pitch = s.Pitch;
        camera.Distance = s.Distance;
    }

    /// <summary>
    /// Waits for requested startup geometry when asked. Runtime owns navigation publication and gate labels.
    /// </summary>
    void UpdateNav(bool wait = false)
    {
        if (nav is null || population is null || !wait || session is not WorldSession local) return;
        var pending = new List<Task<Meitou.Navigation.NavZoneReady>>();
        if (wait)
        {
            local.SynchronizeWorld();
            var wanted = population.ActiveZones.Concat(local.World.Regions.Capture(simulation: false).Keys)
                .Concat(nav.Walkability.RouteZones(TimeSpan.FromSeconds(30))).Distinct().ToArray();
            using var preparation = local.World.Regions.Retain(wanted, simulation: false);
            foreach (var zone in wanted) if (!nav.IsReady(zone)) pending.Add(nav.PrepareZone(zone));
            Task.WaitAll([.. pending]); local.SynchronizeWorld();
        }
    }

    /// <summary>Gives the player interface the camera it picks with.</summary>
    void UpdateView(int width, int height)
    {
        player.ViewProjection = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        player.Eye = camera.Eye;
        player.Width = width;
        player.Height = height;
    }

    void DrawWorld(int width, int height)
    {
        // The stage and the portraits finished as the world is (a change of the options redraws the portraits).
        if (gpu.Characters is { } previews)
        {
            previews.Look = Meitou.Rendering.Characters.PreviewLook.Of(o.Post);
            player.PortraitSize = Meitou.Rendering.Characters.CharacterRenderer.PortraitSizeFor(previews.Look);
            SyncIconLook();
        }
        if (TitleVisible) { DrawTitleWorld(width, height); return; }
        if (player.EditorOpen) { DrawEditorStage(width, height); return; }
        if (gpu.Characters is { StageRequest: not null } staged) staged.StageRequest = null;
        ClearEditorStage();
        double seconds = animClock ? animSeconds : realTime.Elapsed.TotalSeconds;
        if (gpu.Foliage is { } foliage && o.Screenshot is null) foliage.SwaySeconds = seconds;
        // The heat haze's gameTime: game hours since the start (it stops while paused, as in the game).
        gpu.GameHours = session.Clock.HoursSinceStart;
        // The sim's sight counts the interior lamps the renderer shows (the interiorlamps switch).
        if (session is WorldSession sightOwner) sightOwner.World.Sight.InteriorLampsAllDay = gpu.Lamps?.InteriorAllDay ?? o.InteriorLamps;
        // The weather's three time bases: the game day and time of day (the hour below), the game speed (the last non-zero one while paused) and the paused flag.
        if (gpu.Weather is { } weather)
        {
            weather.Day = (int)session.Clock.Day;
            weather.Paused = session.IsPaused || skyArrival is not null;
            weather.GameSpeed = (float)(session.TimeScale > 0 ? session.TimeScale : session is WorldSession localWeather ? localWeather.Simulation.LastNonZeroSpeed : 1);
        }
        Meitou.Rendering.StageClock.Lap(Meitou.Rendering.StageClock.Host);   // the profiler: the host's frame so far, then the blood marks apart from the terrain's update
        UpdateBlood();
        Meitou.Rendering.StageClock.Lap(Meitou.Rendering.StageClock.Blood, "blood marks");
        UpdateWeatherSource();
        UpdateInteriorView();
        // The frame's animation time (particles, water, wet surfaces) is game time: still while paused, faster at higher speeds.
        float gameTime = (float)(session.PresentationSeconds / 600 % 1e4);
        // The next world tick runs on its own thread while the world is drawn from the shown snapshots (docs/simulation.md "Tick ahead"): from here to the
        // join nothing the renderer calls back may touch the world, only the session's snapshots.
        if (tickAhead && session is WorldSession local) local.BeginTickAhead();
        try
        {
            WorldFrame.Draw(gpu, scene, camera, render, width, height, presentationHour ?? (float)session.Clock.HourOfDay, gameTime, o.FogDistance,
                weatherHour: (float)session.Clock.HourOfDay);
        }
        finally { if (session is WorldSession completedLocal) completedLocal.EndTickAhead(); }
    }

    /// <summary>This frame's world draw overlaps the next world tick (set by the interactive loop; never in stills, the duel, recorded flights or the play benchmark).</summary>
    bool tickAhead;

    /// <summary>
    /// Meitou's dynamic tick ahead (engine choice): on once a world tick takes <see cref="AheadOnMs"/> (smoothed), off again under <see cref="AheadOffMs"/>, so a quiet
    /// world keeps the game's input delay and a big fight or crowd overlaps; a tick run ahead is slower (it shares the cores with the draw), hence the gap.
    /// </summary>
    const double AheadOnMs = 4, AheadOffMs = 2;
    bool worldBusy;

    bool TickAheadWanted() => session is WorldSession local && config.TickAhead switch
    {
        TickAheadMode.Always => true,
        TickAheadMode.Dynamic => worldBusy = local.TickMs >= (worldBusy ? AheadOffMs : AheadOnMs),
        _ => false,
    };

}
