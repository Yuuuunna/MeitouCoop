using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Production;

/// <summary>
/// Craft options shared by benches of the same record and owning faction. Unlock changes invalidate that faction's lists; ownership changes
/// rebuild the site map, so one player's research never enables another faction's bench.
/// </summary>
sealed class CraftOptionsCache(Func<World, Machine, IReadOnlyList<CraftOptionView>> build)
{
    /// <summary>
    /// The immutable site map last published, or null when one of its inputs changed.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>>? _published;

    /// <summary>
    /// The crafting machines and their owners at the last publication, in registry order.
    /// </summary>
    (Machine Machine, int Faction)[] _benches = [];

    /// <summary>
    /// Each faction's immutable research unlock set used by its cached options.
    /// </summary>
    readonly Dictionary<int, IReadOnlySet<(CraftKind, string)>> _unlocked = [];

    /// <summary>
    /// The options of each faction's bench records, with shared machine records compared by reference.
    /// </summary>
    readonly Dictionary<int, Dictionary<MachineData, IReadOnlyList<CraftOptionView>>> _lists = [];

    /// <summary>
    /// Every crafting bench's options by site, retaining the immutable map while its exact inputs stay the same.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>> Of(World world)
    {
        foreach (var machine in world.Machines.All)
            if (machine.Craft is not null) Sync(world, FactionOf(world, machine));

        if (_published is not null && SameBenches(world)) return _published;

        _benches = [.. world.Machines.All.Where(m => m.Craft is not null).Select(m => (m, FactionOf(world, m)))];
        var map = new Dictionary<string, IReadOnlyList<CraftOptionView>>(StringComparer.Ordinal);
        foreach (var (machine, faction) in _benches) map[machine.Site] = ListOf(world, machine, faction);
        return _published = map;
    }

    /// <summary>
    /// The options of one site, or an empty list when it has no crafting bench.
    /// </summary>
    public IReadOnlyList<CraftOptionView> Of(World world, string site)
    {
        if (world.Machines.Find(site) is not { Data.Craft: not null } machine) return [];

        int faction = FactionOf(world, machine);
        Sync(world, faction);
        return ListOf(world, machine, faction);
    }

    /// <summary>
    /// Drops one faction's cached options when its immutable unlock set is replaced.
    /// </summary>
    void Sync(World world, int faction)
    {
        var unlocked = faction < 0 ? world.Research.UnlockedCrafts : world.Factions.GetOrCreate(faction).Research.UnlockedCrafts;
        if (_unlocked.TryGetValue(faction, out var previous) && ReferenceEquals(previous, unlocked)) return;

        _unlocked[faction] = unlocked;
        _lists.Remove(faction);
        _published = null;
    }

    /// <summary>
    /// Whether the same machines, owners and registry order still describe the published site map.
    /// </summary>
    bool SameBenches(World world)
    {
        int k = 0;
        foreach (var machine in world.Machines.All)
        {
            if (machine.Craft is null) continue;
            if (k == _benches.Length || !ReferenceEquals(machine, _benches[k].Machine)
                || FactionOf(world, machine) != _benches[k].Faction) return false;
            k++;
        }

        return k == _benches.Length;
    }

    /// <summary>
    /// The current canonical owner of a crafting machine, or the legacy world's faction when it has no site.
    /// </summary>
    static int FactionOf(World world, Machine machine) => world.Buildings.Find(machine.Site)?.Owner ?? -1;

    /// <summary>
    /// Builds a record's option list once for the owning faction's current research.
    /// </summary>
    IReadOnlyList<CraftOptionView> ListOf(World world, Machine machine, int faction)
    {
        if (machine.Data.Craft is null) return [];
        if (!_lists.TryGetValue(faction, out var records))
            _lists.Add(faction, records = new(ReferenceEqualityComparer.Instance));
        if (records.TryGetValue(machine.Data, out var list)) return list;

        return records[machine.Data] = build(world, machine);
    }
}
