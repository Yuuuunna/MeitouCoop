using System.Numerics;
using Meitou.Server;
using System.Security.Cryptography;
using Meitou.Networking;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Runtime.Placement;
using Meitou.Navigation;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Construction;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public class HeadlessContentTests
{
    [Fact, Slow]
    public async Task InstalledCreationAndReconnectKeepAuthorizedViewsAcrossTwentyPublications()
    {
        NativeFixture.Require();
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        using var host = HeadlessWorld.Open(install!, new WorldSettings { Threads = 1, PublishSnapshots = false },
            new StandardSystemOptions { SynchronousPaths = true, Population = new PopulationSettings { Background = false } });
        host.Runtime.Speed = 0; var data = host.Content.Population; data.Relations.PlayerFaction = -1;
        var starts = NewGameStart.LoadAll(data.Db); var start = NewGameStart.Find(starts, NewGameStart.DefaultName) ?? starts.First();
        string root = Save.SaveSamples.Temp("installed-network-creation");
        try
        {
            var accounts = new Meitou.Server.TokenAccountStore(Path.Combine(root, "accounts.json")); var credential = accounts.Create("first-network-player");
            var checkpoints = host.CreateCheckpointCoordinator(new CheckpointStore(root));
            var sessions = new Meitou.Server.ServerAccountSessions(accounts, host.Runtime, checkpoints);
            await using var loop = new Meitou.Server.ServerWorldLoop(host.Runtime);
            var birth = new Meitou.Server.HeadlessCreationPreparation(host, loop); var arrival = new Meitou.Server.HeadlessEntryPreparation(host, loop);
            var creation = new Meitou.Server.ServerCreationEndpoint(loop, checkpoints, data,
                [new("network-start", data.Bodies.Constants.MaxSquadSize, [new(start, data.PlayerFaction)])], birth.PrepareAsync, "network-start");
            using var certificate = GameTlsTests.Certificate();
            var hello = host.CreateCompatibilityHello("installed-network-test", new Dictionary<string, string> { ["start"] = start.Id }, []);
            var peer = new Meitou.Server.SecureServerPeer(loop, sessions, hello, new(data.Db), arrival.PrepareAsync, creation: creation);
            await using var server = new Meitou.Server.QuicGameServer(new(System.Net.IPAddress.Loopback, 0), certificate, peer);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await using var client = await RemoteGameSession.ConnectAsync("127.0.0.1", server.Endpoint.Port, hello,
                new(credential.Account, credential.Token), new(data.Db), timeout.Token, transportOptions: NativeFixture.Options(certificate));
            Assert.Equal(PlayerEntryRoute.NewPlayer, client.State.Admission.State.Entry!.Route);
            Assert.Equal(0, await loop.InvokeAsync(() => host.World.Characters.Count, timeout.Token));
            var offered = await client.CatalogAsync(new(Guid.NewGuid()), timeout.Token);
            Assert.Equal(Meitou.Networking.CreationOutcome.Catalog, offered.Outcome);
            var grant = offered.Catalog!.Qualification.Grant;
            var request = new PlayerCreationRequest(Guid.NewGuid(), grant.Id, grant.Revision, start.Id, null, [new(0, "First online role")]);
            var draft = new Meitou.Networking.CreationDraftQuery(Guid.NewGuid(), 1, request);
            var duplicateName = await client.PreviewAsync(draft with { Query = Guid.NewGuid(), Request = request with { FactionName = data.Factions[data.PlayerFaction].Name } }, timeout.Token);
            Assert.Equal(CreationOutcome.Rejected, duplicateName.Outcome);
            Assert.Equal(CreationFailure.InvalidRequest, duplicateName.Failure);
            var preview = await client.PreviewAsync(draft, timeout.Token);
            Assert.Equal(Meitou.Networking.CreationOutcome.Preview, preview.Outcome);
            Assert.NotNull(preview.Preview!.Members[0].Appearance); Assert.NotNull(preview.Preview.Members[0].Editor);
            Assert.Equal(0, await loop.InvokeAsync(() => host.World.Characters.Count, timeout.Token));
            var committed = await client.CommitAsync(draft with { Query = Guid.NewGuid() }, timeout.Token);
            Assert.Equal(Meitou.Networking.CreationOutcome.Committed, committed.Outcome);
            await Enter(client, committed.Receipt!.Roles[0]);
            Assert.Equal(start.Money, Assert.Single(client.State.View!.Factions).Money);
            Assert.Contains(client.State.View.Snapshot.Characters, c => c.Name == "First online role" && c.ItemGrid is not null);
            Assert.NotNull(client.State.View.Weather);
            var weatherView = new Meitou.Rendering.WorldWeather(WeatherWorld.Create(data.Db, install!, 999, WeatherTime.At(0))) { Log = false };
            weatherView.AcceptRemoteAuthority(client.State.View.Weather!);
            Assert.Equal(client.State.View.Weather!.Regions.OrderBy(r => r.RegionId), weatherView.World.Snapshot().OrderBy(r => r.RegionId));
            Assert.False(await loop.InvokeAsync(() => host.World.Player.Exists, timeout.Token));
            Assert.NotNull(new CheckpointStore(root).LoadCurrent());
            var entity = committed.Receipt.Roles[0];
            var previousPosition = await loop.InvokeAsync(() =>
            {
                Assert.True(host.World.Entities.TryResolve(entity, out var role)); return host.World.Characters.Current[role.Slot].Position;
            }, timeout.Token);
            await client.DisposeAsync(); await server.DisposeAsync(); await loop.DisposeAsync();
            var restored = host.RestoreCheckpoint(checkpoints, new CheckpointStore(root).LoadCurrent()!, deferPlayerRoles: true);
            Assert.Null(restored.CleanupFailure); Assert.NotSame(data, host.Population);
            await using var restoredLoop = new Meitou.Server.ServerWorldLoop(host.Runtime);
            var restoredSessions = new Meitou.Server.ServerAccountSessions(accounts, host.Runtime, restored.Coordinator);
            var restoredArrival = new Meitou.Server.HeadlessEntryPreparation(host, restoredLoop);
            var restoredPeer = new Meitou.Server.SecureServerPeer(restoredLoop, restoredSessions, hello, new(host.Population.Db), restoredArrival.PrepareAsync);
            await using var restoredServer = new Meitou.Server.QuicGameServer(new(System.Net.IPAddress.Loopback, 0), certificate, restoredPeer);
            await using var resumed = await RemoteGameSession.ConnectAsync("127.0.0.1", restoredServer.Endpoint.Port, hello,
                new(credential.Account, credential.Token), new(host.Population.Db), timeout.Token, transportOptions: NativeFixture.Options(certificate));
            Assert.NotEqual(client.State.Admission.Identity.World, resumed.State.Admission.Identity.World);
            Assert.Contains(resumed.State.Admission.State.Roles, r => r.Entity == entity && !r.Dead);
            await Enter(resumed, entity);
            Assert.Equal(start.Money, Assert.Single(resumed.State.View!.Factions).Money);
            Assert.Equal(previousPosition, await restoredLoop.InvokeAsync(() =>
            {
                Assert.True(host.World.Entities.TryResolve(entity, out var role)); return host.World.Characters.Current[role.Slot].Position;
            }, timeout.Token));
            async Task Enter(RemoteGameSession session, EntityId role)
            {
                session.RequestEntry(role);
                var state = session.State;
                while (!state.CanControl)
                {
                    Assert.Null(state.Disconnection);
                    state = await session.WaitForChangeAsync(state.Version, timeout.Token);
                }
                // Admission alone is not a stable loaded view. Observe more than two history windows,
                // both on first entry and after restoring the checkpoint.
                ulong publication = state.View!.Publication;
                for (int updates = 0; updates < 20;)
                {
                    state = await session.WaitForChangeAsync(state.Version, timeout.Token);
                    Assert.Null(state.Disconnection);
                    Assert.True(state.CanControl);
                    Assert.NotNull(state.View);
                    Assert.NotEmpty(state.View.Snapshot.Characters);
                    if (state.View.Publication > publication) { publication = state.View.Publication; updates++; }
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void TerrainCachePreservesHeightsAtTileBoundariesAndEvictionUnderParallelReads()
    {
        byte[] bytes = World.TerrainTests.Tiff(257, 257, (x, z) => (ushort)((x * 127 + z * 59) % 65536));
        using var expected = TerrainHeightmap.Open(new MemoryStream(bytes));
        using var terrain = new TerrainSource(TerrainHeightmap.Open(new MemoryStream(bytes)), capacity: 2);
        Parallel.For(0, 120, i =>
        {
            float x = (float)(i % 4 * 64 + 0.25) / 256 * WorldLayout.WorldSize - WorldLayout.HalfWorldSize;
            float z = (float)(i % 3 * 64 + 0.75) / 256 * WorldLayout.WorldSize - WorldLayout.HalfWorldSize;
            float wanted;
            lock (expected) wanted = expected.HeightAt(x, z);
            Assert.Equal(wanted, terrain.HeightAt(x, z), 3);
        });
        Assert.InRange(terrain.CachedTiles, 1, 2);
        Assert.Equal(expected.HeightAt(WorldLayout.HalfWorldSize, WorldLayout.HalfWorldSize),
            terrain.HeightAt(WorldLayout.HalfWorldSize, WorldLayout.HalfWorldSize), 3);
    }

    [Fact, Slow]
    public async Task InstalledWorldLoadsAndTicksWithoutAnyRenderingAssembly()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        using var host = HeadlessWorld.Open(install!, new WorldSettings { Threads = 1, PublishSnapshots = false },
            new StandardSystemOptions { SynchronousPaths = true, Population = new PopulationSettings { Background = false } });
        Assert.NotNull(host.Content.Navigation);
        Assert.NotNull(host.Content.Population.Sight);
        Assert.NotNull(host.Content.Population.ResourceNodes);
        var hello = host.CreateCompatibilityHello("installed-test", new Dictionary<string, string> { ["tick-hz"] = "30" }, []);
        Assert.Equal(Meitou.Networking.CompatibilityFailure.None, new Meitou.Networking.CompatibilityHandshake(hello).Receive(hello));
        Assert.NotEmpty(host.Content.Towns);
        Assert.Contains(host.World.Systems, s => s is SimulationWeather);
        Assert.Contains(host.World.Systems, s => s is Meitou.Simulation.Combat.CombatSystem);
        Assert.Contains(host.World.Systems, s => s is Meitou.Simulation.Ragdolls.RagdollSystem);
        var starts = NewGameStart.LoadAll(host.Content.Population.Db);
        var start = NewGameStart.Find(starts, NewGameStart.DefaultName) ?? starts.First();
        var place = PopulationSystem.StartPlace(host.Content.Population, host.World.Seed, start).Centre;
        var focus = new Vector3(place.X, host.Terrain.HeightAt(place.X, place.Y), place.Y);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await host.PrepareRegionAsync(focus, timeout.Token);
        var population = host.World.Systems.OfType<PopulationSystem>().Single();
        var squad = population.StartPlayer(host.World, start);
        host.World.Player.InCreation = false;
        Assert.NotEmpty(squad.Members);
        host.World.PreparePopulation(focus);
        Assert.True(host.Content.Navigation!.Walkability.World.Contains(WorldLayout.ZoneOf(focus.X, focus.Z)));
        Assert.Equal(3, host.Advance(3 * WorldRuntime.TickSeconds));
        Assert.Same(host.Weather, host.World.Weather);
        Assert.NotNull(host.Weather.At(0, 0));
        var construction = host.World.Systems.OfType<ConstructionSystem>().Single();
        var placement = Assert.IsType<WorldPlacement>(construction.Settings.Placement);
        var build = host.World.Research.Unlocked.Select(construction.Data)
            .Where(b => b is { IsExteriorFurniture: true, LinksTogether: false, CeilingPlacement: false, IsNode: false })
            .OrderBy(b => b!.Target).ThenBy(b => b!.Id, StringComparer.Ordinal).FirstOrDefault();
        Assert.NotNull(build);
        var footprints = new BuildingFootprints(install!, host.Content.Population.Db, host.Content.Levels);
        _ = footprints.Require(build!.Id); // declared collision must be readable even when the record has no footprint
        var towns = host.Content.Population.Sites.Select(s => new PlacementTown(s.Town, new(s.Position.X, s.Position.Z))).ToArray();
        var candidates = from x in Enumerable.Range(-3, 7) from z in Enumerable.Range(-3, 7)
                         where x != 0 || z != 0
                         let p = focus + new Vector3(x * 3000, 0, z * 3000)
                         where WorldLayout.ZoneOf(p.X, p.Z).IsInsideGrid
                         where PlacementRules.TownBlock(build, new(p.X, p.Z), towns, host.Content.Population.Factions[host.World.Player.Faction].Id) == PlacementFailure.None
                         select new Vector3(p.X, host.Terrain.HeightAt(p.X, p.Z), p.Z);
        var centre = candidates.First();
        await host.PrepareRegionAsync(centre, timeout.Token);
        PlannedBuilding? accepted = null;
        foreach (int x in Enumerable.Range(-3, 7))
        {
            foreach (int z in Enumerable.Range(-3, 7))
            {
                var p = centre + new Vector3(x * 40, 0, z * 40); p.Y = host.Terrain.HeightAt(p.X, p.Z);
                var plan = new PlannedBuilding(build.Id, p, Quaternion.Identity);
                if (placement.Check(host.World, null, host.World.Player.Faction, plan) == PlacementFailure.None) { accepted = plan; break; }
            }
            if (accepted is not null) break;
        }
        Assert.NotNull(accepted);
        int before = host.World.Buildings.Count;
        host.World.Commands.Enqueue(new PlaceBuildings([accepted!]) { Tick = host.World.Tick, Faction = host.World.Player.Faction });
        host.World.ApplyPausedInterfaceCommands();
        Assert.Equal(before + 1, host.World.Buildings.Count);
        var blockerRecord = host.Content.Population.Db.OfType(Meitou.Data.Fcs.FcsRecordType.BUILDING)
            .First(r => r.GetInt("path mode", 2) != 0 && r.GetReferences("interior mask").Any());
        var blocker = new BuildSite { Id = "runtime-navigation-test", Record = blockerRecord.StringId,
            Position = centre + new Vector3(200, 0, 0), Owner = host.World.Player.Faction, Complete = true, Materials = [] };
        host.World.Buildings.Add(blocker); host.Runtime.Synchronize();
        Assert.Contains(host.Content.Navigation.Runtime.All(), p => p.Key == blocker.Id && p.BuildingId == blocker.Record);
        host.World.Buildings.Remove(blocker); host.Runtime.Synchronize();
        Assert.DoesNotContain(host.Content.Navigation.Runtime.All(), p => p.Key == blocker.Id);
        var multiplayerData = host.Content.Population;
        long localMoney = host.World.Player.Money;
        host.World.Players.SetMembership(PlayerId.Local, host.World.Player.Faction, FactionPermission.All);
        foreach (var role in squad.Members) host.World.Players.AssignInitial(PlayerId.Local, role);
        var newcomer = host.World.Players.Register(new PlayerId(2));
        multiplayerData.Relations.PlayerFaction = -1;
        // Runtime's dependency closure must remain usable by a process without a renderer.
        Assert.DoesNotContain(typeof(HeadlessWorld).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Meitou.Rendering", StringComparison.Ordinal));
        Assert.NotEmpty(Meitou.Simulation.Saving.PlayerRegistrySave.Capture(host.World, host.Content.Population));
        string checkpointRoot = Meitou.Tests.Save.SaveSamples.Temp("installed-runtime-checkpoint");
        try
        {
            var store = new CheckpointStore(checkpointRoot);
            var coordinator = host.CreateCheckpointCoordinator(store);
            var service = new PlayerCreationService(coordinator, (ruleset, id) => ruleset == "headless-start" && id == start.Id
                ? new ApprovedCreationStart(start with { ForceStartPos = true, StartPosition = place }, multiplayerData.PlayerFaction) : null);
            var qualification = host.World.CreationQualifications.IssueInitial(newcomer.Id, "headless-start", multiplayerData.Bodies.Constants.MaxSquadSize);
            var editableRace = Meitou.Data.Characters.CharacterDraft.PlayableRaces(multiplayerData.Db).SelectMany(g => g.Races).First();
            var choice = new Meitou.Data.Characters.CreationAppearanceChoice(editableRace.StringId, false,
                [new(Meitou.Data.Characters.EditorTab.Hair, Meitou.Data.Characters.EditorControlKind.HairHue, "Hair colour", 37)], []);
            var request = new PlayerCreationRequest(Guid.NewGuid(), qualification.Id, qualification.Revision, start.Id, "Headless newcomer",
                [new CreationMemberChoice(0, "Network test", choice)]);
            var connection = host.World.Authority.Open(newcomer.Id);
            int charactersBeforeChoice = host.World.Characters.Count;
            foreach (var bad in new[]
            {
                choice with { Sliders = [new(Meitou.Data.Characters.EditorTab.Body, Meitou.Data.Characters.EditorControlKind.Slider, "combat skill", 100)] },
                choice with { Sliders = [new(Meitou.Data.Characters.EditorTab.Hair, Meitou.Data.Characters.EditorControlKind.HairHue, "Hair colour", 101)] },
                choice with { Pickers = [new(Meitou.Data.Characters.EditorControlKind.HairStyle, "missing-attachment")] },
            })
                Assert.Throws<InvalidDataException>(() => service.Execute(connection, request with { Id = Guid.NewGuid(), Choices = [new CreationMemberChoice(0, "Network test", bad)] }));
            Assert.Equal(charactersBeforeChoice, host.World.Characters.Count);
            Assert.Equal(CreationQualificationStatus.Available, host.World.CreationQualifications.Find(qualification.Id)!.Status);
            var preview = service.Preview(connection, request);
            Assert.Equal(charactersBeforeChoice, host.World.Characters.Count);
            var previewCodec = new Meitou.Networking.WorldViewCodec(multiplayerData.Db);
            var previewReply = new Meitou.Networking.CreationReply(new(connection.World, connection.Player, connection.Generation), Guid.NewGuid(), request.Id, 1,
                Meitou.Networking.CreationOutcome.Preview, Preview: preview);
            var delivered = Meitou.Networking.CreationProtocol.ReadReply(Meitou.Networking.CreationProtocol.Reply(previewReply, previewCodec), previewCodec).Preview!;
            Assert.Equal(37, Assert.Single(delivered.Members[0].Editor!.Controls, c => c.Kind == Meitou.Data.Characters.EditorControlKind.HairHue).Value);
            Assert.Contains(editableRace.StringId, delivered.Members[0].Editor!.AllowedRaces);
            Assert.Null(delivered.Members[0].Appearance!.Loadout);
            var receipt = service.Execute(connection, request);
            Assert.NotEmpty(receipt.Roles);
            foreach (var entity in receipt.Roles)
            {
                Assert.True(host.World.Entities.TryResolve(entity, out var role));
                Assert.True(host.World.Players.CanControl(newcomer.Id, role));
            }
            Assert.Equal(receipt.Roles, service.Execute(connection, request).Roles);
            Assert.True(host.World.Entities.TryResolve(receipt.Roles[0], out var editedRole));
            var edited = host.World.Characters.Cold(editedRole.Slot)!;
            Assert.Equal("Network test", edited.Name); Assert.True(edited.LookEdited);
            Assert.Equal(editableRace.StringId, edited.Race!.StringId);
            Assert.Equal(37, edited.Appearance!.Loadout!.Appearance.Record.Floats["Hair Colour"]);
            Assert.Equal(localMoney, host.World.Player.Money);
            Assert.True(host.World.Authority.BeginSynchronization(connection));
            var authorized = host.World.Authority.CaptureView(connection, new(), new())!;
            var codec = new Meitou.Networking.WorldViewCodec(multiplayerData.Db);
            var copiedView = codec.Decode(codec.Encode(authorized));
            var copiedRole = Assert.Single(copiedView.Snapshot.Characters, c => c.Id == editedRole);
            Assert.NotNull(copiedRole.ItemGrid); Assert.Equal(edited.Appearance.Parts.Count, copiedRole.Appearance!.Parts.Count);
            Assert.Same(multiplayerData.Db.Find(editableRace.StringId), copiedRole.Appearance.Race);
            Assert.Null(copiedRole.Appearance.Loadout); Assert.Null(copiedRole.Appearance.BodyFile);
            host.Runtime.Speed = 0;
            await using (var loop = new Meitou.Server.ServerWorldLoop(host.Runtime))
            {
                var arriving = await loop.InvokeAsync(() => host.World.Authority.Open(newcomer.Id), timeout.Token);
                var preparation = new Meitou.Server.HeadlessEntryPreparation(host, loop);
                Assert.False(await preparation.PrepareAsync(arriving, new EntityId(ulong.MaxValue), timeout.Token));
                Assert.True(await preparation.PrepareAsync(arriving, receipt.Roles[0], timeout.Token));
                Assert.True(await loop.InvokeAsync(() => host.World.Players.CanControl(newcomer.Id, editedRole), timeout.Token));
            }
            var captured = coordinator.Capture();
            coordinator.Commit(captured);
            var saved = store.LoadCurrent()!;
            Assert.Empty(saved.World.Problems);
            Assert.True(saved.World.Platoons.Sum(p => p.Characters.Count) >= squad.Members.Count);
            Assert.Equal((int)(captured.Point.GameHours / 24), saved.World.Camera.Day);
            var previousWeather = host.Weather;
            var weatherState = previousWeather.CaptureState();
            previousWeather.CaptureEffects().Validate();
            Assert.Contains(weatherState.Placed.Concat(weatherState.Regions.SelectMany(r => r.Groups)),
                g => g.Units.Any(u => u.Simulation is { } simulation && simulation.Techniques.Any(t => t.Seeds.Length > 0)));
            host.RestoreCheckpoint(coordinator, saved, deferPlayerRoles: true);
            Assert.Equal(World.ParticleCheckpointTests.Encoded(weatherState), World.ParticleCheckpointTests.Encoded(host.Weather.CaptureState()));
            Assert.Equal(previousWeather.CaptureEffects().Nodes, host.Weather.CaptureEffects().Nodes);
            for (int i = 0; i < 8; i++)
            {
                previousWeather.Advance(previousWeather.Schedule.Time, 1f / 30);
                host.Weather.Advance(host.Weather.Schedule.Time, 1f / 30);
            }
            Assert.Equal(World.ParticleCheckpointTests.Encoded(previousWeather.CaptureState()), World.ParticleCheckpointTests.Encoded(host.Weather.CaptureState()));
        }
        finally { if (Directory.Exists(checkpointRoot)) Directory.Delete(checkpointRoot, true); }
    }
}
