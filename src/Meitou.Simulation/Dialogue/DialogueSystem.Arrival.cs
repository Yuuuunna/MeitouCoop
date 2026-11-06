using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

/// <summary>The biome banner has faded for <see cref="GroupId"/> (a BIOME_GROUP): the active squad may banter about the area (<see cref="DialogueSystem"/>, "arrival dialog").</summary>
public sealed record AreaArrival(string GroupId, int SquadId = -1, int Serial = 0) : SimCommand
{
    public CharacterId Leader { get; init; } = CharacterId.None;
}
public sealed record AreaArrivalMemory(ulong Leader, string GroupId);


public sealed partial class DialogueSystem
{
    readonly Dictionary<EntityId, string> areaArrivals = [];
    readonly Dictionary<EntityId, long> areaArrivalRetry = [];

    public AreaArrivalMemory[] CaptureAreaArrivals() => [.. areaArrivals.OrderBy(a => a.Key.Value).Select(a => new AreaArrivalMemory(a.Key.Value, a.Value))];

    void HashAreaArrivals(ref StateHasher hasher)
    {
        if (areaArrivals.Count == 0) return;
        hasher.Add(areaArrivals.Count);
        foreach (var (leader, group) in areaArrivals.OrderBy(a => a.Key.Value))
        { hasher.Add(leader.Value); hasher.Add(Rng.StableHash(group)); }
        foreach (var (leader, tick) in areaArrivalRetry.OrderBy(a => a.Key.Value))
        { hasher.Add(leader.Value); hasher.Add(tick); }
    }

    public void RestoreAreaArrivals(IEnumerable<AreaArrivalMemory> memories)
    {
        var restored = new Dictionary<EntityId, string>();
        foreach (var memory in memories)
            if (memory is null || memory.Leader == 0 || string.IsNullOrEmpty(memory.GroupId) || !restored.TryAdd(new(memory.Leader), memory.GroupId))
                throw new InvalidDataException("Invalid area arrival memory.");
        areaArrivals.Clear();
        areaArrivalRetry.Clear();
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
            if (changed) areaArrivalRetry[leader.Entity] = world.Tick;
            if (!areaArrivalRetry.TryGetValue(leader.Entity, out long next) || world.Tick < next) continue;
            if (TryArrive(world, new AreaArrival(group, squad.Id) { Tick = world.Tick })) areaArrivalRetry.Remove(leader.Entity);
            else areaArrivalRetry[leader.Entity] = world.Tick + (long)Math.Ceiling(10 / world.TickSeconds);
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
        bool forget = TryArrive(world, a);
        int result = a.Serial * 2 + (forget ? 1 : 0);
        if (a.Issuer is { } actor) arrivalResults[actor.Value] = result;
        else Volatile.Write(ref arrivalResult, result);
    }

    int arrivalResult;
    readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, int> arrivalResults = [];
    public (int Serial, bool Forget) ArrivalResultFor(PlayerId actor)
    { int r = arrivalResults.GetValueOrDefault(actor.Value); return (r >> 1, (r & 1) != 0); }

    /// <summary>
    /// The result of the last arrival try: its <see cref="AreaArrival.Serial"/> and whether the host forgets the group (<c>FUN_140800eb0</c>: a non-zero result of the banter,
    /// squad of under 2 or a line started; an enemy sensed, under 2 candidates or no pair with a line keep the 10 s retry going).
    /// </summary>
    public (int Serial, bool Forget) ArrivalResult { get { int r = Volatile.Read(ref arrivalResult); return (r >> 1, (r & 1) != 0); } }

    /// <summary>One try of <see cref="Arrive"/>; true: forget the group.</summary>
    bool TryArrive(World world, AreaArrival a)
    {
        var table = world.Characters;
        if (world.Squads.Find(a.SquadId >= 0 ? a.SquadId : world.CommandPlayer(a)?.Squad ?? -1) is not { Members.Count: >= 2 } squad) return true;
        if (a.Issuer is { } actor && (a.Leader != squad.Leader || !world.MayOrder(a, squad.Leader) || world.Players.Find(actor)?.State.Faction != squad.Faction)) return false;
        if (data.Db.Find(a.GroupId) is not { } group) return false;

        var roots = group.GetReferences("arrival dialog").Select(r => dialogue.Node(r.TargetStringId)).Where(n => n is { IsRoot: true }).ToList();
        if (roots.Count == 0) return false;
        var candidates = new List<CharacterId>();
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { } c) continue;
            // Only a member that is up, free, not an animal and out of melee counts for the enemy test, which aborts the whole arrival; the town filter comes after it (FUN_1407ffe40 :100-121).
            if (!Awake(table, s) || !c.CarriedBy.IsNone || c.InSomething != Locks.HeldState.None || SquadRegistry.IsAnimal(c) || c.InCombat) continue;
            if (SensesEnemy(world, s, c)) return false;
            if (data.TownAt(table.Next[s].Position) >= 0) continue;
            candidates.Add(id);
        }
        if (candidates.Count < 2) return false;
        foreach (var root in roots)
            foreach (var speaker in candidates)
                foreach (var partner in candidates)
                    if (partner != speaker && MaySpeak(world, root!, speaker) && StartBubbles(world, speaker, partner, root!, 0)) return true;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="speaker"/> may start <paramref name="root"/> (<c>FUN_14067e060</c>, <c>FUN_140684d70</c>, docs/game/dialogue.md "Arrival banter"): a character whose record says
    /// <c>dumb</c> only when the dialogue's <c>is character</c> names it (an empty list fails), another when the list is empty or names it.
    /// </summary>
    bool MaySpeak(World world, DialogueNode root, CharacterId speaker)
    {
        if (Cold(world, speaker) is not { } c) return false;
        return ListsSpeaker(root, c.RecordId);
    }

    /// <summary>The original's <c>numEnemies</c> &gt; 0: the character's senses count an enemy (<see cref="CharacterFacts.Senses"/>).</summary>
    static bool SensesEnemy(World world, int slot, CharacterCold c) => CharacterFacts.Senses(world, slot) is { Enemies: > 0 };
}
