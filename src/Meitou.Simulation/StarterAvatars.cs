using Meitou.Data.Save;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation;

/// <summary>
/// Authority-owned provenance recorded when a game start creates its characters. Missing legacy provenance conservatively protects owned characters.
/// </summary>
public sealed class StarterAvatars
{
    /// <summary>
    /// The world whose creation, controls and lasting ids this registry follows.
    /// </summary>
    readonly World _world;

    /// <summary>
    /// Rows in lasting-id order. Known death persists even after removal or unloading.
    /// </summary>
    readonly SortedDictionary<ulong, SaveLastingIds.Starter> _starters = [];

    /// <summary>
    /// Players with known start provenance.
    /// </summary>
    readonly SortedSet<ulong> _players = [];

    /// <summary>
    /// Immutable views reused across calls until the owner gains a starter or a successor.
    /// </summary>
    readonly Dictionary<PlayerId, IReadOnlyList<LastingId>> _rosters = [];

    /// <summary>
    /// Canonical hash words, rebuilt only when membership, ownership or death confirmation changes.
    /// </summary>
    ulong[]? _hashWords = [];

    /// <summary>
    /// Whether a consumer or checkpoint has already requested usable provenance.
    /// </summary>
    bool _requested;

    /// <summary>
    /// Native roles retain their identity even when first observed offline.
    /// </summary>
    readonly Dictionary<EntityId, LastingId> _roles = [];

    /// <summary>
    /// Sorted owner ids for checkpointing and hashing, including empty, already-migrated slots.
    /// </summary>
    internal IEnumerable<ulong> Owners => _players;

    /// <summary>
    /// Whether a previously classified player slot must be saved.
    /// </summary>
    internal bool HasOwners => _players.Count > 0;

    /// <summary>
    /// The lasting id reserved for a native role that has not yet materialized.
    /// </summary>
    internal LastingId Reserved(EntityId entity) => _roles.GetValueOrDefault(entity);

    /// <summary>
    /// Creates an empty world-owned registry.
    /// </summary>
    internal StarterAvatars(World world) => _world = world;

    /// <summary>
    /// Exact birth slots retained until a consumer or checkpoint needs lasting identities. A removed body's final cold state identifies a death.
    /// </summary>
    readonly List<(PlayerId Owner, CharacterId Body, CharacterCold Cold)> _births = [];

    /// <summary>
    /// Collects birth evidence without allocating simulation ids for an otherwise unmodified native game.
    /// </summary>
    internal void NoteBirths(PlayerId owner, IEnumerable<CharacterId> roster)
    {
        foreach (var body in roster) _births.Add((owner, body, _world.Characters.Cold(body.Slot)!));
        if (_requested) Materialize();
    }

    /// <summary>
    /// Materializes the recorded birth roster on first use or before capture. Later hires are never consulted.
    /// </summary>
    internal StarterAvatars Materialize()
    {
        _requested = true;
        if (_births.Count == 0) return this;
        if (_world.Characters.Phase == TablePhase.Parallel)
            throw new InvalidOperationException("Initialize starter identities from a serial phase.");
        foreach (var (owner, body, cold) in _births)
        {
            LastingId id = cold.LastingId;
            if (id.IsNone)
            {
                id = _world.Characters.TryResolveNext(body, out _) ? _world.LastingIds.CreatedStarter(body)
                    : _world.LastingIds.ReserveStarter(cold.Save?.Source);
                cold.LastingId = id;
            }
            Add(owner, id, cold.Entity, cold.DiedTick >= 0 || cold.Medical?.Dead == true);
        }
        _births.Clear();
        return this;
    }

    /// <summary>
    /// The player's recorded starters and explicit successors, including those confirmed dead.
    /// </summary>
    public IReadOnlyList<LastingId> ForPlayer(PlayerId player)
    {
        if (!_players.Contains(player.Value)) return Array.Empty<LastingId>();
        if (_rosters.TryGetValue(player, out var roster)) return roster;
        var ids = _starters.Values.Where(s => s.Player == player.Value).Select(s => new LastingId(s.Id)).ToArray();
        return _rosters[player] = Array.AsReadOnly(ids);
    }

    /// <summary>
    /// Tests a stored member by its lasting identity before a whole-platoon directive.
    /// </summary>
    public bool IsProtected(LastingId id) => !id.IsNone && _starters.ContainsKey(id.Value);

    /// <summary>
    /// Whether autonomous action and speech must be withheld. A load without creation provenance treats the owner's roster as protected.
    /// </summary>
    public bool IsProtected(CharacterId who)
    {
        if (!_world.Characters.TryResolveNext(who, out int slot) || _world.Characters.Cold(slot) is not { } cold) return false;
        if (_starters.ContainsKey(cold.LastingId.Value) || _roles.ContainsKey(cold.Entity)) return true;
        if (!cold.IsPlayer) return false;
        var owner = _world.Players.RequiresExplicitControl ? _world.Players.ControlOf(who)?.Player : PlayerId.Local;
        return owner is not { } player || !_players.Contains(player.Value);
    }

    /// <summary>
    /// Records the actual initial creation output before a save or player-visible publication.
    /// </summary>
    internal void Capture(PlayerId owner, IEnumerable<CharacterId> created)
    {
        foreach (var who in created)
        {
            var id = _world.LastingIds.CreatedStarter(who);
            if (id.IsNone) throw new InvalidOperationException("Starter creation referenced an absent character.");
            Add(owner, id, _world.Characters.Cold(who.Slot)!.Entity, false);
        }
    }

    /// <summary>
    /// Records a previously unclassified starter, retaining its original owner after any subsequent transfer.
    /// </summary>
    void Add(PlayerId owner, LastingId id, EntityId entity, bool dead)
    {
        if (!_starters.TryAdd(id.Value, new(owner.Value, id.Value, dead, entity.Value))) return;
        if (!entity.IsNone) _roles.Add(entity, id);
        _players.Add(owner.Value);
        _rosters.Remove(owner);
        _hashWords = null;
    }

    /// <summary>
    /// A native solo save has no authenticated creation receipt. Conservatively freeze exactly its existing player platoon members once.
    /// Includes saved corpses and members with no materialized character.
    /// </summary>
    internal void MigrateLocal(IEnumerable<LoadedPlatoon> squads, IReadOnlyDictionary<SaveCharacter, CharacterId> loaded)
    {
        var owner = PlayerId.Local;
        if (!_players.Add(owner.Value)) return;
        _hashWords = null;
        foreach (var group in squads)
        {
            foreach (var source in group.Platoon.Characters)
            {
                var id = loaded.TryGetValue(source, out var who) ? _world.LastingIds.GetOrGive(who) : _world.LastingIds.ReserveStarter(source);
                bool dead = group.Squad.Dead || source.Medical?.Bools.GetValueOrDefault("dead") == true
                    || source.GameState?.Bools.GetValueOrDefault(PlayerSquadSave.DeadKey) == true;
                Add(owner, id, EntitySave.Read(source), dead);
            }
        }
    }

    /// <summary>
    /// Authentic initial-start receipts identify the actual starter roles. For older owners without a receipt, freeze just their current grants.
    /// Restores missing offline identities without spawning the roles.
    /// </summary>
    internal void MigrateMultiplayer(PlayerRegistryState state, CreationQualificationState qualifications)
    {
        if (!state.ExplicitControl) return;
        var retired = state.Retired!.ToDictionary(r => r.Entity, r => r.Kind);
        foreach (var r in (qualifications.Receipts ?? []).Where(r => r.IncludesStartResources).OrderBy(r => r.Player.Value))
        {
            if (!_players.Add(r.Player.Value)) continue;
            _hashWords = null;
            foreach (var entity in r.Roles) MigrateRole(r.Player, entity, retired);
        }
        foreach (var owner in state.Players.OrderBy(p => p.Id.Value))
        {
            if (!_players.Add(owner.Id.Value)) continue;
            _hashWords = null;
            foreach (var grant in state.Controls.Where(g => g.Player == owner.Id).OrderBy(g => g.Entity.Value))
                MigrateRole(owner.Id, grant.Entity, retired);
        }
    }

    /// <summary>
    /// Records one role's actual live/saved identity and only confirmed death, even when its body was removed.
    /// </summary>
    void MigrateRole(PlayerId owner, EntityId entity, IReadOnlyDictionary<EntityId, RetiredRoleKind> retired)
    {
        if (_roles.ContainsKey(entity)) return;
        LastingId id;
        bool dead = retired.GetValueOrDefault(entity) == RetiredRoleKind.Dead;
        if (_world.Entities.TryResolve(entity, out var who))
        {
            id = _world.LastingIds.GetOrGive(who);
            var cold = _world.Characters.Cold(who.Slot)!;
            dead |= cold.DiedTick >= 0 || cold.Medical?.Dead == true;
        }
        else
        {
            id = _world.LastingIds.ReserveStarter(_world.StoredPlayerRoles?.Source(entity));
            dead |= _world.StoredPlayerRoles?.Locate(entity)?.State == PlayerRoleAvailability.Dead;
        }
        Add(owner, id, entity, dead);
    }

    /// <summary>
    /// Promotes a living, controlled recruit after all known starters have confirmed deaths. An unloaded or missing starter counts as unconfirmed.
    /// The caller must dispatch the explicit request using the authenticated issuer.
    /// </summary>
    public bool TryInherit(PlayerId owner, CharacterId who)
    {
        _world.Physical.RequireSerial();
        if (!CanInherit(owner, who)) return false;
        RecordDeaths();
        Capture(owner, [who]);
        return true;
    }

    /// <summary>
    /// Whether a living, controlled recruit can currently succeed the player's confirmed dead starters. Read on the simulation owner.
    /// </summary>
    public bool CanInherit(PlayerId owner, CharacterId who)
    {
        if (!_players.Contains(owner.Value) || !_world.Characters.TryResolveNext(who, out int slot) || _world.Characters.Cold(slot) is not { } cold
            || !cold.IsPlayer || cold.DiedTick >= 0 || cold.Medical?.Dead == true || IsProtected(who)) return false;
        if (_world.Players.RequiresExplicitControl ? !_world.Players.CanControl(owner, who) : owner != PlayerId.Local) return false;
        var own = _starters.Values.Where(s => s.Player == owner.Value);
        return own.Any() && own.All(ConfirmedDead);
    }

    /// <summary>
    /// A recorded death or the dead body itself confirms succession eligibility.
    /// </summary>
    bool ConfirmedDead(SaveLastingIds.Starter entry)
    {
        if (entry.Dead) return true;
        var who = _world.LastingIds.Find(new(entry.Id));
        return !who.IsNone && _world.Characters.Cold(who.Slot) is { } cold && (cold.DiedTick >= 0 || cold.Medical?.Dead == true);
    }

    /// <summary>
    /// Records confirmed deaths of loaded starters. Missing or unloaded roles keep their previous status.
    /// </summary>
    internal void RecordDeaths()
    {
        if (_starters.Count == 0) return;
        List<SaveLastingIds.Starter>? dead = null;
        foreach (var entry in _starters.Values)
        {
            if (!entry.Dead && ConfirmedDead(entry)) (dead ??= []).Add(entry);
        }
        if (dead is null) return;
        foreach (var entry in dead) _starters[entry.Id] = entry with { Dead = true };
        _hashWords = null;
    }

    /// <summary>
    /// Records a confirmed death even when the corpse is removed before the next tick.
    /// </summary>
    internal void Removed(CharacterCold cold, bool unloaded)
    {
        if (cold.DiedTick < 0 && cold.Medical?.Dead != true || !_starters.TryGetValue(cold.LastingId.Value, out var entry)) return;
        _starters[entry.Id] = entry with { Dead = true };
        _hashWords = null;
    }

    /// <summary>
    /// The sorted provenance saved with the lasting ids.
    /// </summary>
    internal IEnumerable<SaveLastingIds.Starter> Entries => _starters.Values;

    /// <summary>
    /// Restores creation provenance before world-ready callbacks.
    /// </summary>
    internal void Restore(IReadOnlyList<SaveLastingIds.Starter> entries, IReadOnlyList<ulong>? owners = null)
    {
        foreach (var owner in owners ?? []) _players.Add(owner);
        _hashWords = null;
        foreach (var entry in entries) Add(new(entry.Player), new(entry.Id), new(entry.Entity), entry.Dead);
    }

    /// <summary>
    /// Includes ownership and confirmed death in the deterministic world hash.
    /// </summary>
    internal void Hash(ref StateHasher hash)
    {
        _hashWords ??= HashWords();
        foreach (ulong word in _hashWords) hash.Add(word);
    }

    /// <summary>
    /// Owner classifications followed by the sorted identity, native role and confirmed death of every starter.
    /// </summary>
    ulong[] HashWords()
    {
        var words = new ulong[_players.Count + 4 * _starters.Count];
        int index = 0;
        foreach (ulong owner in _players) words[index++] = owner;
        foreach (var row in _starters.Values)
        {
            words[index++] = row.Player;
            words[index++] = row.Id;
            words[index++] = row.Entity;
            words[index++] = row.Dead ? 1UL : 0;
        }
        return words;
    }
}
