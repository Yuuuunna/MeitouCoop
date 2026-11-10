using Meitou.Engine;
using Meitou.Game;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task PrivateNoticesAndDialogueRestoreWithoutPopupReplayWhilePublicSpeechUsesVisibility()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var aa = scene.Actors[0]; var bb = scene.Actors[1];
        await scene.Loop.InvokeAsync(() =>
        {
            scene.Runtime.World.Post(aa.Player, "A previous notice");
            scene.Runtime.World.Post(bb.Player, "B private notice");
            scene.Runtime.World.Post("Unattributed diagnostic");
        }, token);
        await using var a = new RemotePlayerSession(await ConnectRemote(scene, aa.Credential, token), (_, _) => 300);
        await using var b = new RemotePlayerSession(await ConnectRemote(scene, bb.Credential, token), (_, _) => 300);
        var uiA = new PlayerInterface(a, (_, _) => 300, c => c.Position);
        var uiB = new PlayerInterface(b, (_, _) => 300, c => c.Position);
        uiA.PollSessionMessages(); uiB.PollSessionMessages();
        a.Connection.RequestEntry(aa.Grant.Entity); b.Connection.RequestEntry(bb.Grant.Entity);
        await PumpPlayer(a, () => a.CanControl, token); await PumpPlayer(b, () => b.CanControl, token);
        uiA.PollSessionMessages(); uiB.PollSessionMessages();
        Assert.Contains("A previous notice", uiA.MessagesText()); Assert.DoesNotContain("B private notice", uiA.MessagesText());
        Assert.Contains("B private notice", uiB.MessagesText()); Assert.DoesNotContain("Unattributed diagnostic", uiA.MessagesText());
        Assert.Equal(0, uiA.PendingNoticeCount); Assert.Equal(0, uiB.PendingNoticeCount);
        await scene.Loop.InvokeAsync(() =>
        {
            scene.Runtime.World.Post(aa.Player, "A fresh notice");
            scene.Runtime.World.Said(aa.Player, "NPC", "A private dialogue", false);
            scene.Runtime.World.SaidPublic(aa.Role, "Role 0", "Public nearby speech", true);
        }, token);
        await PumpPlayer(a, () => a.Messages.HighWater == 3, token);
        await PumpPlayer(b, () => b.Connection.State.View!.Events.HighWater >= 1, token);
        uiA.PollSessionMessages(); uiB.PollSessionMessages();
        Assert.Equal(1, uiA.PendingNoticeCount); Assert.Equal(0, uiB.PendingNoticeCount);
        Assert.Contains("A private dialogue", uiA.MessagesText()); Assert.DoesNotContain("A private dialogue", uiB.MessagesText());
        Assert.Contains("Public nearby speech", uiA.MessagesText()); Assert.Contains("Public nearby speech", uiB.MessagesText());
        ulong publication = a.Connection.State.View!.Publication;
        await RefreshBaseline(a, token);
        uiA.PollSessionMessages();
        Assert.Equal(1, uiA.PendingNoticeCount);
        Assert.Equal(2, uiA.MessagesText().Split("Public nearby speech", StringSplitOptions.None).Length);
        await a.DisposeAsync();
        await using var resumed = new RemotePlayerSession(await ConnectRemote(scene, aa.Credential, token), (_, _) => 300);
        resumed.Connection.RequestEntry(aa.Grant.Entity);
        await PumpPlayer(resumed, () => resumed.CanControl, token);
        var restoredUi = new PlayerInterface(resumed, (_, _) => 300, c => c.Position);
        restoredUi.PollSessionMessages();
        Assert.Contains("A previous notice", restoredUi.MessagesText()); Assert.Contains("A fresh notice", restoredUi.MessagesText());
        Assert.Contains("A private dialogue", restoredUi.MessagesText()); Assert.DoesNotContain("B private notice", restoredUi.MessagesText());
        Assert.Equal(0, restoredUi.PendingNoticeCount); Assert.Equal(3, resumed.Messages.Items.Count);
    }
}
