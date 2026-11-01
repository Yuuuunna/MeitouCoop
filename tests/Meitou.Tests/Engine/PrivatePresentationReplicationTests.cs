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
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        Assert.Equal(ReplicaResult.Applied, client.Receive(server.Publish(view).Payload)); Assert.True(server.Acknowledge(client.Acknowledgement!));
        var next = server.Publish(view with { Publication = 2, GameHours = view.GameHours + 1 });
        Assert.DoesNotContain(StatePatchProtocol.Decode(next.Payload).Changes, c => c.Key == "messages");
        Assert.Equal(ReplicaResult.Applied, client.Receive(next.Payload));
        Assert.Equal(message, Assert.Single(client.Current!.Messages.Items));
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
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        var first = server.Publish(view);
        Assert.Equal(ReplicaResult.Applied, client.Receive(first.Payload)); Assert.True(server.Acknowledge(client.Acknowledgement!));
        Assert.Equal(Report().Map!.Heights, client.Current!.Prospected!.Map!.Heights);
        Assert.Null(client.Current.Prospected.Map.Fields[1]); Assert.Single(client.Current.Prospected.Map.Rocks[1]);
        var second = server.Publish(view with { Publication = 2, GameHours = view.GameHours + 1 });
        var patch = StatePatchProtocol.Decode(second.Payload);
        Assert.DoesNotContain(patch.Changes, c => c.Key == "prospect");
        Assert.Equal(ReplicaResult.Applied, client.Receive(second.Payload)); Assert.NotNull(client.Current!.Prospected);
        Assert.True(server.Acknowledge(client.Acknowledgement!));
        var third = server.Publish(view with { Publication = 3, Prospected = null });
        Assert.Equal(ReplicaResult.Applied, client.Receive(third.Payload)); Assert.Null(client.Current!.Prospected);
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
        var decoded = codec.Decode(codec.Encode(view with { Prospected = Report(), Events = new(2, [floater]) { Sounds = [sound with { Id = 2 }] } }));
        Assert.Equal("private", Assert.Single(decoded.Events.Floaters).Value.Part); Assert.NotNull(decoded.Prospected!.Map);
        Assert.Equal(SoundKind.Hit, Assert.Single(decoded.Events.Sounds).Value.Kind);
    }
}
