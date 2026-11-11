using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Construction;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Engine;

public class NetworkCommandTests
{
    static CompatibilityHello Hello() => new(CompatibilityHandshake.Protocol, "test-build", new('A', 64), new('B', 64));
    static WireFrame HelloFrame() => new(WireMessage.Hello, CompatibilityHandshake.Encode(Hello()));
    static ExpectedCommandAuthority Empty() => new([], -1, 0);
    static WireFrame Move(Guid world) => CommandProtocol.Encode(world, 1,
        new MoveOrder([new(5, 9)], new(1.5f, 2, 3)) { Tick = long.MaxValue, Queued = true }, Empty());
    static WireFrame Change(WireFrame frame, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(frame.Payload.Span)!.AsObject(); edit(node);
        return frame with { Payload = Encoding.UTF8.GetBytes(node.ToJsonString()) };
    }

    [Fact]
    public void MovementAndPlacementPreserveDataButHaveNoClientSchedulingOrIssuer()
    {
        var world = Guid.NewGuid(); var frame = Move(world);
        string json = Encoding.UTF8.GetString(frame.Payload.Span);
        Assert.DoesNotContain("Tick", json); Assert.DoesNotContain("Issuer", json); Assert.DoesNotContain("ControlGrants", json);
        var decoded = CommandProtocol.Decode(frame);
        Assert.Equal(world, decoded.World);
        var move = Assert.IsType<MoveOrder>(decoded.Command);
        Assert.Equal(new CharacterId(5, 9), Assert.Single(move.Characters));
        Assert.Equal(new Vector3(1.5f, 2, 3), move.Target); Assert.True(move.Queued);
        Assert.Equal(0, move.Tick); Assert.Null(move.Issuer);
        var plan = new PlannedBuilding("house", new(10, 20, 30), Quaternion.CreateFromYawPitchRoll(1, .1f, .2f), 2, "parent");
        var placed = Assert.IsType<PlaceBuildings>(CommandProtocol.Decode(CommandProtocol.Encode(world, 2,
            new PlaceBuildings([plan]) { Tick = 99, Faction = 7 }, new([], 7, 3))).Command);
        Assert.Equal(plan, Assert.Single(placed.Plans)); Assert.Equal(7, placed.Faction);
    }

    [Theory]
    [InlineData("Issuer")]
    [InlineData("Tick")]
    [InlineData("ControlGrants")]
    [InlineData("FactionGrantRevision")]
    [InlineData("$type")]
    public void ServerOwnedAndUnknownFieldsAreRejected(string field)
    {
        var frame = Change(Move(Guid.NewGuid()), node => node["Body"]![field] = 2);
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(frame));
    }

    [Fact]
    public void DecoderRejectsMalformedScopesDuplicatesMissingDataAndUnboundedNumbers()
    {
        var frame = Move(Guid.NewGuid());
        foreach (var edit in new Action<JsonObject>[]
        {
            n => n["Kind"] = 65535,
            n => n["Request"] = 0,
            n => n["Expected"]!["Roles"] = null,
            n => n["Body"]!["Characters"] = null,
            n => n["Body"]!.AsObject().Remove("Target"),
            n => n["Body"]!["Target"]!["X"] = "Infinity",
            n => n["Body"]!["Target"]!["X"] = 1e100,
        }) Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(Change(frame, edit)));
        string json = Encoding.UTF8.GetString(frame.Payload.Span).Replace("\"X\":1.5", "\"X\":1.5,\"X\":2", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(frame with { Payload = Encoding.UTF8.GetBytes(json) }));
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(frame with { Payload = new byte[WireFrame.MaximumPayload + 1] }));
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Encode(Guid.NewGuid(), 1,
            new SandboxMovement(new(1, 1), 1, 100) { Tick = 0 }, Empty()));
    }

    sealed class Recorder : ITickSystem
    {
        public List<SimCommand> Commands { get; } = [];
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands) => Commands.AddRange(commands);
    }

    [Fact]
    public void CollectionsAndEnumsRejectInvalidValuesWhileOptionalScalarsRemainUsable()
    {
        var world = Guid.NewGuid();
        var frame = CommandProtocol.Encode(world, 1,
            new PlaceBuildings([new("house", Vector3.Zero, Quaternion.Identity)]) { Tick = 0, Faction = 7 }, new([], 7, 1));
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(Change(frame, n => n["Body"]!["Plans"]![0] = null)));
        var move = Move(world);
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(Change(move, n =>
            n["Body"]!["Characters"] = new JsonArray(Enumerable.Range(0, 4097).Select(_ => JsonNode.Parse("{\"Slot\":1,\"Generation\":1}")).ToArray()))));
        var flags = CommandProtocol.Encode(world, 2,
            new OrdersCommand([new(1, 1)], CharacterOrders.Block | CharacterOrders.Hold) { Tick = 0 }, Empty());
        var orders = Assert.IsType<OrdersCommand>(CommandProtocol.Decode(flags).Command);
        Assert.Equal(CharacterOrders.Block | CharacterOrders.Hold, orders.Order); Assert.Null(orders.On);
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(Change(flags, n => n["Body"]!["Order"] = 32768)));
        var formation = CommandProtocol.Encode(world, 3, new SquadFormationCommand(SquadFormation.Caravan) { Tick = 0 }, Empty());
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(Change(formation, n => n["Body"]!["Formation"] = 99)));
    }
    static SimWorld World(Recorder recorder) => new(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), [recorder]);

    [Fact]
    public void FiniteButEnormousPositionsAndDegenerateRotationsAreRejectedBeforeDispatch()
    {
        var world = Guid.NewGuid(); var move = Move(world);
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(Change(move, n => n["Body"]!["Target"]!["X"] = 1e30f)));
        var frame = CommandProtocol.Encode(world, 1,
            new PlaceBuildings([new("house", Vector3.Zero, Quaternion.Identity)]) { Tick = 0, Faction = 7 }, new([], 7, 1));
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(Change(frame, n => n["Body"]!["Plans"]![0]!["Rotation"]!["W"] = 0)));
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(Change(frame, n => n["Body"]!["Plans"]![0]!["Rotation"]!["W"] = 1e30f)));
        Assert.Throws<InvalidDataException>(() => CommandProtocol.Decode(Change(frame, n => n["Body"]!["Plans"]![0]!["Position"]!["Y"] = 1e30f)));
    }

    static (AuthorityConnection Connection, CharacterId Role, ExpectedCommandAuthority Expected) Register(SimWorld world, ulong number)
    {
        var player = new PlayerId(number); int faction = (int)number;
        world.Players.Register(player); world.Players.SetMembership(player, faction, FactionPermission.All);
        var role = world.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = faction, IsPlayer = true }, 0);
        var grant = world.Players.AssignInitial(player, role);
        return (world.Authority.Open(player), role, new([new(grant.Entity, grant.Faction, grant.Revision)], -1, 0));
    }
    static void Activate(SimWorld world, AuthorityConnection connection)
    {
        Assert.True(world.Authority.BeginSynchronization(connection)); Assert.True(world.Authority.Activate(connection));
    }
    static AuthoritativeCommandChannel Channel(SimWorld world, AuthorityConnection connection)
    {
        var channel = new AuthoritativeCommandChannel(Hello(), world.Authority);
        Assert.Equal(CompatibilityFailure.None, channel.ReceiveHello(HelloFrame()));
        channel.BindAuthenticated(connection); return channel;
    }

    [Fact]
    public async Task CompatibilityAuthenticationAndAdmissionAreSeparateGates()
    {
        var recorder = new Recorder(); using var world = World(recorder);
        var (connection, role, expected) = Register(world, 2);
        using var channel = new AuthoritativeCommandChannel(Hello(), world.Authority);
        var frame = CommandProtocol.Encode(connection.World, 1, new SelectCommand([role]) { Tick = 0 }, expected);
        Assert.Throws<InvalidOperationException>(() => channel.BindAuthenticated(connection));
        channel.ReceiveHello(HelloFrame());
        Assert.Throws<InvalidOperationException>(() => { _ = channel.ReceiveCommand(frame); });
        channel.BindAuthenticated(connection);
        Assert.Equal(AuthorityStatus.NotActive, (await channel.ReceiveCommand(frame)).Status);
        Activate(world, connection);
        Assert.Throws<InvalidDataException>(() => { _ = channel.ReceiveCommand(Change(frame, n => n["World"] = Guid.NewGuid())); });
        var accepted = channel.ReceiveCommand(frame); world.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.Evaluated, (await accepted).Status);
        Assert.Equal(connection.Player, Assert.Single(recorder.Commands).Issuer);
    }

    [Fact]
    public async Task ReconnectCancelsOldQueuedCommandsAndOldChannelCannotCloseNewConnection()
    {
        var recorder = new Recorder(); using var world = World(recorder);
        var (old, role, expected) = Register(world, 2); Activate(world, old);
        using var oldChannel = Channel(world, old);
        var frame = CommandProtocol.Encode(old.World, 1, new SelectCommand([role]) { Tick = 0 }, expected);
        var pending = oldChannel.ReceiveCommand(frame);
        var current = world.Authority.Open(old.Player); Activate(world, current);
        using var currentChannel = Channel(world, current);
        oldChannel.Dispose();
        Assert.Equal(AdmissionPhase.Active, world.Authority.GetPhase(current));
        world.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.InvalidConnection, (await pending).Status); Assert.Empty(recorder.Commands);
        var accepted = currentChannel.ReceiveCommand(Change(frame, n => n["Request"] = 2));
        world.ApplyPausedInterfaceCommands(); Assert.Equal(AuthorityStatus.Evaluated, (await accepted).Status);
        Assert.Single(recorder.Commands);
    }

    [Fact]
    public async Task ThreeTcpPeersKeepIdentityIsolatedAndReplayDoesNotExecuteTwice()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var recorder = new Recorder(); using var world = World(recorder);
        var registrations = Enumerable.Range(2, 3).Select(n => Register(world, (ulong)n)).ToArray();
        foreach (var r in registrations) Activate(world, r.Connection);
        var peers = new List<(TcpClient Client, TcpClient Server, AuthoritativeCommandChannel Channel)>();
        var cleanup = new List<IDisposable>();
        try
        {
            foreach (var r in registrations)
            {
                var client = new TcpClient(); cleanup.Add(client);
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
                var server = await listener.AcceptTcpClientAsync(deadline.Token); cleanup.Add(server);
                var channel = new AuthoritativeCommandChannel(Hello(), world.Authority); cleanup.Add(channel);
                await WireFrame.WriteAsync(client.GetStream(), HelloFrame(), deadline.Token);
                Assert.Equal(CompatibilityFailure.None, channel.ReceiveHello((await WireFrame.ReadAsync(server.GetStream(), deadline.Token))!));
                channel.BindAuthenticated(r.Connection);
                peers.Add((client, server, channel));
            }
            var pending = new List<Task<AuthorityReceipt>>();
            var frames = registrations.Select(r => CommandProtocol.Encode(r.Connection.World, 1,
                new SelectCommand([r.Role]) { Tick = long.MaxValue }, r.Expected)).ToArray();
            for (int i = 0; i < peers.Count; i++)
            {
                await WireFrame.WriteAsync(peers[i].Client.GetStream(), frames[i], deadline.Token);
                pending.Add(peers[i].Channel.ReceiveCommand((await WireFrame.ReadAsync(peers[i].Server.GetStream(), deadline.Token))!));
            }
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            world.ApplyPausedInterfaceCommands();
            for (int i = 0; i < peers.Count; i++)
            {
                var r = registrations[i]; var peer = peers[i];
                var outgoing = peer.Client.GetStream(); var incoming = peer.Server.GetStream();
                await WireFrame.WriteAsync(incoming, CommandProtocol.EncodeReceipt(r.Connection.World, await pending[i]), deadline.Token);
                var receipt = CommandProtocol.DecodeReceipt((await WireFrame.ReadAsync(outgoing, deadline.Token))!);
                Assert.Equal(r.Connection.World, receipt.World); Assert.Equal(AuthorityStatus.Evaluated, receipt.Receipt.Status);
                await WireFrame.WriteAsync(outgoing, frames[i], deadline.Token);
                Assert.Same(pending[i], peer.Channel.ReceiveCommand((await WireFrame.ReadAsync(incoming, deadline.Token))!));
                var other = registrations[(i + 1) % registrations.Length];
                var forged = CommandProtocol.Encode(r.Connection.World, 2, new SelectCommand([other.Role]) { Tick = 0 }, other.Expected);
                var rejected = peer.Channel.ReceiveCommand(forged); world.ApplyPausedInterfaceCommands();
                Assert.Equal(AuthorityStatus.StaleAuthority, (await rejected).Status);
            }
            Assert.Equal(registrations.Select(r => r.Connection.Player), recorder.Commands.Select(c => c.Issuer!.Value));
            Assert.All(recorder.Commands, c => Assert.Equal(0, c.Tick));
        }
        finally { foreach (var resource in cleanup.AsEnumerable().Reverse()) resource.Dispose(); }
    }
}
