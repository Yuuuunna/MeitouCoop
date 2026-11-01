using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>
    /// A line's effects (<c>FUN_140680560</c>, docs/game/dialogue.md "Effects"), run as it is said or clicked, with <paramref name="me"/> the NPC-side speaker and the player character the
    /// target, in the original's order: locks, interrupt, relation changes, then the DA effects in data order, then AI changes and contracts. Not built yet: arrests other
    /// than the bounty desk's hand-over, DA 48 (animals for sale), DA 5, 15, 17, 39 and 58 (docs/game/dialogue.md "Effects").
    /// </summary>
    void RunEffects(World world, Conversation c, DialogueNode line, CharacterId me)
    {
        if (!PrepareEffects(world, c, line, me)) { c.Ended = true; return; }
        var mc = Cold(world, me);
        if (mc is not null)
        {
            foreach (string d in line.Unlocks) Lock(mc, d, false);
            if (line.Unlocks.Count > 0 && c.Root is { } root) Lock(mc, root.StringId, true);
            foreach (string d in line.UnlockButKeepMe) Lock(mc, d, false);
            foreach (string d in line.Locks) Lock(mc, d, true);
        }
        if (line.Interrupt is { } interrupt) SetInterrupt(c, me, interrupt);
        foreach (var (factionId, amount) in line.ChangeRelations)
        {
            int f = data.Relations.IndexOf(factionId);
            if (f < 0 || amount == 0 || c.Faction < 0) continue;
            ChangeRelation(world, f, c.Faction, amount);
            Post(world, c, amount > 0 ? Loc.Tr("Relations with {1} improved by {2}", data.Factions[f].Name, amount) : Loc.Tr("Relations with {1} decreased by {2}", data.Factions[f].Name, -amount));
        }
        CrowdTrigger(world, c, line, me);
        var target = c.Player;
        var tc = Cold(world, target);
        if (tc is not null) GiveItems(world, c, line, target, tc);
        if (line.TriggerCampaigns.Count > 0) population?.TriggerFromDialogue(world, me, c.Player, line.TriggerCampaigns);
        bool takeOver = false, enslave = false;
        foreach (var effect in line.Effects)
        {
            switch (effect.Action)
            {
                case DialogueActions.Trade:
                    c.Ended = true;
                    if (mc is { SquadId: >= 0 } && world.Squads.Find(mc.SquadId)?.Shop is not null)
                        DeferPlayer(world, c, new Trading.OpenTrade(target, me) { Tick = world.Tick + 1 });
                    return;
                case DialogueActions.AffectRelations when mc is not null && tc is not null:
                    ChangeRelation(world, mc.Faction, tc.Faction, effect.Value);
                    break;
                case DialogueActions.AffectReputation when mc is not null:
                    data.Relations.AddGlobalTrust(mc.Faction, effect.Value);   // the speaker's faction's global trust (slot 9), whoever the target is
                    break;
                case DialogueActions.AttackChaseForever or DialogueActions.AttackStayNearHome:
                    if (mc is not null && tc is not null) FlagFrom(world, mc, me, target, tc, MemoryTag.TempEnemy);
                    Attack(world, me, target);
                    break;
                case DialogueActions.AttackIfNoCoexist when mc is not null && tc is not null && !data.Relations.Coexists(mc.Faction, tc.Faction):
                    FlagFrom(world, mc, me, target, tc, MemoryTag.TempEnemy);
                    Attack(world, me, target);
                    break;
                case DialogueActions.TakeMoney when tc is not null:
                    Pay(world, tc, -(long)effect.Value);
                    Post(world, c, effect.Value > 100000 ? Loc.Tr("Paid c.{1}k", effect.Value / 1000) : Loc.Tr("Paid c.{1}", effect.Value.ToString("N0", CultureInfo.InvariantCulture)));
                    break;
                case DialogueActions.GiveMoney when tc is not null:
                    Pay(world, tc, effect.Value);
                    break;
                case DialogueActions.CharacterEditor:
                    c.Ended = true;
                    var editSession = Editing.CharacterEditing.OpenSession(world, c.Actor, target, me, Editing.EditKind.Surgery, paid: true);
                    if (editSession != Guid.Empty)
                        InteractionOf(c).Request = new DialogueRequest(++requestId, DialogueRequestKind.CharacterEditor, target, me) { EditSession = editSession };
                    return;
                case DialogueActions.ChooseSlavesSelling or DialogueActions.ChooseSlavesBuying or DialogueActions.ChooseAnimalsBuying when target != me:
                    // The character trading window opens as the conversation closes (OpenCharacterTrade), a speech bubble's at once; val0 is the price override.
                    InteractionOf(c).TradeAfter = (effect.Action switch
                    {
                        DialogueActions.ChooseSlavesSelling => CharacterTradeKind.SlaveSelling,
                        DialogueActions.ChooseSlavesBuying => CharacterTradeKind.SlaveBuying,
                        _ => CharacterTradeKind.AnimalBuying,
                    }, me, target, effect.Value, c);
                    if (!ReferenceEquals(InteractionOf(c).Open, c)) { OpenCharacterTrade(world, InteractionOf(c)); break; }
                    c.Ended = true;
                    return;
                case ChoosePrisonerBail when Arrests is not null:
                    c.Ended = true;
                    InteractionOf(c).BailAfter = (me, target, c);   // the bail list opens as the conversation closes
                    return;
                case DialogueActions.AssignBounty when tc is not null && mc is not null && effect.Value > 0 && data.Relations.Contains(mc.Faction):
                {
                    tc.Bounties ??= new Crime.BountyLedger();
                    tc.Bounties.Get(mc.Faction, data.Factions[mc.Faction].Name).Amount += effect.Value;
                    break;
                }
                // Without an amount only a speaker whose CHARACTER has `assigns bounties` books the target's pending crime (Character::canAssignBounties, Verified).
                case DialogueActions.AssignBounty when tc is not null && mc is not null && effect.Value <= 0 && Arrests is { } booker
                    && data.Db.Find(mc.RecordId)?.GetBool("assigns bounties") == true && world.Characters.TryResolveNext(target, out int culprit):
                    booker.Crimes.Book(world, culprit, booker.Crimes.Factions.BookingOf(mc.Faction), me);   // for the law faction (getLawEnforcementFaction)
                    break;
                case DialogueActions.CrimeAlarm when tc is not null && mc is not null && Arrests is { } law
                    && world.Characters.TryResolveNext(me, out int alarmer) && world.Characters.TryResolveNext(target, out int alarmed):
                    law.Crimes.Alarm(world, alarmer, alarmed);
                    break;
                case DialogueActions.PayBounty:
                    Arrests?.PayBounty(world, me, target);
                    break;
                case DialogueActions.ArrestTargetsCarriedPerson when tc is { Carrying.IsNone: false }:
                    takeOver = true;   // after the line's other effects: the original only gives the speaker the lift task, so DA_PAY_BOUNTY still finds the body
                    break;
                case DialogueActions.EnslaveTargetsCarriedPerson when tc is { Carrying.IsNone: false }:
                    takeOver = enslave = true;
                    break;
                case DialogueActions.GiveTargetMySlaves when tc is not null && mc is not null:
                    GiveSlaves(world, c, me, mc, tc);
                    break;
                case DialogueActions.SurrenderNonHumans when tc is not null && mc is not null:
                    SurrenderNonHumans(world, me, mc, tc);
                    break;
                case DialogueActions.TalkToLeader when tc is not null && mc is not null:
                    TalkToLeader(world, c, mc, target, tc);
                    break;
                case DialogueActions.ArrestTarget when tc is not null:
                    ArrestMemory(world, me, mc, target, tc);
                    Arrests?.ArrestFromDialogue(world, me, target);
                    break;
                case DialogueActions.TagEscapedSlave when tc is not null:
                    Locks.FixtureRegistry.TagEscaped(world, tc);
                    break;
                case DialogueActions.RemoveSlaveStatus when tc is not null && world.Characters.TryResolveNext(target, out int freed):
                    Locks.FixtureRegistry.FreeSlave(world, freed, tc);
                    break;
                case DialogueActions.ClearBounty when tc?.Bounties is { } ledger && mc is not null:
                    if (ledger.Of(mc.Faction) is { } entry) ledger.Remove(entry);
                    if (Position(world, target) is { } at && TownAt(at) is >= 0 and var town && town < data.SiteFactions.Count && ledger.Of(data.SiteFactions[town]) is { } local)
                        ledger.Remove(local);
                    if (ledger.Count == 0) tc.Bounties = null;
                    tc.Recognition = 0;
                    break;
                case DialogueActions.IncreaseFactionRank when mc is not null:
                    RaiseFactionRank(world, c, mc.Faction, effect.Value);
                    break;
                case DialogueActions.LockThisDialog when mc is not null && c.Root is { } current:
                    Lock(mc, current.StringId, true);
                    break;
                case DialogueActions.AssaultPhase or DialogueActions.RetreatPhase:
                    population?.SetCampaignPhase(world, me, effect.Action == DialogueActions.AssaultPhase);
                    break;
                case DialogueActions.FollowWhileTalking when !target.IsNone && mc is { IsPlayer: false }:
                    followers[me] = target;
                    break;
                case DialogueActions.ThugHunter when !target.IsNone && mc is { IsPlayer: false }:
                    stalkers[me] = new Stalk(target);
                    break;
                case DialogueActions.RunAway when mc is not null && tc is not null:
                    RunAway(world, me, mc, target, tc);
                    break;
                case DialogueActions.GoHome when mc is not null && tc is not null:
                    GoHome(world, me, mc, target, tc);
                    break;
                case DialogueActions.RememberCharacter when mc is not null && tc is not null:
                    Memory.Remember(world, mc.SquadId, target, MemoryTag.None);
                    break;
                case DialogueActions.FlagTempAlly or DialogueActions.FlagTempEnemy when mc is not null && tc is not null:
                    Memory.Flag(world, mc.SquadId, target, effect.Action == DialogueActions.FlagTempAlly ? MemoryTag.TempAlly : MemoryTag.TempEnemy);
                    break;
                case DialogueActions.MakeTargetRunFaster when tc is not null && world.Characters.TryResolveNext(target, out int runner):
                    world.Characters.Next[runner].Mode = SpeedMode.Free;   // the original's speed mode 2
                    break;
                case DialogueActions.SeparateToMyOwnSquad when mc is not null:
                    Separate(world, mc.IsPlayer || mc.Faction == c.Faction ? target : me);
                    break;
                case DialogueActions.DeclareWar when mc is not null && tc is not null && data.Relations.Contains(mc.Faction) && data.Relations.Contains(tc.Faction):
                    PlayerRelations.DeclareWar(world, data.Relations, mc.Faction, tc.Faction);
                    break;
                case DialogueActions.EndWar when mc is not null && tc is not null && data.Relations.Contains(mc.Faction) && data.Relations.Contains(tc.Faction):
                    PlayerRelations.EndWar(world, data.Relations, mc.Faction, tc.Faction);
                    break;
                case DialogueActions.SummonMySquad:
                    Summon(world, me);
                    break;
                case DialogueActions.OpenNearestGate:
                    OpenNearestGate(world, me);
                    break;
                case DialogueActions.MassiveAlarm when mc is not null && tc is not null:
                    MassiveAlarm(world, me, mc, target, tc);
                    break;
                case DialogueActions.JoinSquadWithEdit or DialogueActions.JoinSquadFast:
                    if (!Recruit(world, c, me, effect.Action == DialogueActions.JoinSquadWithEdit)) return;
                    break;
            }
        }
        if (takeOver) Arrests?.TakeOver(world, me, target, enslave);
        // Last, for a speaker squad outside the player's faction (FUN_140273330): change AI replaces its packages, then AI contract starts the contract aimed at the target.
        if (mc is { SquadId: >= 0 } && mc.Faction != c.Faction && !mc.IsPlayer)
        {
            if (line.ChangeAi is { } package) world.Defer(new Ai.ChangeAi(mc.SquadId, package) { Tick = world.Tick + 1 });
            if (line.AiContract is { } contract) world.Defer(new Ai.StartContract(mc.SquadId, contract.Package, contract.Hours, target.IsNone ? null : target) { Tick = world.Tick + 1 });
        }
    }

    /// <summary>The arrest system the bounty desk's effects pay and hand over through (<c>DA_PAY_BOUNTY</c>, <c>DA_ARREST_TARGETS_CARRIED_PERSON</c>); null: they do nothing.</summary>
    public Crime.ArrestSystem? Arrests { get; set; }

    /// <summary>
    /// <c>give item</c> (<c>FUN_140680560</c>): each listed item made and put into the target's inventory, val0 2..49 its stack count, and "Received &lt;name&gt;" posted. What does not fit
    /// is dropped at the target's feet (engine choice; the original's add has no such case in this code).
    /// </summary>
    void GiveItems(World world, Conversation c, DialogueNode line, CharacterId target, CharacterCold tc)
    {
        foreach (var (id, count) in line.GiveItems)
        {
            if (data.Items.Create(id, count is >= 2 and <= 49 ? count : 1) is not { } item) continue;
            string name = item.Name;
            if (tc.Inventory?.Add(item) != true && Position(world, target) is { } at) world.GroundItems.Drop(item, at, 0);
            Post(world, c, $"{Loc.Tr("Received")} {name}");
        }
    }

    /// <summary>
    /// <c>DA_SURRENDER_NON_HUMANS</c> (47, <c>FUN_140680560</c>): the target faction's characters within 100 of the speaker, not animals, whose race is not the speaker's
    /// (<c>FUN_14042ecd0</c>: its race or one of its sub-races; engine reading: the same RACE record) become slaves owned by the speaker's faction (<c>FUN_14032e590</c>), and each
    /// one's squad remembers the speaker. Their squads are left as they are (what follows is not read).
    /// </summary>
    void SurrenderNonHumans(World world, CharacterId me, CharacterCold mc, CharacterCold tc)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(me, out int s) || mc.Race is not { } race) return;
        var at = table.Next[s].Position;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Medical.Dead: false } c || c.Faction != tc.Faction || c.SlaveState != 0) continue;
            if (c.Fighter?.IsAnimal == true || c.Appearance?.Animal is not null || c.Race is null || c.Race.StringId == race.StringId) continue;
            if (Vector3.DistanceSquared(table.Next[i].Position, at) > 100 * 100) continue;
            Locks.FixtureRegistry.Enslave(world, c, mc.Faction, mc.SquadId);
            Memory.Remember(world, c.SquadId, me, MemoryTag.None);   // its squad remembers the speaker (the tag is not visible in the decompile; engine: none)
        }
    }

    /// <summary>
    /// DA_ARREST_TARGET's memory (<c>FUN_140680560</c>): in the speaker's home town (and the target no animal) the target's squad tags the speaker MY_CAPTOR; elsewhere the speaker's squad
    /// tags the target TEMP_ENEMY.
    /// </summary>
    void ArrestMemory(World world, CharacterId me, CharacterCold? mc, CharacterId target, CharacterCold tc)
    {
        if (mc is null) return;
        bool inTown = world.Squads.Find(mc.SquadId) is { Town: >= 0 } squad && Position(world, me) is { } here && TownAt(here) == squad.Town
            && tc.Fighter?.IsAnimal != true && tc.Appearance?.Animal is null;
        if (inTown) Memory.Remember(world, tc.SquadId, me, MemoryTag.Captor);
        else Memory.Flag(world, mc.SquadId, target, MemoryTag.TempEnemy);
    }

    static void Lock(CharacterCold cold, string dialogue, bool locked) => (cold.DialogueLocks ??= new(StringComparer.Ordinal))[dialogue] = locked;

    void ChangeRelation(World world, int a, int b, int amount)
    {
        var (before, after) = data.Relations.Add(a, b, amount);
        if (world.IsPlayerFaction(a) && data.Relations.Contains(b) && PlayerRelations.Notice(data.Factions[b].Name, before, after) is { } first) world.PostFaction(a, first);
        if (b != a && world.IsPlayerFaction(b) && data.Relations.Contains(a) && PlayerRelations.Notice(data.Factions[a].Name, before, after) is { } second) world.PostFaction(b, second);
    }

    static void Pay(World world, CharacterCold who, long amount)
    {
        if (who.IsPlayer || world.IsPlayerFaction(who.Faction))
        {
            if (who.Faction < 0) return;
            var funds = world.Factions.GetOrCreate(who.Faction);
            if (amount >= 0) funds.TryCredit(amount); else funds.TryDebit(-amount);
        }
        else if (who.SquadId >= 0 && world.Squads.Find(who.SquadId) is { } squad)
            squad.Money = amount >= 0 ? Math.Min(squad.Money, long.MaxValue - amount) + amount : Math.Max(0, squad.Money + amount);
    }

    bool PrepareEffects(World world, Conversation c, DialogueNode line, CharacterId speaker)
    {
        if (!Authorized(world, c)) return false;
        var target = Cold(world, c.Player);
        if (target is { IsPlayer: true } && Funds(world, c) is { } funds)
        {
            long balance = funds.Money;
            foreach (var effect in line.Effects)
            {
                long delta = effect.Action switch { DialogueActions.TakeMoney => -(long)effect.Value, DialogueActions.GiveMoney => effect.Value, _ => 0 };
                if (delta < 0 && (!CanSpend(world, c) || balance < -delta))
                { Post(world, c, "Not enough money or no permission to spend this faction's funds."); return false; }
                if (delta > 0 && balance > long.MaxValue - delta)
                { Post(world, c, "This payment would exceed the account balance limit."); return false; }
                balance += delta;
            }
        }
        if (line.Effects.Any(e => e.Action is DialogueActions.JoinSquadFast or DialogueActions.JoinSquadWithEdit)
            && Cold(world, speaker) is { IsPlayer: false } recruit && target is { IsPlayer: true }
            && (!CanJoin(world, c, target) || c.Actor is not null && world.Players.KnownControl(recruit.Entity) is not null))
        { Post(world, c, "It looks like your squad is a little full, or you cannot manage this faction."); return false; }
        return true;
    }

    /// <summary>The speaker and the awake members of its squad near the target attack it from the next tick (DA_ATTACK_CHASE_FOREVER's task 101 on the target's squad, engine reading).</summary>
    static void Attack(World world, CharacterId me, CharacterId target)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(target, out int t)) return;
        var at = table.Next[t].Position;
        var fighters = new List<CharacterId>();
        foreach (var m in SquadOf(world, me))
        {
            if (!table.TryResolveNext(m, out int s) || !table.Next[s].Alive || table.Cold(s) is not { IsPlayer: false, IsImprisoned: false, Medical.Incapacitated: false, Fighter.CanAttack: true } mc) continue;
            float r = 2 * AggroSystem.RangeOf(mc, AggroSystem.OutdoorSight);
            if (m == me || Vector3.DistanceSquared(table.Next[s].Position, at) <= r * r) fighters.Add(m);
        }
        if (fighters.Count > 0) world.Defer(new Combat.AttackOrder(fighters, target) { Tick = world.Tick + 1 });
    }

    /// <summary>
    /// The text tokens (docs/game/dialogue.md "Tokens"): <c>/NAME/</c> pairs until none is left; COST, MYNAME, YOURNAME, TARGETFACTION and PLAYERFACTION are built in, any other name
    /// is the WORD_SWAPS record of that name (its lines chosen like a line's children, one of their texts; none: empty). A token at the start or after ". ", "! ", "? " is capitalised.
    /// </summary>
    string Expand(World world, Conversation c, DialogueNode line, string text, CharacterId me, CharacterId other, int depth = 0)
    {
        if (depth > 4 || text.IndexOf('/') < 0) return text;
        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('/', i);
            int close = open < 0 ? -1 : text.IndexOf('/', open + 1);
            if (close < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, open - i);
            string token = text[(open + 1)..close];
            string value = Token(world, c, line, token.Trim(), me, other, depth);
            bool capital = sb.Length == 0 || sb.ToString().EndsWith(". ", StringComparison.Ordinal) || sb.ToString().EndsWith("! ", StringComparison.Ordinal) || sb.ToString().EndsWith("? ", StringComparison.Ordinal);
            if (capital && value.Length > 0) value = char.ToUpperInvariant(value[0]) + value[1..];
            sb.Append(value);
            i = close + 1;
        }
        return sb.ToString();
    }

    string Token(World world, Conversation c, DialogueNode line, string token, CharacterId me, CharacterId other, int depth)
    {
        switch (token)
        {
            case "COST": return line.Cost is { } cost ? cost.ToString("N0", CultureInfo.InvariantCulture) : "";
            case "MYNAME": return NameOf(world, me);
            case "YOURNAME": return Cold(world, other)?.Name ?? "you";
            case "TARGETFACTION": return Cold(world, other) is { } o && data.Relations.Contains(o.Faction) ? data.Factions[o.Faction].Name : "you";
            case "PLAYERFACTION": return data.Relations.Contains(c.Faction) ? world.Factions.Find(c.Faction)?.Name ?? data.Factions[c.Faction].Name : "";
        }
        if (dialogue.WordSwap(token) is not { } swap) return "";
        var candidates = new List<DialogueNode>();
        int best = int.MinValue;
        foreach (var child in swap.Children)
        {
            if (child.Score < best) break;
            if (!Check(world, c, child, me, other)) continue;
            if (child.Score > best) { best = child.Score; candidates.Clear(); }
            candidates.Add(child);
        }
        if (candidates.Count == 0) return "";
        var pick = candidates.Count == 1 ? candidates[0] : candidates[Rng.Int(NextRoll(world, me), candidates.Count)];
        return Expand(world, c, pick, PickText(world, me, pick), me, other, depth + 1);
    }
}
