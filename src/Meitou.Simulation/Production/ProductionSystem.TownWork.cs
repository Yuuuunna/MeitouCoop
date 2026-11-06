using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

// NPC workers use ordinary level placements through the canonical building registry.
public sealed partial class ProductionSystem
{
    public static string TownWorkId(TownBuilding building) => building.Key;

    /// <summary>Lazily creates the machine of a registered town placement with its resolved owner.</summary>
    public Machine? TownWork(World world, TownBuilding building)
    {
        if (Data(building.BuildingId) is null) return null;
        var site = world.Buildings.Find(building.Key) ?? world.Fixtures.GetTownMachineSite(world, building);
        return site is { Complete: true, Destroyed: false } && site.Record == building.BuildingId ? Ensure(world, site) : null;
    }

    public static Machine? TownWorkOf(World world, TownBuilding building) => world.Machines.Find(building.Key);

    /// <summary>
    /// State 66 <c>haveSomeResourcesFrom</c> (<c>FUN_140597530</c>, <b>Observed</b>): the character carries the machine's output item.
    /// </summary>
    public static bool CarriesOutputOf(CharacterCold cold, Machine m) => m.Output is { } o && Carries(cold, o.Recipe.Item);

    /// <summary>State 67 <c>haveSomeResourcesFor</c> (<c>FUN_1405a45f0</c>, <b>Observed</b>): the character carries an item one of the machine's inputs takes.</summary>
    public static bool CarriesInputOf(CharacterCold cold, Machine m) => m.Inputs.Any(input => input.Rate > 0 && Carries(cold, input.Item));

    static bool Carries(CharacterCold cold, string item) => cold.Inventory is { } inv && (inv.All().Any(i => i.Record == item) || inv.Backpack?.Contents?.All().Any(i => i.Record == item) == true);

    /// <summary>
    /// The finder of row 94 (<c>FUN_14059d9b0</c>, scored by <c>FUN_14059d000</c>, <b>Verified</b>): for each item type the target machine's inputs take, in list order, the nearest other machine whose output holds that item and
    /// has some; the first item with one decides. <b>Not modelled</b>: the cached earlier pick, loose items on the ground (searched first by the original), and the split of buildings by the owner test of vfunc <c>0x3b0</c>
    /// (<b>Unknown</b>); the candidates are the town's workplaces (the original's building mask is not decoded).
    /// </summary>
    public static TownBuilding? InputSource(World world, Machine target, IEnumerable<TownBuilding> candidates, Vector3 from)
    {
        foreach (var input in target.Inputs)
        {
            TownBuilding? best = null;
            float bestD = float.MaxValue;
            foreach (var b in candidates)
            {
                if (TownWorkOf(world, b) is not { Output: { Count: > 0 } o } other || ReferenceEquals(other, target) || o.Recipe.Item != input.Item) continue;
                float d = Vector3.DistanceSquared(new Vector3(from.X, 0, from.Z), new Vector3(b.Position.X, 0, b.Position.Z));
                if (d < bestD) (best, bestD) = (b, d);
            }
            if (best is not null) return best;
        }
        return null;
    }

    /// <summary>
    /// <c>Task_FillMachine</c> (row 93, <c>FUN_140344510</c>, <b>Verified</b>) at a machine with a production component: the carried items (inventory and worn backpack) its inputs accept go into them up to
    /// each input's capacity, a unit at a time.
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
    void PrepareTownWorkers(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { Ai: { Behaviour: Ai.AiBehaviour.Work } mind } cold
                && (mind.WorkOperate ?? mind.CollectFrom ?? mind.FillTo) is { } building && Able(cold)
                && Ai.AiSystem.Holds(table.Next[i], cold) && AtTownWork(table.Next[i], world.Buildings.Find(building.Key)?.Position ?? building.Position)) TownWork(world, building);
    }

    /// <summary>
    /// The workers of town machines at their building this tick (serial, before the players' operators): the AI's workers that stand at their building doing row 87 operate it (a producer
    /// only: a research bench counts for the player's research alone, and a crafting bench with no queue refuses an operator, <c>dontNeedWorkRightNow</c>, so both are pretence), those
    /// doing row 92 empty its output into their inventory.
    /// </summary>
    void TownWorkers(World world, float dt)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Ai: { WorkOperate: not null } or { CollectFrom: not null } or { FillTo: not null } } cold) continue;
            ref var n = ref table.Next[i];
            var mind = cold.Ai!;
            var building = mind.WorkOperate ?? mind.CollectFrom ?? mind.FillTo!;
            if (!Able(cold) || !Ai.AiSystem.Holds(n, cold) || mind.Behaviour != Ai.AiBehaviour.Work || !AtTownWork(n, world.Buildings.Find(building.Key)?.Position ?? building.Position)) continue;
            if ((world.Machines.Find(TownWorkId(building)) ?? TownWork(world, building)) is not { } m || !Live(world, m)) continue;
            if (world.Buildings.Find(m.Site) is not { Complete: true, Destroyed: false } site
                || site.Record != building.BuildingId || site.Owner != cold.Faction && !(cold.SlaveState == 1 && cold.SlaveOwner == site.Owner)) continue;
            if (mind.FillTo is not null) FillInputs(world, cold, m);
            else if (mind.CollectFrom is not null) TakeOutput(world, cold, m);
            else if (mind.WorkOperate is not null) TownOperate(world, i, cold, m, dt);
        }
    }

    bool AtTownWork(in CharacterHot n, Vector3 position)
    {
        float dx = n.Position.X - position.X, dz = n.Position.Z - position.Z;
        return dx * dx + dz * dz <= Ai.AiSystem.WorkReach * Ai.AiSystem.WorkReach * 1.5f * 1.5f && (CharacterTask)n.Task is CharacterTask.Idle or CharacterTask.Rest;
    }

    /// <summary>One worker's frame at a town producer: <see cref="ProduceStep"/> at the worker's skill, and the operating XP (the same lines as a player's operator).</summary>
    void TownOperate(World world, int slot, CharacterCold cold, Machine m, float dt)
    {
        if (!m.Data.IsProducer || m.Data.IsResearch || m.Data.IsCrafting || m.Farm is not null || m.Output is null) return;
        if (m.Data.IsAutomatic || m.Operators >= m.Data.MaxOperators || m.Broken) return;
        m.Operators++;
        var stat = (StatsEnumerated)m.Data.Stat;
        float skill = m.Data.Stat > 0 ? Effective(cold, stat) : -1;
        float s = skill < 0 ? 1 : Lerp(0.5f, 1.4f, skill / 100);
        cold.Operating = ProduceStep(world, m, s, dt);
        if (m.Data.Stat > 0) cold.UseSkill(stat, world.Tick);
        if (m.Data.Stat > 0 && cold.Stats is { } stats && cold.Race is { } race) xp.Continuous(stats, race, stat, dt, OperateXp(m.Data.Stat));
    }

    /// <summary>Room for one freshly produced unit in the character's main grid or worn backpack.</summary>
    internal static bool RoomForOutput(Inventory inventory, ItemInstance unit)
    {
        static bool Room(Inventory grid, ItemInstance item, string section)
        {
            int limit = grid.LimitOf(item);
            return limit > 1 && grid.Items.Any(other => other.Section is ("main" or "backpack_content") && other.Record == item.Record && other.Quality == item.Quality
                && other.StolenFrom == item.StolenFrom && other.Death == item.Death && other.Unique == item.Unique
                && other.Contents is null && other.Buyback is null && other.Quantity < limit)
                || grid.FindCell(section, item.Width, item.Height, out _, out _);
        }
        return Room(inventory, unit, "main") || inventory.Backpack?.Contents is { } bag && Room(bag, unit, "backpack_content");
    }

    /// <summary>
    /// <c>Task_EmptyMachine</c> (rows 92 and 284, <c>FUN_14033cd10</c>, <b>Verified</b>) at a machine with an output: the output's items go into the character's inventory (the worn backpack
    /// when the main grid is full) while there is room, a unit at a time; a producer whose status was 1 (output full) then goes back to 2.
    /// </summary>
    void TakeOutput(World world, CharacterCold cold, Machine m)
    {
        if (cold.Inventory is not { } inv || m.Output is not { Count: > 0 } output) return;
        bool moved = false;
        while (output.Count > 0 && items.Create(output.Recipe.Item, 1) is { } unit && inv.HasRoomFor(unit) && inv.Add(unit))
        {
            output.Count--;
            moved = true;
        }
        if (!moved) return;
        if (m.Status == 1) m.Status = 2;
        world.Machines.Touch();
    }
}
