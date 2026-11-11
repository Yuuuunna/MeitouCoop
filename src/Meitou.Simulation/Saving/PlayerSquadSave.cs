using Meitou.Data.Save;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// Meitou multiplayer role/squad markers. Faction membership does not claim NPC squads.
/// </summary>
public static class PlayerSquadSave
{
    public const string WorldKey = "meitou multiplayer";
    public const string SquadKey = "meitou player squad";
    public const string SquadIdKey = "meitou squad id";
    public const string NextSquadKey = "meitou next squad";
    public const string RoleKey = "meitou player role";
    public const string DeadKey = "meitou role dead";

    internal static int? ValidateLoad(SaveGame save, PopulationData data, IReadOnlySet<string>? factions)
    {
        bool marked = save.Camera.Record.Ints.TryGetValue(WorldKey, out int version);
        if (!marked)
        {
            if (factions is not null) throw new InvalidDataException("Multiplayer load options require a multiplayer world checkpoint.");
            return null;
        }
        if (version != 1 || factions is null || factions.Any(f => f is null || data.FactionIndex(f) < 0) ||
            !save.Camera.Record.Ints.TryGetValue(NextSquadKey, out int next) || next < 0)
            throw new InvalidDataException("Multiplayer world requires its matching metadata and faction catalogue.");
        var ids = new HashSet<int>();
        foreach (var platoon in save.Platoons)
        {
            if (!platoon.Record.Bools.GetValueOrDefault(SquadKey)) continue;
            if (!platoon.IsLoaded || !factions.Contains(platoon.FactionId) || !platoon.Record.Ints.TryGetValue(SquadIdKey, out int id) ||
                id < 0 || id >= next || !ids.Add(id)) throw new InvalidDataException("Invalid or missing player squad.");
            foreach (var role in platoon.Characters)
                if (role.GameState?.Bools.GetValueOrDefault(RoleKey) == true && !factions.Contains(role.OwnerFactionId))
                    throw new InvalidDataException("Player role has an unknown player faction.");
        }
        return next;
    }
}