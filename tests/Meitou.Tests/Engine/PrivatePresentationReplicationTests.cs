using System.Numerics;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Tests.Engine;

public class PrivatePresentationReplicationTests
{
    [Fact]
    public void PrivateMessageHistoryIsAnIndependentComponentAndMalformedIdentifiersAreRejected()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db);
        var message = new PlayerMessage(1, PlayerMessageKind.Notice, "", "Private notice");
        var view = WorldViewCodecTests.View(db) with { Messages = new(1, [message]) };
        var peer = new GameReplicationFixture(codec);
        peer.Apply(peer.Capture(view));
        var next = peer.Capture(view with { Publication = 2, GameHours = view.GameHours + 1 });
        Assert.False(GameReplicationFixture.Changes(next, "Messages"));
        peer.Apply(next);
        Assert.Equal(message, Assert.Single(peer.Client.View!.Messages.Items));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Messages = new(0, [message]) }));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Messages = new(1, [message, message]) }));
    }

    static ProspectReport Report() => new(1, Vector3.Zero, "Owner", 0, "Region", 0, 400, [], [new("Water", .5f), new("Iron", 1)],
        new(2, 0, 0, 100, [1, 2, 3, 4], [new float[] { 0, .5f, 1, 0 }, null], [Array.Empty<ProspectRock>(), new[] { new ProspectRock(1, 2, 1) }]));

    [Fact]
    public void SurveyMapIsAnIndependentDeltaComponentAndIsRemovedWhenWithdrawn()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db);
        var view = WorldViewCodecTests.View(db) with { Prospected = Report() };
        var peer = new GameReplicationFixture(codec);
        var first = peer.Capture(view);
        peer.Apply(first);
        Assert.Equal(Report().Map!.Heights, peer.Client.View!.Prospected!.Map!.Heights);
        Assert.Null(peer.Client.View.Prospected.Map.Fields[1]); Assert.Single(peer.Client.View.Prospected.Map.Rocks[1]);
        var second = peer.Capture(view with { Publication = 2, GameHours = view.GameHours + 1 });
        Assert.False(GameReplicationFixture.Changes(second, "Prospected"));
        peer.Apply(second); Assert.NotNull(peer.Client.View!.Prospected);
        var third = peer.Capture(view with { Publication = 3, Prospected = null });
        peer.Apply(third); Assert.Null(peer.Client.View!.Prospected);
    }

    [Fact]
    public void MalformedSurveyGridsAndEventIdentitiesAreRejectedBeforeTheyReachTheUi()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Prospected = Report() with { Map = Report().Map! with { Size = 3 } } }));
        var floater = new FloaterEvent(1, Floater.Text(view.Snapshot.Characters[0].Id, Vector3.Zero, "private"));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Events = new(0, [floater]) }));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Events = new(1, [floater, floater]) }));
        var sound = new SoundEventEntry(1, new(SoundKind.Hit, floater.Value.Target, CharacterId.None, Vector3.Zero));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Events = new(1, [floater]) { Sounds = [sound] } }));
        var peer = new GameReplicationFixture(codec);
        peer.Apply(peer.Capture(view with { Prospected = Report(), Events = new(2, [floater]) { Sounds = [sound with { Id = 2 }] } }));
        var decoded = peer.Client.View!;
        Assert.Equal("private", Assert.Single(decoded.Events.Floaters).Value.Part); Assert.NotNull(decoded.Prospected!.Map);
        Assert.Equal(SoundKind.Hit, Assert.Single(decoded.Events.Sounds).Value.Kind);
    }
}
