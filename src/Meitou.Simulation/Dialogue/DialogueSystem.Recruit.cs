using System.Numerics;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>
    /// DA_JOIN_SQUAD_WITH_EDIT (3) / DA_JOIN_SQUAD_FAST (18) with the player character as the target (docs/game/dialogue.md "Recruiting"): the speaker leaves its squad and joins the
    /// player's active squad (<see cref="PlayerState.Squad"/>, else the selection's, else the talker's) as a player character; when that squad already holds CONSTANTS <c>max squad size</c>
    /// members, a new squad of the "blank squad" template takes it. At the faction's head cap (<c>max faction size</c>) the conversation ends with "It looks like your squad is a
    /// little full." and the line's remaining effects are skipped (false). DA 3 then opens the character editor on the recruit (engine: the conversation ends for it).
    /// </summary>
    bool Recruit(World world, Conversation c, CharacterId me, bool withEditor)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(me, out int slot) || table.Cold(slot) is not { IsPlayer: false } || Cold(world, c.Player) is not { IsPlayer: true } tc) return true;
        if (PlayerHeads(world, c.Faction) >= data.Bodies.Constants.MaxFactionSize)
        {
            Post(world, c, "It looks like your squad is a little full.");
            c.Ended = true;
            return false;
        }
        if (!JoinPlayer(world, c, slot, tc)) return true;
        if (withEditor)
        {
            c.Ended = true;
            var session = Editing.CharacterEditing.OpenSession(world, c.Actor, me, me, Editing.EditKind.Recruit);
            if (session != Guid.Empty) InteractionOf(c).Request = new DialogueRequest(++requestId, DialogueRequestKind.RecruitEditor, me, me)
                { EditSession = session };
        }
        return true;
    }

    static int PlayerHeads(World world, int faction)
    {
        var table = world.Characters;
        int heads = 0;
        for (int i = 0; i < table.Next.Length; i++)
            if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, DiedTick: < 0 } member && member.Faction == faction) heads++;
        return heads;
    }

    /// <summary>The character at <paramref name="slot"/> leaves its squad and joins the player's active squad (or a new blank squad when it is full) as a player character.</summary>
    bool JoinPlayer(World world, Conversation context, int slot, CharacterCold talker)
    {
        var table = world.Characters;
        var me = table.IdOf(slot);
        var mc = table.Cold(slot)!;
        if (context.Actor is not null && world.Players.KnownControl(mc.Entity) is not null) return false;
        var limits = data.Bodies.Constants;
        if (!CanJoin(world, context, talker)) return false;
        var owner = Player(world, context);
        if (JoiningSquad(world, context, talker) is not { } squad) return false;
        if (squad.Members.Count >= limits.MaxSquadSize)
        {
            squad = SquadManagement.NewSquad(world, data.Db, table.Next[slot].Position, data.Factions[context.Faction].Name is { Length: > 0 } f ? f : "Nameless", context.Faction);
            if (owner is not null) owner.Squad = squad.Id;
        }

        Leave(world, me, mc);
        mc.Faction = context.Faction;
        mc.RoadPreference = data.RoadPreferenceOf(mc.Faction);
        mc.SquadId = squad.Id;
        mc.Role = (int)SquadRole.Squad1;
        mc.IsPlayer = true;
        mc.NpcClass = FundamentalType.Adventurer;   // every player-faction character's class
        mc.Orders = Orders.PlayerDefaults;
        mc.Ai = null;
        mc.Engaged = false;
        mc.Fleeing = false;
        mc.Pending = null;
        mc.Jobs.Clear();   // an NPC's list, if any, does not carry over
        if (mc.Inventory is { } kit) foreach (var piece in kit.All()) piece.Uniform = "";   // a recruit's clothes are no longer a faction's uniform (economy.md "Unknowns", Observed)
        mc.Save = null;   // written as a new member of the player's platoon; the save's old slot of it is removed
        table.Next[slot].Mode = SpeedMode.Free;   // the original's speed mode 2
        MovementSystem.Stop(table, slot);
        squad.Members.Add(me);
        if (squad.Leader.IsNone) squad.Leader = me;
        if (context.Actor is { } actor) world.Players.AssignInitial(actor, me);
        return true;
    }

    bool CanJoin(World world, Conversation context, CharacterCold talker)
        => RecruitSpace(world, context, talker) > 0;

    Squad? JoiningSquad(World world, Conversation context, CharacterCold talker)
    {
        var player = Player(world, context);
        var selected = player?.Selection.Select(id => Cold(world, id)).FirstOrDefault(s => s is { IsPlayer: true } && s.Faction == context.Faction);
        Squad? Own(int id) => world.Squads.Find(id) is { } found && found.Faction == context.Faction && !found.Dead &&
            (!world.Players.RequiresExplicitControl || found.PlayerManaged) ? found : null;
        return Own(player?.Squad ?? -1) ?? Own(selected?.SquadId ?? -1) ?? Own(talker.SquadId);
    }

    int RecruitSpace(World world, Conversation context, CharacterCold talker)
    {
        if (context.Faction < 0 || !CanManage(world, context) || JoiningSquad(world, context, talker) is not { } chosen) return 0;
        var squads = SquadManagement.PlayerSquads(world, context.Faction);
        var limits = data.Bodies.Constants;
        long capacity = Math.Max(0, limits.MaxSquadSize - chosen.Members.Count) + (long)Math.Max(0, limits.MaxSquads - squads.Count) * limits.MaxSquadSize;
        return (int)Math.Max(0, Math.Min(capacity, limits.MaxFactionSize - PlayerHeads(world, context.Faction)));
    }

    static void Leave(World world, CharacterId me, CharacterCold mc)
    {
        if (world.Squads.Find(mc.SquadId) is not { } old) return;
        old.Members.Remove(me);
        if (old.Leader == me) old.Leader = SquadRegistry.Successor(old.Members, world.Characters);
    }

    /// <summary>
    /// <c>DA_GIVE_TARGET_MY_SLAVES</c> (24, <c>FUN_140680560</c>): the slaves on the speaker's platoon's list are let out of their cages and join the target's squad. Engine reading of
    /// the list: the members of the speaker's squad in slave state (Meitou makes no slave squads yet, so it is often empty). They join the player as recruits do, keeping their
    /// slave state, now owned by the target's faction; a non-player target's squad takes them into its own.
    /// </summary>
    void GiveSlaves(World world, Conversation c, CharacterId me, CharacterCold mc, CharacterCold tc)
    {
        var table = world.Characters;
        if (mc.SquadId < 0 || world.Squads.Find(mc.SquadId) is not { } squad) return;
        foreach (var id in squad.Members.ToArray())
        {
            if (id == me || !table.TryResolveNext(id, out int s) || table.Cold(s) is not { SlaveState: not 0, Medical.Dead: false } slave) continue;
            byte state = slave.SlaveState;
            if (slave.IsImprisoned) world.Fixtures.Release(world, id);   // it clears the slave state; restored below
            if (tc.IsPlayer || world.IsPlayerFaction(tc.Faction))
            {
                if (PlayerHeads(world, c.Faction) >= data.Bodies.Constants.MaxFactionSize || !JoinPlayer(world, c, s, tc)) break;
            }
            else if (world.Squads.Find(tc.SquadId) is { } theirs)
            {
                Leave(world, id, slave);
                slave.SquadId = theirs.Id;
                theirs.Members.Add(id);
            }
            (slave.SlaveState, slave.SlaveOwner) = (state, tc.Faction);
        }
    }
}
