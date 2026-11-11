using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using Meitou.Simulation.Locks;

namespace Meitou.Tests.Simulation;

public partial class HousePurchaseTests
{
    sealed class MultiplayerHouse : IDisposable
    {
        public PopulationData Data { get; }
        public TownBuilding House { get; }
        public Meitou.Simulation.World World { get; }
        public WorldPlayer A { get; }
        public WorldPlayer B { get; }
        public CharacterId BuyerA { get; }
        public CharacterId BuyerB { get; }
        public MultiplayerHouse()
        {
            (Data, House) = Setup(destroyed: true);
            int a = Data.RegisterFaction("runtime:a", "A", Data.PlayerFaction);
            int b = Data.RegisterFaction("runtime:b", "B", Data.PlayerFaction);
            Data.Relations.PlayerFaction = -1;
            World = HousePurchaseTests.World(Data);
            A = World.Players.Register(new PlayerId(2)); B = World.Players.Register(new PlayerId(3));
            World.Players.SetMembership(A.Id, a, FactionPermission.All);
            World.Players.SetMembership(B.Id, b, FactionPermission.All);
            A.State.Money = B.State.Money = 20000;
            BuyerA = Spawn(A); BuyerB = Spawn(B);
            World.Fixtures.RegisterSite(World, Data, 0);
        }
        CharacterId Spawn(WorldPlayer player)
        {
            var id = World.Characters.Spawn(new CharacterHot { Position = House.Position + Vector3.UnitX * 50 },
                new CharacterCold { IsPlayer = true, Faction = player.State.Faction }, 0);
            World.Players.AssignInitial(player.Id, id);
            return id;
        }
        public BuyBuilding Quote(WorldPlayer player, CharacterId buyer) => new(House.Key, 4800)
        {
            Tick = 0, Faction = player.State.Faction, Buyer = buyer, PropertyRevision = World.Fixtures.PropertyRevision(House.Key),
        };
        public AuthorityConnection Connect(WorldPlayer player)
        {
            var connection = World.Authority.Open(player.Id);
            Assert.True(World.Authority.BeginSynchronization(connection)); Assert.True(World.Authority.Activate(connection));
            return connection;
        }
        public AuthorityContext Context(WorldPlayer player, CharacterId buyer) => new([World.Players.ControlOf(buyer)!.Value], player.State.Faction, player.MembershipRevision(player.State.Faction));
        public void Dispose() => World.Dispose();
    }

    [Fact]
    public async Task CompetingBuyersAndReplayedReceiptsTransferTheHouseAndChargeExactlyOnce()
    {
        using var scene = new MultiplayerHouse();
        var a = scene.Connect(scene.A); var b = scene.Connect(scene.B);
        var quote = scene.Quote(scene.A, scene.BuyerA);
        var first = scene.World.Authority.Submit(a, 1, quote, scene.Context(scene.A, scene.BuyerA));
        var second = scene.World.Authority.Submit(b, 1, scene.Quote(scene.B, scene.BuyerB), scene.Context(scene.B, scene.BuyerB));
        scene.World.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.Evaluated, (await first).Status);
        Assert.Equal(AuthorityStatus.Evaluated, (await second).Status); // Evaluation does not claim business success.
        Assert.Equal(15200, scene.A.State.Money); Assert.Equal(20000, scene.B.State.Money);
        Assert.Equal(scene.A.State.Faction, scene.World.Fixtures.PropertyOwner(scene.House.Key));
        Assert.Equal(scene.A.State.Faction, scene.World.Buildings.Find(scene.House.Key)!.Owner);
        Assert.NotEqual(quote.PropertyRevision, scene.World.Fixtures.PropertyRevision(scene.House.Key));
        Assert.Same(first, scene.World.Authority.Submit(a, 1, quote, scene.Context(scene.A, scene.BuyerA)));
        scene.World.ApplyPausedInterfaceCommands();
        Assert.Equal(15200, scene.A.State.Money); Assert.False(scene.World.Player.Exists);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("regrant")]
    [InlineData("buyer")]
    [InlineData("payer")]
    [InlineData("range")]
    [InlineData("revision")]
    [InlineData("price")]
    public void PurchaseRechecksFactionActorAndDisplayedPropertyAtExecution(string defect)
    {
        using var scene = new MultiplayerHouse();
        var quote = scene.Quote(scene.A, scene.BuyerA);
        if (defect == "buyer") quote = quote with { Buyer = scene.BuyerB };
        if (defect == "payer")
        {
            scene.World.Players.SetMembership(scene.A.Id, scene.B.State.Faction, FactionPermission.All);
            Assert.True(scene.World.Players.TryTransfer(scene.B.Id, scene.World.Players.ControlOf(scene.BuyerB)!.Value, scene.A.Id, out _));
            quote = quote with { Buyer = scene.BuyerB };
        }
        if (defect == "price") quote = quote with { Price = 1 };
        scene.World.Commands.Enqueue(scene.A.Id, quote);
        if (defect is "permission" or "regrant") scene.World.Players.SetMembership(scene.A.Id, scene.A.State.Faction, FactionPermission.ReadFunds);
        if (defect == "regrant") scene.World.Players.SetMembership(scene.A.Id, scene.A.State.Faction, FactionPermission.All);
        if (defect == "range") scene.World.Characters.Place(scene.BuyerA.Slot, scene.House.Position + new Vector3(5000, 0, 0), 0);
        if (defect == "revision")
        {
            scene.World.Fixtures.TownReplaced(scene.World, scene.Data, 0);
            Assert.Equal(quote.Price, scene.World.Fixtures.SalePrice(scene.House.Key, scene.A.State.Faction));
            Assert.NotEqual(quote.PropertyRevision, scene.World.Fixtures.PropertyRevision(scene.House.Key));
        }
        scene.World.ApplyPausedInterfaceCommands();
        Assert.Equal(20000, scene.A.State.Money); Assert.Equal(20000, scene.B.State.Money);
        Assert.Equal(scene.Data.FactionIndex("owner"), scene.World.Fixtures.PropertyOwner(scene.House.Key));
    }

    [Fact]
    public void RemotePurchaseAndRepairStateSurviveReloadAndTownReplacementBeforeRegistration()
    {
        using var scene = new MultiplayerHouse();
        scene.World.Commands.Enqueue(scene.A.Id, scene.Quote(scene.A, scene.BuyerA));
        scene.World.ApplyPausedInterfaceCommands();
        var site = scene.World.Buildings.Find(scene.House.Key)!;
        site.Destroyed = false; site.Complete = false; site.Ruined = true; site.Paused = true;
        site.Progress = 11; site.Materials[0].Delivered = 12;
        var state = new FcsRecord { RecordType = FcsRecordType.GamestateBuilding, StringId = "owned-house" };
        Assert.True(scene.World.Fixtures.WriteBuilding(scene.House.Key, state));
        var file = new SaveFile(); file.Records.Add(state);
        var persisted = SaveFile.Read(file.ToBytes()).Records.Single();
        using var restored = World(scene.Data);
        var player = restored.Players.Register(scene.A.Id);
        restored.Players.SetMembership(player.Id, scene.A.State.Faction, FactionPermission.None);
        restored.Fixtures.Saved = new Dictionary<string, FcsRecord> { [scene.House.Key] = persisted };
        restored.Fixtures.TownReplaced(restored, scene.Data, 0);
        Assert.Contains(scene.House.Key, restored.Fixtures.Saved.Keys);
        restored.Fixtures.RegisterSite(restored, scene.Data, 0);
        Assert.Equal(scene.A.State.Faction, restored.Fixtures.PropertyOwner(scene.House.Key));
        Assert.Null(restored.Fixtures.SalePrice(scene.House.Key, scene.B.State.Faction));
        var actual = restored.Buildings.Find(scene.House.Key)!;
        Assert.Equal((scene.A.State.Faction, 11f, 12f, false, false, true, true),
            (actual.Owner, actual.Progress, actual.Materials[0].Delivered, actual.Complete, actual.Destroyed, actual.Ruined, actual.Paused));
        restored.Fixtures.TownReplaced(restored, scene.Data, 0);
        Assert.Same(actual, restored.Buildings.Find(scene.House.Key));
    }

    [Fact]
    public void MembershipInNpcFactionDoesNotClaimItsUnpurchasedRuin()
    {
        var (data, house) = Setup(destroyed: true);
        using var world = World(data);
        var player = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(player.Id, data.FactionIndex("owner"), FactionPermission.All);
        world.Fixtures.RegisterSite(world, data, 0);
        Assert.Null(world.Buildings.Find(house.Key));
        Assert.Equal(data.FactionIndex("owner"), world.Fixtures.PropertyOwner(house.Key));
    }

    [Fact]
    public void ExplicitUnpurchasedStateIsNotOverriddenByAFactionOwnershipChange()
    {
        using var scene = new MultiplayerHouse();
        var saved = new FcsRecord { RecordType = FcsRecordType.GamestateBuilding, StringId = "npc-house" };
        saved.Strings["owner faction ID"] = scene.Data.Factions[scene.A.State.Faction].Id;
        saved.Bools[FixtureRegistry.PurchasedKey] = false;
        using var world = World(scene.Data);
        var player = world.Players.Register(scene.A.Id);
        world.Players.SetMembership(player.Id, scene.A.State.Faction, FactionPermission.All);
        world.Fixtures.Saved = new Dictionary<string, FcsRecord> { [scene.House.Key] = saved };
        world.Fixtures.RegisterSite(world, scene.Data, 0);
        Assert.Null(world.Buildings.Find(scene.House.Key));
        Assert.Equal(scene.A.State.Faction, world.Fixtures.PropertyOwner(scene.House.Key));
    }
}
