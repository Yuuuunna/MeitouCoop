using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

internal sealed record SavedResearchProgress(string Id, float Seconds);
internal sealed record SavedRank(int Faction, int Rank);
internal sealed record SavedResearch(string[] Completed, SavedResearchProgress[] Queue, string[] Paid,
    SavedResearchProgress[] Partial, long Version, bool Changed);
internal sealed record SavedFactionResources(int Faction, long Money, string? Name, SavedRank[] Ranks, SavedResearch Research);

/// <summary>
/// The faction-resource portion of multiplayer metadata; independent of the legacy CAMERA money/RESEARCH aliases.
/// </summary>
internal static class FactionResourceSave
{
    internal static SavedFactionResources[] Capture(World world) => world.Factions.All.Select(f => new SavedFactionResources(
        f.Index, f.Money, f.Name, f.Ranks.Select(r => new SavedRank(r.Key, r.Value)).ToArray(),
        new SavedResearch(f.Research.Completed.ToArray(), f.Research.Queue.Select(q => new SavedResearchProgress(q.Id, q.Seconds)).ToArray(),
            f.Research.Paid.ToArray(), f.Research.Partial.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new SavedResearchProgress(p.Key, p.Value)).ToArray(),
            f.Research.Version, f.Research.Changed))).ToArray();

    internal static void Validate(SavedFactionResources[] resources, PopulationData data)
    {
        if (resources is null) throw new InvalidDataException("Missing faction resources.");
        var factions = new HashSet<int>();
        foreach (var faction in resources)
        {
            if (faction is null || faction.Faction < 0 || faction.Faction >= data.Factions.Count || !factions.Add(faction.Faction) ||
                faction.Ranks is null || faction.Research is not { Completed: not null, Queue: not null, Paid: not null, Partial: not null } research || research.Version < 0)
                throw new InvalidDataException("Invalid faction resource state.");
            var ranks = new HashSet<int>();
            foreach (var rank in faction.Ranks)
                if (rank is null || rank.Faction < 0 || rank.Faction >= data.Factions.Count || rank.Rank < 0 || !ranks.Add(rank.Faction))
                    throw new InvalidDataException("Invalid faction rank.");
            bool Id(string id) => id is not null && data.Db.Find(id) is { Type: FcsRecordType.Research };
            bool Progress(SavedResearchProgress p) => p is not null && Id(p.Id) && float.IsFinite(p.Seconds) && p.Seconds >= 0;
            if (!research.Completed.All(Id) || !research.Paid.All(Id) || !research.Queue.All(Progress) || !research.Partial.All(Progress) ||
                research.Paid.Distinct(StringComparer.Ordinal).Count() != research.Paid.Length ||
                research.Partial.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != research.Partial.Length)
                throw new InvalidDataException("Invalid or missing research content.");
            var candidate = new ResearchState();
            Apply(candidate, research, data.Db);
            if (!candidate.Completed.SequenceEqual(research.Completed, StringComparer.Ordinal) ||
                !candidate.Queue.Select(q => (q.Id, q.Seconds)).SequenceEqual(research.Queue.Select(q => (q.Id, q.Seconds))))
                throw new InvalidDataException("Research state is incompatible with the loaded content.");
        }
    }

    internal static void Restore(World world, SavedFactionResources[] resources, GameDatabase db)
    {
        foreach (var saved in resources)
        {
            var faction = world.Factions.GetOrCreate(saved.Faction);
            faction.Money = saved.Money;
            faction.Name = saved.Name;
            faction.RestoreRanks(saved.Ranks.Select(r => new KeyValuePair<int, int>(r.Faction, r.Rank)));
            Apply(faction.Research, saved.Research, db);
        }
    }
    static void Apply(ResearchState research, SavedResearch saved, GameDatabase db)
    {
        research.Set(db, saved.Completed, saved.Queue.Select(q => (q.Id, q.Seconds)), saved.Paid, saved.Partial.Select(q => (q.Id, q.Seconds)));
        research.RestoreVersion(saved.Version, saved.Changed);
    }
}