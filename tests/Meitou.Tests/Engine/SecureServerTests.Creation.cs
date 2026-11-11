using Meitou.Simulation.Population;
using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    static async Task<CreationDraftQuery> InitialDraft(Client client, string name, CancellationToken token)
    {
        var offered = await client.Creation(CreationProtocol.Catalog(new(Guid.NewGuid())), token);
        Assert.Equal(CreationOutcome.Catalog, offered.Outcome);
        var catalog = offered.Catalog!; Assert.True(catalog.Qualification.Grant.IncludesStartResources);
        var start = Assert.Single(catalog.Starts); Assert.Equal(1234, start.StartMoney);
        return new(Guid.NewGuid(), 1, new(Guid.NewGuid(), catalog.Qualification.Grant.Id, catalog.Qualification.Grant.Revision, start.Id, name,
            [new CreationMemberChoice(0, "Created role")]));
    }

    [Fact]
    public async Task RolelessClientCanPreviewCreateEnterAndReplayWithoutSharingAnotherFactionsResources()
    {
        await using var scene = new Scene(creations: true); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var credential = scene.Accounts.Create("creator"); await using var client = await Connect(scene, credential, timeout.Token);
        Assert.Equal(PlayerEntryRoute.NewPlayer, client.Admission.State.Entry!.Route);
        var draft = await InitialDraft(client, "Network newcomers", timeout.Token);
        var before = await scene.Loop.InvokeAsync(() => (Count: scene.Runtime.World.Characters.Count, Entity: scene.Runtime.World.Entities.LastAllocated, Factions: scene.Data.Factions.Count), timeout.Token);
        var preview = await client.Creation(CreationProtocol.Preview(draft), timeout.Token);
        Assert.Equal(CreationOutcome.Preview, preview.Outcome); Assert.Equal(draft.DraftRevision, preview.DraftRevision);
        Assert.NotEmpty(preview.Preview!.Members); Assert.Equal("Created role", preview.Preview.Members[0].Name);
        Assert.Equal(before, await scene.Loop.InvokeAsync(() => (scene.Runtime.World.Characters.Count, scene.Runtime.World.Entities.LastAllocated, scene.Data.Factions.Count), timeout.Token));
        var commit = await client.Creation(CreationProtocol.Commit(draft with { Query = Guid.NewGuid() }), timeout.Token);
        Assert.Equal(CreationOutcome.Committed, commit.Outcome); Assert.Equal(preview.Preview.Fingerprint, commit.Receipt!.Fingerprint);
        Assert.Equal(preview.Preview.Members.Length, commit.Receipt.Roles.Length); Assert.True(commit.Receipt.IncludesStartResources);
        Assert.Equal(1234, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Factions.Find(commit.Receipt.Faction)!.Money, timeout.Token));
        Assert.Equal(100, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Factions.Find(scene.Actors[0].Grant.Faction)!.Money, timeout.Token));
        Assert.DoesNotContain(scene.Actors, a => a.Grant.Faction == commit.Receipt.Faction);
        var replay = await client.Creation(CreationProtocol.Commit(draft with { Query = Guid.NewGuid(), DraftRevision = 2 }), timeout.Token);
        Assert.Equal(commit.Receipt.Roles, replay.Receipt!.Roles);
        var conflict = await client.Creation(CreationProtocol.Commit(draft with { Query = Guid.NewGuid(), Request = draft.Request with { FactionName = "Changed" } }), timeout.Token);
        Assert.Equal(CreationFailure.Conflict, conflict.Failure);
        Assert.Equal(before.Count + commit.Receipt.Roles.Length, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Count, timeout.Token));
        await client.Enter(commit.Receipt.Roles[0], timeout.Token);
        Assert.Equal(1234, Assert.Single(client.State.Replica.Current!.Factions).Money);
        await using var other = await Connect(scene, scene.Actors[0].Credential, timeout.Token);
        var privateResult = await other.Creation(CreationProtocol.Lookup(new(Guid.NewGuid(), draft.Request.Id)), timeout.Token);
        Assert.Equal(CreationOutcome.Unknown, privateResult.Outcome); Assert.Null(privateResult.Receipt);
        var again = await client.Creation(CreationProtocol.Catalog(new(Guid.NewGuid())), timeout.Token);
        Assert.Equal(CreationFailure.Qualification, again.Failure);
    }

    [Fact]
    public async Task CreationReplyLossAndPersistenceFailureRecoverThroughAuthenticatedReconnect()
    {
        await using var scene = new Scene(creations: true); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var credential = scene.Accounts.Create("recovering-creator"); await using var client = await Connect(scene, credential, timeout.Token);
        await using var other = await Connect(scene, scene.Actors[0].Credential, timeout.Token);
        var draft = await InitialDraft(client, "Recoverable faction", timeout.Token);
        Client? resumed = null;
        try
        {
            using (var writer = new FileStream(Path.Combine(scene.Root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var failed = await client.Creation(CreationProtocol.Commit(draft), timeout.Token);
                Assert.Equal(CreationOutcome.PendingPersistence, failed.Outcome); Assert.Null(failed.Receipt);
                Assert.Equal(CreationFailure.Persistence, failed.Failure);
                var foreign = await other.Creation(CreationProtocol.Lookup(new(Guid.NewGuid(), draft.Request.Id, Resume: true)), timeout.Token);
                Assert.Equal(CreationOutcome.Busy, foreign.Outcome); Assert.Null(foreign.Receipt);
                await client.DisposeAsync(); resumed = await Connect(scene, credential, timeout.Token);
                Assert.True(resumed.Admission.State.Busy); Assert.Empty(resumed.Admission.State.Roles);
                var recoveredId = await resumed.Creation(CreationProtocol.Lookup(new(Guid.NewGuid(), Guid.Empty)), timeout.Token);
                Assert.Equal(draft.Request.Id, recoveredId.Request); Assert.Equal(CreationOutcome.PendingPersistence, recoveredId.Outcome);
            }
            var count = await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Count, timeout.Token);
            var persisted = await resumed.Creation(CreationProtocol.Lookup(new(Guid.NewGuid(), Guid.Empty, Resume: true)), timeout.Token);
            Assert.Equal(CreationOutcome.Committed, persisted.Outcome); Assert.NotNull(persisted.Receipt);
            Assert.Equal(count, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Count, timeout.Token));
            // Discard the old connection/result and find the same durable result on another authenticated connection.
            await resumed.DisposeAsync(); resumed = null;
            await using var next = await Connect(scene, credential, timeout.Token);
            var lookup = await next.Creation(CreationProtocol.Lookup(new(Guid.NewGuid(), draft.Request.Id)), timeout.Token);
            Assert.Equal(persisted.Receipt.Roles, lookup.Receipt!.Roles);
            await next.Enter(lookup.Receipt.Roles[0], timeout.Token);
            Assert.Equal(1234, Assert.Single(next.State.Replica.Current!.Factions).Money);
        }
        finally { if (resumed is not null) await resumed.DisposeAsync(); }
    }

    [Fact]
    public async Task FactionApprovedCreationUsesTheApprovedBudgetWithoutAnIndependentStartReward()
    {
        await using var scene = new Scene(creations: true); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var credential = scene.Accounts.Create("sponsored-creator"); await using var client = await Connect(scene, credential, timeout.Token);
        await using var sponsor = await Connect(scene, scene.Actors[0].Credential, timeout.Token);
        var invitation = await sponsor.Invitation(new(Guid.NewGuid(), InvitationAction.Offer, Offer: new(Guid.NewGuid(), client.Admission.Identity.Player,
            scene.Actors[0].Grant.Faction, FactionPermission.None, [], "initial", 10, 40)), timeout.Token);
        var joined = await client.Invitation(new(Guid.NewGuid(), InvitationAction.Accept, Assert.Single(invitation.Invitations).Invitation.Id), timeout.Token);
        Assert.Equal(InvitationOutcome.Accepted, joined.Outcome);
        var grant = await scene.Loop.InvokeAsync(() => scene.Runtime.World.CreationQualifications.Find(Assert.Single(joined.Invitations).Invitation.CreationQualification)!, timeout.Token);
        var offered = await client.Creation(CreationProtocol.Catalog(new(Guid.NewGuid(), grant.Id, grant.Revision)), timeout.Token);
        Assert.Equal(CreationOutcome.Catalog, offered.Outcome); Assert.Equal(0, Assert.Single(offered.Catalog!.Starts).StartMoney);
        var draft = new CreationDraftQuery(Guid.NewGuid(), 1, new(Guid.NewGuid(), grant.Id, grant.Revision, offered.Catalog.Starts[0].Id));
        var reply = await client.Creation(CreationProtocol.Commit(draft), timeout.Token);
        Assert.Equal(CreationOutcome.Committed, reply.Outcome); Assert.Equal(grant.Faction, reply.Receipt!.Faction);
        Assert.False(reply.Receipt.IncludesStartResources); Assert.Equal(40, reply.Receipt.Paid);
        Assert.Equal(60, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Factions.Find(grant.Faction)!.Money, timeout.Token));
        await client.Enter(reply.Receipt.Roles[0], timeout.Token);
        Assert.Null(Assert.Single(client.State.Replica.Current!.Factions).Money);
        Assert.False(await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.CanControl(client.Admission.Identity.Player, scene.Actors[0].Role), timeout.Token));
    }
}
