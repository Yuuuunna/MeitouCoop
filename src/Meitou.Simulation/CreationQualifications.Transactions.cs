using Meitou.Simulation.Factions;
namespace Meitou.Simulation;

/// <summary>
/// Historical creation result. Current role ownership/liveness may change after this receipt was committed.
/// </summary>
public sealed record CreationReceipt(PlayerId Player, Guid RequestId, string Fingerprint, ulong Qualification,
    ulong QualificationRevision, int Faction, int Squad, EntityId[] Roles, long Paid, bool IncludesStartResources);

public sealed partial class CreationQualifications
{
    readonly Dictionary<(PlayerId Player, Guid Request), CreationReceipt> _receipts = [];
    readonly Dictionary<ulong, Reservation> _reservations = [];
    internal sealed record Reservation(CreationQualification Grant, Guid Request, string Fingerprint, int Count, ulong EntityFloor);

    static CreationReceipt Copy(CreationReceipt receipt) => receipt with { Roles = receipt.Roles.ToArray() };
    public CreationReceipt? Receipt(PlayerId player, Guid request)
    {
        Boundary(); return _receipts.TryGetValue((player, request), out var receipt) ? Copy(receipt) : null;
    }
    static bool FingerprintValid(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal Reservation BeginUse(PlayerId player, Guid request, string fingerprint, ulong qualification, ulong expectedRevision, int count)
    {
        Boundary();
        if (!_world.HasPendingDurableMutation) throw new InvalidOperationException("Creation requires a durable mutation barrier.");
        if (request == Guid.Empty || !FingerprintValid(fingerprint)) throw new ArgumentException("Invalid creation request identity.");
        if (_receipts.ContainsKey((player, request)) || _reservations.ContainsKey(qualification) || _reservations.Values.Any(r => r.Grant.Player == player && r.Request == request))
            throw new InvalidOperationException("Creation request or qualification is already in use.");
        var eligibility = Assess(player, qualification, expectedRevision, count);
        if (eligibility != CreationEligibility.Eligible) throw new InvalidOperationException($"Creation qualification is no longer usable: {eligibility}.");
        var reservation = new Reservation(_grants[qualification], request, fingerprint, count, _world.Entities.LastAllocated);
        _reservations.Add(qualification, reservation);
        return reservation;
    }

    internal void Cancel(Reservation reservation)
    {
        Boundary();
        if (_reservations.TryGetValue(reservation.Grant.Id, out var current) && ReferenceEquals(current, reservation)) _reservations.Remove(reservation.Grant.Id);
    }

    internal CreationReceipt Complete(Reservation reservation, Squad squad)
    {
        Boundary();
        var grant = reservation.Grant;
        if (!_world.HasPendingDurableMutation || !_reservations.TryGetValue(grant.Id, out var current) || !ReferenceEquals(current, reservation) ||
            _grants[grant.Id] != grant || !ReferenceEquals(_world.Squads.Find(squad.Id), squad) || !squad.PlayerManaged || squad.Members.Count != reservation.Count)
            throw new InvalidOperationException("Creation reservation changed.");
        var roles = squad.Members.Select(id => _world.Players.ControlOf(id)).ToArray();
        if (roles.Any(r => r is null || r.Value.Player != grant.Player || r.Value.Faction != squad.Faction || r.Value.Entity.Value <= reservation.EntityFloor) ||
            roles.Select(r => r!.Value.Entity).Distinct().Count() != roles.Length) throw new InvalidOperationException("Creation output is not a new controlled roster.");
        if (grant.Kind == CreationQualificationKind.FactionApproved)
        {
            if (squad.Faction != grant.Faction || Assess(grant.Player, grant.Id, grant.Revision, reservation.Count) != CreationEligibility.Eligible)
                throw new InvalidOperationException("Faction creation authority changed.");
        }
        else if (_world.Players.KnownRoles(grant.Player).Count() != roles.Length || Player(grant.Player).Memberships.Count() != 1 ||
            !Player(grant.Player).IsMember(squad.Faction)) throw new InvalidOperationException("Initial creation acquired unrelated roles or memberships.");
        ulong next = checked(_revision + 1);
        var receipt = new CreationReceipt(grant.Player, reservation.Request, reservation.Fingerprint, grant.Id, grant.Revision,
            squad.Faction, squad.Id, roles.Select(r => r!.Value.Entity).ToArray(), grant.Cost, grant.IncludesStartResources);
        var consumed = grant with { Revision = next, Status = CreationQualificationStatus.Consumed };
        _receipts.EnsureCapacity(_receipts.Count + 1);
        if (grant.Cost > 0 && _world.Factions.Find(grant.Faction)?.TryDebit(grant.Cost) != true) throw new InvalidOperationException("Creation funds changed.");
        _grants[grant.Id] = consumed; _revision = next;
        _receipts.Add((grant.Player, reservation.Request), receipt);
        _reservations.Remove(grant.Id);
        return Copy(receipt);
    }

    static void ValidateReceipts(CreationQualificationState state, PlayerRegistryState authority, int factionCount, IReadOnlySet<EntityId>? entities)
    {
        var used = new HashSet<ulong>(); var requests = new HashSet<(PlayerId, Guid)>(); var roles = new HashSet<EntityId>();
        var grants = state.Grants.ToDictionary(g => g.Id);
        foreach (var receipt in state.Receipts!)
        {
            if (receipt is null || receipt.RequestId == Guid.Empty || !FingerprintValid(receipt.Fingerprint) || !requests.Add((receipt.Player, receipt.RequestId)) ||
                !used.Add(receipt.Qualification) || !grants.TryGetValue(receipt.Qualification, out var grant) || grant.Status != CreationQualificationStatus.Consumed ||
                grant.Player != receipt.Player || receipt.QualificationRevision == 0 || receipt.QualificationRevision >= grant.Revision || receipt.Paid != grant.Cost ||
                receipt.IncludesStartResources != grant.IncludesStartResources || receipt.Faction < 0 || receipt.Faction >= factionCount || receipt.Squad < 0 ||
                grant.Kind == CreationQualificationKind.FactionApproved && receipt.Faction != grant.Faction ||
                receipt.Roles is null || receipt.Roles.Length == 0 || receipt.Roles.Length > grant.CharacterLimit ||
                receipt.Roles.Any(r => r.IsNone || entities?.Contains(r) != true || !roles.Add(r)) ||
                authority.Players.FirstOrDefault(p => p.Id == receipt.Player)?.RoleHistory != PlayerRoleHistory.Assigned)
                throw new InvalidDataException("Invalid creation receipt.");
        }
        if (state.Grants.Any(g => g.Status == CreationQualificationStatus.Consumed && !used.Contains(g.Id)))
            throw new InvalidDataException("Consumed creation qualification has no receipt.");
    }

    void HashReceipts(ref StateHasher h)
    {
        foreach (var r in _receipts.Values.OrderBy(r => r.Player.Value).ThenBy(r => r.RequestId))
        {
            h.Add(r.Player.Value); foreach (byte b in r.RequestId.ToByteArray()) h.Add(b);
            h.Add(Rng.StableHash(r.Fingerprint)); h.Add(r.Qualification); h.Add(r.QualificationRevision);
            h.Add(r.Faction); h.Add(r.Squad); h.Add(r.Paid); h.Add(r.IncludesStartResources); h.Add(r.Roles.Length);
            foreach (var entity in r.Roles) h.Add(entity.Value);
        }
    }
}