using Meitou.Simulation.Population;
namespace Meitou.Simulation.Saving;

public sealed record SavedFactionDefinition(string Id, string Name, string Template);
public sealed record FactionCatalogueState(int Version, int ContentCount, SavedFactionDefinition[] Factions);

/// <summary>
/// Stable faction-index catalogue. Restore on exclusive candidate data before any world records are loaded.
/// </summary>
public static class FactionCatalogueSave
{
    public static FactionCatalogueState Capture(PopulationData data)
    {
        var state = new FactionCatalogueState(1, data.Relations.ContentCount,
            data.Factions.Select((f, i) => new SavedFactionDefinition(f.Id, f.Name, data.FactionTemplate(i))).ToArray());
        Validate(data, state, complete: true);
        return state;
    }

    /// <summary>
    /// All definitions are checked before appending. Never remove or reorder an existing runtime faction.
    /// </summary>
    public static void Restore(PopulationData data, FactionCatalogueState state)
    {
        Validate(data, state, complete: false);
        for (int i = data.Factions.Count; i < state.Factions.Length; i++)
        {
            var faction = state.Factions[i];
            int index = data.RegisterFaction(faction.Id, faction.Name, data.FactionIndex(faction.Template));
            if (index != i) throw new InvalidOperationException("The faction catalogue changed during restoration.");
        }
    }

    internal static void Validate(PopulationData data, FactionCatalogueState state, bool complete)
    {
        if (state is null || state.Version != 1 || state.Factions is null || state.ContentCount != data.Relations.ContentCount ||
            state.ContentCount < 0 || state.ContentCount > state.Factions.Length || data.Factions.Count > state.Factions.Length ||
            complete && data.Factions.Count != state.Factions.Length || data.Factions.Count != data.Relations.Count ||
            data.Factions.Where((f, i) => f.Id != data.Relations.Factions[i].Id).Any())
            throw new InvalidDataException("Faction catalogue does not match the loaded content.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var templates = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < state.Factions.Length; i++)
        {
            var saved = state.Factions[i];
            if (saved is null || string.IsNullOrWhiteSpace(saved.Id) || !ids.Add(saved.Id) || saved.Name is null ||
                i >= state.ContentCount && string.IsNullOrWhiteSpace(saved.Name) || string.IsNullOrWhiteSpace(saved.Template))
                throw new InvalidDataException("Invalid or duplicate faction definition.");
            if (i < state.ContentCount)
            {
                if (saved.Template != saved.Id || data.Factions[i].Id != saved.Id || data.FactionTemplate(i) != saved.Template)
                    throw new InvalidDataException("Static faction order or template differs.");
                templates.Add(saved.Id);
            }
            else if (!templates.Contains(saved.Template)) throw new InvalidDataException("Runtime faction template is not loaded content.");
            if (i < data.Factions.Count && (data.Factions[i].Id != saved.Id || data.FactionTemplate(i) != saved.Template ||
                i >= state.ContentCount && data.Factions[i].Name != saved.Name))
                throw new InvalidDataException("Existing runtime faction differs from the checkpoint.");
        }
    }
}