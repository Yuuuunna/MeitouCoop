using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Simulation;
using Meitou.Simulation.Player;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class InventoryActorTests
{
    sealed class Scene : IDisposable
    {
        public SimWorld World { get; }
        public WorldPlayer A { get; }
        public WorldPlayer B { get; }
        public CharacterId First { get; }
        public CharacterId Second { get; }
        public Inventory FirstBag => World.Characters.Cold(First.Slot)!.Inventory!;
        public Inventory SecondBag => World.Characters.Cold(Second.Slot)!.Inventory!;

        public Scene(params FcsRecord[] extra)
        {
            var tech = SyntheticTown.Rec("tech", FcsRecordType.Research, "Private research");
            var map = SyntheticTown.Rec("map", FcsRecordType.MapItem, "Town map");
            map.References["towns"] = [SyntheticTown.Ref("t1-t")];
            var data = TradeTests.Data([tech, map, .. extra]);
            int other = data.RegisterFaction("remote", "Remote", data.PlayerFaction);
            var production = new ProductionSystem(data.Db, data.Bodies.Constants, data.BodyOptions, data.Items, null);
            World = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300),
                [new PlayerSystem(data), production]);
            World.Player.Faction = data.PlayerFaction;
            A = World.Players.Register(new PlayerId(2)); B = World.Players.Register(new PlayerId(3));
            World.Players.SetMembership(A.Id, data.PlayerFaction, FactionPermission.All);
            World.Players.SetMembership(B.Id, other, FactionPermission.All);
            First = Spawn(A.State.Faction); Second = Spawn(B.State.Faction);
            World.Players.AssignInitial(A.Id, First); World.Players.AssignInitial(B.Id, Second);
        }

        public CharacterId Spawn(int faction, Vector3? at = null) => World.Characters.Spawn(new CharacterHot { Position = at ?? new Vector3(0, 300, 0) },
            new CharacterCold { Faction = faction, IsPlayer = true, Inventory = new Inventory(), Medical = Bodies.BodyFixtures.Healthy() }, 0);

        public InventoryItemToken Token(Inventory inventory, ItemInstance item) =>
            new(inventory.All().ToList().IndexOf(item), item.Record, item.Section, item.X, item.Y, item.Quantity, item.Quality);
        public TransferInventoryItem Transfer(CharacterId from, CharacterId to, ItemInstance item, int count = 1, string section = "main", int x = 0, int y = 0, bool swap = false)
        {
            var source = World.Characters.Cold(from.Slot)!.Inventory!;
            var target = World.Characters.Cold(to.Slot)!.Inventory!;
            return new(from, to, Token(source, item), count, section, x, y, swap)
                { Tick = World.Tick, InventoryRevision = World.InventoryRevision(source), TargetRevision = World.InventoryRevision(target) };
        }
        public void ControlBoth()
        {
            World.Players.SetMembership(A.Id, B.State.Faction, FactionPermission.All);
            Assert.True(World.Players.TryTransfer(B.Id, World.Players.ControlOf(Second)!.Value, A.Id, out _));
        }
        public void Send(PlayerId player, SimCommand command) => World.Commands.Enqueue(player, command with { Tick = World.Tick });
        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void CashUsesTheControlledCharactersFactionAndOverflowKeepsTheItem()
    {
        using var s = new Scene();
        int faction = s.A.State.Faction;
        var cash = new ItemInstance { Record = "cats-t", Type = FcsRecordType.Item, ItemFunction = PlayerSystem.MoneyFunction, Quantity = 20, StackLimit = 100 };
        Assert.True(s.FirstBag.Add(cash));
        s.A.State.Money = 10; s.B.State.Money = 100;
        var token = s.Token(s.FirstBag, cash);
        ulong revision = s.World.InventoryRevision(s.FirstBag);
        s.Send(s.B.Id, new QuickMoveItem(s.First, token, false, false) { Tick = 0, InventoryRevision = revision });
        s.World.RunTick();
        Assert.Contains(cash, s.FirstBag.Items);
        s.World.Players.SetMembership(s.A.Id, s.B.State.Faction, FactionPermission.None);
        Assert.True(s.World.Players.SelectFaction(s.A.Id, s.B.State.Faction));
        s.Send(s.A.Id, new QuickMoveItem(s.First, token, false, false) { Tick = 0, InventoryRevision = revision });
        s.World.RunTick();
        Assert.Empty(s.FirstBag.Items);
        Assert.Equal(30, s.World.Factions.Find(faction)!.Money);
        Assert.Equal(100, s.B.State.Money);
        Assert.Contains("20 cats added.", s.World.TakeMessages(s.A.Id));
        Assert.Empty(s.World.TakeMessages());

        s.World.Factions.Find(faction)!.Money = long.MaxValue;
        Assert.True(s.FirstBag.Add(cash));
        s.Send(s.A.Id, new QuickMoveItem(s.First, s.Token(s.FirstBag, cash), false, false)
            { Tick = 0, InventoryRevision = s.World.InventoryRevision(s.FirstBag) });
        s.World.RunTick();
        Assert.Contains(cash, s.FirstBag.Items);
        Assert.Equal(long.MaxValue, s.World.Factions.Find(faction)!.Money);
    }

    [Fact]
    public void BlueprintAndMapAffectOnlyTheirReaderAndFaction()
    {
        using var s = new Scene();
        var blueprint = new ItemInstance { Record = "blueprint-look", Type = FcsRecordType.Blueprint, CompanyId = "tech" };
        var map = new ItemInstance { Record = "map", Type = FcsRecordType.MapItem };
        Assert.True(s.FirstBag.Add(blueprint)); Assert.True(s.FirstBag.Add(map));
        s.Send(s.A.Id, new QuickMoveItem(s.First, s.Token(s.FirstBag, blueprint), false, false)
            { Tick = 0, InventoryRevision = s.World.InventoryRevision(s.FirstBag) });
        s.World.RunTick();
        Assert.True(s.A.State.FactionState.Research.IsDone("tech"));
        Assert.False(s.B.State.FactionState.Research.IsDone("tech"));
        Assert.DoesNotContain(blueprint, s.FirstBag.Items);
        Assert.Contains(s.World.TakeMessages(s.A.Id), message => message.Contains("Research complete", StringComparison.Ordinal));
        Assert.Empty(s.World.TakeMessages());
        s.Send(s.A.Id, new ReadMapItem(s.First, s.Token(s.FirstBag, map))
            { Tick = 0, InventoryRevision = s.World.InventoryRevision(s.FirstBag) });
        s.World.RunTick();
        Assert.Contains(s.A.State.Towns, knowledge => (knowledge & TownKnowledge.Discovered) != 0);
        Assert.Empty(s.B.State.Towns);
        Assert.Empty(s.World.Player.Towns);
        Assert.DoesNotContain(map, s.FirstBag.Items);
    }

    [Fact]
    public void DragCommitRequiresBothRolesAndRejectsRevokedGrantsAndDistantTargets()
    {
        using var s = new Scene();
        var item = new ItemInstance { Record = "ore" };
        s.FirstBag.Items.Add(item);
        s.Send(s.A.Id, s.Transfer(s.First, s.Second, item));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Same(item, Assert.Single(s.FirstBag.Items)); Assert.Empty(s.SecondBag.Items);
        s.ControlBoth();
        s.Send(s.A.Id, s.Transfer(s.First, s.Second, item));
        var grant = s.World.Players.ControlOf(s.Second)!.Value;
        Assert.True(s.World.Players.TryTransfer(s.A.Id, grant, s.B.Id, out var returned));
        Assert.True(s.World.Players.TryTransfer(s.B.Id, returned, s.A.Id, out _));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Same(item, Assert.Single(s.FirstBag.Items)); Assert.Empty(s.SecondBag.Items);
        var distant = s.Spawn(s.A.State.Faction, new Vector3(2000, 300, 0));
        s.World.Players.AssignInitial(s.A.Id, distant);
        s.Send(s.A.Id, s.Transfer(s.First, distant, item));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Same(item, Assert.Single(s.FirstBag.Items)); Assert.Empty(s.SecondBag.Items);
        s.Send(s.A.Id, s.Transfer(s.First, s.Second, item));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Empty(s.FirstBag.Items); Assert.Same(item, Assert.Single(s.SecondBag.Items));
        Assert.Null(s.A.State.Held); Assert.Null(s.B.State.Held);
    }

    [Fact]
    public void DragCommitRejectsStaleDestinationAndRepeatedSplitWithoutLosingCharges()
    {
        using var s = new Scene();
        s.ControlBoth();
        var item = new ItemInstance { Record = "food", Type = FcsRecordType.Item, Quantity = 5, StackLimit = 10, Charges = 30, MaxCharges = 100 };
        s.FirstBag.Items.Add(item);
        var stale = s.Transfer(s.First, s.Second, item, 2);
        s.SecondBag.Items.Add(new ItemInstance { Record = "arrived", X = 5 });
        s.Send(s.A.Id, stale);
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(5, item.Quantity);
        var split = s.Transfer(s.First, s.Second, item, 2);
        s.Send(s.A.Id, split); s.Send(s.A.Id, split);
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(3, item.Quantity); Assert.Equal(30, item.Charges);
        var moved = s.SecondBag.Items.Single(i => i.Record == "food");
        Assert.Equal(2, moved.Quantity); Assert.Equal(100, moved.Charges);
        s.Send(s.A.Id, s.Transfer(s.First, s.Second, item, 3));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Empty(s.FirstBag.Items);
        Assert.Equal(5, moved.Quantity); Assert.Equal(30, moved.Charges);
    }

    [Fact]
    public void SwapRefusalLeavesBothInventoriesUntouchedAndSuccessfulSwapIsAtomic()
    {
        using var s = new Scene();
        s.ControlBoth();
        var item = new ItemInstance { Record = "small" };
        var other = new ItemInstance { Record = "large", Width = 2 };
        var blocker = new ItemInstance { Record = "blocker", X = 1 };
        s.FirstBag.Items.AddRange([item, blocker]); s.SecondBag.Items.Add(other);
        ulong before = s.World.InventoryRevision(s.FirstBag), targetBefore = s.World.InventoryRevision(s.SecondBag);
        s.Send(s.A.Id, s.Transfer(s.First, s.Second, item, swap: true));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(before, s.World.InventoryRevision(s.FirstBag)); Assert.Equal(targetBefore, s.World.InventoryRevision(s.SecondBag));
        Assert.Equal(new[] { item, blocker }, s.FirstBag.Items); Assert.Same(other, Assert.Single(s.SecondBag.Items));
        blocker.X = 3;
        s.Send(s.A.Id, s.Transfer(s.First, s.Second, item, swap: true));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Contains(other, s.FirstBag.Items); Assert.DoesNotContain(item, s.FirstBag.Items);
        Assert.Same(item, Assert.Single(s.SecondBag.Items));
        Assert.Equal(0, other.X); Assert.Null(s.A.State.Held);
    }

    [Fact]
    public void DragPreservesLockedEquipmentAndChecksTheWearersDisabledSlots()
    {
        using var s = new Scene();
        var cold = s.World.Characters.Cold(s.First.Slot)!;
        cold.Race = new Meitou.Data.Gameplay.Bodies.RaceData { StringId = "no-hats", NoHats = true };
        var hat = new ItemInstance { Record = "hat", Type = FcsRecordType.Armour, EquipmentSection = "head" };
        var shackles = new ItemInstance { Record = "shackles", Type = FcsRecordType.Armour, EquipmentSection = "boots", Section = "boots",
            Lock = new Meitou.Simulation.Locks.LockState { Locked = true, Level = 10 } };
        s.FirstBag.Items.AddRange([hat, shackles]);
        s.Send(s.A.Id, s.Transfer(s.First, s.First, hat, section: "head"));
        s.Send(s.A.Id, s.Transfer(s.First, s.First, shackles, x: 2));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal("main", hat.Section); Assert.Equal("boots", shackles.Section);
        Assert.True(shackles.LockedOn); Assert.Equal(2, s.FirstBag.Items.Count);
    }

    [Fact]
    public void ProstheticDragUpdatesTheBodyOnlyWhenTheInventoryCommitSucceeds()
    {
        var record = SyntheticTown.Rec("left-arm", FcsRecordType.LimbReplacement, "Left arm");
        record.Ints["slot"] = 50;
        using var s = new Scene(record);
        var body = s.World.Characters.Cold(s.First.Slot)!.Medical!;
        var part = body.LimbPart(0)!;
        part.Limb = Meitou.Simulation.Bodies.LimbState.Stump;
        var arm = new ItemInstance { Record = "left-arm", Type = FcsRecordType.LimbReplacement, LimbDamage = 12, LimbWear = 7, LimbStun = 3 };
        s.FirstBag.Items.Add(arm);
        s.Send(s.A.Id, s.Transfer(s.First, s.First, arm, section: "right_arm_replace"));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(Meitou.Simulation.Bodies.LimbState.Stump, part.Limb); Assert.Equal("main", arm.Section);
        s.Send(s.A.Id, s.Transfer(s.First, s.First, arm, section: "left_arm_replace"));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(Meitou.Simulation.Bodies.LimbState.Replaced, part.Limb); Assert.Same(arm, part.ReplacementItem);
        Assert.Equal(12, part.MaxHp - part.Flesh); Assert.Equal(7, part.Wear); Assert.Equal(3, part.Stun);
        var blocker = new ItemInstance { Record = "blocked" };
        s.FirstBag.Items.Add(blocker);
        s.Send(s.A.Id, s.Transfer(s.First, s.First, arm));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(Meitou.Simulation.Bodies.LimbState.Replaced, part.Limb); Assert.Same(arm, part.ReplacementItem);
        s.Send(s.A.Id, s.Transfer(s.First, s.First, arm, x: 2));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(Meitou.Simulation.Bodies.LimbState.Stump, part.Limb); Assert.Null(part.ReplacementItem);
        Assert.Equal("main", arm.Section); Assert.Equal(12, arm.LimbDamage); Assert.Equal(7, arm.LimbWear); Assert.Equal(3, arm.LimbStun);
    }

    [Fact]
    public void DragCannotCreateContainerCyclesOrOverflowAStack()
    {
        using var s = new Scene();
        var contents = new Inventory();
        var bag = new ItemInstance { Record = "bag", Type = FcsRecordType.Container, Section = "backpack_attach", Contents = contents };
        s.FirstBag.Items.Add(bag);
        s.Send(s.A.Id, s.Transfer(s.First, s.First, bag, section: "backpack_content"));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Same(bag, Assert.Single(s.FirstBag.Items)); Assert.Empty(contents.Items);
        var source = new ItemInstance { Record = "ore", Type = FcsRecordType.Item, Quantity = 3, StackLimit = 5 };
        var target = new ItemInstance { Record = "ore", Type = FcsRecordType.Item, Quantity = 4, StackLimit = 5, X = 2 };
        s.FirstBag.Items.AddRange([source, target]);
        s.Send(s.A.Id, s.Transfer(s.First, s.First, source, 2, x: 2));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(3, source.Quantity); Assert.Equal(4, target.Quantity);
        s.Send(s.A.Id, s.Transfer(s.First, s.First, source, x: 2));
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(2, source.Quantity); Assert.Equal(5, target.Quantity);
    }

    [Fact]
    public void InventoryAndStanceChangesRejectForeignRolesAndStaleRevisions()
    {
        using var s = new Scene();
        var a = new ItemInstance { Record = "a", X = 3 };
        var b = new ItemInstance { Record = "b", X = 4 };
        s.FirstBag.Items.Add(a); s.SecondBag.Items.Add(b);
        ulong before = s.World.InventoryRevision(s.FirstBag);
        a.X = 5;
        s.Send(s.A.Id, new ArrangeInventory(s.First) { Tick = 0, InventoryRevision = before });
        s.Send(s.A.Id, new ArrangeInventory(s.Second) { Tick = 0, InventoryRevision = s.World.InventoryRevision(s.SecondBag) });
        s.Send(s.A.Id, new OrdersCommand([s.First, s.Second], CharacterOrders.Hold, true) { Tick = 0 });
        s.Send(s.A.Id, new OrdersCommand([s.First], (CharacterOrders)ushort.MaxValue, true) { Tick = 0 });
        s.World.RunTick();
        Assert.Equal(5, a.X); Assert.Equal(4, b.X);
        Assert.False(Orders.Has(s.World.Characters.Cold(s.First.Slot)!, CharacterOrders.Hold));
        Assert.False(Orders.Has(s.World.Characters.Cold(s.Second.Slot)!, CharacterOrders.Hold));
        s.Send(s.A.Id, new ArrangeInventory(s.First) { Tick = 0, InventoryRevision = s.World.InventoryRevision(s.FirstBag) });
        s.Send(s.A.Id, new OrdersCommand([s.First], CharacterOrders.Hold, true) { Tick = 0 });
        s.World.RunTick();
        Assert.Equal(0, a.X); Assert.Equal(4, b.X);
        Assert.True(Orders.Has(s.World.Characters.Cold(s.First.Slot)!, CharacterOrders.Hold));
        Assert.False(Orders.Has(s.World.Characters.Cold(s.Second.Slot)!, CharacterOrders.Hold));
    }
}
