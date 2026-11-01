using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class WorldPlayersTests
{
    sealed class AtCommit : ITickSystem
    {
        public Action<SimWorld>? Change;
        public void SlowWorld(SimWorld world) { var change = Change; Change = null; change?.Invoke(world); }
    }

    [Fact]
    public void BoundaryQueriesSeeRolesSpawnedAndRemovedDuringTheLastTick()
    {
        var commit = new AtCommit();
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), [commit]);
        var player = world.Players.Register(new PlayerId(2)); world.Players.SetMembership(player.Id, 6, FactionPermission.All);
        CharacterId id = CharacterId.None;
        commit.Change = w => { id = Spawn(w, 6); w.Players.AssignInitial(player.Id, id); };
        world.RunTick();
        Assert.True(world.Players.CanControl(player.Id, id));
        Assert.True(world.Characters.TryResolveNext(id, out _)); Assert.True(world.Characters.Current[id.Slot].Alive);
        var original = id;
        commit.Change = w => { w.Characters.Remove(id); id = Spawn(w, 6); w.Players.AssignInitial(player.Id, id); };
        world.RunTick();
        Assert.NotEqual(original.Generation, id.Generation); Assert.True(world.Players.CanControl(player.Id, id));
        world.Characters.Place(id.Slot, new System.Numerics.Vector3(1, 2, 3), 0);
        Assert.True(world.Players.CanControl(player.Id, id));
        Assert.True(world.Characters.Remove(id));
        var replacement = Spawn(world, 6);
        Assert.NotEqual(id.Generation, replacement.Generation); Assert.False(world.Players.CanControl(player.Id, id));
        id = replacement;
        commit.Change = w => w.Characters.Remove(id);
        world.RunTick();
        Assert.False(world.Characters.TryResolveNext(id, out _)); Assert.False(world.Players.CanControl(player.Id, id));
    }

    static SimWorld MakeWorld() => new(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
    static CharacterId Spawn(SimWorld world, int faction) =>
        world.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = faction, IsPlayer = true }, world.Tick);

    [Fact]
    public void IndependentFactionsKeepFundsAndResearchWhileMembershipChanges()
    {
        using var world = MakeWorld();
        var a = world.Players.Register(new PlayerId(2));
        var b = world.Players.Register(new PlayerId(3));
        world.Players.SetMembership(a.Id, 4, FactionPermission.All);
        world.Players.SetMembership(b.Id, 5, FactionPermission.All);
        a.State.Money = 100;
        b.State.Money = 250;
        a.State.FactionName = "A";
        a.State.FactionState.Research.Complete(SyntheticTown.Database(), "research-a");
        Assert.Equal(250, b.State.Money);
        Assert.Null(b.State.FactionName);
        Assert.Empty(b.State.FactionState.Research.Completed);
        Assert.False(world.Players.SelectFaction(a.Id, 5));

        world.Players.SetMembership(a.Id, 5, FactionPermission.None);
        Assert.True(world.Players.SelectFaction(a.Id, 5));
        Assert.Equal(250, a.State.Money);
        Assert.False(a.HasPermission(5, FactionPermission.SpendFunds));
        Assert.True(world.Players.SelectFaction(a.Id, 4));
        Assert.Equal(100, a.State.Money);
        Assert.Contains("research-a", a.State.FactionState.Research.Completed);
    }

    [Fact]
    public void LocalPlayerUsesTheSameFactionResourcesAsTheRegistry()
    {
        using var world = MakeWorld();
        world.Player.Faction = 3;
        world.Player.Money = 12;
        world.Player.FactionName = "local";
        var faction = world.Factions.Find(3)!;
        Assert.Equal(12, faction.Money);
        Assert.Equal("local", faction.Name);
        Assert.Same(world.Research, faction.Research);
        Assert.Same(world.Player, world.Players.Find(PlayerId.Local)!.State);
        faction.Money = 28;
        Assert.Equal(28, world.Player.Money);
        world.Player.Faction = 4;
        Assert.Equal(0, world.Player.Money);
        Assert.NotSame(world.Research, faction.Research);
        world.Player.Faction = 3;
        Assert.Equal(28, world.Player.Money);
    }

    [Fact]
    public void MembershipAndManagementDoNotGrantCharacterControl()
    {
        using var world = MakeWorld();
        var a = world.Players.Register(new PlayerId(2));
        var b = world.Players.Register(new PlayerId(3));
        world.Players.SetMembership(a.Id, 7, FactionPermission.All);
        world.Players.SetMembership(b.Id, 7, FactionPermission.None);
        var character = Spawn(world, 7);
        Assert.False(world.Players.CanControl(a.Id, character));
        Assert.False(world.Players.CanControl(b.Id, character));
        var original = world.Players.AssignInitial(a.Id, character);
        Assert.True(world.Players.Validate(original));
        Assert.False(world.Players.TryTransfer(b.Id, original, b.Id, out _));
        a.State.Selection.Add(character);
        a.State.InventoryShown.Add(character);
        Assert.True(world.Players.TryTransfer(a.Id, original, b.Id, out var assigned));
        Assert.False(world.Players.Validate(original));
        Assert.True(world.Players.Validate(assigned));
        Assert.Empty(a.State.Selection);
        Assert.Empty(a.State.InventoryShown);
        Assert.False(world.Players.TryTransfer(a.Id, original, a.Id, out _));
        Assert.True(world.Players.TryTransfer(a.Id, assigned, a.Id, out var returned));
        Assert.True(world.Players.Validate(returned));
        Assert.False(world.Players.Validate(original));
    }

    [Fact]
    public void RevokingMembershipInvalidatesControlEvenAfterRejoining()
    {
        using var world = MakeWorld();
        var player = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(player.Id, 6, FactionPermission.None);
        var character = Spawn(world, 6);
        var old = world.Players.AssignInitial(player.Id, character);
        world.Players.RemoveMembership(player.Id, 6);
        Assert.False(player.State.Exists);
        Assert.False(world.Players.Validate(old));
        world.Players.SetMembership(player.Id, 6, FactionPermission.None);
        Assert.False(world.Players.CanControl(player.Id, character));
        var next = world.Players.AssignInitial(player.Id, character);
        Assert.True(world.Players.Validate(next));
        Assert.NotEqual(old.Revision, next.Revision);
    }

    [Fact]
    public void RemovingTheLastMembershipDoesNotRestoreImplicitLocalControlOrEraseTheFaction()
    {
        using var world = MakeWorld();
        world.Players.SetMembership(PlayerId.Local, 6, FactionPermission.All);
        var character = Spawn(world, 6);
        world.Players.AssignInitial(PlayerId.Local, character);
        world.Player.Money = 42;
        world.Players.RemoveMembership(PlayerId.Local, 6);
        Assert.True(world.Players.RequiresExplicitControl);
        Assert.True(world.IsPlayerFaction(6));
        Assert.Equal(42, world.Factions.Find(6)!.Money);
        Assert.Empty(world.Players.KnownRoles(PlayerId.Local));
        world.Players.SetMembership(PlayerId.Local, 6, FactionPermission.None);
        world.Player.Selection.Add(character);
        world.RunTick();
        Assert.Empty(world.Player.Selection);
        Assert.False(world.Players.CanControl(PlayerId.Local, character));
    }

    [Fact]
    public void DeathAndSlotReuseNeverAuthorizeAnotherCharacter()
    {
        using var world = MakeWorld();
        var player = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(player.Id, 6, FactionPermission.All);
        var character = Spawn(world, 6);
        var grant = world.Players.AssignInitial(player.Id, character);
        world.Characters.Cold(character.Slot)!.DiedTick = 0;
        Assert.False(world.Players.Validate(grant));
        Assert.Throws<InvalidOperationException>(() => world.Players.AssignInitial(player.Id, character));
        Assert.True(world.Characters.Remove(character));
        var replacement = Spawn(world, 6);
        Assert.Equal(character.Slot, replacement.Slot);
        Assert.NotEqual(character.Generation, replacement.Generation);
        Assert.False(world.Players.CanControl(player.Id, replacement));
        Assert.False(world.Players.Validate(grant));
    }

    [Fact]
    public void GrantRequiresMembershipInTheActualCharacterFaction()
    {
        using var world = MakeWorld();
        var player = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(player.Id, 6, FactionPermission.All);
        Assert.Throws<InvalidOperationException>(() => world.Players.AssignInitial(player.Id, Spawn(world, 7)));
        Assert.Throws<ArgumentException>(() => world.Players.Register(default));
        Assert.Throws<InvalidOperationException>(() => world.Players.Register(player.Id));
    }

    [Fact]
    public void UnloadingKeepsTheRoleAndRebindingDoesNotGrantTheReusedSlot()
    {
        using var world = MakeWorld();
        var player = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(player.Id, 6, FactionPermission.None);
        var original = Spawn(world, 6);
        var cold = world.Characters.Cold(original.Slot)!;
        var grant = world.Players.AssignInitial(player.Id, original);
        Assert.True(world.Characters.Remove(original, unloaded: true));
        Assert.Single(world.Players.KnownRoles(player.Id));
        Assert.False(world.Players.Validate(grant));
        Assert.False(world.Entities.TryResolve(grant.Entity, out _));

        var stranger = Spawn(world, 6);
        Assert.Equal(original.Slot, stranger.Slot);
        Assert.False(world.Players.CanControl(player.Id, stranger));
        var restored = world.Characters.Spawn(new CharacterHot(), cold, world.Tick);
        Assert.NotEqual(original, restored);
        Assert.True(world.Entities.TryResolve(grant.Entity, out var resolved));
        Assert.Equal(restored, resolved);
        Assert.True(world.Players.Validate(grant));
        Assert.True(world.Players.CanControl(player.Id, restored));
        Assert.False(world.Players.CanControl(player.Id, original));
    }

    [Fact]
    public void RevocationAlsoRemovesUnloadedRolesOnlyForThatMembership()
    {
        using var world = MakeWorld();
        var player = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(player.Id, 6, FactionPermission.None);
        world.Players.SetMembership(player.Id, 7, FactionPermission.None);
        var a = Spawn(world, 6);
        var b = Spawn(world, 7);
        world.Players.AssignInitial(player.Id, a);
        var keep = world.Players.AssignInitial(player.Id, b);
        world.Characters.Remove(a, unloaded: true);
        world.Characters.Remove(b, unloaded: true);
        world.Players.RemoveMembership(player.Id, 6);
        Assert.Equal(keep, Assert.Single(world.Players.KnownRoles(player.Id)));
    }

    [Fact]
    public void ReservedUnloadedIdentitiesAreNotReusedAndDuplicateLiveIdentityIsRejected()
    {
        using var world = MakeWorld();
        world.Entities.Reserve(new EntityId(500));
        var character = Spawn(world, 6);
        var id = world.Entities.Track(character);
        Assert.Equal(501UL, id.Value);
        Assert.Equal(id, world.Entities.Track(character));
        Assert.Throws<InvalidOperationException>(() => world.Characters.Spawn(new CharacterHot(),
            new CharacterCold { Entity = id, Faction = 6 }, world.Tick));
        Assert.Equal(1, world.Characters.Count);
        Assert.True(world.Entities.TryResolve(id, out var resolved));
        Assert.Equal(character, resolved);
    }

    [Fact]
    public void LosingTheLastSelectedRoleDoesNotSelectAnotherPlayersCharacter()
    {
        using var world = MakeWorld();
        var a = world.Players.Register(new PlayerId(2));
        var b = world.Players.Register(new PlayerId(3));
        world.Players.SetMembership(a.Id, 6, FactionPermission.None);
        world.Players.SetMembership(b.Id, 6, FactionPermission.None);
        var theirs = Spawn(world, 6);
        var mine = Spawn(world, 6);
        world.Players.AssignInitial(a.Id, mine);
        world.Players.AssignInitial(b.Id, theirs);
        a.State.Selection.Add(mine);
        world.Characters.Cold(mine.Slot)!.DiedTick = 0;
        world.RunTick();
        Assert.Empty(a.State.Selection);
        Assert.True(world.Players.CanControl(b.Id, theirs));
    }

    [Fact]
    public void WorldHashIncludesRemoteFactionFundsResearchAndGrants()
    {
        using var world = MakeWorld();
        var player = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(player.Id, 6, FactionPermission.All);
        var character = Spawn(world, 6);
        ulong before = world.StateHash();
        player.State.Money++;
        ulong funded = world.StateHash();
        Assert.NotEqual(before, funded);
        player.State.FactionState.Research.Complete(SyntheticTown.Database(), "research-a");
        ulong researched = world.StateHash();
        Assert.NotEqual(funded, researched);
        world.Players.AssignInitial(player.Id, character);
        Assert.NotEqual(researched, world.StateHash());
    }
}
