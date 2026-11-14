using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Text.Json;
using Meitou.Content;
using Meitou.Diagnostics;
using Meitou.Game.Host;
using Meitou.Networking;
using Meitou.Runtime;
using Meitou.Server;
using Meitou.Simulation;
using Meitou.Simulation.Movement;

namespace Meitou.Tests.Engine;

/// <summary>
/// Opt-in movement diagnosis of an existing owned role, without recruiting a replacement or resetting its gameplay state.
/// </summary>
[Collection("LatencyTrace")]
public sealed class InstalledMovementTests
{
    /// <summary>
    /// A numeric sample of authority, path service, replica and transport progress.
    /// </summary>
    sealed record Sample(string Phase, double Seconds, long Tick, float AuthorityX, float AuthorityZ, float ReplicaX, float ReplicaZ,
        byte Task, ushort Flags, long Request, long Applied, long Submitted, int Pending, bool NavigationWaiting, bool HoldsStill,
        bool Incapacitated, bool Imprisoned, bool Carried, float Speed, int PathPoints, int Cursor,
        ulong SentBytes, ulong ReceivedBytes, ulong RttMicroseconds);

    /// <summary>
    /// Isolated paths and shipping rules for this run.
    /// </summary>
    sealed record Configuration(string Output, string Copy, ServerConfiguration Shipping);

    /// <summary>
    /// Runs real QUIC clicks and held steering over a detached checkpoint copy; output remains a local diagnostic artifact.
    /// </summary>
    [Fact, Slow, Bench]
    public async Task ExistingOwnedRoleMovesWithSingleAndHeldOrdersOverActualQuic()
    {
        NativeFixture.Require();
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "No Kenshi install configured.");
        var configuration = PrepareCopy();
        var samples = new List<Sample>();
        var sink = new TraceSink();
        string? previousMods = ModList.ModsCfgOverride;
        var previousSink = LatencyTrace.Sink;
        LatencyTrace.Sink = sink;

        try
        {
            if (configuration.Shipping.ModsCfg is { } mods) ModList.UseModsCfg(mods);
            await RunScene(install!, configuration, samples, sink);
            CheckMovement(samples, configuration.Output);
        }
        finally
        {
            LatencyTrace.Sink = previousSink;
            if (previousMods is null) ModList.ClearModsCfg();
            else ModList.UseModsCfg(previousMods);
            await WriteReports(configuration.Output, samples, sink);
        }
    }

    /// <summary>
    /// Copies immutable checkpoint artifacts and private account state, never production writer locks.
    /// </summary>
    static Configuration PrepareCopy()
    {
        string? source = Environment.GetEnvironmentVariable("MEITOU_MOVEMENT_SERVER_DATA");
        Assert.SkipWhen(source is null, "Set MEITOU_MOVEMENT_SERVER_DATA to a server-data directory; only its copy is opened.");
        string output = Path.GetFullPath(Environment.GetEnvironmentVariable("MEITOU_MOVEMENT_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "Meitou.Movement-" + Guid.NewGuid().ToString("N")));
        string copy = Path.Combine(output, "server-data-copy");
        Assert.False(Directory.Exists(copy), "Use a fresh diagnostic output directory.");
        Assert.False(copy.StartsWith(Path.GetFullPath(source!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "Never copy a checkpoint into itself.");
        string configuration = Environment.GetEnvironmentVariable("MEITOU_MOVEMENT_SERVER_CONFIG")
            ?? Path.Combine(Directory.GetParent(Path.GetFullPath(source!))!.FullName, "server.json");
        Assert.True(File.Exists(configuration));
        Copy(source!, copy);
        return new(output, copy, ServerConfiguration.Load(configuration));
    }

    /// <summary>
    /// Restores the actual player and connects a real native peer on a private ephemeral loopback port.
    /// </summary>
    static async Task RunScene(GameInstall install, Configuration configuration, List<Sample> samples, TraceSink sink)
    {
        var shipping = configuration.Shipping;
        using var host = OpenWorld(install, shipping);
        var store = new CheckpointStore(Path.Combine(configuration.Copy, "world"));
        var saved = store.LoadCurrent();
        Assert.NotNull(saved);
        var restored = host.RestoreCheckpoint(host.CreateCheckpointCoordinator(store), saved!);
        Assert.Null(restored.CleanupFailure);
        host.World.ObserveLatency(sink);
        host.Runtime.Speed = 0;
        host.Population.Relations.PlayerFaction = -1;
        var accounts = new TokenAccountStore(Path.Combine(configuration.Copy, "accounts.json"));
        var (credential, role) = ExistingRole(host, accounts);
        var hello = Hello(install, shipping);
        await using var loop = new ServerWorldLoop(host.Runtime);
        var entry = new HeadlessEntryPreparation(host, loop);
        var sessions = new ServerAccountSessions(accounts, host.Runtime, restored.Coordinator);
        var peer = new SecureServerPeer(loop, sessions, hello, new(host.Population.Db), entry.PrepareAsync);
        using var certificate = GameTlsTests.Certificate();
        await using var server = new QuicGameServer(new(IPAddress.Loopback, 0), certificate, peer);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        await using var remote = new RemotePlayerSession(await RemoteGameSession.ConnectAsync("127.0.0.1", server.Endpoint.Port,
            hello, new(credential.Account, credential.Token), new(host.Population.Db), cancellationToken: token,
            transportOptions: NativeFixture.Options(certificate)), host.Terrain.HeightAt);
        await RunPhases(host, loop, remote, role, samples, token);
    }

    /// <summary>
    /// Creates the production asynchronous simulation without a local renderer.
    /// </summary>
    static HeadlessWorld OpenWorld(GameInstall install, ServerConfiguration shipping) => HeadlessWorld.Open(install, new WorldSettings
    {
        Seed = shipping.WorldSeed, Threads = shipping.SimulationThreads, PublishSnapshots = false
    }, new StandardSystemOptions { SynchronousPaths = false }, shipping.TrustedCodeMods.ToHashSet(StringComparer.Ordinal),
        shipping.ModSettings, shipping.Gameplay);

    /// <summary>
    /// Rotates credentials only in the copied account store and retains the player's original role.
    /// </summary>
    static (IssuedCredential Credential, CharacterControl Role) ExistingRole(HeadlessWorld host, TokenAccountStore accounts)
    {
        var account = accounts.List().First(a => a.Enabled
            && host.World.Players.FindAccount(new("local-token", a.Id.ToString("N"))) is { } p
            && host.World.Players.KnownRoles(p.Id).Any());
        var credential = accounts.Rotate(account.Id);
        var player = host.World.Players.FindAccount(new("local-token", account.Id.ToString("N")))!;
        return (credential, host.World.Players.KnownRoles(player.Id).First());
    }

    /// <summary>
    /// Uses the shipping rules and installed-content compatibility manifest.
    /// </summary>
    static CompatibilityHello Hello(GameInstall install, ServerConfiguration shipping)
    {
        var trusted = shipping.TrustedCodeMods.ToHashSet(StringComparer.Ordinal);
        var rules = ServerCompatibility.WithCodeSettings(shipping.Rules(), install, trusted, shipping.ModSettings);
        return ServerCompatibility.CreateHello(install, ServerCompatibility.EngineBuild, rules, shipping.GameplayAssets, trusted, shipping.ModSettings);
    }

    /// <summary>
    /// Waits for normal admission, then samples clicks and held steering on the same out-and-back route.
    /// </summary>
    static async Task RunPhases(HeadlessWorld host, ServerWorldLoop loop, RemotePlayerSession remote, CharacterControl role,
        List<Sample> samples, CancellationToken token)
    {
        remote.Connection.RequestEntry(role.Entity);
        while (!remote.CanControl)
        {
            Assert.Null(remote.Connection.State.Disconnection);
            remote.Advance(.01);
            await Task.Delay(10, token);
        }

        var actor = remote.Connection.State.View!.Roles.Single(r => r.Entity == role.Entity).Character;
        await loop.InvokeAsync(() => host.Runtime.Speed = 1, token);
        var probe = new Probe(host, loop, remote, actor, samples, token);
        await probe.Phase("warmup", 5, held: false, move: false);
        await probe.Phase("single", 8, held: false, move: true);
        await probe.Phase("held-fixed", 8, held: true, move: true);
        await probe.Phase("held-changing", 8, held: true, move: true);
    }

    /// <summary>
    /// Checks authority motion rather than inferring success from evaluated command receipts.
    /// </summary>
    static void CheckMovement(List<Sample> samples, string output)
    {
        foreach (string phase in new[] { "single", "held-fixed", "held-changing" })
        {
            var rows = samples.Where(s => s.Phase == phase).ToArray();
            Assert.NotEmpty(rows);
            Assert.True(rows.Any(s => MathF.Abs(s.AuthorityX - rows[0].AuthorityX) + MathF.Abs(s.AuthorityZ - rows[0].AuthorityZ) > 1),
                $"No authoritative movement in {phase}; see {Path.Combine(output, "movement.json")}.");
        }
    }

    /// <summary>
    /// Writes detached diagnostic artifacts; these counters are UDP payload, not exact IP wire accounting or renderer measurements.
    /// </summary>
    static async Task WriteReports(string output, List<Sample> samples, TraceSink sink)
    {
        var json = new JsonSerializerOptions { WriteIndented = true };
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(output, "movement.json"), JsonSerializer.Serialize(samples, json), token);
        await File.WriteAllTextAsync(Path.Combine(output, "stages.json"), JsonSerializer.Serialize(new
        {
            Frequency = Stopwatch.Frequency, sink.Drops, Events = sink.Events
        }, json), token);
    }

    /// <summary>
    /// Copies input files without opening production writer locks or modifying the source.
    /// </summary>
    static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            if (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (string directory in Directory.GetDirectories(source)) Copy(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    /// <summary>
    /// Per-connection movement sampling, always reading mutable gameplay state through the authority owner.
    /// </summary>
    sealed class Probe(HeadlessWorld host, ServerWorldLoop loop, RemotePlayerSession remote, CharacterId actor,
        List<Sample> samples, CancellationToken token)
    {
        /// <summary>
        /// Alternates a longer destination than the prior four-unit acceptance walk; the return leg retraces the single-click route.
        /// </summary>
        internal async Task Phase(string name, double seconds, bool held, bool move)
        {
            remote.Submit(new StopCommand([actor]) { Tick = remote.CommandTick });
            while (remote.PendingCommands != 0)
            {
                remote.Advance(.01);
                await Task.Delay(10, token);
            }

            var origin = await loop.InvokeAsync(() => host.World.Characters.Current[actor.Slot].Position, token);
            var target = origin + new Vector3(name == "held-fixed" ? -100 : 100, 0, 0);
            target.Y = host.Terrain.HeightAt(target.X, target.Z);
            var watch = Stopwatch.StartNew();
            double nextOrder = 0;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                Assert.Null(remote.Connection.State.Disconnection);
                remote.Advance(.05);
                double elapsed = watch.Elapsed.TotalSeconds;
                if (move && elapsed >= nextOrder)
                {
                    var aim = name == "held-changing" ? target + new Vector3(0, 0, (float)Math.Sin(elapsed) * 20) : target;
                    remote.Submit(new MoveOrder([actor], aim) { Tick = remote.CommandTick });
                    nextOrder = held ? elapsed + .1 : double.PositiveInfinity;
                }

                var replica = remote.CurrentSnapshot.Characters.Single(c => c.Id == actor);
                var statistics = remote.Connection.TransportStatistics;
                samples.Add(await loop.InvokeAsync(() => Capture(name, elapsed, replica.Position,
                    statistics.SentUdpPayloadBytes, statistics.ReceivedUdpPayloadBytes, statistics.RttMicroseconds), token));
                await Task.Delay(50, token);
            }
        }

        /// <summary>
        /// Reads numeric gameplay state only on the authority owner; no capability, health or position is changed.
        /// </summary>
        Sample Capture(string phase, double seconds, Vector3 replica, ulong sent, ulong received, ulong rtt)
        {
            var hot = host.World.Characters.Current[actor.Slot];
            var cold = host.World.Characters.Cold(actor.Slot)!;
            var movement = host.World.Systems.OfType<MovementSystem>().Single();
            return new(phase, seconds, host.World.Tick, hot.Position.X, hot.Position.Z, replica.X, replica.Z,
                hot.Task, hot.Flags, cold.PathRequest, cold.PathApplied, movement.Paths.Submitted, movement.Paths.Pending,
                cold.NavigationWaiting, cold.Animation?.HoldsStill ?? false, cold.Medical?.Incapacitated ?? false,
                cold.IsImprisoned, !cold.CarriedBy.IsNone, hot.Velocity.Length(), cold.Path.Length, hot.PathCursor, sent, received, rtt);
        }
    }

    /// <summary>
    /// Bounded stage capture shared by the real authority and transport.
    /// </summary>
    sealed class TraceSink : ILatencyTraceSink
    {
        /// <summary>
        /// Serializes worker and owner observations.
        /// </summary>
        readonly Lock _gate = new();

        /// <summary>
        /// Retained observations; overflow is explicit in the report.
        /// </summary>
        readonly List<LatencyTraceEvent> _events = [];

        /// <summary>
        /// Events beyond the diagnostic bound.
        /// </summary>
        public int Drops { get; private set; }

        /// <summary>
        /// Retains only the first bounded batch without changing gameplay.
        /// </summary>
        public void Emit(in LatencyTraceEvent value)
        {
            lock (_gate)
            {
                if (_events.Count >= 100000) Drops++;
                else _events.Add(value);
            }
        }

        /// <summary>
        /// Returns a detached stage list.
        /// </summary>
        public LatencyTraceEvent[] Events { get { lock (_gate) return _events.ToArray(); } }
    }
}
