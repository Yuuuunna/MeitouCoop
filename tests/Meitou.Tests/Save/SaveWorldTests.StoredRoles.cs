using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Items;
using Meitou.Simulation.Saving;

namespace Meitou.Tests.Save;

public partial class SaveWorldTests
{
    sealed class StoredRoleNavigation : Meitou.Runtime.IRegionNavigation
    {
        public HashSet<Meitou.Data.World.ZoneCoordinate> Requested { get; } = [];
        public bool Available;
        public IReadOnlyCollection<Meitou.Data.World.ZoneCoordinate> Tracked => Requested.ToArray();
        public IEnumerable<Meitou.Data.World.ZoneCoordinate> RouteHints => [];
        public bool Ready(Meitou.Data.World.ZoneCoordinate zone) => Available && Requested.Contains(zone);
        public void Request(Meitou.Data.World.ZoneCoordinate zone) => Requested.Add(zone);
        public void Unload(Meitou.Data.World.ZoneCoordinate zone) => Requested.Remove(zone);
    }
    [Fact]
    public void DeferredRolesActivateWhenRegionsBecomeReadyWithoutWaitingForAnyClient()
    {
        using var g = NewGame(); var player = new PlayerId(2);
        g.World.Players.Register(player); g.World.Players.SetMembership(player, g.Data.PlayerFaction, FactionPermission.None);
        foreach (var role in g.Squad.Members) g.World.Players.AssignInitial(player, role);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0)); var metadata = PlayerRegistrySave.Capture(g.World, g.Data);
        using var restored = EmptyWorld();
        SaveLoader.Load(restored, save, g.Data, new SaveLoadOptions { MultiplayerMetadata = metadata, DeferPlayerRoles = true });
        PlayerRegistrySave.Restore(restored, g.Data, metadata);
        var navigation = new StoredRoleNavigation(); var service = new Meitou.Runtime.WorldRegionNavigation(navigation);
        service.Synchronize(restored); Assert.NotEmpty(navigation.Requested); Assert.Equal(0, restored.Characters.Count);
        navigation.Available = true; service.Synchronize(restored);
        Assert.Equal(4, restored.Characters.Count); Assert.Equal(0, restored.Tick);
        Assert.False(restored.Authority.HasHistory); Assert.Equal(PlayerEntryRoute.EnterWorld, restored.Players.AssessEntry(player).Route);
        service.Synchronize(restored); Assert.Equal(4, restored.Characters.Count);
    }

    [Fact]
    public void DeferredPlayerSquadRetainsQuotaSaveDataAndExactStateUntilItsRegionIsReady()
    {
        using var g = NewGame(items: true);
        var player = new PlayerId(2); g.World.Players.Register(player); g.World.Players.SetMembership(player, g.Data.PlayerFaction, FactionPermission.All);
        foreach (var id in g.Squad.Members) g.World.Players.AssignInitial(player, id);
        var leader = g.Squad.Leader; var grant = g.World.Players.ControlOf(leader)!.Value; var cold = g.World.Characters.Cold(leader.Slot)!;
        var position = g.World.Characters.Current[leader.Slot].Position;
        cold.Name = "Stored role"; cold.Stats![StatsEnumerated.Strength] = 53; cold.Medical!.Blood = 42;
        cold.Inventory = new Inventory(); var item = g.Data.Items.Create("70-t", 3)!; item.Charges = 40; cold.Inventory.Items.Add(item);
        cold.Jobs.Add(Meitou.Simulation.Production.PermaJob.Untargeted(TaskType.JobMedic)); cold.Orders = CharacterOrders.Hold | CharacterOrders.Jobs;
        SquadManagement.NewSquad(g.World, g.Data.Db, position, "Empty", g.Data.PlayerFaction);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0)); var metadata = PlayerRegistrySave.Capture(g.World, g.Data);
        using var restored = new Meitou.Simulation.World(new WorldSettings { PublishSnapshots = false }, g.World.Walkability, [new PlayerSystem(g.Data)]);
        var loaded = SaveLoader.Load(restored, save, g.Data, new SaveLoadOptions { MultiplayerMetadata = metadata, DeferPlayerRoles = true });
        PlayerRegistrySave.Restore(restored, g.Data, metadata);
        Assert.Equal(0, restored.Characters.Count); Assert.Equal(4, SquadManagement.FactionHeads(restored, g.Data.PlayerFaction));
        var connection = restored.Authority.Open(player);
        Assert.Equal(PlayerEntryRoute.ResolveStoredRoles, restored.Authority.AssessEntry(connection)!.Route);
        Assert.Equal(PlayerRoleAvailability.Stored, restored.Authority.LocateRole(connection, grant.Entity)!.Availability);
        Assert.Equal(position, restored.Authority.LocateRole(connection, grant.Entity)!.Position);
        Assert.False(restored.Authority.BeginSynchronization(connection));
        restored.Commands.Enqueue(player, new RemoveSquadCommand(g.Squad.Id) { Tick = 0, Faction = g.Data.PlayerFaction });
        restored.ApplyPausedInterfaceCommands(); Assert.NotNull(restored.Squads.Find(g.Squad.Id));
        Assert.False(restored.StoredPlayerRoles!.Restore(grant.Entity, _ => false)); Assert.Equal(0, restored.Characters.Count);
        var untouched = SaveCapture.Capture(restored, g.Data, new SaveClock(1, 12, 0), loaded);
        Assert.Equal(4, untouched.Camera.Members); Assert.Equal(4, untouched.Platoons.Sum(p => p.Characters.Count));
        var stranger = restored.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = g.Data.PlayerFaction }, 0);
        Assert.False(restored.Players.CanControl(player, stranger));
        Assert.True(restored.StoredPlayerRoles.Restore(grant.Entity, _ => true));
        Assert.True(restored.Entities.TryResolve(grant.Entity, out var current)); Assert.NotEqual(stranger, current);
        var actual = restored.Characters.Cold(current.Slot)!;
        Assert.Equal(position, restored.Characters.Current[current.Slot].Position); Assert.Equal(53, actual.Stats!.Strength);
        Assert.Equal(42, actual.Medical!.Blood); Assert.Equal("Stored role", actual.Name);
        Assert.Equal(40, Assert.Single(actual.Inventory!.Items).Charges); Assert.Equal(3, actual.Inventory.Items[0].Quantity);
        Assert.Contains(actual.Jobs, j => j.Type == TaskType.JobMedic); Assert.Equal(cold.Orders, actual.Orders);
        Assert.True(restored.Players.Validate(grant)); Assert.True(restored.Authority.BeginSynchronization(connection));
        Assert.True(restored.StoredPlayerRoles.Restore(grant.Entity, _ => true)); Assert.Equal(5, restored.Characters.Count);
        restored.Characters.Remove(current);
        Assert.False(restored.StoredPlayerRoles.Restore(grant.Entity, _ => true));
        Assert.Equal(PlayerRoleAvailability.Unavailable, restored.Authority.LocateRole(connection, grant.Entity)!.Availability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeadRoleArchiveAndRemovedCorpseStayDeadAcrossCheckpointMetadata(bool remove)
    {
        using var g = NewGame(); var player = new PlayerId(2);
        g.World.Players.Register(player); g.World.Players.SetMembership(player, g.Data.PlayerFaction, FactionPermission.None);
        var grant = g.World.Players.AssignInitial(player, g.Squad.Leader);
        g.World.Characters.Cold(g.Squad.Leader.Slot)!.DiedTick = 0;
        if (remove) g.World.Characters.Remove(g.Squad.Leader);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0)); var metadata = PlayerRegistrySave.Capture(g.World, g.Data);
        using var restored = EmptyWorld();
        SaveLoader.Load(restored, save, g.Data, new SaveLoadOptions { MultiplayerMetadata = metadata, DeferPlayerRoles = true });
        PlayerRegistrySave.Restore(restored, g.Data, metadata);
        var status = restored.Players.AssessEntry(player);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, status.Route); Assert.Equal(1, status.DeadRoles); Assert.Equal(0, status.UnresolvedRoles);
        Assert.False(restored.StoredPlayerRoles!.Restore(grant.Entity, _ => true));
        Assert.Equal(PlayerRoleAvailability.Dead, restored.Players.LocateRole(player, grant.Entity)!.Availability);
        Assert.Throws<InvalidOperationException>(() => restored.CreationQualifications.IssueInitial(player, "start", 1));
    }

    [Fact]
    public void MissingCarryPartnerKeepsTheStoredRoleUncontrollableWithoutDeletingItsSavedRecords()
    {
        using var g = NewGame(); var player = new PlayerId(2);
        g.World.Players.Register(player); g.World.Players.SetMembership(player, g.Data.PlayerFaction, FactionPermission.None);
        var grant = g.World.Players.AssignInitial(player, g.Squad.Leader);
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0)); var metadata = PlayerRegistrySave.Capture(g.World, g.Data);
        var character = save.Platoons.SelectMany(p => p.Characters).Single(c => EntitySave.Read(c) == grant.Entity);
        (character.Handle with { C = int.MaxValue }).Write(character.GameState!, "carrying"); character.GameState!.Bools["carrying"] = false;
        using var restored = EmptyWorld();
        var loaded = SaveLoader.Load(restored, save, g.Data, new SaveLoadOptions { MultiplayerMetadata = metadata, DeferPlayerRoles = true });
        PlayerRegistrySave.Restore(restored, g.Data, metadata);
        Assert.False(restored.StoredPlayerRoles!.Restore(grant.Entity, _ => true)); Assert.Equal(0, restored.Characters.Count);
        Assert.Equal(PlayerEntryRoute.ResolveStoredRoles, restored.Players.AssessEntry(player).Route);
        Assert.False(restored.Authority.BeginSynchronization(restored.Authority.Open(player)));
        var again = SaveCapture.Capture(restored, g.Data, new SaveClock(1, 12, 0), loaded);
        Assert.Contains(again.Platoons.SelectMany(p => p.Characters), c => EntitySave.Read(c) == grant.Entity);
    }

    [Fact]
    public void DeferredCarryDependenciesRestoreTogetherAndNeverGrantControlOverTheCarrier()
    {
        using var g = NewGame(items: true);
        var a = new PlayerId(2); var b = new PlayerId(3);
        g.World.Players.Register(a); g.World.Players.Register(b);
        g.World.Players.SetMembership(a, g.Data.PlayerFaction, FactionPermission.None); g.World.Players.SetMembership(b, g.Data.PlayerFaction, FactionPermission.None);
        var carrier = g.Squad.Members[0]; var body = g.Squad.Members[1];
        g.World.Players.AssignInitial(a, carrier); var grant = g.World.Players.AssignInitial(b, body);
        g.World.Characters.Cold(carrier.Slot)!.Carrying = body; g.World.Characters.Cold(body.Slot)!.CarriedBy = carrier;
        var save = SaveCapture.Capture(g.World, g.Data, new SaveClock(1, 12, 0)); var metadata = PlayerRegistrySave.Capture(g.World, g.Data);
        using var restored = new Meitou.Simulation.World(new WorldSettings { PublishSnapshots = false }, g.World.Walkability,
            [new PlayerSystem(g.Data), new Meitou.Simulation.Carrying.CarrySystem(g.Data.Bodies.Constants, g.Data.BodyOptions)]);
        SaveLoader.Load(restored, save, g.Data, new SaveLoadOptions { MultiplayerMetadata = metadata, DeferPlayerRoles = true });
        PlayerRegistrySave.Restore(restored, g.Data, metadata);
        Assert.True(restored.StoredPlayerRoles!.Restore(grant.Entity, _ => true)); Assert.True(restored.Entities.TryResolve(grant.Entity, out var carried));
        var who = restored.Characters.Cold(carried.Slot)!.CarriedBy; Assert.False(who.IsNone);
        Assert.True(restored.Players.CanControl(b, carried)); Assert.False(restored.Players.CanControl(b, who));
        Assert.Equal(restored.Characters.Current[who.Slot].Position, restored.Characters.Current[carried.Slot].Position);
        Assert.Equal(PlayerEntryRoute.EnterWorld, restored.Players.AssessEntry(b).Route);
    }
}
