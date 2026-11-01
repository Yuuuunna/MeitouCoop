using System.Text.Json;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class PlayerRegistryPersistenceTests
{
    static SimWorld World() => new(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
    static PlayerRegistryState State(SimWorld world)
    {
        var a = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(a.Id, 2, FactionPermission.ReadFunds);
        world.Players.SetMembership(PlayerId.Local, 2, FactionPermission.All);
        var id = world.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = 2, IsPlayer = true }, 0);
        world.Players.AssignInitial(a.Id, id);
        return world.Players.Capture();
    }
    static void Reserve(SimWorld world, PlayerRegistryState state)
    {
        foreach (var grant in state.Controls) world.Entities.Reserve(grant.Entity);
    }

    [Fact]
    public void DetachedSnapshotPreservesUnloadedOwnershipWithoutGrantingAnActiveRole()
    {
        using var source = World();
        var state = State(source);
        var owner = new PlayerId(2);
        source.Players.SetMembership(owner, 2, FactionPermission.All);
        using var target = World();
        Reserve(target, state);
        target.Players.Restore(state, 3);
        Assert.True(target.Players.RequiresExplicitControl);
        Assert.Equal(FactionPermission.ReadFunds, Assert.Single(target.Players.Find(owner)!.Memberships).Value);
        var grant = Assert.Single(target.Players.KnownRoles(owner));
        Assert.False(target.Players.Validate(grant));
        Assert.False(target.Authority.BeginSynchronization(target.Authority.Open(owner)));
        Assert.True(target.Factions.Find(2)!.PlayerOwned);
    }

    [Fact]
    public void RemovedLastMembershipStillRestoresExplicitModeAndFactionClassification()
    {
        using var source = World();
        source.Players.SetMembership(PlayerId.Local, 2, FactionPermission.All);
        ulong revoked = source.Players.Find(PlayerId.Local)!.MembershipRevision(2);
        source.Players.RemoveMembership(PlayerId.Local, 2);
        var state = source.Players.Capture();
        using var target = World();
        target.Players.Restore(state, 3);
        Assert.True(target.Players.RequiresExplicitControl);
        Assert.True(target.Factions.Find(2)!.PlayerOwned);
        Assert.Empty(target.Players.Find(PlayerId.Local)!.Memberships);
        target.Players.SetMembership(PlayerId.Local, 2, FactionPermission.All);
        Assert.True(target.Players.Find(PlayerId.Local)!.MembershipRevision(2) > revoked);
    }

    [Theory]
    [InlineData("duplicate-player")]
    [InlineData("missing-local")]
    [InlineData("zero-player")]
    [InlineData("permission")]
    [InlineData("membership")]
    [InlineData("revision")]
    [InlineData("duplicate-role")]
    [InlineData("unknown-role")]
    [InlineData("owner")]
    [InlineData("view")]
    [InlineData("implicit")]
    [InlineData("classification")]
    public void InvalidAuthorityGraphCannotPartiallyRegisterPlayersOrGrantPermissions(string defect)
    {
        using var source = World();
        var valid = State(source);
        var state = JsonSerializer.Deserialize<PlayerRegistryState>(JsonSerializer.Serialize(valid))!;
        var remote = state.Players.Single(p => p.Id.Value == 2);
        var membership = remote.Memberships[0];
        state = defect switch
        {
            "duplicate-player" => state with { Players = [.. state.Players, remote] },
            "missing-local" => state with { Players = [remote] },
            "zero-player" => state with { Players = [state.Players[0], remote with { Id = default }] },
            "permission" => state with { Players = [state.Players[0], remote with { Memberships = [membership with { Permissions = (FactionPermission)256 }] }] },
            "membership" => state with { Players = [state.Players[0], remote with { Memberships = [membership, membership] }] },
            "revision" => state with { Revision = 0 },
            "duplicate-role" => state with { Controls = [state.Controls[0], state.Controls[0]] },
            "unknown-role" => state with { Controls = [state.Controls[0] with { Entity = new EntityId(999) }] },
            "owner" => state with { Controls = [state.Controls[0] with { Player = new PlayerId(999) }] },
            "view" => state with { Players = [state.Players[0], remote with { ViewedFaction = 0 }] },
            "implicit" => state with { ExplicitControl = false },
            "classification" => state with { PlayerOwnedFactions = [] },
            _ => throw new InvalidOperationException(),
        };
        using var target = World();
        Reserve(target, valid);
        Assert.Throws<InvalidDataException>(() => target.Players.Restore(state, 3));
        Assert.Single(target.Players.All);
        Assert.Empty(target.Players.Find(PlayerId.Local)!.Memberships);
        Assert.Empty(target.Factions.All);
        Assert.False(target.Players.RequiresExplicitControl);
        target.Players.Restore(valid, 3); // Rejection leaves the candidate usable.
        Assert.Equal(2, target.Players.All.Count());
    }

    [Fact]
    public void RestoreIsRefusedAfterConnectionAdmissionAndCannotBeRepeated()
    {
        using var source = World();
        var state = State(source);
        using var admitted = World();
        Reserve(admitted, state);
        admitted.Authority.Close(admitted.Authority.Open(PlayerId.Local));
        Assert.Throws<InvalidOperationException>(() => admitted.Players.Restore(state, 3));
        using var target = World();
        Reserve(target, state);
        target.Players.Restore(state, 3);
        Assert.Throws<InvalidOperationException>(() => target.Players.Restore(state, 3));
    }

    [Fact]
    public void LoadedRoleFactionMustAgreeBeforeAnyAuthorityIsRestored()
    {
        using var source = World();
        var state = State(source);
        using var target = World();
        Reserve(target, state);
        target.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = 1, Entity = state.Controls[0].Entity }, 0);
        Assert.Throws<InvalidDataException>(() => target.Players.Restore(state, 3));
        Assert.Single(target.Players.All);
    }

    [Fact]
    public void DeadRoleOwnershipIsRestoredWithoutMakingThePlayerEligibleForControl()
    {
        using var source = World();
        var state = State(source);
        using var target = World();
        Reserve(target, state);
        var grant = state.Controls.Single();
        target.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = grant.Faction, Entity = grant.Entity, IsPlayer = true, DiedTick = 0 }, 0);
        target.Players.Restore(state, 3);
        Assert.Equal(grant, Assert.Single(target.Players.KnownRoles(grant.Player)));
        Assert.False(target.Players.Validate(grant));
        Assert.False(target.Authority.BeginSynchronization(target.Authority.Open(grant.Player)));
    }
}
