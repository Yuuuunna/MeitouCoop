using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Player;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
using Meitou.Simulation.Trading;
using static Meitou.Tests.Gameplay.EconomyTests;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Prices in play (docs/game/economy.md sections 5 to 7) on an invented database: a trader faction and town with their cultures, and items of each
/// price class.
/// </summary>
public class TradeTests
{
    static FcsReference Ref(string id, int v0 = 0) => new(id, v0, 0, 0);

    /// <summary>
    /// Factions: Nameless (the player, 0), Traders (1). Towns: Trade Town (Traders, at the origin), Near Town (1000 away), Far Town (500000 away), a
    /// nest beside it. The faction's culture prices cloth at 200 %, the town's the katana at 150 % (and cloth not at all, so the faction's applies).
    /// <c>all trade goods</c> holds cloth.
    /// </summary>
    internal static PopulationData Data(params FcsRecord[] extra)
    {
        var db = Db([
            Rec("204-gamedata.base", FcsRecordType.Faction, r => r.Name = "Nameless"),
            Rec("f-t", FcsRecordType.Faction, r => { r.Name = "Traders"; r.References["trade culture"] = [Ref("cf-t")]; }),
            Rec("cf-t", FcsRecordType.ItemsCulture, r => r.References["trade prices"] = [Ref("cloth-t", 200)]),
            Rec("ct-t", FcsRecordType.ItemsCulture, r => r.References["trade prices"] = [Ref("katana-t", 150)]),
            Rec("t1-t", FcsRecordType.Town,
                    r => { r.Ints["type"] = 2; r.References["faction"] = [Ref("f-t")]; r.References["trade culture"] = [Ref("ct-t")]; }),
            Rec("t2-t", FcsRecordType.Town, r => r.Ints["type"] = 2),
            Rec("t3-t", FcsRecordType.Town, r => r.Ints["type"] = 3),
            Rec("n-t", FcsRecordType.Town, r => r.Ints["type"] = 0),
            Rec("katana-t", FcsRecordType.Weapon, r => r.Ints["value"] = 500),
            Rec("cloth-t", FcsRecordType.Item, r => { r.Ints["value"] = 100; r.Bools["trade item"] = true; r.Ints["stackable"] = 20; }),
            Rec("junk-t", FcsRecordType.Item, r => r.Ints["value"] = 100),
            Rec("ration-t", FcsRecordType.Item,
                    r => { r.Ints["value"] = 10; r.Ints["item function"] = 3; r.Floats["charges"] = 100; r.Ints["stackable"] = 10; }),
            Rec("cats-t", FcsRecordType.Item,
                    r => { r.Name = Market.MoneyItemName; r.Ints["value"] = 1; r.Ints["stackable"] = 100; r.Ints["item function"] = 14; }),
            Rec("atg-t", FcsRecordType.VendorList, r => { r.Name = Market.AllTradeGoods; r.References["items"] = [Ref("cloth-t", 100)]; }),
            .. extra]);
        TownPlacement At(string town, float x) => new("i-" + town, town, new Vector3(x, 0, 0), null);
        return PopulationData.Create(db, [At("t1-t", 0), At("t2-t", 1000), At("t3-t", 500_000), At("n-t", 500_100)]);
    }


    // ------------------------------------------------------------------ a shop in a world

    /// <summary>
    /// A trader template (10 items from one list that always adds 500 cats) and a fence that buys stolen goods.
    /// </summary>
    internal static readonly FcsRecord[] Shops =
    [
        Rec("vl-t", FcsRecordType.VendorList, r =>
        {
            r.References["items"] = [Ref("cloth-t", 100), Ref("junk-t", 0)];
            r.Floats["money item prob"] = 100;   // percent-looking, as in the data: always pays
            r.Ints["money item min"] = 500;
            r.Ints["money item max"] = 500;
        }),
        Rec("shop-t", FcsRecordType.SquadTemplate, r =>
        {
            r.Bools["is trader"] = true;
            r.References["vendors"] = [Ref("vl-t", 100)];
            r.Ints["vendors fill total amount"] = 10;
            r.Floats["vendors refresh time"] = 24;
        }),
        Rec("fence-t", FcsRecordType.SquadTemplate, r =>
        {
            r.Bools["is trader"] = true;
            r.Bools["buys stolen"] = true;
            r.References["vendors"] = [Ref("vl-t", 100)];
        }),
        Rec("bshop-t", FcsRecordType.SquadTemplate, r =>
        {
            r.Bools["is trader"] = true;
            r.References["vendors"] = [Ref("vl-t", 100)];
            r.Ints["vendors fill total amount"] = 10;
            r.Ints["vendor money"] = 5000;
        }),
    ];

    [Fact]
    public void A_housed_trader_sells_from_its_homes_merged_stock_containers_with_its_vendor_money_purse()
    {
        using var b = new Bazaar(Rec("box", FcsRecordType.Building, r => r.Name = "Box"));
        var shopkeeper = b.Spawn("shopkeeper", new Vector2(10, 0), b.Data.FactionIndex("f-t"), false, "bshop-t");
        TownFixture Box(string id, int function, int w, int h) => new(id, "box", false, Vector3.Zero, 0) { Function = function, StorageWidth = w,
                StorageHeight = h };
        // Two stock containers (20 x 10 and 10 x 10); a cage and a chest of another function do not count.
        var home = new TownBuilding("shop", "shop", "Shop", Vector3.Zero, 0, true, false, [])
        {
            Fixtures = [Box("a", TownBuildings.ShopFunction, 20, 10), Box("b", TownBuildings.GeneralStorageFunction, 10, 10),
                    Box("c", TownBuildings.CageFunction, 0, 0), Box("d", TownBuildings.RefineryFunction, 30, 30)],
        };
        b.SquadOf(shopkeeper).HomeBuilding = home;
        b.World.Fixtures.RegisterSite(b.World, b.Data, 0);
        b.World.Fixtures.RegisterAdded(b.World, b.Data, 0, home);
        b.World.RunTicks(1);
        var shop = b.SquadOf(shopkeeper).Shop!;
        Assert.Equal(home.Key, shop.Building);
        Assert.Equal((20, 15), (shop.Stock.Width, shop.Stock.Height));   // the widest container's columns, ceil(300 cells / 20) rows
        Assert.InRange(b.SquadOf(shopkeeper).Money, 5000 - 2000, 5000 + 2000);   // vendor money +/- 0.4 x it
        Assert.Empty(shop.Stock.Items);   // the goods lie in the containers
        var held = shop.Containers.SelectMany(id => b.World.Fixtures.Find(id)!.Contents!.All()).ToList();
        Assert.InRange(held.Where(i => i.Record != "cats-t").Sum(i => i.Quantity), 8, 12);
        Assert.Equal(500, held.Where(i => i.Record == "cats-t").Sum(i => i.Quantity));   // the money roll, as money items

        // No stock container: a squad shop of 18 x 18.
        var bare = new TownBuilding("bare", "bare", "Bare", Vector3.Zero, 0, false, false,
                []) { Fixtures = [Box("c", TownBuildings.CageFunction, 0, 0)] };
        Assert.Null(Shop.ForBuilding(bare, shop.Terms));
        Assert.Equal((Shop.Width, Shop.Height), (new Shop { Terms = shop.Terms }.Stock.Width, new Shop { Terms = shop.Terms }.Stock.Height));
    }

    sealed class Bazaar : IDisposable
    {
        public PopulationData Data { get; }
        public SimWorld World { get; }
        public TradeSystem Trade { get; }
        /// <summary>
        /// The conversations; always there, as the Trade order is the dialogue's talk order 12.
        /// </summary>
        public DialogueSystem Dialogue { get; }

        public Bazaar(params FcsRecord[] extra) : this(false, extra) { }

        public Bazaar(bool heldItems, params FcsRecord[] extra)
        {
            // The NPCs' record says is trader, as a real shopkeeper's: with the dialogue system there, IS_A_TRADER reads it (DialogueSystem.Trades).
            Data = TradeTests.Data([.. Shops, Rec("keeper-t", FcsRecordType.Character, r => r.Bools["is trader"] = true), .. extra]);
            Trade = new TradeSystem(Data);
            Dialogue = new DialogueSystem(Data);
            var ground = new OpenGroundWalkability((_, _) => 150);   // above the water line
            ITickSystem[] systems = heldItems
                ? [new PlayerSystem(Data), new MovementSystem(new PathService(ground, true)), Trade, Dialogue]
                : [new MovementSystem(new PathService(ground, true)), Trade, Dialogue];
            World = new SimWorld(new WorldSettings { Seed = 3 }, ground, systems);
            World.Player.Faction = Data.PlayerFaction;
            World.Player.Money = 1000;

            // The trade check runs on its own phase (TickPhases): the tests' first tick is a check tick.
            World.RunTicks(TickPhases.TradeCheck);
        }

        public void Dispose() => World.Dispose();

        public CharacterId Spawn(string name, Vector2 at, int faction, bool player, string template = "p")
        {
            var squad = new Squad { Id = World.Squads.NextId(), TemplateId = template, Faction = faction, Town = 0, HomeCentre = at,
                    HomeRadius = 10 };
            World.Squads.Add(squad);
            var cold = new Combat.Combatant { Name = name }.MakeCold();
            cold.Faction = faction;
            cold.IsPlayer = player;
            if (!player) cold.RecordId = "keeper-t";
            cold.Inventory = new Inventory();
            cold.SquadId = squad.Id;
            var id = World.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, 150, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15,
                    Task = (byte)CharacterTask.Idle }, cold, 0);
            squad.Members.Add(id);
            squad.Leader = id;
            return id;
        }

        public Squad SquadOf(CharacterId id) => World.Squads.Find(World.Characters.Cold(id.Slot)!.SquadId)!;
        public Inventory Bag(CharacterId id) => World.Characters.Cold(id.Slot)!.Inventory!;
        public void Say(SimCommand c) => World.Commands.Enqueue(c with { Tick = World.Tick });
        public TradeView View => World.Snapshot.Trade!;
    }

    sealed class MultiplayerShop : IDisposable
    {
        public Bazaar Bazaar { get; }
        public SimWorld World => Bazaar.World;
        public TradeSystem Trade => Bazaar.Trade;
        public PlayerId A { get; } = new(2);
        public PlayerId B { get; } = new(3);
        public CharacterId CustomerA { get; }
        public CharacterId CustomerB { get; }
        public CharacterId Trader { get; }
        public WorldPlayer PlayerA => World.Players.Find(A)!;
        public WorldPlayer PlayerB => World.Players.Find(B)!;
        public Shop Shop => Bazaar.SquadOf(Trader).Shop!;
        public Squad Vendor => Bazaar.SquadOf(Trader);

        public MultiplayerShop(bool heldItems = false)
        {
            Bazaar = new Bazaar(heldItems);
            int other = Bazaar.Data.RegisterFaction("remote", "Remote", Bazaar.Data.PlayerFaction);
            Bazaar.Data.Relations.PlayerFaction = -1;
            World.Players.Register(A);
            World.Players.Register(B);
            World.Players.SetMembership(A, Bazaar.Data.PlayerFaction, FactionPermission.All);
            World.Players.SetMembership(B, other, FactionPermission.All);
            PlayerA.State.Money = PlayerB.State.Money = 1000;
            CustomerA = Bazaar.Spawn("A", Vector2.Zero, PlayerA.State.Faction, true);
            CustomerB = Bazaar.Spawn("B", new Vector2(1, 0), PlayerB.State.Faction, true);
            Trader = Bazaar.Spawn("vendor", new Vector2(10, 0), Bazaar.Data.FactionIndex("f-t"), false, "shop-t");
            World.Players.AssignInitial(A, CustomerA);
            World.Players.AssignInitial(B, CustomerB);
            World.RunTick();
            Shop.Stock.Items.Clear();
            Assert.True(Shop.Stock.Add(Bazaar.Data.Items.Create("cloth-t", 1)!));
        }

        public void Send(PlayerId player, SimCommand command) => World.Commands.Enqueue(player, command with { Tick = World.Tick });
        public void OpenBoth()
        {
            Send(A, new OpenTrade(CustomerA, Trader) { Tick = 0 });
            Send(B, new OpenTrade(CustomerB, Trader) { Tick = 0 });
            World.RunTick();
            Assert.NotNull(Trade.PublishTrade(A));
            Assert.NotNull(Trade.PublishTrade(B));
        }
        public TradeView View(PlayerId player) => Assert.IsType<TradeView>(Trade.PublishTrade(player));
        public void Dispose() => Bazaar.Dispose();
    }

    [Fact]
    public void AuthorizedPublicationDropsCachedTradeImmediatelyAfterMembershipChanges()
    {
        using var m = new MultiplayerShop(); m.OpenBoth();
        var connection = m.World.Authority.Open(m.A);
        Assert.True(m.World.Authority.BeginSynchronization(connection));
        Assert.NotNull(m.World.Authority.CaptureView(connection, new(), new())!.Snapshot.Trade);
        m.World.Players.SetMembership(m.A, m.PlayerA.State.Faction, FactionPermission.SpendFunds);
        Assert.NotNull(m.Trade.PublishTrade(m.A)); // cache still holds the previous window until the next tick
        Assert.Null(m.World.Authority.CaptureView(connection, new(), new())!.Snapshot.Trade);
        Assert.NotNull(m.Trade.PublishTrade(m.World, m.B));
    }

    [Fact]
    public void DisconnectCleanupClosesOnlyTheCurrentConnectionsWindowsAndKeepsOtherTraders()
    {
        using var m = new MultiplayerShop(); m.OpenBoth();
        var old = m.World.Authority.Open(m.A); var current = m.World.Authority.Open(m.A);
        m.World.ReleasePlayerConnection(old);
        Assert.NotNull(m.Trade.PublishTrade(m.A)); Assert.NotNull(m.Trade.PublishTrade(m.B));
        m.World.ReleasePlayerConnection(current);
        Assert.Null(m.Trade.PublishTrade(m.A)); Assert.NotNull(m.Trade.PublishTrade(m.B));
        Assert.False(DecisionSystem.Ordered(m.World.Characters.Cold(m.Trader.Slot)!)); // open trade windows leave the trader with its AI
        Assert.False(m.Bazaar.Dialogue.HoldsNpc(m.Trader));
        Assert.True(m.World.Players.CanControl(m.A, m.CustomerA));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoFactionsCompeteForOneStockItemWithoutDuplicatingItOrChargingTheLoser(bool reverse)
    {
        using var m = new MultiplayerShop();
        m.OpenBoth();
        var a = m.View(m.A);
        var b = m.View(m.B);
        Assert.NotEqual(a.SessionId, b.SessionId);
        Assert.Null(m.Trade.PublishTrade()); // neither remote window replaces the local interface's window
        long vendorBefore = m.Vendor.Money;
        var first = reverse ? m.B : m.A;
        var second = reverse ? m.A : m.B;
        var firstView = reverse ? b : a;
        var secondView = reverse ? a : b;
        m.Send(first, new BuyItem(firstView.Stock.Single().Token, 1) { Tick = 0, SessionId = firstView.SessionId, InventoryRevision = firstView.StockRevision });
        m.Send(second, new BuyItem(secondView.Stock.Single().Token, 1) { Tick = 0, SessionId = secondView.SessionId, InventoryRevision = secondView.StockRevision });
        m.World.RunTick();
        var winner = reverse ? m.CustomerB : m.CustomerA;
        var loser = reverse ? m.CustomerA : m.CustomerB;
        Assert.Equal("cloth-t", Assert.Single(m.Bazaar.Bag(winner).Items).Record);
        Assert.Empty(m.Bazaar.Bag(loser).Items);
        Assert.Empty(m.Shop.Stock.Items);
        Assert.Equal(1000 - firstView.Stock.Single().UnitPrice, m.World.Players.Find(first)!.State.Money);
        Assert.Equal(1000, m.World.Players.Find(second)!.State.Money);
        Assert.Equal(vendorBefore + 2000, m.Vendor.Money + m.PlayerA.State.Money + m.PlayerB.State.Money);
        Assert.Empty(m.World.TakeMessages(first)); // successful trades no longer post bought/sold messages upstream
        Assert.Empty(m.World.TakeMessages(second));
        Assert.Empty(m.World.TakeMessages());
    }

    /// <summary>
    /// Failed purchases still report only to their issuer after successful trade messages were removed.
    /// </summary>
    [Fact]
    public void UnaffordablePurchaseReportsOnlyToTheIssuingPlayer()
    {
        using var m = new MultiplayerShop();
        m.OpenBoth();
        m.PlayerA.State.Money = 0;
        long vendor = m.Vendor.Money;
        var view = m.View(m.A);
        m.Send(m.A, new BuyItem(view.Stock.Single().Token, 1)
            { Tick = 0, SessionId = view.SessionId, InventoryRevision = view.StockRevision });
        m.World.RunTick();

        Assert.Empty(m.Bazaar.Bag(m.CustomerA).Items);
        Assert.Empty(m.Bazaar.Bag(m.CustomerB).Items);
        Assert.Single(m.Shop.Stock.Items);
        Assert.Equal(0, m.PlayerA.State.Money);
        Assert.Equal(1000, m.PlayerB.State.Money);
        Assert.Equal(vendor, m.Vendor.Money);
        Assert.Contains("You can't afford that.", m.World.TakeMessages(m.A));
        Assert.Empty(m.World.TakeMessages(m.B));
        Assert.Empty(m.World.TakeMessages());
    }

    /// <summary>
    /// The held stock item returns to the registered shop's current projection without a purchase, a ground drop or another player's custody.
    /// </summary>
    [Fact]
    public void Dropping_from_trader_stock_returns_the_item_instead_of_dropping_it()
    {
        using var m = new MultiplayerShop(heldItems: true);
        m.OpenBoth();
        var view = m.View(m.A);
        var goods = Assert.Single(view.Stock);
        long vendor = m.Vendor.Money;
        m.Send(m.A, new LiftItem(m.CustomerA, ItemOwner.Stock, goods.Token, goods.Quantity)
        {
            Tick = 0, ControlledCharacters = [m.CustomerA], Serial = 1, ExpectedHeldSerial = 0,
            InventoryRevision = view.StockRevision, SessionId = view.SessionId
        });
        m.World.RunTick();
        var held = Assert.IsType<HeldItem>(m.PlayerA.State.Held);
        Assert.Equal(ItemOwner.Stock, held.From);
        Assert.Same(m.Shop, held.SourceShop);
        Assert.Null(m.PlayerB.State.Held);
        Assert.Null(m.World.Player.Held);

        m.Send(m.A, new DropHeld
        {
            Tick = 0, ControlledCharacters = [m.CustomerA], HolderActor = m.CustomerA,
            Serial = 2, ExpectedHeldSerial = 1, SessionId = view.SessionId
        });
        m.World.RunTick();

        Assert.Empty(m.World.GroundItems.All);
        Assert.Null(m.PlayerA.State.Held);
        Assert.Null(m.PlayerB.State.Held);
        Assert.Empty(m.Bazaar.Bag(m.CustomerA).Items);
        Assert.Equal((goods.Record, goods.Quantity), (Assert.Single(m.Shop.Stock.Items).Record, m.Shop.Stock.Items.Sum(item => item.Quantity)));
        Assert.Equal(goods.Record, Assert.Single(m.View(m.B).Stock).Record);
        Assert.Equal((1000L, 1000L, vendor), (m.PlayerA.State.Money, m.PlayerB.State.Money, m.Vendor.Money));
    }

    [Fact]
    public async Task AuthorityPurchaseReceiptSurvivesReconnectWithoutChargingOrTransferringTwice()
    {
        using var m = new MultiplayerShop();
        var authority = m.World.Authority;
        var connection = authority.Open(m.A);
        Assert.True(authority.BeginSynchronization(connection));
        Assert.True(authority.Activate(connection));
        var opening = authority.Submit(connection, 1, new OpenTrade(m.CustomerA, m.Trader) { Tick = 0 },
            new([m.World.Players.ControlOf(m.CustomerA)!.Value], m.PlayerA.State.Faction, m.PlayerA.MembershipRevision(m.PlayerA.State.Faction)));
        m.World.RunTick();
        Assert.Equal(AuthorityStatus.Evaluated, (await opening).Status);
        var view = m.View(m.A);
        var item = Assert.Single(view.Stock);
        var command = new BuyItem(item.Token, 1) { Tick = 0, SessionId = view.SessionId, InventoryRevision = view.StockRevision };
        long vendor = m.Vendor.Money;
        var bought = authority.Submit(connection, 2, command, new([]));
        m.World.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.Evaluated, (await bought).Status);
        Assert.Equal(1000 - item.UnitPrice, m.PlayerA.State.Money);
        Assert.Equal(vendor + item.UnitPrice, m.Vendor.Money);
        Assert.Single(m.Bazaar.Bag(m.CustomerA).Items);
        connection = authority.Open(m.A);
        Assert.True(authority.BeginSynchronization(connection));
        Assert.True(authority.Activate(connection));
        Assert.Same(bought, authority.Submit(connection, 2, command, new([])));
        m.World.ApplyPausedInterfaceCommands();
        Assert.Equal(1000 - item.UnitPrice, m.PlayerA.State.Money);
        Assert.Equal(vendor + item.UnitPrice, m.Vendor.Money);
        Assert.Single(m.Bazaar.Bag(m.CustomerA).Items);
        Assert.Empty(m.Shop.Stock.Items);
        Assert.Empty(m.Bazaar.Bag(m.CustomerB).Items);
        Assert.Equal(1000, m.PlayerB.State.Money);
    }

    [Fact]
    public void ClosingOneWindowDoesNotCloseAnotherAndOpenTradesLeaveTheTraderFree()
    {
        using var m = new MultiplayerShop();
        m.OpenBoth();
        ulong a = m.View(m.A).SessionId, b = m.View(m.B).SessionId;
        m.Send(m.A, new CloseTrade { Tick = 0, SessionId = a });
        m.Send(m.B, new CloseTrade { Tick = 0, SessionId = a }); // another player's window is not a valid target
        m.World.RunTick();
        Assert.Null(m.Trade.PublishTrade(m.A));
        Assert.Equal(b, m.View(m.B).SessionId);
        Assert.False(DecisionSystem.Ordered(m.World.Characters.Cold(m.Trader.Slot)!)); // open trade windows leave the trader with its AI
        Assert.False(m.Bazaar.Dialogue.HoldsNpc(m.Trader));
        m.Send(m.B, new CloseTrade { Tick = 0, SessionId = b });
        m.World.RunTick();
        Assert.Null(m.Trade.PublishTrade(m.B));
        Assert.False(DecisionSystem.Ordered(m.World.Characters.Cold(m.Trader.Slot)!));
        Assert.False(m.Bazaar.Dialogue.HoldsNpc(m.Trader));
    }

    [Fact]
    public void OldWindowAndRevokedMembershipRequestsDoNotSpendFunds()
    {
        using var m = new MultiplayerShop();
        m.OpenBoth();
        var before = m.View(m.A);
        m.Send(m.A, new CloseTrade { Tick = 0, SessionId = before.SessionId });
        m.Send(m.A, new OpenTrade(m.CustomerA, m.Trader) { Tick = 0 });
        m.World.RunTick();
        var after = m.View(m.A);
        Assert.NotEqual(before.SessionId, after.SessionId);
        m.Send(m.A, new BuyItem(before.Stock.Single().Token, 1) { Tick = 0, SessionId = before.SessionId, InventoryRevision = before.StockRevision });
        m.World.RunTick();
        Assert.Empty(m.Bazaar.Bag(m.CustomerA).Items);
        m.Send(m.A, new BuyItem(after.Stock.Single().Token, 1) { Tick = 0, SessionId = after.SessionId, InventoryRevision = after.StockRevision });
        m.World.Players.SetMembership(m.A, m.PlayerA.State.Faction, FactionPermission.None);
        m.World.Players.SetMembership(m.A, m.PlayerA.State.Faction, FactionPermission.All);
        m.World.RunTick();
        Assert.Empty(m.Bazaar.Bag(m.CustomerA).Items);
        Assert.Equal(1000, m.PlayerA.State.Money);
        Assert.Null(m.Trade.PublishTrade(m.A));
        Assert.NotNull(m.Trade.PublishTrade(m.B));
    }

    [Fact]
    public void MembershipDoesNotGrantSpendingOrExposeTheFactionBalance()
    {
        using var m = new MultiplayerShop();
        m.World.Players.SetMembership(m.A, m.PlayerA.State.Faction, FactionPermission.None);
        m.OpenBoth();
        var view = m.View(m.A);
        Assert.False(view.FundsVisible);
        Assert.False(view.CanSpendFunds);
        Assert.Equal(0, view.PlayerMoney);
        m.Send(m.A, new BuyItem(view.Stock.Single().Token, 1) { Tick = 0, SessionId = view.SessionId, InventoryRevision = view.StockRevision });
        m.World.RunTick();
        Assert.Empty(m.Bazaar.Bag(m.CustomerA).Items);
        Assert.Equal(1000, m.PlayerA.State.Money);
        Assert.Single(m.Shop.Stock.Items);
    }

    [Fact]
    public void OutOfRangeAndOverflowingTradesDoNotMoveItemsOrMoney()
    {
        using var m = new MultiplayerShop();
        Assert.True(m.Bazaar.Bag(m.CustomerA).Add(m.Bazaar.Data.Items.Create("junk-t", 1)!));
        m.OpenBoth();
        var view = m.View(m.A);
        m.PlayerA.State.Money = long.MaxValue;
        long vendorMoney = m.Vendor.Money;
        m.Send(m.A, new SellItem(view.Goods.Single().Token, 1) { Tick = 0, SessionId = view.SessionId, InventoryRevision = view.GoodsRevision });
        m.World.RunTick();
        Assert.Single(m.Bazaar.Bag(m.CustomerA).Items);
        Assert.Equal(long.MaxValue, m.PlayerA.State.Money);
        Assert.Equal(vendorMoney, m.Vendor.Money);
        m.World.Characters.Place(m.CustomerA.Slot, new Vector3(10000, 150, 10000), 0);
        m.Send(m.A, new BuyItem(view.Stock.Single().Token, 1) { Tick = 0, SessionId = view.SessionId, InventoryRevision = view.StockRevision });
        m.World.RunTick();
        Assert.Equal(view.SessionId, m.View(m.A).SessionId); // only item moves are blocked beyond the pass range
        Assert.Single(m.Shop.Stock.Items);
        Assert.Equal(long.MaxValue, m.PlayerA.State.Money);
    }

    [Fact]
    public void APlayerCannotOpenAnotherPlayersCharacterOrBorrowALegacyWindow()
    {
        using var m = new MultiplayerShop();
        m.Send(m.A, new OpenTrade(m.CustomerB, m.Trader) { Tick = 0 });
        m.World.RunTick();
        Assert.Null(m.Trade.PublishTrade(m.A));
        m.Bazaar.Say(new OpenTrade(m.CustomerA, m.Trader) { Tick = 0 });
        m.World.RunTick();
        var local = Assert.IsType<TradeView>(m.Trade.PublishTrade());
        m.Send(PlayerId.Local, new BuyItem(local.Stock.Single().Token, 1) { Tick = 0, SessionId = local.SessionId, InventoryRevision = local.StockRevision });
        m.World.RunTick();
        Assert.Empty(m.Bazaar.Bag(m.CustomerA).Items);
        Assert.Equal(1000, m.PlayerA.State.Money);
        Assert.Single(m.Shop.Stock.Items);
    }

    [Fact]
    public void ChangingTheViewedFactionDoesNotChangeTheTradesPayer()
    {
        using var m = new MultiplayerShop();
        m.OpenBoth();
        int payer = m.PlayerA.State.Faction;
        var view = m.View(m.A);
        m.World.Players.SetMembership(m.A, m.PlayerB.State.Faction, FactionPermission.None);
        Assert.True(m.World.Players.SelectFaction(m.A, m.PlayerB.State.Faction));
        m.Send(m.A, new BuyItem(view.Stock.Single().Token, 1) { Tick = 0, SessionId = view.SessionId, InventoryRevision = view.StockRevision });
        m.World.RunTick();
        Assert.Equal(1000 - view.Stock.Single().UnitPrice, m.World.Factions.Find(payer)!.Money);
        Assert.Equal(1000, m.PlayerB.State.Money);
        Assert.Equal(payer, m.World.Characters.Cold(m.CustomerA.Slot)!.Faction);
    }

    [Fact]
    public void AnotherPlayerCannotUseTheSellersBuybackReceipt()
    {
        using var m = new MultiplayerShop();
        Assert.True(m.Bazaar.Bag(m.CustomerA).Add(m.Bazaar.Data.Items.Create("junk-t", 1)!));
        m.OpenBoth();
        var selling = m.View(m.A);
        m.Send(m.A, new SellItem(selling.Goods.Single().Token, 1) { Tick = 0, SessionId = selling.SessionId, InventoryRevision = selling.GoodsRevision });
        m.World.RunTick();
        var own = m.View(m.A).Stock.Single(i => i.Record == "junk-t");
        var other = m.View(m.B).Stock.Single(i => i.Record == "junk-t");
        Assert.True(own.Buyback);
        Assert.False(other.Buyback);
        m.Send(m.B, new BuyItem(other.Token, 1) { Tick = 0, SessionId = m.View(m.B).SessionId, InventoryRevision = m.View(m.B).StockRevision });
        m.World.RunTick();
        Assert.Equal(1000 - other.Price, m.PlayerB.State.Money);
        Assert.Equal("junk-t", Assert.Single(m.Bazaar.Bag(m.CustomerB).Items).Record);
    }

    [Fact]
    public void IdenticalLookingRestockStillInvalidatesAnOldInventoryRevision()
    {
        using var m = new MultiplayerShop();
        m.OpenBoth();
        var before = m.View(m.A);
        var original = Assert.Single(m.Shop.Stock.Items);
        m.Shop.Stock.Items[0] = original.WithQuantity(original.Quantity);
        m.Send(m.A, new BuyItem(before.Stock.Single().Token, 1)
            { Tick = 0, SessionId = before.SessionId, InventoryRevision = before.StockRevision });
        m.World.RunTick();
        Assert.Empty(m.Bazaar.Bag(m.CustomerA).Items);
        Assert.Equal(1000, m.PlayerA.State.Money);
        var after = m.View(m.A);
        Assert.NotEqual(before.StockRevision, after.StockRevision);
        Assert.Equal(before.Stock.Single().Token, after.Stock.Single().Token);
        m.Send(m.A, new BuyItem(after.Stock.Single().Token, 1)
            { Tick = 0, SessionId = after.SessionId, InventoryRevision = after.StockRevision });
        m.World.RunTick();
        Assert.Single(m.Bazaar.Bag(m.CustomerA).Items);
    }

    [Fact]
    public void PausedTradesStillSerializeStockChangesAndAdvanceInventoryRevisions()
    {
        using var m = new MultiplayerShop();
        m.OpenBoth();
        var a = m.View(m.A);
        var b = m.View(m.B);
        long tick = m.World.Tick;
        m.Send(m.A, new BuyItem(a.Stock.Single().Token, 1) { Tick = 0, SessionId = a.SessionId, InventoryRevision = a.StockRevision });
        m.Send(m.B, new BuyItem(b.Stock.Single().Token, 1) { Tick = 0, SessionId = b.SessionId, InventoryRevision = b.StockRevision });
        m.World.ApplyPausedInterfaceCommands();
        Assert.Equal(tick, m.World.Tick);
        Assert.Single(m.Bazaar.Bag(m.CustomerA).Items);
        Assert.Empty(m.Bazaar.Bag(m.CustomerB).Items);
        Assert.NotEqual(a.StockRevision, m.View(m.A).StockRevision);
        Assert.Equal(m.View(m.A).StockRevision, m.View(m.B).StockRevision);
    }

    [Fact]
    public void A_trader_squad_gets_a_shop_and_the_player_buys_and_sells_at_its_prices()
    {
        using var b = new Bazaar();
        var hero = b.Spawn("hero", new Vector2(0, 0), b.Data.PlayerFaction, true);
        var shopkeeper = b.Spawn("shopkeeper", new Vector2(10, 0), b.Data.FactionIndex("f-t"), false, "shop-t");
        b.World.RunTicks(1);
        var shop = b.SquadOf(shopkeeper).Shop!;
        int units = shop.Stock.All().Where(i => i.Record != "cats-t").Sum(i => i.Quantity);
        Assert.InRange(units, 8, 12);                                                         // round(U(0.8, 1.2) x 10)
        Assert.Equal(500, shop.Stock.All().Where(i => i.Record == "cats-t").Sum(i => i.Quantity));   // the money roll, as money items
        Assert.Equal(3000, b.SquadOf(shopkeeper).Money);   // a small stock, its money items included, is worth less than 12000: the floor
        Assert.Equal(TickPhases.TradeCheck + (long)(24 / BodySystem.HoursPerTick), shop.NextRestockTick);

        b.Say(new OpenTrade(hero, shopkeeper) { Tick = 0 });
        b.World.RunTicks(2);
        var view = b.View;
        Assert.Equal(units, view.Stock.Sum(i => i.Quantity));   // the window leaves the money items out
        Assert.True(b.World.Snapshot.Characters.Single(c => c.Id == shopkeeper).IsTrader);

        // Buy one unit of the first stack: its unit price leaves the purse and enters the shop's.
        var first = view.Stock[0];
        b.Say(new BuyItem(first.Token, 1) { Tick = 0, Section = "main", X = 3, Y = 2 });   // dropped on a pocket cell
        b.World.RunTicks(1);
        Assert.Equal(1000 - first.UnitPrice, b.World.Player.Money);
        Assert.Equal(3000 + first.UnitPrice, b.SquadOf(shopkeeper).Money);
        Assert.Equal((1, 3, 2), b.Bag(hero).All().Select(i => (i.Quantity, i.X, i.Y)).Single());

        // Sell it back: the sell price (a trade item at full price, junk at half).
        var mine = b.View.Goods.Single();
        b.Say(new SellItem(mine.Token, 1) { Tick = 0, X = 11, Y = 17 });   // dropped on the stock's last row
        b.World.RunTicks(1);
        Assert.Equal(1000 - first.UnitPrice + mine.Price, b.World.Player.Money);
        Assert.Empty(b.Bag(hero).All());
        Assert.Contains(shop.Window.Items, i => (i.X, i.Y) == (11, 17));   // the window, a copy of the goods (Shop.Window)
        Assert.Equal(units, shop.Stock.All().Where(i => i.Record != "cats-t").Sum(i => i.Quantity));

        // Too poor: nothing moves.
        b.World.Player.Money = 0;
        b.Say(new BuyItem(b.View.Stock[0].Token, 1) { Tick = 0 });
        b.World.RunTicks(1);
        Assert.Contains("You can't afford that.", b.World.TakeMessages());
        Assert.Empty(b.Bag(hero).All());

        // Walking away (beyond TradeSettings.PassRange) leaves the trade open, as the original's; the stock is replaced when the refresh time comes.
        b.Say(new MoveOrder([hero], new Vector3(-1100, 150, 0)) { Tick = 0 });
        b.World.RunTicks(1500);
        Assert.NotNull(b.World.Snapshot.Trade);
        b.Say(new CloseTrade { Tick = 0 });
        b.World.RunTicks((int)(shop.NextRestockTick - b.World.Tick) + 15);
        Assert.Equal(2, shop.Fills);
    }

    /// <summary>
    /// A Caravan (docs/game/economy.md "Who trades": a homeless template with <c>vendors</c> but no <c>is trader</c>): its boss's Trade line opens a
    /// trade from an empty stock that buys with the squad's purse, and IS_A_TRADER stays false before and after. Catches the Trade line needing a
    /// shop again, and the shop it gets making the squad a trader.
    /// </summary>
    [Fact]
    public void A_caravan_without_is_trader_trades_from_an_empty_stock_and_stays_no_trader()
    {
        using var b = new Bazaar(
            Rec("caravan-t", FcsRecordType.SquadTemplate, r => r.References["vendors"] = [Ref("vl-t", 100)]),
            Rec("boss-t", FcsRecordType.Character,
                    r => { r.Name = "Boss"; r.Bools["is trader"] = true; r.References["dialogue"] = [Ref("hello-t")]; }),
            Rec("trader-c", FcsRecordType.DialogAction, r => r.Ints["condition name"] = 9),
            Rec("trade-a", FcsRecordType.DialogAction, r => r.Ints["action name"] = DialogueActions.Trade),
            Rec("hello-t", FcsRecordType.Dialogue, r => r.References["lines"] = [Ref("hello-l")]),
            Rec("hello-l", FcsRecordType.DialogueLine,
                    r => { r.Strings["text0"] = "Supplies?"; r.References["lines"] = [Ref("trade-l"), Ref("shop-l")]; }),
            Rec("trade-l", FcsRecordType.DialogueLine,
                    r => { r.Ints["speaker"] = (int)Talker.Target; r.Strings["text0"] = "Show me."; r.References["effects"] = [Ref("trade-a")]; }),
            Rec("shop-l", FcsRecordType.DialogueLine,
                    r => { r.Ints["speaker"] = (int)Talker.Target; r.Strings["text0"] = "A trader!"; r.References["conditions"] = [Ref("trader-c",
                            1)]; }));
        var hero = b.Spawn("hero", new Vector2(0, 0), b.Data.PlayerFaction, true);
        var boss = b.Spawn("Boss", new Vector2(10, 0), b.Data.FactionIndex("f-t"), false, "caravan-t");
        b.World.Characters.Cold(boss.Slot)!.RecordId = "boss-t";
        b.SquadOf(boss).Money = 500;
        Assert.True(b.Bag(hero).Add(new ItemInstance { Record = "cloth-t" }));

        IReadOnlyList<string> Talk()
        {
            b.Say(new TalkOrder(hero, boss) { Tick = 0 });
            b.World.RunTicks(2);
            return b.Dialogue.PublishConversation()!.Choices;
        }
        Assert.Equal(["Show me."], Talk());
        b.Say(new DialogueChoice(0) { Tick = 0 });
        b.World.RunTicks(3);
        Assert.Empty(b.View.Stock);
        Assert.True(b.World.Snapshot.Characters.Single(c => c.Id == boss).IsTrader);

        var mine = b.View.Goods.Single();
        b.Say(new SellItem(mine.Token, 1) { Tick = 0 });
        b.World.RunTicks(1);
        Assert.Equal(500 - mine.Price, b.SquadOf(boss).Money);
        Assert.Equal(1000 + mine.Price, b.World.Player.Money);
        Assert.Single(b.SquadOf(boss).Shop!.Stock.Items);

        b.Say(new CloseTrade { Tick = 0 });
        b.World.RunTicks(30);
        Assert.Equal(["Show me."], Talk());   // the shop it got does not make it a trader
        Assert.Single(b.SquadOf(boss).Shop!.Stock.Items);   // never filled
    }

    static FcsRecord BackpackRecord() => Rec("bag-t", FcsRecordType.Container, r =>
    {
        r.Ints["value"] = 100;
        r.Ints["inventory footprint width"] = 4;
        r.Ints["inventory footprint height"] = 4;
        r.Ints["storage size width"] = 6;
        r.Ints["storage size height"] = 8;
        r.Ints["slot"] = 12;   // ATTACH_BACKPACK, as every base-game bag
    });

    [Theory]
    [InlineData(null, true)]
    [InlineData("main", false)]
    public void A_backpack_trades_whole_and_only_when_empty(string? section, bool equip)
    {
        using var b = new Bazaar(BackpackRecord());
        var hero = b.Spawn("hero", Vector2.Zero, b.Data.PlayerFaction, true);
        var trader = b.Spawn("trader", new(10, 0), b.Data.FactionIndex("f-t"), false, "shop-t");
        b.World.RunTick();
        var shop = b.SquadOf(trader).Shop!;
        shop.Stock.Items.Clear();
        var bag = b.Data.Items.Create("bag-t")!;
        var contents = bag.Contents!;
        Assert.True(contents.Add(b.Data.Items.Create("ration-t")!));
        Assert.True(shop.Stock.Add(bag));
        b.Say(new OpenTrade(hero, trader) { Tick = 0 });
        b.World.RunTicks(2);
        long playerMoney = b.World.Player.Money, shopMoney = b.SquadOf(trader).Money;
        // Its contents are not loose stock, and a bag with something in it is not traded either way.
        var stock = Assert.Single(b.View.Stock);
        b.Say(new BuyItem(stock.Token, 1) { Tick = 0, Section = section, X = 2, Y = 3, Equip = equip });
        b.World.RunTick();
        Assert.Same(bag, Assert.Single(shop.Stock.Items));
        Assert.Equal(playerMoney, b.World.Player.Money);
        // The window trades a copy of the goods' bag (Shop.Window); emptied, the copy trades.
        var copy = Assert.Single(shop.Window.Items);
        Assert.NotSame(bag, copy);
        contents = copy.Contents!;
        contents.Items.Clear();
        b.World.RunTick();
        stock = Assert.Single(b.View.Stock);
        b.Say(new BuyItem(stock.Token, 1) { Tick = 0, Section = section, X = 2, Y = 3, Equip = equip });
        b.World.RunTick();
        Assert.Same(copy, Assert.Single(b.Bag(hero).Items));
        Assert.Same(contents, copy.Contents);
        Assert.Equal(section == "main" ? "main" : "backpack_attach", copy.Section);
        if (section == "main") Assert.Equal((2, 3), (copy.X, copy.Y));
        Assert.Empty(shop.Stock.All());   // the goods' bag left with it
        Assert.Equal(playerMoney - stock.Price, b.World.Player.Money);
        Assert.True(contents.Add(b.Data.Items.Create("ration-t")!));
        b.World.RunTick();
        b.Say(new SellItem(b.View.Goods.Single(i => i.Record == "bag-t").Token, 1) { Tick = 0 });
        b.World.RunTick();
        Assert.Same(copy, Assert.Single(b.Bag(hero).Items));
        Assert.Equal(playerMoney - stock.Price, b.World.Player.Money);
        // Emptied, it sells back for exactly what it cost (buyback).
        contents.Items.Clear();
        b.World.RunTick();
        var goods = b.View.Goods.Single(i => i.Record == "bag-t");
        Assert.True(goods.Buyback);
        b.Say(new SellItem(goods.Token, 1) { Tick = 0 });
        b.World.RunTick();
        Assert.Empty(b.Bag(hero).All());
        Assert.Same(copy, Assert.Single(shop.Window.Items));
        Assert.Same(contents, copy.Contents);
        Assert.Equal("bag-t", Assert.Single(shop.Stock.Items).Record);   // the goods take a copy of it
        Assert.Equal(playerMoney, b.World.Player.Money);
        Assert.Equal(shopMoney, b.SquadOf(trader).Money);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Buyback_refunds_exact_coins_in_both_directions_including_partial_stacks(bool buyFirst, bool close)
    {
        using var b = new Bazaar();
        var hero = b.Spawn("hero", Vector2.Zero, b.Data.PlayerFaction, true);
        var trader = b.Spawn("trader", new(10, 0), b.Data.FactionIndex("f-t"), false, "shop-t");
        b.World.RunTick();
        var shop = b.SquadOf(trader).Shop!;
        shop.Stock.Items.Clear();
        var item = b.Data.Items.Create("ration-t")!;
        item.Quantity = 4;
        item.Charges = 37; // Fractional charges and integer rounding must not create or lose coins on undo.
        Assert.True((buyFirst ? shop.Stock : b.Bag(hero)).Add(item));
        Assert.True((buyFirst ? b.Bag(hero) : shop.Stock).Add(b.Data.Items.Create("ration-t")!));
        b.Say(new OpenTrade(hero, trader) { Tick = 0 });
        b.World.RunTicks(2);
        long player = b.World.Player.Money, merchant = b.SquadOf(trader).Money;
        IReadOnlyList<TradeItemView> Source() => buyFirst ? b.View.Stock : b.View.Goods;
        IReadOnlyList<TradeItemView> Target() => buyFirst ? b.View.Goods : b.View.Stock;
        SimCommand Move(TradeItemView v, int n, bool undo) => buyFirst != undo ? new BuyItem(v.Token, n) { Tick = 0 }
                : new SellItem(v.Token, n) { Tick = 0 };
        // Moved one unit at a time, the units share one stack, kept apart from the plain one.
        for (int n = 0; n < 4; n++)
        {
            b.Say(Move(Assert.Single(Source()), 1, false));
            b.World.RunTick();
        }
        var reverse = Assert.Single(Target(), v => v.Buyback);
        Assert.Equal((4, Math.Abs(b.World.Player.Money - player)), (reverse.Quantity, reverse.Price));
        Assert.Equal(1, Assert.Single(Target(), v => !v.Buyback).Quantity);
        if (close)
        {
            b.Say(new CloseTrade { Tick = 0 });
            b.World.RunTick();
            Assert.Null(b.World.Snapshot.Trade);
            b.Say(new OpenTrade(hero, trader) { Tick = 0 });
            b.World.RunTicks(2);
            Assert.DoesNotContain(Target(), v => v.Buyback);
            return;
        }
        // Undone in parts, each part quotes what was actually paid.
        b.Say(Move(reverse, 1, true));
        b.World.RunTick();
        reverse = Assert.Single(Target(), v => v.Buyback);
        Assert.Equal(3, reverse.Quantity);
        b.Say(Move(reverse, 3, true));
        b.World.RunTick();
        Assert.Equal(player, b.World.Player.Money);
        Assert.Equal(merchant, b.SquadOf(trader).Money);
        Assert.DoesNotContain(b.View.Stock.Concat(b.View.Goods), v => v.Buyback);
    }

    /// <summary>
    /// A knocked-out player character trades through its window (docs/game/economy.md "Trading range"): it cannot open a trade itself, but a buy into
    /// its window passes the open trade to it, pays, and the trade stays with it; a dead one's window takes nothing. Catches the trade refusing
    /// or closing on an unconscious customer.
    /// </summary>
    [Fact]
    public void A_knocked_out_player_character_buys_into_its_window_during_an_open_trade()
    {
        using var b = new Bazaar();
        var hero = b.Spawn("hero", new Vector2(0, 0), b.Data.PlayerFaction, true);
        var mate = b.Spawn("mate", new Vector2(2, 0), b.Data.PlayerFaction, true);
        var fallen = b.Spawn("fallen", new Vector2(-2, 0), b.Data.PlayerFaction, true);
        var shopkeeper = b.Spawn("shopkeeper", new Vector2(10, 0), b.Data.FactionIndex("f-t"), false, "shop-t");
        b.World.RunTicks(1);
        b.World.Characters.Cold(mate.Slot)!.Medical!.KnockOut(600);
        b.World.Characters.Cold(fallen.Slot)!.Medical!.Kill();

        b.Say(new OpenTrade(mate, shopkeeper) { Tick = 0 });
        b.World.RunTicks(2);
        Assert.Null(b.World.Snapshot.Trade);

        b.World.Player.Selection.Add(hero);
        b.Say(new OpenTrade(hero, shopkeeper) { Tick = 0 });
        b.World.RunTicks(2);
        b.Say(new BuyItem(b.View.Stock[0].Token, 1) { Tick = 0, Into = fallen });
        b.World.RunTicks(1);
        Assert.Equal(hero, b.View.Customer);
        Assert.Equal(1000, b.World.Player.Money);
        Assert.Empty(b.Bag(fallen).All());

        // Its portrait selects it, then the stock is dropped into its window.
        b.World.Player.Selection.Clear();
        b.World.Player.Selection.Add(mate);
        var first = b.View.Stock[0];
        b.Say(new BuyItem(first.Token, 1) { Tick = 0, Into = mate });
        b.World.RunTicks(1);
        Assert.Equal(mate, b.View.Customer);
        Assert.Equal(1000 - first.UnitPrice, b.World.Player.Money);
        Assert.Equal(1, b.Bag(mate).All().Sum(i => i.Quantity));
        Assert.Empty(b.Bag(hero).All());

        b.World.RunTicks(30);
        Assert.Equal(mate, b.View.Customer);
    }

    [Fact]
    public void Stolen_goods_are_only_safe_with_a_fence()
    {
        Assert.Equal(0.4f, TradeSystem.FenceChance(0, false), 5);
        Assert.Equal(1.25f, TradeSystem.FenceChance(100, false), 5);
        Assert.Equal(0.2f, TradeSystem.FenceChance(0, true), 5);   // halved when it was stolen from the shop's own faction

        using var b = new Bazaar();
        var hero = b.Spawn("hero", new Vector2(0, 0), b.Data.PlayerFaction, true);
        var fence = b.Spawn("fence", new Vector2(10, 0), b.Data.FactionIndex("f-t"), false, "fence-t");
        var loot = b.Data.Items.Create("cloth-t", 2)!;
        loot.StolenFrom = b.Data.FactionIndex("f-t");
        b.Bag(hero).Add(loot);
        b.World.RunTicks(1);
        b.Say(new OpenTrade(hero, fence) { Tick = 0 });
        b.World.RunTicks(2);
        var goods = b.View.Goods.Single();
        Assert.True(goods.Stolen);
        b.Say(new SellItem(goods.Token, 2) { Tick = 0 });
        b.World.RunTicks(1);
        Assert.Equal(1000 + goods.Price, b.World.Player.Money);
        Assert.Empty(b.Bag(hero).All());
    }
}
