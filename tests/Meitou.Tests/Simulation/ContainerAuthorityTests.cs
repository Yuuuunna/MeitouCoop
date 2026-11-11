using Meitou.Simulation.Player;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class ContainerAuthorityTests
{
    sealed class Scene : IDisposable
    {
        public PopulationData Data { get; }
        public SimWorld World { get; }
        public ProductionSystem Production { get; }
        public WorldPlayer A { get; }
        public WorldPlayer B { get; }
        public CharacterId First { get; }
        public CharacterId Second { get; }
        public Fixture Box => World.Fixtures.Find("box")!;
        public Fixture OtherBox => World.Fixtures.Find("other-box")!;
        public Inventory Bag(CharacterId id) => World.Characters.Cold(id.Slot)!.Inventory!;
        public Scene()
        {
            var chest = SyntheticTown.Rec("chest", FcsRecordType.Building, "Chest");
            chest.Bools["has lock"] = true; chest.Bools["has inventory"] = true;
            var house = SyntheticTown.Rec("house", FcsRecordType.Building, "House");
            var function = SyntheticTown.Rec("store-fn", FcsRecordType.BuildingFunctionality, "Storage");
            function.Ints["function"] = TownBuildings.GeneralStorageFunction;
            var store = SyntheticTown.Rec("store", FcsRecordType.Building, "Storage");
            store.Bools["has inventory"] = true;
            store.References["functionality"] = [SyntheticTown.Ref("store-fn")];
            var ore = SyntheticTown.Rec("ore", FcsRecordType.Item, "Ore"); ore.Ints["stackable"] = 10;
            var db = SyntheticTown.Database(extra: [chest, house, store, function, ore]);
            var at = SyntheticTown.Centre;
            var building = new TownBuilding("home", "house", "House", at, 0, true, false, [])
            {
                Zone = WorldLayout.ZoneOf(at.X, at.Z), CanHouse = true, Furnished = true,
                Fixtures = [new TownFixture("box", "chest", false, at, 0) { Function = TownBuildings.GeneralStorageFunction, StorageWidth = 4, StorageHeight = 4 },
                    new TownFixture("other-box", "chest", false, at + new Vector3(5, 0, 0), 0) { Function = TownBuildings.GeneralStorageFunction, StorageWidth = 4, StorageHeight = 4 }],
            };
            Data = PopulationData.Create(db, [new TownPlacement("inst", "30-t", at, null)],
                buildings: new Dictionary<string, List<TownBuilding>> { ["inst"] = [building] });
            int remote = Data.RegisterFaction("remote", "Remote", Data.PlayerFaction);
            Production = new ProductionSystem(db, Data.Bodies.Constants, Data.BodyOptions, Data.Items, null);
            World = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => at.Y), [new PlayerSystem(Data), Production]);
            World.Fixtures.RegisterSite(World, Data, 0);
            Box.Owner = OtherBox.Owner = -1;
            A = World.Players.Register(new PlayerId(2)); B = World.Players.Register(new PlayerId(3));
            World.Players.SetMembership(A.Id, Data.PlayerFaction, FactionPermission.All);
            World.Players.SetMembership(B.Id, remote, FactionPermission.All);
            First = Spawn(A); Second = Spawn(B);
        }
        public CharacterId Spawn(WorldPlayer player, Vector3? at = null)
        {
            var id = World.Characters.Spawn(new CharacterHot { Position = at ?? SyntheticTown.Centre },
                new CharacterCold { Faction = player.State.Faction, IsPlayer = true, Inventory = new Inventory(), Medical = Bodies.BodyFixtures.Healthy() }, 0);
            World.Players.AssignInitial(player.Id, id); return id;
        }
        public void Send(WorldPlayer player, SimCommand command) => World.Commands.Enqueue(player.Id, command with { Tick = World.Tick });
        public void Apply() => World.ApplyPausedInterfaceCommands();
        public void Open(WorldPlayer player, CharacterId actor, Fixture? fixture = null)
        { Send(player, new OpenContainerOrder([actor], (fixture ?? Box).Id) { Tick = World.Tick }); Apply(); }
        public TakeFromMachine Take(WorldPlayer player, CharacterId actor, Fixture? fixture = null) =>
            new(actor, (fixture ?? Box).Id, -1) { Tick = World.Tick, InventoryRevision = World.InventoryRevision(Bag(actor)),
                StorageRevision = World.InventoryRevision((fixture ?? Box).Contents!), ContainerSessionId = player.State.ContainerSessionId };
        public Machine AddStorage(int faction)
        {
            var site = new BuildSite { Id = World.Buildings.NewId(WorldLayout.ZoneOf(SyntheticTown.Centre.X, SyntheticTown.Centre.Z)),
                Record = "store", Owner = faction, Position = SyntheticTown.Centre, Complete = true, Materials = [] };
            World.Buildings.Add(site); return Production.Ensure(World, site)!;
        }
        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void ContainerSessionsAndCloseCommandsBelongToOnePlayerAndOneOpening()
    {
        using var s = new Scene();
        s.Open(s.A, s.First); s.Open(s.B, s.Second, s.OtherBox);
        Assert.Equal("box", s.World.PublishContainer(s.A.Id)!.Site);
        Assert.Equal("other-box", s.World.PublishContainer(s.B.Id)!.Site);
        Assert.Null(s.World.PublishContainer(PlayerId.Local));
        ulong old = s.A.State.ContainerSessionId, b = s.B.State.ContainerSessionId;
        s.Open(s.A, s.First);
        s.Send(s.A, new CloseContainer { Tick = 0, SessionId = old });
        s.Send(s.A, new CloseContainer { Tick = 0, SessionId = b }); s.Apply();
        Assert.NotNull(s.World.PublishContainer(s.A.Id)); Assert.NotNull(s.World.PublishContainer(s.B.Id));
        s.Send(s.B, new CloseContainer { Tick = 0, SessionId = b }); s.Apply();
        Assert.Null(s.World.PublishContainer(s.B.Id)); Assert.NotNull(s.World.PublishContainer(s.A.Id));
    }

    [Fact]
    public void TwoOpenersCompeteForOneInventoryAndIdenticalReplacementInvalidatesTheRequest()
    {
        using var s = new Scene();
        var item = new ItemInstance { Record = "ore" };
        s.Box.Contents!.Items.Add(item);
        s.Open(s.A, s.First); s.Open(s.B, s.Second);
        s.Send(s.A, s.Take(s.A, s.First)); s.Send(s.B, s.Take(s.B, s.Second)); s.Apply();
        Assert.Empty(s.Box.Contents.Items); Assert.Same(item, Assert.Single(s.Bag(s.First).Items)); Assert.Empty(s.Bag(s.Second).Items);
        s.Box.Contents.Items.Add(new ItemInstance { Record = "ore" });
        var stale = s.Take(s.B, s.Second);
        s.Box.Contents.Items.Clear(); s.Box.Contents.Items.Add(new ItemInstance { Record = "ore" });
        s.Send(s.B, stale); s.Apply();
        Assert.Single(s.Box.Contents.Items); Assert.Empty(s.Bag(s.Second).Items);
    }

    [Fact]
    public void MembershipDoesNotOpenOwnedAssetsAndPermissionRevocationClosesTheSession()
    {
        using var s = new Scene();
        s.Box.Owner = s.A.State.Faction;
        s.World.Players.SetMembership(s.B.Id, s.Box.Owner, FactionPermission.None);
        s.Open(s.B, s.Second);
        Assert.Null(s.World.PublishContainer(s.B.Id));
        s.World.Players.SetMembership(s.B.Id, s.Box.Owner, FactionPermission.ManageAssets);
        s.Open(s.B, s.Second); Assert.NotNull(s.World.PublishContainer(s.B.Id));
        s.Box.Contents!.Items.Add(new ItemInstance { Record = "ore" });
        var request = s.Take(s.B, s.Second);
        s.World.Players.SetMembership(s.B.Id, s.Box.Owner, FactionPermission.None);
        s.World.Players.SetMembership(s.B.Id, s.Box.Owner, FactionPermission.ManageAssets);
        s.Send(s.B, request); s.Apply();
        Assert.Empty(s.Bag(s.Second).Items); Assert.Null(s.World.PublishContainer(s.B.Id));
        s.Open(s.B, s.Second); s.Send(s.B, s.Take(s.B, s.Second)); s.Apply();
        Assert.Equal(-1, Assert.Single(s.Bag(s.Second).Items).StolenFrom); // delegated access is not theft by the role's other faction
    }

    [Fact]
    public void LockedContainersForeignRolesAndMissingSessionsCannotBeBypassed()
    {
        using var s = new Scene();
        s.Box.Contents!.Items.Add(new ItemInstance { Record = "ore" });
        s.Send(s.A, s.Take(s.A, s.First)); s.Apply(); Assert.Empty(s.Bag(s.First).Items);
        s.Open(s.A, s.Second); Assert.Null(s.World.PublishContainer(s.A.Id));
        s.Open(s.A, s.First);
        s.Box.Lock!.Level = 10; s.Box.Lock.Locked = true;
        s.Send(s.A, s.Take(s.A, s.First)); s.Apply();
        Assert.Empty(s.Bag(s.First).Items); Assert.Single(s.Box.Contents.Items); Assert.Null(s.World.PublishContainer(s.A.Id));
    }

    [Fact]
    public void MachineStorageRequiresPermissionAndARevisionBoundToTheInstance()
    {
        using var s = new Scene();
        var machine = s.AddStorage(s.A.State.Faction);
        machine.Storage!.Items.Add(new ItemInstance { Record = "ore" });
        TakeFromMachine Take(WorldPlayer player, CharacterId actor) => new(actor, machine.Site, -1)
            { Tick = s.World.Tick, InventoryRevision = s.World.InventoryRevision(s.Bag(actor)), StorageRevision = s.World.MachineRevision(machine) };
        s.Send(s.B, Take(s.B, s.Second)); s.Apply(); Assert.Empty(s.Bag(s.Second).Items);
        var old = Take(s.A, s.First);
        var copy = new Machine { Site = machine.Site, Data = machine.Data, Position = machine.Position, Inputs = [], Storage = new Inventory() };
        copy.Storage.Items.Add(new ItemInstance { Record = "ore" });
        s.World.Machines.Replace(copy);
        s.Send(s.A, old); s.World.Commands.Enqueue(old); s.Apply(); Assert.Empty(s.Bag(s.First).Items); Assert.Single(copy.Storage.Items);
        machine = copy;
        s.Send(s.A, Take(s.A, s.First)); s.Apply(); Assert.Single(s.Bag(s.First).Items); Assert.Empty(machine.Storage.Items);
    }

    [Fact]
    public void LocalUiAlsoHonorsTheSessionIdItDisplays()
    {
        using var s = new Scene();
        s.World.Commands.Enqueue(new OpenContainerOrder([s.First], "box") { Tick = 0 }); s.Apply();
        ulong old = s.World.Player.ContainerSessionId;
        Assert.NotEqual(0UL, old);
        s.World.Commands.Enqueue(new OpenContainerOrder([s.First], "box") { Tick = 0 }); s.Apply();
        s.World.Commands.Enqueue(new CloseContainer { Tick = 0, SessionId = old }); s.Apply();
        Assert.Equal("box", s.World.Player.OpenContainer);
        s.World.Commands.Enqueue(new CloseContainer { Tick = 0, SessionId = s.World.Player.ContainerSessionId }); s.Apply();
        Assert.Null(s.World.Player.OpenContainer);
    }

    [Fact]
    public void ApproachingAndOpenSessionsLoseAccessWhenTheirRoleIsTransferredOrTheContainerIsRemoved()
    {
        using var s = new Scene();
        var upstairs = s.Spawn(s.A, SyntheticTown.Centre + new Vector3(0, 100, 0));
        s.Open(s.A, upstairs);
        Assert.Null(s.World.PublishContainer(s.A.Id));
        Assert.Equal("box", s.A.State.OpeningContainer);
        s.World.Players.SetMembership(s.B.Id, s.A.State.Faction, FactionPermission.None);
        Assert.True(s.World.Players.TryTransfer(s.A.Id, s.World.Players.ControlOf(upstairs)!.Value, s.B.Id, out _));
        s.World.RunTick();
        Assert.Null(s.A.State.OpeningContainer); Assert.Equal(0UL, s.A.State.ContainerSessionId);
        s.Open(s.A, s.First);
        Assert.NotNull(s.World.PublishContainer(s.A.Id));
        s.World.Fixtures.Unregister(s.Box.Building);
        Assert.Null(s.World.PublishContainer(s.A.Id));
        s.World.RunTick(); Assert.Null(s.A.State.OpenContainer);
    }

    [Fact]
    public void MachineSlotRequestsSurviveProgressButRejectChangedCountsAndConsumedAuthority()
    {
        using var s = new Scene();
        var baseMachine = s.AddStorage(s.A.State.Faction);
        var machine = new Machine { Site = baseMachine.Site, Data = baseMachine.Data, Position = baseMachine.Position,
            Inputs = [new InputSlot(new RecipeInput("ore", 10, 1))], Output = new OutputSlot(new RecipeOutput("ore", 10)) { Count = 2 } };
        s.World.Machines.Replace(machine);
        var request = new TakeFromMachine(s.First, machine.Site, -2, Count: 1)
        { Tick = s.World.Tick, InventoryRevision = s.World.InventoryRevision(s.Bag(s.First)), StorageRevision = s.World.MachineRevision(machine) };
        machine.Output.Progress = 0.8f; machine.Inputs[0].Level = 0.7f;
        Assert.Equal(request.StorageRevision, s.World.MachineRevision(machine));
        s.Send(s.A, request); s.Send(s.A, request); s.Apply();
        Assert.Equal(1, machine.Output.Count); Assert.Equal(1, Assert.Single(s.Bag(s.First).Items).Quantity);
        var stale = request with { InventoryRevision = s.World.InventoryRevision(s.Bag(s.First)), StorageRevision = s.World.MachineRevision(machine) };
        machine.Output.Count = 3;
        s.Send(s.A, stale); s.Apply(); Assert.Equal(3, machine.Output.Count);
        var ready = stale with { StorageRevision = s.World.MachineRevision(machine) };
        s.Send(s.A, ready);
        s.World.Players.SetMembership(s.A.Id, s.A.State.Faction, FactionPermission.None);
        s.World.Players.SetMembership(s.A.Id, s.A.State.Faction, FactionPermission.All);
        s.Apply(); Assert.Equal(3, machine.Output.Count);
    }

    [Fact]
    public void RightClickSplitKeepsThePartlyUsedUnitAtItsSource()
    {
        using var s = new Scene();
        s.Open(s.A, s.First);
        var food = new ItemInstance { Record = "ore", Type = FcsRecordType.Item, Quantity = 5, StackLimit = 10, Charges = 20, MaxCharges = 100 };
        s.Box.Contents!.Items.Add(food);
        s.Send(s.A, s.Take(s.A, s.First) with { Count = 1 }); s.Apply();
        Assert.Equal((4, 20f), (food.Quantity, food.Charges));
        var taken = Assert.Single(s.Bag(s.First).Items);
        Assert.Equal((1, 100f), (taken.Quantity, taken.Charges));
        s.Bag(s.First).Items.Clear();
        var carried = new ItemInstance { Record = "other", Type = FcsRecordType.Item, Quantity = 5, StackLimit = 10, Charges = 30, MaxCharges = 100 };
        s.Bag(s.First).Items.Add(carried);
        s.Send(s.A, new PutInMachine(s.First, new(0, "other", "main", 0, 0, 5, 0), "box", -1, X: -1, Count: 2)
        { Tick = 0, InventoryRevision = s.World.InventoryRevision(s.Bag(s.First)), StorageRevision = s.World.InventoryRevision(s.Box.Contents), ContainerSessionId = s.A.State.ContainerSessionId });
        s.Apply();
        Assert.Equal((3, 30f), (carried.Quantity, carried.Charges));
        var put = s.Box.Contents.Items.Single(i => i.Record == "other");
        Assert.Equal((2, 100f), (put.Quantity, put.Charges));
    }

    [Fact]
    public void PositionedPutMovesOnlyTheRequestedUnitsAndReplayCannotRepeatIt()
    {
        using var s = new Scene();
        s.Open(s.A, s.First);
        var item = new ItemInstance { Record = "ore", Quantity = 5, StackLimit = 10 };
        s.Bag(s.First).Items.Add(item);
        var command = new PutInMachine(s.First, new(0, "ore", "main", 0, 0, 5, 0), "box", -1, 1, 1, 2)
        { Tick = s.World.Tick, InventoryRevision = s.World.InventoryRevision(s.Bag(s.First)), StorageRevision = s.World.InventoryRevision(s.Box.Contents!), ContainerSessionId = s.A.State.ContainerSessionId };
        s.Send(s.A, command); s.Send(s.A, command); s.Apply();
        Assert.Equal(3, item.Quantity);
        var placed = Assert.Single(s.Box.Contents!.Items);
        Assert.Equal((2, 1, 1), (placed.Quantity, placed.X, placed.Y)); Assert.Null(s.A.State.Held);
    }
}
