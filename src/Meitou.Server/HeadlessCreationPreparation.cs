using System.Numerics;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Simulation;

namespace Meitou.Server;

/// <summary>
/// Trusted approved-start preparation. Call only with PlayerCreationService.Arrival output; clients never supply coordinates here.
/// </summary>
public sealed class HeadlessCreationPreparation(HeadlessWorld host, ServerWorldLoop loop)
{
    public async Task<bool> PrepareAsync(AuthorityConnection connection, Vector2 centre, CancellationToken cancellation)
    {
        var prepared = await loop.InvokeAsync(() =>
        {
            if (!ReferenceEquals(host.Runtime, loop.Runtime) || host.Content.Navigation is null || host.World.HasPendingDurableMutation || host.World.Authority.GetPhase(connection)is null)
                return null;
            var zone = WorldLayout.ZoneOf(centre.X, centre.Y);
            if (!float.IsFinite(centre.X) || !float.IsFinite(centre.Y) || !zone.IsInsideGrid)
                throw new InvalidDataException("Invalid approved creation region.");
            var zones = (
                from z in Enumerable.Range(-1, 3)from x in Enumerable.Range(-1, 3)let part = new ZoneCoordinate(zone.X + x, zone.Y + z)
                where part.IsInsideGrid
                select part).ToArray();
            var lease = host.World.Regions.Retain(zones, simulation: false);
            try
            {
                host.Runtime.Synchronize();
                return new Preparation(host.World, zones, lease);
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
                    if (!ReferenceEquals(prepared.World, host.World) || host.World.HasPendingDurableMutation || host.World.Authority.GetPhase(connection)is null)
                        return -1;
                    host.Runtime.Synchronize();
                    return prepared.Zones.All(host.Content.Navigation!.IsReady) ? 1 : 0;
                }, cancellation).ConfigureAwait(false);
                if (status != 0)
                    return status == 1;
            }
        }
    }

    sealed record Preparation(World World, ZoneCoordinate[] Zones, IDisposable Lease);
}