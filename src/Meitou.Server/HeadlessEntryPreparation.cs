using Meitou.Simulation.Saving;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Simulation;

namespace Meitou.Server;

/// <summary>
/// Arrival navigation for a loaded owned role. IO prepares geometry; publication and grant checks stay on the shared world owner.
/// </summary>
public sealed class HeadlessEntryPreparation(HeadlessWorld host, ServerWorldLoop loop)
{
    sealed record Preparation(World World, CharacterControl Grant, ZoneCoordinate[] Zones, IDisposable Lease);
    public async Task<bool> PrepareAsync(AuthorityConnection connection, EntityId entity, CancellationToken cancellation)
    {
        var prepared = await loop.InvokeAsync(() =>
        {
            if (!ReferenceEquals(host.Runtime, loop.Runtime) || host.Content.Navigation is null || host.World.HasPendingDurableMutation || host.World.Authority.GetPhase(connection) != AdmissionPhase.Preparing || host.World.Authority.LocateRole(connection, entity)is not { Availability: PlayerRoleAvailability.Available or PlayerRoleAvailability.Stored, Position: { } at } location)
                return null;
            var centre = WorldLayout.ZoneOf(at.X, at.Z);
            if (!centre.IsInsideGrid)
                return null;
            var zones = (
                from z in Enumerable.Range(-1, 3)from x in Enumerable.Range(-1, 3)let zone = new ZoneCoordinate(centre.X + x, centre.Y + z)
                where zone.IsInsideGrid
                select zone).ToArray();
            var lease = host.World.Regions.Retain(zones, simulation: true);
            try
            {
                host.Runtime.Synchronize();
                return new Preparation(host.World, location.Grant, zones, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }, cancellation).ConfigureAwait(false);
        if (prepared is null)
            return false;
        using (prepared.Lease)
        {
            while (true)
            {
                await Task.WhenAll(prepared.Zones.Select(host.Content.Navigation!.PrepareZone)).WaitAsync(cancellation).ConfigureAwait(false);
                int status = await loop.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(prepared.World, host.World) || host.World.HasPendingDurableMutation || host.World.Authority.LocateRole(connection, entity)is not { Availability: PlayerRoleAvailability.Available or PlayerRoleAvailability.Stored, Position: { } at } location || location.Grant != prepared.Grant || !prepared.Zones.Contains(WorldLayout.ZoneOf(at.X, at.Z)))
                        return -1;
                    host.Runtime.Synchronize();
                    bool population = host.World.PreparePopulation(at);
                    if (!host.World.Entities.TryResolve(entity, out _) && host.World.StoredPlayerRoles is { } stored)
                    {
                        stored.Restore(entity, p => host.Content.Navigation!.IsReady(WorldLayout.ZoneOf(p.X, p.Z)));
                        if (stored.Failed(entity))
                            return -1;
                    }

                    return population && TryRole(host.World, connection, entity, out var grant, out _) && grant == prepared.Grant && prepared.Zones.All(host.Content.Navigation!.IsReady) ? 1 : 0;
                }, cancellation).ConfigureAwait(false);
                if (status != 0)
                    return status == 1;
                await Task.Delay(10, cancellation).ConfigureAwait(false);
            }
        }
    }

    static bool TryRole(World world, AuthorityConnection connection, EntityId entity, out CharacterControl grant, out Vector3 position)
    {
        grant = default;
        position = default;
        if (world.HasPendingDurableMutation || world.Authority.GetPhase(connection) != AdmissionPhase.Preparing || !world.Entities.TryResolve(entity, out var role) || world.Players.ControlOf(role)is not { } control || control.Player != connection.Player)
            return false;
        int slot = world.Characters.TryResolveNext(world.Characters.Cold(role.Slot)!.CarriedBy, out int carrier) ? carrier : role.Slot;
        grant = control;
        position = world.Characters.Current[slot].Position;
        return true;
    }
}