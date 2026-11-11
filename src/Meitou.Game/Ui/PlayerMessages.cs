using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// Restores recipient-private message history and consumes new notices once, without replaying old popups.
/// </summary>
sealed class PlayerMessages(PlayerInterface player)
{
    /// <summary>
    /// The world incarnation whose message baseline is being displayed.
    /// </summary>
    object? _messageWorld;

    /// <summary>
    /// The highest private message sequence already consumed.
    /// </summary>
    ulong _messageHighWater;

    /// <summary>
    /// The next private messages may show popups after an initial usable baseline was restored.
    /// </summary>
    bool _messageBaseline;

    /// <summary>
    /// Restores the first usable history and then consumes each new private or public message once.
    /// </summary>
    internal void Poll()
    {
        // The local host retains its legacy TakeMessages/TakeSaid flow, including quiet screenshot setup.
        if (!player.Session.IsRemote) return;
        if (!ReferenceEquals(_messageWorld, player.Session.WorldIdentity))
        {
            if (_messageWorld is not null)
            {
                player.DialogueLog.ClearMessages();
                player.MessageRoller.ClearNotices();
            }
            _messageWorld = player.Session.WorldIdentity;
            _messageHighWater = 0;
            _messageBaseline = false;
        }
        if (!player.Session.HasWorldView)
        {
            _messageBaseline = false;
            return;
        }
        var batch = player.Session.Messages;
        foreach (var message in batch.Items.Where(m => m.Id > _messageHighWater).OrderBy(m => m.Id))
        {
            if (message.Kind == PlayerMessageKind.Notice)
            {
                if (_messageBaseline) player.Notice(message.Text);
                else player.DialogueLog.LogMessage("", Loc.Tr(message.Text).ReplaceLineEndings(" "), MessageTone.System);
            }
            else player.DialogueLog.LogMessage(message.Owner, message.Text, message.Player ? MessageTone.Player : MessageTone.Normal);
        }
        _messageHighWater = Math.Max(_messageHighWater, batch.HighWater);
        _messageBaseline = true;
        while (player.Session.TryDequeuePublicSpeech(out var line)) player.DialogueLog.LogMessage(line.Owner, line.Text, line.Player ? MessageTone.Player : MessageTone.Normal);
    }

    /// <summary>
    /// Drops the message baseline when the displayed world is replaced.
    /// </summary>
    internal void Reset()
    {
        _messageWorld = null;
        _messageHighWater = 0;
        _messageBaseline = false;
    }

}