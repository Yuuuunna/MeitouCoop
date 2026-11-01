using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Locks;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class PlayerAdmissionHistoryTests
{
    [Fact]
    public void PreparingSessionAssessmentUsesOnlyTheCurrentConnectionIdentity()
    {
        using var world = World();
        var a = world.Players.Register(new PlayerId(2));
        var b = world.Players.Register(new PlayerId(3));
        world.Players.SetMembership(b.Id, 2, FactionPermission.None);
        var old = world.Authority.Open(a.Id);
        var other = world.Authority.Open(b.Id);
        Assert.Equal(PlayerEntryRoute.NewPlayer, world.Authority.AssessEntry(old)!.Route);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, world.Authority.AssessEntry(other)!.Route);
        var current = world.Authority.Open(a.Id);
        Assert.Null(world.Authority.AssessEntry(old));
        Assert.Equal(PlayerEntryRoute.NewPlayer, world.Authority.AssessEntry(current)!.Route);
        world.Authority.Close(current);
        Assert.Null(world.Authority.AssessEntry(current));
    }

    static SimWorld World() => new(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
    static CharacterId Role(SimWorld world, int faction) => world.Characters.Spawn(new CharacterHot { Position = Vector3.One },
        new CharacterCold { Faction = faction, IsPlayer = true, Medical = Bodies.BodyFixtures.Healthy() }, 0);

    [Fact]
    public void MembershipAndRevokedRoleHistoryAreDistinctAndSurviveRestore()
    {
        using var world = World();
        var player = world.Players.Register(new PlayerId(2));
        Assert.Equal(PlayerEntryRoute.NewPlayer, world.Players.AssessEntry(player.Id).Route);
        world.Players.SetMembership(player.Id, 2, FactionPermission.None);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, world.Players.AssessEntry(player.Id).Route);
        Assert.Equal(PlayerRoleHistory.NeverAssigned, player.RoleHistory);
        var role = Role(world, 2);
        world.Players.AssignInitial(player.Id, role);
        Assert.Equal(PlayerEntryRoute.EnterWorld, world.Players.AssessEntry(player.Id).Route);
        world.Players.RemoveMembership(player.Id, 2);
        Assert.Empty(world.Players.KnownRoles(player.Id));
        Assert.Equal(PlayerRoleHistory.Assigned, player.RoleHistory);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, world.Players.AssessEntry(player.Id).Route);
        using var restored = World();
        foreach (var entity in world.Entities.All) restored.Entities.Reserve(entity);
        restored.Players.Restore(world.Players.Capture(), 3);
        Assert.Equal(PlayerRoleHistory.Assigned, restored.Players.Find(player.Id)!.RoleHistory);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, restored.Players.AssessEntry(player.Id).Route);
    }

    [Theory]
    [InlineData("unconscious")]
    [InlineData("captured")]
    [InlineData("carried")]
    public void LivingRolesEnterInTheirExistingConditionWithoutHealingOrTeleporting(string condition)
    {
        using var world = World();
        var player = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(player.Id, 2, FactionPermission.None);
        var role = Role(world, 2);
        var cold = world.Characters.Cold(role.Slot)!;
        world.Players.AssignInitial(player.Id, role);
        if (condition == "unconscious") cold.Medical!.KnockOut(30);
        if (condition == "captured") cold.InSomething = HeldState.Imprisoned;
        if (condition == "carried") cold.CarriedBy = Role(world, 1);
        var before = (cold.Medical!.KoTimer, cold.InSomething, cold.CarriedBy, world.Characters.Current[role.Slot].Position);
        var assessment = world.Players.AssessEntry(player.Id);
        Assert.Equal(PlayerEntryRoute.EnterWorld, assessment.Route);
        Assert.Equal(1, assessment.AvailableRoles);
        var connection = world.Authority.Open(player.Id);
        Assert.True(world.Authority.BeginSynchronization(connection)); Assert.True(world.Authority.Activate(connection));
        Assert.Equal(before, (cold.Medical.KoTimer, cold.InSomething, cold.CarriedBy, world.Characters.Current[role.Slot].Position));
    }

    [Fact]
    public void DeadAndUnresolvedRolesNeverBecomeAFirstTimePlayer()
    {
        using var world = World();
        var player = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(player.Id, 2, FactionPermission.None);
        var role = Role(world, 2);
        world.Players.AssignInitial(player.Id, role);
        world.Characters.Cold(role.Slot)!.DiedTick = 0;
        var dead = world.Players.AssessEntry(player.Id);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, dead.Route);
        Assert.Equal(1, dead.DeadRoles); Assert.Equal(0, dead.AvailableRoles);
        world.Characters.Remove(role);
        var unresolved = world.Players.AssessEntry(player.Id);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, unresolved.Route);
        Assert.Equal(0, unresolved.UnresolvedRoles); Assert.Equal(1, unresolved.DeadRoles);
        Assert.Equal(PlayerRoleHistory.Assigned, unresolved.History);
        Assert.False(world.Authority.BeginSynchronization(world.Authority.Open(player.Id)));
    }

    [Fact]
    public void TransferRecordsBothPlayersHistoryAndARejectedTransferDoesNotInventHistory()
    {
        using var world = World();
        var a = world.Players.Register(new PlayerId(2));
        var b = world.Players.Register(new PlayerId(3));
        var stranger = world.Players.Register(new PlayerId(4));
        world.Players.SetMembership(a.Id, 2, FactionPermission.All);
        world.Players.SetMembership(b.Id, 2, FactionPermission.None);
        var role = Role(world, 2);
        var grant = world.Players.AssignInitial(a.Id, role);
        Assert.False(world.Players.TryTransfer(a.Id, grant, stranger.Id, out _));
        Assert.Equal(PlayerRoleHistory.NeverAssigned, stranger.RoleHistory);
        Assert.True(world.Players.TryTransfer(a.Id, grant, b.Id, out _));
        Assert.Equal(PlayerRoleHistory.Assigned, a.RoleHistory);
        Assert.Equal(PlayerRoleHistory.Assigned, b.RoleHistory);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, world.Players.AssessEntry(a.Id).Route);
        Assert.Equal(PlayerEntryRoute.EnterWorld, world.Players.AssessEntry(b.Id).Route);
    }

    [Fact]
    public void GrantCannotBeRestoredWithAFalseNeverAssignedHistory()
    {
        using var source = World();
        var player = source.Players.Register(new PlayerId(2));
        source.Players.SetMembership(player.Id, 2, FactionPermission.All);
        var grant = source.Players.AssignInitial(player.Id, Role(source, 2));
        var state = source.Players.Capture();
        state.Players[1] = state.Players[1] with { RoleHistory = PlayerRoleHistory.NeverAssigned };
        using var target = World(); target.Entities.Reserve(grant.Entity);
        Assert.Throws<InvalidDataException>(() => target.Players.Restore(state, 3));
        Assert.Single(target.Players.All);
    }
}
