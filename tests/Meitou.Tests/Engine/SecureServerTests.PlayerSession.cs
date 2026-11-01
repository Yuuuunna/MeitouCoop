using System.Numerics;
using Meitou.Engine;
using Meitou.Game;
using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task CommonRemoteLoopUsesServerClockWithoutResettingAnimationTimeOrAcceptingLocalPause()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var actor = scene.Actors[0];
        await using var connection = await ConnectRemote(scene, actor.Credential, token);
        await using var remote = new RemotePlayerSession(connection, (_, _) => 300, tickRate: 60);
        IClientSession client = remote;
        connection.RequestEntry(actor.Grant.Entity);
        await PumpPlayer(remote, () => remote.CanControl, token);
        Assert.Equal(60, client.Ticks.TickRate); Assert.True(client.IsPaused);
        await scene.Loop.InvokeAsync(() => scene.Runtime.Speed = 1, token);
        await PumpPlayer(remote, () => remote.CurrentSnapshot.Tick > 0 && remote.TimeScale == 1 && remote.Clock.GameSeconds > 0, token);
        double animationTime = client.Clock.GameSeconds;
        client.Bindings.Set(Meitou.Engine.Input.InputAction.Pause, Meitou.Engine.Input.Key.Space);
        client.Input.SetKey(Meitou.Engine.Input.Key.Space, true);
        Assert.True(client.Tick().Pressed(Meitou.Engine.Input.InputAction.Pause));
        Assert.Equal(1, await scene.Loop.InvokeAsync(() => scene.Runtime.Speed, token));
        client.SetModalPause(true);
        Assert.False(client.IsPaused);
        await scene.Loop.InvokeAsync(() => scene.Runtime.Speed = 0, token);
        await PumpPlayer(remote, () => client.IsPaused, token);
        Assert.True(client.Clock.GameSeconds >= animationTime);
        double stopped = client.Clock.GameSeconds;
        ulong publication = connection.State.View!.Publication;
        await PumpPlayer(remote, () => connection.State.View!.Publication > publication, token);
        Assert.Equal(stopped, client.Clock.GameSeconds, 8);
        client.Advance(10);
        Assert.Equal(stopped, client.Clock.GameSeconds, 8);
        Assert.InRange(client.PresentationTick, Math.Max(0, client.PreviousSnapshot.Tick), client.CurrentSnapshot.Tick);
        Assert.Equal(stopped, client.PresentationSeconds, 8);
    }

    static async Task PumpPlayer(RemotePlayerSession session, Func<bool> done, CancellationToken token)
    {
        while (true)
        {
            session.Advance(.01);
            if (done()) return;
            Assert.Null(session.Connection.State.Disconnection);
            await Task.Delay(10, token);
        }
    }

    [Fact]
    public async Task GraphicalSessionAdapterUsesRealRemoteViewsLocalSelectionAndRevisionBoundCommands()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var actor = scene.Actors[0]; var other = scene.Actors[1];
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.SetMembership(actor.Player, other.Grant.Faction, FactionPermission.ReadFunds), token);
        await using var connection = await ConnectRemote(scene, actor.Credential, token);
        await using var session = new RemotePlayerSession(connection, (_, _) => 300);
        var ui = new PlayerInterface(session, (_, _) => 300, c => c.Position);
        Assert.False(session.CanControl); Assert.Empty(session.CurrentSnapshot.Characters);
        connection.RequestEntry(actor.Grant.Entity);
        await PumpPlayer(session, () => session.CanControl, token);
        Assert.Equal(100, session.Money); Assert.Equal(0, session.TimeScale);
        Assert.False(session.CanChangeSpeed); Assert.False(session.CanSaveWorld);
        session.SelectFaction(other.Grant.Faction); Assert.Equal(200, session.Money);
        ui.Select([actor.Role], false);
        Assert.Equal(actor.Role, Assert.Single(session.CurrentSnapshot.Characters, c => c.Selected).Id);
        Assert.Equal(actor.Grant.Faction, session.ActiveFaction); Assert.Equal(100, session.Money);
        await PumpPlayer(session, () => session.PendingCommands == 0, token);
        Assert.Equal(AuthorityStatus.Evaluated, session.LastCompletion!.Receipt!.Status);
        ulong selectionRequest = session.LastCompletion.Request;
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.Find(actor.Player)!.State.Selection.Clear(), token);
        var before = session.CurrentSnapshot.Characters.Single(c => c.Id == actor.Role).Position;
        ui.Move(before + new Vector3(50, 0, 0), false);
        await PumpPlayer(session, () => session.PendingCommands == 0, token);
        Assert.True(session.LastCompletion!.Request > selectionRequest);
        Assert.Equal(AuthorityStatus.Evaluated, session.LastCompletion.Receipt!.Status);
        session.Advance(10); // no local world or movement prediction
        Assert.Equal(before, session.CurrentSnapshot.Characters.Single(c => c.Id == actor.Role).Position);
        Assert.Equal(0, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Tick, token));
        ui.Select([other.Role], false);
        Assert.NotNull(session.LastCompletion.Error); Assert.Equal(0, session.PendingCommands);
        Assert.Equal(actor.Role, Assert.Single(session.CurrentSnapshot.Characters, c => c.Selected).Id);
        session.SetModalPause(true);
        Assert.Equal(0, await scene.Loop.InvokeAsync(() => scene.Runtime.Speed, token));
        await connection.DisposeAsync(); session.Advance(0);
        Assert.False(session.CanControl); Assert.Null(session.Money);
        Assert.Empty(session.CurrentSnapshot.Characters); Assert.Empty(session.PreviousSnapshot.Characters);
    }

    [Fact]
    public async Task RemotePresentationClearsRevokedPrivateDetailsFromBothRenderingSnapshots()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var actor = scene.Actors[0];
        await using var connection = await ConnectRemote(scene, actor.Credential, token);
        await using var session = new RemotePlayerSession(connection, (_, _) => 300);
        connection.RequestEntry(actor.Grant.Entity);
        await PumpPlayer(session, () => session.CanControl, token);
        Assert.NotNull(session.CurrentSnapshot.Characters.Single(c => c.Id == actor.Role).ItemGrid);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.RemoveMembership(actor.Player, actor.Grant.Faction), token);
        await PumpPlayer(session, () => session.CurrentSnapshot.Characters.Count == 0, token);
        Assert.Empty(session.PreviousSnapshot.Characters); Assert.Null(session.Money);
        Assert.False(session.CanControl);
    }
}
