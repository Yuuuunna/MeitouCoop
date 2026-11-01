namespace Meitou.Simulation;

/// <summary>A world-local persistent player key. Zero is never a player; connection identity is separate.</summary>
public readonly record struct PlayerId(ulong Value)
{
    public static PlayerId Local => new(1);
    public bool IsNone => Value == 0;
}

/// <summary>Unknown is retained for older checkpoints that did not record revoked assignments.</summary>
public enum PlayerRoleHistory { Unknown, NeverAssigned, Assigned }

[Flags]
public enum FactionPermission
{
    None = 0,
    ReadFunds = 1,
    SpendFunds = 2,
    ManageAssets = 4,
    InviteMembers = 8,
    AssignCharacters = 16,
    ManageFaction = 32,
    ManageSquads = 64,
    CreateCharacters = 128,
    All = ReadFunds | SpendFunds | ManageAssets | InviteMembers | AssignCharacters | ManageFaction | ManageSquads | CreateCharacters,
}

/// <summary>Membership gives no implicit control or spending rights.</summary>
public sealed class WorldPlayer
{
    readonly SortedDictionary<int, FactionPermission> memberships = [];
    readonly Dictionary<int, ulong> membershipRevisions = [];

    internal WorldPlayer(PlayerId id, PlayerState state) => (Id, State) = (id, state);

    public PlayerId Id { get; }
    public AccountIdentity? Account { get; internal set; }
    public PlayerRoleHistory RoleHistory { get; internal set; } = PlayerRoleHistory.NeverAssigned;
    public PlayerState State { get; }
    public IEnumerable<KeyValuePair<int, FactionPermission>> Memberships => memberships;
    public bool IsMember(int faction) => memberships.ContainsKey(faction);
    public bool HasPermission(int faction, FactionPermission permission) =>
        permission != FactionPermission.None && memberships.TryGetValue(faction, out var granted) && (granted & permission) == permission;

    public ulong MembershipRevision(int faction) => membershipRevisions.GetValueOrDefault(faction);
    internal bool MatchesMembership(int faction, FactionPermission permission) => memberships.TryGetValue(faction, out var current) && current == permission;
    internal void SetMembership(int faction, FactionPermission permission, ulong revision)
    {
        memberships[faction] = permission;
        membershipRevisions[faction] = revision;
    }
    internal void RemoveMembership(int faction)
    {
        memberships.Remove(faction);
        membershipRevisions.Remove(faction);
    }
}

/// <summary>A command's control grant. Reassignment increments Revision, including when returning to the same owner.</summary>
public readonly record struct CharacterControl(PlayerId Player, EntityId Entity, int Faction, ulong Revision);

/// <summary>
/// Server-owned players and grants for one world. Registration, membership and initial grants are trusted setup operations;
/// requests must pass the authority gateway. Mutate only outside a tick or in its serial input/slow-world phases.
/// </summary>
public sealed partial class WorldPlayers
{
    readonly World world;
    readonly SortedDictionary<ulong, WorldPlayer> players = [];
    readonly Dictionary<EntityId, CharacterControl> controls = [];
    ulong revision;
    bool explicitControl;

    internal WorldPlayers(World world)
    {
        this.world = world;
        players.Add(PlayerId.Local.Value, new WorldPlayer(PlayerId.Local, world.Player));
    }

    public IEnumerable<WorldPlayer> All => players.Values;
    public bool RequiresExplicitControl => explicitControl;
    public WorldPlayer? Find(PlayerId id) => players.GetValueOrDefault(id.Value);

    public WorldPlayer Register(PlayerId id)
    {
        EnsureMutable();
        if (id.IsNone) throw new ArgumentException("A player identity cannot be empty.", nameof(id));
        if (players.ContainsKey(id.Value)) throw new InvalidOperationException("The player is already registered.");
        var player = new WorldPlayer(id, new PlayerState(world.Factions));
        players.Add(id.Value, player);
        EnableExplicitControl();
        return player;
    }

    public void SetMembership(PlayerId id, int faction, FactionPermission permission)
    {
        EnsureMutable();
        ArgumentOutOfRangeException.ThrowIfNegative(faction);
        if ((permission & ~FactionPermission.All) != 0) throw new ArgumentOutOfRangeException(nameof(permission));
        var player = Required(id);
        world.Factions.GetOrCreate(faction).PlayerOwned = true;
        EnableExplicitControl();
        if (!player.MatchesMembership(faction, permission)) player.SetMembership(faction, permission, NextRevision());
        if (!player.State.Exists) player.State.Faction = faction;
    }

    public void RemoveMembership(PlayerId id, int faction)
    {
        EnsureMutable();
        var player = Required(id);
        foreach (var (entity, control) in controls.ToArray())
            if (control.Player == id && control.Faction == faction) { controls.Remove(entity); retiredRoles.Remove(entity); }
        player.RemoveMembership(faction);
        if (player.State.Faction == faction) player.State.Faction = -1;
        PruneView(player);
    }

    public bool SelectFaction(PlayerId id, int faction)
    {
        EnsureMutable();
        if (Find(id) is not { } player || !player.IsMember(faction)) return false;
        player.State.Faction = faction;
        return true;
    }

    public bool CanControl(PlayerId id, CharacterId character) => ControlOf(character) is { } grant && grant.Player == id;

    public CharacterControl? ControlOf(CharacterId character) => world.Characters.TryResolveNext(character, out int slot)
        && controls.TryGetValue(world.Characters.Cold(slot)!.Entity, out var grant) && Validate(grant) ? grant : null;

    /// <summary>Ownership records including unloaded and dead roles; absence from the active table is not absence of a role.</summary>
    public IEnumerable<CharacterControl> KnownRoles(PlayerId player) => controls.Values.Where(c => c.Player == player).OrderBy(c => c.Entity.Value);
    internal CharacterControl? KnownControl(EntityId entity) => controls.TryGetValue(entity, out var grant) ? grant : null;

    public bool Validate(CharacterControl grant) =>
        !retiredRoles.ContainsKey(grant.Entity) && controls.TryGetValue(grant.Entity, out var current) && current == grant &&
        world.Entities.TryResolve(grant.Entity, out var character) && LivingCharacter(character, out int faction) &&
        world.Characters.Cold(character.Slot)?.Save is not { CarryPartner.IsNull: false } &&
        faction == grant.Faction && Find(grant.Player)?.IsMember(faction) == true;

    /// <summary>Initial spawn/load grant. Replacing an existing controller requires TryTransfer.</summary>
    public CharacterControl AssignInitial(PlayerId id, CharacterId character)
    {
        EnsureMutable();
        if (!LivingCharacter(character, out int faction) || !Required(id).IsMember(faction))
            throw new InvalidOperationException("The player must belong to the living character's faction.");
        var entity = world.Entities.Track(character);
        if (controls.ContainsKey(entity)) throw new InvalidOperationException("The character already has a controller.");
        var grant = new CharacterControl(id, entity, faction, NextRevision());
        controls.Add(entity, grant);
        Required(id).RoleHistory = PlayerRoleHistory.Assigned;
        return grant;
    }

    public bool TryTransfer(PlayerId actor, CharacterControl expected, PlayerId recipient, out CharacterControl granted)
    {
        EnsureMutable();
        granted = default;
        if (!Validate(expected) || !world.Entities.TryResolve(expected.Entity, out var character) || !LivingCharacter(character, out int faction) ||
            Find(actor)?.HasPermission(faction, FactionPermission.AssignCharacters) != true ||
            Find(recipient)?.IsMember(faction) != true) return false;
        granted = new CharacterControl(recipient, expected.Entity, faction, NextRevision());
        controls[expected.Entity] = granted;
        Required(recipient).RoleHistory = PlayerRoleHistory.Assigned;
        PruneView(Required(expected.Player));
        return true;
    }

    /// <summary>Trusted lifecycle transition (for example dismissal), not a remote administrative request.</summary>
    internal void RevokeControl(CharacterId character)
    {
        EnsureMutable();
        if (!world.Characters.TryResolveNext(character, out int slot)) return;
        var entity = world.Characters.Cold(slot)!.Entity;
        if (!controls.Remove(entity, out var grant)) return;
        retiredRoles.Remove(entity);
        NextRevision();
        PruneView(Required(grant.Player));
    }

    bool LivingCharacter(CharacterId character, out int faction)
    {
        faction = -1;
        if (!world.Characters.TryResolveNext(character, out int slot) ||
            world.Characters.Cold(slot) is not { DiedTick: < 0 } cold || cold.Medical?.Dead == true) return false;
        faction = cold.Faction;
        return faction >= 0;
    }

    void PruneView(WorldPlayer player)
    {
        player.State.Selection.RemoveAll(id => !CanControl(player.Id, id));
        player.State.InventoryShown.RemoveAll(id => !CanControl(player.Id, id));
        if (world.Squads.Find(player.State.Squad) is { } squad && !squad.Members.Any(id => CanControl(player.Id, id)))
            player.State.Squad = -1;
    }

    internal void Prune()
    {
        foreach (var player in players.Values)
        {
            if (player.Id == PlayerId.Local && !RequiresExplicitControl) player.State.Prune(world.Characters);
            else player.State.Prune(world.Characters, id => CanControl(player.Id, id));
        }
    }

    WorldPlayer Required(PlayerId id) => Find(id) ?? throw new KeyNotFoundException("Unknown player identity.");
    void EnableExplicitControl()
    {
        if (explicitControl) return;
        foreach (var squad in world.Squads.All)
            if (squad.PlayerManaged && squad.Faction >= 0) world.Factions.GetOrCreate(squad.Faction).PlayerOwned = true;
        explicitControl = true;
    }
    ulong NextRevision() => revision = checked(revision + 1);

    void EnsureMutable()
    {
        if (world.Characters.Phase is not (TablePhase.Outside or TablePhase.Inputs or TablePhase.SlowWorld))
            throw new InvalidOperationException("Player grants change only at setup, input or slow-world boundaries.");
    }

    internal void Hash(ref StateHasher h)
    {
        // Leave the single-player golden scenarios unchanged until the multiplayer registry is used.
        if (players.Count == 1 && !players[PlayerId.Local.Value].Memberships.Any() && controls.Count == 0 && revision == 0) return;
        h.Add(0x506C7273);
        h.Add(explicitControl);
        h.Add(revision);
        h.Add(players.Count);
        foreach (var player in players.Values)
        {
            h.Add(player.Id.Value);
            h.Add((int)player.RoleHistory);
            if (player.Account is { } account) { h.Add(0x41636374); h.Add(Rng.StableHash(account.Provider)); h.Add(Rng.StableHash(account.Subject)); }
            if (player.Id != PlayerId.Local) player.State.Hash(ref h);
            var memberships = player.Memberships.ToArray();
            h.Add(memberships.Length);
            foreach (var (faction, permission) in memberships) { h.Add(faction); h.Add((int)permission); h.Add(player.MembershipRevision(faction)); }
        }
        h.Add(controls.Count);
        foreach (var retired in retiredRoles.OrderBy(p => p.Key.Value)) { h.Add(0x52657472); h.Add(retired.Key.Value); h.Add((int)retired.Value); }
        foreach (var (id, grant) in controls.OrderBy(p => p.Key.Value))
        {
            h.Add(id.Value);
            h.Add(grant.Player.Value);
            h.Add(grant.Faction);
            h.Add(grant.Revision);
        }
    }
}
