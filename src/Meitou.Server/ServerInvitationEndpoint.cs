using Meitou.Networking;
using Meitou.Runtime;
using Meitou.Simulation;

namespace Meitou.Server;

public sealed class ServerInvitationEndpoint
{
    readonly ServerWorldLoop loop;
    readonly World source;
    readonly PlayerInvitationService service;
    readonly ServerCreationEndpoint? creation;
    public ServerInvitationEndpoint(ServerWorldLoop loop, WorldCheckpointCoordinator checkpoints, ServerCreationEndpoint? creation = null)
    {
        checkpoints.VerifyOwner(loop.Runtime); this.loop = loop; source = loop.Runtime.World; service = new(checkpoints); this.creation = creation;
    }
    public async Task<WireFrame> HandleAsync(AuthorityConnection connection, WireFrame frame, CancellationToken cancellation)
    {
        var request = InvitationProtocol.ReadRequest(frame);
        var reply = await loop.InvokeAsync(() => Handle(connection, request), cancellation).ConfigureAwait(false);
        return InvitationProtocol.Reply(reply);
    }
    InvitationReply Handle(AuthorityConnection connection, InvitationRequest request)
    {
        if (!ReferenceEquals(source, loop.Runtime.World) || source.Authority.GetPhase(connection) is null) throw new IOException("Invitation connection was replaced.");
        var identity = new ReplicationIdentity(connection.World, connection.Player, connection.Generation);
        InvitationReply Result(InvitationOutcome outcome, FactionInvitation? invitation = null, Guid pending = default)
            => new(identity, request.Query, outcome, invitation is null ? [] : [new(invitation, source.Invitations.Eligibility(invitation))], pending, []);
        Guid? waiting = service.PendingFor(connection);
        if (source.HasPendingDurableMutation && !(waiting is { } id && request.Action is InvitationAction.Accept or InvitationAction.Resume && (request.Invitation == id || request.Invitation == Guid.Empty)))
            return waiting is { } own ? Result(InvitationOutcome.PendingPersistence, pending: own) : Result(InvitationOutcome.Busy);
        try
        {
            switch (request.Action)
            {
                case InvitationAction.List:
                    var list = source.Invitations.Page(connection.Player, request.After, request.Limit + 1, request.IncludeClosed).ToArray();
                    return new(identity, request.Query, InvitationOutcome.Listed, list.Take(request.Limit).ToArray(), default, [], list.Length > request.Limit ? list[request.Limit - 1].Invitation.Id : default);
                case InvitationAction.Offer:
                    if (request.Offer!.CreationRuleset is { } ruleset && creation?.AllowsApproval(ruleset, request.Offer.CharacterLimit) != true) return Result(InvitationOutcome.Rejected);
                    return Result(InvitationOutcome.Offered, service.Offer(connection, request.Offer));
                case InvitationAction.Accept:
                case InvitationAction.Resume:
                    Guid target = request.Invitation != Guid.Empty ? request.Invitation : waiting ?? Guid.Empty;
                    if (!source.HasPendingDurableMutation && source.Invitations.FindFor(connection.Player, target) is { Status: InvitationStatus.Pending, Offer.CreationRuleset: { } rule } offer
                        && creation?.AllowsApproval(rule, offer.Offer.CharacterLimit) != true) return Result(InvitationOutcome.Rejected);
                    return target == Guid.Empty ? Result(InvitationOutcome.Unknown) : Result(InvitationOutcome.Accepted, service.Accept(connection, target));
                case InvitationAction.Decline:
                case InvitationAction.Revoke:
                    return Result(source.Invitations.Close(connection.Player, request.Invitation, request.Action == InvitationAction.Decline) ? InvitationOutcome.Closed : InvitationOutcome.Rejected);
                case InvitationAction.Lookup:
                    var found = service.Lookup(connection, request.Invitation); return Result(found is null ? InvitationOutcome.Unknown : found.Status == InvitationStatus.Accepted ? InvitationOutcome.Accepted : InvitationOutcome.Listed, found);
                case InvitationAction.Roster:
                    if (source.Players.Find(connection.Player)?.HasPermission(request.Faction, FactionPermission.AssignCharacters) != true) return Result(InvitationOutcome.Rejected);
                    var roles = source.Players.All.SelectMany(p => source.Players.KnownRoles(p.Id)).Where(g => g.Faction == request.Faction && g.Entity.Value > request.AfterEntity && source.Players.Validate(g))
                        .OrderBy(g => g.Entity.Value).Take(request.Limit + 1).Select(g =>
                        {
                            source.Entities.TryResolve(g.Entity, out var role); var cold = source.Characters.Cold(role.Slot)!;
                            return new AssignableRole(g, cold.Name, cold.SquadId);
                        }).ToArray();
                    return new(identity, request.Query, InvitationOutcome.Roster, [], default, roles.Take(request.Limit).ToArray(),
                        NextEntity: roles.Length > request.Limit ? roles[request.Limit - 1].Control.Entity.Value : 0);
                default: return Result(InvitationOutcome.Rejected);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (service.PendingFor(connection) is { } own) return Result(InvitationOutcome.PendingPersistence, pending: own);
            return Result(InvitationOutcome.Rejected);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            if (loop.Runtime.Faulted) throw;
            return Result(InvitationOutcome.Rejected);
        }
    }
}
