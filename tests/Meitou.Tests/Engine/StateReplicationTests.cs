using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public class StateReplicationTests
{
    [Fact]
    public void MovingSnapshotsReuseUnchangedAppearancesButOutfitChangesAndWithdrawalInvalidateThem()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        client.Receive(server.Publish(view).Payload); server.Acknowledge(client.Acknowledgement!);
        var preparedLook = client.Current!.Snapshot.Characters[0].Appearance;
        for (ulong publication = 2; publication < 22; publication++)
        {
            view = view with { Publication = publication, Snapshot = view.Snapshot with
                { Characters = [view.Snapshot.Characters[0] with { Position = new Vector3(publication, 0, 0) }] } };
            Assert.Equal(ReplicaResult.Applied, client.Receive(server.Publish(view).Payload));
            Assert.Same(preparedLook, client.Current!.Snapshot.Characters[0].Appearance);
            Assert.True(server.Acknowledge(client.Acknowledgement!));
        }
        view.Snapshot.Characters[0].Appearance!.Parts.Clear();
        view = view with { Publication = 22 };
        client.Receive(server.Publish(view).Payload);
        Assert.NotSame(preparedLook, client.Current!.Snapshot.Characters[0].Appearance);
        Assert.Empty(client.Current.Snapshot.Characters[0].Appearance!.Parts);
        client.Withdraw(23); server.Withdraw(23);
        view = WorldViewCodecTests.View(db) with { World = identity.World, Publication = 24 };
        client.Receive(server.Publish(view).Payload);
        Assert.NotSame(preparedLook, client.Current!.Snapshot.Characters[0].Appearance);
    }

    [Fact]
    public void BaselineDeltaAndRevocationReplacePrivateFieldsRatherThanMergingThem()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        var first = server.Publish(view); Assert.True(first.Baseline);
        Assert.Equal(ReplicaResult.Applied, client.Receive(first.Payload)); Assert.True(server.Acknowledge(client.Acknowledgement!));
        Assert.NotNull(Assert.Single(client.Current!.Snapshot.Characters).ItemGrid);
        var character = Assert.Single(view.Snapshot.Characters);
        var changed = view with { Publication = 2, Snapshot = view.Snapshot with { Characters = [character with { Position = new Vector3(15, 20, 30), ItemGrid = null, Jobs = [] }] },
            Factions = [view.Factions[0] with { Money = null }] };
        var second = server.Publish(changed); Assert.False(second.Baseline);
        var patch = StatePatchProtocol.Decode(second.Payload); Assert.Equal(1ul, patch.BasePublication);
        Assert.Equal(ReplicaResult.Applied, client.Receive(second.Payload));
        var actual = Assert.Single(client.Current!.Snapshot.Characters);
        Assert.Null(actual.ItemGrid); Assert.Empty(actual.Jobs); Assert.Equal(new Vector3(15, 20, 30), actual.Position);
        Assert.Null(Assert.Single(client.Current.Factions).Money);
        Assert.Equal(ReplicaResult.Stale, client.Receive(first.Payload)); Assert.Null(Assert.Single(client.Current.Snapshot.Characters).ItemGrid);
    }

    [Fact]
    public void SuccessiveBaselinesRetainTheOlderAcknowledgedBaseForInFlightDeltas()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        var first = server.Publish(view); var second = server.Publish(view with { Publication = 2 });
        Assert.True(second.Baseline);
        client.Receive(first.Payload); var delayed = client.Acknowledgement!;
        client.Receive(second.Payload); Assert.True(server.Acknowledge(delayed));
        var third = server.Publish(view with { Publication = 3, Factions = [view.Factions[0] with { Money = null }] });
        Assert.Equal(1ul, StatePatchProtocol.Decode(third.Payload).BasePublication);
        Assert.Equal(ReplicaResult.Applied, client.Receive(third.Payload));
        Assert.Null(Assert.Single(client.Current!.Factions).Money); Assert.Equal(3ul, client.Current.Publication);
    }

    [Fact]
    public void LostAcknowledgementAndReorderedUpdatesUseTheAcknowledgedBase()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        client.Receive(server.Publish(view).Payload); Assert.True(server.Acknowledge(client.Acknowledgement!));
        var second = server.Publish(view with { Publication = 2 }); client.Receive(second.Payload); // its ACK is lost
        var third = server.Publish(view with { Publication = 3 });
        var fourth = server.Publish(view with { Publication = 4 });
        Assert.Equal(1ul, StatePatchProtocol.Decode(fourth.Payload).BasePublication);
        Assert.Equal(ReplicaResult.Applied, client.Receive(fourth.Payload));
        Assert.Equal(ReplicaResult.Stale, client.Receive(third.Payload));
        Assert.True(server.Acknowledge(client.Acknowledgement!));
        Assert.Equal(4ul, StatePatchProtocol.Decode(server.Publish(view with { Publication = 5 }).Payload).BasePublication);
        Assert.False(server.Acknowledge(client.Acknowledgement! with { Publication = 99 }));
    }

    [Fact]
    public void MissingBaseClearsUsableStateAndOldBaselinesCannotResurrectRevokedDetails()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        var first = server.Publish(view); client.Receive(first.Payload); server.Acknowledge(client.Acknowledgement!);
        for (ulong number = 2; number <= 10; number++)
        {
            var result = client.Receive(server.Publish(view with { Publication = number }).Payload);
            if (number < 10) Assert.Equal(ReplicaResult.Applied, result);
            else Assert.Equal(ReplicaResult.NeedsBaseline, result);
        }
        Assert.True(client.NeedsBaseline); Assert.Null(client.Current); Assert.Null(client.Acknowledgement);
        Assert.Equal(ReplicaResult.Stale, client.Receive(first.Payload)); Assert.Null(client.Current);
        server.RequestBaseline();
        var restored = server.Publish(view with { Publication = 11 }); Assert.True(restored.Baseline);
        Assert.Equal(ReplicaResult.Applied, client.Receive(restored.Payload)); Assert.False(client.NeedsBaseline);
    }

    [Fact]
    public void DeparturesDistinguishLossOfScopeFromConfirmedDestruction()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        var firstCharacter = view.Snapshot.Characters[0]; var other = firstCharacter with { Id = new(4, 8), ItemGrid = null };
        view = view with { Snapshot = view.Snapshot with { Characters = [firstCharacter, other] } };
        client.Receive(server.Publish(view).Payload); server.Acknowledge(client.Acknowledgement!);
        var next = view with { Publication = 2, Snapshot = view.Snapshot with { Characters = [] } };
        client.Receive(server.Publish(next, id => id == other.Id).Payload);
        Assert.Empty(client.Current!.Snapshot.Characters);
        Assert.Contains(client.LastRemovals, r => r.Key == "c:1:2" && r.Reason == StateRemovalReason.OutOfScope);
        Assert.Contains(client.LastRemovals, r => r.Key == "c:4:8" && r.Reason == StateRemovalReason.Destroyed);
    }

    [Fact]
    public void CorruptionOrAnotherRecipientsStateCannotPartiallyUpdateAReplica()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        client.Receive(server.Publish(view).Payload); server.Acknowledge(client.Acknowledgement!);
        var second = server.Publish(view with { Publication = 2 });
        var node = JsonNode.Parse(second.Payload)!; node["Hash"] = new string('0', 64);
        Assert.Throws<InvalidDataException>(() => client.Receive(Encoding.UTF8.GetBytes(node.ToJsonString())));
        Assert.Null(client.Current); Assert.True(client.NeedsBaseline);
        var foreignView = view with { Player = new(4), Publication = 3 };
        var foreign = new ServerStateReplicator(ReplicationIdentity.Of(foreignView), codec).Publish(foreignView);
        Assert.Throws<InvalidDataException>(() => client.Receive(foreign.Payload)); Assert.Null(client.Current);
    }
}
