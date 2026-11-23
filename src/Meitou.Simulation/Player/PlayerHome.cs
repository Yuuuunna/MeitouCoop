using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Player;

/// <summary>
/// Finds the player faction's home building (<see cref="PlayerState.Home"/>; docs/game/factions-squads-towns.md "The player's home"). The
/// original's player faction has an Ownerships of its own (Faction <c>+0x80</c>, made with the faction, <c>FUN_1407fe270</c>; not saved), which
/// every player character's <c>getOwnerships</c> <c>FUN_1407956a0</c> returns. Its home (<c>+0x38</c>) is taken once, while it is null, by
/// <c>Ownerships::addOwned</c> <c>FUN_1407ecb00</c> from the first building it is given that <c>setHome</c> <c>FUN_1407ec9e0</c> accepts
/// (<see cref="Accepts"/>): the building factory gives it every building of the player's faction that is no door as it is made, placed or
/// loaded (<c>FUN_14057cc70</c>), and the purchase of a town house gives it the house (<c>FUN_1407ad6c0</c>). Nothing clears it when the building
/// goes, so it then names nothing until the next load.
/// </summary>
internal sealed class PlayerHome
{
    /// <summary>
    /// Each faction's registry revision and owned-house count at its last unsuccessful search.
    /// </summary>
    readonly Dictionary<int, (long Revision, int Houses)> _searched = [];

    /// <summary>
    /// Takes the home while the player has none: the first building of the player's faction that <see cref="Accepts"/>. Serial. The original
    /// takes them in the order its zones make their buildings; Meitou takes the town houses first, by key, then the placed buildings in the
    /// registry's order (engine choice: a zone's level buildings come before the ones built in it).
    /// </summary>
    public void Find(World world, ConstructionSystem construction)
    {
        foreach (int faction in world.PlayerFactionIndices()) FindFaction(world, construction, faction);
    }

    /// <summary>
    /// Takes one faction's home independently of the active command's player context.
    /// </summary>
    void FindFaction(World world, ConstructionSystem construction, int faction)
    {
        var state = world.Factions.GetOrCreate(faction);
        if (state.Home is not null) return;

        int houses = world.Fixtures.OwnedProperties(faction).Count;
        var revision = (world.Buildings.Revision, houses);
        if (_searched.TryGetValue(faction, out var searched) && searched == revision) return;

        _searched[faction] = revision;
        state.Home = world.Fixtures.FirstHome(faction) ?? FirstSite(world, construction, faction);
    }

    /// <summary>
    /// The first placed building of the faction that <see cref="Accepts"/>, or null.
    /// </summary>
    static string? FirstSite(World world, ConstructionSystem construction, int faction)
    {
        foreach (var site in world.Buildings.All)
            if (site.Owner == faction && construction.Data(site.Record) is { } data && Accepts(data)) return site.Id;

        return null;
    }

    /// <summary>
    /// Whether a building of this record can be the home (<c>FUN_1407ec9e0</c>, <b>Verified</b>): a gate (<c>is wall gate</c>, the factory's
    /// GatewayBuilding, Building slot 123 non-null), or one with an interior (Building <c>+0x1f0</c>) that is no wall (<c>links together</c>,
    /// the factory's WallBuilding, slot 89 non-null). A door (Building <c>+0x1a0</c>) is never given to the Ownerships.
    /// </summary>
    public static bool Accepts(BuildingBuildData data) => !data.IsDoor && (data.IsWallGate || data.HasInterior && !data.LinksTogether);
}
