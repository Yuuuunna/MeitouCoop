using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class SquadAuthorityTests
{
    [Fact]
    public void FactionManagementPermissionDoesNotClaimNpcSquads()
    {
        using var s = new Scene();
        var npc = new Squad { Id = s.World.Squads.NextId(), TemplateId = "npc", Name = "NPC squad", Faction = s.A.State.Faction, Town = 0 };
        s.World.Squads.Add(npc);
        s.Send(s.A, new RenameSquadCommand(npc.Id, "claimed") { Tick = 0, Faction = npc.Faction });
        s.Send(s.A, new ActiveSquadCommand(npc.Id) { Tick = 0, Faction = npc.Faction });
        s.Send(s.A, new RemoveSquadCommand(npc.Id) { Tick = 0, Faction = npc.Faction });
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal("NPC squad", npc.Name);
        Assert.Same(npc, s.World.Squads.Find(npc.Id));
        Assert.Equal(s.SquadA.Id, s.A.State.Squad);
        Assert.DoesNotContain(npc, SquadManagement.PlayerSquads(s.World, npc.Faction));
    }
    sealed class Scene : IDisposable
    {
        readonly MovementSystem _movement;
        public PopulationData Data { get; }
        public SimWorld World { get; }
        public WorldPlayer A { get; }
        public WorldPlayer B { get; }
        public Squad SquadA { get; }
        public Squad SquadB { get; }
        public CharacterId First { get; }
        public CharacterId Second { get; }
        public CharacterId Other { get; }

        public Scene(bool sameFaction = false)
        {
            var hunters = SyntheticTown.Rec(SquadManagement.DismissFaction, FcsRecordType.Faction, "Hunters");
            var limits = SyntheticTown.Rec("limits", FcsRecordType.Constants, "Limits");
            limits.Ints["max squad size"] = 2;
            limits.Ints["max squads"] = 8;
            Data = SyntheticTown.Data(SyntheticTown.Database(extra: [hunters, limits]));
            int other = sameFaction ? Data.PlayerFaction : Data.RegisterFaction("remote", "Remote", Data.PlayerFaction);
            var walk = new OpenGroundWalkability((_, _) => 300);
            _movement = new MovementSystem(new PathService(walk, true));
            World = new SimWorld(new WorldSettings { PublishSnapshots = false }, walk, [new PlayerSystem(Data), _movement]);
            A = World.Players.Register(new PlayerId(2)); B = World.Players.Register(new PlayerId(3));
            World.Players.SetMembership(A.Id, Data.PlayerFaction, FactionPermission.All);
            World.Players.SetMembership(B.Id, other, FactionPermission.All);
            SquadA = AddSquad(A.State.Faction); SquadB = AddSquad(B.State.Faction);
            A.State.Squad = SquadA.Id; B.State.Squad = SquadB.Id;
            First = Spawn(A, SquadA); Second = Spawn(A, SquadA); Other = Spawn(B, SquadB);
        }

        public Squad AddSquad(int faction) => SquadManagement.NewSquad(World, Data.Db, new Vector3(0, 300, 0), "group", faction);
        public CharacterId Spawn(WorldPlayer player, Squad squad)
        {
            var id = World.Characters.Spawn(new CharacterHot { Position = new Vector3(World.Characters.Count * 20, 300, 0), MaxSpeed = 40, WalkSpeed = 15, Task = (byte)CharacterTask.Idle },
                new CharacterCold { Faction = squad.Faction, SquadId = squad.Id, IsPlayer = true, Inventory = new Inventory() }, 0);
            squad.Members.Add(id);
            if (squad.Leader.IsNone) squad.Leader = id;
            World.Players.AssignInitial(player.Id, id);
            return id;
        }
        public void Send(WorldPlayer actor, SimCommand command) => World.Commands.Enqueue(actor.Id, command with { Tick = World.Tick });
        public void Dispose() { World.Dispose(); _movement.Dispose(); }
    }

    [Fact]
    public void SquadChangesUseTheExplicitFactionAndDoNotRequireALocalPlayer()
    {
        using var s = new Scene();
        Assert.False(s.World.Player.Exists);
        int own = s.A.State.Faction, other = s.B.State.Faction;
        s.World.Players.SetMembership(s.A.Id, other, FactionPermission.None);
        Assert.True(s.World.Players.SelectFaction(s.A.Id, other));
        s.Send(s.A, new NewSquadCommand(Vector3.Zero) { Tick = 0, Faction = own });
        s.Send(s.A, new NewSquadCommand(Vector3.Zero) { Tick = 0, Faction = other });
        s.World.RunTick();
        Assert.Equal(2, SquadManagement.PlayerSquads(s.World, own).Count);
        Assert.Single(SquadManagement.PlayerSquads(s.World, other));
        var added = SquadManagement.PlayerSquads(s.World, own).Single(q => q != s.SquadA);
        s.Send(s.A, new RenameSquadCommand(added.Id, "ours") { Tick = 0, Faction = own });
        s.Send(s.B, new RenameSquadCommand(added.Id, "stolen") { Tick = 0, Faction = other });
        s.World.RunTick();
        Assert.Equal("ours", added.Name);
        Assert.Equal(other, s.A.State.Faction);
    }

    [Fact]
    public void ManagementPermissionAndRosterTargetsAreCheckedAgainAtExecution()
    {
        using var s = new Scene();
        int faction = s.A.State.Faction;
        s.Send(s.A, new RenameSquadCommand(s.SquadA.Id, "stale") { Tick = 0, Faction = faction });
        s.World.Players.SetMembership(s.A.Id, faction, FactionPermission.None);
        s.World.Players.SetMembership(s.A.Id, faction, FactionPermission.All);
        s.World.RunTick();
        Assert.NotEqual("stale", s.SquadA.Name);
        s.World.Players.SetMembership(s.A.Id, faction, FactionPermission.None);
        var empty = s.AddSquad(faction);
        s.Send(s.A, new ActiveSquadCommand(empty.Id) { Tick = 0, Faction = faction });
        s.Send(s.A, new RemoveSquadCommand(empty.Id) { Tick = 0, Faction = faction });
        s.World.RunTick();
        Assert.Equal(empty.Id, s.A.State.Squad); // membership permits viewing, not deleting
        Assert.NotNull(s.World.Squads.Find(empty.Id));
    }

    [Fact]
    public void SwappingRequiresBothControlGrantsAndTheDisplayedTargetMember()
    {
        using var s = new Scene(sameFaction: true);
        int faction = s.A.State.Faction;
        s.Send(s.A, new MoveToSquadCommand(s.First, s.SquadB.Id, 0, true) { Tick = 0, Faction = faction, SwapWith = s.Other });
        s.World.RunTick();
        Assert.Contains(s.First, s.SquadA.Members); Assert.Contains(s.Other, s.SquadB.Members);
        var target = s.AddSquad(faction);
        s.Send(s.A, new MoveToSquadCommand(s.Second, target.Id) { Tick = 0, Faction = faction });
        s.World.RunTick();
        var newcomer = s.Spawn(s.A, target);
        s.Send(s.A, new MoveToSquadCommand(newcomer, target.Id, 0) { Tick = 0, Faction = faction });
        s.Send(s.A, new MoveToSquadCommand(s.First, target.Id, 0, true) { Tick = 0, Faction = faction, SwapWith = s.Second });
        s.World.RunTick();
        Assert.Contains(s.First, s.SquadA.Members); Assert.Equal(newcomer, target.Members[0]);
        s.Send(s.A, new MoveToSquadCommand(s.First, target.Id, 0, true) { Tick = 0, Faction = faction, SwapWith = newcomer });
        s.World.RunTick();
        Assert.Contains(s.First, target.Members); Assert.Contains(newcomer, s.SquadA.Members);
        Assert.True(s.World.Players.CanControl(s.A.Id, s.First));
    }

    [Fact]
    public void ShiftDroppingPastTheLastMemberCannotBypassSquadCapacity()
    {
        using var s = new Scene();
        int faction = s.A.State.Faction;
        var target = s.AddSquad(faction);
        s.Spawn(s.A, target); s.Spawn(s.A, target);
        s.Send(s.A, new MoveToSquadCommand(s.First, target.Id, -1, true) { Tick = 0, Faction = faction });
        s.World.RunTick();
        Assert.Equal(2, target.Members.Count);
        Assert.Contains(s.First, s.SquadA.Members);
    }

    [Fact]
    public void DismissalRevokesQueuedAuthorityButPreservesTheCharacterAndItsItems()
    {
        using var s = new Scene();
        var table = s.World.Characters;
        var cold = table.Cold(s.First.Slot)!;
        var inventory = cold.Inventory!;
        inventory.Items.Add(new ItemInstance { Record = "keepsake" });
        var old = s.World.Players.ControlOf(s.First)!.Value;
        var position = table.Previous[s.First.Slot].Position;
        s.Send(s.A, new DismissCommand(s.First) { Tick = 0, Faction = s.A.State.Faction });
        s.World.Commands.Enqueue(s.A.Id, new MoveOrder([s.First], new Vector3(500, 300, 500)) { Tick = s.World.Tick });
        s.World.RunTick();
        Assert.False(cold.IsPlayer);
        Assert.Equal(s.Data.FactionIndex(SquadManagement.DismissFaction), cold.Faction);
        Assert.False(s.World.Players.Validate(old));
        Assert.DoesNotContain(s.World.Players.KnownRoles(s.A.Id), grant => grant.Entity == old.Entity);
        Assert.True(s.World.Entities.TryResolve(old.Entity, out var current));
        Assert.Equal(s.First, current);
        Assert.Same(inventory, cold.Inventory); Assert.Equal("keepsake", Assert.Single(inventory.Items).Record);
        Assert.Equal(position, table.Previous[s.First.Slot].Position);
        s.Send(s.A, new DismissCommand(s.Second) { Tick = 0, Faction = s.A.State.Faction });
        s.World.RunTick();
        Assert.True(table.Cold(s.Second.Slot)!.IsPlayer); // another faction's members do not satisfy this faction's last-member rule
        cold.Faction = s.B.State.Faction; cold.IsPlayer = true; // a later authorized recruitment may grant the same entity again
        var next = s.World.Players.AssignInitial(s.B.Id, s.First);
        Assert.True(s.World.Players.Validate(next)); Assert.False(s.World.Players.Validate(old));
    }

    [Fact]
    public void RenameAndFormationUseAuthorizedFactionAndSharedNameUniqueness()
    {
        using var s = new Scene();
        int own = s.A.State.Faction, other = s.B.State.Faction;
        s.World.FactionFormations = s.Data.Factions.Take(s.Data.Factions.Count - 1).Select(f => f.SquadFormation).ToArray();
        s.World.Players.SetMembership(s.A.Id, other, FactionPermission.None);
        s.World.Players.SelectFaction(s.A.Id, other);
        s.Send(s.A, new RenameFactionCommand("Mine") { Faction = own, Tick = 0 });
        s.Send(s.A, new RenameFactionCommand("Stolen") { Faction = other, Tick = 0 });
        s.Send(s.A, new SquadFormationCommand(SquadFormation.Military) { Faction = other, Tick = 0 });
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal("Mine", s.World.Factions.Find(own)!.Name);
        Assert.Null(s.World.Factions.Find(other)!.Name);
        Assert.NotEqual((int)SquadFormation.Military, SquadAiKeys.FormationOf(s.World, other, s.Data.Factions));
        s.Send(s.B, new RenameFactionCommand("Mine") { Faction = other, Tick = 0 });
        s.Send(s.B, new SquadFormationCommand(SquadFormation.Military) { Faction = other, Tick = 0 });
        s.World.ApplyPausedInterfaceCommands();
        Assert.Null(s.World.Factions.Find(other)!.Name);
        Assert.Equal((int)SquadFormation.Military, SquadAiKeys.FormationOf(s.World, other, s.Data.Factions));
        s.Send(s.A, new RenameFactionCommand("stale") { Faction = own, Tick = 0 });
        s.World.Players.SetMembership(s.A.Id, own, FactionPermission.None);
        s.World.Players.SetMembership(s.A.Id, own, FactionPermission.All);
        s.Send(s.B, new SquadFormationCommand((SquadFormation)999) { Faction = other, Tick = 0 });
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal("Mine", s.World.Factions.Find(own)!.Name);
        Assert.Equal((int)SquadFormation.Military, SquadAiKeys.FormationOf(s.World, other, s.Data.Factions));
        Assert.Equal(other, s.A.State.Faction);
    }

    [Fact]
    public void FoodSharingRequiresBothControllersOptInAndFollowsControlTransfers()
    {
        using var s = new Scene(sameFaction: true);
        var table = s.World.Characters;
        var hungry = table.Cold(s.First.Slot)!;
        hungry.Race = new RaceData { StringId = "human" };
        hungry.Medical = new MedicalState([]) { Hunger = 2 };
        var donor = table.Cold(s.Other.Slot)!;
        s.SquadB.Members.Remove(s.Other); s.SquadA.Members.Add(s.Other); donor.SquadId = s.SquadA.Id;
        var bag = new Inventory();
        var food = new ItemInstance { Record = "food", ItemFunction = FeedSystem.FoodFunction, Charges = 150, MaxCharges = 150 };
        bag.Items.Add(food);
        donor.Inventory!.Items.Add(new ItemInstance { Record = "bag", Section = "backpack_attach", Contents = bag });
        var feeding = new FeedSystem(new ItemFactory(s.Data.Db, GameConstants.FromDatabase(s.Data.Db)), new FeedSettings { CheckEveryTicks = 1 });
        s.Send(s.B, new SquadAiCommand(SquadAi.Share, false) { Tick = 0 });
        s.Send(s.A, new SquadAiCommand((SquadAi)ushort.MaxValue, false) { Tick = 0 });
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(SquadAi.Defaults, s.A.State.SquadAi);
        feeding.SlowWorld(s.World);
        Assert.Equal(150, food.Charges); Assert.Equal(0, hungry.Medical.Fed);
        s.Send(s.B, new SquadAiCommand(SquadAi.Share, true) { Tick = 0 });
        s.World.ApplyPausedInterfaceCommands();
        feeding.SlowWorld(s.World);
        Assert.Equal(100, food.Charges); Assert.Equal(0.5f, hungry.Medical.Fed);
        hungry.Medical.Fed = 0;
        s.Send(s.A, new SquadAiCommand(SquadAi.Share, false) { Tick = 0 });
        s.World.ApplyPausedInterfaceCommands();
        feeding.SlowWorld(s.World);
        Assert.Equal(100, food.Charges);
        var grant = s.World.Players.ControlOf(s.First)!.Value;
        Assert.True(s.World.Players.TryTransfer(s.A.Id, grant, s.B.Id, out _));
        feeding.SlowWorld(s.World);
        Assert.Equal(50, food.Charges); // new controller's policy, independently of the old owner's or local options
    }

    [Fact]
    public void DeathArchivesAreSeparateAndKeepTheOwnersDeadRoleRecords()
    {
        using var s = new Scene();
        s.A.State.Selection.Add(s.First); s.B.State.Selection.Add(s.Other);
        var owner = s.World.Players.ControlOf(s.Other)!.Value;
        s.World.Characters.Cold(s.First.Slot)!.DiedTick = 0;
        s.World.Characters.Cold(s.Other.Slot)!.DiedTick = 0;
        s.World.RunTick();
        var archives = s.World.Squads.All.Where(q => q.Dead).ToArray();
        Assert.Equal(2, archives.Length);
        Assert.Equal(s.First, Assert.Single(archives.Single(q => q.Faction == s.A.State.Faction).Members));
        Assert.Equal(s.Other, Assert.Single(archives.Single(q => q.Faction == s.B.State.Faction).Members));
        Assert.Equal(s.Second, Assert.Single(s.A.State.Selection));
        Assert.Empty(s.B.State.Selection);
        Assert.Equal(owner, Assert.Single(s.World.Players.KnownRoles(s.B.Id)));
        Assert.False(s.World.Players.Validate(owner));
    }
}
