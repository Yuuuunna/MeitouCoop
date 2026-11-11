using Meitou.Simulation.Population;
namespace Meitou.Simulation.Saving;

public sealed record CampaignTargetClock(int Faction, string CampaignId, string TownInstance, double LastStart);
public sealed record CampaignTargetState(CampaignTargetClock[] Clocks);

/// <summary>
/// Base-specific campaign repeat clocks, keyed by persistent town instance rather than load-order town numbers.
/// </summary>
public static class CampaignTargetSave
{
    public static CampaignTargetState Capture(World world)
    {
        var clocks = new List<CampaignTargetClock>();
        foreach (var population in world.Systems.OfType<PopulationSystem>())
            foreach (var manager in population.Wars)
                foreach (var known in manager.Known)
                    foreach (var (target, hour) in known.TargetLastStarts.OrderBy(t => t.Key.Site).ThenBy(t => t.Key.PlayerTown))
                    {
                        string? instance = target.Site >= 0 ? population.Data.Sites[target.Site].InstanceId : world.Buildings.FindTown(target.PlayerTown)?.SaveInstance;
                        // A destroyed base ends its trigger scope. A rebuilt base has a new persistent instance.
                        if (instance is null) continue;
                        clocks.Add(new(manager.Faction, known.Id, instance, hour));
                    }
        return new([.. clocks]);
    }

    public static void Validate(CampaignTargetState state, PopulationData data)
    {
        if (state.Clocks is null) throw new InvalidDataException("Missing campaign target clocks.");
        var seen = new HashSet<(int, string, string)>();
        foreach (var clock in state.Clocks)
            if (clock is null || clock.Faction < 0 || clock.Faction >= data.Factions.Count || string.IsNullOrEmpty(clock.CampaignId)
                || string.IsNullOrEmpty(clock.TownInstance) || !double.IsFinite(clock.LastStart) || clock.LastStart < 0
                || !seen.Add((clock.Faction, clock.CampaignId, clock.TownInstance)))
                throw new InvalidDataException("Invalid campaign target clock.");
    }

    /// <summary>
    /// After buildings and legacy war records are restored, before the first simulation tick.
    /// </summary>
    public static void Restore(World world, PopulationData data, CampaignTargetState state)
    {
        Validate(state, data);
        foreach (var population in world.Systems.OfType<PopulationSystem>())
        {
            population.EnsureWars(world);
            foreach (var manager in population.Wars)
                foreach (var known in manager.Known) known.TargetLastStarts.Clear();
            foreach (var clock in state.Clocks)
            {
                if (population.WarOf(clock.Faction) is not { } manager || population.CampaignOf(clock.CampaignId) is null) continue;
                CampaignTown target = CampaignTown.None;
                foreach (var site in data.Sites)
                    if (site.InstanceId == clock.TownInstance) { target = CampaignTown.OfSite(site.Index); break; }
                if (target.IsNone)
                    foreach (var town in world.Buildings.Towns)
                        if (town.SaveInstance == clock.TownInstance) { target = CampaignTown.OfPlayer(town.Number); break; }
                if (!target.IsNone) manager.Know(clock.CampaignId).TargetLastStarts[target] = clock.LastStart;
            }
        }
    }
}