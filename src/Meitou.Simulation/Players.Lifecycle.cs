using System.Numerics;

namespace Meitou.Simulation;

public enum RetiredRoleKind { Dead = 1, Removed = 2 }
public sealed record RetiredRole(EntityId Entity, RetiredRoleKind Kind);
public enum PlayerRoleAvailability { Available, Stored, Dead, Unavailable, Unknown }
public sealed record PlayerRoleLocation(CharacterControl Grant, PlayerRoleAvailability Availability, Vector3? Position);

public sealed partial class WorldPlayers
{
    readonly Dictionary<EntityId, RetiredRoleKind> retiredRoles = [];
    internal void RoleRemoved(CharacterCold cold, bool unloaded)
    {
        if (cold.Entity.IsNone || !controls.ContainsKey(cold.Entity)) return;
        bool dead = cold.DiedTick >= 0 || cold.Medical?.Dead == true;
        if (dead || !unloaded) retiredRoles[cold.Entity] = dead ? RetiredRoleKind.Dead : RetiredRoleKind.Removed;
    }
    public PlayerRoleLocation? LocateRole(PlayerId player, EntityId entity)
    {
        if (!world.AtTickBoundary) throw new InvalidOperationException("Role lookup requires the world boundary.");
        if (!controls.TryGetValue(entity, out var grant) || grant.Player != player || Find(player)?.IsMember(grant.Faction) != true) return null;
        if (retiredRoles.TryGetValue(entity, out var retired)) return new(grant, retired == RetiredRoleKind.Dead ? PlayerRoleAvailability.Dead : PlayerRoleAvailability.Unavailable, null);
        if (world.Entities.TryResolve(entity, out var role) && world.Characters.Cold(role.Slot) is { } cold)
        {
            if (cold.DiedTick >= 0 || cold.Medical?.Dead == true) return new(grant, PlayerRoleAvailability.Dead, world.Characters.Current[role.Slot].Position);
            if (cold.Faction != grant.Faction) return new(grant, PlayerRoleAvailability.Unavailable, null);
            int slot = world.Characters.TryResolveNext(cold.CarriedBy, out int carrier) ? carrier : role.Slot;
            bool unresolvedCarry = cold.Save is { CarryPartner.IsNull: false };
            var at = world.Characters.Current[slot].Position;
            return new(grant, unresolvedCarry ? PlayerRoleAvailability.Stored : PlayerRoleAvailability.Available,
                unresolvedCarry ? world.StoredPlayerRoles?.CarryPosition(cold, at) ?? at : at);
        }
        if (world.StoredPlayerRoles?.Locate(entity) is { } stored)
            return new(grant, stored.Faction == grant.Faction ? stored.State : PlayerRoleAvailability.Unavailable, stored.Position);
        return new(grant, PlayerRoleAvailability.Unknown, null);
    }
    RetiredRole[] CaptureRetiredRoles() => controls.Values.Select(g => LocateRole(g.Player, g.Entity)).OfType<PlayerRoleLocation>()
        .Where(r => r.Availability is PlayerRoleAvailability.Dead or PlayerRoleAvailability.Unavailable)
        .Select(r => new RetiredRole(r.Grant.Entity, r.Availability == PlayerRoleAvailability.Dead ? RetiredRoleKind.Dead : RetiredRoleKind.Removed))
        .OrderBy(r => r.Entity.Value).ToArray();
}
