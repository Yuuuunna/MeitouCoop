using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
namespace Meitou.Simulation;

/// <summary>
/// Public identity only. Resource accounts and membership privileges remain in PlayerFactionView.
/// </summary>
public sealed record FactionIdentityView(int Faction, string? RecordId, string Name, bool NotReal, bool PlayerOwned);
/// <summary>
/// A member faction's directed standing towards a disclosed faction.
/// </summary>
public sealed record FactionStandingView(int Faction, float Relation, bool Ally, bool Hostile);

public sealed partial class World
{
    PopulationData? FactionDataForView => SystemOf<PopulationSystem>()?.Data ?? SystemOf<PlayerSystem>()?.Data;

    public FactionIdentityView? DescribeFaction(int faction)
    {
        if (faction < 0) return null;
        var data = FactionDataForView;
        var definition = data is not null && data.Relations.Contains(faction) ? data.Factions[faction] : null;
        var resource = Factions.Find(faction);
        if (definition is null && resource is null) return null;
        return new(faction, definition?.Id, resource?.Name ?? definition?.Name ?? "", definition?.NotReal ?? false, IsPlayerFaction(faction));
    }

    public FactionStandingView? DescribeStanding(int from, int other)
    {
        if (from < 0 || other < 0) return null;
        if (from == other) return new(other, 100, true, false);
        var data = FactionDataForView;
        if (data is null || !data.Relations.Contains(from) || !data.Relations.Contains(other)) return null;
        return new(other, data.Relations.Get(from, other), data.Relations.IsAlly(from, other), data.Relations.IsHostile(from, other));
    }

    /// <summary>
    /// Local presentation may inspect its loaded catalogue; remote capture supplies its permitted indices explicitly.
    /// </summary>
    public IReadOnlyList<FactionIdentityView> LocalFactionCatalogue()
        => DescribeFactions(Enumerable.Range(0, FactionDataForView?.Factions.Count ?? 0).Concat(Factions.All.Select(f => f.Index)));

    IReadOnlyList<FactionIdentityView> DescribeFactions(IEnumerable<int> indices)
        => indices.Where(i => i >= 0).Distinct().Order().Select(DescribeFaction).OfType<FactionIdentityView>().ToArray();

    IReadOnlyList<FactionIdentityView> CaptureFactionCatalogue(PlayerState state, IEnumerable<int> memberships, WorldSnapshot snapshot)
    {
        var data = FactionDataForView;
        var townOwners = data is null ? [] : Enumerable.Range(0, Math.Min(snapshot.Towns.Count, data.SiteFactions.Count))
            .Where(i => (snapshot.Towns[i] & TownKnowledge.Discovered) != 0).Select(i => data.SiteFactions[i]);
        return DescribeFactions(state.KnownFactions.Concat(memberships).Concat(townOwners)
            .Concat(snapshot.Characters.Select(c => c.Faction))
            .Concat(snapshot.Buildings.Select(b => b.Owner)).Concat(snapshot.Buildings.Select(b => b.AuthorityFaction))
            .Concat(snapshot.Machines.Select(m => m.AuthorityFaction)).Concat(snapshot.DoorPicks.Select(d => d.Owner))
            .Concat(snapshot.Containers.Select(c => c.Owner)).Concat(snapshot.Beds.Select(b => b.Owner))
            .Concat(snapshot.Cages.Select(c => c.Owner)).Concat(snapshot.Outposts.Select(o => o.Owner)));
    }
}