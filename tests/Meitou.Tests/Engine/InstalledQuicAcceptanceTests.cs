using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Meitou.Content;
using Meitou.Diagnostics;
using Meitou.Engine;
using Meitou.Networking;
using Meitou.Networking.VNext.Transport;
using Meitou.Networking.VNext;
using Meitou.Runtime;
using Meitou.Server;
using Meitou.Simulation;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay;

namespace Meitou.Tests.Engine;

[Collection("LatencyTrace")]
public sealed class InstalledQuicAcceptanceTests
{
    sealed class Sink : ILatencyTraceSink
    {
        readonly Lock gate = new();
        readonly List<LatencyTraceRecord> records = [];
        readonly Guid run = Guid.NewGuid();
        readonly HashSet<(Guid World, ulong Connection, ulong Generation, ulong Entity)> sampled = [];
        public long Drops { get; private set; }
        public long IgnoredUnsampled { get; private set; }
        public long Attempts { get; private set; }
        public void Emit(in LatencyTraceEvent e)
        {
            lock (gate)
            {
                Attempts++;
                var key = (e.World, e.Connection, e.Generation, e.Entity);
                if (e.Stage == LatencyStage.Intent) sampled.Add(key);
                if (e.Stage is LatencyStage.MotionSendQueued or LatencyStage.MotionWriteStarted or LatencyStage.MotionWriteComplete && !sampled.Contains(key))
                { IgnoredUnsampled++; return; }
                if (records.Count >= 500000) { Drops++; return; }
                records.Add(new(run, Guid.Empty, Stopwatch.Frequency, e));
            }
        }
        public LatencyTraceRecord[] Records { get { lock (gate) return records.ToArray(); } }
    }
    sealed record RoleMetadata(ulong Entity, int Faction, ulong GrantRevision, int CharacterSlot, int CharacterGeneration, bool Loaded, bool Dead, int Availability, bool Valid);
    static RoleMetadata[] Roles(Meitou.Simulation.World world, PlayerId player) => world.Players.KnownRoles(player).Select(grant =>
    {
        bool loaded = world.Entities.TryResolve(grant.Entity, out var character);
        var location = world.Players.LocateRole(player, grant.Entity);
        return new RoleMetadata(grant.Entity.Value, grant.Faction, grant.Revision, character.Slot, character.Generation, loaded,
            location?.Availability == PlayerRoleAvailability.Dead, (int)(location?.Availability ?? PlayerRoleAvailability.Unavailable), world.Players.Validate(grant));
    }).ToArray();
    sealed record Capability(ulong Entity, bool IsPlayer, bool Imprisoned, bool Incapacitated, bool Carried, byte HeldState, byte Task, float VelocitySquared, bool MoveEligible);
    static Capability Capabilities(Meitou.Simulation.World world, EntityId entity)
    {
        Assert.True(world.Entities.TryResolve(entity, out var character));
        var cold = world.Characters.Cold(character.Slot)!; var hot = world.Characters.Current[character.Slot];
        return new(entity.Value, cold.IsPlayer, cold.IsImprisoned, cold.Medical?.Incapacitated ?? true, !cold.CarriedBy.IsNone,
            (byte)cold.InSomething, hot.Task, hot.Velocity.LengthSquared(), TaskStart.Eligible(world, cold, TaskType.MOVE_CUS_ORDERED));
    }
    sealed record Window(int Clients, double StartSeconds, double EndSeconds, int Peer, ulong UdpPayloadBytes, ulong QuicPackets, ulong ConservativeIpv4Bytes, double ConservativeKbitPerSecond, int VisibleEntities, long AuthorityTick);

    [Fact, Slow, Bench]
    public async Task InstalledPreCutoverCheckpointRunsOneThenTwoActualQuicReplicasFor120SecondsEach()
    {
        NativeFixture.Require();
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "No Kenshi install configured.");
        string? source = Environment.GetEnvironmentVariable("MEITOU_QUIC_ACCEPTANCE_SERVER_DATA");
        Assert.SkipWhen(source is null, "Set MEITOU_QUIC_ACCEPTANCE_SERVER_DATA to a pre-cutover server-data directory. The harness always copies it.");
        string output = Path.GetFullPath(Environment.GetEnvironmentVariable("MEITOU_QUIC_ACCEPTANCE_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "Meitou.QuicAcceptance-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        string copy = Path.Combine(output, "server-data-copy");
        Assert.False(Directory.Exists(copy), "Acceptance output must be new; never reuse a mutable checkpoint copy.");
        string sourcePath = Path.GetFullPath(source!);
        string profile = Environment.GetEnvironmentVariable("MEITOU_QUIC_ACCEPTANCE_PROFILE") ?? "production";
        Assert.Contains(profile, new[] { "production", "deterministic-stress" });
        string? configurationPath = Environment.GetEnvironmentVariable("MEITOU_QUIC_ACCEPTANCE_SERVER_CONFIG");
        string adjacentConfiguration = Path.Combine(Directory.GetParent(sourcePath)!.FullName, "server.json");
        if (configurationPath is null && File.Exists(adjacentConfiguration)) configurationPath = adjacentConfiguration;
        if (configurationPath is not null) Assert.True(File.Exists(configurationPath), "The explicitly selected server configuration must exist; source configuration files are never created or modified.");
        var shipping = configurationPath is null ? new ServerConfiguration() : ServerConfiguration.Load(configurationPath);
        bool synchronousPaths = profile == "deterministic-stress";
        int simulationThreads = synchronousPaths ? 1 : shipping.SimulationThreads;
        string? previousModsCfg = ModList.ModsCfgOverride;
        Assert.False(copy.StartsWith(sourcePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Output must be outside the source checkpoint directory.");
        Copy(sourcePath, copy);
        var previousSink = LatencyTrace.Sink; var sink = new Sink(); LatencyTrace.Sink = sink;
        long initialTraceFailures = LatencyTrace.FailedEmissions;
        var windows = new List<Window>();
        var metricsGate = new Lock();
        var componentBytes = new Dictionary<int, Dictionary<ushort, long>>();
        var publicationSizes = new List<(int Profile, bool Baseline, int Bytes, IReadOnlyDictionary<ushort, long> Components)>();
        var bindingPersistence = new List<AccountBindingPersistenceDiagnostics>();
        int measuredProfile = 0;
        void Observe(GameStatePublication publication)
        {
            lock (metricsGate)
            {
                if (!componentBytes.TryGetValue(measuredProfile, out var totals)) componentBytes[measuredProfile] = totals = [];
                foreach (var pair in publication.ComponentBytes) totals[pair.Key] = totals.GetValueOrDefault(pair.Key) + pair.Value;
                publicationSizes.Add((measuredProfile, publication.Baseline, publication.Frames.Sum(f => f.Length), publication.ComponentBytes));
            }
        }
        try
        {
            if (shipping.ModsCfg is not null) ModList.UseModsCfg(shipping.ModsCfg);
            using var host = HeadlessWorld.Open(install!, new WorldSettings { Seed = shipping.WorldSeed, Threads = simulationThreads, PublishSnapshots = false },
                new StandardSystemOptions { SynchronousPaths = synchronousPaths }, shipping.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), shipping.ModSettings, shipping.Gameplay);
            var store = new CheckpointStore(Path.Combine(copy, "world"));
            var saved = store.LoadCurrent(); Assert.NotNull(saved);
            var restored = host.RestoreCheckpoint(host.CreateCheckpointCoordinator(store), saved!);
            Assert.Null(restored.CleanupFailure);
            var checkpoints = restored.Coordinator;
            host.Runtime.Speed = 0; host.Population.Relations.PlayerFaction = -1;
            var originalRoles = host.World.Players.All.SelectMany(p => host.World.Players.KnownRoles(p.Id)).OrderBy(r => r.Entity.Value).ToArray();
            Assert.NotEmpty(originalRoles);
            var accounts = new TokenAccountStore(Path.Combine(copy, "accounts.json"));
            // Rotate only the copied store: preserve the pre-cutover account identity and ownership without reading a production bearer token.
            var existing = accounts.List().Where(a => a.Enabled).First(a => host.World.Players.FindAccount(new("local-token", a.Id.ToString("N"))) is { } p && host.World.Players.KnownRoles(p.Id).Any());
            var credential = accounts.Rotate(existing.Id);
            var player = host.World.Players.FindAccount(new("local-token", credential.Account.ToString("N")))!;
            var role = host.World.Players.KnownRoles(player.Id).First();
            host.World.ObserveLatency(sink);
            await using var loop = new ServerWorldLoop(host.Runtime);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20)); var token = timeout.Token;
            var serverConfig = shipping with { DataDirectory = copy, WorldSeed = host.World.Seed, Gameplay = GameplayRules.Capture(host.World), SimulationThreads = simulationThreads };
            var rules = ServerCompatibility.WithCodeSettings(serverConfig.Rules(), install!, shipping.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), shipping.ModSettings);
            var hello = ServerCompatibility.CreateHello(install!, ServerCompatibility.EngineBuild, rules, shipping.GameplayAssets, shipping.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), shipping.ModSettings);
            var entry = new HeadlessEntryPreparation(host, loop);
            var preparationWatch = Stopwatch.StartNew();
            var preparing = await loop.InvokeAsync(() => host.World.Authority.Open(player.Id), token);
            Assert.True(await entry.PrepareAsync(preparing, role.Entity, token));
            await loop.InvokeAsync(() => host.World.Authority.Close(preparing), token);
            double preparationSeconds = preparationWatch.Elapsed.TotalSeconds;
            var secondCredential = accounts.Create("acceptance-second-peer");
            var actors = await loop.InvokeAsync(() =>
            {
                var original = Capabilities(host.World, role.Entity);
                Assert.True(host.World.Entities.TryResolve(role.Entity, out var originalCharacter));
                var origin = host.World.Characters.Current[originalCharacter.Slot].Position;
                var candidates = Enumerable.Range(0, host.World.Characters.HighWater).Select(host.World.Characters.IdOf)
                    .Where(id => host.World.Characters.IsAlive(id) && host.World.Players.ControlOf(id) is null &&
                        host.World.Characters.Cold(id.Slot) is { DiedTick: < 0, Faction: >= 0, IsImprisoned: false, Medical: { Incapacitated: false, Dead: false }, Appearance: not null } cold &&
                        cold.CarriedBy.IsNone && (byte)cold.InSomething == 0 && host.World.Characters.Current[id.Slot].MaxSpeed > 0 &&
                        Vector3.DistanceSquared(host.World.Characters.Current[id.Slot].Position, origin) < 2000 * 2000)
                    .OrderBy(id => host.World.Characters.Current[id.Slot].Task == (byte)CharacterTask.Idle ? 0 : 1)
                    .ThenBy(id => Vector3.DistanceSquared(host.World.Characters.Current[id.Slot].Position, origin)).Take(2).ToArray();
                Assert.Equal(2, candidates.Length);
                var secondIdentity = accounts.Authenticate(new(secondCredential.Account, secondCredential.Token))!.Identity;
                var second = host.World.Players.ResolveAccount(secondIdentity).Player;
                var players = new[] { player.Id, second.Id }; var grants = new CharacterControl[2];
                for (int index = 0; index < candidates.Length; index++)
                {
                    var cold = host.World.Characters.Cold(candidates[index].Slot)!;
                    // Enroll only this recruit into a real player-managed platoon, as the recruitment/save contract requires.
                    if (host.World.Squads.Find(cold.SquadId) is { } oldSquad)
                    {
                        oldSquad.Members.Remove(candidates[index]);
                        if (oldSquad.Leader == candidates[index]) oldSquad.Leader = SquadRegistry.Successor(oldSquad.Members, host.World.Characters);
                    }
                    var squad = SquadManagement.NewSquad(host.World, host.Population.Db, host.World.Characters.Current[candidates[index].Slot].Position, "Acceptance", cold.Faction);
                    cold.SquadId = squad.Id; cold.Role = (int)SquadRole.Squad1; cold.IsPlayer = true;
                    cold.NpcClass = FundamentalType.Adventurer; cold.Orders = Orders.PlayerDefaults;
                    cold.Ai = null; cold.Engaged = false; cold.Fleeing = false; cold.Pending = null; cold.Jobs.Clear();
                    cold.Save = null;
                    squad.Members.Add(candidates[index]); squad.Leader = candidates[index];
                    host.World.Players.Find(players[index])!.State.Squad = squad.Id;
                    Assert.True(TaskStart.Eligible(host.World, cold, TaskType.MOVE_CUS_ORDERED));
                    host.World.Players.SetMembership(players[index], cold.Faction, FactionPermission.All);
                    grants[index] = host.World.Players.AssignInitial(players[index], candidates[index]);
                }
                checkpoints.Commit(checkpoints.Capture());
                host.World.Players.ConfirmAccountCheckpoint(secondIdentity);
                return (Original: original, Primary: grants[0], Secondary: grants[1],
                    PrimaryCapabilities: Capabilities(host.World, grants[0].Entity), SecondaryCapabilities: Capabilities(host.World, grants[1].Entity));
            }, token);
            role = actors.Primary; var secondRole = actors.Secondary;
            var reloadedSetup = await loop.InvokeAsync(() =>
            {
                var restore = host.RestoreCheckpoint(checkpoints, store.LoadCurrent()!);
                Assert.Null(restore.CleanupFailure); checkpoints = restore.Coordinator;
                host.Population.Relations.PlayerFaction = -1; host.World.ObserveLatency(sink);
                foreach (var grant in originalRoles) Assert.Contains(grant, host.World.Players.KnownRoles(grant.Player));
                foreach (var grant in new[] { role, secondRole })
                {
                    Assert.Contains(grant, host.World.Players.KnownRoles(grant.Player));
                    Assert.Contains(host.World.Players.LocateRole(grant.Player, grant.Entity)!.Availability,
                        new[] { PlayerRoleAvailability.Available, PlayerRoleAvailability.Stored });
                }
                return new { Primary = Roles(host.World, role.Player), Secondary = Roles(host.World, secondRole.Player) };
            }, token);
            await File.WriteAllTextAsync(Path.Combine(output, "acceptance-actors.json"), JsonSerializer.Serialize(new
            {
                primaryRole = role.Entity.Value, secondaryRole = secondRole.Entity.Value,
                actors.Original, Primary = actors.PrimaryCapabilities, Secondary = actors.SecondaryCapabilities, Reloaded = reloadedSetup,
                Setup = "Two healthy unheld recruits join dedicated PlayerManaged squads with normal recruit flags and cleared NPC save links; original grants remain intact. Health, position and physics are unchanged. Normal client StopCommand establishes stationary starts."
            }, new JsonSerializerOptions { WriteIndented = true }), token);
            var sessions = new ServerAccountSessions(accounts, host.Runtime, checkpoints);
            var peer = new SecureServerPeer(loop, sessions, hello, new(host.Population.Db), entry.PrepareAsync, new ServerPeerOptions { PublicationObserved = Observe, BindingPersistenceObserved = d => { lock (metricsGate) bindingPersistence.Add(d); } });
            using var certificate = GameTlsTests.Certificate();
            File.WriteAllBytes(Path.Combine(copy, "server.pfx"), certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, ""));
            await using var server = new QuicGameServer(new(IPAddress.Loopback, 0), certificate, peer);
            // Keep a private local profile/config for a separate real graphical submission run; do not print or commit their contents.
            var graphicalConfig = serverConfig with { Port = server.Endpoint.Port };
            await File.WriteAllTextAsync(Path.Combine(output, "server.json"), JsonSerializer.Serialize(graphicalConfig, new JsonSerializerOptions { WriteIndented = true }), token);
            new ServerConnectionProfile("localhost", server.Endpoint.Port, credential.Account, credential.Token, hello) { Rules = rules, GameplayAssets = shipping.GameplayAssets }.Write(Path.Combine(output, "private-client.json"));

            var config = NativeFixture.Options(certificate);
            if (Environment.GetEnvironmentVariable("MEITOU_QUIC_ACCEPTANCE_SETUP_ONLY") == "1")
            {
                await File.WriteAllTextAsync(Path.Combine(output, "setup-verified.json"), JsonSerializer.Serialize(new { Profile = profile, SimulationThreads = simulationThreads,
                    SynchronousPaths = synchronousPaths, primaryRole = role.Entity.Value, secondaryRole = secondRole.Entity.Value, Reloaded = reloadedSetup }, new JsonSerializerOptions { WriteIndented = true }), token);
                Assert.Skip("Setup-only mode produced and reloaded the private GUI fixture; no timed acceptance was performed.");
            }
            var clients = new List<RemotePlayerSession>();
            var controlledRoles = new List<EntityId>();
            var beforeRoles = new Dictionary<ulong, RoleMetadata[]>();
            var beforeFrameCounts = new Dictionary<ulong, int[]>();
            try
            {
                async Task<RemotePlayerSession> Connect(IssuedCredential issued, EntityId entity)
                {
                    var connection = await RemoteGameSession.ConnectAsync("127.0.0.1", server.Endpoint.Port, hello,
                        new(issued.Account, issued.Token), new(host.Population.Db), cancellationToken: token, transportOptions: config);
                    var remote = new RemotePlayerSession(connection, host.Terrain.HeightAt);
                    remote.Connection.RequestEntry(entity);
                    while (!remote.CanControl) { Assert.Null(connection.State.Disconnection); remote.Advance(.01); await Task.Delay(10, token); }
                    var character = connection.State.View!.Roles.Single(r => r.Entity == entity).Character;
                    remote.Submit(new StopCommand([character]) { Tick = remote.CommandTick });
                    while (remote.PendingCommands != 0) { Assert.Null(connection.State.Disconnection); remote.Advance(.01); await Task.Delay(10, token); }
                    var playerId = connection.State.Admission.Identity.Player;
                    beforeRoles[playerId.Value] = await loop.InvokeAsync(() => Roles(host.World, playerId), token);
                    var readyView = connection.State.View!;
                    beforeFrameCounts[playerId.Value] = [readyView.Snapshot.Characters.Count, readyView.Roles.Count, readyView.Factions.Count, readyView.Snapshot.Characters.Count(c => c.ItemGrid is not null)];
                    return remote;
                }
                var cold = Stopwatch.StartNew(); clients.Add(await Connect(credential, role.Entity)); controlledRoles.Add(role.Entity);
                double coldSeconds = cold.Elapsed.TotalSeconds;
                var coldStats = clients[0].Connection.TransportStatistics;
                int initialEntities = clients[0].Connection.State.View!.Snapshot.Characters.Count;
                await File.WriteAllTextAsync(Path.Combine(output, "cold.json"), JsonSerializer.Serialize(new { InitialEntities = initialEntities, Seconds = coldSeconds, UdpPayloadBytes = coldStats.SentUdpPayloadBytes + coldStats.ReceivedUdpPayloadBytes }, new JsonSerializerOptions { WriteIndented = true }), token);
                Assert.True(initialEntities >= 100, $"Installed scene has only {initialEntities} authorized entities; this cannot represent the ~124-entity gate.");
                await loop.InvokeAsync(() => host.Runtime.Speed = 1, token);
                await Capture(1);
                clients.Add(await Connect(secondCredential, secondRole.Entity)); controlledRoles.Add(secondRole.Entity);
                Assert.True(clients[1].Connection.State.View!.Snapshot.Characters.Count >= 100, "Second peer must observe the same populated installed scene.");
                await Capture(2);
                var liveBinding = await VerifyPopulatedBinding();
                await loop.InvokeAsync(() =>
                {
                    host.Runtime.Speed = 0;
                    foreach (var grant in originalRoles) Assert.Contains(grant, host.World.Players.KnownRoles(grant.Player));
                    checkpoints.Commit(checkpoints.Capture());
                }, token);
                Assert.All(clients, remote => Assert.Null(remote.Connection.State.Disconnection));
                Assert.Equal(0, server.FailedConnections);
                foreach (var remote in clients) await remote.DisposeAsync(); clients.Clear();
                await server.DisposeAsync(); await loop.DisposeAsync();
                var reloaded = host.RestoreCheckpoint(checkpoints, store.LoadCurrent()!);
                Assert.Null(reloaded.CleanupFailure);
                foreach (var grant in originalRoles) Assert.Contains(grant, host.World.Players.KnownRoles(grant.Player));
                foreach (var grant in new[] { role, secondRole })
                    Assert.Contains(host.World.Players.LocateRole(grant.Player, grant.Entity)!.Availability, new[] { PlayerRoleAvailability.Available, PlayerRoleAvailability.Stored });
                var traceRecords = sink.Records;
                var reports = LatencyTraceAnalyzer.Analyze(traceRecords);
                var clientRootObservations = traceRecords.Where(r => r.Event.Stage == LatencyStage.Intent).Select(intent =>
                {
                    var key = intent.Event;
                    var root = traceRecords.FirstOrDefault(r => r.Run == intent.Run && r.Event.Stage == LatencyStage.ClientRootChanged &&
                        (r.Event.World, r.Event.Connection, r.Event.Generation, r.Event.Request, r.Event.Entity) == (key.World, key.Connection, key.Generation, key.Request, key.Entity));
                    return new { key.World, key.Connection, key.Generation, key.Request, key.Entity,
                        Milliseconds = root.Run == Guid.Empty ? (double?)null : (root.Event.Timestamp - key.Timestamp) * 1000d / intent.Frequency };
                }).ToArray();
                var summary = new
                {
                    Transport = "actual native QUIC loopback; installed content; pre-cutover checkpoint copy",
                    Profile = profile, SimulationThreads = simulationThreads, SynchronousPaths = synchronousPaths,
                    ConfigurationSource = configurationPath is null ? "ServerConfiguration defaults" : "existing supplied or checkpoint-adjacent server configuration",
                    Gameplay = serverConfig.Gameplay,
                    OriginalRoleCount = originalRoles.Length, RegionPreparationSeconds = preparationSeconds, InitialEntities = initialEntities, ColdSeconds = coldSeconds,
                    ColdUdpPayloadBytes = coldStats.SentUdpPayloadBytes + coldStats.ReceivedUdpPayloadBytes,
                    ColdConservativeIpv4Bytes = coldStats.SentUdpPayloadBytes + coldStats.ReceivedUdpPayloadBytes + (coldStats.SentQuicPackets + coldStats.ReceivedQuicPackets) * 28,
                    Windows = windows, LiveAccountBinding = liveBinding,
                    EncodedReliableComponentBytesByProfile = componentBytes.ToDictionary(p => p.Key, p => p.Value.ToDictionary(f => $"{f.Key}:{GameStatePublisher.ComponentName(f.Key)}", f => f.Value)),
                    EncodedReliablePublicationSizes = publicationSizes.Select(p => new { p.Profile, p.Baseline, p.Bytes, Components = p.Components.ToDictionary(f => $"{f.Key}:{GameStatePublisher.ComponentName(f.Key)}", f => f.Value) }),
                    AuthorityReceivedTickProgress = windows.Where(w => w.Peer == 0).GroupBy(w => w.Clients).Select(g => new { Clients = g.Key, TicksPerRealSecond = (g.Last().AuthorityTick - g.First().AuthorityTick) / (g.Last().EndSeconds - g.First().EndSeconds), MinVisible = g.Min(w => w.VisibleEntities), MaxVisible = g.Max(w => w.VisibleEntities) }),
                    TraceAttempts = sink.Attempts, IgnoredUnsampledMotionEvents = sink.IgnoredUnsampled, TraceRetained = sink.Records.Length, TraceDrops = sink.Drops, TraceEmissionFailures = LatencyTrace.FailedEmissions - initialTraceFailures,
                    ConservativeMeanKbitPerSecond = windows.GroupBy(w => (w.Clients, w.Peer)).Select(g => new { g.Key.Clients, g.Key.Peer, KbitPerSecond = g.Sum(w => (double)w.ConservativeIpv4Bytes) * 8 / g.Sum(w => w.EndSeconds - w.StartSeconds) / 1000 }),
                    ClientAuthoritativeRootObservations = clientRootObservations,
                    TraceRequests = reports.Count, Classifications = reports.GroupBy(r => r.Classification).ToDictionary(g => g.Key, g => g.Count()),
                    Requests = reports.Select(r => new { r.ClientRun, r.ServerRun, r.World, r.Connection, r.Generation, r.Request, r.Entity, r.Classification, r.Joined, r.AcceptanceToMotionMs, r.IntentToPoseMs, r.DurationsMs, r.Missing }),
                    Limitations = new[] { "Loopback is not the nominal RTT/jitter profile or VPS acceptance.", "QUIC packet counters yield conservative IPv4/UDP header upper bounds, not exact wire accounting.", "RemotePlayerSession is exercised; no renderer callback is fabricated. Renderer-pose gate remains open.", "Single-click movement is sampled; held steering and tracing-on/off overhead remain separate unmeasured gates.", "Copied account credentials were rotated; original account identity and grants were retained.", "Both sampled actors use explicit recruitment into dedicated PlayerManaged squads; no health, position or physics reset is applied. Original and sampled grant availability survive save/reload." },
                };
                await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), token);
                Assert.Equal(0, sink.Drops);
                Assert.Equal(initialTraceFailures, LatencyTrace.FailedEmissions);
                foreach (var rateProfile in windows.GroupBy(w => (w.Clients, w.Peer)))
                {
                    double rate = rateProfile.Sum(w => (double)w.ConservativeIpv4Bytes) * 8 / rateProfile.Sum(w => w.EndSeconds - w.StartSeconds) / 1000;
                    Assert.True(rate <= 500, $"Conservative loopback IPv4 upper bound is {rate:F1} kbit/s for {rateProfile.Key.Clients} clients, peer {rateProfile.Key.Peer}.");
                }


                async Task<object> VerifyPopulatedBinding()
                {
                    var receiptMilliseconds = new List<double>();
                    var completions = new List<RemoteCommandCompletion>();
                    var remote = clients[0]; remote.Advance(0);
                    remote.CommandCompleted += completions.Add;
                    var fresh = accounts.Create("acceptance-live-binding");
                    long startTick = await loop.InvokeAsync(() => host.World.Tick, token);
                    var watch = Stopwatch.StartNew();
                    var connecting = RemoteGameSession.ConnectAsync("127.0.0.1", server.Endpoint.Port, hello,
                        new(fresh.Account, fresh.Token), new(host.Population.Db), cancellationToken: token, transportOptions: config);
                    try
                    {
                        do
                        {
                            Assert.True(remote.CanControl, "Existing control must survive another account's binding persistence.");
                            var character = remote.Connection.State.View!.Roles.Single(r => r.Entity == controlledRoles[0]).Character;
                            var submitted = Stopwatch.StartNew(); int before = completions.Count;
                            remote.Submit(new SelectCommand([character]) { Tick = remote.CommandTick });
                            while (remote.PendingCommands != 0)
                            {
                                foreach (var client in clients) { Assert.Null(client.Connection.State.Disconnection); client.Advance(.01); }
                                await Task.Delay(5, token);
                            }
                            Assert.True(completions.Count > before);
                            foreach (var completion in completions.Skip(before))
                            { Assert.Null(completion.Error); Assert.Equal(AuthorityStatus.Evaluated, completion.Receipt?.Status); }
                            receiptMilliseconds.Add(submitted.Elapsed.TotalMilliseconds);
                            if (!connecting.IsCompleted) await Task.Delay(20, token);
                        } while (!connecting.IsCompleted);
                        var newcomer = await connecting;
                        Assert.Equal(AdmissionPhase.Preparing, newcomer.State.Admission.State.Phase);
                        Assert.Empty(newcomer.State.Admission.State.Roles);
                        // Keep the third endpoint alive through shutdown so expected EOFs cannot contaminate the active-peer failure count.
                        var unentered = new RemotePlayerSession(newcomer, host.Terrain.HeightAt); clients.Add(unentered);
                        long endTick = await loop.InvokeAsync(() => host.World.Tick, token);
                        Assert.NotEmpty(bindingPersistence);
                        var result = new { AuthenticationMilliseconds = watch.Elapsed.TotalMilliseconds,
                            AuthorityTicksPerSecond = (endTick - startTick) / watch.Elapsed.TotalSeconds,
                            CaptureAndCommit = bindingPersistence.ToArray(), CommandReceiptMilliseconds = receiptMilliseconds };
                        await File.WriteAllTextAsync(Path.Combine(output, "live-binding.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), token);
                        return result;
                    }
                    finally { remote.CommandCompleted -= completions.Add; }
                }

                async Task Capture(int count)
                {
                    var watch = Stopwatch.StartNew(); double previous = 0;
                    var previousStats = clients.Select(c => c.Connection.TransportStatistics).ToArray();
                    while (windows.Count(w => w.Clients == count) < 12 * count)
                    {
                        foreach (var remote in clients)
                        {
                            var status = remote.Connection.State; Assert.Null(status.Disconnection);
                            if (status.View is null)
                            {
                                var retention = sink.Records.Where(r => r.Event.Stage == LatencyStage.PublicationRetentionRejected).Select(r => r.Event).ToArray();
                                var subject = status.Admission.Identity.Player;
                                var current = await loop.InvokeAsync(() => new { Roles = Roles(host.World, subject), LoadedWorldCharacters = host.World.Characters.Count, Memberships = host.World.Players.Find(subject)!.Memberships.Count() }, token);
                                var capabilities = await loop.InvokeAsync(() => controlledRoles.Select(entity => Capabilities(host.World, entity)).ToArray(), token);
                                await File.WriteAllTextAsync(Path.Combine(output, "view-outage.json"), JsonSerializer.Serialize(new
                                { Clients = count, Seconds = watch.Elapsed.TotalSeconds, remote.Connection.ViewEpoch, Phase = (int)status.Admission.State.Phase, RejectionReason = retention.LastOrDefault().Value,
                                    BeforeRoles = beforeRoles.GetValueOrDefault(subject.Value), CurrentRoles = current.Roles,
                                    BeforeFrameCounts = beforeFrameCounts.GetValueOrDefault(subject.Value), CurrentFrameCounts = new[] { 0, status.Admission.State.Roles.Count(), current.Memberships, 0 },
                                    current.LoadedWorldCharacters, Retention = retention, Actors = capabilities }, new JsonSerializerOptions { WriteIndented = true }), token);
                                Assert.Fail("The nominal active view was withdrawn; see view-outage.json. This interval is not excluded from acceptance.");
                            }
                            remote.Advance(.01);
                        }
                        await Task.Delay(10, token);
                        double elapsed = watch.Elapsed.TotalSeconds;
                        if (elapsed < 30) { previousStats = clients.Select(c => c.Connection.TransportStatistics).ToArray(); previous = elapsed; continue; }
                        lock (metricsGate) measuredProfile = count;
                        int completed = windows.Count(w => w.Clients == count) / count;
                        if (elapsed < 30 + (completed + 1) * 10) continue;
                        for (int index = 0; index < count; index++)
                        {
                            var now = clients[index].Connection.TransportStatistics; var before = previousStats[index];
                            ulong bytes = checked(now.SentUdpPayloadBytes - before.SentUdpPayloadBytes + now.ReceivedUdpPayloadBytes - before.ReceivedUdpPayloadBytes);
                            ulong packets = checked(now.SentQuicPackets - before.SentQuicPackets + now.ReceivedQuicPackets - before.ReceivedQuicPackets);
                            ulong upper = checked(bytes + packets * 28);
                            windows.Add(new(count, previous - 30, elapsed - 30, index, bytes, packets, upper, upper * 8 / (elapsed - previous) / 1000,
                                clients[index].Connection.State.View!.Snapshot.Characters.Count, clients[index].Connection.State.View!.Snapshot.Tick));
                            previousStats[index] = now;
                        }
                        previous = elapsed;
                        await File.WriteAllTextAsync(Path.Combine(output, "windows.partial.json"), JsonSerializer.Serialize(windows, new JsonSerializerOptions { WriteIndented = true }), token);
                        if (completed >= 11) continue;
                        for (int index = 0; index < clients.Count; index++)
                        {
                            var remote = clients[index];
                            var character = remote.Connection.State.View!.Roles.Single(r => r.Entity == controlledRoles[index]).Character;
                            var controlled = remote.Connection.State.View.Snapshot.Characters.Single(c => c.Id == character);
                            var position = controlled.Position;
                            // Short alternating stationary-start clicks; path failures and supersession remain explicit trace outcomes.
                            float dx = completed % 2 == 0 ? 20 : -20;
                            remote.Submit(new MoveOrder([controlled.Id], new(position.X + dx, host.Terrain.HeightAt(position.X + dx, position.Z), position.Z)) { Tick = remote.CommandTick });
                        }
                    }
                    lock (metricsGate) measuredProfile = 0;
                    Assert.Equal(12 * count, windows.Count(w => w.Clients == count));
                }
            }
            finally { foreach (var remote in clients) await remote.DisposeAsync(); File.Delete(config.CaFile); }
        }
        finally
        {
            LatencyTrace.Sink = previousSink;
            if (previousModsCfg is null) ModList.ClearModsCfg(); else ModList.UseModsCfg(previousModsCfg);
            lock (metricsGate)
            {
                File.WriteAllText(Path.Combine(output, "components.partial.json"), JsonSerializer.Serialize(new
                {
                    Profile = profile, SimulationThreads = simulationThreads, SynchronousPaths = synchronousPaths,
                    BindingPersistence = bindingPersistence,
                    ComponentBytes = componentBytes.ToDictionary(p => p.Key, p => p.Value.ToDictionary(f => $"{f.Key}:{GameStatePublisher.ComponentName(f.Key)}", f => f.Value)),
                    Publications = publicationSizes.Select(p => new { p.Profile, p.Baseline, p.Bytes, Components = p.Components.ToDictionary(f => $"{f.Key}:{GameStatePublisher.ComponentName(f.Key)}", f => f.Value) }),
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            File.WriteAllText(Path.Combine(output, "latency.partial.json"), JsonSerializer.Serialize(LatencyTraceAnalyzer.Analyze(sink.Records), new JsonSerializerOptions { WriteIndented = true }));
            await using var traceOutput = new StreamWriter(Path.Combine(output, "latency-trace.jsonl"), false);
            foreach (var record in sink.Records) await traceOutput.WriteLineAsync(JsonSerializer.Serialize(record));
        }
    }
    static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string path in Directory.GetFiles(source)) File.Copy(path, Path.Combine(destination, Path.GetFileName(path)));
        foreach (string path in Directory.GetDirectories(source)) Copy(path, Path.Combine(destination, Path.GetFileName(path)));
    }
}
