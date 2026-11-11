using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation.Trading;

namespace Meitou.Game.Ui;

/// <summary>
/// The player-to-player trade panel; every mutation names the displayed session and quote revision.
/// </summary>
sealed class PlayerBarter(PlayerInterface player)
{
    /// <summary>
    /// The own-item page, other-item page and selected paying faction index.
    /// </summary>
    int _playerTradePage, _playerTradeOtherPage, _playerTradeFaction;

    /// <summary>
    /// The barter session whose paging state is displayed.
    /// </summary>
    ulong _playerTradeShown;

    /// <summary>
    /// The item rows on each side of a barter page.
    /// </summary>
    const int PlayerTradeRows = 8;

    /// <summary>Shared controls for either renderer; only authoritative revisions are submitted.</summary>
    internal void AddControls(List<UiControl> controls)
    {
        if (player.Session.CurrentSnapshot.PlayerTrade is not { } trade) return;
        if (_playerTradeShown != trade.SessionId)

        {
            _playerTradeShown = trade.SessionId;
            _playerTradePage = _playerTradeOtherPage = _playerTradeFaction = 0;
        }
        float scale = Math.Clamp(player.Height / 900f, .65f, 1.5f);
        float width = 720 * scale, left = Math.Max(0, (player.Width - width) / 2), top = 50 * scale;
        var textProperties = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" };
        void Row(string id, string text, float x, float y, float w, bool button = false, bool enabled = true)
        {
            controls.Add(new("player-trade-" + id, button ? "Button" : "TextBox", new(left + x * scale, top + y * scale, w * scale, 28 * scale), text,
                button ? "player-trade-" + id : "", button ? "Kenshi_Button1Skin" : "Kenshi_GenericTextBoxFlatSkin", enabled,
                textProperties, Vector4.Zero, TextColour: new Vector4(.9f, .9f, .86f, 1), Pickable: button));
        }
        controls.Add(new("player-trade-panel", "Window", new(left, top, width, 470 * scale), "Player trade", "", "", true, textProperties, new Vector4(.09f, .09f, .08f, .98f)));
        Row("title", $"PLAYER TRADE  |  quote {trade.Revision}", 12, 8, 550);
        Row("cancel", "Cancel", 610, 8, 95, true);
        Row("own", $"Your offer: c.{trade.Own.Money}  {(trade.Own.Confirmed ? "CONFIRMED" : "")}", 12, 42, 340);
        Row("other", $"Their offer: c.{trade.Other.Money}  {(trade.Other.Confirmed ? "CONFIRMED" : "")}", 370, 42, 335);
        if (!trade.Own.Accepted)
        {
            if (trade.PaymentFactions.Count > 0)
            {
                _playerTradeFaction = Math.Clamp(_playerTradeFaction, 0, trade.PaymentFactions.Count - 1);
                Row("faction", $"Paying faction: {trade.PaymentFactions[_playerTradeFaction]} (change)", 12, 82, 340, true);
                Row("accept", "Accept invitation", 12, 120, 340, true);
            }
            else Row("no-funds", "You need an account with spending permission.", 12, 82, 690);
            return;
        }
        Row("account", $"Your paying faction: {trade.Own.PaymentFaction}", 12, 74, 340);
        Row("other-account", trade.Other.Accepted ? $"Their paying faction: {trade.Other.PaymentFaction}" : "Waiting for invitation acceptance", 370, 74, 335);
        for (int i = 0; i < 4; i++)
        {
            long step = i switch { 0 => 1, 1 => 10, 2 => 100, _ => 1000 };
            Row("minus-" + step, "-" + step, 12 + i * 85, 108, 80, true);
            Row("plus-" + step, "+" + step, 12 + i * 85, 140, 80, true);
        }
        int pages = Math.Max(1, (trade.Available.Count + PlayerTradeRows - 1) / PlayerTradeRows);
        _playerTradePage = Math.Clamp(_playerTradePage, 0, pages - 1);
        int otherPages = Math.Max(1, (trade.Other.Items.Count + PlayerTradeRows - 1) / PlayerTradeRows);
        _playerTradeOtherPage = Math.Clamp(_playerTradeOtherPage, 0, otherPages - 1);
        Row("help", "Click a stack to add/remove the whole stack", 12, 180, 345);
        Row("their-items", "Offered items", 370, 180, 335);
        for (int i = 0; i < PlayerTradeRows; i++)
        {
            int index = _playerTradePage * PlayerTradeRows + i;
            if (index < trade.Available.Count)
            {
                var row = trade.Available[index];
                bool offered = trade.Own.Items.Any(item => item.Item == row.Item);
                Row("item-" + index, $"{(offered ? "[x]" : "[ ]")} {row.Name} x{row.Count}", 12, 210 + i * 26, 340, true);
            }
            int otherIndex = _playerTradeOtherPage * PlayerTradeRows + i;
            if (otherIndex < trade.Other.Items.Count)
            {
                var row = trade.Other.Items[otherIndex];
                Row("other-item-" + otherIndex, $"{row.Name} x{row.Count}", 370, 210 + i * 26, 335);
            }
        }
        Row("previous", "<", 12, 425, 35, true);
        Row("page", $"{_playerTradePage + 1}/{pages}", 52, 425, 70);
        Row("next", ">", 125, 425, 35, true);
        Row("confirm", trade.Own.Confirmed ? "Confirmed; waiting" : "Confirm this quote", 170, 425, 182, true, trade.Other.Accepted && !trade.Own.Confirmed);
        Row("other-previous", "<", 370, 425, 35, true);
        Row("other-page", $"{_playerTradeOtherPage + 1}/{otherPages}", 410, 425, 70);
        Row("other-next", ">", 485, 425, 35, true);
    }

    /// <summary>
    /// Handles an action against the current barter quote.
    /// </summary>
    internal bool Click(UiControl control)
    {
        const string prefix = "player-trade-";
        if (!control.Name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (player.Session.CurrentSnapshot.PlayerTrade is not { } trade) return true;
        string action = control.Name[prefix.Length..];
        switch (action)
        {
            case "cancel":
                player.Say(new CancelPlayerTrade(trade.SessionId, trade.Revision) { Tick = player.Session.CommandTick });
                return true;
            case "accept":
                if (!trade.Own.Accepted && trade.PaymentFactions.Count > 0)
                    player.Say(new AcceptPlayerTrade(trade.SessionId, trade.Revision, trade.Own.Actor, trade.PaymentFactions[Math.Clamp(_playerTradeFaction, 0, trade.PaymentFactions.Count - 1)]) { Tick = player.Session.CommandTick });
                return true;
            case "faction":
                _playerTradeFaction = trade.PaymentFactions.Count == 0 ? 0 : (_playerTradeFaction + 1) % trade.PaymentFactions.Count;
                return true;
            case "confirm":
                if (trade.Own.Accepted && trade.Other.Accepted && !trade.Own.Confirmed)
                    player.Say(new ConfirmPlayerTrade(trade.SessionId, trade.Revision) { Tick = player.Session.CommandTick });
                return true;
            case "previous":
                _playerTradePage = Math.Max(0, _playerTradePage - 1);
                return true;
            case "next":
                _playerTradePage++;
                return true;
            case "other-previous":
                _playerTradeOtherPage = Math.Max(0, _playerTradeOtherPage - 1);
                return true;
            case "other-next":
                _playerTradeOtherPage++;
                return true;
        }
        if (!trade.Own.Accepted) return true;
        long money = trade.Own.Money;
        var items = trade.Own.Items.Select(row => new PlayerTradeItemOffer(row.Item, row.Count)).ToList();
        if (action.StartsWith("item-", StringComparison.Ordinal) && int.TryParse(action.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index >= 0 && index < trade.Available.Count)
        {
            var row = trade.Available[index];
            if (items.RemoveAll(item => item.Item == row.Item) == 0) items.Add(new(row.Item, row.Count));
        }
        else if ((action.StartsWith("plus-", StringComparison.Ordinal) || action.StartsWith("minus-", StringComparison.Ordinal))
            && long.TryParse(action.AsSpan(action.IndexOf('-') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long amount))
        {
            money = action.StartsWith("plus-", StringComparison.Ordinal) ? money > long.MaxValue - amount ? long.MaxValue : money + amount : Math.Max(0, money - amount);
        }
        else return true;
        player.Say(new RevisePlayerTrade(trade.SessionId, trade.Revision, money, trade.Own.InventoryRevision, items.ToArray()) { Tick = player.Session.CommandTick });
        return true;
    }
}