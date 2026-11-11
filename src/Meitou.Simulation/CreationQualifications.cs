namespace Meitou.Simulation;

public enum CreationQualificationKind { InitialStart, FactionApproved }
public enum CreationQualificationStatus { Available, Revoked, Consumed }
public enum CreationEligibility { Eligible, UnknownGrant, WrongPlayer, StaleRevision, Revoked, Expired, RoleHistory, MembershipChanged, ApprovalChanged, InsufficientFunds, CharacterLimit, Consumed }

/// <summary>
/// Server-approved policy, never a client-generated stat/equipment payload. Only InitialStart may award independent start resources.
/// </summary>
public sealed record CreationQualification(ulong Id, ulong Revision, PlayerId Player, CreationQualificationKind Kind,
    string Ruleset, int CharacterLimit, int Faction, long Cost, double? ExpiresAtHours,
    PlayerId Approver, ulong ApproverRevision, ulong MemberRevision, Guid ApprovalId, CreationQualificationStatus Status)
{
    public bool IncludesStartResources => Kind == CreationQualificationKind.InitialStart;
}
public sealed record CreationQualificationState(int Version, ulong LastId, ulong Revision, CreationQualification[] Grants, CreationReceipt[]? Receipts = null);
public sealed record CreationQualificationView(CreationQualification Grant, CreationEligibility Eligibility);

/// <summary>
/// World-owner qualification ledger. Issuance approves policy only; it does not create roles, debit funds or acknowledge a durable creation.
/// </summary>
public sealed partial class CreationQualifications
{
    readonly World _world;
    readonly SortedDictionary<ulong, CreationQualification> _grants = [];
    ulong _lastId, _revision;
    bool _restored;
    internal CreationQualifications(World world) => this._world = world;

    void Boundary()
    {
        if (!_world.AtTickBoundary) throw new InvalidOperationException("Creation qualifications require the serialized world boundary.");
    }
    WorldPlayer Player(PlayerId player) => _world.Players.Find(player) ?? throw new ArgumentException("Unknown player.", nameof(player));
    static void Policy(string ruleset, int limit, long cost, double? expires)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(cost);
        if (expires is { } at && (!double.IsFinite(at) || at < 0)) throw new ArgumentOutOfRangeException(nameof(expires));
    }
    static FactionPermission ApprovalRights(long cost) => FactionPermission.CreateCharacters | (cost > 0 ? FactionPermission.SpendFunds : FactionPermission.None);

    public CreationQualification? Find(ulong id) { Boundary(); return _grants.GetValueOrDefault(id); }
    public IReadOnlyList<CreationQualificationView> For(PlayerId player)
    {
        Boundary(); Player(player);
        return _grants.Values.Where(g => g.Player == player).Select(g => new CreationQualificationView(g, Assess(player, g.Id, g.Revision, 1))).ToArray();
    }

    /// <summary>
    /// Trusted server policy approval. Repeating it never re-enables a revoked grant or changes the original policy.
    /// </summary>
    public CreationQualification IssueInitial(PlayerId player, string ruleset, int characterLimit, double? expiresAtHours = null)
    {
        Boundary(); Policy(ruleset, characterLimit, 0, expiresAtHours);
        var recipient = Player(player);
        if (_grants.Values.FirstOrDefault(g => g.Player == player && g.Kind == CreationQualificationKind.InitialStart) is { } existing)
        {
            if (existing.Ruleset != ruleset || existing.CharacterLimit != characterLimit || existing.ExpiresAtHours != expiresAtHours)
                throw new InvalidOperationException("An initial qualification already exists with different policy.");
            return existing;
        }
        if (!_world.Players.RequiresExplicitControl || recipient.RoleHistory != PlayerRoleHistory.NeverAssigned || recipient.Memberships.Any())
            throw new InvalidOperationException("Initial qualification requires a new player without faction membership.");
        return Add(player, CreationQualificationKind.InitialStart, ruleset, characterLimit, -1, 0, expiresAtHours, default, 0, 0, Guid.Empty);
    }

    /// <summary>
    /// Approval ID is stable per approving player. Retries return the original qualification, including if it was revoked.
    /// </summary>
    public CreationQualification ApproveFaction(PlayerId approver, Guid approvalId, PlayerId recipient, int faction,
        string ruleset, int characterLimit, long cost, double? expiresAtHours = null)
    {
        Boundary(); Policy(ruleset, characterLimit, cost, expiresAtHours);
        var actor = Player(approver); var player = Player(recipient);
        if (approvalId == Guid.Empty) throw new ArgumentException("A stable approval ID is required.", nameof(approvalId));
        if (_grants.Values.FirstOrDefault(g => g.Approver == approver && g.ApprovalId == approvalId) is { } existing)
        {
            if (existing.Player != recipient || existing.Faction != faction || existing.Ruleset != ruleset || existing.CharacterLimit != characterLimit ||
                existing.Cost != cost || existing.ExpiresAtHours != expiresAtHours) throw new InvalidOperationException("Approval ID has a different payload.");
            return existing;
        }
        if (!actor.HasPermission(faction, ApprovalRights(cost)) || !player.IsMember(faction))
            throw new InvalidOperationException("Faction creation requires creation/spending authority and recipient membership.");
        return Add(recipient, CreationQualificationKind.FactionApproved, ruleset, characterLimit, faction, cost, expiresAtHours,
            approver, actor.MembershipRevision(faction), player.MembershipRevision(faction), approvalId);
    }

    CreationQualification Add(PlayerId player, CreationQualificationKind kind, string ruleset, int limit, int faction, long cost,
        double? expires, PlayerId approver, ulong approverRevision, ulong memberRevision, Guid approvalId)
    {
        ulong id = checked(_lastId + 1), next = checked(_revision + 1);
        var grant = new CreationQualification(id, next, player, kind, ruleset, limit, faction, cost, expires,
            approver, approverRevision, memberRevision, approvalId, CreationQualificationStatus.Available);
        _grants.Add(id, grant); _lastId = id; _revision = next;
        return grant;
    }

    /// <summary>
    /// Trusted host revocation. An old revision cannot revoke a replacement approval.
    /// </summary>
    public bool Revoke(ulong id, ulong expectedRevision)
    {
        Boundary();
        if (!_grants.TryGetValue(id, out var grant) || grant.Revision != expectedRevision || grant.Status != CreationQualificationStatus.Available) return false;
        ulong next = checked(_revision + 1);
        _grants[id] = grant with { Revision = next, Status = CreationQualificationStatus.Revoked }; _revision = next;
        return true;
    }

    /// <summary>
    /// Explicit server-policy reauthorization, not a login retry. Keeps the initial entitlement's identity and invalidates old previews.
    /// </summary>
    public CreationQualification? ReauthorizeInitial(ulong id, ulong expectedRevision, string ruleset, int characterLimit, double? expiresAtHours = null)
    {
        Boundary(); Policy(ruleset, characterLimit, 0, expiresAtHours);
        if (!_grants.TryGetValue(id, out var grant) || grant.Kind != CreationQualificationKind.InitialStart || grant.Revision != expectedRevision || grant.Status == CreationQualificationStatus.Consumed)
            return null;
        var recipient = Player(grant.Player);
        if (recipient.RoleHistory != PlayerRoleHistory.NeverAssigned || recipient.Memberships.Any()) return null;
        if (grant.Status == CreationQualificationStatus.Available && grant.Ruleset == ruleset && grant.CharacterLimit == characterLimit && grant.ExpiresAtHours == expiresAtHours)
            return grant;
        ulong next = checked(_revision + 1);
        var replacement = grant with { Revision = next, Ruleset = ruleset, CharacterLimit = characterLimit, ExpiresAtHours = expiresAtHours, Status = CreationQualificationStatus.Available };
        _grants[id] = replacement; _revision = next;
        return replacement;
    }

    public CreationEligibility Assess(PlayerId player, ulong id, ulong expectedRevision, int characterCount)
    {
        Boundary();
        if (!_grants.TryGetValue(id, out var grant)) return CreationEligibility.UnknownGrant;
        if (grant.Player != player) return CreationEligibility.WrongPlayer;
        if (grant.Revision != expectedRevision) return CreationEligibility.StaleRevision;
        if (grant.Status != CreationQualificationStatus.Available) return grant.Status == CreationQualificationStatus.Consumed ? CreationEligibility.Consumed : CreationEligibility.Revoked;
        if (characterCount < 1 || characterCount > grant.CharacterLimit) return CreationEligibility.CharacterLimit;
        if (grant.ExpiresAtHours is { } expires && _world.GameHours >= expires) return CreationEligibility.Expired;
        var recipient = Player(player);
        if (grant.Kind == CreationQualificationKind.InitialStart)
            return recipient.RoleHistory != PlayerRoleHistory.NeverAssigned ? CreationEligibility.RoleHistory
                : recipient.Memberships.Any() ? CreationEligibility.MembershipChanged : CreationEligibility.Eligible;
        if (!recipient.IsMember(grant.Faction) || recipient.MembershipRevision(grant.Faction) != grant.MemberRevision) return CreationEligibility.MembershipChanged;
        var actor = Player(grant.Approver);
        if (actor.MembershipRevision(grant.Faction) != grant.ApproverRevision || !actor.HasPermission(grant.Faction, ApprovalRights(grant.Cost)))
            return CreationEligibility.ApprovalChanged;
        return _world.Factions.Find(grant.Faction) is not { } funds || funds.Money < grant.Cost ? CreationEligibility.InsufficientFunds : CreationEligibility.Eligible;
    }

    public CreationQualificationState Capture()
    {
        Boundary();
        if (_reservations.Count != 0) throw new InvalidOperationException("Creation mutation has not produced its final receipt.");
        return new CreationQualificationState(2, _lastId, _revision, _grants.Values.ToArray(), _receipts.Values.OrderBy(r => r.Player.Value).ThenBy(r => r.RequestId).Select(Copy).ToArray());
    }

    internal static void Validate(CreationQualificationState state, PlayerRegistryState authority, int factionCount, IReadOnlySet<EntityId>? entities = null)
    {
        if (state is null || state.Version != 2 || state.Grants is null || state.Receipts is null || (ulong)state.Grants.Length != state.LastId || state.Revision < state.LastId)
            throw new InvalidDataException("Invalid creation qualification state.");
        var players = authority.Players.Select(p => p.Id).ToHashSet();
        var ids = new HashSet<ulong>(); var revisions = new HashSet<ulong>(); var initials = new HashSet<PlayerId>(); var approvals = new HashSet<(PlayerId, Guid)>();
        foreach (var grant in state.Grants)
        {
            if (grant is null || grant.Id == 0 || grant.Id > state.LastId || !ids.Add(grant.Id) || grant.Revision == 0 || grant.Revision > state.Revision ||
                !revisions.Add(grant.Revision) || !players.Contains(grant.Player) || !Enum.IsDefined(grant.Kind) || !Enum.IsDefined(grant.Status) ||
                string.IsNullOrWhiteSpace(grant.Ruleset) || grant.CharacterLimit < 1 || grant.Cost < 0 ||
                grant.ExpiresAtHours is { } at && (!double.IsFinite(at) || at < 0)) throw new InvalidDataException("Invalid creation qualification.");
            if (grant.Kind == CreationQualificationKind.InitialStart)
            {
                if (!initials.Add(grant.Player) || grant.Faction != -1 || grant.Cost != 0 || !grant.Approver.IsNone || grant.ApproverRevision != 0 ||
                    grant.MemberRevision != 0 || grant.ApprovalId != Guid.Empty) throw new InvalidDataException("Invalid initial-start qualification.");
            }
            else if (grant.Faction < 0 || grant.Faction >= factionCount || !players.Contains(grant.Approver) || grant.ApprovalId == Guid.Empty ||
                grant.ApproverRevision == 0 || grant.MemberRevision == 0 || grant.ApproverRevision > authority.Revision || grant.MemberRevision > authority.Revision ||
                !approvals.Add((grant.Approver, grant.ApprovalId))) throw new InvalidDataException("Invalid faction creation approval.");
        }
        if (!authority.ExplicitControl && (state.LastId != 0 || state.Revision != 0 || state.Grants.Length != 0))
            throw new InvalidDataException("Creation qualifications require explicit player authority.");
        if (state.Grants.Select(g => g.Revision).DefaultIfEmpty(0ul).Max() != state.Revision)
            throw new InvalidDataException("Creation qualification revision history is incomplete.");
        ValidateReceipts(state, authority, factionCount, entities);
    }

    internal void Restore(CreationQualificationState state, int factionCount)
    {
        Boundary();
        if (_restored || _grants.Count != 0 || _lastId != 0 || _revision != 0 || _world.Tick != 0 || _world.Authority.HasHistory)
            throw new InvalidOperationException("Creation qualifications restore once before admission.");
        Validate(state, _world.Players.Capture(), factionCount, _world.Entities.All.ToHashSet());
        foreach (var grant in state.Grants) _grants.Add(grant.Id, grant);
        foreach (var receipt in state.Receipts!) _receipts.Add((receipt.Player, receipt.RequestId), Copy(receipt));
        _lastId = state.LastId; _revision = state.Revision; _restored = true;
    }

    internal void Hash(ref StateHasher h)
    {
        if (_lastId == 0 && _revision == 0) return;
        h.Add(0x43724772); h.Add(_lastId); h.Add(_revision); h.Add(_grants.Count);
        foreach (var g in _grants.Values)
        {
            h.Add(g.Id); h.Add(g.Revision); h.Add(g.Player.Value); h.Add((int)g.Kind); h.Add(Rng.StableHash(g.Ruleset));
            h.Add(g.CharacterLimit); h.Add(g.Faction); h.Add(g.Cost); h.Add(g.ExpiresAtHours.HasValue);
            if (g.ExpiresAtHours is { } expiry) h.Add(BitConverter.DoubleToInt64Bits(expiry));
            h.Add(g.Approver.Value); h.Add(g.ApproverRevision); h.Add(g.MemberRevision); h.Add((int)g.Status);
            foreach (byte value in g.ApprovalId.ToByteArray()) h.Add(value);
        }
        HashReceipts(ref h);
    }
}