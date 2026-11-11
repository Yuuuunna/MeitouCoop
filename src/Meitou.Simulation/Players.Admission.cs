namespace Meitou.Simulation;

public enum PlayerEntryRoute { LegacyWorld, NewPlayer, AwaitingAssignment, ResolveStoredRoles, EnterWorld }
public sealed record PlayerEntryAssessment(PlayerRoleHistory History, PlayerEntryRoute Route, int AvailableRoles, int DeadRoles, int UnresolvedRoles);

public sealed partial class WorldPlayers
{
    /// <summary>
    /// Trusted server-side routing after authentication. NewPlayer is history, not a creation entitlement.
    /// Unloaded records require lifecycle/region resolution and never imply a free start.
    /// </summary>
    public PlayerEntryAssessment AssessEntry(PlayerId id)
    {
        if (!_world.AtTickBoundary) throw new InvalidOperationException("Admission assessment requires the serialized world boundary.");
        var player = Required(id);
        int available = 0, dead = 0, unresolved = 0;
        foreach (var grant in KnownRoles(id))
        {
            switch (LocateRole(id, grant.Entity)?.Availability)
            {
                case PlayerRoleAvailability.Available: available++; break;
                case PlayerRoleAvailability.Dead: dead++; break;
                case PlayerRoleAvailability.Stored or PlayerRoleAvailability.Unknown: unresolved++; break;
            }
        }
        var route = !_explicitControl ? PlayerEntryRoute.LegacyWorld
            : available > 0 ? PlayerEntryRoute.EnterWorld
            : unresolved > 0 ? PlayerEntryRoute.ResolveStoredRoles
            : player.RoleHistory == PlayerRoleHistory.NeverAssigned && !player.Memberships.Any() ? PlayerEntryRoute.NewPlayer
            : PlayerEntryRoute.AwaitingAssignment;
        return new PlayerEntryAssessment(player.RoleHistory, route, available, dead, unresolved);
    }
}