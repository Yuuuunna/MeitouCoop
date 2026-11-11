using System.Numerics;
using Meitou.Game.Host;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task CombatVisualsAreScopedDeduplicatedMutedAndClearedImmediatelyOnWithdrawal()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var aa = scene.Actors[0]; var bb = scene.Actors[1];
        BloodMark Mark(CharacterId who, float x, BloodMarkKind kind = BloodMarkKind.Splash) => new(kind, who, new(x, 300, 0), Vector2.UnitX, .3f, 0xFF0000, kind == BloodMarkKind.Explosion ? "1-t" : null);
        await scene.Loop.InvokeAsync(() =>
        {
            scene.Runtime.World.Characters.Place(bb.Role.Slot, new(5000, 300, 0), 0);
            scene.Runtime.World.EmitBlood(Mark(aa.Role, 0)); scene.Runtime.World.EmitBlockSpark(aa.Role);
        }, token);
        await using var a = new RemotePlayerSession(await ConnectRemote(scene, aa.Credential, token), (_, _) => 300);
        await using var b = new RemotePlayerSession(await ConnectRemote(scene, bb.Credential, token), (_, _) => 300);
        a.EnableBlood(true); a.EnableBlockSparks(true); b.EnableBlood(true); b.EnableBlockSparks(true);
        a.Connection.RequestEntry(aa.Grant.Entity); b.Connection.RequestEntry(bb.Grant.Entity);
        await PumpPlayer(a, () => a.CanControl, token); await PumpPlayer(b, () => b.CanControl, token);
        Assert.False(a.TryDequeueBlood(out _)); Assert.False(a.TryDequeueBlockSpark(out _));
        await scene.Loop.InvokeAsync(() =>
        {
            var world = scene.Runtime.World;
            world.EmitBlood(Mark(aa.Role, 0)); world.EmitBlockSpark(aa.Role);
            world.EmitBlood(Mark(bb.Role, 0, BloodMarkKind.Explosion));
            world.EmitBlood(Mark(bb.Role, 5000)); world.EmitBlockSpark(bb.Role);
        }, token);
        var av = new List<BloodVisual>(); var bv = new List<BloodVisual>(); var sparks = new List<CharacterId>();
        static void Drain(RemotePlayerSession session, List<BloodVisual> marks, List<CharacterId>? sparks = null)
        {
            while (session.TryDequeueBlood(out var mark)) marks.Add(mark);
            if (sparks is not null) while (session.TryDequeueBlockSpark(out var who)) sparks.Add(who);
        }
        await PumpPlayer(a, () => { Drain(a, av, sparks); return av.Count == 2 && sparks.Count == 1; }, token);
        await PumpPlayer(b, () => { Drain(b, bv); return bv.Count == 1; }, token);
        Assert.Equal(aa.Role, av[0].Mark.Who); Assert.True(av[1].Mark.Who.IsNone); Assert.Equal(bb.Role, bv[0].Mark.Who);
        Assert.Equal(aa.Role, Assert.Single(sparks)); Assert.True(b.TryDequeueBlockSpark(out var bs)); Assert.Equal(bb.Role, bs);
        ulong publication = a.Connection.State.View!.Publication; await RefreshBaseline(a, token);
        Assert.False(a.TryDequeueBlood(out _)); Assert.False(a.TryDequeueBlockSpark(out _));
        a.EnableBlood(false); a.EnableBlockSparks(false);
        ulong before = a.Connection.State.View!.Events.HighWater;
        await scene.Loop.InvokeAsync(() => { scene.Runtime.World.EmitBlood(Mark(aa.Role, 0)); scene.Runtime.World.EmitBlockSpark(aa.Role); }, token);
        await WaitRemote(a.Connection, s => s.View!.Events.HighWater > before, token);
        a.EnableBlood(true); a.EnableBlockSparks(true); a.Advance(0);
        Assert.False(a.TryDequeueBlood(out _)); Assert.False(a.TryDequeueBlockSpark(out _));
        before = a.Connection.State.View!.Events.HighWater;
        await scene.Loop.InvokeAsync(() => { scene.Runtime.World.EmitBlood(Mark(aa.Role, 0)); scene.Runtime.World.EmitBlockSpark(aa.Role); }, token);
        await WaitRemote(a.Connection, s => s.View!.Events.HighWater > before, token); a.Advance(0);
        // Leave both queues populated until the transport withdraws, with no intervening presentation update.
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.RemoveMembership(aa.Player, aa.Grant.Faction), token);
        await WaitRemote(a.Connection, s => s.View is null && !s.CanControl, token);
        Assert.False(a.TryDequeueBlood(out _)); Assert.False(a.TryDequeueBlockSpark(out _));
    }
}
