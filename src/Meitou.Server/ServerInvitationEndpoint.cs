using Meitou.Networking;
using Meitou.Runtime;
using Meitou.Simulation;

namespace Meitou.Server;
public sealed class ServerInvitationEndpoint
{
    readonly ServerWorldLoop _loop;
    readonly World _source;
    readonly PlayerInvitationService _service;
    readonly ServerCreationEndpoint? _creation;
    public ServerInvitationEndpoint(ServerWorldLoop loop, WorldCheckpointCoordinator checkpoints, ServerCreationEndpoint? creation = null)
    {
        checkpoints.VerifyOwner(loop.Runtime);
        this._loop = loop;
        _source = loop.Runtime.World;
        _service = new(checkpoints);
        this._creation = creation;
    }

    public async Task<WireFrame> HandleAsync(AuthorityConnection connection, WireFrame frame, CancellationToken cancellation)
    {
        var request = InvitationProtocol.ReadRequest(frame);
        var reply = await _loop.InvokeAsync(() => Handle(connection, request), cancellation).ConfigureAwait(false);
        return InvitationProtocol.Reply(reply);
    }

    InvitationReply Handle(AuthorityConnection connection, InvitationRequest request)
    {
        if (!ReferenceEquals(_source, _loop.Runtime.World) || _source.Authority.GetPhase(connection)is null)
            throw new IOException("Invitation connection was replaced.");
        var identity = new ReplicationIdentity(connection.World, connection.Player, connection.Generation);
        InvitationReply Result(InvitationOutcome outcome, FactionInvitation? invitation = null, Guid pending = default) => new(identity, request.Query, outcome, invitation is null ? [] : [new(invitation, _source.Invitations.Eligibility(invitation))], pending, []);
        Guid? waiting = _service.PendingFor(connection);
        if (_source.HasPendingDurableMutation && !(waiting is { } id && request.Action is InvitationAction.Accept or InvitationAction.Resume && (request.Invitation == id || request.Invitation == Guid.Empty)))
            return waiting is { } own ? Result(InvitationOutcome.PendingPersistence, pending: own) : Result(InvitationOutcome.Busy);
        try
        {
            switch (request.Action)
            {
                case InvitationAction.List:
                    var list = _source.Invitations.Page(connection.Player, request.After, request.Limit + 1, request.IncludeClosed).ToArray();
                    return new(identity, request.Query, InvitationOutcome.Listed, list.Take(request.Limit).ToArray(), default, [], list.Length > request.Limit ? list[request.Limit - 1].Invitation.Id : default);
                case InvitationAction.Offer:
                    if (request.Offer!.CreationRuleset is { } ruleset && _creation?.AllowsApproval(ruleset, request.Offer.CharacterLimit) != true)
                        return Result(InvitationOutcome.Rejected);
                    return Result(InvitationOutcome.Offered, _service.Offer(connection, request.Offer));
                case InvitationAction.Accept:
                case InvitationAction.Resume:
                    Guid target = request.Invitation != Guid.Empty ? request.Invitation : waiting ?? Guid.Empty;
                    if (!_source.HasPendingDurableMutation && _source.Invitations.FindFor(connection.Player, target)is { Status: InvitationStatus.Pending, Offer.CreationRuleset: { } rule } offer && _creation?.AllowsApproval(rule, offer.Offer.CharacterLimit) != true)
                        return Result(InvitationOutcome.Rejected);
                    return target == Guid.Empty ? Result(InvitationOutcome.Unknown) : Result(InvitationOutcome.Accepted, _service.Accept(connection, target));
                case InvitationAction.Decline:
                case InvitationAction.Revoke:
                    return Result(_source.Invitations.Close(connection.Player, request.Invitation, request.Action == InvitationAction.Decline) ? InvitationOutcome.Closed : InvitationOutcome.Rejected);
                case InvitationAction.Lookup:
                    var found = _service.Lookup(connection, request.Invitation);
                    return Result(found is null ? InvitationOutcome.Unknown : found.Status == InvitationStatus.Accepted ? InvitationOutcome.Accepted : InvitationOutcome.Listed, found);
                case InvitationAction.Roster:
                    if (_source.Players.Find(connection.Player)?.HasPermission(request.Faction, FactionPermission.AssignCharacters) != true)
                        return Result(InvitationOutcome.Rejected);
                    var roles = _source.Players.All.SelectMany(p => _source.Players.KnownRoles(p.Id)).Where(g => g.Faction == request.Faction && g.Entity.Value > request.AfterEntity && _source.Players.Validate(g)).OrderBy(g => g.Entity.Value).Take(request.Limit + 1).Select(g =>
                    {
                        _source.Entities.TryResolve(g.Entity, out var role);
                        var cold = _source.Characters.Cold(role.Slot)!;
                        return new AssignableRole(g, cold.Name, cold.SquadId);
                    }).ToArray();
                    return new(identity, request.Query, InvitationOutcome.Roster, [], default, roles.Take(request.Limit).ToArray(), NextEntity: roles.Length > request.Limit ? roles[request.Limit - 1].Control.Entity.Value : 0);
                default:
                    return Result(InvitationOutcome.Rejected);
            }
        }
        catch (Exception e)when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (_service.PendingFor(connection)is { } own)
                return Result(InvitationOutcome.PendingPersistence, pending: own);
            return Result(InvitationOutcome.Rejected);
        }
        catch (Exception e)when (e is InvalidOperationException or ArgumentException)
        {
            if (_loop.Runtime.Faulted)
                throw;
            return Result(InvitationOutcome.Rejected);
        }
    }
}