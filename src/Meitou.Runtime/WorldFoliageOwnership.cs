using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Navigation;

namespace Meitou.Runtime;

/// <summary>Publishes all player factions' town ownership to this world's CPU foliage consumers at the serialized boundary.</summary>
public sealed class WorldFoliageOwnership(WorldLevelData levels, PopulationData population, NavSystem navigation) : IRuntimeWorldService
{
    World? active;

    public void Synchronize(World world)
    {
        if (active is not null && !ReferenceEquals(active, world)) throw new InvalidOperationException("Suspend the previous foliage owner before replacement.");
        active = world;
        var owned = new HashSet<(int X, int Z)>();
        foreach (var site in population.Sites)
            if ((uint)site.Index < (uint)population.SiteFactions.Count && world.IsPlayerFaction(population.SiteFactions[site.Index]))
                owned.Add(FoliageWorld.TownKey(site.Position.X, site.Position.Z));
        if (levels.FoliageOwnership.SetEquals(owned)) return;
        var tracked = navigation.TrackedZones.ToArray();
        levels.SetFoliageOwnership(owned);
        navigation.NewIncarnation();
        foreach (var zone in tracked) navigation.RebuildZone(zone);
    }

    public void Suspend(World world)
    {
        if (!ReferenceEquals(active, world)) return;
        // Content caches retain this snapshot until the replacement publishes its ownership or the content is disposed.
        active = null;
    }
}
