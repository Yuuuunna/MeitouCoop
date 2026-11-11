using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Meitou.Data.Fcs;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Items;
using Meitou.Simulation.Saving;
using Meitou.Tests.Save;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Engine;

public class PlayerInvitationServiceTests
{
    [Fact]
    public async Task RemovedServerCreationPolicyCannotAcceptAnOldSponsoredInvitation()
    {
        using var s = new Scene();
        var offer = s.Service.Offer(s.From, s.Request(roles: false) with { CreationRuleset = "removed-policy", CharacterLimit = 2, Cost = 10 });
        await using var loop = new Meitou.Server.ServerWorldLoop(s.Runtime);
        var endpoint = new Meitou.Server.ServerInvitationEndpoint(loop, s.Checkpoints);
        var frame = await endpoint.HandleAsync(s.To, Meitou.Networking.InvitationProtocol.Request(new(Guid.NewGuid(), Meitou.Networking.InvitationAction.Accept, offer.Id)), TestContext.Current.CancellationToken);
        Assert.Equal(Meitou.Networking.InvitationOutcome.Rejected, Meitou.Networking.InvitationProtocol.ReadReply(frame).Outcome);
        Assert.False(await loop.InvokeAsync(() => s.Recipient.IsMember(s.Sender.State.Faction), TestContext.Current.CancellationToken));
        Assert.Empty(await loop.InvokeAsync(() => s.World.CreationQualifications.For(s.Recipient.Id), TestContext.Current.CancellationToken));
    }

    static PopulationData Data() => PopulationData.Create(SyntheticTown.Database(items: true), []);
    sealed class Scene : IDisposable
    {
        public string Root { get; } = SaveSamples.Temp("invitation-transaction");
        public PopulationData Data { get; } = PlayerInvitationServiceTests.Data();
        public WorldRuntime Runtime { get; }
        public Meitou.Simulation.World World => Runtime.World;
        public WorldPlayer Sender { get; }
        public WorldPlayer Recipient { get; }
        public WorldPlayer Other { get; }
        public CharacterId[] Roles { get; }
        public CheckpointStore Store { get; }
        public WorldCheckpointCoordinator Checkpoints { get; }
        public PlayerInvitationService Service { get; }
        public AuthorityConnection From { get; }
        public AuthorityConnection To { get; }
        public Scene()
        {
            Runtime = new(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300), [new PlayerSystem(Data)]);
            Sender = World.Players.Register(new(2)); Recipient = World.Players.Register(new(3)); Other = World.Players.Register(new(4));
            World.Players.SetMembership(Sender.Id, Data.PlayerFaction, FactionPermission.All); Sender.State.Money = 100;
            var squad = SquadManagement.NewSquad(World, Data.Db, new(10, 300, 20), "Existing", Data.PlayerFaction);
            Roles = Enumerable.Range(0, 3).Select(i =>
            {
                var id = World.Characters.Spawn(new CharacterHot { Position = new(i * 10, 300, 20), Health = 42 },
                    new CharacterCold { Name = "role-" + i, RecordId = Data.Db.OfType(FcsRecordType.Character).First().StringId,
                        Faction = squad.Faction, SquadId = squad.Id, IsPlayer = true, Orders = CharacterOrders.Hold, Inventory = new Inventory() }, 0);
                World.Characters.Cold(id.Slot)!.Inventory!.Items.Add(Data.Items.Create("70-t", 3)!);
                squad.Members.Add(id); World.Players.AssignInitial(Sender.Id, id); return id;
            }).ToArray();
            squad.Leader = Roles[0]; From = World.Authority.Open(Sender.Id); To = World.Authority.Open(Recipient.Id);
            Store = new(Root); Checkpoints = new(Runtime, Data, Store, new SaveCaptureOptions { DataFiles = Data.Db.Files }); Service = new(Checkpoints);
        }
        public FactionInviteRequest Request(bool roles = true) => new(Guid.NewGuid(), Recipient.Id, Sender.State.Faction, FactionPermission.None,
            roles ? Roles.Take(2).Select(id => World.Players.ControlOf(id)!.Value).ToArray() : []);
        public void Dispose() { try { Runtime.Dispose(); } finally { if (Directory.Exists(Root)) Directory.Delete(Root, true); } }
    }

    [Fact]
    public async Task AcceptedTransfersPreserveCharactersInvalidateOldOrdersAndReplayAfterRestartWithoutRejoining()
    {
        using var s = new Scene(); var request = s.Request(); var old = request.Roles.ToArray();
        var offered = s.Service.Offer(s.From, request);
        Assert.Equal(offered.Id, s.Service.Offer(s.From, request).Id);
        Assert.True(s.World.Authority.BeginSynchronization(s.From)); Assert.True(s.World.Authority.Activate(s.From));
        var queued = s.World.Authority.Submit(s.From, 1, new SelectCommand(s.Roles.Take(2).ToArray()) { Tick = 0 }, new(old));
        var at = s.World.Characters.Current[s.Roles[0].Slot].Position; var bag = s.World.Characters.Cold(s.Roles[0].Slot)!.Inventory;
        var accepted = s.Service.Accept(s.To, offered.Id);
        Assert.Equal(InvitationStatus.Accepted, accepted.Status); Assert.Equal(2, accepted.Assigned.Length);
        Assert.True(s.Recipient.IsMember(s.Sender.State.Faction)); Assert.False(s.Recipient.HasPermission(s.Sender.State.Faction, FactionPermission.ReadFunds));
        Assert.Equal(at, s.World.Characters.Current[s.Roles[0].Slot].Position); Assert.Same(bag, s.World.Characters.Cold(s.Roles[0].Slot)!.Inventory);
        Assert.Equal(42, s.World.Characters.Current[s.Roles[0].Slot].Health); Assert.Equal(CharacterOrders.Hold, s.World.Characters.Cold(s.Roles[0].Slot)!.Orders);
        s.World.ApplyPausedInterfaceCommands(); Assert.Equal(AuthorityStatus.StaleAuthority, (await queued).Status);
        Assert.Equal(3, s.World.Characters.Count); Assert.Equal(100, s.Sender.State.Money);
        var data = Data(); var restored = s.Checkpoints.Restore(s.Store.LoadCurrent()!, data, d => [new PlayerSystem(d)], new SaveCaptureOptions { DataFiles = data.Db.Files });
        var service = new PlayerInvitationService(restored.Coordinator); var connection = s.World.Authority.Open(s.Recipient.Id);
        Assert.Equal(accepted.Assigned, service.Accept(connection, offered.Id).Assigned);
        s.World.Players.RemoveMembership(s.Recipient.Id, request.Faction);
        Assert.Equal(InvitationStatus.Accepted, service.Accept(connection, offered.Id).Status);
        Assert.False(s.World.Players.Find(s.Recipient.Id)!.IsMember(request.Faction)); Assert.Empty(s.World.Players.KnownRoles(s.Recipient.Id));
    }

    [Fact]
    public void ChangedRoleOrInviterAuthorityCannotPartiallyGrantMembershipOrTransferTheRemainingRole()
    {
        using var s = new Scene(); var request = s.Request(); var offered = s.Service.Offer(s.From, request);
        s.World.Players.SetMembership(s.Other.Id, request.Faction, FactionPermission.None);
        Assert.True(s.World.Players.TryTransfer(s.Sender.Id, request.Roles[0], s.Other.Id, out _));
        Assert.Throws<InvalidOperationException>(() => s.Service.Accept(s.To, offered.Id));
        Assert.False(s.Recipient.IsMember(request.Faction)); Assert.True(s.World.Players.Validate(request.Roles[1]));
        var membership = s.Service.Offer(s.From, s.Request(roles: false));
        s.World.Players.SetMembership(s.Sender.Id, request.Faction, FactionPermission.ReadFunds);
        s.World.Players.SetMembership(s.Sender.Id, request.Faction, FactionPermission.All);
        Assert.Throws<InvalidOperationException>(() => s.Service.Accept(s.To, membership.Id));
        Assert.False(s.Recipient.IsMember(request.Faction)); Assert.False(s.World.HasPendingDurableMutation);
    }

    [Fact]
    public void MembershipAndSponsoredCreationAreExplicitAndNeverAwardAnIndependentStart()
    {
        using var s = new Scene();
        var request = s.Request(roles: false) with { CreationRuleset = "approved", CharacterLimit = 3, Cost = 40 };
        var accepted = s.Service.Accept(s.To, s.Service.Offer(s.From, request).Id);
        Assert.Empty(accepted.Assigned); Assert.Empty(s.World.Players.KnownRoles(s.Recipient.Id));
        var grant = s.World.CreationQualifications.Find(accepted.CreationQualification)!;
        Assert.Equal(CreationQualificationKind.FactionApproved, grant.Kind); Assert.False(grant.IncludesStartResources);
        Assert.Equal(40, grant.Cost); Assert.Equal(100, s.Sender.State.Money); Assert.Equal(s.Recipient.Id, grant.Player);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, s.World.Players.AssessEntry(s.Recipient.Id).Route);
        using var only = new Scene(); var member = only.Service.Accept(only.To, only.Service.Offer(only.From, only.Request(roles: false)).Id);
        Assert.Equal(0ul, member.CreationQualification); Assert.Empty(only.World.CreationQualifications.For(only.Recipient.Id));
        Assert.Empty(only.World.Players.KnownRoles(only.Recipient.Id));
    }

    [Fact]
    public void LaterAssignmentPreservesExistingMembershipAndRejectsChangedRecipientRights()
    {
        using var s = new Scene(); int faction = s.Sender.State.Faction;
        s.World.Players.SetMembership(s.Recipient.Id, faction, FactionPermission.ReadFunds);
        ulong membership = s.Recipient.MembershipRevision(faction);
        var first = s.Service.Offer(s.From, s.Request());
        s.World.Players.SetMembership(s.Recipient.Id, faction, FactionPermission.ReadFunds | FactionPermission.SpendFunds);
        Assert.Throws<InvalidOperationException>(() => s.Service.Accept(s.To, first.Id));
        s.World.Players.SetMembership(s.Recipient.Id, faction, FactionPermission.ReadFunds);
        var current = s.Service.Offer(s.From, s.Request()); ulong before = s.Recipient.MembershipRevision(faction);
        s.Service.Accept(s.To, current.Id);
        Assert.True(before > membership); Assert.Equal(before, s.Recipient.MembershipRevision(faction));
        Assert.True(s.Recipient.HasPermission(faction, FactionPermission.ReadFunds)); Assert.False(s.Recipient.HasPermission(faction, FactionPermission.SpendFunds));
        Assert.Equal(2, s.World.Players.KnownRoles(s.Recipient.Id).Count());
    }

    [Fact]
    public void FailedAcceptancePersistenceCanResumeAfterReconnectButAnotherServiceCannotAcknowledgeMemoryState()
    {
        using var s = new Scene(); var second = new PlayerInvitationService(s.Checkpoints); var offered = s.Service.Offer(s.From, s.Request());
        Directory.CreateDirectory(s.Root);
        CharacterControl[] assigned;
        using (var locked = new FileStream(Path.Combine(s.Root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => s.Service.Accept(s.To, offered.Id));
            Assert.True(s.World.HasPendingDurableMutation); Assert.Equal(offered.Id, s.Service.PendingFor(s.To));
            Assert.Null(s.Service.Lookup(s.To, offered.Id)); Assert.Throws<InvalidOperationException>(() => second.Accept(s.To, offered.Id));
            assigned = s.World.Players.KnownRoles(s.Recipient.Id).ToArray();
        }
        var reconnected = s.World.Authority.Open(s.Recipient.Id); var resumed = s.Service.Accept(reconnected, offered.Id);
        Assert.Equal(assigned, resumed.Assigned); Assert.False(s.World.HasPendingDurableMutation); Assert.Equal(3, s.World.Characters.Count);
        Assert.NotNull(s.Store.LoadCurrent());
    }

    [Fact]
    public void CorruptInvitationOutcomeCannotReplaceTheLiveWorldAndLegacyMetadataCannotInjectOffers()
    {
        using var s = new Scene(); var invitation = s.Service.Offer(s.From, s.Request()); s.Service.Accept(s.To, invitation.Id);
        var saved = s.Store.LoadCurrent()!; var original = s.World;
        var json = JsonNode.Parse(saved.Metadata)!;
        json["Invitations"]!["Invitations"]![0]!["Assigned"]![0]!["Player"]!["Value"] = s.Sender.Id.Value;
        var data = Data();
        Assert.Throws<InvalidDataException>(() => s.Checkpoints.Restore(saved with { Metadata = Encoding.UTF8.GetBytes(json.ToJsonString()) }, data,
            d => [new PlayerSystem(d)], new SaveCaptureOptions { DataFiles = data.Db.Files }));
        Assert.Same(original, s.World);
        json = JsonNode.Parse(saved.Metadata)!; json["Version"] = 6; json["Authority"]!["Version"] = 3;
        Assert.Throws<InvalidDataException>(() => PlayerRegistrySave.PrepareCatalogue(Data(), Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void InvitingPermissionDoesNotDelegateOtherRightsAndClosedOrExpiredOffersStayClosed()
    {
        using var s = new Scene(); int faction = s.Sender.State.Faction;
        s.World.Players.SetMembership(s.Sender.Id, faction, FactionPermission.InviteMembers);
        Assert.Throws<InvalidOperationException>(() => s.Service.Offer(s.From, s.Request(roles: false) with { Permissions = FactionPermission.SpendFunds }));
        Assert.Throws<InvalidOperationException>(() => s.Service.Offer(s.From, s.Request()));
        var request = s.Request(roles: false); var offer = s.Service.Offer(s.From, request);
        Assert.True(s.World.Invitations.Close(s.Recipient.Id, offer.Id, decline: true));
        Assert.Equal(InvitationStatus.Declined, s.Service.Offer(s.From, request).Status);
        Assert.Throws<InvalidOperationException>(() => s.Service.Accept(s.To, offer.Id));
        var expired = s.Service.Offer(s.From, s.Request(roles: false) with { ExpiresAtHours = s.World.GameHours + 1 });
        s.World.GameHours += 2; Assert.Throws<InvalidOperationException>(() => s.Service.Accept(s.To, expired.Id));
        Assert.False(s.Recipient.IsMember(faction));
    }
}
