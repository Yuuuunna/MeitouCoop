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

    void PrepareTownWorkers(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { Ai: { Behaviour: Ai.AiBehaviour.Work } mind } cold
                && (mind.WorkOperate ?? mind.CollectFrom) is { } building && Able(cold)
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
            if (!table.Next[i].Alive || table.Cold(i) is not { Ai: { WorkOperate: not null } or { CollectFrom: not null } } cold) continue;
            ref var n = ref table.Next[i];
            var mind = cold.Ai!;
            var building = mind.WorkOperate ?? mind.CollectFrom!;
            if (!Able(cold) || !Ai.AiSystem.Holds(n, cold) || mind.Behaviour != Ai.AiBehaviour.Work || !AtTownWork(n, world.Buildings.Find(building.Key)?.Position ?? building.Position)) continue;
            if ((world.Machines.Find(TownWorkId(building)) ?? TownWork(world, building)) is not { } m || !Live(world, m)) continue;
            if (world.Buildings.Find(m.Site) is not { Complete: true, Destroyed: false } site
                || site.Record != building.BuildingId || site.Owner != cold.Faction && !(cold.SlaveState == 1 && cold.SlaveOwner == site.Owner)) continue;
            if (mind.CollectFrom is not null) TakeOutput(world, cold, m);
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
        while (output.Count > 0 && items.Create(output.Recipe.Item, 1) is { } unit && inv.Add(unit))
        {
            output.Count--;
            moved = true;
        }
        if (!moved) return;
        if (m.Status == 1) m.Status = 2;
        world.Machines.Touch();
    }
}
