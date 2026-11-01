using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>A chosen line, the root it came from (a root's chosen child), and who says it.</summary>
    readonly record struct Picked(DialogueNode Line, DialogueNode? Root, CharacterId Speaker);

    /// <summary>
    /// Chooses a line from <paramref name="list"/> (<c>FUN_14067a0c0</c>, docs/game/dialogue.md "Choosing a line"): the list is sorted by score; the walk stops at the first line scoring
    /// below the best so far; each node's speaker is resolved and the line check run with that speaker as "me" (the NPC for a player-side line); a passing root descends into its
    /// children; one candidate at the top score is picked at random.
    /// </summary>
    Picked? Pick(World world, Conversation c, IReadOnlyList<DialogueNode> list, int depth = 0)
    {
        if (depth > 8) return null;
        var candidates = new List<Picked>();
        int best = int.MinValue;
        foreach (var node in list)
        {
            if (!node.IsRoot && node.Score < best) break;
            if (node.Interjection)
            {
                int slot = node.Speaker is >= Talker.Interjector1 and <= Talker.Interjector3 ? node.Speaker - Talker.Interjector1 : 0;
                c.Interjectors[slot] = CharacterId.None;
                if (FindInterjector(world, c, node) is not { } interjector) continue;
                c.Interjectors[slot] = interjector;
                if (Pick(world, c, node.Children, depth + 1) is not { } inner) continue;
                Consider(node.Score + inner.Line.Score, inner with { Root = inner.Root ?? c.Root });
                continue;
            }
            if (!SpeakerOf(world, c, node, out var speaker)) continue;
            var me = IsNpcSide(node.Speaker) ? speaker : c.Npc;
            if (!Check(world, c, node, me, c.Player)) continue;
            int score = node.Score + RaceBonus(world, node, c.Player);
            if (node.IsRoot)
            {
                var root = c.Root;
                c.Root = node;
                var child = Pick(world, c, node.Children, depth + 1);
                c.Root = root;
                if (child is not { } p) continue;
                Consider(score + p.Line.Score, p with { Root = node });
            }
            else Consider(score, new Picked(node, c.Root, speaker));
        }
        if (candidates.Count == 0) return null;
        return candidates.Count == 1 ? candidates[0] : candidates[Rng.Int(NextRoll(world, c.Npc), candidates.Count)];

        void Consider(int score, Picked p)
        {
            if (score > best) { best = score; candidates.Clear(); }
            if (score == best) candidates.Add(p);
        }
    }

    /// <summary>+2 when the target's race is in the line's <c>target race</c> (<c>FUN_140671bb0</c>).</summary>
    int RaceBonus(World world, DialogueNode node, CharacterId target) =>
        node.TargetRace.Count > 0 && Cold(world, target)?.Race is { } race && node.TargetRace.Contains(race.StringId) ? 2 : 0;

    /// <summary>Who says <paramref name="node"/> (<c>FUN_140672bf0</c>); false: nobody (the line is skipped).</summary>
    bool SpeakerOf(World world, Conversation c, DialogueNode node, out CharacterId speaker)
    {
        speaker = node.Speaker switch
        {
            Talker.Me or Talker.WholeSquad => c.Npc,
            Talker.Target => c.Player,
            Talker.TargetIfPlayer => Cold(world, c.Player) is { IsPlayer: true } ? c.Player : CharacterId.None,
            Talker.Interjector1 or Talker.Interjector2 or Talker.Interjector3 => c.Interjectors[node.Speaker - Talker.Interjector1],
            Talker.TargetWithRace => Known(world, c.Npc, c.Player).FirstOrDefault(m => Cold(world, m)?.Race is { } r && node.TargetRace.Contains(r.StringId)),
            _ => CharacterId.None,
        };
        return !speaker.IsNone;
    }

    /// <summary>
    /// An interjector for <paramref name="node"/> (<c>FUN_140679bc0</c>): an awake member of the NPC's own squad, not an animal, not already talking or interjecting, passing the node's
    /// check as the speaker; a <c>dumb</c> character only when the node's <c>is character</c> lists it. Weighted by distance (1 within 30, 0 beyond ~450, x0.1 for another race).
    /// </summary>
    CharacterId? FindInterjector(World world, Conversation c, DialogueNode node)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(c.Npc, out int n) || table.Cold(n) is not { } owner) return null;
        var at = table.Next[n].Position;
        var weighted = new List<(CharacterId Id, float Weight)>();
        foreach (var m in SquadOf(world, c.Npc))
        {
            if (m == c.Npc || c.Interjectors.Contains(m) || !table.TryResolveNext(m, out int s) || !Awake(table, s) || table.Cold(s) is not { } mc) continue;
            if (mc.Engaged || mc.Fighter?.IsAnimal == true) continue;
            bool dumb = data.Db.Find(mc.RecordId)?.GetBool("dumb") == true;
            if ((dumb || node.IsCharacter.Count > 0) && !node.IsCharacter.Contains(mc.RecordId)) continue;
            float d2 = Vector3.DistanceSquared(table.Next[s].Position, at);
            float w = Math.Clamp((d2 - 202800f) / (900f - 202800f), 0, 1);
            if (mc.Race?.StringId != owner.Race?.StringId) w *= 0.1f;
            if (w <= 0 || !Check(world, c, node, m, c.Player)) continue;
            weighted.Add((m, w));
        }
        if (weighted.Count == 0) return null;
        float pick = Rng.Float(NextRoll(world, c.Npc)) * weighted.Sum(e => e.Weight);
        foreach (var (id, w) in weighted)
        {
            if (pick < w) return id;
            pick -= w;
        }
        return weighted[^1].Id;
    }

    /// <summary>
    /// The line check (<c>FUN_140678f30</c>, docs/game/dialogue.md "Line check") with <paramref name="me"/> as the speaker and <paramref name="other"/> the target: tag masks, the lock,
    /// <c>chance temporary</c>, the record lists, <c>repetition limit</c>, <c>target is type</c>, <c>chance permanent</c>, then the conditions.
    /// </summary>
    bool Check(World world, Conversation c, DialogueNode node, CharacterId me, CharacterId other)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(me, out int ms) || table.Cold(ms) is not { } mc) return false;
        var tc = Cold(world, other);
        if (node.PersonalityRequired != 0 || node.PersonalityForbidden != 0)
        {
            uint bit = 1u << Personality.Of(world, data.Db, me, mc);
            if (node.PersonalityRequired != 0 && (node.PersonalityRequired & bit) == 0) return false;
            if ((node.PersonalityForbidden & bit) != 0) return false;
        }
        // DC_HAS_TAG (FUN_140674c50): the speaker squad's long-term tags on the target hold every required tag and no forbidden one; never noted: only lines that require
        // nothing pass; no target: not tested.
        if ((node.MemoryRequired | node.MemoryForbidden) != 0 && tc is not null)
        {
            if (Memory.Find(world, mc.SquadId, other) is not { } known) { if (node.MemoryRequired != 0) return false; }
            else if ((known.Long & node.MemoryRequired) != node.MemoryRequired || (known.Long & node.MemoryForbidden) != 0) return false;
        }
        if (node.IsRoot && (mc.DialogueLocks?.TryGetValue(node.StringId, out bool locked) == true ? locked : node.Locked)) return false;
        if (node.OneAtATime && node.IsRoot && SquadOf(world, me).Any(m => m != me && interactions.Values.Any(s => s.Open?.Npc == m && s.Open.Root == node))) return false;
        if (node.ChanceTemporary < 99 && Rng.Float(NextRoll(world, me)) * 100 >= node.ChanceTemporary) return false;
        if (node.InTownOf.Count > 0 && (TownFaction(world, table.Next[ms].Position) is not { } tf || !node.InTownOf.Contains(tf))) return false;
        if (node.MyFaction.Count > 0 && !node.MyFaction.Contains(FactionId(mc.Faction))) return false;
        if (node.TargetFaction.Count > 0 && (tc is null || !node.TargetFaction.Contains(FactionId(tc.Faction)))) return false;
        if (node.HasPackage.Count > 0 && !node.HasPackage.Overlaps(SetOf(world, ms).Packages)) return false;
        if (node.IsCharacter.Count > 0 && !node.IsCharacter.Contains(mc.RecordId)) return false;
        if (node.TargetCarryingCharacter.Count > 0 && !Known(world, me, other).Any(m => Cold(world, m) is { Carrying.IsNone: false } carrier
            && Cold(world, carrier.Carrying) is { } body && node.TargetCarryingCharacter.Contains(body.RecordId))) return false;
        var states = population?.States ?? data.States;
        foreach (var (state, value) in node.WorldStates) if (!IWorldStates.Passes(states, data.Db, state, value ? 1 : 0)) return false;
        if (node.TargetHasItemFunction.Count > 0 && !Known(world, me, other).Any(m => Cold(world, m)?.Inventory?.All()
            .Any(i => node.TargetHasItemFunction.Contains(data.Db.Find(i.Record)?.GetInt("item function") ?? -1)) == true)) return false;
        if (node.TargetHasItem.Count > 0 && !Known(world, me, other).Any(m => Cold(world, m)?.Inventory?.All().Any(i => node.TargetHasItem.Contains(i.Record)) == true)) return false;
        if (node.TargetRace.Count > 0 && !Known(world, me, other).Any(m => Cold(world, m)?.Race is { } r && node.TargetRace.Contains(r.StringId))) return false;
        if (node.MyRace.Count > 0 && (mc.Race is null || !node.MyRace.Contains(mc.Race.StringId))) return false;
        if (node.MySubrace.Count > 0 && (mc.Race is null || !node.MySubrace.Contains(mc.Race.StringId))) return false;
        if (node.CooldownHours > 0 && lastUse.TryGetValue(node.StringId, out double last) && world.GameHours - last < node.CooldownHours) return false;
        if (node.TargetIsType > 0)
        {
            if (tc is null || (int)tc.NpcClass != node.TargetIsType) return false;
        }
        if (node.ChancePermanent < 99)
        {
            var key = (me, node.StringId);
            if (!permanent.TryGetValue(key, out bool passed)) permanent[key] = passed = Rng.Float(NextRoll(world, me)) * 100 < node.ChancePermanent;
            if (!passed) return false;
        }
        foreach (var condition in node.Conditions)
            if (!Condition(world, c, condition, me, mc, other)) return false;
        return true;
    }

    static CharacterCold? Cold(World world, CharacterId id) => world.Characters.TryResolveNext(id, out int s) ? world.Characters.Cold(s) : null;

    /// <summary>The members of <paramref name="id"/>'s squad (itself alone without one).</summary>
    static IReadOnlyList<CharacterId> SquadOf(World world, CharacterId id) =>
        Cold(world, id) is { SquadId: >= 0 } c && world.Squads.Find(c.SquadId) is { } squad ? squad.Members : [id];

    /// <summary>
    /// The members of <paramref name="target"/>'s squad that <paramref name="me"/> perceives (<c>FUN_140855960</c>): the target itself, and the others within <paramref name="me"/>'s
    /// sight range (engine stand-in for the original's sensed list).
    /// </summary>
    static IEnumerable<CharacterId> Known(World world, CharacterId me, CharacterId target)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(me, out int ms) || table.Cold(ms) is not { } mc) return [];
        var at = table.Next[ms].Position;
        float r = AggroSystem.RangeOf(mc, AggroSystem.OutdoorSight);
        return SquadOf(world, target).Where(m => m == target || m == me
            || table.TryResolveNext(m, out int s) && Vector3.DistanceSquared(table.Next[s].Position, at) <= r * r);
    }

    string FactionId(int faction) => data.Relations.Contains(faction) ? data.Factions[faction].Id : "";

    /// <summary>The placed town whose radius holds <paramref name="at"/> (the game's position lookup, <see cref="PopulationData.TownAt"/>: the town status of a character), or -1.</summary>
    int TownAt(Vector3 at) => data.TownAt(at);

    string? TownFaction(World world, Vector3 at) =>
        TownAt(at) is >= 0 and var site && site < data.SiteFactions.Count && data.SiteFactions[site] is >= 0 and var f ? FactionId(f) : null;
}
