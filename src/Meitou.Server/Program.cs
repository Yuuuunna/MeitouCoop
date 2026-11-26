using Meitou.Simulation.Bodies;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Meitou.Content;
using Meitou.Modding;
using Meitou.Data.Gameplay;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Runtime;
using Meitou.Simulation;

namespace Meitou.Server;
static class Program
{
    const string Help = """
        Meitou experimental dedicated server (original Kenshi installation required)
        --config <file>             Server JSON; created on first use (default server.json).
        --create-account <name>     Issue an account and export a private connection JSON; then exit.
        --output <file>             Connection JSON destination (default server-data/connections/<account>.json).
        --run-for-seconds <seconds> Graceful bounded run for manual startup checks.
        --help                     Show this help without loading game content.
        Stop with Ctrl+C or enter stop. Save immediately by entering save.
        Game speed: speed [0|1|2|5] (0 pauses; no value shows the current speed). Enter help for console commands.
        Runtime diagnostics: diagnostics on | off | status | capture | collect-all.
        Set KENSHI_PATH or meitou.local.json to locate licensed original content.
        This is an experimental real-machine package, not a completed multiplayer acceptance release.
        """;
    public static async Task<int> Main(string[] args)
    {
        await using var latency = Meitou.Diagnostics.LatencyTraceSession.FromEnvironment();
        try
        {
            string configFile = "server.json";
            string? accountName = null, output = null;
            double? runSeconds = null;
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing command-line value.");
                switch (args[i])
                {
                    case "--help":
                    case "-h":
                        Console.WriteLine(Help);
                        return 0;
                    case "--config":
                        configFile = Next();
                        break;
                    case "--create-account":
                        accountName = Next();
                        break;
                    case "--output":
                        output = Next();
                        break;
                    case "--run-for-seconds":
                        runSeconds = double.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new ArgumentException("Unknown option: " + args[i]);
                }
            }

            if (runSeconds is { } limit && (!double.IsFinite(limit) || limit <= 0 || limit > 86400))
                throw new ArgumentException("Run duration must be 0–86400 seconds.");
            if (output is not null && accountName is null)
                throw new ArgumentException("--output requires --create-account.");
            var config = ServerConfiguration.Load(configFile);
            Directory.CreateDirectory(config.DataDirectory);
            using var diagnostics = new Meitou.Diagnostics.DiagnosticSession("server", ServerCompatibility.EngineBuild,
                Path.Combine(config.DataDirectory, "diagnostics"));
            using var diagnosticUploads = new Meitou.Diagnostics.DiagnosticUploadWorker(diagnostics.Store);
            using var certificate = ServerCertificate.Open(config.DataDirectory, config.AdvertisedHost);
            var install = GameInstall.Locate() ?? throw new DirectoryNotFoundException("Kenshi installation was not found. Set KENSHI_PATH or meitou.local.json.");
            Console.WriteLine("Meitou experimental server: computing local compatibility manifest.");
            if (config.ModsCfg is not null)
                ModList.UseModsCfg(config.ModsCfg);
            config = config.AdoptBuiltIns(install, configFile);
            var rules = ServerCompatibility.WithCodeSettings(config.Rules(), install, config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), config.ModSettings);
            CompatibilityHello hello;
            var accounts = new TokenAccountStore(Path.Combine(config.DataDirectory, "accounts.json"));
            if (accountName is not null)
            {
                var dataPhase = new ModDataPhase(install, config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), ModHost.RunningVersion());
                var db = dataPhase.LoadData();
                rules = ServerCompatibility.WithCodeSettings(rules, install, config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), config.ModSettings, db.CodeEdits);
                hello = ServerCompatibility.CreateHello(install, ServerCompatibility.EngineBuild, rules, config.GameplayAssets,
                    config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), config.ModSettings, db.CodeEdits);
                if (output is not null && File.Exists(Path.GetFullPath(output)))
                    throw new IOException("Connection output already exists; use another --output path.");
                var issued = accounts.Create(accountName);
                string path = output is null ? Path.Combine(config.DataDirectory, "connections", issued.Account.ToString("N") + ".json") : Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var profile = new ServerConnectionProfile(config.AdvertisedHost, config.Port, issued.Account, issued.Token, hello)
                {
                    Rules = rules,
                    GameplayAssets = config.GameplayAssets
                };
                if (File.Exists(path))
                    throw new IOException("Connection output already exists; use another --output path.");
                profile.Write(path);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Console.WriteLine("Account created. Private connection file: " + path);
                return 0;
            }

            using var instance = new FileStream(Path.Combine(config.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Console.WriteLine("Loading headless world; this may take several minutes.");
            using var host = HeadlessWorld.Open(install, new WorldSettings { Seed = config.WorldSeed, Threads = config.SimulationThreads, PublishSnapshots = false, }, trustedCodeMods: config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), modSettings: config.ModSettings, gameplay: config.Gameplay, notifyReady: false);
            host.Population.Relations.PlayerFaction = -1;
            var store = new CheckpointStore(Path.Combine(config.DataDirectory, "world"));
            var checkpoints = host.CreateCheckpointCoordinator(store);
            var saved = store.LoadCurrent();
            if (saved is not null)
            {
                var restored = host.RestoreCheckpoint(checkpoints, saved);
                checkpoints = restored.Coordinator;
                if (restored.CleanupFailure is not null)
                    throw new IOException("Old-world cleanup failed after restore.", restored.CleanupFailure);
                Console.WriteLine("Restored checkpoint " + saved.Id);
            }
            else if (host.Mods is { } mods) mods.TellReady(host.World, new WorldStart(WorldStartKind.NewGame));
            else host.World.TellReady(new WorldStart(WorldStartKind.NewGame));

            rules = ServerCompatibility.WithCodeSettings(config.Rules(GameplayRules.Capture(host.World)), install, config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), config.ModSettings, host.Population.Db.CodeEdits);
            host.World.ObserveLatency(Meitou.Diagnostics.LatencyTrace.Sink);
            hello = ServerCompatibility.CreateHello(install, ServerCompatibility.EngineBuild, rules, config.GameplayAssets, config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), config.ModSettings, host.Population.Db.CodeEdits);
            host.Population.Relations.PlayerFaction = -1;
            var starts = NewGameStart.LoadAll(host.Population.Db);
            var start = NewGameStart.Find(starts, config.DefaultStart) ?? throw new InvalidDataException("Configured default start was not found.");
            if (host.Population.PlayerFaction < 0)
                throw new InvalidDataException("The installed content has no player faction template.");
            host.Runtime.Speed = 0;
            await using var loop = new ServerWorldLoop(host.Runtime);
            var speed = new ServerGameSpeed(loop, config.PauseWhenEmpty);
            var preparation = new HeadlessCreationPreparation(host, loop);
            var entry = new HeadlessEntryPreparation(host, loop);
            const string policy = "server-default";
            var services = await loop.InvokeAsync(() =>
            {
                var creationEndpoint = new ServerCreationEndpoint(loop, checkpoints, host.Population, [new(policy, Math.Min(config.CharacterLimit, Math.Min(host.Population.Bodies.MaxSquadSize, host.Population.Bodies.MaxFactionSize)), starts.OrderBy(s => s.Id == start.Id ? 0 : 1).Select(s => new ApprovedCreationStart(s, host.Population.PlayerFaction)).ToArray())], preparation.PrepareAsync, policy);
                return (Creation: creationEndpoint, Invitations: new ServerInvitationEndpoint(loop, checkpoints, creationEndpoint), Sessions: new ServerAccountSessions(accounts, host.Runtime, checkpoints));
            }).ConfigureAwait(false);
            var creation = services.Creation;
            var invitations = services.Invitations;
            var sessions = services.Sessions;
            var handler = new SecureServerPeer(loop, sessions, hello, new WorldViewCodec(host.Population.Db), entry.PrepareAsync, creation: creation, invitations: invitations, information: new(hello, rules.ToDictionary(), config.GameplayAssets, config.AllowDeviceRegistration));
            var native = QuicDeployment.ResolveNative();
            var address = new IPEndPoint(IPAddress.Parse(config.BindAddress), config.Port);
            await using var listener = new QuicGameServer(address, native.Directory, native.ShimSha256, Path.Combine(config.DataDirectory, "server.pfx"), handler, config.MaximumPlayers);
            Console.WriteLine("Preparing enabled mod downloads.");
            using var modPackage = ServerModPackage.Create(install, Path.Combine(config.DataDirectory, "mod-downloads"), config.ShareMods,
                config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), config.ModSettings);
            await using var discovery = new TcpDiscoveryServer(address, certificate,
                new(hello, rules.ToDictionary(), config.GameplayAssets, config.AllowDeviceRegistration), mods: modPackage);
            using var stop = new CancellationTokenSource();
            if (runSeconds is { } seconds)
                stop.CancelAfter(TimeSpan.FromSeconds(seconds));
            ConsoleCancelEventHandler cancel = (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };
            Console.CancelKeyPress += cancel;
            Console.WriteLine($"Listening on {listener.Endpoint} (QUIC/UDP game, TCP public discovery); maximum {config.MaximumPlayers} connections. Enter help for console commands.");
            Console.WriteLine("Compatibility covers database merge order and " + config.GameplayAssets.Length + " configured gameplay assets.");
            using var saves = new SemaphoreSlim(1, 1);
            async Task Save()
            {
                await saves.WaitAsync().ConfigureAwait(false);
                try
                {
                    var captured = await loop.InvokeAsync(() => host.World.HasPendingDurableMutation ? null : checkpoints.Capture()).ConfigureAwait(false);
                    if (captured is not null)
                        await Task.Run(() => checkpoints.Commit(captured)).ConfigureAwait(false);
                    Console.WriteLine(captured is not null ? "Checkpoint saved." : "Checkpoint deferred: a critical persistence operation is pending.");
                }
                finally
                {
                    saves.Release();
                }
            }

            Console.WriteLine(ServerConsole.Help);
            var console = new ServerConsole(speed, diagnostics, handler.Diagnostics, Save, stop);
            var input = Task.Run(console.RunAsync);
            try
            {
                var nextSave = DateTime.UtcNow.AddSeconds(config.Gameplay.AutoSaveSeconds);
                while (!stop.IsCancellationRequested)
                {
                    var delay = Task.Delay(1000, stop.Token);
                    var completed = await Task.WhenAny(delay, loop.Completion, listener.Completion, discovery.Completion).ConfigureAwait(false);
                    if (completed == loop.Completion)
                    {
                        await loop.Completion.ConfigureAwait(false);
                        throw new IOException("World loop stopped unexpectedly.");
                    }

                    if (completed == listener.Completion)
                    {
                        await listener.Completion.ConfigureAwait(false);
                        throw new IOException("Listener stopped unexpectedly.");
                    }

                    if (completed == discovery.Completion)
                    {
                        await discovery.Completion.ConfigureAwait(false);
                        throw new IOException("Public discovery stopped unexpectedly.");
                    }

                    await delay.ConfigureAwait(false);
                    await speed.UpdateAsync(stop.Token).ConfigureAwait(false);
                    if (config.Gameplay.AutoSaveSeconds > 0 && DateTime.UtcNow >= nextSave)
                    {
                        try
                        {
                            await Save().ConfigureAwait(false);
                        }
                        catch (Exception error)when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            Console.Error.WriteLine("Autosave failed; previous checkpoint retained: " + error.Message);
                        }

                        nextSave = DateTime.UtcNow.AddSeconds(config.Gameplay.AutoSaveSeconds);
                    }
                }
            }
            catch (OperationCanceledException)when (stop.IsCancellationRequested)
            {
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
                stop.Cancel();
                await listener.DisposeAsync().ConfigureAwait(false);
                await input.ConfigureAwait(false);
            }

            await Save().ConfigureAwait(false);
            if (host.World.HasPendingDurableMutation)
                throw new IOException("Shutdown retains the last committed checkpoint; a critical persistence operation could not finish.");
            Console.WriteLine("Server stopped.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Server failed: " + error.GetType().Name + ": " + error.Message);
            Console.Error.WriteLine(error.StackTrace);
            return 1;
        }
    }
}
