using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class CreationQualificationTests
{
    static SimWorld World() => new(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
    static (WorldPlayer Actor, WorldPlayer Recipient) Members(SimWorld world, FactionPermission rights = FactionPermission.All)
    {
        var actor = world.Players.Register(new PlayerId(2)); var recipient = world.Players.Register(new PlayerId(3));
        world.Players.SetMembership(actor.Id, 2, rights);
        world.Players.SetMembership(recipient.Id, 2, FactionPermission.None);
        actor.State.Money = 100;
        return (actor, recipient);
    }

    [Fact]
    public void InitialApprovalIsExplicitUniqueAndDoesNotCreateRolesOrAwardMoney()
    {
        using var world = World(); var player = world.Players.Register(new PlayerId(2));
        Assert.Empty(world.CreationQualifications.For(player.Id));
        var grant = world.CreationQualifications.IssueInitial(player.Id, "starts:v1", 4);
        Assert.True(grant.IncludesStartResources);
        Assert.Equal(-1, grant.Faction);
        Assert.Equal(CreationEligibility.Eligible, world.CreationQualifications.Assess(player.Id, grant.Id, grant.Revision, 4));
        Assert.Equal(CreationEligibility.CharacterLimit, world.CreationQualifications.Assess(player.Id, grant.Id, grant.Revision, 5));
        Assert.Equal(grant, world.CreationQualifications.IssueInitial(player.Id, "starts:v1", 4));
        Assert.Equal(0, world.Characters.Count); Assert.Equal(0, player.State.Money);
        Assert.True(world.CreationQualifications.Revoke(grant.Id, grant.Revision));
        var replay = world.CreationQualifications.IssueInitial(player.Id, "starts:v1", 4);
        Assert.Equal(CreationQualificationStatus.Revoked, replay.Status);
        Assert.Equal(CreationEligibility.Revoked, world.CreationQualifications.Assess(player.Id, replay.Id, replay.Revision, 1));
        Assert.Equal(CreationEligibility.StaleRevision, world.CreationQualifications.Assess(player.Id, grant.Id, grant.Revision, 1));
    }

    [Fact]
    public void ReceivingAndLosingARoleCannotMakeAnInitialQualificationUsableAgain()
    {
        using var world = World(); var player = world.Players.Register(new PlayerId(2));
        var grant = world.CreationQualifications.IssueInitial(player.Id, "start", 1);
        world.Players.SetMembership(player.Id, 2, FactionPermission.None);
        Assert.Equal(CreationEligibility.MembershipChanged, world.CreationQualifications.Assess(player.Id, grant.Id, grant.Revision, 1));
        var role = world.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = 2, IsPlayer = true }, 0);
        world.Players.AssignInitial(player.Id, role);
        world.Players.RemoveMembership(player.Id, 2);
        Assert.Equal(CreationEligibility.RoleHistory, world.CreationQualifications.Assess(player.Id, grant.Id, grant.Revision, 1));
        Assert.Equal(PlayerRoleHistory.Assigned, player.RoleHistory);
        Assert.Null(world.CreationQualifications.ReauthorizeInitial(grant.Id, grant.Revision, "new-start", 1));
    }

    [Fact]
    public void ExplicitPolicyReauthorizationKeepsIdentityAndInvalidatesEarlierPreviews()
    {
        using var world = World(); var player = world.Players.Register(new PlayerId(2));
        var old = world.CreationQualifications.IssueInitial(player.Id, "starts:v1", 4);
        Assert.True(world.CreationQualifications.Revoke(old.Id, old.Revision));
        var revoked = world.CreationQualifications.Find(old.Id)!;
        Assert.Null(world.CreationQualifications.ReauthorizeInitial(old.Id, old.Revision, "starts:v2", 3));
        var current = world.CreationQualifications.ReauthorizeInitial(old.Id, revoked.Revision, "starts:v2", 3)!;
        Assert.Equal(old.Id, current.Id); Assert.True(current.Revision > revoked.Revision);
        Assert.Equal(CreationEligibility.StaleRevision, world.CreationQualifications.Assess(player.Id, old.Id, old.Revision, 1));
        Assert.Equal(CreationEligibility.Eligible, world.CreationQualifications.Assess(player.Id, current.Id, current.Revision, 3));
        Assert.Single(world.CreationQualifications.For(player.Id));
    }

    [Fact]
    public void MembershipOrAssignmentPermissionDoesNotAuthorizeCreatingCharacters()
    {
        using var world = World();
        var (actor, recipient) = Members(world, FactionPermission.AssignCharacters | FactionPermission.SpendFunds);
        Assert.Throws<InvalidOperationException>(() => world.CreationQualifications.ApproveFaction(actor.Id, Guid.NewGuid(), recipient.Id, 2, "recruit", 1, 10));
        world.Players.SetMembership(actor.Id, 2, FactionPermission.CreateCharacters);
        Assert.Throws<InvalidOperationException>(() => world.CreationQualifications.ApproveFaction(actor.Id, Guid.NewGuid(), recipient.Id, 2, "recruit", 1, 10));
        var approved = world.CreationQualifications.ApproveFaction(actor.Id, Guid.NewGuid(), recipient.Id, 2, "recruit", 1, 0);
        Assert.False(approved.IncludesStartResources);
        Assert.Equal(100, actor.State.Money);
        Assert.Throws<InvalidOperationException>(() => world.CreationQualifications.IssueInitial(recipient.Id, "start", 1));
    }

    [Theory]
    [InlineData("approver")]
    [InlineData("member")]
    [InlineData("funds")]
    [InlineData("expiry")]
    public void FactionApprovalRechecksLiveMembershipFundsAndExpiry(string change)
    {
        using var world = World(); var (actor, recipient) = Members(world);
        var grant = world.CreationQualifications.ApproveFaction(actor.Id, Guid.NewGuid(), recipient.Id, 2, "faction-recruit", 2, 40, world.GameHours + 1);
        Assert.False(grant.IncludesStartResources);
        Assert.Equal(CreationEligibility.Eligible, world.CreationQualifications.Assess(recipient.Id, grant.Id, grant.Revision, 2));
        var expected = CreationEligibility.Eligible;
        switch (change)
        {
            case "approver":
                world.Players.SetMembership(actor.Id, 2, FactionPermission.None); world.Players.SetMembership(actor.Id, 2, FactionPermission.All);
                expected = CreationEligibility.ApprovalChanged; break;
            case "member":
                world.Players.RemoveMembership(recipient.Id, 2); world.Players.SetMembership(recipient.Id, 2, FactionPermission.None);
                expected = CreationEligibility.MembershipChanged; break;
            case "funds": actor.State.Money = 39; expected = CreationEligibility.InsufficientFunds; break;
            case "expiry": world.GameHours += 1; expected = CreationEligibility.Expired; break;
        }
        Assert.Equal(expected, world.CreationQualifications.Assess(recipient.Id, grant.Id, grant.Revision, 2));
        Assert.Equal(0, world.Characters.Count);
    }

    [Fact]
    public void ApprovalRetryDoesNotDuplicateOrReopenRevokedQualifications()
    {
        using var world = World(); var (actor, recipient) = Members(world);
        Guid key = Guid.NewGuid();
        var grant = world.CreationQualifications.ApproveFaction(actor.Id, key, recipient.Id, 2, "recruit", 2, 40);
        Assert.Equal(grant, world.CreationQualifications.ApproveFaction(actor.Id, key, recipient.Id, 2, "recruit", 2, 40));
        Assert.Throws<InvalidOperationException>(() => world.CreationQualifications.ApproveFaction(actor.Id, key, recipient.Id, 2, "recruit", 2, 10));
        Assert.True(world.CreationQualifications.Revoke(grant.Id, grant.Revision));
        Assert.Equal(CreationQualificationStatus.Revoked, world.CreationQualifications.ApproveFaction(actor.Id, key, recipient.Id, 2, "recruit", 2, 40).Status);
        Assert.Single(world.CreationQualifications.For(recipient.Id));
        Assert.Equal(100, actor.State.Money);
    }

    [Fact]
    public void PreparationConnectionOnlySeesItsOwnQualifications()
    {
        using var world = World(); var (actor, recipient) = Members(world);
        world.CreationQualifications.ApproveFaction(actor.Id, Guid.NewGuid(), recipient.Id, 2, "recruit", 1, 10);
        var first = world.Authority.Open(recipient.Id); var other = world.Authority.Open(actor.Id);
        Assert.Single(world.Authority.CreationOptions(first)!); Assert.Empty(world.Authority.CreationOptions(other)!);
        var current = world.Authority.Open(recipient.Id);
        Assert.Null(world.Authority.CreationOptions(first)); Assert.Single(world.Authority.CreationOptions(current)!);
        Assert.False(world.Authority.BeginSynchronization(current)); // Approval alone never opens gameplay.
    }

    [Fact]
    public void IncompleteOrForgedQualificationStateIsRejected()
    {
        using var world = World(); var (actor, recipient) = Members(world);
        world.CreationQualifications.ApproveFaction(actor.Id, Guid.NewGuid(), recipient.Id, 2, "recruit", 1, 10);
        var state = world.CreationQualifications.Capture(); var authority = world.Players.Capture();
        Assert.Throws<InvalidDataException>(() => CreationQualifications.Validate(state with { Grants = [] }, authority, 3));
        Assert.Throws<InvalidDataException>(() => CreationQualifications.Validate(state with { Grants = [state.Grants[0] with { Kind = CreationQualificationKind.InitialStart }] }, authority, 3));
        Assert.Throws<InvalidDataException>(() => CreationQualifications.Validate(state with { Grants = [state.Grants[0] with { ApproverRevision = ulong.MaxValue }] }, authority, 3));
    }
}
