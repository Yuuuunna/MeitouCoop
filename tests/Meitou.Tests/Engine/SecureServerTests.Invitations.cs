using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task InvitationPagesDoNotRepeatEntriesAndAcceptedWriteFailureResumesOnlyForTheRecipient()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var sender = await Connect(scene, scene.Actors[0].Credential, timeout.Token);
        var credential = scene.Accounts.Create("pending-invite"); await using var recipient = await Connect(scene, credential, timeout.Token);
        var ids = new HashSet<Guid>();
        for (int i = 0; i < 3; i++)
        {
            var offered = await sender.Invitation(new(Guid.NewGuid(), InvitationAction.Offer, Offer: new(Guid.NewGuid(), recipient.Admission.Identity.Player,
                scene.Actors[0].Grant.Faction, FactionPermission.None, [scene.Actors[0].Grant])), timeout.Token);
            ids.Add(Assert.Single(offered.Invitations).Invitation.Id);
        }
        Guid cursor = default; var pages = new HashSet<Guid>();
        do
        {
            var page = await recipient.Invitation(new(Guid.NewGuid(), InvitationAction.List, After: cursor, Limit: 1), timeout.Token);
            Assert.True(pages.Add(Assert.Single(page.Invitations).Invitation.Id)); cursor = page.Next;
        } while (cursor != Guid.Empty);
        Assert.True(ids.SetEquals(pages)); Guid chosen = ids.First();
        Client? reconnected = null;
        try
        {
            using (var writer = new FileStream(Path.Combine(scene.Root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var pending = await recipient.Invitation(new(Guid.NewGuid(), InvitationAction.Accept, chosen), timeout.Token);
                Assert.Equal(InvitationOutcome.PendingPersistence, pending.Outcome); Assert.Empty(pending.Invitations); Assert.Equal(chosen, pending.Pending);
                Assert.Equal(InvitationOutcome.Busy, (await sender.Invitation(new(Guid.NewGuid(), InvitationAction.Lookup, chosen), timeout.Token)).Outcome);
                await recipient.DisposeAsync(); reconnected = await Connect(scene, credential, timeout.Token);
                Assert.True(reconnected.Admission.State.Busy); Assert.Empty(reconnected.Admission.State.Roles);
                var lookup = await reconnected.Invitation(new(Guid.NewGuid(), InvitationAction.List), timeout.Token);
                Assert.Equal(chosen, lookup.Pending); Assert.Empty(lookup.Invitations);
            }
            var accepted = await reconnected.Invitation(new(Guid.NewGuid(), InvitationAction.Resume), timeout.Token);
            Assert.Equal(InvitationOutcome.Accepted, accepted.Outcome);
            Assert.Equal(scene.Actors[0].Grant.Entity, Assert.Single(accepted.Invitations[0].Invitation.Assigned).Entity);
            Assert.Equal(3, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Count, timeout.Token));
            await reconnected.Enter(scene.Actors[0].Grant.Entity, timeout.Token);
        }
        finally { if (reconnected is not null) await reconnected.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecipientAcceptsOnlyExplicitMembershipOrRolesAndInvitationDetailsStayPrivate(bool transfer)
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var sender = await Connect(scene, scene.Actors[0].Credential, timeout.Token);
        await using var recipient = await Connect(scene, scene.Accounts.Create("invite-recipient"), timeout.Token);
        await using var outsider = await Connect(scene, scene.Actors[1].Credential, timeout.Token);
        var actor = scene.Actors[0];
        var roster = await sender.Invitation(new(Guid.NewGuid(), InvitationAction.Roster, Faction: actor.Grant.Faction), timeout.Token);
        Assert.Equal(actor.Grant, Assert.Single(roster.Roles).Control);
        Assert.Equal(InvitationOutcome.Rejected, (await recipient.Invitation(new(Guid.NewGuid(), InvitationAction.Roster, Faction: actor.Grant.Faction), timeout.Token)).Outcome);
        var offer = new FactionInviteRequest(Guid.NewGuid(), recipient.Admission.Identity.Player, actor.Grant.Faction, FactionPermission.None, transfer ? [actor.Grant] : []);
        var sent = await sender.Invitation(new(Guid.NewGuid(), InvitationAction.Offer, Offer: offer), timeout.Token);
        Assert.Equal(InvitationOutcome.Offered, sent.Outcome); var id = Assert.Single(sent.Invitations).Invitation.Id;
        Assert.Equal(InvitationOutcome.Unknown, (await outsider.Invitation(new(Guid.NewGuid(), InvitationAction.Lookup, id), timeout.Token)).Outcome);
        Assert.Equal(id, Assert.Single((await recipient.Invitation(new(Guid.NewGuid(), InvitationAction.List), timeout.Token)).Invitations).Invitation.Id);
        var joined = await recipient.Invitation(new(Guid.NewGuid(), InvitationAction.Accept, id), timeout.Token);
        Assert.Equal(InvitationOutcome.Accepted, joined.Outcome);
        Assert.Equal(transfer ? 1 : 0, Assert.Single(joined.Invitations).Invitation.Assigned.Length);
        var replay = await recipient.Invitation(new(Guid.NewGuid(), InvitationAction.Accept, id), timeout.Token);
        Assert.Equal(joined.Invitations[0].Invitation.Assigned, replay.Invitations[0].Invitation.Assigned);
        Assert.Equal(3, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Count, timeout.Token));
        Assert.Equal(100, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Factions.Find(actor.Grant.Faction)!.Money, timeout.Token));
        if (transfer)
        {
            await recipient.Enter(actor.Grant.Entity, timeout.Token);
            Assert.Null(Assert.Single(recipient.State.Replica.Current!.Factions).Money);
            Assert.False(await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.CanControl(actor.Player, actor.Role), timeout.Token));
        }
        else
        {
            await recipient.Send(new(WireMessage.AdmissionQuery, ReadOnlyMemory<byte>.Empty), timeout.Token);
            await recipient.Until(f => f.Kind == WireMessage.Admission && recipient.Admission.State.Entry?.Route == PlayerEntryRoute.AwaitingAssignment, timeout.Token);
            Assert.Empty(recipient.Admission.State.Roles); Assert.Empty(recipient.Admission.State.Qualifications); Assert.Equal(0, recipient.StateFrames);
            var assignment = await sender.Invitation(new(Guid.NewGuid(), InvitationAction.Offer, Offer: offer with { Request = Guid.NewGuid(), Roles = [actor.Grant] }), timeout.Token);
            var assigned = await recipient.Invitation(new(Guid.NewGuid(), InvitationAction.Accept, Assert.Single(assignment.Invitations).Invitation.Id), timeout.Token);
            Assert.Equal(InvitationOutcome.Accepted, assigned.Outcome);
            Assert.NotEqual(0ul, assigned.Invitations[0].Invitation.RecipientRevision);
            await recipient.Enter(actor.Grant.Entity, timeout.Token);
            Assert.Null(Assert.Single(recipient.State.Replica.Current!.Factions).Money);
        }
    }
}
