using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>An NPC walking up to a player character to talk (the original's task 102 <c>Task_TalktoPlayer</c>), and the game hours it gives up at.</summary>
    sealed record Approach(CharacterId Npc, CharacterId Target, double GiveUpHours);

    readonly List<Approach> approaches = [];

    /// <summary>Game hours an approach may take before the NPC gives up with event 60 (<c>Task_TalktoPlayer</c>: 25 game minutes, <b>Observed</b>).</summary>
    const double ApproachGiveUpHours = 25.0 / 60;
    /// <summary>EV_GIVE_UP_CHASE.</summary>
    const int GiveUpChase = 60;

    /// <summary>
    /// <c>DA_TALK_TO_LEADER</c> (2, <c>FUN_140680560</c>): for a player-faction target, the speaker squad's leader gets task 102 on it with event 1, so it walks up and opens a
    /// conversation. Nothing when the leader is already in the open player conversation (its line would send it again); a bubble speaker that leads still walks up.
    /// </summary>
    void TalkToLeader(World world, Conversation c, CharacterCold mc, CharacterId target, CharacterCold tc)
    {
        if (!world.IsPlayerFaction(tc.Faction) && !tc.IsPlayer || mc.SquadId < 0 || world.Squads.Find(mc.SquadId) is not { Leader: { IsNone: false } leader }) return;
        if (HoldsNpc(leader) || approaches.Any(a => a.Npc == leader)) return;
        if (!world.Characters.TryResolveNext(leader, out int l) || !Awake(world.Characters, l)) return;
        approaches.Add(new Approach(leader, target, world.GameHours + ApproachGiveUpHours));
        Hold(world, l);
    }

    /// <summary>
    /// The NPC approaches (serial): it walks to the target and, in talk range (<see cref="InTalkRange"/>) while no conversation is open, fires event 1 at it (the player's
    /// conversation window opens with it); it gives up after 25 game minutes with event 60 at the target; a target or NPC gone or down ends it.
    /// </summary>
    void StepApproaches(World world)
    {
        var table = world.Characters;
        for (int k = 0; k < approaches.Count; k++)
        {
            var a = approaches[k];
            var state = InteractionFor(world.Players.ControlOf(a.Target)?.Player ?? PlayerId.Local);
            int n = -1, t = -1;
            bool done = !table.TryResolveNext(a.Npc, out n) || !Awake(table, n) || !table.TryResolveNext(a.Target, out t) || !Awake(table, t);
            if (!done && state.Open is null && NpcAvailable(world, a.Npc, state) && InTalkRange(table, n, t))
            {
                MovementSystem.Stop(table, n);
                approaches.RemoveAt(k--);
                Start(world, a.Npc, a.Target, DialogueEvents.PlayerTalkToMe, quiet: true);
                if (state.Open?.Npc != a.Npc) Release(world, a.Npc);
                continue;
            }
            if (!done && world.GameHours >= a.GiveUpHours)
            {
                world.Defer(new DialogueEvent(a.Npc, a.Target, GiveUpChase) { Tick = world.Tick + 1 });
                done = true;
            }
            if (done)
            {
                approaches.RemoveAt(k--);
                Release(world, a.Npc);
                continue;
            }
            // Walking: towards where the target stands, again when it has moved on.
            var to = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
            ref var nh = ref table.Next[n];
            table.Cold(n)!.Engaged = true;
            if (nh.Task != (byte)CharacterTask.GoTo || Vector2.Distance(nh.Goal, to) > TalkReach) MovementSystem.GoTo(table, n, to, SpeedMode.Free);
        }
    }

    void HashApproaches(ref StateHasher hasher)
    {
        foreach (var a in approaches) { hasher.Add(a.Npc); hasher.Add(a.Target); hasher.Add(a.GiveUpHours); }
    }
}
