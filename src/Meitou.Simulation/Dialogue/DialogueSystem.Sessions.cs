using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Player;
using CharacterTrade = Meitou.Simulation.Dialogue.CharacterTrading.CharacterTrade;
using Conversation = Meitou.Simulation.Dialogue.DialogueSystem.Conversation;
using Interaction = Meitou.Simulation.Dialogue.DialogueSystem.Interaction;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    internal sealed class Interaction(PlayerId actor)
    {
        public PlayerId Actor { get; } = actor;
        public ulong Id;
        public bool Attributed;
        public CharacterControl? Control;
        public ulong MembershipRevision;
        public int Faction = -1;
        public CharacterId Talker = CharacterId.None, Target = CharacterId.None;

        public Conversation? Open;
        public ConversationView? View;
        public DialogueRequest? Request;
        public (CharacterId Npc, CharacterId Player, Conversation Context)? BailAfter;
        public List<CharacterId>? BailList;
        public (CharacterTradeKind Kind, CharacterId Slaver, CharacterId Player, int Price, Conversation Context)? TradeAfter;
        public CharacterTrade? Trade;
        public CharacterTradeView? TradeView;

        public WalkUp? Walk;
    }

    readonly SortedDictionary<ulong, Interaction> _interactions = new() { [1] = new Interaction(PlayerId.Local) };
    Interaction Local => _interactions[PlayerId.Local.Value];
    ulong _nextInteraction;

    internal Interaction InteractionFor(PlayerId actor)
    {
        if (!_interactions.TryGetValue(actor.Value, out var state)) _interactions.Add(actor.Value, state = new Interaction(actor));
        return state;
    }

    bool Valid(World world, Interaction state) => !state.Attributed || state.Control is { } grant && world.Players.Validate(grant)
        && world.Players.Find(state.Actor)?.MembershipRevision(state.Faction) == state.MembershipRevision;

    Interaction? Publishable(World world, PlayerId player) => _interactions.GetValueOrDefault(player.Value) is { Attributed: true } state
        && Valid(world, state) && (state.Open is null || Authorized(world, state.Open)) ? state : null;
    public ConversationView? PublishConversation(World world, PlayerId player) => Publishable(world, player)?.View;
    public DialogueRequest? PublishRequest(World world, PlayerId player) => Publishable(world, player)?.Request;
    public CharacterTradeView? PublishCharacterTrade(World world, PlayerId player)
    {
        if (Publishable(world, player) is not { Trade: { } trade } state || !Authorized(world, trade.Context)) return null;
        RefreshCharacterTrade(world, state);
        return state.TradeView;
    }

    internal bool HoldsNpc(CharacterId npc) => _interactions.Values.Any(s => s.Open?.Npc == npc || s.Target == npc);
    internal bool IsServingOther(CharacterId npc, PlayerId actor) => _interactions.Values.Any(s => s.Actor != actor && (s.Open?.Npc == npc || s.Target == npc));
    public bool IsBusy(World world, CharacterId npc, PlayerId actor) => IsServingOther(npc, actor) || world.SystemOf<Trading.TradeSystem>()?.IsServingOther(npc, actor) == true;

    internal bool NpcAvailable(World world, CharacterId npc, Interaction state) =>
        !_interactions.Values.Any(s => s != state && (s.Open?.Npc == npc || s.Target == npc))
        && world.SystemOf<Trading.TradeSystem>()?.IsServingOther(npc, state.Actor) != true;

    Conversation Context(World world, Conversation c, Interaction? state = null)
    {
        c.Interaction = state;
        if (state is not null)
        {
            c.Actor = state.Attributed ? state.Actor : null; c.Faction = state.Faction;
            c.Control = state.Attributed ? state.Control : null; c.MembershipRevision = state.MembershipRevision;
        }
        else if (world.Players.RequiresExplicitControl && (world.Players.ControlOf(c.Player) ?? world.Players.ControlOf(c.Npc)) is { } grant)
        {
            c.Actor = grant.Player; c.Faction = grant.Faction; c.Control = grant;
            c.MembershipRevision = world.Players.Find(grant.Player)!.MembershipRevision(grant.Faction);
        }
        else c.Faction = world.Players.RequiresExplicitControl ? -1 : world.Player.Faction;
        return c;
    }

    Interaction InteractionOf(Conversation c) => c.Interaction ?? InteractionFor(c.Actor ?? PlayerId.Local);
    internal FactionState? Funds(World world, Conversation c) => c.Faction >= 0 ? world.Factions.GetOrCreate(c.Faction)
        : !world.Players.RequiresExplicitControl ? world.Player.FactionState : null;
    PlayerState? Player(World world, Conversation c) => world.Players.Find(c.Actor ?? PlayerId.Local)?.State;
    internal bool Authorized(World world, Conversation c) => c.Actor is null || c.Control is { } grant && world.Players.Validate(grant)
        && world.Players.Find(c.Actor.Value)?.MembershipRevision(c.Faction) == c.MembershipRevision
        && (c.Interaction is null || world.Characters.TryResolveNext(c.Player, out int player) && Awake(world.Characters, player)
            && world.Characters.TryResolveNext(c.Npc, out int npc) && Awake(world.Characters, npc) && InTalkRange(world.Characters, player, npc)
            && (world.Players.ControlOf(c.Npc) is not { } npcControl || npcControl.Player == c.Actor));
    internal bool CanSpend(World world, Conversation c) => Authorized(world, c) && (c.Actor is null || world.Players.Find(c.Actor.Value)?.HasPermission(c.Faction, FactionPermission.SpendFunds) == true);
    internal bool CanManage(World world, Conversation c) => Authorized(world, c) && (c.Actor is null || world.Players.Find(c.Actor.Value)?.HasPermission(c.Faction, FactionPermission.ManageFaction) == true);
    internal bool CanReadFunds(World world, Conversation c) => c.Actor is null || world.Players.Find(c.Actor.Value)?.HasPermission(c.Faction, FactionPermission.ReadFunds) == true;
    int FactionRank(World world, Conversation c, int other) => world.Players.RequiresExplicitControl ? Funds(world, c)?.Rank(other) ?? 0 : _data.Relations.Rank(other);
    void RaiseFactionRank(World world, Conversation c, int other, int cap)
    {
        if (world.Players.RequiresExplicitControl) Funds(world, c)?.RaiseRank(other, cap);
        else _data.Relations.RaiseRank(other, cap);
    }
    internal void Post(World world, Conversation c, string text)
    {
        if (c.Actor is { } actor) world.Post(actor, text);
        else world.Post(text);
    }

    void LogLine(World world, Conversation c, CharacterId speaker, string owner, string text, bool player)
    {
        if (c.Actor is { } actor) world.Said(actor, owner, text, player);
        else world.SaidPublic(speaker, owner, text, player);
    }

    void DeferPlayer(World world, Conversation c, SimCommand command)
    {
        if (c.Actor is { } actor && c.Control is { } grant)
            command = command with { Issuer = actor, ControlGrants = [grant], FactionGrantRevision = c.MembershipRevision };
        world.Defer(command);
    }

    public void CloseForPlayer(World world, PlayerId player)
    {
        if (!_interactions.TryGetValue(player.Value, out var state)) return;
        End(world, state, followups: false);
        CancelApproach(world, state);
        state.Trade = null; state.TradeView = null; state.TradeAfter = null; state.Request = null;
        state.BailAfter = null; state.BailList = null;
        if (player != PlayerId.Local) _interactions.Remove(player.Value);
    }

    void HashInteractions(ref StateHasher h)
    {
        if (_interactions.Count == 1 && !Local.Attributed) return;
        h.Add(0x446C6750); h.Add(_nextInteraction); h.Add(_interactions.Count);
        foreach (var s in _interactions.Values)
        {
            h.Add(s.Actor.Value); h.Add(s.Id); h.Add(s.Attributed); h.Add(s.Faction); h.Add(s.MembershipRevision);
            h.Add(s.Talker); h.Add(s.Target);
            s.Walk?.Hash(ref h);
            if (s.Control is { } grant) { h.Add(grant.Entity.Value); h.Add(grant.Revision); }
            h.Add(s.Open is not null);
            if (s.Open is { } c)
            {
                h.Add(c.Npc); h.Add(c.Player); h.Add(c.Faction); h.Add(c.Revision);
                h.Add(c.Line is { } line ? Rng.StableHash(line.StringId) : 0UL); h.Add(c.Choices.Count);
            }
            h.Add(s.Trade is not null);
            if (s.Trade is { } t)
            {
                h.Add(t.Id); h.Add((int)t.Kind); h.Add(t.Slaver); h.Add(t.Player); h.Add(t.Rows.Count);
                foreach (var row in t.Rows) { h.Add(row.Character); h.Add(row.Price); }
            }
            if (s.BailList is { } bail) { h.Add(bail.Count); foreach (var id in bail) h.Add(id); }
        }
    }
}