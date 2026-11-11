using System.Numerics;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
namespace Meitou.Tests.Engine;

public sealed class ComponentViewBehaviorTests
{
    [Fact]
    public void MotionRetainsPreparedAppearanceWhileShapeOutfitAndViewWithdrawalReplaceIt()
    {
        var db = WorldViewCodecTests.Database(); var view = WorldViewCodecTests.View(db); var peer = new GameReplicationFixture(new(db));
        peer.Apply(peer.Capture(view)); var appearance = peer.Client.View!.Snapshot.Characters[0].Appearance;
        for (ulong publication = 2; publication < 22; publication++)
        {
            view = view with { Publication = publication, Snapshot = view.Snapshot with { Tick = (long)publication + 4, Characters = [view.Snapshot.Characters[0] with { Position = new(publication,0,0) }] } };
            var delta = peer.Capture(view); peer.Apply(delta);
            Assert.False(GameReplicationFixture.Changes(delta,"Appearance")); Assert.Same(appearance,peer.Client.View!.Snapshot.Characters[0].Appearance);
            var motion = Assert.Single(delta.Motion).Record;
            Assert.True(peer.Client.ApplyMotion(new(new(11,22,publication,(ulong)view.Snapshot.Tick,publication),[motion])));
        }
        view.Snapshot.Characters[0].Appearance!.Parts.Clear(); view.Snapshot.Characters[0].Appearance!.Shape = view.Snapshot.Characters[0].Appearance!.Shape! with { Sliders = new Dictionary<string,float> { ["Height"] = 140 } };
        peer.Apply(peer.Capture(view with { Publication = 22 }));
        var changed = peer.Client.View!.Snapshot.Characters[0].Appearance;
        Assert.NotSame(appearance,changed); Assert.Empty(changed!.Parts); Assert.Equal(140,changed.Shape!.Sliders["Height"]);
        var nextView = new GameReplicationFixture(new(db)); nextView.Apply(nextView.Capture(view with { Publication = 23 }));
        Assert.NotSame(changed,nextView.Client.View!.Snapshot.Characters[0].Appearance);
    }
    [Fact]
    public void PrivateInventoryJobsAndFactionFundsCannotReturnFromAnOldBaseline()
    {
        var db = WorldViewCodecTests.Database(); var view = WorldViewCodecTests.View(db); var peer = new GameReplicationFixture(new(db));
        var baseline = peer.Capture(view); peer.Apply(baseline);
        var changed = view with { Publication = 2, Snapshot = view.Snapshot with { Characters = [view.Snapshot.Characters[0] with { ItemGrid = null, Jobs = [] }] }, Factions = [view.Factions[0] with { Money = null }] };
        peer.Apply(peer.Capture(changed)); peer.Apply(baseline);
        Assert.Null(peer.Client.View!.Snapshot.Characters[0].ItemGrid); Assert.Empty(peer.Client.View.Snapshot.Characters[0].Jobs); Assert.Null(peer.Client.View.Factions[0].Money);
    }
    [Fact]
    public void CorruptFramesNeverPartiallyUpdateAndForeignRecipientsFailClosed()
    {
        var db = WorldViewCodecTests.Database(); var view = WorldViewCodecTests.View(db); var peer = new GameReplicationFixture(new(db));
        peer.Apply(peer.Capture(view)); var before = peer.Client.View;
        var delta = peer.Capture(view with { Publication = 2 }); var corrupt = (byte[])delta.Frames[0].Clone(); corrupt[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => peer.Client.Apply(corrupt)); Assert.Same(before,peer.Client.View);
        var foreign = new GameReplicationFixture(new(db)); var publication = foreign.Capture(view with { Player = new(4), Publication = 3 });
        Assert.Throws<InvalidDataException>(() => peer.Apply(publication)); Assert.Null(peer.Client.View);
    }
    [Fact]
    public void DeliveredBaselineCannotActivateAfterMembershipWasRevokedAndRestored()
    {
        using var world = new Meitou.Simulation.World(new WorldSettings { PublishSnapshots = false },new OpenGroundWalkability((_,_)=>0),[]);
        var player = new PlayerId(2); world.Players.Register(player); world.Players.SetMembership(player,7,FactionPermission.All);
        var id = world.Characters.Spawn(new CharacterHot(),new CharacterCold { Faction = 7, IsPlayer = true },0); world.Players.AssignInitial(player,id);
        var connection = world.Authority.Open(player); Assert.True(world.Authority.BeginSynchronization(connection));
        var first = world.Authority.CaptureView(connection,new(),new()); Assert.NotNull(first);
        var peer = new GameReplicationFixture(new(WorldViewCodecTests.Database())); peer.Apply(peer.Capture(first!));
        world.Players.SetMembership(player,7,FactionPermission.ReadFunds); world.Players.SetMembership(player,7,FactionPermission.All);
        Assert.False(world.Authority.CanRetainPublishedView(connection,first.Publication)); Assert.False(world.Authority.ActivateAfterView(connection,first.Publication));
        var current = world.Authority.CaptureView(connection,new(),new()); Assert.NotNull(current); peer.Apply(peer.Capture(current!));
        Assert.True(world.Authority.ActivateAfterView(connection,current.Publication));
    }
}
