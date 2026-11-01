using System.Numerics;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class PlayerMessageTests
{
    [Fact]
    public void PrivateHistoryIsRecipientScopedBoundedAndIndependentOfLegacyQueueConsumption()
    {
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
        var a = world.Players.Register(new(2)); var b = world.Players.Register(new(3));
        world.Players.SetMembership(a.Id, 7, FactionPermission.All); world.Players.SetMembership(b.Id, 7, FactionPermission.All);
        world.Player.Faction = 7; // an imported local slot is not implicitly a member in explicit-control mode
        ulong before = world.StateHash();
        world.Post(a.Id, "A private"); world.Said(a.Id, "NPC", "A dialogue", false);
        world.Post(b.Id, "B private"); world.PostFaction(7, "Member notice");
        world.Post("Unattributed diagnostic"); world.Said("Diagnostic", "Unattributed dialogue", false);
        var first = a.State.Messages.Capture();
        Assert.Equal(new[] { "A private", "A dialogue", "Member notice" }, first.Items.Select(m => m.Text));
        Assert.Equal(new[] { "B private", "Member notice" }, b.State.Messages.Capture().Items.Select(m => m.Text));
        Assert.Empty(world.Player.Messages.Capture().Items);
        Assert.Equal(before, world.StateHash());
        Assert.Equal(2, world.TakeMessages(a.Id).Count);
        Assert.Equal(first, a.State.Messages.Capture());
        for (int i = 0; i < 250; i++) world.Post(a.Id, "Message " + i);
        var retained = a.State.Messages.Capture();
        Assert.Equal(200, retained.Items.Count); Assert.Equal(253ul, retained.HighWater); Assert.Equal(54ul, retained.Items[0].Id);
        Assert.Equal(3, first.Items.Count); Assert.Equal(200, world.TakeMessages(a.Id).Count);
        world.Post(PlayerId.Local, "Explicit local recipient");
        Assert.Equal("Explicit local recipient", Assert.Single(world.Player.Messages.Capture().Items).Text);
    }

    [Fact]
    public void PublicSpeechUsesVisibilityAndViewerOwnershipInsteadOfTheLocalSlot()
    {
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
        var a = world.Players.Register(new(2)); var b = world.Players.Register(new(3));
        world.Players.SetMembership(a.Id, 7, FactionPermission.All); world.Players.SetMembership(b.Id, 8, FactionPermission.All);
        CharacterId Role(WorldPlayer player)
        {
            var id = world.Characters.Spawn(new() { Position = Vector3.Zero }, new() { Faction = player.State.Faction, IsPlayer = true }, 0);
            world.Players.AssignInitial(player.Id, id); return id;
        }
        var speaker = Role(a); _ = Role(b);
        var ca = world.Authority.Open(a.Id); var cb = world.Authority.Open(b.Id);
        Assert.True(world.Authority.BeginSynchronization(ca)); Assert.True(world.Authority.BeginSynchronization(cb));
        world.SaidPublic(speaker, "Speaker", "Visible speech", true);
        var av = world.Authority.CaptureView(ca, new(), new(100))!;
        var bv = world.Authority.CaptureView(cb, new(), new(100))!;
        Assert.True(Assert.Single(av.Events.Speech).Line.Player);
        Assert.False(Assert.Single(bv.Events.Speech).Line.Player);
        Assert.Empty(world.Player.Messages.Capture().Items); Assert.Empty(a.State.Messages.Capture().Items);
        Assert.Empty(world.Authority.CaptureView(cb, new([]), new(100))!.Events.Speech);
    }
}
