using System.Security.Cryptography;
using System.Text.Json;

namespace Meitou.Simulation;

public enum InvitationStatus { Pending, Accepted, Declined, Revoked }
public enum InvitationEligibility { Eligible, Closed, Expired, StaleAuthority, AlreadyMember }
public sealed record FactionInviteRequest(Guid Request, PlayerId Recipient, int Faction, FactionPermission Permissions,
    CharacterControl[] Roles, string? CreationRuleset = null, int CharacterLimit = 0, long Cost = 0, double? ExpiresAtHours = null);
public sealed record FactionInvitation(Guid Id, PlayerId Inviter, ulong InviterRevision, ulong RecipientRevision, FactionInviteRequest Offer, string Fingerprint,
    Guid ApprovalId, InvitationStatus Status, CharacterControl[] Assigned, ulong CreationQualification = 0);
public sealed record FactionInvitationView(FactionInvitation Invitation, InvitationEligibility Eligibility);
public sealed record InvitationState(int Version, FactionInvitation[] Invitations);

/// <summary>
/// World-owner invitation ledger. Accepted results remain historical after membership, role or permission changes.
/// </summary>
public sealed class FactionInvitations(World world)
{
    readonly Dictionary<Guid, FactionInvitation> _invitations = [];
    const int MaximumInvitations = 16384;
    const int MaximumRoleReferences = 65536;
    int _roleReferences;
    bool _restored;
    void Boundary(bool pending = false)
    {
        if (!world.AtTickBoundary || world.HasPendingDurableMutation != pending) throw new InvalidOperationException("Invitation operation requires its world boundary.");
    }
    static FactionInviteRequest Freeze(FactionInviteRequest request)
    {
        if (request is null || request.Request == Guid.Empty || request.Recipient.IsNone || request.Faction < 0 || (request.Permissions & ~FactionPermission.All) != 0 ||
            request.Roles is null || request.Roles.Length > 1024 || request.Roles.Any(g => g.Entity.IsNone || g.Player.IsNone || g.Faction != request.Faction || g.Revision == 0) ||
            request.Roles.Select(g => g.Entity).Distinct().Count() != request.Roles.Length || request.Cost < 0 ||
            request.ExpiresAtHours is { } at && (!double.IsFinite(at) || at < 0) ||
            request.CreationRuleset is null && (request.CharacterLimit != 0 || request.Cost != 0) ||
            request.CreationRuleset is not null && (string.IsNullOrWhiteSpace(request.CreationRuleset) || request.CreationRuleset.Length > 128 || request.CharacterLimit is < 1 or > 1024 || request.Roles.Length != 0))
            throw new InvalidDataException("Invalid invitation offer.");
        return request with { Roles = request.Roles.OrderBy(g => g.Entity.Value).ToArray() };
    }
    static string Fingerprint(FactionInviteRequest request) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
    static FactionInvitation Copy(FactionInvitation invitation) => invitation with { Offer = invitation.Offer with { Roles = invitation.Offer.Roles.ToArray() }, Assigned = invitation.Assigned.ToArray() };
    bool Rights(PlayerId inviter, FactionInviteRequest offer)
    {
        var actor = world.Players.Find(inviter);
        if (actor?.HasPermission(offer.Faction, FactionPermission.InviteMembers) != true ||
            (offer.Permissions & ~actor.Memberships.First(m => m.Key == offer.Faction).Value) != 0) return false;
        if (offer.Roles.Length > 0 && !actor.HasPermission(offer.Faction, FactionPermission.AssignCharacters)) return false;
        return offer.CreationRuleset is null || actor.HasPermission(offer.Faction,
            FactionPermission.CreateCharacters | (offer.Cost > 0 ? FactionPermission.SpendFunds : FactionPermission.None));
    }
    public FactionInvitation Offer(PlayerId inviter, FactionInviteRequest request)
    {
        Boundary(); request = Freeze(request); string fingerprint = Fingerprint(request);
        if (_invitations.Values.FirstOrDefault(i => i.Inviter == inviter && i.Offer.Request == request.Request) is { } previous)
        {
            if (previous.Fingerprint != fingerprint) throw new InvalidOperationException("Invitation request has a different payload.");
            return Copy(previous);
        }
        if (_invitations.Count >= MaximumInvitations || _roleReferences + request.Roles.Length > MaximumRoleReferences || _invitations.Values.Count(i => i.Inviter == inviter && i.Status == InvitationStatus.Pending) >= 128)
            throw new InvalidOperationException("Invitation limit reached.");
        if (inviter == request.Recipient || world.Players.Find(request.Recipient) is not { } recipient || !Rights(inviter, request)
            || recipient.IsMember(request.Faction) && (request.Permissions != FactionPermission.None || request.Roles.Length == 0 && request.CreationRuleset is null)
            || request.Roles.Any(g => !world.Players.Validate(g)) || request.ExpiresAtHours is { } expires && expires <= world.GameHours)
            throw new InvalidOperationException("Invitation is not authorized.");
        var invitation = new FactionInvitation(Guid.NewGuid(), inviter, world.Players.Find(inviter)!.MembershipRevision(request.Faction), recipient.MembershipRevision(request.Faction), request, fingerprint,
            Guid.NewGuid(), InvitationStatus.Pending, []);
        _invitations.Add(invitation.Id, invitation); _roleReferences += request.Roles.Length; return Copy(invitation);
    }
    public InvitationEligibility Eligibility(FactionInvitation invitation)
    {
        if (invitation.Status != InvitationStatus.Pending) return InvitationEligibility.Closed;
        var offer = invitation.Offer;
        if (offer.ExpiresAtHours is { } expiry && world.GameHours >= expiry) return InvitationEligibility.Expired;
        var recipient = world.Players.Find(offer.Recipient)!;
        if (invitation.RecipientRevision == 0 && recipient.IsMember(offer.Faction)) return InvitationEligibility.AlreadyMember;
        if (invitation.RecipientRevision != 0 && recipient.MembershipRevision(offer.Faction) != invitation.RecipientRevision) return InvitationEligibility.StaleAuthority;
        if (!Rights(invitation.Inviter, offer) || world.Players.Find(invitation.Inviter)!.MembershipRevision(offer.Faction) != invitation.InviterRevision
            || offer.Roles.Any(g => !world.Players.Validate(g))) return InvitationEligibility.StaleAuthority;
        return InvitationEligibility.Eligible;
    }
    public IReadOnlyList<FactionInvitationView> For(PlayerId actor)
    {
        Boundary(); return _invitations.Values.Where(i => i.Inviter == actor || i.Offer.Recipient == actor).OrderBy(i => i.Id)
            .Select(i => new FactionInvitationView(Copy(i), Eligibility(i))).ToArray();
    }
    public FactionInvitation? FindFor(PlayerId actor, Guid id) => _invitations.TryGetValue(id, out var invitation)
        && (invitation.Inviter == actor || invitation.Offer.Recipient == actor) ? Copy(invitation) : null;
    public IReadOnlyList<FactionInvitationView> Page(PlayerId actor, Guid after, int limit, bool includeClosed)
    {
        Boundary();
        if (limit is < 1 or > 129) throw new ArgumentOutOfRangeException(nameof(limit));
        return _invitations.Values.Where(i => (i.Inviter == actor || i.Offer.Recipient == actor) && i.Id.CompareTo(after) > 0
            && (includeClosed || i.Status == InvitationStatus.Pending)).OrderBy(i => i.Id).Take(limit)
            .Select(i => new FactionInvitationView(Copy(i), Eligibility(i))).ToArray();
    }
    public bool Close(PlayerId actor, Guid id, bool decline)
    {
        Boundary();
        if (!_invitations.TryGetValue(id, out var invitation) || invitation.Status != InvitationStatus.Pending ||
            (decline ? invitation.Offer.Recipient != actor : invitation.Inviter != actor)) return false;
        _invitations[id] = invitation with { Status = decline ? InvitationStatus.Declined : InvitationStatus.Revoked }; return true;
    }
    internal FactionInvitation PrepareAcceptance(PlayerId recipient, Guid id)
    {
        Boundary();
        if (!_invitations.TryGetValue(id, out var invitation) || invitation.Offer.Recipient != recipient) throw new InvalidOperationException("Unknown invitation.");
        if (invitation.Status == InvitationStatus.Accepted) return Copy(invitation);
        if (Eligibility(invitation) != InvitationEligibility.Eligible) throw new InvalidOperationException("Invitation is no longer eligible.");
        var qualifications = world.CreationQualifications.Capture();
        if (invitation.Offer.CreationRuleset is not null && (qualifications.LastId == ulong.MaxValue || qualifications.Revision == ulong.MaxValue ||
            qualifications.Grants.Any(g => g.Approver == invitation.Inviter && g.ApprovalId == invitation.ApprovalId)))
            throw new InvalidOperationException("Invitation approval identity is unavailable.");
        world.Players.PreflightInvitation(invitation);
        return Copy(invitation);
    }
    internal FactionInvitation AcceptPrepared(FactionInvitation prepared)
    {
        Boundary(pending: true);
        if (!_invitations.TryGetValue(prepared.Id, out var current) || current.Status != InvitationStatus.Pending || current.Fingerprint != prepared.Fingerprint
            || Eligibility(current) != InvitationEligibility.Eligible) throw new InvalidOperationException("Invitation changed before acceptance.");
        var assigned = world.Players.AcceptInvitation(current);
        var offer = current.Offer;
        ulong qualification = offer.CreationRuleset is null ? 0 : world.CreationQualifications.ApproveFaction(current.Inviter, current.ApprovalId, offer.Recipient,
            offer.Faction, offer.CreationRuleset, offer.CharacterLimit, offer.Cost, offer.ExpiresAtHours).Id;
        var accepted = current with { Status = InvitationStatus.Accepted, Assigned = assigned, CreationQualification = qualification };
        _invitations[current.Id] = accepted; return Copy(accepted);
    }
    public InvitationState Capture() { if (!world.AtTickBoundary) throw new InvalidOperationException("Capture requires a world boundary."); return new(1, _invitations.Values.OrderBy(i => i.Id).Select(Copy).ToArray()); }
    internal static void Validate(InvitationState state, PlayerRegistryState players, CreationQualificationState qualifications, int factionCount, IReadOnlySet<EntityId> entities)
    {
        if (state?.Version != 1 || state.Invitations is null || state.Invitations.Length > MaximumInvitations) throw new InvalidDataException("Invalid invitation state.");
        var ids = new HashSet<Guid>(); var requests = new HashSet<(PlayerId, Guid)>(); var approvals = new HashSet<Guid>();
        var playerIds = players.Players.Select(p => p.Id).ToHashSet();
        int roleReferences = 0;
        foreach (var i in state.Invitations)
        {
            if (i is null || i.Id == Guid.Empty || !ids.Add(i.Id) || i.ApprovalId == Guid.Empty || !approvals.Add(i.ApprovalId) || !Enum.IsDefined(i.Status)
                || !playerIds.Contains(i.Inviter) || i.InviterRevision == 0 || i.InviterRevision > players.Revision || i.Assigned is null)
                throw new InvalidDataException("Invalid invitation identity.");
            var offer = Freeze(i.Offer);
            roleReferences += offer.Roles.Length;
            if (roleReferences > MaximumRoleReferences) throw new InvalidDataException("Invitation role-reference budget exceeded.");
            if (!offer.Roles.SequenceEqual(i.Offer.Roles) || i.RecipientRevision > players.Revision ||
                i.RecipientRevision != 0 && (offer.Permissions != FactionPermission.None || offer.Roles.Length == 0 && offer.CreationRuleset is null) ||
                !requests.Add((i.Inviter, offer.Request)) || !playerIds.Contains(offer.Recipient) || offer.Recipient == i.Inviter || offer.Faction >= factionCount || Fingerprint(offer) != i.Fingerprint
                || offer.Roles.Any(g => !entities.Contains(g.Entity) || !playerIds.Contains(g.Player) || g.Revision > players.Revision)) throw new InvalidDataException("Invalid invitation references.");
            if (i.Status != InvitationStatus.Accepted)
            {
                if (i.Assigned.Length != 0 || i.CreationQualification != 0) throw new InvalidDataException("Unaccepted invitation has an outcome.");
                continue;
            }
            if (i.Assigned.Length != offer.Roles.Length || !i.Assigned.Select(g => g.Entity).SequenceEqual(offer.Roles.Select(g => g.Entity)) ||
                i.Assigned.Any(g => g.Player != offer.Recipient || g.Faction != offer.Faction || g.Revision == 0 || g.Revision > players.Revision)
                || i.Assigned.Select(g => g.Revision).Distinct().Count() != i.Assigned.Length || i.Assigned.Where((g, k) => g.Revision <= offer.Roles[k].Revision).Any())
                throw new InvalidDataException("Invalid invitation assignment result.");
            if (offer.CreationRuleset is null ? i.CreationQualification != 0 : !qualifications.Grants.Any(g => g.Id == i.CreationQualification && g.Approver == i.Inviter
                && g.Player == offer.Recipient && g.ApprovalId == i.ApprovalId && g.Kind == CreationQualificationKind.FactionApproved && g.ApproverRevision == i.InviterRevision
                && g.Ruleset == offer.CreationRuleset && g.Faction == offer.Faction && g.Cost == offer.Cost && g.CharacterLimit == offer.CharacterLimit && g.ExpiresAtHours == offer.ExpiresAtHours))
                throw new InvalidDataException("Invalid invitation creation result.");
        }
    }
    internal void Restore(InvitationState state)
    {
        if (_restored || _invitations.Count != 0 || !world.AtTickBoundary) throw new InvalidOperationException("Invitation state restores once into a candidate world.");
        foreach (var invitation in state.Invitations) { _invitations.Add(invitation.Id, Copy(invitation)); _roleReferences += invitation.Offer.Roles.Length; }
        _restored = true;
    }
    internal void Hash(ref StateHasher h)
    {
        if (_invitations.Count == 0) return;
        h.Add(0x496E7674);
        foreach (var i in _invitations.Values.OrderBy(i => i.Id))
        {
            h.Add(Rng.StableHash(i.Id.ToString("N"))); h.Add(i.Inviter.Value); h.Add(i.InviterRevision); h.Add(Rng.StableHash(i.Fingerprint));
            h.Add(Rng.StableHash(i.ApprovalId.ToString("N")));
            h.Add(i.RecipientRevision);
            h.Add((int)i.Status); h.Add(i.CreationQualification);
            foreach (var grant in i.Assigned) { h.Add(grant.Entity.Value); h.Add(grant.Player.Value); h.Add(grant.Revision); }
        }
    }
}

public sealed partial class WorldPlayers
{
    internal void PreflightInvitation(FactionInvitation invitation)
    {
        var offer = invitation.Offer;
        var recipient = Required(offer.Recipient);
        if ((invitation.RecipientRevision == 0 ? recipient.IsMember(offer.Faction) : recipient.MembershipRevision(offer.Faction) != invitation.RecipientRevision)
            || offer.Roles.Any(g => !Validate(g))) throw new InvalidOperationException("Invitation membership or roles changed.");
        _ = checked(_revision + (ulong)offer.Roles.Length + (invitation.RecipientRevision == 0 ? 1ul : 0));
    }
    internal CharacterControl[] AcceptInvitation(FactionInvitation invitation)
    {
        PreflightInvitation(invitation); var offer = invitation.Offer;
        if (invitation.RecipientRevision == 0) SetMembership(offer.Recipient, offer.Faction, offer.Permissions);
        var assigned = new List<CharacterControl>();
        foreach (var previous in offer.Roles)
        {
            var grant = new CharacterControl(offer.Recipient, previous.Entity, offer.Faction, NextRevision());
            _controls[grant.Entity] = grant; assigned.Add(grant); PruneView(Required(previous.Player));
        }
        if (assigned.Count != 0) Required(offer.Recipient).RoleHistory = PlayerRoleHistory.Assigned;
        return assigned.ToArray();
    }
}