using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

/// <summary>The player's Talk order: <see cref="Talker"/> walks up to <see cref="Target"/> and the conversation opens in reach (the original's task 12, then event 1).</summary>
public sealed record TalkOrder(CharacterId Talker, CharacterId Target) : OrderCommand;
/// <summary>Clicks choice <see cref="Index"/> of the open conversation.</summary>
public abstract record DialogueCommand : SimCommand
{
    public ulong SessionId { get; init; }
    public ulong ChoiceRevision { get; init; }
}
public sealed record DialogueChoice(int Index) : DialogueCommand;
/// <summary>Leaves the open conversation (its window closed).</summary>
public sealed record EndConversation : DialogueCommand;
/// <summary>
/// Fires dialogue event <see cref="Event"/> at <see cref="Npc"/> with <see cref="Target"/> as its partner (the original's <c>FUN_140684990</c>, from the systems that witness it):
/// a line of the NPC's list for the event, or of <see cref="Dialogue"/> when given, said as speech bubbles.
/// </summary>
public sealed record DialogueEvent(CharacterId Npc, CharacterId Target, int Event, string? Dialogue = null) : SimCommand;

/// <summary>The open player conversation as the conversation window shows it (published with the snapshot).</summary>
public sealed record ConversationView(CharacterId Npc, CharacterId Player, CharacterId Speaker, string SpeakerName, string PlayerName, string Text,
    IReadOnlyList<string> Choices, IReadOnlyList<string> Log, long PlayerMoney)
{
    public ulong SessionId { get; init; }
    public ulong ChoiceRevision { get; init; }
    public bool FundsVisible { get; init; } = true;
};

/// <summary>
/// What a conversation asks the host to do once it has ended: DA_CHARACTER_EDITOR, the surgeon's editor on <see cref="DialogueRequest.Player"/>, already paid; DA_JOIN_SQUAD_WITH_EDIT,
/// the in-play editor on the recruit (<see cref="DialogueRequest.Player"/>); the animal buying window's confirm, the in-play editor on the bought animals
/// (<see cref="DialogueRequest.Characters"/>, to name them).
/// </summary>
public enum DialogueRequestKind : byte { CharacterEditor, RecruitEditor, AnimalEditor }
public sealed record DialogueRequest(long Id, DialogueRequestKind Kind, CharacterId Player, CharacterId Npc)
{
    public Guid EditSession { get; init; }
    public IReadOnlyList<CharacterId> Characters { get; init; } = [];
    public IReadOnlyDictionary<CharacterId, Guid> EditSessions { get; init; } = new Dictionary<CharacterId, Guid>();

}

/// <summary>A system with a conversation to publish (the dialogue system).</summary>
public interface IDialogueSource
{
    (int Serial, bool Forget) ArrivalResultFor(PlayerId player) => (0, false);
    ConversationView? PublishConversation();
    ConversationView? PublishConversation(PlayerId player) => player == PlayerId.Local ? PublishConversation() : null;
    ConversationView? PublishConversation(World world, PlayerId player) => null;
    DialogueRequest? PublishRequest();
    DialogueRequest? PublishRequest(PlayerId player) => player == PlayerId.Local ? PublishRequest() : null;
    DialogueRequest? PublishRequest(World world, PlayerId player) => null;
    /// <summary>The open character trading window (DA 41 / 42; null: none).</summary>
    CharacterTradeView? PublishCharacterTrade() => null;
    CharacterTradeView? PublishCharacterTrade(PlayerId player) => player == PlayerId.Local ? PublishCharacterTrade() : null;
    CharacterTradeView? PublishCharacterTrade(World world, PlayerId player) => null;
    /// <summary>The character answers the player's Talk (it has event-1 dialogue, and with <paramref name="talker"/> given, a line of it passes now).</summary>
    bool CanTalk(World world, int slot, CharacterId? talker = null);
    /// <summary>The character holds a line it wants to say to the player (the original's Dialogue <c>playerInterruptionDialog</c>, +0xc8): its quick right click pointer is <c>s_talk</c>.</summary>
    bool HasInterrupt(CharacterId npc) => false;
    bool HasInterrupt(CharacterId npc, PlayerId? player, int? faction = null) => HasInterrupt(npc);
    bool IsBusy(World world, CharacterId npc, PlayerId actor) => false;
    /// <summary>The lines on show over characters' heads (speech bubbles).</summary>
    IReadOnlyList<SpeechBubble> PublishSpeech() => [];
}

public sealed class DialogueSettings
{
    /// <summary>Game seconds the talker has to reach the target before the order is dropped (engine choice).</summary>
    public float ApproachSeconds { get; init; } = 60;
    /// <summary>
    /// NPCs react to a player character they see with an ambient line (events 3 neutral, 66 enemy, 67 ally) as a speech bubble. Engine choice for the Unknown perception rules
    /// (docs/game/dialogue.md "Ambient conversations"): off by default (the pinned scenarios), the game turns it on.
    /// </summary>
    public bool AmbientEvents { get; init; }
    /// <summary>
    /// How near a player character NPCs must be for their exchanges with each other to be run (a player character is seen by the sight test instead), how often (ticks) the NPCs look,
    /// how many speak per look and the game hours before a squad reacts to the same character again (engine choices).
    /// </summary>
    public float AmbientRange { get; init; } = 600;
    public int AmbientEveryTicks { get; init; } = 30;
    public int AmbientPerScan { get; init; } = 2;
    public float AmbientRepeatHours { get; init; } = 0.5f;
    /// <summary>Whether the displayed lines go through <see cref="Censorship"/> (the player's setting, read when a line is built).</summary>
    public Func<bool>? Censored { get; init; }
}

/// <summary>
/// Dialogue (docs/game/dialogue.md; the engine design is its "Meitou design"): the player's conversations with NPCs. A <see cref="TalkOrder"/> walks the talker up and fires event 1
/// (PLAYER_TALK_TO_ME) at the NPC: its event-1 dialogue is chosen (score, line checks, a random pick at the top score), the NPC's line is said (its effects run at once) and the
/// passing replies become choices. The window pauses the game, so <see cref="DialogueChoice"/> and <see cref="EndConversation"/> apply between ticks (<see cref="World.ApplyPausedInterfaceCommands"/>);
/// effects that give AI orders start on the next tick.
/// <list type="bullet">
/// <item><b>Engine choices</b>: a childless NPC line is shown with a single <c>[Leave]</c> (the original ends at once and how it shows such a line is <b>Unknown</b>); a reply whose best
/// next line is another reply shows that reply as the only choice; conditions whose inputs Meitou lacks compute "no" (<see cref="Condition"/>).</item>
/// <item><b>State</b> (hashed): last use per line (global, as the original's), permanent chance rolls and event counters per character, the squads' memories (<see cref="Memory"/>), pending
/// interrupts, the open conversation. The characters' dialogue locks are on <see cref="CharacterCold.DialogueLocks"/>.</item>
/// </list>
/// </summary>
public sealed partial class DialogueSystem : ITickSystem, IStateHashed, IDialogueSource
{
    readonly PopulationData data;
    readonly DialogueDatabase dialogue;
    readonly SortedDictionary<string, double> lastUse = new(StringComparer.Ordinal);
    readonly SortedDictionary<(CharacterId, string), bool> permanent = new(Comparer<(CharacterId, string)>.Create((a, b) =>
        a.Item1 != b.Item1 ? CompareIds(a.Item1, b.Item1) : string.CompareOrdinal(a.Item2, b.Item2)));
    readonly SortedDictionary<(CharacterId, int), (int Count, double Last)> counters = new(Comparer<(CharacterId, int)>.Create((a, b) =>
        a.Item1 != b.Item1 ? CompareIds(a.Item1, b.Item1) : a.Item2.CompareTo(b.Item2)));
    readonly SortedDictionary<CharacterId, string> interrupts = new(Comparer<CharacterId>.Create(CompareIds));
    ulong rolls;
    long requestId;


    readonly PopulationSystem? population;

    public DialogueSystem(PopulationData data, DialogueSettings? settings = null, PopulationSystem? population = null)
    {
        this.data = data;
        this.population = population;
        dialogue = data.Dialogue;
        Settings = settings ?? new DialogueSettings();
    }

    public DialogueSettings Settings { get; }

    /// <summary>The squads' memories of characters (docs/game/dialogue.md "Squad memory"): written by dialogue effects and player conversations, read by the conditions.</summary>
    public SquadMemory Memory { get; } = new();

    static int CompareIds(CharacterId a, CharacterId b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Generation.CompareTo(b.Generation);

    /// <summary>One open player conversation.</summary>
    sealed class Conversation
    {
        public Interaction? Interaction;
        public PlayerId? Actor;
        public int Faction = -1;
        public CharacterControl? Control;
        public ulong MembershipRevision, Revision;
        public required CharacterId Npc, Player;
        public required int Event;
        /// <summary>The Talk menu's dry run of the pick: conditions post no messages.</summary>
        public bool DryRun { get; init; }
        public DialogueNode? Root;
        /// <summary>The NPC-side line on screen and who said it.</summary>
        public DialogueNode? Line;
        public CharacterId Speaker;
        public string Text = "";
        public readonly CharacterId[] Interjectors = [CharacterId.None, CharacterId.None, CharacterId.None];
        /// <summary>The choices: a reply line, or <c>null</c> for <c>[...]</c> (continue <see cref="Line"/>) / <c>[Leave]</c> (when <see cref="Line"/> has no next).</summary>
        public readonly List<(DialogueNode? Reply, string Text)> Choices = [];
        public readonly List<string> Log = [];
        public bool Ended;
    }

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            if (command is not (TalkOrder or DialogueCommand or ConfirmCharacterTrade or CloseCharacterTrade or AreaArrival or DialogueEvent)) continue;
            if (command is AreaArrival arrivalEvent)
            {
                if (command.Issuer is null) Arrive(world, arrivalEvent);
                else if (world.CommandPlayer(command) is { } player
                    && world.Squads.Find(arrivalEvent.SquadId >= 0 ? arrivalEvent.SquadId : player.Squad) is { } squad
                    && squad.Faction == player.Faction && arrivalEvent.Leader == squad.Leader && world.MayOrder(command, squad.Leader)
                    && world.Characters.TryResolveNext(squad.Leader, out int leader)
                    && data.BiomeGroupAt(new Vector2(world.Characters.Next[leader].Position.X, world.Characters.Next[leader].Position.Z)) == arrivalEvent.GroupId)
                    Arrive(world, arrivalEvent);
                continue;
            }
            if (command is DialogueEvent scripted)
            {
                if (command.Issuer is not null) continue;
                if (scripted.Event == UnlockMyCageOrShackles && scripted.Dialogue is null) Freed(world, scripted.Npc, scripted.Target);
                if (scripted.Dialogue is { } rootId) { if (dialogue.Node(rootId) is { IsRoot: true } root) StartBubbles(world, scripted.Npc, scripted.Target, root, scripted.Event); }
                else if (Settings.AmbientEvents) Fire(world, scripted.Npc, scripted.Target, scripted.Event);
                continue;
            }
            var actor = command.Issuer ?? PlayerId.Local;
            if (world.Players.Find(actor) is null) continue;
            var state = InteractionFor(actor);
            if (command is DialogueCommand scoped && (command.Issuer.HasValue != state.Attributed
                || command.Issuer is not null && scoped.SessionId == 0 || scoped.SessionId != 0 && scoped.SessionId != state.Id)) continue;
            if (command is not TalkOrder && !Valid(world, state)) { CloseForPlayer(world, actor); continue; }
            switch (command)
            {
                case TalkOrder talk:
                    if (!world.MayOrder(command, talk.Talker) || !world.Characters.TryResolveNext(talk.Talker, out int p)) break;
                    if (command.Issuer is not null && world.Players.ControlOf(talk.Target) is not null) break;
                    int faction = world.Characters.Cold(p)!.Faction;
                    if (command.Issuer is not null && (command.FactionGrantRevision == 0
                        || world.Players.Find(actor)!.MembershipRevision(faction) != command.FactionGrantRevision)) break;
                    if (!NpcAvailable(world, talk.Target, state)) { world.Post(actor, "This character is busy."); break; }
                    End(world, state, followups: false);
                    CancelApproach(world, state);
                    state.Trade = null; state.TradeView = null;
                    state.Id = nextInteraction = checked(nextInteraction + 1);
                    state.Attributed = command.Issuer is not null;
                    state.Control = state.Attributed ? world.Players.ControlOf(talk.Talker) : null;
                    state.MembershipRevision = command.FactionGrantRevision;
                    state.Faction = faction;
                    world.SystemOf<Trading.TradeSystem>()?.CloseForPlayer(world, actor);
                    StartApproach(world, talk, state);
                    break;
                case DialogueChoice choice when state.Open is { } conversation:
                    if (command.Issuer is not null && choice.ChoiceRevision != conversation.Revision
                        || command.Issuer is null && choice.ChoiceRevision != 0 && choice.ChoiceRevision != conversation.Revision) break;
                    Choose(world, conversation, choice.Index);
                    if (conversation.Ended) End(world, state);
                    break;
                case EndConversation:
                    End(world, state, followups: false);
                    CancelApproach(world, state);
                    break;
                case ConfirmCharacterTrade confirm:
                    ConfirmTrade(world, state, confirm);
                    break;
                case CloseCharacterTrade close:
                    if (state.Trade is { } trade && command.Issuer == trade.Context.Actor
                        && (command.Issuer is null && close.TradeId == 0 || trade.Id == close.TradeId)) { state.Trade = null; state.TradeView = null; }
                    break;
            }
        }
        if (bubbles.Count > 0) StepBubbles(world);
        RefreshView(world);
    }

    public void SlowWorld(World world)
    {
        StepAreaArrivals(world);
        ExpireSpeech(world);
        foreach (var state in interactions.Values.ToArray()) KeepCharacterTrade(world, state);
        Memory.Age(world);

        if (bubbles.Count > 0) StepBubbles(world);
        if (Settings.AmbientEvents && world.Tick % Settings.AmbientEveryTicks == 0) { SeeOthers(world); SeeGettingUp(world); }
        if (approaches.Count > 0) StepApproaches(world);
        StepFollowers(world);
        StepStalkers(world);
        foreach (var state in interactions.Values.ToArray())
        {
            if (!Valid(world, state)) { CloseForPlayer(world, state.Actor); continue; }
            if (state.Open is { } c && (!Authorized(world, c) || !world.Characters.TryResolveNext(c.Player, out int player) || !Awake(world.Characters, player)
                || !world.Characters.TryResolveNext(c.Npc, out int npc) || !Awake(world.Characters, npc)))
                End(world, state, followups: false);
            if (state.Talker.IsNone || state.Open is not null) continue;
            var table = world.Characters;
            if (!table.TryResolveNext(state.Talker, out int p) || !table.TryResolveNext(state.Target, out int n) || !Awake(table, p) || !Awake(table, n)) CancelApproach(world, state);
            else if (InTalkRange(table, p, n))
            {
                TaskStart.Stand(table, p);
                var (who, whom) = (state.Talker, state.Target);
                CancelApproach(world, state);
                Start(world, whom, who, DialogueEvents.PlayerTalkToMe, state: state);
            }
            else if (world.Tick >= state.ApproachUntil) CancelApproach(world, state);
            else
            {
                Hold(world, n);
                if (world.Tick >= state.NextAim)
                {
                    state.NextAim = world.Tick + (long)MathF.Ceiling(RepathSeconds / world.TickSeconds);
                    ref readonly var tp = ref table.Next[p];
                    var at = table.Next[n].Position;
                    bool walking = tp.Task == (byte)CharacterTask.GoTo && Vector2.Distance(tp.Goal, state.AimedAt) <= RepathDistance;
                    bool stopped = tp.Task == (byte)CharacterTask.Idle;
                    if (!walking && !stopped) CancelApproach(world, state);
                    else if (stopped || Vector2.Distance(state.AimedAt, new Vector2(at.X, at.Z)) > RepathDistance)
                    {
                        state.AimedAt = new Vector2(at.X, at.Z);
                        var context = Context(world, new Conversation { Npc = state.Target, Player = state.Talker, Event = DialogueEvents.PlayerTalkToMe }, state);
                        DeferPlayer(world, context, new MoveOrder([state.Talker], at) { Tick = world.Tick + 1 });
                    }
                }
            }

        }
        RefreshView(world);
    }

    void StartApproach(World world, TalkOrder order, Interaction state)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Talker, out int p) || table.Cold(p) is not { IsPlayer: true } pc
            || !table.TryResolveNext(order.Target, out int n) || table.Cold(n) is not { IsPlayer: false } nc || !Awake(table, n)) return;
        if (data.Db.Find(pc.RecordId)?.GetBool("dumb") == true)
        {
            Post(world, Context(world, new Conversation { Npc = order.Target, Player = order.Talker, Event = DialogueEvents.PlayerTalkToMe }, state), Loc.Tr("{1} can't talk to people", pc.Name));
            return;
        }
        if (data.Relations.Contains(nc.Faction) && data.Relations.Contains(pc.Faction) && data.Relations.IsHostile(nc.Faction, pc.Faction) && !HasInterrupt(order.Target, state.Attributed ? state.Actor : null, state.Faction))
        {
            if (state.Attributed) { world.Post(state.Actor, "This character will not talk right now."); return; }
            var context = Context(world, new Conversation { Npc = order.Target, Player = order.Talker, Event = DialogueEvents.PlayerTalkToMe }, state);
            DeferPlayer(world, context, new Combat.AttackOrder([order.Talker], order.Target) { Tick = world.Tick + 1 });
            return;
        }
        state.Talker = order.Talker; state.Target = order.Target;
        state.ApproachUntil = world.Tick + (long)(Settings.ApproachSeconds / world.TickSeconds);
        Hold(world, n);
        state.NextAim = world.Tick + (long)MathF.Ceiling(RepathSeconds / world.TickSeconds);
        state.AimedAt = new Vector2(table.Next[n].Position.X, table.Next[n].Position.Z);
        if (!InTalkRange(table, p, n))
        {
            var context = Context(world, new Conversation { Npc = order.Target, Player = order.Talker, Event = DialogueEvents.PlayerTalkToMe }, state);
            DeferPlayer(world, context, new MoveOrder([state.Talker], table.Next[n].Position) { Tick = world.Tick + 1 });
        }
    }

    void CancelApproach(World world, Interaction state)
    {
        var target = state.Target;
        state.Talker = state.Target = CharacterId.None;
        if (!target.IsNone && state.Open?.Npc != target) Release(world, target);
    }

    /// <summary>The talk distance beyond the target's radius while walking (7 x 3, <c>AI::isWithinChoppingRange</c> for a character, docs/game/dialogue.md "Player conversation").</summary>
    public const float TalkReach = 21;
    /// <summary>How far the target may move from the walk's goal, and how often that is looked at (the pursuit's constants).</summary>
    const float RepathDistance = 12, RepathSeconds = 0.5f;
    /// <summary>The largest height difference to talk across (the original's on the same floor; engine choice, the sim has no floors here).</summary>
    public const float TalkHeight = 35;

    /// <summary>
    /// Whether <paramref name="talker"/> is near enough <paramref name="target"/> to talk: X/Z within the target's scaled radius (footprint x body scale) + <see cref="TalkReach"/>, twice the reach once
    /// the talker has stopped, and within <see cref="TalkHeight"/> in height.
    /// </summary>
    static bool InTalkRange(CharacterTable table, int talker, int target)
    {
        var (a, b) = (table.Next[talker].Position, table.Next[target].Position);
        float reach = Combat.CombatSteering.Radius(table.Cold(target)) + TalkReach * ((table.Next[talker].Flags & (ushort)MoveFlags.AnyPath) == 0 ? 2 : 1);

        return Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) < reach && MathF.Abs(a.Y - b.Y) < TalkHeight;
    }

    static bool Awake(CharacterTable table, int slot) => table.Current[slot].Alive && table.Cold(slot) is { IsImprisoned: false } c && c.Medical?.Incapacitated != true;

    /// <summary>Keeps the NPC standing while the talker comes and talks (as the trader in a trade); released when it ends.</summary>
    static void Hold(World world, int slot)
    {
        var table = world.Characters;
        table.Cold(slot)!.Engaged = true;
        if (table.Next[slot].Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Idle) TaskStart.Stand(table, slot);
    }

    static void Release(World world, CharacterId id)
    {
        if (world.Characters.TryResolveNext(id, out int slot) && world.Characters.Cold(slot) is { } c
            && world.SystemOf<DialogueSystem>()?.HoldsNpc(id) != true && world.SystemOf<Trading.TradeSystem>()?.HoldsNpc(id) != true) c.Engaged = false;
    }

    /// <summary>The farthest the talking player character may be from the camera for a player conversation to start (<c>FUN_140684320</c>, constant 9216).</summary>
    public const float CameraReach = 9216;

    /// <summary>Whether the talker is more than <see cref="CameraReach"/> from the camera focus (no focus known: never; headless runs and tests have none).</summary>
    bool TooFarFromCamera(CharacterTable table, CharacterId talker) =>
        population?.Focus is { } focus && table.TryResolveNext(talker, out int p) && Vector3.Distance(table.Next[p].Position, focus) > CameraReach;

    /// <summary>Fires <paramref name="ev"/> at <paramref name="npc"/> with <paramref name="player"/> as its target and opens the conversation (docs/game/dialogue.md "Player conversation").</summary>
    void Start(World world, CharacterId npc, CharacterId player, int ev, bool quiet = false, Interaction? state = null)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(npc, out int n) || table.Cold(n) is not { } nc) return;
        if (world.Players.RequiresExplicitControl && world.Players.ControlOf(player) is null) return;
        if (!world.Players.RequiresExplicitControl && ev == DialogueEvents.PlayerTalkToMe && TooFarFromCamera(table, player)) return;
        bool supplied = state is not null;
        state ??= InteractionFor(world.Players.ControlOf(player)?.Player ?? PlayerId.Local);
        if (!NpcAvailable(world, npc, state) || state.Open is not null) return;
        if (!supplied || state.Id == 0) state.Id = nextInteraction = checked(nextInteraction + 1);
        var c = Context(world, new Conversation { Npc = npc, Player = player, Event = ev }, supplied ? state : null);
        c.Interaction = state;
        state.Attributed = c.Actor is not null; state.Control = c.Control;
        state.Faction = c.Faction; state.MembershipRevision = c.MembershipRevision;
        IReadOnlyList<DialogueNode> roots = TakeInterrupt(npc, c.Actor, c.Faction, out var pending) && dialogue.Node(pending) is { IsRoot: true } interrupt ? [interrupt] : SetOf(world, n).For(ev);

        var picked = Pick(world, c, roots);
        if (picked is not { } p)
        {
            if (!quiet) Post(world, c, $"{nc.Name} has nothing to say.");
            return;
        }
        state.Open = c;
        Hold(world, n);
        if (TaskStart.OnAiWalk(table, n)) TaskStart.Stand(table, n);   // a walking NPC stops to talk (engine choice); the approach chased it
        c.Root = p.Root;
        if (IsNpcSide(p.Line.Speaker)) Say(world, c, p.Line, p.Speaker);
        else Offer(world, c, p.Root ?? p.Line);
        if (c.Ended) End(world, state);
    }

    /// <summary>The character's dialogue (its record, its squad's template and its role, docs/game/dialogue.md "Who has which dialogue").</summary>
    internal DialogueSet SetOf(World world, int slot)
    {
        var cold = world.Characters.Cold(slot)!;
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        bool animal = cold.Fighter?.IsAnimal ?? cold.Appearance?.Animal is not null;
        string? playerFaction = data.Relations.Contains(cold.Faction) ? data.FactionTemplate(cold.Faction) : null;
        return dialogue.SetFor(cold.RecordId, squad?.TemplateId, squad is not null && squad.Leader == world.Characters.IdOf(slot), cold.IsPlayer, animal, playerFaction);
    }

    /// <summary>
    /// The character offers Trade: its squad has a shop, or has no home building and a template with <c>vendors</c> (a Caravan, stocked at its first trade), and its record says
    /// <c>is trader</c> or its dialogue reaches a Trade line (docs/game/economy.md "Who trades"); the guards and other members of a trader squad do not.
    /// </summary>
    public bool Trades(World world, int slot) =>
        world.Characters.Cold(slot) is { IsPlayer: false, SquadId: >= 0 } cold && world.Squads.Find(cold.SquadId) is { } squad
        && (squad.Shop is not null || squad.HomeBuilding is null && data.Db.Find(squad.TemplateId)?.GetReferences("vendors").Count > 0)
        && (data.Db.Find(cold.RecordId)?.GetBool("is trader") == true || SetOf(world, slot).Trades);

    public bool CanTalk(World world, int slot, CharacterId? talker = null)
    {
        if (world.Characters.Cold(slot) is not { IsPlayer: false } || !Awake(world.Characters, slot) ||
            talker is { } speaking && (!world.Characters.TryResolveNext(speaking, out int speakerSlot) || !Awake(world.Characters, speakerSlot))) return false;
        var npc = world.Characters.IdOf(slot);
        if (world.Players.RequiresExplicitControl && world.Players.ControlOf(npc) is not null) return false;
        var grant = talker is { } who ? world.Players.ControlOf(who) : null;
        PlayerId? actor = grant?.Player;
        if (world.Players.RequiresExplicitControl && talker is not null && actor is null) return false;
        if (actor is { } owner && (IsServingOther(npc, owner) || world.SystemOf<Trading.TradeSystem>()?.IsServingOther(npc, owner) == true)) return false;
        if (HasInterrupt(npc, actor, grant?.Faction)) return true;
        int npcFaction = world.Characters.Cold(slot)!.Faction;
        if (grant is { } controlled && data.Relations.Contains(npcFaction) && data.Relations.Contains(controlled.Faction) &&
            data.Relations.IsHostile(npcFaction, controlled.Faction)) return false;
        var roots = SetOf(world, slot).For(DialogueEvents.PlayerTalkToMe);
        if (roots.Count == 0) return false;
        if (talker is not { } player) return true;
        // A dry run of Start's pick (it rolls dice and memoizes permanent chances: both put back), so the menu never offers a Talk that ends in "has nothing to say".
        var (savedRolls, known) = (rolls, permanent.Keys.ToList());
        try { return Pick(world, Context(world, new Conversation { Npc = npc, Player = player, Event = DialogueEvents.PlayerTalkToMe, DryRun = true }), roots) is not null; }
        finally
        {
            rolls = savedRolls;
            foreach (var key in permanent.Keys.Except(known).ToList()) permanent.Remove(key);
        }
    }

    static bool IsNpcSide(Talker t) => t is Talker.Me or Talker.Interjector1 or Talker.Interjector2 or Talker.Interjector3 or Talker.WholeSquad;

    /// <summary>The NPC side says <paramref name="line"/> (docs/game/dialogue.md "Saying a line"): a text, marked used, its effects at once, then the choices.</summary>
    void Say(World world, Conversation c, DialogueNode line, CharacterId speaker)
    {
        c.Line = line;
        c.Speaker = speaker;
        MarkUsed(world, c, line);
        string text = Said(Expand(world, c, line, PickText(world, speaker, line), speaker, c.Player));
        text += MoneySuffix(line);
        c.Text = text;
        c.Log.Add($"{NameOf(world, speaker)}: {text}");
        LogLine(world, c, speaker, NameOf(world, speaker), text, false);
        RunEffects(world, c, line, speaker);
        if (c.Ended) return;
        Offer(world, c, line);
    }

    /// <summary>The choices after <paramref name="line"/> (docs/game/dialogue.md "Choices"): the passing children in order; an NPC-side one makes a single <c>[...]</c>.</summary>
    void Offer(World world, Conversation c, DialogueNode line)
    {
        c.Revision++;
        c.Choices.Clear();
        foreach (var child in line.Children)
        {
            if (!SpeakerOf(world, c, child, out var speaker)) continue;
            var me = IsNpcSide(child.Speaker) ? speaker : c.Npc;
            if (!Check(world, c, child, me, c.Player)) continue;
            if (IsNpcSide(child.Speaker))
            {
                c.Choices.Clear();
                c.Choices.Add((null, "[...]"));
                return;
            }
            string label = Said(Expand(world, c, child, PickText(world, c.Player, child), c.Player, c.Npc)) + MoneySuffix(child);
            if (label.Length > 0) c.Choices.Add((child, label));   // a choice with no text and no cost is never offered (FUN_140680130)
        }
        if (c.Choices.Count == 0) c.Choices.Add((null, LeaveChoice));
    }

    void Choose(World world, Conversation c, int index)
    {
        if ((uint)index >= (uint)c.Choices.Count || !Authorized(world, c)) return;
        c.Revision++;
        if (c.Interaction?.BailList is not null) { ChooseBail(world, c, index); return; }
        var (reply, text) = c.Choices[index];
        DialogueNode? from = c.Line;
        if (reply is not null)
        {
            if (!PrepareEffects(world, c, reply, c.Npc)) return;
            c.Log.Add($"{NameOf(world, c.Player)}: {text}");
            LogLine(world, c, c.Player, NameOf(world, c.Player) is { Length: > 0 } name ? name : "Player", text, true);
            MarkUsed(world, c, reply);
            RunEffects(world, c, reply, c.Npc);
            if (c.Ended) return;
            from = reply;
        }
        else if (text == LeaveChoice || from is null) { c.Ended = true; return; }
        var next = Pick(world, c, from.Children);
        if (next is not { } p) { c.Ended = true; return; }
        if (IsNpcSide(p.Line.Speaker)) Say(world, c, p.Line, p.Speaker);
        else
        {
            c.Choices.Clear();
            string label = Said(Expand(world, c, p.Line, PickText(world, c.Player, p.Line), c.Player, c.Npc)) + MoneySuffix(p.Line);
            if (label.Length == 0) c.Ended = true;   // nothing to offer: the conversation ends
            else c.Choices.Add((p.Line, label));
        }
    }

    /// <summary>Ends the open conversation (<c>FUN_140674830</c>): the event counter and its time, the speaker squad's memory of the partner, the NPC released.</summary>
    void End(World world, Interaction state, bool followups = true)
    {
        if (state.Open is not { } c) return;
        state.Open = null;
        state.View = null;
        if (state.BailList is not null)
        {
            state.BailList = null; state.BailAfter = null; state.TradeAfter = null;
            Release(world, c.Npc); return;
        }   // the bail list closing: the conversation was counted already
        var key = (c.Npc, c.Event);
        var (count, _) = counters.GetValueOrDefault(key);
        counters[key] = (count + 1, world.GameHours);
        // A player conversation (event 1) leaves the speaker's squad remembering the partner (FUN_140674830; other events do not).
        if (c.Event == DialogueEvents.PlayerTalkToMe && world.Characters.TryResolveNext(c.Npc, out int n) && world.Characters.Cold(n) is { SquadId: >= 0 } nc)
            Memory.Remember(world, nc.SquadId, c.Player, MemoryTag.None);
        // An arrest the conversation started (DA 29, 30) keeps the guard held.
        if (Arrests?.Arrests.Any(a => a.Guard == c.Npc) != true) Release(world, c.Npc);
        if (followups) { OpenBail(world, state); OpenCharacterTrade(world, state); }
        else { state.BailAfter = null; state.TradeAfter = null; }
    }

    void MarkUsed(World world, Conversation c, DialogueNode line)
    {
        lastUse[line.StringId] = world.GameHours;
        if (c.Root is { } root) lastUse[root.StringId] = world.GameHours;
    }

    ulong NextRoll(World world, CharacterId who) => Rng.Hash(world.Seed, Rng.Key(who), RngPurpose.Dialogue, rolls++);

    string PickText(World world, CharacterId who, DialogueNode line) =>
        line.Texts.Count switch { 0 => "", 1 => line.Texts[0], var n => line.Texts[Rng.Int(NextRoll(world, who), n)] };

    /// <summary>
    /// The cost a line's text shows (<c>FUN_140675320</c>, for a said line and a choice button alike): " [c.N]" once for every DA_TAKE_MONEY effect, N its own val0 with thousands
    /// separators, in effect order. DA_GIVE_MONEY adds nothing.
    /// </summary>
    static string MoneySuffix(DialogueNode line)
    {
        var sb = new StringBuilder();
        foreach (var e in line.Effects)
            if (e.Action == DialogueActions.TakeMoney) sb.Append(" [c.").Append(e.Value.ToString("N0", CultureInfo.InvariantCulture)).Append(']');
        return sb.ToString();
    }

    string NameOf(World world, CharacterId id) => world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s) is { } c ? c.Name : "";

    internal void RefreshView(World world)
    {
        foreach (var state in interactions.Values)
        {
            RefreshCharacterTrade(world, state);
            if (state.Open is not { } c) { state.View = null; continue; }
            bool visible = c.Actor is null || world.Players.Find(c.Actor.Value)?.HasPermission(c.Faction, FactionPermission.ReadFunds) == true;
            state.View = new ConversationView(c.Npc, c.Player, c.Speaker, NameOf(world, c.Speaker.IsNone ? c.Npc : c.Speaker), NameOf(world, c.Player), c.Text,
                [.. c.Choices.Select(Shown)], [.. c.Log], visible ? Funds(world, c)?.Money ?? 0 : 0)
                { SessionId = state.Id, ChoiceRevision = c.Revision, FundsVisible = visible };
        }
    }

    internal const string LeaveChoice = "[Leave]";
    static string Shown((DialogueNode? Reply, string Text) choice) => choice.Reply is null && choice.Text == LeaveChoice ? Loc.Tr(LeaveChoice) : choice.Text;

    public ConversationView? PublishConversation() => Local.View;
    public ConversationView? PublishConversation(PlayerId player) => interactions.GetValueOrDefault(player.Value)?.View;
    public DialogueRequest? PublishRequest() => Local.Request;
    public DialogueRequest? PublishRequest(PlayerId player) => interactions.GetValueOrDefault(player.Value)?.Request;

    public void Hash(ref StateHasher hasher)
    {
        HashAreaArrivals(ref hasher);
        HashInteractions(ref hasher);
        var (open, talker, target) = (Local.Open, Local.Talker, Local.Target);
        if (lastUse.Count == 0 && permanent.Count == 0 && counters.Count == 0 && Memory.IsEmpty && interrupts.Count == 0 && playerInterrupts.Count == 0 && bubbles.Count == 0 && ambientAt.Count == 0 && approaches.Count == 0 && down.Count == 0 && quiet.Count == 0 && followers.Count == 0 && stalkers.Count == 0 && open is null && talker.IsNone) return;

        hasher.Add(0x44696167);
        hasher.Add(rolls);
        foreach (var (line, hours) in lastUse) { hasher.Add(Rng.StableHash(line)); hasher.Add(hours); }
        foreach (var ((who, line), passed) in permanent) { hasher.Add(who); hasher.Add(Rng.StableHash(line)); hasher.Add(passed); }
        foreach (var ((who, ev), (count, last)) in counters) { hasher.Add(who); hasher.Add(ev); hasher.Add(count); hasher.Add(last); }
        Memory.Hash(ref hasher);
        HashActs(ref hasher);
        foreach (var (who, line) in interrupts) { hasher.Add(who); hasher.Add(Rng.StableHash(line)); }
        foreach (var ((actor, npc), factions) in playerInterrupts)
            foreach (var (faction, line) in factions)
            { hasher.Add(0x496E7450); hasher.Add(actor); hasher.Add(npc); hasher.Add(faction); hasher.Add(Rng.StableHash(line)); }
        foreach (var b in bubbles) { hasher.Add(b.C.Npc); hasher.Add(Rng.StableHash(b.Line.StringId)); hasher.Add(b.DueTick); }
        foreach (var ((squad, whom), hours) in ambientAt) { hasher.Add(squad); hasher.Add(whom); hasher.Add(hours); }
        HashApproaches(ref hasher);
        foreach (var id in down) hasher.Add(id);
        foreach (var (npc, pc) in quiet) { hasher.Add(npc); hasher.Add(pc); }
        hasher.Add(talker);
        hasher.Add(target);
        if (open is { } c)
        {
            hasher.Add(c.Npc);
            hasher.Add(c.Player);
            hasher.Add(c.Line is { } l ? Rng.StableHash(l.StringId) : 0UL);
            hasher.Add(c.Choices.Count);
        }
    }
}
