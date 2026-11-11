using System.Globalization;
using Meitou.Data.Gameplay.Dialogue;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The dialogue system's bail list, and the events and queries other systems use.
/// </summary>
public sealed partial class DialogueSystem
{
    #region Bail list

    /// <summary>
    /// DA_CHOOSE_PRISONER_BAIL.
    /// </summary>
    const int ChoosePrisonerBail = 43;

    /// <summary>
    /// The bail list (DA 43 <c>CHOOSE_PRISONER_BAIL</c>, the original's <c>CharacterTrading_PrisonerBailout</c> window, docs/game/economy.md 11.5):
    /// the conversation window again, the speaker saying "Pay Bounty", one choice per prisoner it offers
    /// (<see cref="Crime.ArrestSystem.BailOffers"/>) with its price, and <c>[Leave]</c>. Engine stand-in for the trading window.
    /// </summary>
    void OpenBail(World world, Interaction state)
    {
        if (state.BailAfter is not { } b || Arrests is null) return;
        state.BailAfter = null;
        var table = world.Characters;
        if (!table.TryResolveNext(b.Npc, out int n) || !Awake(table, n)) return;
        state.Id = _nextInteraction = checked(_nextInteraction + 1);
        state.Attributed = b.Context.Actor is not null; state.Control = b.Context.Control;
        state.Faction = b.Context.Faction; state.MembershipRevision = b.Context.MembershipRevision;
        var offers = Arrests.BailOffers(world, b.Npc, state.Faction);
        var c = Context(world, new Conversation { Npc = b.Npc, Player = b.Player, Event = DialogueEvents.PlayerTalkToMe, Speaker = b.Npc, Text = Loc.Tr("Pay Bounty"), Revision = 1 }, state);
        foreach (var (_, name, price) in offers) c.Choices.Add((null, $"{name} (c.{price.ToString("N0", CultureInfo.InvariantCulture)})"));
        c.Choices.Add((null, "[Leave]"));
        if (offers.Count == 0) Post(world, c, Loc.Tr("Nobody here to bail out."));
        state.BailList = [.. offers.Select(o => o.Prisoner)];
        state.Open = c;

        Hold(world, n);
    }

    /// <summary>
    /// A click in the bail list: the prisoner's bail paid by the talker's faction
    /// (<see cref="Crime.ArrestSystem.PayBail(World, CharacterId, CharacterId)"/>), then the window closes.
    /// </summary>
    void ChooseBail(World world, Conversation c, int index)
    {
        if (c.Interaction?.BailList is { } list && (uint)index < (uint)list.Count && CanSpend(world, c)) Arrests?.PayBail(world, list[index], c.Player);
        c.Ended = true;
    }

    #endregion

    #region Events and queries for other systems

    /// <summary>
    /// Fires <paramref name="ev"/> at <paramref name="npc"/> now, with <paramref name="target"/> as its partner (<c>FUN_140684990</c>; the police's
    /// spotting, events 44 and 45): a line of its list for the event said as a speech bubble, its effects at once. Returns whether a line was said;
    /// false while the event lines are off (<see cref="DialogueSettings.AmbientEvents"/>).
    /// </summary>
    public bool FireEvent(World world, CharacterId npc, CharacterId target, int ev) => Settings.AmbientEvents && Fire(world, npc, target, ev);

    /// <summary>
    /// Whether <paramref name="npc"/> is saying a line or is in the open conversation.
    /// </summary>
    public bool Engages(CharacterId npc) => _interactions.Values.Any(s => s.Open?.Npc == npc) || BubbleOf(npc);

    #endregion
}