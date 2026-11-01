using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Networking;
using Meitou.Runtime;
using Meitou.Server;
using Meitou.Simulation;
using Meitou.Simulation.Saving;
using Meitou.Tests.Save;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    static CompatibilityHello Hello() => new(CompatibilityHandshake.Protocol, "server-test", new('A', 64), new('B', 64));
    sealed record Actor(IssuedCredential Credential, PlayerId Player, CharacterId Role, CharacterControl Grant);
    sealed class Scene : IAsyncDisposable
    {
        public string Root { get; } = SaveSamples.Temp("secure-server");
        public PopulationData Data { get; } = PopulationData.Create(SyntheticTown.Database(items: true), []);
        public WorldRuntime Runtime { get; }
        public ServerWorldLoop Loop { get; }
        public TokenAccountStore Accounts { get; }
        public Actor[] Actors { get; }
        public X509Certificate2 Certificate { get; } = GameTlsTests.Certificate();
        public TcpGameServer Server { get; }
        public Scene(Func<AuthorityConnection, EntityId, CancellationToken, Task<bool>>? prepare = null, int maximumConnections = 32, bool creations = false, bool production = false, bool dialogue = false, ServerInformation? information = null)
        {
            Data.Relations.PlayerFaction = -1;
            if (dialogue)
            {
                var npc = SyntheticTown.Rec("hint-npc", FcsRecordType.CHARACTER, "Speaker");
                npc.References["dialogue"] = [SyntheticTown.Ref("hint-root")];
                var root = SyntheticTown.Rec("hint-root", FcsRecordType.DIALOGUE, "Greeting");
                root.References["lines"] = [SyntheticTown.Ref("hint-line")];
                var line = SyntheticTown.Rec("hint-line", FcsRecordType.DIALOGUE_LINE, "Hello"); line.Strings["text0"] = "Hello.";
                var file = new FcsFile(); file.Records.AddRange([npc, root, line]); Data.Db.Apply(file, "hints.mod");
            }
            ITickSystem[] systems = creations ? [new PlayerSystem(Data), new PopulationSystem(Data, new PopulationSettings { Background = false })] : [new PlayerSystem(Data)];
            if (production) systems = [.. systems, new Meitou.Simulation.Production.ProductionSystem(Data)];
            if (dialogue) systems = [.. systems, new Meitou.Simulation.Dialogue.DialogueSystem(Data)];
            Runtime = new(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300),
                systems);
            Runtime.Speed = 0;
            Accounts = new(Path.Combine(Root, "accounts.json"));
            Actors = Enumerable.Range(0, 3).Select(i =>
            {
                var credential = Accounts.Create("player-" + i); var principal = Accounts.Authenticate(new(credential.Account, credential.Token))!;
                var player = Runtime.World.Players.ResolveAccount(principal.Identity).Player;
                int faction = Data.RegisterFaction("peer:" + i, "Faction " + i, Data.PlayerFaction);
                Runtime.World.Players.SetMembership(player.Id, faction, FactionPermission.All); player.State.Money = (i + 1) * 100;
                var squad = SquadManagement.NewSquad(Runtime.World, Data.Db, new(i * 20, 300, 0), "Squad " + i, faction);
                var role = Runtime.World.Characters.Spawn(new CharacterHot { Position = squad.Position }, new CharacterCold
                {
                    Name = "Role " + i, RecordId = Data.Db.OfType(FcsRecordType.CHARACTER).First().StringId,
                    Faction = faction, SquadId = squad.Id, IsPlayer = true, Inventory = new(),
                }, 0);
                Runtime.World.Characters.Cold(role.Slot)!.Inventory!.Items.Add(Data.Items.Create("70-t", 2)!);
                squad.Members.Add(role); squad.Leader = role;
                return new Actor(credential, player.Id, role, Runtime.World.Players.AssignInitial(player.Id, role));
            }).ToArray();
            var coordinator = new WorldCheckpointCoordinator(Runtime, Data, new CheckpointStore(Root), new SaveCaptureOptions { DataFiles = Data.Db.Files });
            var sessions = new ServerAccountSessions(Accounts, Runtime, coordinator);
            Loop = new(Runtime);
            var start = NewGameStart.From(Data.Db.Find("40-t")!) with { ForceStartPos = true, StartPosition = new Vector2(1000, 2000), Money = 1234 };
            var creation = creations ? new ServerCreationEndpoint(Loop, coordinator, Data, [new("initial", 10, [new(start, Data.PlayerFaction)])],
                (_, _, _) => Task.FromResult(true), initialRuleset: "initial") : null;
            var peer = new SecureServerPeer(Loop, sessions, information?.Hello ?? Hello(), new(Data.Db), prepare ?? ((_, _, _) => Task.FromResult(true)),
                new ServerPeerOptions { Observation = new(100) }, creation, new ServerInvitationEndpoint(Loop, coordinator, creation), information);
            Server = new(new(IPAddress.Loopback, 0), Certificate, peer, maximumConnections);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Server.DisposeAsync(); }
            finally
            {
                try { await Loop.DisposeAsync(); }
                finally { Runtime.Dispose(); Certificate.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
            }
        }
    }
    internal sealed class Client(TcpClient tcp, SslStream stream, AdmissionMessage admission, WorldViewCodec codec) : IAsyncDisposable
    {
        public AdmissionMessage Admission { get; private set; } = admission;
        public ClientStateReceiver State { get; } = new(admission.Identity, codec);
        public int StateFrames { get; private set; }
        public Task Send(WireFrame frame, CancellationToken token) => WireFrame.WriteAsync(stream, frame, token).AsTask();
        public async Task<CreationReply> Creation(WireFrame request, CancellationToken token)
        {
            await Send(request, token);
            var frame = await Until(f => f.Kind == WireMessage.CreationReply, token);
            var reply = CreationProtocol.ReadReply(frame, codec); Assert.Equal(Admission.Identity, reply.Identity); return reply;
        }
        public async Task<InvitationReply> Invitation(InvitationRequest request, CancellationToken token)
        {
            await Send(InvitationProtocol.Request(request), token);
            var reply = InvitationProtocol.ReadReply(await Until(f => f.Kind == WireMessage.InvitationReply, token));
            Assert.Equal(Admission.Identity, reply.Identity); return reply;
        }
        public async Task<WireFrame> Until(Func<WireFrame, bool> done, CancellationToken token, bool acknowledge = true)
        {
            while (true)
            {
                var frame = await WireFrame.ReadAsync(stream, token) ?? throw new EndOfStreamException();
                if (frame.Kind == WireMessage.Admission)
                {
                    var status = SessionProtocol.ReadAdmission(frame); Assert.Equal(Admission.Identity, status.Identity);
                    if (status.State.Sequence > Admission.State.Sequence) Admission = status;
                }
                if (frame.Kind is WireMessage.StateChunk or WireMessage.StateWithdrawn)
                {
                    StateFrames++; var result = State.Receive(frame);
                    if (acknowledge && result == ReplicaResult.Applied && State.Acknowledgement is { } ack) await Send(ack, token);
                    if (result == ReplicaResult.NeedsBaseline && frame.Kind != WireMessage.StateWithdrawn) await Send(State.ResyncRequest(), token);
                }
                if (done(frame)) return frame;
            }
        }
        public async Task Enter(EntityId role, CancellationToken token)
        {
            await Send(SessionProtocol.Enter(role), token);
            await Until(_ => Admission.State.Phase == AdmissionPhase.Active && State.Replica.Current is not null, token);
            Assert.NotNull(State.Replica.Current);
        }
        public async ValueTask DisposeAsync() { tcp.Dispose(); await stream.DisposeAsync(); }
    }
    static Task<Client> Connect(Scene scene, IssuedCredential credential, CancellationToken token)
        => Connect(scene.Server.Endpoint, scene.Certificate, Hello(), credential, new(scene.Data.Db), token);
    internal static async Task<Client> Connect(IPEndPoint endpoint, X509Certificate2 certificate, CompatibilityHello hello,
        IssuedCredential credential, WorldViewCodec codec, CancellationToken token)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(endpoint, token);
            var tls = await GameTls.ConnectAsync(tcp.GetStream(), "localhost", certificate.GetCertHashString(HashAlgorithmName.SHA256), token);
            await WireFrame.WriteAsync(tls, new(WireMessage.Hello, CompatibilityHandshake.Encode(hello)), token);
            Assert.Equal(WireMessage.Compatible, (await WireFrame.ReadAsync(tls, token))!.Kind);
            await WireFrame.WriteAsync(tls, AuthenticationProtocol.Encode(new(credential.Account, credential.Token)), token);
            var reply = (await WireFrame.ReadAsync(tls, token))!; Assert.Equal(WireMessage.Authenticated, reply.Kind);
            return new(tcp, tls, SessionProtocol.ReadAdmission(reply), codec);
        }
        catch { tcp.Dispose(); throw; }
    }
    static WireFrame Select(Client client, Actor actor, ulong request) => CommandProtocol.Encode(client.Admission.Identity.World, request,
        new SelectCommand([actor.Role]) { Tick = long.MaxValue }, new([new(actor.Grant.Entity, actor.Grant.Faction, actor.Grant.Revision)], -1, 0));

    [Fact]
    public async Task ThreeAuthenticatedClientsEnterIndependentFactionsAndExecuteDeduplicatedCommandsThroughTls()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var a = await Connect(scene, scene.Actors[0].Credential, timeout.Token);
        await using var b = await Connect(scene, scene.Actors[1].Credential, timeout.Token);
        await using var c = await Connect(scene, scene.Actors[2].Credential, timeout.Token);
        foreach (var client in new[] { a, b, c }) Assert.Equal(AdmissionPhase.Preparing, client.Admission.State.Phase);
        Assert.Equal(scene.Actors[0].Grant.Entity, Assert.Single(a.Admission.State.Roles).Entity);
        await a.Send(Select(a, scene.Actors[0], 1), timeout.Token);
        var blocked = CommandProtocol.DecodeReceipt(await a.Until(f => f.Kind == WireMessage.CommandReceipt, timeout.Token));
        Assert.Equal(AuthorityStatus.NotActive, blocked.Receipt.Status); Assert.Equal(0, a.StateFrames);
        await a.Send(SessionProtocol.Enter(scene.Actors[1].Grant.Entity), timeout.Token);
        await a.Until(f => f.Kind == WireMessage.Admission, timeout.Token); Assert.Equal(AdmissionPhase.Preparing, a.Admission.State.Phase);
        await Task.WhenAll(a.Enter(scene.Actors[0].Grant.Entity, timeout.Token), b.Enter(scene.Actors[1].Grant.Entity, timeout.Token), c.Enter(scene.Actors[2].Grant.Entity, timeout.Token));
        foreach (var pair in new[] { (Client: a, Index: 0), (Client: b, Index: 1), (Client: c, Index: 2) })
        {
            var view = pair.Client.State.Replica.Current!;
            Assert.Equal((pair.Index + 1) * 100, Assert.Single(view.Factions).Money);
            Assert.Equal(3, view.Snapshot.Characters.Count);
            Assert.NotNull(view.Snapshot.Characters.Single(r => r.Id == scene.Actors[pair.Index].Role).ItemGrid);
            Assert.All(view.Snapshot.Characters.Where(r => r.Id != scene.Actors[pair.Index].Role), r => Assert.Null(r.ItemGrid));
        }
        var select = Select(a, scene.Actors[0], 1); await a.Send(select, timeout.Token);
        var first = CommandProtocol.DecodeReceipt(await a.Until(f => f.Kind == WireMessage.CommandReceipt, timeout.Token)).Receipt;
        Assert.Equal(AuthorityStatus.Evaluated, first.Status);
        await a.Send(select, timeout.Token);
        Assert.Equal(first, CommandProtocol.DecodeReceipt(await a.Until(f => f.Kind == WireMessage.CommandReceipt, timeout.Token)).Receipt);
        Assert.Equal(scene.Actors[0].Role, await scene.Loop.InvokeAsync(() => Assert.Single(scene.Runtime.World.Players.Find(scene.Actors[0].Player)!.State.Selection), timeout.Token));
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.Find(scene.Actors[0].Player)!.State.OpenLoot = scene.Actors[0].Role, timeout.Token);
        await using var replacement = await Connect(scene, scene.Actors[0].Credential, timeout.Token);
        Assert.Equal(a.Admission.Identity.Player, replacement.Admission.Identity.Player);
        Assert.True(replacement.Admission.Identity.Generation > a.Admission.Identity.Generation);
        Assert.Equal(1ul, replacement.Admission.State.CommandHighWater);
        Assert.True(await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.Find(scene.Actors[0].Player)!.State.OpenLoot.IsNone, timeout.Token));
        await replacement.Enter(scene.Actors[0].Grant.Entity, timeout.Token);
        await replacement.Send(Select(replacement, scene.Actors[0], 2), timeout.Token);
        Assert.Equal(AuthorityStatus.Evaluated, CommandProtocol.DecodeReceipt(await replacement.Until(f => f.Kind == WireMessage.CommandReceipt, timeout.Token)).Receipt.Status);
    }

    [Fact]
    public async Task RolelessAccountReceivesOnlyItsPreparationStateAndCannotBorrowAnotherPlayersRole()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var credential = scene.Accounts.Create("newcomer"); await using var client = await Connect(scene, credential, timeout.Token);
        Assert.Equal(PlayerEntryRoute.NewPlayer, client.Admission.State.Entry!.Route); Assert.Empty(client.Admission.State.Roles);
        Assert.Empty(client.Admission.State.Qualifications);
        await client.Send(SessionProtocol.Enter(scene.Actors[0].Grant.Entity), timeout.Token);
        await client.Until(f => f.Kind == WireMessage.Admission, timeout.Token);
        Assert.Equal(AdmissionPhase.Preparing, client.Admission.State.Phase); Assert.Equal(0, client.StateFrames);
        await client.Send(Select(client, scene.Actors[0], 1), timeout.Token);
        Assert.Equal(AuthorityStatus.NotActive, CommandProtocol.DecodeReceipt(await client.Until(f => f.Kind == WireMessage.CommandReceipt, timeout.Token)).Receipt.Status);
        Assert.Equal(3, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Count, timeout.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompatibilityOrCredentialRejectionCannotOpenAuthorityOrCommitAPlayerBinding(bool incompatible)
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var tcp = new TcpClient(); await tcp.ConnectAsync(scene.Server.Endpoint, timeout.Token);
        await using var tls = await GameTls.ConnectAsync(tcp.GetStream(), "localhost", scene.Certificate.GetCertHashString(HashAlgorithmName.SHA256), timeout.Token);
        var hello = incompatible ? Hello() with { EngineBuild = "different" } : Hello();
        await WireFrame.WriteAsync(tls, new(WireMessage.Hello, CompatibilityHandshake.Encode(hello)), timeout.Token);
        var response = (await WireFrame.ReadAsync(tls, timeout.Token))!;
        if (incompatible) Assert.Equal(WireMessage.Incompatible, response.Kind);
        else
        {
            Assert.Equal(WireMessage.Compatible, response.Kind);
            await WireFrame.WriteAsync(tls, AuthenticationProtocol.Encode(new(scene.Actors[0].Credential.Account, new string('0', 64))), timeout.Token);
            Assert.Equal(WireMessage.AuthenticationFailed, (await WireFrame.ReadAsync(tls, timeout.Token))!.Kind);
        }
        Assert.False(await scene.Loop.InvokeAsync(() => scene.Runtime.World.Authority.HasHistory, timeout.Token));
        Assert.False(File.Exists(Path.Combine(scene.Root, "current.json")));
    }

    [Fact]
    public async Task ConnectionLimitIncludesUnauthenticatedSocketsAndListenerContinuesAfterRejection()
    {
        await using var scene = new Scene(maximumConnections: 1); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stalled = new TcpClient(); await stalled.ConnectAsync(scene.Server.Endpoint, timeout.Token);
        while (scene.Server.ActiveConnections == 0) await Task.Delay(10, timeout.Token);
        using var rejected = new TcpClient(); await rejected.ConnectAsync(scene.Server.Endpoint, timeout.Token);
        Assert.Equal(0, await rejected.GetStream().ReadAsync(new byte[1], timeout.Token));
        Assert.Equal(1, scene.Server.RejectedConnections);
        stalled.Dispose();
        while (scene.Server.ActiveConnections != 0) await Task.Delay(10, timeout.Token);
        await using var valid = await Connect(scene, scene.Actors[0].Credential, timeout.Token);
        Assert.Equal(scene.Actors[0].Player, valid.Admission.Identity.Player);
    }

    [Fact]
    public async Task DisconnectWhilePreparingCancelsTheLoaderAndKeepsTheRoleInTheWorld()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scene = new Scene(async (_, _, token) =>
        {
            started.TrySetResult();
            try { return await never.Task.WaitAsync(token); }
            finally { canceled.TrySetResult(); }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await Connect(scene, scene.Actors[0].Credential, timeout.Token);
        await client.Send(SessionProtocol.Enter(scene.Actors[0].Grant.Entity), timeout.Token);
        await started.Task.WaitAsync(timeout.Token); await client.DisposeAsync();
        await canceled.Task.WaitAsync(timeout.Token);
        Assert.Equal(3, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Count, timeout.Token));
        Assert.True(await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.Validate(scene.Actors[0].Grant), timeout.Token));
    }

    [Fact]
    public async Task RegionPreparationDoesNotBlockOtherConnectionsAndRechecksRevokedOwnership()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EntityId blocked = default;
        await using var scene = new Scene((_, role, token) =>
        {
            if (role != blocked) return Task.FromResult(true);
            started.TrySetResult(); return release.Task.WaitAsync(token);
        });
        blocked = scene.Actors[0].Grant.Entity;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var a = await Connect(scene, scene.Actors[0].Credential, timeout.Token);
        await using var b = await Connect(scene, scene.Actors[1].Credential, timeout.Token);
        await a.Send(SessionProtocol.Enter(blocked), timeout.Token); await started.Task.WaitAsync(timeout.Token);
        await b.Enter(scene.Actors[1].Grant.Entity, timeout.Token);
        await b.Send(Select(b, scene.Actors[1], 1), timeout.Token);
        Assert.Equal(AuthorityStatus.Evaluated, CommandProtocol.DecodeReceipt(await b.Until(f => f.Kind == WireMessage.CommandReceipt, timeout.Token)).Receipt.Status);
        var before = await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Current[scene.Actors[0].Role.Slot].Position, timeout.Token);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.RemoveMembership(scene.Actors[0].Player, scene.Actors[0].Grant.Faction), timeout.Token);
        release.TrySetResult(true); await a.Until(f => f.Kind == WireMessage.Admission, timeout.Token);
        Assert.Equal(AdmissionPhase.Preparing, a.Admission.State.Phase); Assert.Equal(PlayerEntryRoute.AwaitingAssignment, a.Admission.State.Entry!.Route);
        Assert.Equal(before, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Current[scene.Actors[0].Role.Slot].Position, timeout.Token));
        Assert.Equal(0, a.StateFrames);
    }
}
