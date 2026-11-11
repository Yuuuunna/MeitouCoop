using Meitou.Simulation.Player;
namespace Meitou.Simulation;

public sealed record SavedMembership(int Faction, FactionPermission Permissions, ulong Revision);
public sealed record SavedPlayer(PlayerId Id, int ViewedFaction, SavedMembership[] Memberships,
    PlayerRoleHistory RoleHistory = (PlayerRoleHistory)(-1), AccountIdentity? Account = null);
public sealed record PlayerRegistryState(int Version, bool ExplicitControl, ulong Revision,
    SavedPlayer[] Players, CharacterControl[] Controls, int[] PlayerOwnedFactions, RetiredRole[]? Retired = null);

public sealed partial class WorldPlayers
{
    bool _restored;

    /// <summary>
    /// Detached authority metadata. Includes unloaded and dead role grants; no connection or interaction sessions.
    /// </summary>
    public PlayerRegistryState Capture()
    {
        if (_world.Characters.Phase != TablePhase.Outside) throw new InvalidOperationException("Capture requires the serialized world boundary.");
        return new PlayerRegistryState(4, _explicitControl, _revision,
            _players.Values.Select(p => new SavedPlayer(p.Id,
                !_explicitControl || p.IsMember(p.State.Faction) ? p.State.Faction : -1,
                p.Memberships.Select(m => new SavedMembership(m.Key, m.Value, p.MembershipRevision(m.Key))).ToArray(), p.RoleHistory, p.Account)).ToArray(),
            _controls.Values.OrderBy(c => c.Entity.Value).ToArray(),
            _world.Factions.All.Where(f => f.PlayerOwned).Select(f => f.Index).ToArray(), CaptureRetiredRoles());
    }

    /// <summary>
    /// Restore into a never-admitted world after its entity reservations/characters and faction catalogue are loaded.
    /// Validate the whole graph before changing authority; loaded roles must agree with their recorded faction.
    /// </summary>
    public void Restore(PlayerRegistryState state, int factionCount)
    {
        if (state is { Version: 2, Players: not null } && state.Players.All(p => p is not null && p.Account is null)) state = state with { Version = 3 };
        if (state is { Version: 3, Retired: null or { Length: 0 } }) state = state with { Version = 4, Retired = [] };
        if (_world.Tick != 0 || _world.Characters.Phase != TablePhase.Outside || _restored || _explicitControl || _revision != 0 ||
            _players.Count != 1 || _controls.Count != 0 || _world.Authority.HasHistory || _world.Commands.Count != 0)
            throw new InvalidOperationException("Authority is restored once, before admission or gameplay.");
        ValidateState(state, factionCount);
        if (_world.Factions.All.Any(f => f.PlayerOwned && !state.PlayerOwnedFactions.Contains(f.Index)))
            throw new InvalidDataException("The target already contains a different player-faction classification.");
        // Prepare every player before committing. Keep the local compatibility state object referenced by World.Player.
        var next = state.Players!.Select(p => (Saved: p, Player: new WorldPlayer(p.Id,
            p.Id == PlayerId.Local ? _world.Player : new PlayerState(_world.Factions)))).ToArray();
        foreach (int faction in state.PlayerOwnedFactions) _world.Factions.GetOrCreate(faction).PlayerOwned = true;
        _players.Clear();
        foreach (var (saved, player) in next)
        {
            foreach (var membership in saved.Memberships)
                player.SetMembership(membership.Faction, membership.Permissions, membership.Revision);
            player.State.Faction = saved.ViewedFaction;
            player.RoleHistory = saved.RoleHistory;
            player.Account = saved.Account;
            if (saved.Account is { } account) _accounts.Add(account, player.Id);
            _players.Add(player.Id.Value, player);
        }
        foreach (var grant in state.Controls) _controls.Add(grant.Entity, grant);
        foreach (var retired in state.Retired!) _retiredRoles.Add(retired.Entity, retired.Kind);
        _revision = state.Revision;
        _explicitControl = state.ExplicitControl;
        _restored = true;
        Prune();
    }

    internal void ValidateState(PlayerRegistryState state, int factionCount)
    {
        if (state is null || state.Version != 4 || state.Players is null || state.Controls is null || state.PlayerOwnedFactions is null || state.Retired is null || factionCount < 0)
            throw new InvalidDataException("Unsupported player authority state.");
        bool Faction(int faction) => faction >= 0 && faction < factionCount;
        var owned = new HashSet<int>();
        foreach (int faction in state.PlayerOwnedFactions)
            if (!Faction(faction) || !owned.Add(faction)) throw new InvalidDataException("Invalid player faction classification.");
        var identities = new Dictionary<PlayerId, SavedPlayer>();
        var boundAccounts = new HashSet<AccountIdentity>();
        var revisions = new HashSet<ulong>();
        bool Revision(ulong value) => value != 0 && value <= state.Revision && revisions.Add(value);
        foreach (var player in state.Players)
        {
            if (player is null || player.Id.IsNone || !identities.TryAdd(player.Id, player) || player.Memberships is null || !Enum.IsDefined(player.RoleHistory))
                throw new InvalidDataException("Invalid or duplicate player identity.");
            if (player.Account is { } account && (!AccountIdentity.IsValid(account) || !boundAccounts.Add(account)))
                throw new InvalidDataException("Invalid or duplicate account binding.");
            var memberships = new HashSet<int>();
            foreach (var grant in player.Memberships)
                if (grant is null || !Faction(grant.Faction) || !owned.Contains(grant.Faction) || !memberships.Add(grant.Faction) ||
                    (grant.Permissions & ~FactionPermission.All) != 0 || !Revision(grant.Revision))
                    throw new InvalidDataException("Invalid membership permission or revision.");
            if (player.ViewedFaction != -1 && (!Faction(player.ViewedFaction) || state.ExplicitControl && !memberships.Contains(player.ViewedFaction)))
                throw new InvalidDataException("Viewed faction is not authorized.");
        }
        if (!identities.ContainsKey(PlayerId.Local)) throw new InvalidDataException("The local compatibility identity is missing.");
        var entities = new HashSet<EntityId>();
        foreach (var grant in state.Controls)
        {
            if (!_world.Entities.Contains(grant.Entity) || !entities.Add(grant.Entity) || !Revision(grant.Revision) ||
                !identities.TryGetValue(grant.Player, out var owner) || owner.RoleHistory != PlayerRoleHistory.Assigned || !owner.Memberships.Any(m => m.Faction == grant.Faction))
                throw new InvalidDataException("Invalid character authority reference.");
            if (_world.Entities.TryResolve(grant.Entity, out var role) && _world.Characters.Cold(role.Slot)!.Faction != grant.Faction)
                throw new InvalidDataException("The loaded character has a different faction from its saved authority.");
        }
        var retiredIds = new HashSet<EntityId>();
        foreach (var retired in state.Retired)
            if (retired is null || !Enum.IsDefined(retired.Kind) || !entities.Contains(retired.Entity) || !retiredIds.Add(retired.Entity)
                || _world.StoredPlayerRoles?.Locate(retired.Entity) is { State: PlayerRoleAvailability.Stored }
                || _world.Entities.TryResolve(retired.Entity, out var live) && _world.Characters.Cold(live.Slot) is { DiedTick: < 0 } cold && cold.Medical?.Dead != true)
                throw new InvalidDataException("Invalid retired-role state.");
        if (!state.ExplicitControl && (identities.Count != 1 || state.Controls.Length != 0 || state.Retired.Length != 0 || state.Revision != 0 ||
            state.Players.Any(p => p.Account is not null || p.Memberships.Length != 0 || p.RoleHistory == PlayerRoleHistory.Assigned) || state.PlayerOwnedFactions.Length != 0))
            throw new InvalidDataException("Authority metadata cannot disable explicit-control mode.");
    }
}