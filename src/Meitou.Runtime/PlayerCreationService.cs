using System.Security.Cryptography;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Meitou.Runtime;

/// <summary>Server policy resolver output. Clients select only a start ID; the host supplies the actual content and initial faction template.</summary>
public sealed record ApprovedCreationStart(NewGameStart Start, int FactionTemplate);

/// <summary>
/// Serialized world-owner creation transaction. Region preparation precedes Execute. Success is returned only after checkpoint commit.
/// A disk failure keeps a retryable frozen result; the host must retry the same request or restore a prior checkpoint.
/// </summary>
public sealed class PlayerCreationService
{
    readonly WorldCheckpointCoordinator checkpoints;
    readonly World world;
    readonly PopulationData data;
    readonly PopulationSystem population;
    readonly Func<string, string, ApprovedCreationStart?> resolve;
    Pending? pending;
    sealed record Pending(PlayerId Player, Guid Request, string Fingerprint, CreationReceipt Receipt, World.DurableMutation Barrier)
    {
        internal CapturedCheckpoint? Capture;
    }

    public PlayerCreationService(WorldCheckpointCoordinator checkpoints, Func<string, string, ApprovedCreationStart?> resolve)
    {
        this.checkpoints = checkpoints; this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        world = checkpoints.Source; data = checkpoints.Population;
        population = world.Systems.OfType<PopulationSystem>().Single();
        if (!ReferenceEquals(population.Data, data) || world.HasPendingDurableMutation) throw new InvalidOperationException("Creation service requires its active, idle world.");
    }

    public bool WaitingForPersistence => pending is not null;

    public Guid? PendingRequest(AuthorityConnection connection)
    {
        CheckConnection(connection);
        return pending is { } current && current.Player == connection.Player ? current.Request : null;
    }

    /// <summary>Resumes only the already-frozen result owned by this player; it cannot create or alter a request.</summary>
    public CreationRequestResult ResumePersistence(AuthorityConnection connection, Guid request)
    {
        CheckConnection(connection);
        return pending is { } current && current.Player == connection.Player && current.Request == request
            ? new(CreationRequestStatus.Committed, Persist(current)) : Lookup(connection, request);
    }

    void CheckConnection(AuthorityConnection connection)
    {
        checkpoints.VerifyActive();
        if (!ReferenceEquals(checkpoints.Runtime.World, world) || world.Authority.GetPhase(connection) is null)
            throw new InvalidOperationException("Creation connection or world has been replaced.");
    }

    sealed record Preparation(PlayerCreationRequest Request, CreationQualification Grant, NewGameStart Start, int Faction, ulong Seed);
    Preparation PrepareRequest(AuthorityConnection connection, PlayerCreationRequest request)
    {
        request = Freeze(request); CheckConnection(connection); world.RequireNoDurableMutation();
        if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Start)) throw new ArgumentException("Invalid creation request identity.");
        var grant = world.CreationQualifications.Find(request.Qualification) ?? throw new InvalidOperationException("Unknown creation qualification.");
        if (world.CreationQualifications.Assess(connection.Player, grant.Id, request.QualificationRevision, 1) != CreationEligibility.Eligible)
            throw new InvalidOperationException("Creation qualification is not usable.");
        var policy = resolve(grant.Ruleset, request.Start) ?? throw new InvalidOperationException("The selected start is not approved by server policy.");
        if (policy.Start.Id != request.Start) throw new InvalidOperationException("Server policy resolved a different start.");
        var start = policy.Start with { Squad = policy.Start.Squad.ToArray(), Towns = policy.Start.Towns.ToArray(), Research = policy.Start.Research.ToArray(),
            FactionRelations = policy.Start.FactionRelations.ToArray(), ForceRace = policy.Start.ForceRace.ToArray() };
        int faction = grant.Kind == CreationQualificationKind.InitialStart ? policy.FactionTemplate : grant.Faction;
        if (request.FactionName is not null)
        {
            if (grant.Kind != CreationQualificationKind.InitialStart) throw new InvalidOperationException("Faction-approved creation cannot rename its faction.");
            _ = ValidName(request.FactionName);
        }
        return new(request, grant, start, faction, Seed(connection.Player, request.Id));
    }
    ulong Seed(PlayerId player, Guid request) => Rng.Mix(world.Seed ^ Rng.StableHash(player.Value + ":" + request.ToString("N")));

    /// <summary>Approved deterministic arrival region, before navigation preparation. No entities, funds or qualifications change.</summary>
    public Vector2 Arrival(AuthorityConnection connection, PlayerCreationRequest request)
    {
        var p = PrepareRequest(connection, request);
        var place = PopulationSystem.StartPlace(data, p.Seed, p.Start, p.Faction);
        if (place.Missing) throw new InvalidOperationException("The selected start's towns are unavailable.");
        return place.Centre;
    }

    public PlayerCreationPreview Preview(AuthorityConnection connection, PlayerCreationRequest request)
    {
        var p = PrepareRequest(connection, request);
        var prepared = population.PreparePlayerSquad(world, p.Start, p.Faction, p.Seed, p.Grant.CharacterLimit, newFaction: p.Grant.Kind == CreationQualificationKind.InitialStart);
        prepared = population.CustomizePlayerSquad(world, prepared, p.Start, p.Request.Choices);
        var members = population.PreviewPlayerSquad(world, prepared, p.Start);
        if (world.CreationQualifications.Assess(connection.Player, p.Grant.Id, p.Request.QualificationRevision, members.Length) != CreationEligibility.Eligible)
            throw new InvalidOperationException("Creation qualification does not allow this roster.");
        CheckConnection(connection);
        return new(p.Request.Id, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(p.Request))), prepared.Centre,
            p.Grant.Cost, p.Grant.IncludesStartResources ? p.Start.Money : 0, p.Grant.IncludesStartResources, members);
    }

    public CreationRequestResult Lookup(AuthorityConnection connection, Guid request)
    {
        checkpoints.VerifyActive();
        if (!ReferenceEquals(checkpoints.Runtime.World, world) || world.Authority.GetPhase(connection) is null)
            throw new InvalidOperationException("Creation connection or world has been replaced.");
        if (pending is { } waiting && waiting.Player == connection.Player && waiting.Request == request)
            return new CreationRequestResult(CreationRequestStatus.PendingPersistence);
        if (world.HasPendingDurableMutation) return new CreationRequestResult(CreationRequestStatus.Busy);
        return world.CreationQualifications.Receipt(connection.Player, request) is { } receipt
            ? new CreationRequestResult(CreationRequestStatus.Committed, receipt) : new CreationRequestResult(CreationRequestStatus.Unknown);
    }

    public CreationReceipt Execute(AuthorityConnection connection, PlayerCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = Freeze(request);
        checkpoints.VerifyActive();
        if (!ReferenceEquals(checkpoints.Runtime.World, world) || world.Authority.GetPhase(connection) is null)
            throw new InvalidOperationException("Creation connection or world has been replaced.");
        if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Start)) throw new ArgumentException("A creation request ID and start selection are required.");
        string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        PlayerId player = connection.Player;
        if (pending is { } waiting)
        {
            if (waiting.Player != player || waiting.Request != request.Id)
                throw new InvalidOperationException("Another creation is waiting for persistence.");
            if (waiting.Fingerprint != fingerprint) throw new CreationRequestConflictException("Creation request ID has a different payload.");
            return Persist(waiting);
        }
        world.RequireNoDurableMutation();
        if (world.CreationQualifications.Receipt(player, request.Id) is { } previous)
        {
            if (previous.Fingerprint != fingerprint) throw new CreationRequestConflictException("Creation request ID has a different payload.");
            return previous;
        }
        checkpoints.Runtime.Synchronize();
        world.ReturnAllHeldItems();
        var proposal = PrepareRequest(connection, request);
        var grant = proposal.Grant; var start = proposal.Start;
        bool initial = grant.Kind == CreationQualificationKind.InitialStart;
        string? name = initial ? InitialFactionName(request.FactionName, proposal.Faction, player) : null;
        if (!initial && request.FactionName is not null) throw new InvalidOperationException("Faction-approved creation cannot rename its faction.");
        int faction = proposal.Faction;
        if (initial && data.Relations.PlayerFaction != -1) throw new InvalidOperationException("Initial multiplayer creation requires directed diplomacy.");
        population.FenceCreationBuilds(world);
        ulong seed = proposal.Seed;
        var prepared = population.PreparePlayerSquad(world, start, faction, seed, grant.CharacterLimit, newFaction: initial);
        prepared = population.CustomizePlayerSquad(world, prepared, start, request.Choices);
        // A connection may have closed while content preparation ran. No world mutation has occurred yet.
        if (world.Authority.GetPhase(connection) is null) throw new InvalidOperationException("Creation connection was replaced during preparation.");
        var barrier = checkpoints.BeginDurableMutation();
        CreationQualifications.Reservation? reservation = null;
        FactionDiplomacyState? beforeInitialization = null;
        bool granting = false;
        try
        {
            reservation = world.CreationQualifications.BeginUse(player, request.Id, fingerprint, grant.Id, request.QualificationRevision, prepared.CharacterCount);
            if (initial)
            {
                string identity = "creation:" + player.Value + ":" + request.Id.ToString("N");
                faction = data.FactionIndex(identity);
                if (faction < 0) faction = data.RegisterFaction(identity, name!, proposal.Faction);
                else if (faction < data.Relations.ContentCount || data.Factions[faction].Name != name ||
                    data.FactionTemplate(faction) != data.FactionTemplate(proposal.Faction) || world.Factions.Find(faction) is not null)
                    throw new InvalidOperationException("Reserved creation faction does not match this request.");
                beforeInitialization = data.Relations.Capture();
                population.InitializePlayerFaction(world, faction, start);
                prepared = population.RetargetNewFaction(world, prepared, faction);
            }
            var squad = population.MaterializePlayerSquad(world, prepared);
            // From this point unexpected failure is terminal: never continue a world with partly granted/charged creation output.
            granting = true;
            if (initial) world.Players.SetMembership(player, faction, FactionPermission.All);
            foreach (var role in squad.Members) world.Players.AssignInitial(player, role);
            var receipt = world.CreationQualifications.Complete(reservation, squad);
            reservation = null;
            world.AdvanceDurableRevision(barrier);
            pending = new Pending(player, request.Id, fingerprint, receipt, barrier);
        }
        catch
        {
            if (reservation is not null) world.CreationQualifications.Cancel(reservation);
            if (granting) { checkpoints.Runtime.FaultDurableMutation(); throw; }
            if (beforeInitialization is not null)
            {
                world.Factions.DiscardUnowned(faction);
                data.Relations.Restore(beforeInitialization);
            }
            barrier.Complete();
            throw;
        }
        return Persist(pending!);
    }

    CreationReceipt Persist(Pending operation)
    {
        operation.Capture ??= checkpoints.CaptureDurable(operation.Barrier);
        checkpoints.Commit(operation.Capture);
        operation.Barrier.Complete();
        pending = null;
        return operation.Receipt with { Roles = operation.Receipt.Roles.ToArray() };
    }

    string InitialFactionName(string? requested, int template, PlayerId player)
    {
        if (requested is not null) return ValidName(requested);
        string prefix = data.Factions[template].Name.Trim();
        if (prefix.Length is < 1 or > 32 || prefix.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) prefix = "Nameless";
        // The owner boundary serializes this choice and faction registration. Replays return the durable receipt first.
        for (int suffix = 0; ; suffix++)
        {
            string candidate = suffix == 0 ? FormattableString.Invariant($"{prefix} {player.Value}")
                : FormattableString.Invariant($"{prefix} {player.Value}-{suffix}");
            if (!FactionNameInUse(candidate)) return candidate;
        }
    }

    string ValidName(string? value)
    {
        string name = value?.Trim() ?? "";
        if (name.Length is < 1 or > 64 || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException("Invalid faction name.");
        if (FactionNameInUse(name)) throw new InvalidOperationException("Faction name is already in use.");
        return name;
    }

    bool FactionNameInUse(string name)
    {
        // Empty reserved rows left by failed assembly are not active faction names.
        for (int i = 0; i < data.Factions.Count; i++)
            if ((i < data.Relations.ContentCount || world.Factions.Find(i) is not null) &&
                string.Equals(world.Factions.Find(i)?.Name ?? data.Factions[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    static PlayerCreationRequest Freeze(PlayerCreationRequest request)
    {
        if (request.Start is null || request.Start.Length > 512 || request.FactionName?.Length > 128 || request.Choices?.Count > 1024)
            throw new ArgumentException("Creation request exceeds its limits.");
        if (request.Choices is not { Count: > 0 } choices) return request with { Choices = null };
        var members = new HashSet<int>(); var frozen = new List<CreationMemberChoice>();
        int controls = 0;
        foreach (var choice in choices)
        {
            if (choice is null || choice.Member < 0 || !members.Add(choice.Member) || choice.Name?.Length > 30)
                throw new ArgumentException("Invalid creation member choice.");
            var appearance = choice.Appearance;
            if (appearance is not null)
            {
                if (string.IsNullOrWhiteSpace(appearance.Race) || appearance.Race.Length > 512 || appearance.Sliders is null || appearance.Pickers is null ||
                    appearance.Sliders.Count > 512 || appearance.Pickers.Count > 3 || (controls += appearance.Sliders.Count + appearance.Pickers.Count) > 4096)
                    throw new ArgumentException("Invalid creation appearance choices.");
                foreach (var slider in appearance.Sliders)
                    if (slider is null || string.IsNullOrWhiteSpace(slider.Label) || slider.Label.Length > 128) throw new ArgumentException("Invalid creation control.");
                foreach (var picker in appearance.Pickers)
                    if (picker is null || picker.Record?.Length > 512) throw new ArgumentException("Invalid creation picker.");
                appearance = appearance with
                {
                    Sliders = appearance.Sliders.OrderBy(s => s.Tab).ThenBy(s => s.Kind).ThenBy(s => s.Label, StringComparer.Ordinal).ToArray(),
                    Pickers = appearance.Pickers.OrderBy(p => p.Kind).ToArray(),
                };
            }
            frozen.Add(choice with { Appearance = appearance });
        }
        return request with { Choices = frozen.OrderBy(c => c.Member).ToArray() };
    }
}
