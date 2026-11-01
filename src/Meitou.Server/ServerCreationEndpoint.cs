using System.Numerics;
using Meitou.Networking;
using Meitou.Runtime;
using Meitou.Simulation;

namespace Meitou.Server;

public sealed record ServerCreationRuleset(string Id, int CharacterLimit, IReadOnlyList<ApprovedCreationStart> Starts);

/// <summary>One active world's approved starts and private creation transactions. The client supplies choices, never a faction template, seed, funds or attributes.</summary>
public sealed class ServerCreationEndpoint
{
    readonly ServerWorldLoop loop;
    readonly World source;
    readonly Dictionary<string, ServerCreationRuleset> rules;
    readonly string? initialRuleset;
    readonly PlayerCreationService service;
    readonly WorldViewCodec codec;
    readonly Func<AuthorityConnection, Vector2, CancellationToken, Task<bool>> prepare;
    public ServerCreationEndpoint(ServerWorldLoop loop, WorldCheckpointCoordinator checkpoints, PopulationData data,
        IEnumerable<ServerCreationRuleset> rulesets, Func<AuthorityConnection, Vector2, CancellationToken, Task<bool>> prepare,
        string? initialRuleset = null)
    {
        checkpoints.VerifyOwner(loop.Runtime);
        this.loop = loop; source = loop.Runtime.World; this.prepare = prepare; this.initialRuleset = initialRuleset; codec = new(data.Db);
        if (data.Relations.PlayerFaction != -1) throw new InvalidOperationException("Server creation requires directed multiplayer diplomacy.");
        rules = new(StringComparer.Ordinal);
        foreach (var rule in rulesets)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 128 || rule.CharacterLimit is < 1 or > 1024 || rule.CharacterLimit > data.Bodies.Constants.MaxSquadSize
                || rule.Starts.Count is < 1 or > 1024 || rule.Starts.Any(s => s is null || string.IsNullOrWhiteSpace(s.Start.Id) || s.FactionTemplate < 0 || s.FactionTemplate >= data.Factions.Count)
                || rule.Starts.Select(s => s.Start.Id).Distinct(StringComparer.Ordinal).Count() != rule.Starts.Count)
                throw new ArgumentException("Invalid creation ruleset.", nameof(rulesets));
            var starts = rule.Starts.Select(s => s with { Start = s.Start with { Squad = s.Start.Squad.ToArray(), Towns = s.Start.Towns.ToArray(), Research = s.Start.Research.ToArray(),
                FactionRelations = s.Start.FactionRelations.ToArray(), ForceRace = s.Start.ForceRace.ToArray() } }).ToArray();
            if (!rules.TryAdd(rule.Id, rule with { Starts = starts })) throw new ArgumentException("Duplicate creation ruleset.", nameof(rulesets));
        }
        if (initialRuleset is not null && !rules.ContainsKey(initialRuleset)) throw new ArgumentException("Initial ruleset is unavailable.", nameof(initialRuleset));
        service = new(checkpoints, (policy, start) => rules.GetValueOrDefault(policy)?.Starts.FirstOrDefault(s => s.Start.Id == start));
    }
    void Check(AuthorityConnection connection)
    {
        if (!ReferenceEquals(source, loop.Runtime.World) || source.Authority.GetPhase(connection) is null) throw new IOException("Creation session was replaced.");
    }
    static ReplicationIdentity Identity(AuthorityConnection c) => new(c.World, c.Player, c.Generation);
    internal bool AllowsApproval(string ruleset, int count) => rules.TryGetValue(ruleset, out var rule) && count > 0 && count <= rule.CharacterLimit;
    static CreationReply Failure(AuthorityConnection c, Guid query, Guid request, ulong revision, CreationFailure failure)
        => new(Identity(c), query, request, revision, CreationOutcome.Rejected, failure);
    CreationFailure Eligibility(AuthorityConnection c, PlayerCreationRequest request)
    {
        var grant = source.CreationQualifications.Find(request.Qualification);
        if (grant is null || source.CreationQualifications.Assess(c.Player, grant.Id, request.QualificationRevision, 1) != CreationEligibility.Eligible) return CreationFailure.Qualification;
        return AllowsApproval(grant.Ruleset, grant.CharacterLimit) && rules[grant.Ruleset].Starts.Any(s => s.Start.Id == request.Start) ? CreationFailure.None : CreationFailure.Policy;
    }
    CreationReply Result(AuthorityConnection c, Guid query, Guid request, ulong revision, CreationRequestResult result, CreationFailure error = CreationFailure.None)
        => new(Identity(c), query, request, revision, result.Status switch
        {
            CreationRequestStatus.Committed => CreationOutcome.Committed, CreationRequestStatus.PendingPersistence => CreationOutcome.PendingPersistence,
            CreationRequestStatus.Busy => CreationOutcome.Busy, _ => CreationOutcome.Unknown,
        }, error, Receipt: result.Receipt);

    public async Task<WireFrame> HandleAsync(AuthorityConnection connection, WireFrame frame, CancellationToken cancellation)
    {
        CreationReply reply;
        if (frame.Kind == WireMessage.CreationCatalogQuery)
        {
            var query = CreationProtocol.ReadCatalog(frame);
            reply = await loop.InvokeAsync(() => Catalog(connection, query), cancellation).ConfigureAwait(false);
        }
        else if (frame.Kind == WireMessage.CreationLookup)
        {
            var query = CreationProtocol.ReadLookup(frame);
            reply = await loop.InvokeAsync(() =>
            {
                Check(connection);
                Guid request = query.Request == Guid.Empty ? service.PendingRequest(connection) ?? Guid.Empty : query.Request;
                try { return Result(connection, query.Query, request, 0, query.Resume ? service.ResumePersistence(connection, request) : service.Lookup(connection, request)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                { return Result(connection, query.Query, request, 0, service.Lookup(connection, request), CreationFailure.Persistence); }
            }, cancellation).ConfigureAwait(false);
        }
        else
        {
            var query = CreationProtocol.ReadDraft(frame);
            bool commit = frame.Kind == WireMessage.CreationCommit;
            reply = await Draft(connection, query, commit, cancellation).ConfigureAwait(false);
        }
        try { return CreationProtocol.Reply(reply, codec); }
        catch (InvalidDataException) when (reply.Outcome is CreationOutcome.Preview or CreationOutcome.Catalog)
        { return CreationProtocol.Reply(Failure(connection, reply.Query, reply.Request, reply.DraftRevision, CreationFailure.Preparation), codec); }
    }

    CreationReply Catalog(AuthorityConnection c, CreationCatalogQuery query)
    {
        Check(c);
        if (source.HasPendingDurableMutation) return new(Identity(c), query.Query, Guid.Empty, 0, CreationOutcome.Busy);
        CreationQualification? grant;
        if (query.Qualification == 0)
        {
            grant = source.CreationQualifications.For(c.Player).Select(v => v.Grant).FirstOrDefault(g => g.Kind == CreationQualificationKind.InitialStart);
            if (grant is null)
            {
                var player = source.Players.Find(c.Player)!;
                if (player.RoleHistory != PlayerRoleHistory.NeverAssigned || player.Memberships.Any()) return Failure(c, query.Query, Guid.Empty, 0, CreationFailure.Qualification);
                if (initialRuleset is null) return Failure(c, query.Query, Guid.Empty, 0, CreationFailure.Policy);
                var rule = rules[initialRuleset]; grant = source.CreationQualifications.IssueInitial(c.Player, rule.Id, rule.CharacterLimit);
            }
        }
        else grant = source.CreationQualifications.Find(query.Qualification);
        ulong revision = query.Qualification == 0 ? grant?.Revision ?? 0 : query.QualificationRevision;
        if (grant is null || source.CreationQualifications.Assess(c.Player, grant.Id, revision, 1) != CreationEligibility.Eligible)
            return Failure(c, query.Query, Guid.Empty, 0, CreationFailure.Qualification);
        if (!rules.TryGetValue(grant.Ruleset, out var policy) || !AllowsApproval(grant.Ruleset, grant.CharacterLimit)) return Failure(c, query.Query, Guid.Empty, 0, CreationFailure.Policy);
        return new(Identity(c), query.Query, Guid.Empty, 0, CreationOutcome.Catalog, Catalog: new(new(grant, CreationEligibility.Eligible),
            policy.Starts.Select(s => new CreationStartOption(s.Start.Id, string.IsNullOrWhiteSpace(s.Start.Name) ? s.Start.Id : s.Start.Name,
                s.Start.Description, grant.IncludesStartResources ? s.Start.Money : 0, s.Start.ForceRace.Select(r => r.Id).ToArray())).ToArray()));
    }

    async Task<CreationReply> Draft(AuthorityConnection c, CreationDraftQuery query, bool commit, CancellationToken cancellation)
    {
        var ready = await loop.InvokeAsync(() =>
        {
            Check(c); var previous = service.Lookup(c, query.Request.Id);
            if (previous.Status != CreationRequestStatus.Unknown)
            {
                if (commit && previous.Status is CreationRequestStatus.Committed or CreationRequestStatus.PendingPersistence)
                    return (Reply: Execute(c, query), Centre: default(Vector2));
                return (Reply: Result(c, query.Query, query.Request.Id, query.DraftRevision, previous), Centre: default(Vector2));
            }
            var eligibility = Eligibility(c, query.Request);
            if (eligibility != CreationFailure.None) return (Reply: Failure(c, query.Query, query.Request.Id, query.DraftRevision, eligibility), Centre: default(Vector2));
            try { return (Reply: (CreationReply?)null, Centre: service.Arrival(c, query.Request)); }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException)
            { return (Reply: Failure(c, query.Query, query.Request.Id, query.DraftRevision, CreationFailure.InvalidRequest), Centre: default(Vector2)); }
        }, cancellation).ConfigureAwait(false);
        if (ready.Reply is not null) return ready.Reply;
        try
        {
            if (!await prepare(c, ready.Centre, cancellation).ConfigureAwait(false)) return Failure(c, query.Query, query.Request.Id, query.DraftRevision, CreationFailure.Preparation);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Failure(c, query.Query, query.Request.Id, query.DraftRevision, CreationFailure.Preparation); }
        return await loop.InvokeAsync(() =>
        {
            Check(c);
            var previous = service.Lookup(c, query.Request.Id);
            if (previous.Status != CreationRequestStatus.Unknown)
                return commit && previous.Status is CreationRequestStatus.Committed or CreationRequestStatus.PendingPersistence ? Execute(c, query)
                    : Result(c, query.Query, query.Request.Id, query.DraftRevision, previous);
            var eligibility = Eligibility(c, query.Request);
            if (eligibility != CreationFailure.None) return Failure(c, query.Query, query.Request.Id, query.DraftRevision, eligibility);
            if (commit) return Execute(c, query);
            try { return new CreationReply(Identity(c), query.Query, query.Request.Id, query.DraftRevision, CreationOutcome.Preview, Preview: service.Preview(c, query.Request)); }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException)
            { return Failure(c, query.Query, query.Request.Id, query.DraftRevision, CreationFailure.InvalidRequest); }
        }, cancellation).ConfigureAwait(false);
    }

    CreationReply Execute(AuthorityConnection c, CreationDraftQuery query)
    {
        var before = service.Lookup(c, query.Request.Id);
        try { return new(Identity(c), query.Query, query.Request.Id, query.DraftRevision, CreationOutcome.Committed, Receipt: service.Execute(c, query.Request)); }
        catch (CreationRequestConflictException) { return Failure(c, query.Query, query.Request.Id, query.DraftRevision, CreationFailure.Conflict); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (source.HasPendingDurableMutation) return Result(c, query.Query, query.Request.Id, query.DraftRevision, service.Lookup(c, query.Request.Id), CreationFailure.Persistence);
            return Failure(c, query.Query, query.Request.Id, query.DraftRevision, CreationFailure.Preparation);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            if (loop.Runtime.Faulted) throw;
            if (source.HasPendingDurableMutation && service.PendingRequest(c) == query.Request.Id)
                return Result(c, query.Query, query.Request.Id, query.DraftRevision, service.Lookup(c, query.Request.Id), CreationFailure.Persistence);
            return Failure(c, query.Query, query.Request.Id, query.DraftRevision,
                before.Status is CreationRequestStatus.Committed or CreationRequestStatus.PendingPersistence ? CreationFailure.Conflict : CreationFailure.InvalidRequest);
        }
    }
}
