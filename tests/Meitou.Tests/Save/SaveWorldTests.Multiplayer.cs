using System.Numerics;
using Meitou.Data.Save;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Items;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Save;

public partial class SaveWorldTests
{
    [Fact]
    public void ThreePlayerFactionsRestoreTheirOwnSquadsRolesAndItemsWithoutClaimingSameFactionNpcs()
    {
        using var game = NewGame(items: true);
        var world = game.World;
        int local = world.Player.Faction;
        int remote = game.Data.RegisterFaction("runtime:a", "A", local);
        int joined = game.Data.FactionIndex("20-t");
        world.Players.SetMembership(PlayerId.Local, local, FactionPermission.All);
        world.Players.AssignInitial(PlayerId.Local, game.Squad.Leader);
        var a = world.Players.Register(new PlayerId(2)); var b = world.Players.Register(new PlayerId(3));
        var accountA = new AccountIdentity("local-token", Guid.NewGuid().ToString("N"));
        var accountB = new AccountIdentity("local-token", Guid.NewGuid().ToString("N"));
        world.Players.BindAccount(a.Id, accountA); world.Players.BindAccount(b.Id, accountB);
        world.Players.SetMembership(a.Id, remote, FactionPermission.All);
        world.Players.SetMembership(b.Id, joined, FactionPermission.All);
        var qa = SquadManagement.NewSquad(world, game.Data.Db, new Vector3(50000, 300, 50000), "A", remote);
        var qb = SquadManagement.NewSquad(world, game.Data.Db, new Vector3(-40000, 300, -40000), "B", joined);
        var empty = SquadManagement.NewSquad(world, game.Data.Db, qa.Position, "A-empty", remote);
        var dead = new Squad { Id = world.Squads.NextId(), TemplateId = qa.TemplateId, Name = SquadManagement.DeadSquadName, Faction = remote, Dead = true, PlayerManaged = true };
        world.Squads.Add(dead);
        var npcSquad = new Squad { Id = world.Squads.NextId(), TemplateId = "10-t", Name = "NPC residents", Faction = joined, Town = 0, Position = SyntheticTown.Centre };
        world.Squads.Add(npcSquad);
        string record = world.Characters.Cold(game.Squad.Leader.Slot)!.RecordId;
        CharacterId Spawn(Squad squad, bool player, string name)
        {
            var cold = new CharacterCold { RecordId = record, Name = name, Faction = squad.Faction, SquadId = squad.Id, IsPlayer = player,
                Inventory = new Inventory(), Orders = CharacterOrders.Hold };
            cold.Inventory.Items.Add(game.Data.Items.Create("70-t", 3)!);
            var role = world.Characters.Spawn(new CharacterHot { Position = squad.Position, Yaw = 1 }, cold, world.Tick);
            squad.Members.Add(role);
            if (squad.Leader.IsNone) squad.Leader = role;
            return role;
        }
        var roleA = Spawn(qa, true, "A role"); var roleB = Spawn(qb, true, "B role");
        var deadRole = Spawn(dead, true, "A deceased"); var npc = Spawn(npcSquad, false, "Uncontrolled NPC");
        var ga = world.Players.AssignInitial(a.Id, roleA); var gb = world.Players.AssignInitial(b.Id, roleB);
        var gd = world.Players.AssignInitial(a.Id, deadRole);
        world.Characters.Cold(deadRole.Slot)!.DiedTick = 0;
        var npcEntity = world.Entities.Track(npc);
        int removedId = world.Squads.NextId(); // A consumed identity must not be issued after restart.
        var save = SaveCapture.Capture(world, game.Data, new SaveClock(1, 12, 0));
        byte[] metadata = PlayerRegistrySave.Capture(world, game.Data);
        Assert.Single(save.Platoons, p => p.Characters.Any(c => EntitySave.Read(c) == npcEntity));
        var npcFile = save.Platoons.Single(p => p.Characters.Any(c => EntitySave.Read(c) == npcEntity));
        Assert.False(npcFile.Record.Bools.GetValueOrDefault(PlayerSquadSave.SquadKey));
        Assert.False(npcFile.Characters.Single().GameState!.Bools[PlayerSquadSave.RoleKey]);
        string root = Dir("three-player-squads");
        try
        {
            var store = new CheckpointStore(root); store.Commit(save, metadata);
            var checkpoint = store.LoadCurrent()!;
            var data = SyntheticTown.Data(SyntheticTown.Database(items: true));
            PlayerRegistrySave.PrepareCatalogue(data, checkpoint.Metadata);
            using var restored = EmptyWorld();
            var loaded = SaveLoader.Load(restored, checkpoint.World, data, new SaveLoadOptions { MultiplayerMetadata = checkpoint.Metadata });
            PlayerRegistrySave.Restore(restored, data, checkpoint.Metadata);
            Assert.Equal(a.Id, restored.Players.FindAccount(accountA)!.Id);
            Assert.Equal(b.Id, restored.Players.FindAccount(accountB)!.Id);
            Assert.False(restored.Players.AccountRequiresCheckpoint(accountA));
            foreach (var (grant, expectedSquad) in new[] { (ga, qa), (gb, qb) })
            {
                Assert.True(restored.Players.Validate(grant));
                Assert.True(restored.Entities.TryResolve(grant.Entity, out var role));
                var cold = restored.Characters.Cold(role.Slot)!;
                Assert.Equal(grant.Faction, cold.Faction); Assert.True(cold.IsPlayer);
                Assert.Equal(expectedSquad.Id, cold.SquadId);
                Assert.Equal(expectedSquad.Position, restored.Characters.Current[role.Slot].Position);
                Assert.Equal(3, Assert.Single(cold.Inventory!.Items).Quantity);
                Assert.Equal(CharacterOrders.Hold, cold.Orders);
                Assert.True(restored.Squads.Find(expectedSquad.Id)!.PlayerManaged);
            }
            Assert.Empty(restored.Squads.Find(empty.Id)!.Members);
            Assert.True(restored.Squads.Find(dead.Id)!.Dead);
            Assert.Contains(gd, restored.Players.KnownRoles(a.Id));
            Assert.False(restored.Players.Validate(gd));
            Assert.False(restored.Entities.TryResolve(gd.Entity, out _));
            Assert.False(restored.Entities.TryResolve(npcEntity, out _)); // Same-faction NPC remains an ordinary unloaded platoon.
            Assert.Contains(restored.Saved!.Platoons, p => p.Name == npcFile.Name);
            Assert.Equal(new[] { qb.Id }, SquadManagement.PlayerSquads(restored, joined).Select(q => q.Id));
            Assert.True(restored.Squads.NextId() > removedId);
            var again = SaveCapture.Capture(restored, data, new SaveClock(1, 12, 0), loaded);
            Assert.Equal(save.Platoons.Count, again.Platoons.Count);
            Assert.Contains(again.Platoons.SelectMany(p => p.Characters), c => EntitySave.Read(c) == gd.Entity && c.GameState!.Bools[PlayerSquadSave.DeadKey]);
            Assert.Equal(new[] { local, remote, joined }.Order(), restored.Players.All.SelectMany(p => p.Memberships).Select(m => m.Key).Distinct().Order());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void MultiplayerWorldCannotBeLoadedOrImportedWithoutItsAuthorityMetadata()
    {
        using var game = NewGame();
        game.World.Players.SetMembership(PlayerId.Local, game.World.Player.Faction, FactionPermission.All);
        var save = SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0));
        using var target = EmptyWorld();
        Assert.Throws<InvalidDataException>(() => SaveLoader.Load(target, save, game.Data));
        Assert.Empty(target.Entities.All); Assert.Equal(0, target.Squads.NextAvailableId);
        Assert.Throws<InvalidOperationException>(() => SaveImport.Filter(save, ImportOptions.Default, game.Data));
        Assert.NotEmpty(save.Platoons);
    }

    [Fact]
    public void DuplicatePersistentSquadIdsAreRejectedBeforeWorldMutation()
    {
        using var game = NewGame();
        game.World.Players.SetMembership(PlayerId.Local, game.World.Player.Faction, FactionPermission.All);
        SquadManagement.NewSquad(game.World, game.Data.Db, Vector3.Zero, "empty");
        var save = SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0));
        var metadata = PlayerRegistrySave.Capture(game.World, game.Data);
        var squads = save.Platoons.Where(p => p.Record.Bools.GetValueOrDefault(PlayerSquadSave.SquadKey)).ToArray();
        squads[1].Record.Ints[PlayerSquadSave.SquadIdKey] = squads[0].Record.Ints[PlayerSquadSave.SquadIdKey];
        using var target = EmptyWorld();
        Assert.Throws<InvalidDataException>(() => SaveLoader.Load(target, save, game.Data,
            new SaveLoadOptions { MultiplayerMetadata = metadata }));
        Assert.Empty(target.Entities.All); Assert.Equal(0, target.Squads.NextAvailableId);
        Assert.Equal(0, target.Characters.Count);
    }

    [Fact]
    public void CaptureRejectsAControlledRoleThatHasNoSaveableRoster()
    {
        using var game = NewGame();
        int faction = game.World.Player.Faction;
        game.World.Players.SetMembership(PlayerId.Local, faction, FactionPermission.All);
        var role = game.World.Characters.Spawn(new CharacterHot(), new CharacterCold
        {
            RecordId = game.World.Characters.Cold(game.Squad.Leader.Slot)!.RecordId, Faction = faction, IsPlayer = true,
        }, 0);
        var grant = game.World.Players.AssignInitial(PlayerId.Local, role);
        Assert.Throws<InvalidDataException>(() => SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0)));
        game.World.Characters.Cold(role.Slot)!.SquadId = game.Squad.Id;
        game.Squad.Members.Add(role);
        var save = SaveCapture.Capture(game.World, game.Data, new SaveClock(1, 12, 0));
        Assert.Single(save.Platoons.SelectMany(p => p.Characters), c => EntitySave.Read(c) == grant.Entity);
    }
}
