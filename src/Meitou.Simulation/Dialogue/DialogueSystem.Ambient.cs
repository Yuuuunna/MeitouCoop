using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

/// <summary>A line on show over a character's head (docs/game/dialogue.md "Ambient conversations"): <see cref="Large"/> for the events that use the large font.</summary>
public sealed record SpeechBubble(CharacterId Speaker, string Text, bool Large);

public sealed partial class DialogueSystem
{
    /// <summary>A speech-bubble conversation under way (docs/game/dialogue.md "Ambient conversations"): the line said next, by whom, and when.</summary>
    sealed record Bubble(Conversation C, DialogueNode Line, long DueTick, CharacterId Speaker);

    readonly List<Bubble> bubbles = [];
    // The lines on show: the text and the tick it goes (display only: follows from the bubbles, so not hashed).
    readonly SortedDictionary<CharacterId, (string Text, long Until, bool Large)> shown = new(Comparer<CharacterId>.Create(CompareIds));
    // When a squad last reacted to seeing a character (the cooldown of SeeOthers).
    readonly SortedDictionary<(int Squad, CharacterId Target), double> ambientAt = new(Comparer<(int, CharacterId)>.Create((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : CompareIds(a.Item2, b.Item2)));

    /// <summary>The event of a crowd trigger's lines (EV_CROWD_TRIGGERED), and of a contract's end talk (EV_CONTRACT_JOB_ENDED).</summary>
    const int CrowdTriggered = 74;
    /// <summary>EV_UNLOCK_MY_CAGE_OR_SHACKLES: a freed prisoner's thanks, which also makes its squad remember the liberator (<see cref="Freed"/>).</summary>
    const int UnlockMyCageOrShackles = 13;
    public const int ContractJobEnded = 30;
    /// <summary>The AI-to-AI talking range (DC 16 TARGET_IN_TALKING_RANGE, 260 units): how near another squad's NPC must be to be greeted (engine choice).</summary>
    const float TalkingRange = 260;

    /// <summary>The events whose bubbles use the large font (<c>FUN_140725f30</c>, <b>Observed</b>); the others use the medium one.</summary>
    static bool IsLarge(int ev) => ev is 2 or 11 or 17 or 26 or 31 or 34 or 36 or 40 or 45 or 74;

    public IReadOnlyList<SpeechBubble> PublishSpeech() => [.. shown.Select(s => new SpeechBubble(s.Key, s.Value.Text, s.Value.Large))];

    /// <summary>
    /// A campaign's <c>dialog announcement</c> (docs/game/factions-squads-towns.md "Campaigns", announcement): <paramref name="dialogueId"/> fired as event 2 by
    /// <paramref name="npc"/> with no target. Its lines are said one after another as speech bubbles. Their effects run as said (an <c>interrupt</c> makes the next talk
    /// with the speaker that dialogue). An animal never announces (in the original only the leader's AI package does). Returns whether it started.
    /// </summary>
    public bool Announce(World world, CharacterId npc, string dialogueId) =>
        world.Characters.TryResolveNext(npc, out int slot) && world.Characters.Cold(slot) is { } c && !SquadRegistry.IsAnimal(c)
        && dialogue.Node(dialogueId) is { IsRoot: true } root && StartBubbles(world, npc, CharacterId.None, root, DialogueEvents.Announcement, now: false);

    public bool Announce(World world, CharacterId npc, CharacterId target, string dialogueId) =>
        world.Characters.TryResolveNext(npc, out int slot) && world.Characters.Cold(slot) is { } c && !SquadRegistry.IsAnimal(c)
        && world.Characters.TryResolveNext(target, out _)
        && dialogue.Node(dialogueId) is { IsRoot: true } root && StartBubbles(world, npc, target, root, DialogueEvents.Announcement, now: false);

    /// <summary>
    /// Starts <paramref name="root"/> as a bubble conversation of <paramref name="npc"/> with <paramref name="whom"/> (<c>FUN_140683f90</c>): the best of its lines (speakers resolved as in
    /// any choice) is said, the chain after it. <paramref name="now"/>: said this tick, else on the next bubble step. Returns whether a line was found.
    /// </summary>
    bool StartBubbles(World world, CharacterId npc, CharacterId whom, DialogueNode root, int ev, bool now = true)
    {
        if (!world.Characters.TryResolveNext(npc, out int n) || !Awake(world.Characters, n) || BubbleOf(npc)) return false;
        var c = Context(world, new Conversation { Npc = npc, Player = whom, Event = ev, Root = root });
        if (Pick(world, c, root.Children) is not { } first || Busy(first.Speaker, c)) return false;
        bubbles.Add(new Bubble(c, first.Line, world.Tick, first.Speaker));
        if (now) StepBubbles(world);
        return true;
    }

    /// <summary>
    /// <paramref name="ev"/> fired at <paramref name="npc"/> with <paramref name="whom"/> as its target outside a player conversation (<c>FUN_140684990</c>): a line is chosen from the
    /// NPC's list for the event and said as a speech bubble, the rest of its chain after it. Returns whether a line was said.
    /// </summary>
    bool Fire(World world, CharacterId npc, CharacterId whom, int ev)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(npc, out int n) || !Awake(table, n) || BubbleOf(npc)) return false;
        var c = Context(world, new Conversation { Npc = npc, Player = whom, Event = ev });
        if (Pick(world, c, SetOf(world, n).For(ev)) is not { } picked || picked.Speaker.IsNone || picked.Speaker == whom || Busy(picked.Speaker, c)) return false;
        c.Root = picked.Root;
        bubbles.Add(new Bubble(c, picked.Line, world.Tick, picked.Speaker));
        StepBubbles(world);
        return true;
    }

    bool BubbleOf(CharacterId who) => shown.ContainsKey(who) || bubbles.Any(b => b.Speaker == who || b.C.Npc == who);

    /// <summary>Whether <paramref name="who"/> is saying a line of another conversation, or is in the open player conversation.</summary>
    bool Busy(CharacterId who, Conversation c) =>
        interactions.Values.Any(s => s.Open is { } o && (o.Npc == who || o.Player == who)) || bubbles.Any(b => b.C != c && (b.Speaker == who || b.C.Npc == who));

    /// <summary>
    /// Whom a loaded NPC reacts to by sight (the perception that fires events 3, 66 and 67, <c>FUN_140859680</c>): the nearest player character it sees by the sight test every
    /// other sighting uses (<see cref="SightMap.Sees"/>: its race's range, daylight, light, places and view cone; a sneaker with its stealth, without the spot meter: engine choice),
    /// or that a squad mate sees within twice its range R (the shared sightings of <c>FUN_140857b10</c>), 66 hostile, 67 ally, else 3; else the nearest awake NPC of another squad within <see cref="TalkingRange"/> (event 3, as the original fires it for a seen character outside
    /// the player's faction), only within <see cref="DialogueSettings.AmbientRange"/> of a player character, where it can be heard. The nearest to a player look first.
    /// </summary>
    void SeeOthers(World world)
    {
        var table = world.Characters;
        var players = new List<int>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, Medical.Dead: false } player && data.Relations.Contains(player.Faction)) players.Add(i);
        if (players.Count == 0) return;
        float audible2 = Settings.AmbientRange * Settings.AmbientRange;
        // The NPCs that can look or be greeted: loaded, awake, free, within their sight reach of a player character; by that distance, then slot.
        var near = new List<(int Slot, float D2)>();
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Next[i].Task == (byte)CharacterTask.Rest || table.Cold(i) is not { IsPlayer: false, SquadId: >= 0, Engaged: false, InCombat: false, Fleeing: false, IsImprisoned: false } nc) continue;
            if (nc.Fighter?.IsAnimal == true || nc.Appearance?.Animal is not null || !data.Relations.Contains(nc.Faction) || !Awake(table, i)) continue;
            float reach = MathF.Max(world.Sight.MaxRange(i, AggroSystem.RangeOf(nc, AggroSystem.OutdoorSight)), Settings.AmbientRange);
            float nearest = float.MaxValue;
            foreach (int p in players) nearest = MathF.Min(nearest, Vector3.DistanceSquared(table.Next[i].Position, table.Next[p].Position));
            if (nearest <= reach * reach) near.Add((i, nearest));
        }
        near.Sort((a, b) => a.D2 != b.D2 ? a.D2.CompareTo(b.D2) : a.Slot.CompareTo(b.Slot));
        // Who sees which player character, and which squads have a member seeing it (the original's squad-shared sightings, GroupSense: a mate's counts within 2 R).
        var sees = new HashSet<(int Npc, int Player)>();
        var squadSees = new HashSet<(int Squad, int Player)>();
        foreach (var (i, _) in near)
        {
            var nc = table.Cold(i)!;
            float outdoor = AggroSystem.RangeOf(nc, AggroSystem.OutdoorSight);
            foreach (int p in players)
            {
                var pc = table.Cold(p)!;
                float stealth = (pc.Orders & CharacterOrders.Sneak) != 0 ? Orders.Stealth(pc) : -1;
                if (world.Sight.Sees(i, p, table.Next[i].Position, table.Next[i].Yaw, table.Next[p].Position, outdoor, stealth) <= 0) continue;
                sees.Add((i, p));
                squadSees.Add((nc.SquadId, p));
            }
        }
        int said = 0;
        foreach (var (i, nearest) in near)
        {
            if (said >= Settings.AmbientPerScan) break;
            var nc = table.Cold(i)!;
            float shared = 2 * AggroSystem.RangeOf(nc, AggroSystem.OutdoorSight);
            int best = -1;
            float best2 = float.MaxValue;
            foreach (int p in players)
            {
                float d2 = Vector3.DistanceSquared(table.Next[i].Position, table.Next[p].Position);
                if (d2 < best2 && (sees.Contains((i, p)) || d2 <= shared * shared && squadSees.Contains((nc.SquadId, p)))) (best, best2) = (p, d2);
            }
            // The squad's cooldown starts only once a line is said: the original asks again on every perception update and rolls `chance temporary` afresh each time
            // (the Red Sabres' attack root is 20%), so a failed look is tried again on the next scan. A player character it has nothing to say to leaves the turn to the NPCs.
            if (best >= 0 && !Recent(world, nc.SquadId, table.IdOf(best))
                && TrySay(world, i, nc.SquadId, best, data.Relations.IsHostile(nc.Faction, table.Cold(best)!.Faction) ? 66 : data.Relations.IsAlly(nc.Faction, table.Cold(best)!.Faction) ? 67 : 3))
            {
                said++;
                continue;
            }
            if (nearest > audible2) continue;
            (best, best2) = (-1, TalkingRange * TalkingRange);
            foreach (var (o, od2) in near)
            {
                if (od2 > audible2 || table.Cold(o)!.SquadId == nc.SquadId) continue;
                float d2 = Vector3.DistanceSquared(table.Next[i].Position, table.Next[o].Position);
                if (d2 <= best2 && !Recent(world, nc.SquadId, table.IdOf(o))) (best, best2) = (o, d2);
            }
            if (best >= 0 && TrySay(world, i, nc.SquadId, best, 3)) said++;
        }
    }

    /// <summary>Fires <paramref name="ev"/> at the character in <paramref name="target"/>; when a line is said, the squad does not react to that character again for the cooldown.</summary>
    bool TrySay(World world, int slot, int squad, int target, int ev)
    {
        var whom = world.Characters.IdOf(target);
        if (SetOf(world, slot).For(ev).Count == 0 || !Fire(world, world.Characters.IdOf(slot), whom, ev)) return false;
        ambientAt[(squad, whom)] = world.GameHours;
        return true;
    }

    // The characters that were down at the last look (the get-up events); hashed.
    readonly SortedSet<CharacterId> down = new(Comparer<CharacterId>.Create(CompareIds));

    /// <summary>EV_GET_UP_PEACE, EV_GET_UP_FIGHT, EV_GET_UP_UNNECCESSARY_FIGHT.</summary>
    const int GetUpPeace = 23, GetUpFight = 24, GetUpUnnecessaryFight = 25;

    /// <summary>
    /// Getting up (<c>Task_GetUp</c>, <c>FUN_14033ca10</c> / <c>FUN_1408c6780</c>, docs/game/ragdolls.md): a character that was down at the last look and is awake now fires, with no target,
    /// 23 when it senses no enemy or neutral (<see cref="BodySystem.Senses"/>); else, stood up on the player's order with no conscious ally sensed, 25; else 24.
    /// </summary>
    void SeeGettingUp(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Medical: { } medical } c) continue;
            var id = table.IdOf(i);
            if (medical.Dead) { down.Remove(id); continue; }
            if (medical.Incapacitated) { down.Add(id); continue; }
            if (!down.Remove(id)) continue;
            bool ordered = c.OrderedUp;
            c.OrderedUp = false;
            int ev;
            if (world.SystemOf<Combat.CombatSystem>() is { } combat)
            {
                var (enemies, neutrals, allies) = BodySystem.Senses(world, combat, i, c);
                ev = enemies + neutrals == 0 ? GetUpPeace : ordered && allies == 0 ? GetUpUnnecessaryFight : GetUpFight;
            }
            else ev = c.InCombat || EnemyNear(world, i, c) ? GetUpFight : GetUpPeace;
            Fire(world, id, CharacterId.None, ev);
        }
        down.RemoveWhere(id => !table.TryResolveNext(id, out _));
    }

    bool EnemyNear(World world, int slot, CharacterCold c)
    {
        var table = world.Characters;
        if (!data.Relations.Contains(c.Faction)) return false;
        float r = AggroSystem.RangeOf(c, AggroSystem.OutdoorSight);
        for (int i = 0; i < table.HighWater; i++)
        {
            if (i == slot || !table.Next[i].Alive || table.Cold(i) is not { Medical.Incapacitated: false } o || !data.Relations.Contains(o.Faction)) continue;
            if (data.Relations.IsHostile(c.Faction, o.Faction) && Vector3.DistanceSquared(table.Next[i].Position, table.Next[slot].Position) <= r * r) return true;
        }
        return false;
    }

    bool Recent(World world, int squad, CharacterId target) => ambientAt.TryGetValue((squad, target), out double at) && world.GameHours - at < Settings.AmbientRepeatHours;

    /// <summary>Whether talking to <paramref name="npc"/> opens a pending <c>interrupt</c> dialogue (a campaign's negotiation).</summary>
    public bool HasInterrupt(CharacterId npc) => interrupts.ContainsKey(npc);

    /// <summary>
    /// The line said after <paramref name="line"/> (<c>FUN_140684720</c>): the best of its children with their speakers resolved (the owner, its target, an interjector), as in a choice;
    /// none when the root is not a monologue (a non-monologue root in bubble mode ends after its first line, <c>FUN_140685090</c>) or the speaker is busy elsewhere.
    /// </summary>
    Picked? NextBubble(World world, Conversation c, DialogueNode line)
    {
        if (line.Children.Count == 0 || c.Root is { Monologue: false }) return null;
        return Pick(world, c, line.Children) is { } next && !Busy(next.Speaker, c) ? next : null;
    }

    /// <summary>Says the bubbles that are due (serial): the text, its effects, then the next line after 2 + 0.12 × the text's length seconds, or DA 12's own wait when longer.</summary>
    void StepBubbles(World world)
    {
        for (int k = 0; k < bubbles.Count; k++)
        {
            var (c, line, due, speaker) = bubbles[k];
            if (!Authorized(world, c)) { bubbles.RemoveAt(k--); continue; }
            if (world.Tick < due) continue;
            if (!world.Characters.TryResolveNext(speaker, out int n) || !Awake(world.Characters, n))
            {
                bubbles.RemoveAt(k--);
                continue;
            }
            MarkUsed(world, c, line);
            string text = Expand(world, c, line, PickText(world, speaker, line), speaker, speaker == c.Player ? c.Npc : c.Player);
            RunEffects(world, c, line, c.Npc);
            float seconds = 2 + 0.12f * text.Length;
            if (text.Length > 0)
            {
                shown[speaker] = (text, world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds), IsLarge(c.Event));
                LogLine(world, c, speaker, NameOf(world, speaker), text, world.Characters.Cold(n) is { IsPlayer: true });
            }
            foreach (var e in line.Effects) if (e.Action == DialogueActions.BubbleWait) seconds = MathF.Max(seconds, e.Value);
            if (c.Ended || NextBubble(world, c, line) is not { } next)
            {
                EndBubbles(world, c);
                bubbles.RemoveAt(k--);
            }
            else bubbles[k] = new Bubble(c, next.Line, world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds), next.Speaker);
        }
    }

    /// <summary>
    /// A line's <c>crowd trigger</c> (<c>FUN_140680560</c>, unless the line is itself of event 74): the awake members of the conversation owner's squad other than itself and the
    /// speaker, not animals, start that DIALOGUE as event 74 at the target. Engine readings: the owner is left out (it is talking), and each member joins with the trigger's val0
    /// percent (the data's values are 100 but for two lines).
    /// </summary>
    void CrowdTrigger(World world, Conversation c, DialogueNode line, CharacterId speaker)
    {
        if (c.Event == CrowdTriggered || line.CrowdTrigger is not { } id || dialogue.Node(id) is not { IsRoot: true } root) return;
        var table = world.Characters;
        foreach (var m in SquadOf(world, c.Npc))
        {
            if (m == c.Npc || m == speaker || !table.TryResolveNext(m, out int s) || !Awake(table, s) || table.Cold(s) is not { IsPlayer: false } mc) continue;
            if (mc.Fighter?.IsAnimal == true || mc.Appearance?.Animal is not null || mc.Engaged) continue;
            if (line.CrowdChance < 100 && Rng.Float(NextRoll(world, m)) * 100 >= line.CrowdChance) continue;
            StartBubbles(world, m, c.Player, root, CrowdTriggered, now: false);
        }
    }

    /// <summary>A bubble conversation's end (<c>FUN_140674830</c>): the event's counter and time; only an event-1 conversation leaves the speaker's squad remembering the target.</summary>
    void EndBubbles(World world, Conversation c)
    {
        var key = (c.Npc, c.Event);
        var (count, _) = counters.GetValueOrDefault(key);
        counters[key] = (count + 1, world.GameHours);
        if (c.Event == DialogueEvents.PlayerTalkToMe && !c.Player.IsNone && world.Characters.TryResolveNext(c.Npc, out int n) && world.Characters.Cold(n) is { SquadId: >= 0 } nc)
            Memory.Remember(world, nc.SquadId, c.Player, MemoryTag.None);
    }

    /// <summary>Drops the lines whose time is up or whose speaker is gone (serial, each tick).</summary>
    void ExpireSpeech(World world)
    {
        if (shown.Count == 0) return;
        foreach (var who in shown.Where(s => world.Tick >= s.Value.Until || !world.Characters.TryResolveNext(s.Key, out _)).Select(s => s.Key).ToArray()) shown.Remove(who);
    }
}
