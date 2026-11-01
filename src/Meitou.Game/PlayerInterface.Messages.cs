using Meitou.Simulation;
using Meitou.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    object? messageWorld;
    ulong messageHighWater;
    bool messageBaseline;

    internal void PollSessionMessages()
    {
        // The local host retains its legacy TakeMessages/TakeSaid flow, including quiet screenshot setup.
        if (!session.IsRemote) return;
        if (!ReferenceEquals(messageWorld, session.WorldIdentity))
        {
            if (messageWorld is not null) { uiMessages.Clear(); noticeQueue.Clear(); notice = null; }
            messageWorld = session.WorldIdentity; messageHighWater = 0; messageBaseline = false;
        }
        if (!session.HasWorldView) { messageBaseline = false; return; }
        var batch = session.Messages;
        foreach (var message in batch.Items.Where(m => m.Id > messageHighWater).OrderBy(m => m.Id))
        {
            if (message.Kind == PlayerMessageKind.Notice)
            {
                if (messageBaseline) Notice(message.Text);
                else LogMessage("", Loc.Tr(message.Text).ReplaceLineEndings(" "), MessageTone.System);
            }
            else LogMessage(message.Owner, message.Text, message.Player ? MessageTone.Player : MessageTone.Normal);
        }
        messageHighWater = Math.Max(messageHighWater, batch.HighWater); messageBaseline = true;
        while (session.TryDequeuePublicSpeech(out var line)) LogMessage(line.Owner, line.Text, line.Player ? MessageTone.Player : MessageTone.Normal);
    }
}
