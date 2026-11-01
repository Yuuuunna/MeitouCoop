using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Meitou.Networking;
using Meitou.Server;
using Meitou.Simulation;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    static Task<RemoteGameSession> ConnectRemote(Scene scene, IssuedCredential credential, CancellationToken token)
        => RemoteGameSession.ConnectAsync("127.0.0.1", scene.Server.Endpoint.Port, Hello(), new(credential.Account, credential.Token),
            new(scene.Data.Db), scene.Certificate.GetCertHashString(HashAlgorithmName.SHA256), token);

    static async Task<RemoteSessionState> WaitRemote(RemoteGameSession client, Func<RemoteSessionState, bool> condition, CancellationToken token)
    {
        var state = client.State;
        while (!condition(state))
        {
            Assert.Null(state.Disconnection);
            state = await client.WaitForChangeAsync(state.Version, token);
        }
        return state;
    }

    [Fact]
    public async Task ProductionRemoteClientsReceiveIsolatedViewsExecuteCommandsAndLoseControlOnReplacement()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var a = await ConnectRemote(scene, scene.Actors[0].Credential, token);
        await using var b = await ConnectRemote(scene, scene.Actors[1].Credential, token);
        await using var c = await ConnectRemote(scene, scene.Actors[2].Credential, token);
        var clients = new[] { a, b, c };
        for (int i = 0; i < clients.Length; i++)
        {
            Assert.False(clients[i].State.CanControl); Assert.Null(clients[i].State.View);
            await Assert.ThrowsAsync<InvalidOperationException>(() => clients[i].CommandAsync(1, new SelectCommand([scene.Actors[i].Role]) { Tick = 0 }, new([], -1, 0), token));
            clients[i].RequestEntry(scene.Actors[i].Grant.Entity);
        }
        await Task.WhenAll(clients.Select(client => WaitRemote(client, s => s.CanControl, token)));
        for (int i = 0; i < clients.Length; i++)
        {
            var actor = scene.Actors[i]; var view = clients[i].State.View!;
            Assert.Equal((i + 1) * 100, Assert.Single(view.Factions).Money);
            Assert.All(view.Snapshot.Characters.Where(r => r.Id != actor.Role), r => Assert.Null(r.ItemGrid));
            var bound = ClientCommandBinding.Bind(view, new SelectCommand([actor.Role]) { Tick = 0 }, [], actor.Grant.Faction);
            var receipt = await clients[i].CommandAsync(1, bound.Command, bound.Expected, token);
            Assert.Equal(AuthorityStatus.Evaluated, receipt.Receipt.Status);
            Assert.Equal(receipt, await clients[i].CommandAsync(1, bound.Command, bound.Expected, token));
        }
        await using var replacement = await ConnectRemote(scene, scene.Actors[0].Credential, token);
        var disconnected = await WaitRemote(a, s => s.Disconnection is not null, token);
        Assert.False(disconnected.CanControl); Assert.Null(disconnected.View);
        Assert.True(replacement.State.Admission.Identity.Generation > a.State.Admission.Identity.Generation);
        Assert.Equal(1ul, replacement.State.Admission.State.CommandHighWater);
        replacement.RequestEntry(scene.Actors[0].Grant.Entity);
        await WaitRemote(replacement, s => s.CanControl, token);
        Assert.True(b.State.CanControl); Assert.True(c.State.CanControl);
    }

    [Fact]
    public async Task ProductionRemoteRolelessClientCorrelatesQueriesCreatesAndRecoversTheSameRoles()
    {
        await using var scene = new Scene(creations: true); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var credential = scene.Accounts.Create("remote-creator");
        await using var client = await ConnectRemote(scene, credential, token);
        Assert.Equal(PlayerEntryRoute.NewPlayer, client.State.Admission.State.Entry!.Route);
        Assert.Null(client.State.View);
        var queries = Enumerable.Range(0, 12).Select(_ => new CreationCatalogQuery(Guid.NewGuid())).ToArray();
        var catalogs = await Task.WhenAll(queries.Select(q => client.CatalogAsync(q, token)));
        Assert.Equal(queries.Select(q => q.Query), catalogs.Select(r => r.Query));
        Assert.All(catalogs, r => Assert.Equal(CreationOutcome.Catalog, r.Outcome));
        var catalog = catalogs[0].Catalog!;
        var draft = new CreationDraftQuery(Guid.NewGuid(), 1, new(Guid.NewGuid(), catalog.Qualification.Grant.Id,
            catalog.Qualification.Grant.Revision, catalog.Starts[0].Id, "Remote faction", [new CreationMemberChoice(0, "Remote role")]));
        var preview = await client.PreviewAsync(draft, token);
        Assert.Equal(CreationOutcome.Preview, preview.Outcome);
        var created = await client.CommitAsync(draft with { Query = Guid.NewGuid() }, token);
        Assert.Equal(CreationOutcome.Committed, created.Outcome);
        Assert.Equal(preview.Preview!.Fingerprint, created.Receipt!.Fingerprint);
        client.RequestEntry(created.Receipt.Roles[0]);
        await WaitRemote(client, s => s.CanControl, token);
        Assert.Equal(1234, Assert.Single(client.State.View!.Factions).Money);
        var invitation = await client.InvitationAsync(new(Guid.NewGuid(), InvitationAction.List), token);
        Assert.Equal(InvitationOutcome.Listed, invitation.Outcome); Assert.Empty(invitation.Invitations);
        await client.DisposeAsync();
        await using var replacement = await ConnectRemote(scene, credential, token);
        var lookup = await replacement.LookupAsync(new(Guid.NewGuid(), draft.Request.Id), token);
        Assert.Equal(created.Receipt.Roles, lookup.Receipt!.Roles);
        replacement.RequestEntry(lookup.Receipt.Roles[0]);
        await WaitRemote(replacement, s => s.CanControl, token);
        Assert.Equal(1234, Assert.Single(replacement.State.View!.Factions).Money);
    }

    [Fact]
    public async Task RemoteClientClearsUsableWorldWhenItsLastMembershipIsRevoked()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var actor = scene.Actors[0];
        await using var client = await ConnectRemote(scene, actor.Credential, token);
        client.RequestEntry(actor.Grant.Entity);
        await WaitRemote(client, s => s.CanControl, token);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.RemoveMembership(actor.Player, actor.Grant.Faction), token);
        var revoked = await WaitRemote(client, s => s.Admission.State.Phase == AdmissionPhase.Preparing && !s.CanControl, token);
        Assert.Null(revoked.View); Assert.Empty(revoked.Admission.State.Roles);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CommandAsync(1, new SelectCommand([actor.Role]) { Tick = 0 },
            new([new(actor.Grant.Entity, actor.Grant.Faction, actor.Grant.Revision)], -1, 0), token));
    }

    [Fact]
    public async Task RemoteClientChecksServerHelloBeforeSendingCredentialsEvenWhenServerClaimsCompatibility()
    {
        using var certificate = GameTlsTests.Certificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var token = timeout.Token;
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(token);
            await using var stream = await GameTls.AcceptAsync(socket.GetStream(), certificate, token);
            Assert.Equal(WireMessage.Hello, (await WireFrame.ReadAsync(stream, token))!.Kind);
            await WireFrame.WriteAsync(stream, new(WireMessage.Compatible, CompatibilityHandshake.Encode(Hello() with { ContentDigest = new('C', 64) })), token);
            Assert.Null(await WireFrame.ReadAsync(stream, token)); // no credential was sent
        }, token);
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteGameSession.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            Hello(), new(Guid.NewGuid(), new('0', 64)), new(SyntheticTown.Database(items: true)), certificate.GetCertHashString(HashAlgorithmName.SHA256), token));
        await peer;
    }

    [Fact]
    public async Task RemoteCancelledWaitStillConsumesItsLateReplyWithoutPoisoningSubsequentQueries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scene = new Scene((_, _, token) => { started.TrySetResult(); return release.Task.WaitAsync(token); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = timeout.Token;
        await using var client = await ConnectRemote(scene, scene.Actors[0].Credential, token);
        client.RequestEntry(scene.Actors[0].Grant.Entity); await started.Task.WaitAsync(token);
        using var waiter = new CancellationTokenSource();
        var waiting = client.InvitationAsync(new(Guid.NewGuid(), InvitationAction.List), waiter.Token);
        waiter.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.TrySetResult(true);
        var query = new InvitationRequest(Guid.NewGuid(), InvitationAction.List);
        var reply = await client.InvitationAsync(query, token);
        Assert.Equal(query.Query, reply.Query); Assert.Equal(InvitationOutcome.Listed, reply.Outcome);
        await WaitRemote(client, s => s.CanControl, token);
        Assert.Null(client.State.Disconnection);
        await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task RemoteCancellationStopsWaitingWithoutDiscardingAcceptedRequestsAndDisposeCancelsPreparation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scene = new Scene(async (_, _, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }
            finally { cancelled.TrySetResult(); }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = timeout.Token;
        await using var client = await ConnectRemote(scene, scene.Actors[0].Credential, token);
        client.RequestEntry(scene.Actors[0].Grant.Entity);
        await started.Task.WaitAsync(token);
        using var waiter = new CancellationTokenSource();
        var waiting = client.InvitationAsync(new(Guid.NewGuid(), InvitationAction.List), waiter.Token);
        waiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Null(client.State.Disconnection);
        var pending = client.InvitationAsync(new(Guid.NewGuid(), InvitationAction.List), token);
        await client.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => pending);
        await cancelled.Task.WaitAsync(token);
        Assert.Null(client.State.View); Assert.False(client.State.CanControl);
        Assert.True(await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.Validate(scene.Actors[0].Grant), token));
    }
}
