using System.Numerics;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

/// <summary>
/// The ownership test the AI's loose-item finders ask (<c>FUN_140997d80</c>, <b>Verified</b>; docs/game/character-stats.md "Food on the ground",
/// docs/game/buildings-production.md "Material delivery"): 259's ground pass and the source finder <c>FUN_14059d000</c> (building materials, a
/// machine's inputs).
/// </summary>
internal static class ItemOwnership
{
    /// <summary>
    /// Whether the character standing at <paramref name="at"/> may take the loose item <paramref name="g"/>: one with no owner, or of its faction;
    /// one of another faction only while the character's status town is a player faction's (<see cref="PlayerTownOwner"/>) and the item lies outdoors
    /// or in a building of the player's faction. A world without population data has no status towns.
    /// </summary>
    public static bool MayTake(World world, PopulationData? data, CharacterCold cold, Vector3 at, GroundItem g)
    {
        if (g.OwnedBy < 0 || g.OwnedBy == cold.Faction) return true;
        if (data is null || PlayerTownOwner(world, data, at, cold) is not (>= 0 and var owner)) return false;
        if (cold.IsPlayer && world.Players.RequiresExplicitControl && !world.MayUseAssets(cold, owner)) return false;

        int place = world.Sight.PlaceAt(g.Position);
        if (place < 0) return true;

        return world.Buildings.Find(world.Sight.PlaceKey(place)) is { } building && building.Owner == owner;
    }

    /// <summary>
    /// The player faction owning the character's status town (<see cref="Ai.AiSystem.StatusTownAt"/>), a placed town or outpost; -1 otherwise.
    /// </summary>
    static int PlayerTownOwner(World world, PopulationData data, Vector3 at, CharacterCold cold)
    {
        var town = Ai.AiSystem.StatusTownAt(data, world, at, cold.StatusTown);
        int owner = town.Owner(world, data);
        return world.IsPlayerFaction(owner) ? owner : -1;
    }
}
