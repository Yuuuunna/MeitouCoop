using System.Numerics;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using Meitou.Runtime;
using Meitou.Data.Fcs;
using System.Text.Json.Nodes;
using System.Text;

namespace Meitou.Tests.Save;

public partial class SaveWorldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyEnvelopeCannotDiscardCreationQualificationsOrIntroduceNewPermission(bool qualification)
    {
        using var game = NewGame();
        if (qualification)
        {
            var newcomer = game.World.Players.Register(new PlayerId(2));
            game.World.CreationQualifications.IssueInitial(newcomer.Id, "start", 1);
        }
        else game.World.Players.SetMembership(PlayerId.Local, game.World.Player.Faction, FactionPermission.All);
        var document = JsonNode.Parse(PlayerRegistrySave.Capture(game.World, game.Data))!;
        document["Version"] = 3;
        document["Authority"]!["Version"] = 2;
        if (!qualification) document.AsObject().Remove("Qualifications");
        Assert.Throws<InvalidDataException>(() => PlayerRegistrySave.PrepareCatalogue(game.Data, Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    [Fact]
    public void LegacyMetadataMigratesUnknownRoleHistoryConservatively()
    {
        using var game = NewGame();
        var assigned = game.World.Players.Register(new PlayerId(2));
        game.World.Players.SetMembership(assigned.Id, game.World.Player.Faction, FactionPermission.All);
        var grant = game.World.Players.AssignInitial(assigned.Id, game.Squad.Leader);
        var unknown = game.World.Players.Register(new PlayerId(3));
        var save = SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0));
        var document = JsonNode.Parse(PlayerRegistrySave.Capture(game.World, game.Data))!;
        document["Version"] = 2; document["Authority"]!["Version"] = 1;
        foreach (var player in document["Authority"]!["Players"]!.AsArray())
        {
            player!.AsObject().Remove("RoleHistory");
            player.AsObject().Remove("Account");
            foreach (var member in player["Memberships"]!.AsArray())
                member!["Permissions"] = member["Permissions"]!.GetValue<int>() & ~(int)FactionPermission.CreateCharacters;
        }
        byte[] metadata = Encoding.UTF8.GetBytes(document.ToJsonString());
        using var restored = EmptyWorld();
        SaveLoader.Load(restored, save.Clone(), game.Data, new SaveLoadOptions { MultiplayerMetadata = metadata });
        PlayerRegistrySave.Restore(restored, game.Data, metadata);
        Assert.Equal(PlayerRoleHistory.Assigned, restored.Players.Find(assigned.Id)!.RoleHistory);
        Assert.True(restored.Players.Validate(grant));
        Assert.False(restored.Players.Find(assigned.Id)!.HasPermission(game.World.Player.Faction, FactionPermission.CreateCharacters));
        Assert.Equal(PlayerRoleHistory.Unknown, restored.Players.Find(unknown.Id)!.RoleHistory);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, restored.Players.AssessEntry(unknown.Id).Route);
        var captured = JsonNode.Parse(PlayerRegistrySave.Capture(restored, game.Data))!;
        Assert.Equal(9, captured["Version"]!.GetValue<int>());
        Assert.Equal(4, captured["Authority"]!["Version"]!.GetValue<int>());
        Assert.All(restored.Players.All, player => Assert.Null(player.Account));
    }

    [Fact]
    public void CurrentMetadataRejectsMissingHistoryInsteadOfInventingFirstTimeEligibility()
    {
        using var game = NewGame();
        game.World.Players.Register(new PlayerId(2));
        var document = JsonNode.Parse(PlayerRegistrySave.Capture(game.World, game.Data))!;
        document["Authority"]!["Players"]![1]!.AsObject().Remove("RoleHistory");
        Assert.Throws<InvalidDataException>(() => PlayerRegistrySave.PrepareCatalogue(game.Data, Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    [Fact]
    public void CheckpointRebuildsDynamicFactionCatalogueBeforeLoadingAndRestoresDirectedDiplomacy()
    {
        using var game = NewGame();
        var world = game.World;
        int a = game.Data.RegisterFaction("runtime:a", "A", game.Data.PlayerFaction);
        int b = game.Data.RegisterFaction("runtime:b", "B", a);
        var pa = world.Players.Register(new PlayerId(2));
        var pb = world.Players.Register(new PlayerId(3));
        world.Players.SetMembership(pa.Id, a, FactionPermission.All);
        world.Players.SetMembership(pb.Id, b, FactionPermission.ReadFunds);
        pa.State.Money = 100; pb.State.Money = 250;
        game.Data.Relations.PlayerFaction = -1;
        game.Data.Relations.Set(a, b, -60);
        game.Data.Relations.Set(b, a, 25);
        game.Data.Relations.SetWar(a, b, true);
        game.Data.Relations.SetGlobalTrust(a, 12.5f);
        var expected = game.Data.Relations.Capture();
        var save = SaveCapture.Capture(world, game.Data, new SaveClock(1, 12, 0));
        var metadata = PlayerRegistrySave.Capture(world, game.Data);
        string root = Dir("dynamic-catalogue");
        try
        {
            var store = new CheckpointStore(root);
            store.Commit(save, metadata);
            var checkpoint = store.LoadCurrent()!;
            var data = SyntheticTown.Data(SyntheticTown.Database());
            PlayerRegistrySave.PrepareCatalogue(data, checkpoint.Metadata);
            using var loaded = EmptyWorld();
            SaveLoader.Load(loaded, checkpoint.World, data, new SaveLoadOptions { MultiplayerMetadata = checkpoint.Metadata });
            PlayerRegistrySave.Restore(loaded, data, checkpoint.Metadata);
            Assert.Equal(a, data.FactionIndex("runtime:a"));
            Assert.Equal(b, data.FactionIndex("runtime:b"));
            Assert.Equal(data.Factions[data.PlayerFaction].Id, data.FactionTemplate(b));
            Assert.Equal(100, loaded.Players.Find(pa.Id)!.State.Money);
            Assert.Equal(250, loaded.Players.Find(pb.Id)!.State.Money);
            Assert.False(loaded.Players.Find(pb.Id)!.HasPermission(b, FactionPermission.SpendFunds));
            Assert.Equal(-1, data.Relations.PlayerFaction);
            Assert.Equal(-60, data.Relations.Get(a, b));
            Assert.Equal(25, data.Relations.Get(b, a));
            Assert.True(data.Relations.AtWar(a, b));
            Assert.False(data.Relations.AtWar(b, a));
            Assert.Equal(12.5f, data.Relations.GlobalTrust(a));
            Assert.Equal(expected.Coexistence, data.Relations.Capture().Coexistence);
            Assert.Equal(expected.Changes, data.Relations.Changes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CataloguePreparationRejectsDamagedDiplomacyBeforeRegisteringAnyFaction()
    {
        using var game = NewGame();
        game.Data.RegisterFaction("runtime:a", "A", game.Data.PlayerFaction);
        var document = JsonNode.Parse(PlayerRegistrySave.Capture(game.World, game.Data))!;
        document["Diplomacy"]!["Wars"] = new JsonArray();
        var data = SyntheticTown.Data(SyntheticTown.Database());
        int count = data.Factions.Count;
        Assert.Throws<InvalidDataException>(() => PlayerRegistrySave.PrepareCatalogue(data, Encoding.UTF8.GetBytes(document.ToJsonString())));
        Assert.Equal(count, data.Factions.Count);
        Assert.Equal(-1, data.FactionIndex("runtime:a"));
    }

    [Fact]
    public void CheckpointPreservesSeparateFactionResourcesBeyondTheLegacyMoneyRange()
    {
        var db = SyntheticTown.Database(extra:
        [
            SyntheticTown.Rec("tech-a", FcsRecordType.Research, "A"),
            SyntheticTown.Rec("tech-b", FcsRecordType.Research, "B"),
            SyntheticTown.Rec("tech-c", FcsRecordType.Research, "C"),
        ]);
        using var game = NewGame(db: db);
        var world = game.World;
        int local = world.Player.Faction, other = game.Data.FactionIndex("20-t");
        world.Players.SetMembership(PlayerId.Local, local, FactionPermission.All);
        world.Players.AssignInitial(PlayerId.Local, game.Squad.Leader);
        var remote = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(remote.Id, other, FactionPermission.All);
        var a = world.Factions.GetOrCreate(local); var b = world.Factions.GetOrCreate(other);
        a.Money = 4_000_000_000; b.Money = long.MaxValue - 7;
        a.Name = "Local faction"; b.Name = "Remote faction";
        a.RaiseRank(other, 0); a.RaiseRank(other, 0); b.RaiseRank(local, 0);
        a.Research.Set(db, ["tech-a"], [("tech-b", 12.5f)], ["tech-b"], [("tech-c", 7.25f)]);
        b.Research.Set(db, [], [("tech-c", 99)], ["tech-c"], []);
        var save = SaveCapture.Capture(world, game.Data, new SaveClock(1, 12, 0));
        var metadata = PlayerRegistrySave.Capture(world, game.Data);
        a.Money = 1; b.Research.Set(db, []); // Captured data must be detached.
        string root = Dir("faction-resources");
        try
        {
            var store = new CheckpointStore(root);
            store.Commit(save, metadata);
            var checkpoint = store.LoadCurrent()!;
            using var loaded = EmptyWorld();
            SaveLoader.Load(loaded, checkpoint.World, game.Data, new SaveLoadOptions { MultiplayerMetadata = checkpoint.Metadata });
            Assert.Equal(4_000_000_000, loaded.Player.Money);
            PlayerRegistrySave.Restore(loaded, game.Data, checkpoint.Metadata);
            var la = loaded.Factions.Find(local)!; var lb = loaded.Factions.Find(other)!;
            Assert.Equal(4_000_000_000, la.Money); Assert.Equal(long.MaxValue - 7, lb.Money);
            Assert.Equal("Local faction", la.Name); Assert.Equal("Remote faction", lb.Name);
            Assert.Equal(2, la.Rank(other)); Assert.Equal(1, lb.Rank(local));
            Assert.Equal(new[] { "tech-a" }, la.Research.Completed);
            Assert.Equal(("tech-b", 12.5f), (la.Research.Queue.Single().Id, la.Research.Queue.Single().Seconds));
            Assert.Equal(7.25f, la.Research.Partial["tech-c"]);
            Assert.Contains("tech-b", la.Research.Paid);
            Assert.Empty(lb.Research.Completed);
            Assert.Equal(("tech-c", 99f), (lb.Research.Queue.Single().Id, lb.Research.Queue.Single().Seconds));
            Assert.Same(lb, loaded.Players.Find(remote.Id)!.State.FactionState);
            Assert.True(la.TryDebit(10)); Assert.Equal(long.MaxValue - 7, lb.Money);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("catalogue")]
    [InlineData("allocator")]
    [InlineData("resource")]
    [InlineData("research")]
    [InlineData("required")]
    public void MismatchedCheckpointMetadataDoesNotPartiallyRestoreAuthorityOrMoney(string defect)
    {
        using var game = NewGame();
        game.World.Players.SetMembership(PlayerId.Local, game.World.Player.Faction, FactionPermission.All);
        game.World.Players.AssignInitial(PlayerId.Local, game.Squad.Leader);
        game.World.Players.Register(new PlayerId(2));
        var save = SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0));
        var metadata = PlayerRegistrySave.Capture(game.World, game.Data);
        var json = JsonNode.Parse(metadata)!;
        switch (defect)
        {
            case "catalogue": json["Catalogue"]!["Factions"]![0]!["Id"] = "different-faction"; break;
            case "allocator": json["EntityHighWater"] = 999ul; break;
            case "resource": json["Resources"]!.AsArray().Clear(); break;
            case "research": json["Resources"]![0]!["Research"]!["Completed"] = new JsonArray("missing-tech"); break;
            case "required": json["Authority"]!.AsObject().Remove("Revision"); break;
        }
        using var loaded = EmptyWorld();
        SaveLoader.Load(loaded, save, game.Data, new SaveLoadOptions { MultiplayerMetadata = metadata });
        long money = loaded.Player.Money;
        Assert.Throws<InvalidDataException>(() => PlayerRegistrySave.Restore(loaded, game.Data, Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.False(loaded.Players.RequiresExplicitControl);
        Assert.Single(loaded.Players.All);
        Assert.Equal(money, loaded.Player.Money);
        Assert.Empty(loaded.Players.Find(PlayerId.Local)!.Memberships);
    }

    [Fact]
    public void CheckpointRestoresPlayerMembershipsAndCurrentGrantsWithoutRevivingOldGrants()
    {
        using var game = NewGame();
        var world = game.World;
        int faction = world.Player.Faction;
        world.Players.SetMembership(PlayerId.Local, faction, FactionPermission.All);
        var remote = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(remote.Id, faction, FactionPermission.ReadFunds);
        var pending = world.Players.Register(new PlayerId(3));
        world.Players.SetMembership(pending.Id, faction, FactionPermission.None);
        var old = world.Players.AssignInitial(PlayerId.Local, game.Squad.Leader);
        Assert.True(world.Players.TryTransfer(PlayerId.Local, old, remote.Id, out var transferred));
        var connection = world.Authority.Open(remote.Id);
        Assert.True(world.Authority.BeginSynchronization(connection));
        Assert.True(world.Authority.Activate(connection));
        var save = SaveCapture.Capture(world, game.Data, new SaveClock(1, 12, 0));
        byte[] metadata = PlayerRegistrySave.Capture(world, game.Data);
        string root = Dir("authority-checkpoint");
        try
        {
            var store = new CheckpointStore(root);
            store.Commit(save, metadata);
            var checkpoint = store.LoadCurrent()!;
            using var loaded = EmptyWorld();
            SaveLoader.Load(loaded, checkpoint.World, game.Data, new SaveLoadOptions { MultiplayerMetadata = checkpoint.Metadata });
            PlayerRegistrySave.Restore(loaded, game.Data, checkpoint.Metadata);
            Assert.False(loaded.Players.Validate(old));
            Assert.True(loaded.Players.Validate(transferred));
            Assert.False(loaded.Players.Find(remote.Id)!.HasPermission(faction, FactionPermission.SpendFunds));
            Assert.Empty(loaded.Players.KnownRoles(pending.Id));
            Assert.False(loaded.Authority.BeginSynchronization(loaded.Authority.Open(pending.Id)));
            Assert.Null(loaded.Authority.GetPhase(connection));
            Assert.True(loaded.Players.TryTransfer(PlayerId.Local, transferred, PlayerId.Local, out var next));
            Assert.True(next.Revision > transferred.Revision);
            Assert.False(loaded.Players.Validate(transferred));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PersistentIdentitiesSurviveDiskRoundTripAndDoNotDependOnCharacterSlots()
    {
        using var game = NewGame(items: true);
        var removed = game.Squad.Members[0];
        var surviving = game.Squad.Members[1];
        var removedEntity = game.World.Entities.Track(removed);
        var entity = game.World.Entities.Track(surviving);
        game.World.Entities.Reserve(new EntityId(900));
        game.World.Characters.Remove(removed);
        var captured = SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0));
        string root = Dir("entity-roundtrip");
        try
        {
            captured.Write(Path.Combine(root, "slot"));
            using var restored = EmptyWorld();
            SaveLoader.Load(restored, SaveGame.Load(Path.Combine(root, "slot")), game.Data);
            Assert.True(restored.Entities.TryResolve(entity, out var current));
            Assert.NotEqual(surviving, current);
            Assert.Equal(game.World.Characters.Cold(surviving.Slot)!.Name, restored.Characters.Cold(current.Slot)!.Name);
            Assert.True(restored.Entities.Contains(removedEntity));
            Assert.False(restored.Entities.TryResolve(removedEntity, out _));
            var newcomer = restored.Characters.Spawn(new CharacterHot(), new CharacterCold(), 0);
            Assert.Equal(new EntityId(901), restored.Entities.Track(newcomer));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unreserved")]
    [InlineData("zero")]
    [InlineData("version")]
    [InlineData("registry")]
    [InlineData("missing")]
    public void CorruptIdentityMapsAreRejectedBeforeAnyWorldStateIsLoaded(string defect)
    {
        using var game = NewGame();
        foreach (var role in game.Squad.Members) game.World.Entities.Track(role);
        var save = SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0));
        var characters = save.Platoons.SelectMany(p => p.Characters).ToArray();
        switch (defect)
        {
            case "duplicate": characters[1].GameState!.Strings[EntitySave.CharacterKey] = characters[0].GameState!.Strings[EntitySave.CharacterKey]; break;
            case "unreserved": characters[0].GameState!.Strings[EntitySave.CharacterKey] = "9999"; break;
            case "zero": characters[0].GameState!.Strings[EntitySave.CharacterKey] = "0"; break;
            case "version": save.Camera.Record.Strings[EntitySave.RegistryKey] = "{\"Version\":2,\"LastAllocated\":4,\"Entities\":[1,2,3,4]}"; break;
            case "registry": save.Camera.Record.Strings[EntitySave.RegistryKey] = "{\"Version\":1,\"LastAllocated\":4,\"Entities\":[1,1,3,4]}"; break;
            case "missing": save.Camera.Record.Strings.Remove(EntitySave.RegistryKey); break;
        }
        using var world = EmptyWorld();
        Assert.Throws<InvalidDataException>(() => SaveLoader.Load(world, save, game.Data));
        Assert.Equal(0, world.Characters.Count);
        Assert.Empty(world.Entities.All);
        Assert.Equal(-1, world.Player.Faction);
    }

    [Fact]
    public void UnloadedSavedCharactersReserveTheirIdentitiesBeforeAnyNewAllocation()
    {
        using var game = NewGame();
        game.World.Entities.Reserve(new EntityId(500));
        var save = SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0));
        var builder = new SaveBuilder(save);
        var npc = builder.AddPlatoon("SavedNPC_0", "Testfolk", "20-t", "10-t", SyntheticTown.Centre);
        npc.Record.Strings["basetown"] = "30-t";
        var role = builder.AddCharacter(npc, save.Platoons[0].Characters[0].RecordId, "Offline NPC", "20-t", SyntheticTown.Centre, Quaternion.Identity, true);
        role.GameState!.Strings[EntitySave.CharacterKey] = "500";
        save.UpdateDerived();
        using var restored = EmptyWorld();
        var loaded = SaveLoader.Load(restored, save, game.Data);
        Assert.True(restored.Entities.Contains(new EntityId(500)));
        Assert.False(restored.Entities.TryResolve(new EntityId(500), out _));
        Assert.Contains(restored.Saved!.Platoons, p => p.Name == npc.Name);
        var newcomer = restored.Characters.Spawn(new CharacterHot(), new CharacterCold(), 0);
        Assert.Equal(new EntityId(501), restored.Entities.Track(newcomer));
        var recaptured = SaveCapture.Capture(restored, game.Data, new SaveClock(1, 12, 0), loaded);
        Assert.Equal(new EntityId(500), EntitySave.Read(recaptured.Platoons.Single(p => p.Name == npc.Name).Characters.Single()));
    }

    [Fact]
    public void SavesWithoutIdentityExtensionsStillLoadWithoutAllocatingIdentities()
    {
        using var game = NewGame();
        var save = SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0));
        Assert.False(save.Camera.Record.Strings.ContainsKey(EntitySave.RegistryKey));
        using var restored = EmptyWorld();
        SaveLoader.Load(restored, save, game.Data);
        Assert.NotEqual(0, restored.Characters.Count);
        Assert.Empty(restored.Entities.All);
    }

    [Fact]
    public void IdentityAssignedToAnOriginalSavedNpcSurvivesUnloadRecaptureAndReload()
    {
        using var game = NewGame();
        var save = SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0));
        var builder = new SaveBuilder(save);
        var npc = builder.AddPlatoon("SavedNPC_0", "Testfolk", "20-t", "10-t", SyntheticTown.Centre);
        npc.Record.Strings["basetown"] = "30-t";
        builder.AddCharacter(npc, save.Platoons[0].Characters[0].RecordId, "Offline NPC", "20-t", SyntheticTown.Centre, Quaternion.Identity, true);
        save.UpdateDerived();
        using var restored = EmptyWorld();
        var loaded = SaveLoader.Load(restored, save, game.Data);
        var stored = restored.Saved!.Platoons.Single(p => p.Name == npc.Name);
        var standin = restored.Platoons.Find(stored.PlatoonId)!;
        CharacterId Spawn()
        {
            var prepared = SavedCharacters.Prepare(stored, game.Data, null, restored.Saved!);
            restored.Characters.BeginTick();
            restored.Characters.Phase = TablePhase.SlowWorld;
            try
            {
                var squad = SavedCharacters.Spawn(restored, standin, stored, prepared, game.Data, new CharacterAssembly(game.Data), new Vector2(1000, 2000), 100)!;
                standin.State = PlatoonState.Loaded;
                standin.SquadId = squad.Id;
                stored.Touched = true;
                return Assert.Single(squad.Members);
            }
            finally { restored.Characters.Phase = TablePhase.Outside; restored.Characters.EndTick(); }
        }
        var first = Spawn();
        var entity = restored.Entities.Track(first);
        Assert.False(npc.Characters.Single().GameState!.Strings.ContainsKey(EntitySave.CharacterKey));
        SavedCharacters.Store(restored, stored, standin.Position);
        restored.Characters.Remove(first);
        standin.State = PlatoonState.Unloaded;
        standin.SquadId = -1;
        var recaptured = SaveCapture.Capture(restored, game.Data, new SaveClock(1, 12, 0), loaded);
        Assert.Equal(entity, EntitySave.Read(recaptured.Platoons.Single(p => p.Name == npc.Name).Characters.Single()));
        var second = Spawn();
        Assert.NotEqual(first, second);
        Assert.True(restored.Entities.TryResolve(entity, out var current));
        Assert.Equal(second, current);
        Assert.Equal(entity, restored.Characters.Cold(second.Slot)!.Entity);
        // The recruitment transition discards the NPC source link and moves the existing role into a player roster.
        var recipient = SquadManagement.NewSquad(restored, game.Data.Db, SyntheticTown.Centre, "recruits");
        var previous = restored.Squads.Find(restored.Characters.Cold(second.Slot)!.SquadId)!;
        previous.Members.Remove(second);
        var recruit = restored.Characters.Cold(second.Slot)!;
        recruit.Save = null; recruit.IsPlayer = true; recruit.Faction = recipient.Faction; recruit.SquadId = recipient.Id;
        recipient.Members.Add(second); recipient.Leader = second;
        restored.Players.SetMembership(PlayerId.Local, recipient.Faction, FactionPermission.All);
        restored.Players.AssignInitial(PlayerId.Local, second);
        var recruitedSave = SaveCapture.Capture(restored, game.Data, new SaveClock(1, 12, 0), loaded);
        Assert.Single(recruitedSave.Platoons.SelectMany(p => p.Characters), c => EntitySave.Read(c) == entity);
        Assert.DoesNotContain(recruitedSave.Platoons.Single(p => p.Name == npc.Name).Characters, c => EntitySave.Read(c) == entity);
        SavedCharacters.Store(restored, stored, standin.Position);
        Assert.True(stored.Members.Single().Gone);
    }
}
