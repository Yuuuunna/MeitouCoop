using System.Numerics;

namespace Meitou.Simulation;

public enum RetiredRoleKind { Dead = 1, Removed = 2 }
public sealed record RetiredRole(EntityId Entity, RetiredRoleKind Kind);
public enum PlayerRoleAvailability { Available, Stored, Dead, Unavailable, Unknown }
public sealed record PlayerRoleLocation(CharacterControl Grant, PlayerRoleAvailability Availability, Vector3? Position);

public sealed partial class WorldPlayers
{
    readonly Dictionary<EntityId, RetiredRoleKind> _retiredRoles = [];
    internal void RoleRemoved(CharacterCold cold, bool unloaded)
    {
        if (cold.Entity.IsNone || !_controls.ContainsKey(cold.Entity)) return;
        bool dead = cold.DiedTick >= 0 || cold.Medical?.Dead == true;
        if (dead || !unloaded) _retiredRoles[cold.Entity] = dead ? RetiredRoleKind.Dead : RetiredRoleKind.Removed;
    }
    public PlayerRoleLocation? LocateRole(PlayerId player, EntityId entity)
    {
        if (!_world.AtTickBoundary) throw new InvalidOperationException("Role lookup requires the world boundary.");
        if (!_controls.TryGetValue(entity, out var grant) || grant.Player != player || Find(player)?.IsMember(grant.Faction) != true) return null;
        if (_retiredRoles.TryGetValue(entity, out var retired)) return new(grant, retired == RetiredRoleKind.Dead ? PlayerRoleAvailability.Dead : PlayerRoleAvailability.Unavailable, null);
        if (_world.Entities.TryResolve(entity, out var role) && _world.Characters.Cold(role.Slot) is { } cold)
        {
            if (cold.DiedTick >= 0 || cold.Medical?.Dead == true) return new(grant, PlayerRoleAvailability.Dead, _world.Characters.Current[role.Slot].Position);
            if (cold.Faction != grant.Faction) return new(grant, PlayerRoleAvailability.Unavailable, null);
            int slot = _world.Characters.TryResolveNext(cold.CarriedBy, out int carrier) ? carrier : role.Slot;
            bool unresolvedCarry = cold.Save is { CarryPartner.IsNull: false };
            var at = _world.Characters.Current[slot].Position;
            return new(grant, unresolvedCarry ? PlayerRoleAvailability.Stored : PlayerRoleAvailability.Available,
                unresolvedCarry ? _world.StoredPlayerRoles?.CarryPosition(cold, at) ?? at : at);
        }
        if (_world.StoredPlayerRoles?.Locate(entity) is { } stored)
            return new(grant, stored.Faction == grant.Faction ? stored.State : PlayerRoleAvailability.Unavailable, stored.Position);
        return new(grant, PlayerRoleAvailability.Unknown, null);
    }
    RetiredRole[] CaptureRetiredRoles() => _controls.Values.Select(g => LocateRole(g.Player, g.Entity)).OfType<PlayerRoleLocation>()
        .Where(r => r.Availability is PlayerRoleAvailability.Dead or PlayerRoleAvailability.Unavailable)
        .Select(r => new RetiredRole(r.Grant.Entity, r.Availability == PlayerRoleAvailability.Dead ? RetiredRoleKind.Dead : RetiredRoleKind.Removed))
        .OrderBy(r => r.Entity.Value).ToArray();
}