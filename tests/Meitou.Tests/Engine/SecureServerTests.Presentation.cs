using System.Numerics;
using Meitou.Engine;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task RemoteSoundsKeepPrivateCuesScopedAndDoNotReplayAcrossBaselinesOrMuteTransitions()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var aa = scene.Actors[0]; var bb = scene.Actors[1]; var cc = scene.Actors[2];
        await using var a = new RemotePlayerSession(await ConnectRemote(scene, aa.Credential, token), (_, _) => 300);
        await using var b = new RemotePlayerSession(await ConnectRemote(scene, bb.Credential, token), (_, _) => 300);
        a.EnableSounds(true); b.EnableSounds(true);
        a.Connection.RequestEntry(aa.Grant.Entity); b.Connection.RequestEntry(bb.Grant.Entity);
        await PumpPlayer(a, () => a.CanControl, token); await PumpPlayer(b, () => b.CanControl, token);
        await scene.Loop.InvokeAsync(() =>
        {
            var world = scene.Runtime.World;
            world.Characters.Place(cc.Role.Slot, new(20000, 300, 0), 0);
            world.EmitSound(new SoundEvent(SoundKind.Hit, bb.Role, cc.Role, new(20, 300, 0)));
            world.EmitSound(SoundKind.BountyPaid, aa.Role.Slot, new(0, 300, 0));
            world.EmitSound(SoundKind.ShacklesOn, aa.Role.Slot, new(0, 300, 0), "private-item");
            world.EmitSound(SoundKind.Shot, cc.Role.Slot, new(20000, 300, 0));
        }, token);
        static void Drain(RemotePlayerSession session, List<SoundEvent> into) { while (session.TryDequeueSound(out var sound)) into.Add(sound); }
        var av = new List<SoundEvent>(); var bv = new List<SoundEvent>();
        await PumpPlayer(a, () => { Drain(a, av); return av.Count == 3; }, token);
        await PumpPlayer(b, () => { Drain(b, bv); return bv.Count == 1; }, token);
        Assert.Equal(new[] { SoundKind.Hit, SoundKind.BountyPaid, SoundKind.ShacklesOn }, av.Select(e => e.Kind));
        Assert.Equal(SoundKind.Hit, Assert.Single(bv).Kind); Assert.True(bv[0].Other.IsNone);
        Assert.Equal("private-item", av[2].Item);
        ulong publication = a.Connection.State.View!.Publication;
        await RefreshBaseline(a, token);
        Assert.False(a.TryDequeueSound(out _));
        a.EnableSounds(false);
        ulong seen = a.Connection.State.View!.Events.HighWater;
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.EmitSound(SoundKind.Shot, aa.Role.Slot, new(0, 300, 0)), token);
        // The transport receives a publication while the presentation loop is not being advanced.
        await WaitRemote(a.Connection, s => s.View!.Events.HighWater > seen, token);
        a.EnableSounds(true); a.Advance(0);
        Assert.False(a.TryDequeueSound(out _));
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.EmitSound(SoundKind.Shot, aa.Role.Slot, new(0, 300, 0)), token);
        var latest = new List<SoundEvent>();
        await PumpPlayer(a, () => { Drain(a, latest); return latest.Count > 0; }, token);
        Assert.Equal(SoundKind.Shot, Assert.Single(latest).Kind);
    }

    [Fact]
    public async Task SurveyCompletesOnTheServerAndOnlyTheIssuingRemoteSessionReceivesItsMap()
    {
        await using var scene = new Scene(production: true); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var actor = scene.Actors[0];
        await using var a = new RemotePlayerSession(await ConnectRemote(scene, actor.Credential, token), (_, _) => 300);
        await using var b = new RemotePlayerSession(await ConnectRemote(scene, scene.Actors[1].Credential, token), (_, _) => 300);
        a.Connection.RequestEntry(actor.Grant.Entity); b.Connection.RequestEntry(scene.Actors[1].Grant.Entity);
        await PumpPlayer(a, () => a.CanControl, token); await PumpPlayer(b, () => b.CanControl, token);
        var command = new ProspectOrder(actor.Role) { Tick = 0 };
        var bound = ClientCommandBinding.Bind(a.Connection.State.View!, command, [], actor.Grant.Faction);
        a.Submit(command);
        await PumpPlayer(a, () => a.PendingCommands == 0, token);
        var receipt = a.LastCompletion!.Receipt!; Assert.Equal(AuthorityStatus.Evaluated, receipt.Status);
        Assert.Null(a.Prospected);
        var beforeSurvey = a.Connection.State.View!;
        await scene.Loop.InvokeAsync(() => { for (int i = 0; i < 40; i++) scene.Runtime.Step(publish: false); }, token);
        var serverReport = await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.Find(actor.Player)!.State.Prospected, token);
        Assert.NotNull(serverReport);
        _ = new WorldViewCodec(scene.Data.Db).Encode(beforeSurvey with { Prospected = serverReport });
        await PumpPlayer(a, () => a.Prospected is not null, token);
        b.Advance(0);
        Assert.Equal("Role 0", a.Prospected!.Prospector); Assert.NotNull(a.Prospected.Map);
        Assert.Equal(a.Prospected.Map!.Size * a.Prospected.Map.Size, a.Prospected.Map.Heights.Length);
        Assert.Equal(1, a.Prospected.Serial); Assert.Null(b.Prospected);
        Assert.Null(await scene.Loop.InvokeAsync(() => scene.Runtime.World.Prospected, token));
        var replay = await a.Connection.CommandAsync(receipt.Request, bound.Command, bound.Expected, token);
        Assert.Equal(receipt, replay.Receipt);
        await scene.Loop.InvokeAsync(() => { for (int i = 0; i < 40; i++) scene.Runtime.Step(publish: false); }, token);
        Assert.Equal(1, await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.Find(actor.Player)!.State.Prospected!.Serial, token));
    }

    [Fact]
    public async Task RemoteFloatersArePrivateDeduplicatedAcrossBaselinesAndNotReplayedAfterReenabling()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var aa = scene.Actors[0]; var bb = scene.Actors[1];
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.EmitFloater(Floater.Text(aa.Role, Vector3.Zero, "old event")), token);
        await using var a = new RemotePlayerSession(await ConnectRemote(scene, aa.Credential, token), (_, _) => 300);
        await using var b = new RemotePlayerSession(await ConnectRemote(scene, bb.Credential, token), (_, _) => 300);
        a.Connection.RequestEntry(aa.Grant.Entity); b.Connection.RequestEntry(bb.Grant.Entity);
        await PumpPlayer(a, () => a.CanControl, token); await PumpPlayer(b, () => b.CanControl, token);
        Assert.False(a.TryDequeueFloater(out _)); Assert.False(b.TryDequeueFloater(out _));
        await scene.Loop.InvokeAsync(() =>
        {
            scene.Runtime.World.EmitFloater(Floater.Text(aa.Role, Vector3.Zero, "A private"));
            scene.Runtime.World.EmitFloater(Floater.Text(bb.Role, Vector3.Zero, "B private"));
            scene.Runtime.World.EmitFloater(Floater.Hit(bb.Role, Vector3.Zero, true, true, "Head", default, default), aa.Player);
        }, token);
        var af = new List<Floater>(); var bf = new List<Floater>();
        static void Drain(RemotePlayerSession s, List<Floater> into) { while (s.TryDequeueFloater(out var value)) into.Add(value); }
        await PumpPlayer(a, () => { Drain(a, af); return af.Count >= 2; }, token);
        await PumpPlayer(b, () => { Drain(b, bf); return bf.Count >= 2; }, token);
        Assert.Equal(new[] { "A private", "Head" }, af.Select(f => f.Part));
        Assert.Equal(new[] { "B private", "Head" }, bf.Select(f => f.Part));
        Assert.False(af[1].TargetIsPlayer); Assert.True(af[1].SourceIsPlayer);
        Assert.True(bf[1].TargetIsPlayer); Assert.False(bf[1].SourceIsPlayer);
        ulong publication = a.Connection.State.View!.Publication;
        await RefreshBaseline(a, token);
        Assert.False(a.TryDequeueFloater(out _));
        a.EnableFloaters(false);
        ulong seen = a.Connection.State.View!.Events.HighWater;
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.EmitFloater(Floater.Text(aa.Role, Vector3.Zero, "disabled")), token);
        await PumpPlayer(a, () => a.Connection.State.View!.Events.HighWater > seen, token);
        a.EnableFloaters(true); publication = a.Connection.State.View!.Publication;
        await PumpPlayer(a, () => a.Connection.State.View?.Publication > publication, token);
        Assert.False(a.TryDequeueFloater(out _));
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.EmitFloater(Floater.Text(aa.Role, Vector3.Zero, "new")), token);
        var after = new List<Floater>();
        await PumpPlayer(a, () => { Drain(a, after); return after.Count > 0; }, token);
        Assert.Equal("new", Assert.Single(after).Part);
    }
}
