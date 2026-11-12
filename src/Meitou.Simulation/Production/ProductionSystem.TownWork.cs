using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Production;
/// <summary>
/// The machines of NPC towns (docs/game/buildings-production.md "NPC town machines"): a town building an NPC worker (goals 189 / 190, AiSystem Work)
/// stands at has one canonical level-machine site owned by its registered faction, shared with inventories and saving, made on first use.
/// The workers are the AI's: the decision step keeps AiMind.WorkOperate (Task_OperateMachine, row
/// 87) or AiMind.CollectFrom (Task_EmptyMachine, row 92) while they are at the building, and this system applies them.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// The town catalogue used to resolve a turret on its first player order.
    /// </summary>
    readonly PopulationData? _towns;

    /// <summary>
    /// The canonical placement key shared by town work, machine inventories and saves.
    /// </summary>
    public static string TownWorkId(TownBuilding building) => building.Key;

    /// <summary>
    /// Resolves a registered town turret to its ordinary level-machine site without creating a second identity.
    /// </summary>
    BuildSite? TownTurret(World world, string id)
    {
        if (_towns is not { } data) return null;

        for (int s = 0; s < data.Sites.Count; s++)
        {
            if (data.Turrets(s).FirstOrDefault(b => b.Key == id) is not { } turret) continue;
            if (TownWork(world, turret) is null) return null;

            return world.Buildings.Find(id);
        }

        return null;
    }

    /// <summary>
    /// State 66 <c>haveSomeResourcesFrom</c> (<c>FUN_140597530</c>, <b>Observed</b>): the character carries the machine's output item.
    /// </summary>
    public static bool CarriesOutputOf(CharacterCold cold, Machine m) => m.Output is { } o && Carries(cold, o.Recipe.Item);

    /// <summary>
    /// State 67 <c>haveSomeResourcesFor</c> (<c>FUN_1405a45f0</c>, <b>Observed</b>): the character carries an item one of the machine's inputs takes.
    /// </summary>
    public static bool CarriesInputOf(CharacterCold cold, Machine m) => m.Inputs.Any(input => input.Rate > 0 && Carries(cold, input.Item));

    /// <summary>
    /// Whether the character carries <paramref name="item"/> (in its inventory or its backpack's).
    /// </summary>
    static bool Carries(CharacterCold cold, string item) =>
        cold.Inventory is { } inv && (inv.All().Any(i => i.Record == item) || inv.Backpack?.Contents?.All().Any(i => i.Record == item) == true);

    /// <summary>
    /// The finder of row 94 (<c>FUN_14059d9b0</c>, scored by <c>FUN_14059d000</c>, <b>Verified</b>): for each item type the target machine's inputs
    /// take, in list order, the nearest other machine whose output holds that item and has some; the first item with one decides. <b>Not
    /// modelled</b>: the cached earlier pick, loose items on the ground (searched first by the original), and the split of buildings by the owner
    /// test of vfunc <c>0x3b0</c> (<b>Unknown</b>); the candidates are the town's workplaces (the original's building mask is not decoded).
    /// </summary>
    public static TownBuilding? InputSource(World world, Machine target, IEnumerable<TownBuilding> candidates, Vector3 from)
    {
        foreach (var input in target.Inputs)
        {
            TownBuilding? best = null;
            float bestD = float.MaxValue;
            foreach (var b in candidates)
            {
                if (TownWorkOf(world, b) is not { Output: { Count: > 0 } o } other || ReferenceEquals(other, target) || o.Recipe.Item != input.Item)
                    continue;

                float d = Vector3.DistanceSquared(new Vector3(from.X, 0, from.Z), new Vector3(b.Position.X, 0, b.Position.Z));
                if (d < bestD) (best, bestD) = (b, d);
            }

            if (best is not null) return best;
        }

        return null;
    }

    /// <summary>
    /// <c>Task_FillMachine</c> (row 93, <c>FUN_140344510</c>, <b>Verified</b>) at a machine with a production component: the carried items (inventory
    /// and worn backpack) its inputs accept go into them up to each input's capacity, a unit at a time.
    /// </summary>
    void FillInputs(World world, CharacterCold cold, Machine m)
    {
        if (cold.Inventory is not { } inv) return;
        bool moved = false;
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0) continue;
            foreach (var item in inv.All().Concat(inv.Backpack?.Contents?.All() ?? []).Where(i => i.Record == input.Item).ToList())
            {
                int n = Math.Min(input.Recipe.Capacity - input.Count, item.Quantity);
                if (n <= 0) break;
                input.Count += n;
                item.Quantity -= n;
                if (item.Quantity == 0) (inv.Items.Contains(item) ? inv : inv.Backpack!.Contents!).Remove(item);
                moved = true;
            }
        }

        if (moved) world.Machines.Touch();
    }

    public static Machine? TownWorkOf(World world, TownBuilding building) => world.Machines.Find(building.Key);
    /// <summary>
    /// The workers of town machines at their building this tick (serial, before the players' operators): the AI's workers that stand at their
    /// building doing row 87 operate it (a producer only: a research bench counts for the player's research alone, and a crafting bench with no queue
    /// refuses an operator, <c>dontNeedWorkRightNow</c>, so both are pretence), those doing row 92 empty its output into their inventory.
    /// </summary>
    void TownWorkers(World world, float dt)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i)is not { Ai: { WorkOperate: not null } or { CollectFrom: not null } or { FillTo: not null } } cold)
                continue;
            ref var n = ref table.Next[i];
            var mind = cold.Ai!;
            var building = mind.WorkOperate ?? mind.CollectFrom ?? mind.FillTo!;
            if (!Able(cold) || !Ai.AiSystem.Holds(n, cold) || mind.Behaviour != Ai.AiBehaviour.Work || !AtTownWork(n, building, mind.WorkRange))
                continue;
            if ((world.Machines.Find(TownWorkId(building)) ?? TownWork(world, building))is not { } m || !Live(world, m))
                continue;
            if (world.Buildings.Find(m.Site)is not { Complete: true, Destroyed: false } site || site.Record != building.BuildingId || site.Owner != cold.Faction && !(cold.SlaveState == 1 && cold.SlaveOwner == site.Owner))
                continue;
            if (mind.FillTo is not null)
                FillInputs(world, cold, m);
            else if (mind.CollectFrom is not null)
                TakeOutput(world, cold, m);
            else if (mind.WorkOperate is not null)
                TownOperate(world, i, cold, m, dt);
        }
    }

    /// <summary>
    /// Whether a worker stands at the town building within <paramref name="reach"/>, idle or resting.
    /// </summary>
    static bool AtTownWork(in CharacterHot n, TownBuilding building, float reach) =>
        Ai.AiSystem.AtWork(in n, building.Position, reach) && (CharacterTask)n.Task is CharacterTask.Idle or CharacterTask.Rest;
    /// <summary>
    /// One worker's frame at a town producer: <see cref = "ProduceStep"/> at the worker's skill, and the operating XP (the same lines as a player's
    /// operator).
    /// </summary>
    void TownOperate(World world, int slot, CharacterCold cold, Machine m, float dt)
    {
        if (!m.Data.IsProducer || m.Data.IsResearch || m.Data.IsCrafting || m.Farm is not null || m.Output is null)
            return;
        if (m.Data.IsAutomatic || m.Operators >= m.Data.MaxOperators || m.Broken)
            return;
        m.Operators++;
        var stat = (StatsEnumerated)m.Data.Stat;
        float skill = m.Data.Stat > 0 ? Effective(cold, stat) : -1;
        float s = skill < 0 ? 1 : Lerp(0.5f, 1.4f, skill / 100);
        cold.Operating = ProduceStep(world, m, s, dt);
        OperatingXp(world, cold, m.Data.Stat, dt);
    }

    /// <summary>
    /// <c>Task_EmptyMachine</c> (rows 92 and 284, <c>FUN_14033cd10</c>, <b>Verified</b>) at a machine with an output: the output's items go into the
    /// character's inventory (the worn backpack when the main grid is full) while there is room, a unit at a time; a producer whose status was 1
    /// (output full) then goes back to 2.
    /// </summary>
    void TakeOutput(World world, CharacterCold cold, Machine m)
    {
        if (cold.Inventory is not { } inv || m.Output is not { Count: > 0 } output) return;
        bool moved = false;
        while (output.Count > 0 && _items.Create(output.Recipe.Item, 1) is { } unit && inv.HasRoomFor(unit) && inv.Add(unit))
        {
            output.Count--;
            moved = true;
        }

        if (!moved) return;
        if (m.Status == 1) m.Status = 2;
        world.Machines.Touch();
    }
}
