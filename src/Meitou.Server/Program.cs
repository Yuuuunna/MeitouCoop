using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Meitou.Content;
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
        Set KENSHI_PATH or meitou.local.json to locate licensed original content.
        This is an experimental real-machine package, not a completed multiplayer acceptance release.
        """;

    public static async Task<int> Main(string[] args)
    {
        await using var latency = Meitou.Diagnostics.LatencyTraceSession.FromEnvironment();
        try
        {
            string configFile = "server.json"; string? accountName = null, output = null; double? runSeconds = null;
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing command-line value.");
                switch (args[i])
                {
                    case "--help": case "-h": Console.WriteLine(Help); return 0;
                    case "--config": configFile = Next(); break;
                    case "--create-account": accountName = Next(); break;
                    case "--output": output = Next(); break;
                    case "--run-for-seconds": runSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException("Unknown option: " + args[i]);
                }
            }
            if (runSeconds is { } limit && (!double.IsFinite(limit) || limit <= 0 || limit > 86400)) throw new ArgumentException("Run duration must be 0–86400 seconds.");
            if (output is not null && accountName is null) throw new ArgumentException("--output requires --create-account.");
            var config = ServerConfiguration.Load(configFile);
            Directory.CreateDirectory(config.DataDirectory);
            using var certificate = ServerCertificate.Open(config.DataDirectory, config.AdvertisedHost);
            var install = GameInstall.Locate() ?? throw new DirectoryNotFoundException("Kenshi installation was not found. Set KENSHI_PATH or meitou.local.json.");
            Console.WriteLine("Meitou experimental server: computing local compatibility manifest.");
            if (config.ModsCfg is not null) ModList.UseModsCfg(config.ModsCfg);
            var rules = ServerCompatibility.WithCodeSettings(config.Rules(), install, config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), config.ModSettings);
            var hello = ServerCompatibility.CreateHello(install, ServerCompatibility.EngineBuild, rules, config.GameplayAssets, config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), config.ModSettings);
            var accounts = new TokenAccountStore(Path.Combine(config.DataDirectory, "accounts.json"));
            if (accountName is not null)
            {
                if (output is not null && File.Exists(Path.GetFullPath(output))) throw new IOException("Connection output already exists; use another --output path.");
                var issued = accounts.Create(accountName);
                string path = output is null ? Path.Combine(config.DataDirectory, "connections", issued.Account.ToString("N") + ".json") : Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var profile = new ServerConnectionProfile(config.AdvertisedHost, config.Port, issued.Account, issued.Token, hello) { Rules = rules, GameplayAssets = config.GameplayAssets };
                if (File.Exists(path)) throw new IOException("Connection output already exists; use another --output path.");
                profile.Write(path);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Console.WriteLine("Account created. Private connection file: " + path);
                return 0;
            }
            using var instance = new FileStream(Path.Combine(config.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Console.WriteLine("Loading headless world; this may take several minutes.");
            using var host = HeadlessWorld.Open(install, new WorldSettings
            {
                Seed = config.WorldSeed, Threads = config.SimulationThreads, PublishSnapshots = false,
            }, trustedCodeMods: config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), modSettings: config.ModSettings, gameplay: config.Gameplay);

            host.Population.Relations.PlayerFaction = -1;
            var store = new CheckpointStore(Path.Combine(config.DataDirectory, "world"));
            var checkpoints = host.CreateCheckpointCoordinator(store);
            var saved = store.LoadCurrent();
            if (saved is not null)
            {
                var restored = host.RestoreCheckpoint(checkpoints, saved);
                checkpoints = restored.Coordinator;
                if (restored.CleanupFailure is not null) throw new IOException("Old-world cleanup failed after restore.", restored.CleanupFailure);
                Console.WriteLine("Restored checkpoint " + saved.Id);
            }
            rules = ServerCompatibility.WithCodeSettings(config.Rules(GameplayRules.Capture(host.World)), install,
                config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), config.ModSettings);
            host.World.ObserveLatency(Meitou.Diagnostics.LatencyTrace.Sink);
            hello = ServerCompatibility.CreateHello(install, ServerCompatibility.EngineBuild, rules, config.GameplayAssets,
                config.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), config.ModSettings);
            host.Population.Relations.PlayerFaction = -1;
            var starts = NewGameStart.LoadAll(host.Population.Db);
            var start = NewGameStart.Find(starts, config.DefaultStart) ?? throw new InvalidDataException("Configured default start was not found.");
            if (host.Population.PlayerFaction < 0) throw new InvalidDataException("The installed content has no player faction template.");
            host.Runtime.Speed = 0;
            await using var loop = new ServerWorldLoop(host.Runtime);
            var preparation = new HeadlessCreationPreparation(host, loop);
            var entry = new HeadlessEntryPreparation(host, loop);
            const string policy = "server-default";
            var services = await loop.InvokeAsync(() =>
            {
                var creationEndpoint = new ServerCreationEndpoint(loop, checkpoints, host.Population,
                    [new(policy, Math.Min(config.CharacterLimit, host.Population.Bodies.Constants.MaxSquadSize), starts.OrderBy(s => s.Id == start.Id ? 0 : 1).Select(s => new ApprovedCreationStart(s, host.Population.PlayerFaction)).ToArray())],
                    preparation.PrepareAsync, policy);
                return (Creation: creationEndpoint, Invitations: new ServerInvitationEndpoint(loop, checkpoints, creationEndpoint),
                    Sessions: new ServerAccountSessions(accounts, host.Runtime, checkpoints));
            }).ConfigureAwait(false);
            var creation = services.Creation;
            var invitations = services.Invitations;
            var sessions = services.Sessions;
            var handler = new SecureServerPeer(loop, sessions, hello, new WorldViewCodec(host.Population.Db), entry.PrepareAsync,
                creation: creation, invitations: invitations,
                information: new(hello, rules.ToDictionary(), config.GameplayAssets, config.AllowDeviceRegistration));
            var native = QuicDeployment.ResolveNative();
            var address = new IPEndPoint(IPAddress.Parse(config.BindAddress), config.Port);
            await using var listener = new QuicGameServer(address, native.Directory, native.ShimSha256,
                Path.Combine(config.DataDirectory, "server.pfx"), handler, config.MaximumPlayers);
            await using var discovery = new TcpDiscoveryServer(address, certificate,
                new(hello, rules.ToDictionary(), config.GameplayAssets, config.AllowDeviceRegistration));
            using var stop = new CancellationTokenSource();
            if (runSeconds is { } seconds) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            Console.WriteLine($"Listening on {listener.Endpoint} (QUIC/UDP game, TCP public discovery); maximum {config.MaximumPlayers} connections. Enter stop or save.");
            Console.WriteLine("Compatibility covers database merge order and " + config.GameplayAssets.Length + " configured gameplay assets.");
            using var saves = new SemaphoreSlim(1, 1);
            async Task Save()
            {
                await saves.WaitAsync().ConfigureAwait(false);
                try
                {
                    var captured = await loop.InvokeAsync(() => host.World.HasPendingDurableMutation ? null : checkpoints.Capture()).ConfigureAwait(false);
                    if (captured is not null) await Task.Run(() => checkpoints.Commit(captured)).ConfigureAwait(false);
                    Console.WriteLine(captured is not null ? "Checkpoint saved." : "Checkpoint deferred: a critical persistence operation is pending.");
                }
                finally { saves.Release(); }
            }
            var input = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        // Console readers can ignore cancellation while blocked in native input.
                        // Cancel the wait too, so shutdown can join this task and any active save.
                        var line = await Task.Run(Console.ReadLine).WaitAsync(stop.Token).ConfigureAwait(false);
                        if (line is null) break;
                        if (line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) { stop.Cancel(); break; }
                        if (line.Trim().Equals("save", StringComparison.OrdinalIgnoreCase)) await Save().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error) { Console.Error.WriteLine("Console command failed: " + error); }
            });
            _ = input;
            try
            {
                var nextSave = DateTime.UtcNow.AddSeconds(config.Gameplay.AutoSaveSeconds);
                while (!stop.IsCancellationRequested)
                {
                    var delay = Task.Delay(1000, stop.Token);
                    var completed = await Task.WhenAny(delay, loop.Completion, listener.Completion, discovery.Completion).ConfigureAwait(false);
                    if (completed == loop.Completion) { await loop.Completion.ConfigureAwait(false); throw new IOException("World loop stopped unexpectedly."); }
                    if (completed == listener.Completion) { await listener.Completion.ConfigureAwait(false); throw new IOException("Listener stopped unexpectedly."); }
                    if (completed == discovery.Completion) { await discovery.Completion.ConfigureAwait(false); throw new IOException("Public discovery stopped unexpectedly."); }
                    await delay.ConfigureAwait(false);
                    await loop.InvokeAsync(() => host.Runtime.Speed = config.PauseWhenEmpty && !host.World.Players.All.Any(p => host.World.Authority.IsActive(p.Id)) ? 0 : 1, stop.Token).ConfigureAwait(false);
                    if (config.Gameplay.AutoSaveSeconds > 0 && DateTime.UtcNow >= nextSave)
                    {
                        try { await Save().ConfigureAwait(false); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                        { Console.Error.WriteLine("Autosave failed; previous checkpoint retained: " + error.Message); }
                        nextSave = DateTime.UtcNow.AddSeconds(config.Gameplay.AutoSaveSeconds);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                Console.CancelKeyPress -= cancel;
                stop.Cancel();
                await listener.DisposeAsync().ConfigureAwait(false);
                await input.ConfigureAwait(false);
            }
            await Save().ConfigureAwait(false);
            if (host.World.HasPendingDurableMutation) throw new IOException("Shutdown retains the last committed checkpoint; a critical persistence operation could not finish.");
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
