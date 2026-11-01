using System.Numerics;

namespace Meitou.Simulation.Dialogue;

/// <summary>The biome banner has faded for <see cref="GroupId"/> (a BIOME_GROUP): the active squad may banter about the area (<see cref="DialogueSystem"/>, "arrival dialog").</summary>
public sealed record AreaArrival(string GroupId, int SquadId = -1) : SimCommand;
public sealed record AreaArrivalMemory(ulong Leader, string GroupId);

public sealed partial class DialogueSystem
{
    readonly Dictionary<EntityId, string> areaArrivals = [];

    public AreaArrivalMemory[] CaptureAreaArrivals() => [.. areaArrivals.OrderBy(a => a.Key.Value).Select(a => new AreaArrivalMemory(a.Key.Value, a.Value))];

    void HashAreaArrivals(ref StateHasher hasher)
    {
        if (areaArrivals.Count == 0) return;
        hasher.Add(areaArrivals.Count);
        foreach (var (leader, group) in areaArrivals.OrderBy(a => a.Key.Value))
        { hasher.Add(leader.Value); hasher.Add(Rng.StableHash(group)); }
    }

    public void RestoreAreaArrivals(IEnumerable<AreaArrivalMemory> memories)
    {
        var restored = new Dictionary<EntityId, string>();
        foreach (var memory in memories)
            if (memory is null || memory.Leader == 0 || string.IsNullOrEmpty(memory.GroupId) || !restored.TryAdd(new(memory.Leader), memory.GroupId))
                throw new InvalidDataException("Invalid area arrival memory.");
        areaArrivals.Clear();
        foreach (var (leader, group) in restored) areaArrivals.Add(leader, group);
    }

    // A server has no biome banner or active UI squad. Observe each persistent player faction's squads once.
    void StepAreaArrivals(World world)
    {
        if (!world.Players.RequiresExplicitControl) return;
        foreach (var squad in world.Squads.All.ToArray())
        {
            if (!world.IsPlayerFaction(squad.Faction) || !world.Characters.TryResolveNext(squad.Leader, out int slot)
                || world.Characters.Cold(slot) is not { DiedTick: < 0 } leader || leader.Medical is { Dead: true }) continue;
            var position = world.Characters.Next[slot].Position;
            if (data.BiomeGroupAt(new Vector2(position.X, position.Z)) is not { } group) continue;
            bool changed = areaArrivals.TryGetValue(leader.Entity, out string? previous) && previous != group;
            areaArrivals[leader.Entity] = group;
            if (changed) Arrive(world, new AreaArrival(group, squad.Id) { Tick = world.Tick });
        }
    }

    /// <summary>
    /// <c>PlayerInterface::triggerAreaArrivalDialogue</c> (<c>FUN_1407ffe40</c>, docs/game/dialogue.md "Arrival banter"): with the active squad of at least 2, its members awake, not carried,
    /// not in a bed or cage, not animals, not fighting and not in a town are the candidates; nothing when a member senses an enemy; with 2 or more, a line of the group's
    /// <c>arrival dialog</c> is said between two of them. Engine: the original scores every candidate with every dialogue (<c>scoreCustomDialog</c>) and runs the best; here the first
    /// dialogue, speaker and partner (squad order) that have a line win, "on screen" is not tested, and an enemy sensed is one hostile awake within sight range.
    /// </summary>
    void Arrive(World world, AreaArrival a)
    {
        var table = world.Characters;
        if (world.Squads.Find(a.SquadId >= 0 ? a.SquadId : world.CommandPlayer(a)?.Squad ?? -1) is not { Members.Count: >= 2 } squad || data.Db.Find(a.GroupId) is not { } group) return;
        var roots = group.GetReferences("arrival dialog").Select(r => dialogue.Node(r.TargetStringId)).Where(n => n is { IsRoot: true }).ToList();
        if (roots.Count == 0) return;
        var candidates = new List<CharacterId>();
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { } c) continue;
            if (SensesEnemy(world, s, c)) return;
            if (!Awake(table, s) || !c.CarriedBy.IsNone || c.InSomething != Locks.HeldState.None || SquadRegistry.IsAnimal(c) || c.InCombat || data.TownAt(table.Next[s].Position) >= 0) continue;
            candidates.Add(id);
        }
        if (candidates.Count < 2) return;
        foreach (var root in roots)
            foreach (var speaker in candidates)
                foreach (var partner in candidates)
                    if (partner != speaker && StartBubbles(world, speaker, partner, root!, 0)) return;
    }

    /// <summary>Whether a character awake and hostile to <paramref name="c"/>'s faction is within its sight range and in view (the original's <c>numEnemies</c> &gt; 0).</summary>
    bool SensesEnemy(World world, int slot, CharacterCold c)
    {
        var table = world.Characters;
        float range = AggroSystem.RangeOf(c, AggroSystem.OutdoorSight);
        var at = table.Next[slot].Position;
        for (int o = 0; o < table.HighWater; o++)
        {
            if (o == slot || !Awake(table, o) || table.Cold(o) is not { } oc || !data.Relations.Contains(oc.Faction) || !AggroSystem.IsEnemy(data.Relations, c.Faction, oc.Faction)) continue;
            var there = table.Next[o].Position;
            if (Vector3.DistanceSquared(at, there) <= range * range && world.Sight.Sees(slot, o, at, table.Next[slot].Yaw, there, range) > 0) return true;
        }
        return false;
    }
}
