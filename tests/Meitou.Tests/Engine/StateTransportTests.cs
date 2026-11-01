using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Meitou.Networking;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Engine;

public class StateTransportTests
{
    static (ReplicationIdentity Identity, WorldViewCodec Codec, PlayerWorldView View) LargeView()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        view = view with { Snapshot = view.Snapshot with
            { Characters = Enumerable.Range(1, 400).Select(i => view.Snapshot.Characters[0] with { Id = new(i, 2) }).ToArray() } };
        return (ReplicationIdentity.Of(view), codec, view);
    }

    [Fact]
    public void LargeBaselineAcceptsOutOfOrderAndDuplicateChunksOnlyAfterCompleteValidation()
    {
        var (identity, codec, view) = LargeView();
        var frames = StateTransport.Split(identity, new ServerStateReplicator(identity, codec).Publish(view));
        Assert.True(frames.Count > 1); Assert.All(frames, f => Assert.True(f.Payload.Length <= WireFrame.MaximumPayload));
        var receiver = new ClientStateReceiver(identity, codec);
        Assert.Null(receiver.Receive(frames[^1])); Assert.Null(receiver.Receive(frames[^1])); Assert.Null(receiver.Replica.Current);
        for (int i = frames.Count - 2; i >= 0; i--)
        {
            var result = receiver.Receive(frames[i]);
            if (i > 0) Assert.Null(result); else Assert.Equal(ReplicaResult.Applied, result);
        }
        Assert.Equal(400, receiver.Replica.Current!.Snapshot.Characters.Count);
        Assert.NotNull(receiver.Acknowledgement); Assert.Null(receiver.Receive(frames[0]));
    }

    [Fact]
    public void ChunkSizeConflictsAndWrongRecipientsAreRejectedWithoutRetainingPartialBuffers()
    {
        var (identity, codec, view) = LargeView();
        var frames = StateTransport.Split(identity, new ServerStateReplicator(identity, codec).Publish(view));
        var assembler = new StateChunkAssembler(identity);
        Assert.Null(assembler.Receive(frames[0])); Assert.True(assembler.BufferedBytes > 0);
        byte[] changed = frames[0].Payload.ToArray(); changed[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => assembler.Receive(frames[0] with { Payload = changed })); Assert.Equal(0, assembler.BufferedBytes);
        var oversized = frames[0].Payload.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(48), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => assembler.Receive(frames[0] with { Payload = oversized }));
        Assert.Equal(0, assembler.BufferedBytes);
        var other = new StateChunkAssembler(identity with { Player = new(99) });
        Assert.Throws<InvalidDataException>(() => other.Receive(frames[0])); Assert.Equal(0, other.BufferedBytes);
    }

    [Fact]
    public void NewPublicationSupersedesIncompleteOldTransferAndWithdrawalCannotBeUndoneByLateChunks()
    {
        var (identity, codec, view) = LargeView(); var server = new ServerStateReplicator(identity, codec);
        var first = StateTransport.Split(identity, server.Publish(view));
        var second = StateTransport.Split(identity, server.Publish(view with { Publication = 2 }));
        var receiver = new ClientStateReceiver(identity, codec);
        Assert.Null(receiver.Receive(first[0])); Assert.Null(receiver.Receive(second[0]));
        foreach (var frame in first.Skip(1)) Assert.Null(receiver.Receive(frame));
        foreach (var frame in second.Skip(1)) receiver.Receive(frame);
        Assert.Equal(2ul, receiver.Replica.Current!.Publication);
        Assert.Equal(ReplicaResult.NeedsBaseline, receiver.Receive(StateTransport.EncodeWithdrawal(identity, 3)));
        Assert.Null(receiver.Replica.Current);
        foreach (var frame in second) Assert.Null(receiver.Receive(frame));
        Assert.Null(receiver.Replica.Current);
        var request = StateTransport.DecodeResync(receiver.ResyncRequest()); Assert.Equal(3ul, request.AfterPublication); Assert.Equal(identity, request.Identity);
    }

    [Fact]
    public void BaselineAcknowledgementCannotOpenControlAfterARevokedAndRestoredMembership()
    {
        var codec = new WorldViewCodec(WorldViewCodecTests.Database());
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), []);
        var player = new PlayerId(2); world.Players.Register(player); world.Players.SetMembership(player, 7, FactionPermission.All);
        var role = world.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = 7, IsPlayer = true }, 0);
        world.Players.AssignInitial(player, role);
        var connection = world.Authority.Open(player); Assert.True(world.Authority.BeginSynchronization(connection));
        var publisher = new AuthoritativeStatePublisher(world.Authority, connection, codec);
        var receiver = new ClientStateReceiver(new(connection.World, player, connection.Generation), codec);
        foreach (var frame in publisher.Capture(new(), new())!) receiver.Receive(frame);
        world.Players.SetMembership(player, 7, FactionPermission.ReadFunds); world.Players.SetMembership(player, 7, FactionPermission.All);
        Assert.True(publisher.Acknowledge(receiver.Acknowledgement!));
        Assert.Equal(AdmissionPhase.Synchronizing, world.Authority.GetPhase(connection));
        Assert.False(world.Authority.ActivateAfterView(connection, 999));
        foreach (var frame in publisher.Capture(new(), new())!) receiver.Receive(frame);
        Assert.True(publisher.Acknowledge(receiver.Acknowledgement!));
        Assert.Equal(AdmissionPhase.Active, world.Authority.GetPhase(connection));
    }

    [Fact]
    public async Task TcpWorldBridgePublishesRemovalMeaningAndWithdrawsOnLossOfAdmission()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db);
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), []);
        var player = new PlayerId(2); world.Players.Register(player); world.Players.SetMembership(player, 7, FactionPermission.All);
        var role = world.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = 7, IsPlayer = true }, 0);
        world.Players.AssignInitial(player, role);
        var unloaded = world.Characters.Spawn(new CharacterHot { Position = new Vector3(10, 0, 0) }, new CharacterCold { Faction = 8 }, 0);
        var destroyed = world.Characters.Spawn(new CharacterHot { Position = new Vector3(20, 0, 0) }, new CharacterCold { Faction = 8 }, 0);
        var connection = world.Authority.Open(player); Assert.True(world.Authority.BeginSynchronization(connection));
        var publisher = new AuthoritativeStatePublisher(world.Authority, connection, codec);
        var identity = new ReplicationIdentity(connection.World, connection.Player, connection.Generation);
        var receiver = new ClientStateReceiver(identity, codec);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using var server = await listener.AcceptTcpClientAsync(deadline.Token);
        async Task Transfer(IReadOnlyList<WireFrame> frames)
        {
            foreach (var frame in frames)
            {
                await WireFrame.WriteAsync(server.GetStream(), frame, deadline.Token);
                receiver.Receive((await WireFrame.ReadAsync(client.GetStream(), deadline.Token))!);
            }
        }
        var first = publisher.Capture(new(), new())!; await Transfer(first);
        Assert.Equal(3, receiver.Replica.Current!.Snapshot.Characters.Count);
        await WireFrame.WriteAsync(client.GetStream(), receiver.Acknowledgement!, deadline.Token);
        Assert.True(publisher.Acknowledge((await WireFrame.ReadAsync(server.GetStream(), deadline.Token))!));
        Assert.Equal(AdmissionPhase.Active, world.Authority.GetPhase(connection));
        world.Characters.Remove(unloaded, unloaded: true); world.Characters.Remove(destroyed);
        await Transfer(publisher.Capture(new(), new())!);
        Assert.Single(receiver.Replica.Current!.Snapshot.Characters);
        Assert.Contains(receiver.Replica.LastRemovals, r => r.Key == $"c:{unloaded.Slot}:{unloaded.Generation}" && r.Reason == StateRemovalReason.OutOfScope);
        Assert.Contains(receiver.Replica.LastRemovals, r => r.Key == $"c:{destroyed.Slot}:{destroyed.Generation}" && r.Reason == StateRemovalReason.Destroyed);
        world.Players.RemoveMembership(player, 7);
        await Transfer(publisher.Capture(new(), new())!); Assert.Null(receiver.Replica.Current); Assert.Null(receiver.Acknowledgement);
        await Transfer(first); Assert.Null(receiver.Replica.Current);
        world.Players.SetMembership(player, 7, FactionPermission.All); world.Players.AssignInitial(player, role);
        Assert.True(world.Authority.BeginSynchronization(connection));
        Assert.True(publisher.RequestBaseline(receiver.ResyncRequest()));
        await Transfer(publisher.Capture(new(), new())!); Assert.Single(receiver.Replica.Current!.Snapshot.Characters);
        world.Authority.Open(player); Assert.Null(publisher.Capture(new(), new()));
    }
}
