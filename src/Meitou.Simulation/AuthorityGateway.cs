using System.Security.Cryptography;
using System.Text.Json;
using Meitou.Diagnostics;

namespace Meitou.Simulation;

public enum AdmissionPhase { Preparing, Synchronizing, Active }
public enum PublicationRetentionFailure { None, ConnectionReplaced, Preparing, DurableMutation, MissingPublication, NoRole, MembershipChanged, RoleGrantChanged, RoleStatusChanged }

public enum AuthorityStatus
{
    Evaluated, InvalidConnection, NotActive, InvalidRequest, Unsupported, Conflict, Expired,
    Busy, StaleAuthority, NoActiveRole, WorldClosed, WorldFaulted,
}

/// <summary>
/// Evaluated means every input handler ran; individual gameplay rules may still reject the action.
/// </summary>
public sealed record AuthorityReceipt(ulong Request, AuthorityStatus Status, ulong Operation, ulong Revision, long Tick);

/// <summary>
/// Grants and target faction observed in the client's authorized baseline, not inferred at submission.
/// </summary>
public sealed record AuthorityContext(IReadOnlyList<CharacterControl> Characters, int Faction = -1, ulong MembershipRevision = 0);
public sealed record PlayerAdmissionState(ulong Sequence, AdmissionPhase Phase, bool Busy, PlayerEntryAssessment? Entry,
    PlayerRoleView[] Roles, CreationQualificationView[] Qualifications, ulong CommandHighWater);

internal sealed record ViewMembership(int Faction, FactionPermission Permissions, ulong Revision);
internal sealed record ViewGrantStamp(PlayerRoleView[] Roles, ViewMembership[] Memberships);

/// <summary>
/// Opaque trusted-host handle. A transport retains it after authentication; clients cannot construct it.
/// </summary>
public sealed class AuthorityConnection
{
    internal AuthorityConnection(Guid world, PlayerId player, ulong generation) => (World, Player, Generation) = (world, player, generation);
    public Guid World { get; }
    public PlayerId Player { get; }
    public ulong Generation { get; }
    internal AdmissionPhase Phase;
    internal ulong ViewSequence;
    internal ulong AdmissionSequence;
    internal readonly SortedDictionary<ulong, ViewGrantStamp> PublishedGrants = [];
    internal ulong PinnedPublication;
    internal ViewGrantStamp? PinnedGrant;
    internal HashSet<CharacterId> PublishedCharacters = [];
    internal HashSet<string> PublishedSites = new(StringComparer.Ordinal);
}

/// <summary>
/// One world's bounded, in-memory request ledger. Open/admission/process run on the serialized world owner.
/// Submit/Close/GetPhase/HighWater may run on transport threads. This is neither authentication nor a wire decoder.
/// </summary>
public sealed class AuthorityGateway : IDisposable
{
    readonly World _world;
    readonly Lock _gate = new();
    readonly Guid _incarnation = Guid.NewGuid();
    readonly Dictionary<PlayerId, Ledger> _players = [];
    readonly Queue<Pending> _pending = [];
    readonly Dictionary<PlayerId, ulong> _closed = [];
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, RespectNullableAnnotations = true, Converters = { new CommandAppearanceJson() } };
    const int MaxPayload = 1024 * 1024, MaxPending = 256, MaxBytes = 8 * MaxPayload, MaxReceipts = 128;
    int _queuedBytes;
    ulong _operation;
    bool _disposed, _faulted, _processing;
    long _observedTick;
    ulong _observedRevision;

    sealed class Ledger
    {
        public ulong Generation, HighWater;
        public AuthorityConnection? Connection;
        public bool NeedsCleanup;
        public readonly Dictionary<ulong, Entry> Receipts = [];
        public readonly Queue<ulong> Completed = [];
    }

    sealed record Entry(byte[] Fingerprint)
    {
        public TaskCompletionSource<AuthorityReceipt> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    sealed record Pending(AuthorityConnection Connection, ulong Number, SimCommand Command,
        AuthorityContext Context, Entry Entry, int Bytes);

    internal AuthorityGateway(World world) => this._world = world;
    internal bool HasHistory { get { lock (_gate) return _players.Count != 0 || _disposed || _faulted; } }

    /// <summary>
    /// Only a trusted host calls this after authenticating and resolving an existing persistent player.
    /// </summary>
    public AuthorityConnection Open(PlayerId player)
    {
        lock (_gate)
        {
            EnsureOwnerBoundary();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_faulted) throw new InvalidOperationException("The authority world has faulted.");
            if (_world.Players.Find(player) is null) throw new ArgumentException("Unknown player.", nameof(player));
            if (!_players.TryGetValue(player, out var ledger)) _players.Add(player, ledger = new Ledger());
            if (ledger.Connection is { } previous) _world.MovementTrace?.Close(previous.Player.Value, previous.Generation);
            var connection = new AuthorityConnection(_incarnation, player, checked(++ledger.Generation));
            if (ledger.Generation > 1) ledger.NeedsCleanup = true;
            ledger.Connection = connection;
            return connection;
        }
    }

    void Cleanup(PlayerId player, Ledger ledger)
    {
        if (!ledger.NeedsCleanup || _world.HasPendingDurableMutation) return;
        _world.ResetPlayerInteractions(player);
        ledger.NeedsCleanup = false;
    }

    public AdmissionPhase? GetPhase(AuthorityConnection connection)
    {
        lock (_gate) return Current(connection) ? connection.Phase : null;
    }

    public bool IsActive(PlayerId player)
    {
        lock (_gate) return !_disposed && !_faulted && _players.TryGetValue(player, out var ledger)
            && ledger.Connection?.Phase == AdmissionPhase.Active;
    }

    /// <summary>
    /// World-owner query for an authenticated preparation session; identity comes only from its current connection.
    /// </summary>
    public PlayerEntryAssessment? AssessEntry(AuthorityConnection connection)
    {
        lock (_gate)
        {
            if (!Current(connection)) return null;
            EnsureOwnerBoundary();
            return _world.Players.AssessEntry(connection.Player);
        }
    }

    /// <summary>
    /// Private preparation-session view. Qualification issuance is a separate trusted policy/administrative operation.
    /// </summary>
    public IReadOnlyList<CreationQualificationView>? CreationOptions(AuthorityConnection connection)
    {
        lock (_gate)
        {
            if (!Current(connection)) return null;
            EnsureOwnerBoundary();
            return _world.CreationQualifications.For(connection.Player);
        }
    }

    /// <summary>
    /// Private preparation status. No arbitrary world view is exposed before admission.
    /// </summary>
    public PlayerAdmissionState? ReadAdmission(AuthorityConnection connection)
    {
        lock (_gate)
        {
            EnsureOwnerBoundary();
            if (!Current(connection)) return null;
            ulong sequence = connection.AdmissionSequence = checked(connection.AdmissionSequence + 1);
            if (_world.HasPendingDurableMutation) return new(sequence, connection.Phase, true, null, [], [], _players[connection.Player].HighWater);
            Cleanup(connection.Player, _players[connection.Player]);
            if (connection.Phase != AdmissionPhase.Preparing && !HasRole(connection.Player)) connection.Phase = AdmissionPhase.Preparing;
            return new(sequence, connection.Phase, false, _world.Players.AssessEntry(connection.Player), DescribeRoles(connection.Player),
                _world.CreationQualifications.For(connection.Player).ToArray(), _players[connection.Player].HighWater);
        }
    }

    PlayerRoleView[] DescribeRoles(PlayerId player) => _world.Players.KnownRoles(player).Select(grant =>
    {
        bool loaded = _world.Entities.TryResolve(grant.Entity, out var character);
        var location = _world.Players.LocateRole(player, grant.Entity);
        return new PlayerRoleView(grant.Entity, character, grant.Faction, grant.Revision, _world.Players.Validate(grant), location?.Availability == PlayerRoleAvailability.Dead)
        { Availability = location?.Availability ?? PlayerRoleAvailability.Unavailable };
    }).ToArray();

    public PlayerRoleLocation? LocateRole(AuthorityConnection connection, EntityId entity)
    {
        lock (_gate) { EnsureOwnerBoundary(); return Current(connection) ? _world.Players.LocateRole(connection.Player, entity) : null; }
    }

    public ulong HighWater(AuthorityConnection connection)
    {
        lock (_gate) return Current(connection) ? _players[connection.Player].HighWater : 0;
    }

    /// <summary>
    /// Serialized world-owner capture, rechecking admission and ownership on every publication.
    /// </summary>
    public PlayerWorldView? CaptureView(AuthorityConnection connection, PlayerViewRequest request, PlayerObservationRules rules)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
        {
            EnsureOwnerBoundary();
            if (!Current(connection) || connection.Phase == AdmissionPhase.Preparing || _world.HasPendingDurableMutation) return null;
            if (!HasRole(connection.Player)) { connection.Phase = AdmissionPhase.Preparing; return null; }
            _world.MovementTrace?.Emit(new(LatencyStage.CaptureStarted, connection.World, connection.Player.Value, connection.Generation,
                Publication: connection.ViewSequence + 1));
            var view = _world.CapturePlayerView(connection, request, rules);
            connection.PublishedCharacters = view.Snapshot.Characters.Select(c => c.Id).ToHashSet();
            connection.PublishedSites = view.Snapshot.Buildings.Select(b => b.Id).Concat(view.Snapshot.DoorPicks.Select(d => d.Building))
                .Concat(view.Snapshot.Beds.Select(b => b.Id)).Concat(view.Snapshot.Cages.Select(c => c.Id))
                .Concat(view.Snapshot.Containers.Select(c => c.Id)).ToHashSet(StringComparer.Ordinal);
            connection.ViewSequence = checked(connection.ViewSequence + 1);
            connection.PublishedGrants.Add(connection.ViewSequence, new(view.Roles.ToArray(),
                view.Factions.Select(f => new ViewMembership(f.Faction, f.Permissions, f.MembershipRevision)).ToArray()));
            while (connection.PublishedGrants.Count > 8) connection.PublishedGrants.Remove(connection.PublishedGrants.Keys.First());
            var publication = view with { Publication = connection.ViewSequence };
            _world.MovementTrace?.Publication(connection, publication);
            _world.MovementTrace?.Emit(new(LatencyStage.CaptureFinished, connection.World, connection.Player.Value, connection.Generation,
                Publication: connection.ViewSequence, Revision: _world.StateRevision));
            return publication;
        }
    }

    public void Close(AuthorityConnection connection)
    {
        lock (_gate) if (Current(connection))
        {
            _players[connection.Player].Connection = null;
            connection.PinnedPublication = 0;
            connection.PinnedGrant = null;
            _closed[connection.Player] = connection.Generation;
        }
    }

    /// <summary>
    /// Read-only affordance query, scoped to an active viewer's current actor and visible NPC.
    /// </summary>
    public PlayerTalkHint QueryTalk(AuthorityConnection connection, CharacterId speaker, CharacterId target, CharacterControl grant,
        ulong membershipRevision, PlayerViewRequest observation, PlayerObservationRules rules)
    {
        lock (_gate)
        {
            EnsureOwnerBoundary();
            if (!Current(connection) || connection.Phase != AdmissionPhase.Active || _world.HasPendingDurableMutation)
                return new(TalkHintStatus.Unknown, _world.StateRevision);
            if (grant.Player != connection.Player || _world.Players.ControlOf(speaker) != grant || membershipRevision == 0 ||
                _world.Players.Find(connection.Player)?.MembershipRevision(grant.Faction) != membershipRevision)
                return new(TalkHintStatus.StaleAuthority, _world.StateRevision);
            if (!connection.PublishedCharacters.Contains(target) || !_world.CanObserveCharacter(connection.Player, target, observation, rules) || _world.TalkBusy(target, connection.Player))
                return new(TalkHintStatus.Unknown, _world.StateRevision);
            return new(_world.CanTalk(target, speaker) ? TalkHintStatus.Available : TalkHintStatus.Unavailable, _world.StateRevision);
        }
    }

    /// <summary>
    /// Trusted publisher query for previously visible IDs, never an unrestricted remote lookup.
    /// </summary>
    public bool ConfirmedDestroyed(AuthorityConnection connection, CharacterId character)
    {
        lock (_gate) { EnsureOwnerBoundary(); return Current(connection) && _world.Characters.ConfirmedDestroyed(character); }
    }

    /// <summary>
    /// Current host publication withdrawing a replica on loss of admission or a durable-write barrier.
    /// </summary>
    public ulong? WithdrawView(AuthorityConnection connection)
    {
        lock (_gate)
        {
            EnsureOwnerBoundary();
            if (!Current(connection)) return null;
            connection.PublishedGrants.Clear();
            connection.PinnedPublication = 0;
            connection.PinnedGrant = null;
            connection.PublishedCharacters.Clear();
            connection.PublishedSites.Clear();
            return connection.ViewSequence = checked(connection.ViewSequence + 1);
        }
    }

    /// <summary>
    /// Trusted replication host calls only after validating the exact publication/hash ACK. Rechecks captured role and membership grants before opening control.
    /// </summary>
    public bool ActivateAfterView(AuthorityConnection connection, ulong publication)
    {
        lock (_gate)
        {
            EnsureOwnerBoundary();
            if (!Current(connection) || connection.Phase != AdmissionPhase.Synchronizing || _world.HasPendingDurableMutation ||
                !TryPublicationStamp(connection, publication, out var stamp)) return false;
            if (!HasRole(connection.Player)) { connection.Phase = AdmissionPhase.Preparing; return false; }
            var player = _world.Players.Find(connection.Player)!;
            var memberships = player.Memberships.Select(m => new ViewMembership(m.Key, m.Value, player.MembershipRevision(m.Key)));
            if (!stamp.Memberships.SequenceEqual(memberships)) return false;
            var roles = DescribeRoles(connection.Player);
            if (!stamp.Roles.SequenceEqual(roles)) return false;
            connection.Phase = AdmissionPhase.Active; return true;
        }
    }

    /// <summary>
    /// While a publication awaits its ACK, recheck its authority without capturing or retiring grant stamps.
    /// </summary>
    public bool CanRetainPublishedView(AuthorityConnection connection, ulong publication)
    {
        lock (_gate)
        {
            EnsureOwnerBoundary();
            PublicationRetentionFailure failure;
            if (!Current(connection)) failure = PublicationRetentionFailure.ConnectionReplaced;
            else if (connection.Phase == AdmissionPhase.Preparing) failure = PublicationRetentionFailure.Preparing;
            else if (_world.HasPendingDurableMutation) failure = PublicationRetentionFailure.DurableMutation;
            else if (!TryPublicationStamp(connection, publication, out var stamp)) failure = PublicationRetentionFailure.MissingPublication;
            else if (!HasRole(connection.Player)) failure = PublicationRetentionFailure.NoRole;
            else
            {
                var player = _world.Players.Find(connection.Player)!;
                var roles = DescribeRoles(connection.Player);
                if (!stamp.Memberships.SequenceEqual(player.Memberships.Select(m => new ViewMembership(m.Key, m.Value, player.MembershipRevision(m.Key))))) failure = PublicationRetentionFailure.MembershipChanged;
                else if (stamp.Roles.SequenceEqual(roles)) return true;
                else if (stamp.Roles.Length == roles.Length && stamp.Roles.Zip(roles).All(pair => pair.First.Entity == pair.Second.Entity && pair.First.Faction == pair.Second.Faction && pair.First.Revision == pair.Second.Revision))
                {
                    failure = PublicationRetentionFailure.RoleStatusChanged;
                    foreach (var pair in stamp.Roles.Zip(roles).Where(pair => pair.First != pair.Second))
                    {
                        static ulong Status(PlayerRoleView role) => (role.Available ? 1ul : 0) | (role.Dead ? 2ul : 0) | ((ulong)role.Availability << 2);
                        _world.MovementTrace?.Emit(new(LatencyStage.PublicationRetentionRejected, connection.World, connection.Player.Value, connection.Generation,
                            Entity: pair.First.Entity.Value, Publication: publication, Tick: _world.Tick,
                            Revision: (Status(pair.First) << 32) | Status(pair.Second), Value: (long)failure));
                    }
                }
                else failure = PublicationRetentionFailure.RoleGrantChanged;
            }
            _world.MovementTrace?.Emit(new(LatencyStage.PublicationRetentionRejected, connection.World, connection.Player.Value, connection.Generation,
                Publication: publication, Tick: _world.Tick, Value: (long)failure));
            return false;
        }
    }

    /// <summary>Pin one in-flight baseline's grant stamp while newer motion captures retire ordinary history.
    /// This retains evidence, not permission: every use still revalidates current grants.</summary>
    public bool PinPublishedView(AuthorityConnection connection, ulong publication)
    {
        lock (_gate)
        {
            EnsureOwnerBoundary();
            if (connection.PinnedPublication != 0 && connection.PinnedPublication != publication) return false;
            if (!CanRetainPublishedView(connection, publication) || !TryPublicationStamp(connection, publication, out var stamp)) return false;
            connection.PinnedPublication = publication;
            connection.PinnedGrant = stamp;
            return true;
        }
    }

    /// <summary>
    /// Release only the named transfer's evidence; a late completion cannot release a newer pin.
    /// </summary>
    public void ReleasePublishedView(AuthorityConnection connection, ulong publication)
    {
        lock (_gate)
        {
            if (connection.PinnedPublication != publication) return;
            connection.PinnedPublication = 0;
            connection.PinnedGrant = null;
        }
    }

    static bool TryPublicationStamp(AuthorityConnection connection, ulong publication, out ViewGrantStamp stamp)
    {
        if (publication != 0 && connection.PinnedPublication == publication && connection.PinnedGrant is { } pinned)
        { stamp = pinned; return true; }
        return connection.PublishedGrants.TryGetValue(publication, out stamp!);
    }

    public bool BeginSynchronization(AuthorityConnection connection)
    {
        lock (_gate)
        {
            EnsureOwnerBoundary();
            if (_world.HasPendingDurableMutation) return false;
            if (!Current(connection) || connection.Phase != AdmissionPhase.Preparing || !HasRole(connection.Player)) return false;
            Cleanup(connection.Player, _players[connection.Player]);
            connection.Phase = AdmissionPhase.Synchronizing;
            return true;
        }
    }

    /// <summary>
    /// The trusted host calls this after acknowledging a complete authorized baseline.
    /// </summary>
    public bool Activate(AuthorityConnection connection)
    {
        lock (_gate)
        {
            EnsureOwnerBoundary();
            if (_world.HasPendingDurableMutation) return false;
            if (!Current(connection) || connection.Phase != AdmissionPhase.Synchronizing) return false;
            if (!HasRole(connection.Player)) { connection.Phase = AdmissionPhase.Preparing; return false; }
            Cleanup(connection.Player, _players[connection.Player]);
            connection.Phase = AdmissionPhase.Active;
            return true;
        }
    }

    /// <summary>
    /// Freezes payloads. Request numbers increase per player across connections; Tick is assigned by the server.
    /// </summary>
    public Task<AuthorityReceipt> Submit(AuthorityConnection connection, ulong number, SimCommand command, AuthorityContext context)
    {
        Task<AuthorityReceipt> Reject(AuthorityStatus status)
        {
            if (connection is not null && _world.MovementTrace is { } trace)
            {
                var key = trace.Key(connection, number);
                trace.Emit(key with { Stage = LatencyStage.AuthorityReceipt, Value = (long)status });
                trace.Emit(key with { Stage = LatencyStage.Terminal, Outcome = LatencyOutcome.Rejected, Value = (long)status });
            }
            return Rejected(number, status);
        }
        lock (_gate)
        {
            if (_disposed) return Reject(AuthorityStatus.WorldClosed);
            if (_faulted) return Reject(AuthorityStatus.WorldFaulted);
            if (!Current(connection)) return Reject(AuthorityStatus.InvalidConnection);
            if (number == 0 || command is null || context?.Characters is null) return Reject(AuthorityStatus.InvalidRequest);
            if (!PlayerCommandRouting.Supports(command)) return Reject(AuthorityStatus.Unsupported);
            SimCommand frozen;
            AuthorityContext expected;
            byte[] hash;
            int bytes;
            try
            {
                // Stamping data never participates in a client's command. Type comes from the trusted decoder, not JSON.
                command = command with { Tick = 0, Issuer = null, ControlGrants = [], FactionGrantRevision = 0 };
                using var payload = new BoundedPayload();
                JsonSerializer.Serialize(payload, command, command.GetType(), Json);
                bytes = checked((int)payload.Length);
                payload.Position = 0;
                frozen = (SimCommand)JsonSerializer.Deserialize(payload, command.GetType(), Json)!;
                if (frozen is Construction.PlaceBuildings placement && placement.Plans.Any(p => p is null))
                    return Reject(AuthorityStatus.InvalidRequest);
                if (context.Characters.Count > 4096) return Reject(AuthorityStatus.InvalidRequest);
                expected = context with { Characters = context.Characters.OrderBy(c => c.Entity.Value).ToArray() };
                using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                fingerprint.AppendData(System.Text.Encoding.UTF8.GetBytes(command.GetType().FullName!));
                fingerprint.AppendData(payload.GetBuffer().AsSpan(0, bytes));
                fingerprint.AppendData(JsonSerializer.SerializeToUtf8Bytes(expected, Json));
                hash = fingerprint.GetHashAndReset();
                if (PlayerCommandRouting.Characters(frozen) is null) return Reject(AuthorityStatus.InvalidRequest);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or PayloadTooLargeException)
            { return Reject(AuthorityStatus.InvalidRequest); }

            var ledger = _players[connection.Player];
            if (ledger.Receipts.TryGetValue(number, out var previous))
                return hash.AsSpan().SequenceEqual(previous.Fingerprint) ? previous.Completion.Task : Reject(AuthorityStatus.Conflict);
            if (number <= ledger.HighWater) return Reject(AuthorityStatus.Expired);
            if (connection.Phase != AdmissionPhase.Active) return Reject(AuthorityStatus.NotActive);
            if (_pending.Count >= MaxPending || _queuedBytes + bytes > MaxBytes) return Reject(AuthorityStatus.Busy);
            var entry = new Entry(hash);
            var request = new Pending(connection, number, frozen, expected, entry, bytes);
            ledger.HighWater = number;
            ledger.Receipts.Add(number, entry);
            _pending.Enqueue(request);
            _queuedBytes += bytes;
            if (_world.MovementTrace is { } trace)
                trace.Emit(trace.Key(connection, number) with { Stage = LatencyStage.AuthorityQueued });
            return entry.Completion.Task;
        }
    }

    /// <summary>
    /// Serialized world-owner boundary, before physics or publication. Each request commits its character buffer separately.
    /// </summary>
    public int ProcessPending()
    {
        lock (_gate)
        {
            _world.RequireNoDurableMutation();
            EnsureOwnerBoundary();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_faulted) throw new InvalidOperationException("The authority world has faulted.");
            if (_processing) throw new InvalidOperationException("Recursive authority processing.");
            foreach (var (player, generation) in _closed)
                if (_players.TryGetValue(player, out var ledger) && ledger.Generation == generation && ledger.Connection is null)
                {
                    _world.MovementTrace?.Close(player.Value, generation);
                    _world.ResetPlayerInteractions(player);
                }
            _closed.Clear();
            foreach (var (player, ledger) in _players) Cleanup(player, ledger);
            _processing = true;
            int count = _pending.Count;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var request = _pending.Dequeue();
                    _queuedBytes -= request.Bytes;
                    _operation = checked(_operation + 1);
                    try
                    {
                        var status = Validate(request);
                        if (status == AuthorityStatus.Evaluated)
                        {
                            _world.MovementTrace?.Begin(request.Connection, request.Number, request.Command, request.Context);
                            try
                            {
                                _world.ApplyAuthorityCommand(request.Command with
                                {
                                    Tick = _world.Tick,
                                    Issuer = request.Connection.Player,
                                    ControlGrants = request.Context.Characters,
                                    FactionGrantRevision = request.Context.MembershipRevision,
                                });
                            }
                            finally { _world.MovementTrace?.End(); }
                        }
                        Observe();
                        Complete(request, status, _operation);
                    }
                    catch
                    {
                        _faulted = true;
                        Observe();
                        Complete(request, AuthorityStatus.WorldFaulted, _operation);
                        Drain(AuthorityStatus.WorldFaulted);
                        throw;
                    }
                }
                Observe();
                return count;
            }
            finally { _processing = false; }
        }
    }

    AuthorityStatus Validate(Pending request)
    {
        var connection = request.Connection;
        if (!Current(connection)) return AuthorityStatus.InvalidConnection;
        if (connection.Phase != AdmissionPhase.Active) return AuthorityStatus.NotActive;
        if (!HasRole(connection.Player)) { connection.Phase = AdmissionPhase.Preparing; return AuthorityStatus.NoActiveRole; }
        if (request.Command is InspectCommand inspect && !inspect.Target.IsNone && !connection.PublishedCharacters.Contains(inspect.Target)
            || request.Command is InspectBuildingCommand { Instance: { } site } && !connection.PublishedSites.Contains(site))
            return AuthorityStatus.InvalidRequest;
        var expected = request.Context;
        var characters = PlayerCommandRouting.Characters(request.Command).Distinct().ToArray();
        if (characters.Length != expected.Characters.Count) return AuthorityStatus.StaleAuthority;
        foreach (var id in characters)
            if (_world.Players.ControlOf(id) is not { } grant || grant.Player != connection.Player || !expected.Characters.Contains(grant))
                return AuthorityStatus.StaleAuthority;
        int faction = PlayerCommandRouting.Faction(_world, request.Command);
        if (faction != expected.Faction || expected.MembershipRevision !=
            (faction >= 0 ? _world.Players.Find(connection.Player)!.MembershipRevision(faction) : 0)) return AuthorityStatus.StaleAuthority;
        return AuthorityStatus.Evaluated;
    }

    bool HasRole(PlayerId player) => _world.Players.KnownRoles(player).Any(_world.Players.Validate);
    bool Current(AuthorityConnection connection) => !_disposed && !_faulted && connection is not null &&
        _players.TryGetValue(connection.Player, out var ledger) && ReferenceEquals(ledger.Connection, connection);
    void EnsureOwnerBoundary()
    {
        if (_world.Characters.Phase != TablePhase.Outside) throw new InvalidOperationException("Authority requires the serialized world boundary.");
    }
    void Observe() => (_observedTick, _observedRevision) = (_world.Tick, _world.StateRevision);
    Task<AuthorityReceipt> Rejected(ulong number, AuthorityStatus status) => Task.FromResult(new AuthorityReceipt(number, status, 0, _observedRevision, _observedTick));
    void Complete(Pending request, AuthorityStatus status, ulong sequence)
    {
        if (_world.MovementTrace is { } trace)
        {
            var key = trace.Key(request.Connection, request.Number);
            trace.Emit(key with { Stage = LatencyStage.AuthorityReceipt, Value = (long)status, Revision = _observedRevision });
            if (status != AuthorityStatus.Evaluated) trace.Emit(key with { Stage = LatencyStage.Terminal, Outcome = LatencyOutcome.Rejected, Value = (long)status });
        }
        var ledger = _players[request.Connection.Player];
        request.Entry.Completion.TrySetResult(new AuthorityReceipt(request.Number, status, sequence, _observedRevision, _observedTick));
        ledger.Completed.Enqueue(request.Number);
        while (ledger.Completed.Count > MaxReceipts) ledger.Receipts.Remove(ledger.Completed.Dequeue());
    }
    void Drain(AuthorityStatus status)
    {
        while (_pending.TryDequeue(out var request)) Complete(request, status, 0);
        _queuedBytes = 0;
    }
    /// <summary>
    /// Trusted runtime failure notification. Stops requests even when the failure was outside command processing.
    /// </summary>
    public void MarkFaulted()
    {
        lock (_gate)
        {
            if (_disposed || _faulted) return;
            _faulted = true;
            Drain(AuthorityStatus.WorldFaulted);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Drain(AuthorityStatus.WorldClosed);
            _players.Clear();
        }
    }

    sealed class PayloadTooLargeException : Exception;
    sealed class BoundedPayload : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > MaxPayload) throw new PayloadTooLargeException();
            byte[] copy = buffer.ToArray();
            base.Write(copy, 0, copy.Length);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > MaxPayload) throw new PayloadTooLargeException();
            base.Write(buffer, offset, count);
        }
    }
}