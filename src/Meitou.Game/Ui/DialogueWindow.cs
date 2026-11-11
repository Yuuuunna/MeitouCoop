using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation.Dialogue;

namespace Meitou.Game.Ui;

/// <summary>
/// Conversations (docs/game/dialogue.md "UI", docs/simulation.md "Dialogue"), the same in both UI modes: while the world publishes a conversation the
/// original's conversation panel (<c>Kenshi_ConversationPanel</c>) is open, modal, so the game is paused as the original pauses it. The NPC panel
/// shows the speaker's name and line, the player panel the talker's name, the money and the choices (colours <c>DialogueTextOption</c> #b3b3b3,
/// <c>DialogueTextOptionHover</c> #00ff00 from <c>kenshi_colours.xml</c>); a click on a choice says it, Esc or closing leaves the conversation. The
/// portraits are the speaker's and the player character's, drawn into the last two cells of the portrait atlas.
/// </summary>
sealed class DialogueWindow(PlayerInterface player)
{
    /// <summary>
    /// The conversation session whose window is open.
    /// </summary>
    ulong _conversationSession;
    /// <summary>
    /// The exact choices last displayed, including their session and choice revision.
    /// </summary>
    ConversationView? _displayedConversation;

    /// <summary>
    /// The choices' text colour and their hover colour.
    /// </summary>
    static readonly Vector4 ChoiceColour = new(0xb3 / 255f, 0xb3 / 255f, 0xb3 / 255f, 1), ChoiceHover = new(0, 1, 0, 1);

    /// <summary>
    /// The conversation window is shown, and its end was sent.
    /// </summary>
    internal bool ConversationShown, ConversationEndSent;

    /// <summary>
    /// The id of the last dialogue request handled.
    /// </summary>
    long _dialogueRequestHandled;

    /// <summary>
    /// The NPC line shown; a new one scrolls the box back to the top.
    /// </summary>
    string? _dialogueText;

    /// <summary>
    /// Opens the panel when a conversation opens, closes it when it ends, ends the conversation when the player closed the panel; runs a finished
    /// conversation's request.
    /// </summary>
    internal void SyncDialogueScreen()
    {
        if (player.FaithfulScreens is null) return;

        var snapshot = player.Session.CurrentSnapshot;
        var talk = snapshot.Conversation;
        if (talk is null)
        {
            if (ConversationShown && player.FaithfulScreens.IsOpen("dialogue")) player.FaithfulScreens.Close("dialogue");
            if (ConversationShown) player.UpdateMenuPause();
            ConversationShown = ConversationEndSent = false;
            _conversationSession = 0;
            _displayedConversation = null;
        }
        else if (!ConversationShown || _conversationSession != talk.SessionId)
        {
            ConversationShown = true;
            _conversationSession = talk.SessionId;
            _displayedConversation = null;
            ConversationEndSent = false;
            player.ContextMenuWindow.CloseContextMenu();
            player.FaithfulScreens.Open("dialogue");
            player.UpdateMenuPause();
        }
        else if (!player.FaithfulScreens.IsOpen("dialogue") && !ConversationEndSent)
        {
            player.Say(new EndConversation { Tick = player.Session.CommandTick, SessionId = talk.SessionId });
            ConversationEndSent = true;
            player.UpdateMenuPause();
        }

        if (snapshot.DialogueRequest is { } request && request.Id > _dialogueRequestHandled)
        {
            _dialogueRequestHandled = request.Id;
            if (request.Kind == DialogueRequestKind.CharacterEditor)
                player.CharacterEditor.OpenEditor(Simulation.Editing.EditKind.Surgery, [request.Player], request.Npc, paid: true, editSession: request.EditSession);
            else if (request.Kind == DialogueRequestKind.RecruitEditor)
                player.CharacterEditor.OpenEditor(Simulation.Editing.EditKind.Recruit, [request.Player], request.Npc, editSession: request.EditSession);
            else if (request.Kind == DialogueRequestKind.AnimalEditor)
                player.CharacterEditor.OpenEditor(Simulation.Editing.EditKind.Recruit, request.Characters, request.Npc, editSession: request.EditSession, editSessions: request.EditSessions);
        }
    }

    /// <summary>
    /// The dialogue window's bindings: the names, the NPC line (scrolled to the top when new) and the choices.
    /// </summary>
    internal void BindDialogueScreen(UiBindings b)
    {
        if (player.Session.CurrentSnapshot.Conversation is not { } talk) return;

        b.Text["NpcName"] = talk.SpeakerName;
        b.Text["PlayerName"] = talk.PlayerName;
        b.Text["NpcDialog"] = talk.Text;
        b.Properties["NpcDialog"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Left Top" };

        // A line longer than the NPC box scrolls under the EditBox's own bar (the original grows the frame instead); each new line starts at the top.
        if (talk.Text != _dialogueText)
        {
            _dialogueText = talk.Text;
            player.FaithfulScreens.SetScroll("dialogue", "NpcDialog", 0);
        }

        // The choices are drawn as buttons, so the player box's template scroll bar would only be an empty track.
        var playerBox = player.FaithfulScreens.Layout("dialogue").Find("PlayerDialog");
        foreach (var bar in playerBox?.Children.Where(w => w.Type == "ScrollBar") ?? [])
            b.HiddenWidgets.Add(bar);
        b.NativeScroll.Add("PlayerDialog");   // the choices are drawn against the box, not scrolled by its canvas
        b.Text["MoneyLabelText"] = Loc.Tr("Money:");
        b.Text["MoneyAmountText"] = talk.FundsVisible ? "c." + talk.PlayerMoney.ToString("N0", CultureInfo.InvariantCulture) : Loc.Tr("Unavailable");
    }

    /// <summary>
    /// The choices, one line each down the player panel's dialog area.
    /// </summary>
    internal void AddDialogueChoices(List<UiControl> controls)
    {
        player.PortraitBar.AddDialoguePortraits(controls);
        if (player.Session.CurrentSnapshot.Conversation is not { } talk || controls.FirstOrDefault(c => c.Name == "PlayerDialog") is not { } area)
            return;
        _displayedConversation = talk;

        float line = MathF.Max(16, area.Bounds.Height / 7);
        for (int i = 0; i < talk.Choices.Count; i++)
        {
            var bounds = new UiRect(area.Bounds.X, area.Bounds.Y + i * line, area.Bounds.Width, line);
            controls.Add(new($"dialogue-choice-{i}", "Button", bounds, $"{i + 1}. {talk.Choices[i]}", $"dialogue-choice:{i}:{talk.SessionId}:{talk.ChoiceRevision}", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" }, Vector4.Zero,
                TextColour: ChoiceColour, HoverColour: ChoiceHover, Pickable: true, Clip: area.Clip ?? area.Bounds));
        }
    }

    /// <summary>
    /// A choice picked: it goes to the world.
    /// </summary>
    internal void DialogueAction(MyGuiEvent e)
    {
        var parts = e.Action.Split(':');
        if (parts.Length == 4 && parts[0] == "dialogue-choice" && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int index)
            && ulong.TryParse(parts[2], CultureInfo.InvariantCulture, out ulong window) && ulong.TryParse(parts[3], CultureInfo.InvariantCulture, out ulong revision))
            player.Say(new DialogueChoice(index) { Tick = player.Session.CommandTick, SessionId = window, ChoiceRevision = revision });
    }

    /// <summary>
    /// The number keys pick a choice while the panel is open (engine convenience).
    /// </summary>
    internal bool DialogueKey(Host.Input.Key key)
    {
        if (!player.FaithfulScreens.IsOpen("dialogue") || _displayedConversation is not { } talk) return false;

        int index = key - Host.Input.Key.D1;
        if (index < 0 || index > 8) return false;
        if (index < talk.Choices.Count) player.Say(new DialogueChoice(index) { Tick = player.Session.CommandTick, SessionId = talk.SessionId, ChoiceRevision = talk.ChoiceRevision });
        return true;
    }
}