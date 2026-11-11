using Meitou.Simulation.Player;
using System.Net;
using Meitou.Networking.VNext;
using Meitou.Networking.VNext.Transport;
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
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
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
        public string Root { get; } = CreateRoot();
        static string CreateRoot() { NativeFixture.Require(); return SaveSamples.Temp("secure-server"); }
        public PopulationData Data { get; } = PopulationData.Create(SyntheticTown.Database(items: true), []);
        public WorldRuntime Runtime { get; }
        public ServerWorldLoop Loop { get; }
        public TokenAccountStore Accounts { get; }
        public Actor[] Actors { get; }
        public X509Certificate2 Certificate { get; } = GameTlsTests.Certificate();
        public QuicGameServer Server { get; }
        readonly TcpDiscoveryServer? _discovery;
        public Scene(Func<AuthorityConnection, EntityId, CancellationToken, Task<bool>>? prepare = null, int maximumConnections = 32, bool creations = false, bool production = false, bool dialogue = false, ServerInformation? information = null, bool movement = false, bool synchronousPaths = true, int catalogDescriptionBytes = 0, Action<GameStatePublication>? publicationObserved = null)
        {
            NativeFixture.Require();
            Data.Relations.PlayerFaction = -1;
            if (dialogue)
            {
                var npc = SyntheticTown.Rec("hint-npc", FcsRecordType.Character, "Speaker");
                npc.References["dialogue"] = [SyntheticTown.Ref("hint-root")];
                var root = SyntheticTown.Rec("hint-root", FcsRecordType.Dialogue, "Greeting");
                root.References["lines"] = [SyntheticTown.Ref("hint-line")];
                var line = SyntheticTown.Rec("hint-line", FcsRecordType.DialogueLine, "Hello"); line.Strings["text0"] = "Hello.";
                var file = new FcsFile(); file.Records.AddRange([npc, root, line]); Data.Db.Apply(file, "hints.mod");
            }
            ITickSystem[] systems = creations ? [new PlayerSystem(Data), new PopulationSystem(Data, new PopulationSettings { Background = false })] : [new PlayerSystem(Data)];
            if (production) systems = [.. systems, new Meitou.Simulation.Production.ProductionSystem(Data)];
            if (dialogue) systems = [.. systems, new Meitou.Simulation.Dialogue.DialogueSystem(Data)];
            if (movement) systems = [.. systems, new MovementSystem(new PathService(new OpenGroundWalkability((_, _) => 300), synchronous: synchronousPaths))];
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
                var role = Runtime.World.Characters.Spawn(new CharacterHot { Position = squad.Position,
                    Health = movement ? 100 : 0, MaxSpeed = movement ? 80 : 0, WalkSpeed = movement ? 15 : 0 }, new CharacterCold
                {
                    Name = "Role " + i, RecordId = Data.Db.OfType(FcsRecordType.Character).First().StringId,
                    Faction = faction, SquadId = squad.Id, IsPlayer = true, Inventory = new(),
                }, 0);
                Runtime.World.Characters.Cold(role.Slot)!.Inventory!.Items.Add(Data.Items.Create("70-t", 2)!);
                squad.Members.Add(role); squad.Leader = role;
                return new Actor(credential, player.Id, role, Runtime.World.Players.AssignInitial(player.Id, role));
            }).ToArray();
            var coordinator = new WorldCheckpointCoordinator(Runtime, Data, new CheckpointStore(Root), new SaveCaptureOptions { DataFiles = Data.Db.Files });
            var sessions = new ServerAccountSessions(Accounts, Runtime, coordinator);
            Loop = new(Runtime);
            var start = NewGameStart.From(Data.Db.Find("40-t")!) with { ForceStartPos = true, StartPosition = new Vector2(1000, 2000), Money = 1234, Description = new string('x', catalogDescriptionBytes) };
            var creation = creations ? new ServerCreationEndpoint(Loop, coordinator, Data, [new("initial", 10, [new(start, Data.PlayerFaction)])],
                (_, _, _) => Task.FromResult(true), initialRuleset: "initial") : null;
            var peer = new SecureServerPeer(Loop, sessions, information?.Hello ?? Hello(), new(Data.Db), prepare ?? ((_, _, _) => Task.FromResult(true)),
                new ServerPeerOptions { Observation = new(100), PublicationObserved = publicationObserved }, creation, new ServerInvitationEndpoint(Loop, coordinator, creation), information);
            Server = new(new(IPAddress.Loopback, 0), Certificate, peer, maximumConnections);
            if (information is not null) _discovery = new(Server.Endpoint, Certificate, information);
        }
        public async ValueTask DisposeAsync()
        {
            try { if (_discovery is not null) await _discovery.DisposeAsync(); await Server.DisposeAsync(); }
            finally
            {
                try { await Loop.DisposeAsync(); }
                finally { Runtime.Dispose(); Certificate.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
            }
        }
    }
    internal sealed class TestReplica { public PlayerWorldView? Current { get; set; } }
    internal sealed class TestState { public TestReplica Replica { get; } = new(); }
    internal sealed class Client(NativeGameConnection connection, AdmissionMessage admission, WorldViewCodec codec) : IAsyncDisposable
    {
        public AdmissionMessage Admission { get; private set; } = admission;
        public TestState State { get; } = new();
        public int StateFrames { get; private set; }
        public ulong ViewEpoch => _view;
        public WireFrame? LastWithheldBulkAck { get; private set; }
        GameStateReplica? _replica;
        BulkTransferReceiver? _bulk;
        StagedBulkTransfer? _staged;
        ulong _session, _view, _publication;
        WireMessage? _businessKind;
        Guid _businessQuery;
        bool _active;
        Task? _disposal;
        public Task Send(WireFrame frame, CancellationToken token) { token.ThrowIfCancellationRequested(); connection.SendControl(frame); return Task.CompletedTask; }
        public Task SendStaleControl(WireFrame frame, ulong oldView, CancellationToken token)
        { token.ThrowIfCancellationRequested(); connection.Send(QuicLane.Control, GameControlCodec.Encode(frame, _session, oldView)); return Task.CompletedTask; }
        public async Task<CreationReply> Creation(WireFrame request, CancellationToken token)
        {
            await Send(request, token);
            var reply = CreationProtocol.ReadReply(await Until(f => f.Kind == WireMessage.CreationReply, token), codec);
            Assert.Equal(Admission.Identity, reply.Identity); return reply;
        }
        public async Task<InvitationReply> Invitation(InvitationRequest request, CancellationToken token)
        {
            await Send(InvitationProtocol.Request(request), token);
            var reply = InvitationProtocol.ReadReply(await Until(f => f.Kind == WireMessage.InvitationReply, token));
            Assert.Equal(Admission.Identity, reply.Identity); return reply;
        }
        void Ready()
        {
            if (_publication == 0 || _staged is null) return;
            ulong stagedPublicationWatermark = _staged.Identity.LifecycleWatermark;
            GameViewProtocol.Unpack(_staged.Bytes.Span, bytes => _replica!.Apply(bytes.Span));
            _staged.Dispose(); _staged = null;
            Assert.True(_replica!.View!.Publication >= _publication);
            Assert.Equal(_publication, stagedPublicationWatermark);
            connection.SendControl(GameViewProtocol.Epoch(WireMessage.ViewReady, _session, _view, _publication));
        }
        public async Task<WireFrame> Until(Func<WireFrame, bool> done, CancellationToken token, bool acknowledge = true)
        {
            while (true)
            {
                var message = await connection.ReadAsync(token);
                if (message.Lane != QuicLane.Control)
                {
                    StateFrames++;
                    if (message.Lane == QuicLane.Bulk)
                    {
                        var chunk = BulkChunkCodec.Decode(message.Payload.Span);
                        Assert.Contains(_bulk!.AcceptChunk(chunk), new[] { BulkTransferOutcome.Accepted, BulkTransferOutcome.Duplicate });
                        var acknowledgement = GameViewProtocol.Ack(chunk.Identity, _bulk.ContiguousBytes);
                        if (acknowledge) connection.SendControl(acknowledgement); else LastWithheldBulkAck = acknowledgement;
                        if (_bulk.TryComplete(out var complete) == BulkTransferOutcome.Accepted)
                        {
                            if (_businessKind is { } kind)
                            {
                                WireFrame reply;
                                using (complete) reply = new(kind, complete!.Bytes.ToArray());
                                Assert.Equal(_businessQuery, BusinessBulkProtocol.Query(reply, codec)); _businessKind = null;
                                if (done(reply)) return reply;
                            }
                            else { _staged?.Dispose(); _staged = complete; Ready(); }
                        }
                    }
                    else if (message.Lane == QuicLane.Lifecycle)
                    {
                        if (message.Payload.Span.StartsWith("MTN2"u8))
                        {
                            var motion = MotionDatagramCodec.Decode(message.Payload.Span); _replica!.ApplyMotion(motion);
                            connection.SendControl(GameViewProtocol.Epoch(WireMessage.MotionCheckpointAck, _session, _view, motion.Header.PacketSequence));
                        }
                        else _replica!.Apply(message.Payload.Span);
                    }
                    else if (_active) _replica!.ApplyMotion(MotionDatagramCodec.Decode(message.Payload.Span));
                    if (_active) State.Replica.Current = _replica!.View;
                    continue;
                }
                var frame = connection.DecodeControl(message.Payload.Span);
                switch (frame.Kind)
                {
                    case WireMessage.ViewBegin:
                        var epoch = GameViewProtocol.ReadEpoch(frame); _session = epoch.Session; _view = epoch.View;
                        _active = false; _publication = 0; State.Replica.Current = null; _staged?.Dispose(); _staged = null;
                        _replica = new(codec, _session, _view); _bulk = new(_session, _view); break;
                    case WireMessage.BusinessBulkManifest:
                        var business = BusinessBulkProtocol.ReadManifest(frame); _businessKind = business.Kind; _businessQuery = business.Query;
                        _bulk ??= new(business.Manifest.Identity.SessionEpoch, business.Manifest.Identity.ViewEpoch);
                        Assert.Contains(_bulk.Begin(business.Manifest), new[] { BulkTransferOutcome.Accepted, BulkTransferOutcome.Duplicate });
                        connection.SendControl(GameViewProtocol.Epoch(WireMessage.BulkManifestAck, business.Manifest.Identity.SessionEpoch, business.Manifest.Identity.ViewEpoch, business.Manifest.Identity.BaselineId)); break;
                    case WireMessage.BusinessBulkCancelled: throw new IOException("Test business reply was withdrawn.");
                    case WireMessage.BulkManifest:
                        _publication = 0;
                        var manifest = GameViewProtocol.ReadManifest(frame);
                        Assert.Contains(_bulk!.Begin(manifest), new[] { BulkTransferOutcome.Accepted, BulkTransferOutcome.Duplicate });
                        connection.SendControl(GameViewProtocol.Epoch(WireMessage.BulkManifestAck, _session, _view, manifest.Identity.BaselineId)); break;
                    case WireMessage.ViewCommit: _publication = GameViewProtocol.ReadEpoch(frame).Publication; Ready(); break;
                    case WireMessage.ViewActive: _active = true; State.Replica.Current = _replica!.View; break;
                    case WireMessage.Admission:
                        var status = SessionProtocol.ReadAdmission(frame); Assert.Equal(Admission.Identity, status.Identity);
                        if (status.State.Sequence > Admission.State.Sequence) Admission = status;
                        if (status.State.Phase == AdmissionPhase.Preparing) { State.Replica.Current = null; _active = false; }
                        break;
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
        public ValueTask DisposeAsync() => new(_disposal ??= DisposeCore());
        async Task DisposeCore() { _staged?.Dispose(); await connection.DisposeAsync(); }
    }
    static Task<Client> Connect(Scene scene, IssuedCredential credential, CancellationToken token)
        => Connect(scene.Server.Endpoint, scene.Certificate, Hello(), credential, new(scene.Data.Db), token);
    internal static async Task<Client> Connect(IPEndPoint endpoint, X509Certificate2 certificate, CompatibilityHello hello,
        IssuedCredential credential, WorldViewCodec codec, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15)); token = deadline.Token;
        var connection = await NativeFixture.Connect(endpoint, certificate, token);
        try
        {
            connection.SendControl(new(WireMessage.Hello, CompatibilityHandshake.Encode(hello)));
            Assert.Equal(WireMessage.Compatible, (await connection.ReadControlAsync(token)).Kind);
            connection.SendControl(AuthenticationProtocol.Encode(new(credential.Account, credential.Token)));
            var reply = await connection.ReadControlAsync(token);
            if (reply.Kind == WireMessage.AuthenticationPending)
            {
                Assert.Equal(4, reply.Payload.Length);
                uint milliseconds = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(reply.Payload.Span);
                Assert.InRange(milliseconds, 1u, 180000u);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));
                reply = await connection.ReadControlAsync(token);
            }
            Assert.Equal(WireMessage.Authenticated, reply.Kind);
            return new(connection, SessionProtocol.ReadAdmission(reply), codec);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    static WireFrame Select(Client client, Actor actor, ulong request) => CommandProtocol.Encode(client.Admission.Identity.World, request,
        new SelectCommand([actor.Role]) { Tick = long.MaxValue }, new([new(actor.Grant.Entity, actor.Grant.Faction, actor.Grant.Revision)], -1, 0));

    [Fact]
    public async Task ThreeAuthenticatedClientsEnterIndependentFactionsAndExecuteDeduplicatedCommandsThroughQuic()
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
        await using var connection = await NativeFixture.Connect(scene.Server.Endpoint, scene.Certificate, timeout.Token);
        var hello = incompatible ? Hello() with { EngineBuild = "different" } : Hello();
        connection.SendControl(new(WireMessage.Hello, CompatibilityHandshake.Encode(hello)));
        var response = await connection.ReadControlAsync(timeout.Token);
        if (incompatible) Assert.Equal(WireMessage.Incompatible, response.Kind);
        else
        {
            Assert.Equal(WireMessage.Compatible, response.Kind);
            connection.SendControl(AuthenticationProtocol.Encode(new(scene.Actors[0].Credential.Account, new string('0', 64))));
            Assert.Equal(WireMessage.AuthenticationFailed, (await connection.ReadControlAsync(timeout.Token)).Kind);
        }
        Assert.False(await scene.Loop.InvokeAsync(() => scene.Runtime.World.Authority.HasHistory, timeout.Token));
        Assert.False(File.Exists(Path.Combine(scene.Root, "current.json")));
    }

    [Fact]
    public async Task ConnectionLimitIncludesUnauthenticatedSocketsAndListenerContinuesAfterRejection()
    {
        await using var scene = new Scene(maximumConnections: 1); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stalled = await NativeFixture.Connect(scene.Server.Endpoint, scene.Certificate, timeout.Token);
        while (scene.Server.ActiveConnections == 0) await Task.Delay(10, timeout.Token);
        var config = NativeFixture.Options(scene.Certificate);
        using (var rejected = new NativeQuicEndpoint(config.NativeDirectory, config.ShimSha256, false, config.CaFile))
        {
            rejected.Connect(IPAddress.Loopback, checked((ushort)scene.Server.Endpoint.Port), "localhost");
            while (scene.Server.RejectedConnections == 0) await Task.Delay(2, timeout.Token);
            Assert.Equal(1, scene.Server.ActiveConnections);
        }
        await stalled.DisposeAsync();
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
