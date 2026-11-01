using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Carrying;

// The foraging job and the Rescue Allies option (docs/game/ai-tasks.md "Perma-jobs" and "Squad AI options").
public sealed partial class CarrySystem
{
    /// <summary>
    /// The foraging job (238 "Foraging animals", finder <c>FUN_1408ce3a0</c>), every <see cref="CarrySettings.AutoEveryTicks"/> ticks (serial, slot order): a player character with the
    /// job, free for it (<see cref="PermaJobs.FreeFor"/>, which also wants the jobs switch on), able to carry and with no fight in its squad, loots (<see cref="CharacterTask.Loot"/>: the
    /// walk, the take-all and the butchering) the nearest downed animal within its sight that is not carried, not looted by another and has something in its inventory. The finder's
    /// faction test (a character vfunc, or the animal's faction flag +0x1d0) is <b>Unknown</b>; Meitou leaves the forager's own faction's animals alone (engine choice).
    /// </summary>
    void Forage(World world, CharacterTable table)
    {
        var next = table.Next;
        HashSet<int>? taken = null;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive || table.Cold(i) is not { IsPlayer: true } cold || cold.Jobs.Count == 0) continue;
            if (world.Players.RequiresExplicitControl && world.Players.ControlOf(table.IdOf(i)) is null) continue;
            if (!PermaJobs.FreeFor(cold, (CharacterTask)next[i].Task, PermaJobs.IndexOf(cold, TaskType.LOOT_ANIMALS_JOB)) || cold.Fleeing || !CanCarry(cold) || cold.Inventory is null) continue;
            if (cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad && Fighting(table, squad)) continue;
            taken ??= Looting(table);
            float range = AggroSystem.RangeOf(cold, Settings.Sight), best2 = range * range;
            int best = -1;
            var at = new Vector2(next[i].Position.X, next[i].Position.Z);
            for (int s = 0; s < table.HighWater; s++)
            {
                if (s == i || !next[s].Alive || taken.Contains(s) || table.Cold(s) is not { } animal || animal.Faction == cold.Faction) continue;
                if (!(animal.Fighter?.IsAnimal ?? animal.Appearance?.Animal is not null) || !CanBeLooted(animal) || !animal.CarriedBy.IsNone || !animal.Inventory!.All().Any()) continue;
                float d2 = Vector2.DistanceSquared(at, new Vector2(next[s].Position.X, next[s].Position.Z));
                if (d2 <= best2 && (best < 0 || d2 < best2)) { best = s; best2 = d2; }
            }
            if (best < 0) continue;
            taken.Add(best);
            Begin(table, i, table.IdOf(best), CharacterTask.Loot, cold.JobIndex >= 0 || cold.Implicit is not null ? cold.ResumeTask : (CharacterTask)next[i].Task);
            RememberJob(world, i);
        }
    }

    /// <summary>The bodies being looted now (by slot).</summary>
    static HashSet<int> Looting(CharacterTable table)
    {
        var taken = new HashSet<int>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.Loot && table.TryResolveNext(table.Cold(i)!.CarryTarget, out int busy)) taken.Add(busy);
        return taken;
    }

    /// <summary>
    /// Whom a rescuer's rescue job takes in (the character finder <c>FUN_1408cd2d0</c>): a player character only its own faction, unless <see cref="SquadAi.Rescue"/> is on (the global
    /// +0x112), which drops that test; an NPC its own faction (engine choice). With the option Meitou takes allied factions (<see cref="Friendly"/>), the finder's sensed-list mask
    /// standing for that (its bits are <b>Unknown</b>).
    /// </summary>
    bool Rescues(World world, CharacterCold rescuer, CharacterCold body) =>
        body.Faction == rescuer.Faction || rescuer.IsPlayer && (world.SquadOptions(rescuer) & SquadAi.Rescue) != 0 && Friendly(rescuer, body);
}
