using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Combat;
using static Meitou.Tests.Gameplay.EconomyTests;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class PhysicalWorldTests
{
    sealed class Serial(Action<SimWorld> act) : ITickSystem
    {
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands) => act(world);
    }

    static SimWorld Scene(PopulationData data, int threads, Action<SimWorld> act) => new(
        new WorldSettings { Seed = 42, Threads = threads, MinPartitionSize = 1, PublishSnapshots = false },
        new OpenGroundWalkability((_, _) => 0), [new PlayerSystem(data), new Serial(act)]);

    static CharacterId Spawn(SimWorld world, int faction, int width, bool player = false) =>
        world.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = faction, Inventory = new Inventory(width, 1), IsPlayer = player }, 0);

    [Fact]
    public void Stack_transfers_document_replacement_and_failures_have_the_same_hash_at_one_and_four_threads()
    {
        Assert.Equal(Run(1), Run(4));

        static ulong Run(int threads)
        {
            var data = TradeTests.Data();
            CharacterId author = default, reader = default;
            using var world = Scene(data, threads, w =>
            {
                var items = w.Physical;
                Assert.True(items.SpawnItem(author, "cloth-t", "", 1));
                var sheet = Assert.Single(items.QueryItems(author));
                Assert.False(items.SpawnItem(author, "junk-t", "", 1));
                Assert.True(items.WriteOnPaper(author, sheet, "junk-t", "letter:one"));
                Assert.False(items.RemoveItem(author, sheet, 1));
                var letter = Assert.Single(items.QueryItems(author));
                Assert.Equal("letter:one", letter.ModTag);
                Assert.False(items.TransferItem(author, reader, letter, 2));
                Assert.True(items.TransferItem(author, reader, letter, 1));
                Assert.Empty(items.QueryItems(author));
                Assert.Same(letter, Assert.Single(items.QueryItems(reader)));
                var token = new InventoryItemToken(0, letter.Record, letter.Section, letter.X, letter.Y, 1, letter.Quality)
                    { ModTagDigest = ItemTagIdentity.Digest(letter.ModTag) };
                Assert.Same(letter, items.ResolveItem(reader, token));
                Assert.True(items.SetModTag(reader, letter, "letter:two"));
                Assert.Null(items.ResolveItem(reader, token));
                Assert.Equal(ItemTagIdentity.Digest("letter:two"), ItemTagIdentity.Digest(letter.ModTag));
                Assert.False(items.SetModTag(author, letter, ""));
                Assert.False(items.SpawnItem(reader, "missing-template", "", 1));
                Exception? rejected = null;
                var worker = new Thread(() => rejected = Record.Exception(items.RequireSerial));
                worker.Start();
                worker.Join();
                Assert.IsType<InvalidOperationException>(rejected);
            });
            author = Spawn(world, data.PlayerFaction, 1);
            reader = Spawn(world, data.PlayerFaction, 1);
            Assert.Throws<InvalidOperationException>(world.Physical.RequireSerial);
            world.RunTick();
            return world.StateHash();
        }
    }

    [Fact]
    public void Native_treasuries_coins_bounties_and_faction_names_follow_the_committed_transaction()
    {
        var data = TradeTests.Data();
        int local = data.PlayerFaction, other = data.FactionIndex("f-t");
        CharacterId payer = default, trader = default;
        using var world = Scene(data, 4, w =>
        {
            Assert.False(w.Physical.TransferCats(local, other, 101));
            Assert.True(w.Physical.TransferCats(local, other, 40));
            Assert.True(w.Physical.TransferCats(payer, trader, 25));
            Assert.True(w.Physical.TransferCats(trader, payer, 7));
            Assert.False(w.Physical.TransferCats(trader, payer, 19));
            Assert.Equal(18, Assert.Single(w.Physical.QueryItems(trader)).Quantity);
            Assert.Equal(42, w.Player.Money);
            Assert.Equal(40, w.Factions.Find(other)!.Money);
            Assert.True(w.Physical.SetDisplayName(other, "North Guild"));
            Assert.True(w.Physical.AdjustRelation(other, local, 7));
            Assert.Equal(7, data.Relations.Get(other, local));
            Assert.True(w.Physical.AddOrClearBounty(trader, other, 125));
            Assert.Equal(125, w.Characters.Cold(trader.Slot)!.Bounties!.AmountFor(other));
            Assert.False(w.Physical.AddOrClearBounty(trader, other, int.MaxValue));
            Assert.True(w.Physical.AddOrClearBounty(trader, other, -125));
            Assert.Null(w.Characters.Cold(trader.Slot)!.Bounties);
        });
        world.Player.Faction = local;
        world.Player.Money = 100;
        payer = Spawn(world, local, 1, player: true);
        trader = Spawn(world, other, 1);
        world.RunTick();
        Assert.Equal("North Guild", world.DescribeFaction(other)!.Name);
        var state = SyntheticTown.Rec("camera", FcsRecordType.Camera, "");
        FactionResourceSave.WriteAccounts(world, data, state);
        using var restored = Scene(data, 1, _ => { });
        FactionResourceSave.ReadAccounts(restored, data, state);
        Assert.Equal(40, restored.Factions.Find(other)!.Money);
        Assert.Equal("North Guild", restored.DescribeFaction(other)!.Name);
    }

    [Fact]
    public void A_transferred_town_house_reloads_with_the_same_fixtures_and_without_emptying_its_interior()
    {
        var (data, house) = HousePurchaseTests.Setup();
        int player = data.PlayerFaction;
        using var world = Scene(data, 1, w =>
        {
            var fixtures = w.Fixtures.OfBuilding(house.Key).ToArray();
            Assert.NotEmpty(fixtures);
            Assert.True(w.Physical.SetOwner(house.Key, player));
            Assert.Equal(player, w.Physical.GetOwner(house.Key));
            Assert.False(w.Fixtures.IsEmptied(house.Key));
            Assert.Equal(fixtures, w.Fixtures.OfBuilding(house.Key));
            Assert.All(fixtures, f => Assert.Equal(player, f.Owner));
            Assert.True(w.Physical.SetLocked(house.Key, true));
        });
        world.Fixtures.RegisterSite(world, data, 0);
        world.RunTick();
        var state = SyntheticTown.Rec("home", FcsRecordType.GamestateBuilding, "");
        world.Fixtures.WriteBuilding(house.Key, state);
        var records = new Dictionary<string, FcsRecord> { [house.Key] = state };
        world.Fixtures.WriteOwners(data, records);
        using var restored = Scene(data, 4, w => Assert.Equal(player, w.Physical.GetOwner(house.Key)));
        restored.Fixtures.Saved = records;
        restored.Fixtures.ReadTitles(data, records);
        restored.Fixtures.RegisterSite(restored, data, 0);
        restored.RunTick();
        Assert.True(restored.Fixtures.AllDoorsLocked(house.Key));
        Assert.False(restored.Fixtures.IsEmptied(house.Key));
    }

    [Fact]
    public void Single_player_construction_transferred_to_an_NPC_faction_remains_a_live_site_after_reload()
    {
        var data = TradeTests.Data(Rec("depot", FcsRecordType.Building));
        var owner = data.FactionIndex("f-t");
        var zone = WorldLayout.ZoneOf(100, 100);
        string id = BuildingRegistry.IdOf(42, zone);
        using var world = Scene(data, 1, w => Assert.True(w.Physical.SetOwner(id, owner)));
        world.Player.Faction = data.PlayerFaction;
        world.Buildings.Add(new BuildSite { Id = id, Record = "depot", Position = new(100, 0, 100), Owner = data.PlayerFaction, Materials = [], Complete = true });
        world.RunTick();
        var saved = new SaveBuilder(1).Game;
        string original = data.Factions[data.PlayerFaction].Id;
        BuildingSave.Write(saved, world, original, population: data);
        using var loaded = Scene(data, 1, _ => { });
        Assert.Equal(1, BuildingSave.Read(saved, loaded, data.Db, original, data.PlayerFaction, data));
        Assert.Equal(owner, loaded.Buildings.Find(id)!.Owner);
    }

    [Fact]
    public void A_transferred_gate_keeps_its_chest_and_the_damaged_leaf_and_tag_survive_the_building_save()
    {
        var db = Db(Rec("204-gamedata.base", FcsRecordType.Faction), Rec("owner", FcsRecordType.Faction),
            Rec("town", FcsRecordType.Town, r => r.References["faction"] = [SyntheticTown.Ref("owner")]),
            Rec("gate", FcsRecordType.Building, r =>
            {
                r.Bools["is wall gate"] = true;
                r.References["doors"] = [SyntheticTown.Ref("leaf")];
            }), Rec("leaf", FcsRecordType.Building), Rec("box", FcsRecordType.Building), Rec("letter", FcsRecordType.Item));
        var gate = new TownBuilding("gate-1", "gate", "Gate", SyntheticTown.Centre, 0, false, false, []);
        string box = gate.Key + "/0";
        gate = gate with { Fixtures = [new TownFixture(box, "box", false, gate.Position, 0) { StorageWidth = 1, StorageHeight = 1 }] };
        var data = PopulationData.Create(db, [new TownPlacement("t", "town", gate.Position, null)],
            buildings: new Dictionary<string, List<TownBuilding>> { ["t"] = [gate] });
        int archive = data.RegisterFaction("archive", "Archives", data.FactionIndex("owner"));
        using var world = Scene(data, 4, w =>
        {
            Assert.True(w.Physical.SpawnItem(box, "letter", "wc.doc:gate", 1));
            Assert.True(w.Physical.SetOwner(gate.Key, archive));
            w.Fixtures.TownReplaced(w, data, 0);
            Assert.Equal("wc.doc:gate", Assert.Single(w.Physical.QueryItems(box)).ModTag);
            Assert.False(w.Physical.DamageGate(gate.Key, float.NaN));
            Assert.True(w.Physical.SetLocked(gate.Key, true));
            Assert.True(w.Physical.DamageGate(gate.Key, 100000));
            Assert.False(w.Physical.DamageGate(gate.Key, 1));
        });
        world.Fixtures.RegisterSite(world, data, 0);
        world.RunTick();
        var state = Rec("gate-save", FcsRecordType.GamestateBuilding);
        var records = new Dictionary<string, FcsRecord> { [gate.Key] = state };
        world.Fixtures.WriteBuilding(gate.Key, state);
        world.Fixtures.WriteOwners(data, records);
        using var restored = Scene(data, 1, w =>
        {
            Assert.Equal(archive, w.Physical.GetOwner(gate.Key));
            Assert.Equal("wc.doc:gate", Assert.Single(w.Physical.QueryItems(box)).ModTag);
            Assert.Equal(archive, w.Fixtures.Find(box)!.Owner);
            Assert.True(w.Fixtures.Find(gate.Key + "#0")!.DoorBroken);
        });
        restored.Fixtures.Saved = records;
        restored.Fixtures.Items = data.Items;
        restored.Fixtures.ReadTitles(data, records);
        restored.Fixtures.RegisterSite(restored, data, 0);
        restored.RunTick();
    }

    [Fact]
    public void Barter_can_exchange_the_last_two_occupied_cells_and_rolls_back_an_inexact_cash_payment()
    {
        var data = TradeTests.Data();
        CharacterId seller = default, buyer = default;
        using var world = Scene(data, 4, w =>
        {
            Assert.True(w.Physical.SpawnItem(seller, "junk-t", "seal", 1));
            Assert.True(w.Physical.SpawnItem(buyer, "cats-t", "", 10));
            var goods = Assert.Single(w.Physical.QueryItems(seller));
            Assert.False(w.Physical.Barter(seller, buyer, goods, 1, 5));
            Assert.Same(goods, Assert.Single(w.Physical.QueryItems(seller)));
            Assert.Equal(10, Assert.Single(w.Physical.QueryItems(buyer)).Quantity);
            Assert.True(w.Physical.Barter(seller, buyer, goods, 1, 10));
            Assert.Equal("seal", Assert.Single(w.Physical.QueryItems(buyer)).ModTag);
            var coins = Assert.Single(w.Physical.QueryItems(seller));
            Assert.Equal("cats-t", coins.Record);
            Assert.Equal(10, coins.Quantity);
        });
        seller = Spawn(world, data.FactionIndex("f-t"), 1);
        buyer = Spawn(world, data.PlayerFaction, 1);
        world.RunTick();
    }

    [Fact]
    public void Encounter_wounds_reach_the_native_limb_and_are_independent_of_worker_count()
    {
        Assert.Equal(Run(1), Run(4));

        static ulong Run(int threads)
        {
            using var world = DuelFixture.NewWorld(1234, threads, out _);
            var id = DuelFixture.Spawn(world, new Combatant(), default);
            world.AddSystem(new Serial(w =>
            {
                var limb = w.Characters.Cold(id.Slot)!.Medical!.LimbPart(2)!;
                float before = limb.Flesh;
                Assert.False(w.Physical.ApplyLimbDamage(id, 4, 5));
                Assert.False(w.Physical.ApplyLimbDamage(id, 2, float.PositiveInfinity));
                Assert.True(w.Physical.ApplyLimbDamage(id, 2, 7));
                Assert.Equal(before - 7, limb.Flesh);
            }));
            world.RunTick();
            return world.StateHash();
        }
    }

    [Fact]
    public void Changing_an_NPC_platoon_keeps_the_character_and_tagged_inventory_in_its_new_saved_squad()
    {
        var data = TradeTests.Data(Rec("messenger", FcsRecordType.Character), Rec("guard", FcsRecordType.Character),
            Rec("patrol", FcsRecordType.SquadTemplate));
        int originalFaction = data.FactionIndex("f-t"), faction = data.RegisterFaction("new-guild", "New Guild", originalFaction);
        using var world = Scene(data, 4, _ => { });
        world.Player.Faction = data.PlayerFaction;
        Squad Make(int owner)
        {
            var platoon = new Platoon { Id = world.Platoons.NextId(), Origin = 0, Faction = owner, TemplateId = "patrol", Key = 1, State = PlatoonState.Loaded };
            var squad = new Squad { Id = world.Squads.NextId(), Faction = owner, Town = 0, TemplateId = "patrol", PlatoonId = platoon.Id };
            platoon.SquadId = squad.Id;
            world.Platoons.Add(platoon);
            world.Squads.Add(squad);
            return squad;
        }
        CharacterId Join(Squad squad, string record)
        {
            var cold = new CharacterCold { Faction = squad.Faction, Name = record, RecordId = record, SquadId = squad.Id, Inventory = new Inventory() };
            var id = world.Characters.Spawn(new CharacterHot { Health = 100 }, cold, 0);
            squad.Members.Add(id);
            squad.Leader = id;
            return id;
        }
        var old = Make(originalFaction);
        var destination = Make(faction);
        var person = Join(old, "messenger");
        var guard = Join(destination, "guard");
        var identity = world.LastingIds.GetOrGive(person);
        world.AddSystem(new Serial(w =>
        {
            Assert.True(w.Physical.SpawnItem(person, "cloth-t", "orders", 1));
            Assert.True(w.Physical.JoinNpcPlatoon(person, destination.PlatoonId, true));
            Assert.Equal(identity, w.Characters.Cold(person.Slot)!.LastingId);
            Assert.Empty(old.Members);
            Assert.Equal(person, destination.Leader);
            Assert.Equal((int)SquadRole.Squad1, w.Characters.Cold(guard.Slot)!.Role);
            Assert.Equal("orders", Assert.Single(w.Physical.QueryItems(person)).ModTag);
        }));
        world.RunTick();
        var save = SaveCapture.Capture(world, data, new SaveClock(1, 1, 0));
        var saved = Assert.Single(save.Platoons, p => p.Characters.Any(c => c.Name == "messenger"));
        Assert.Equal("new-guild", saved.FactionId);
        Assert.Contains(saved.Characters, c => c.Name == "guard");
    }
}
