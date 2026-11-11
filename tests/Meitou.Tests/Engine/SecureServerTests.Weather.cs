using System.Numerics;
using Meitou.Data.Particles;
using Meitou.Game.Host;
using Meitou.Tests.World;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task RemoteHazardsFollowObservationMovementBaselinesAndAuthorityWithdrawal()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var aa = scene.Actors[0]; var bb = scene.Actors[1];
        var first = new EffectNodeSnapshot("a-gas", "saved-gas", "saved_gas", 7, new(0, 300, 0)) { Age = 0.5f, Radius = 30 };
        var second = first with { Id = "b-gas", Seed = 8, Position = new(5000, 300, 0) };
        var weather = new SyntheticEffectWeather { Frame = new([first, second]) };
        await scene.Loop.InvokeAsync(() =>
        {
            scene.Runtime.World.Characters.Place(bb.Role.Slot, new(5000, 300, 0), 0);
            scene.Runtime.World.Weather = weather;
        }, token);
        await using var a = new RemotePlayerSession(await ConnectRemote(scene, aa.Credential, token), (_, _) => 300);
        await using var b = new RemotePlayerSession(await ConnectRemote(scene, bb.Credential, token), (_, _) => 300);
        a.Connection.RequestEntry(aa.Grant.Entity); b.Connection.RequestEntry(bb.Grant.Entity);
        await PumpPlayer(a, () => a.CanControl, token); await PumpPlayer(b, () => b.CanControl, token);
        Assert.Equal(first, Assert.Single(a.WeatherEffects!.Nodes)); Assert.Equal(second, Assert.Single(b.WeatherEffects!.Nodes));
        var display = new PresentedEffectSet(ParticleCheckpointTests.Library(), new EffectWorld());
        EffectRecord? Resolve(string id) => id == "saved-gas" ? ParticleCheckpointTests.Effect(Meitou.Data.Particles.EffectType.WanderingGas) : null;
        display.Accept(a.WeatherEffects, Resolve); var root = Assert.Single(Assert.Single(display.Groups).Units);
        var moved = first with { Position = new(25, 300, 0), Age = 0.6f };
        await scene.Loop.InvokeAsync(() => weather.Frame = new([moved]), token);
        await PumpPlayer(a, () => a.WeatherEffects!.Nodes[0].Age == 0.6f, token);
        await PumpPlayer(b, () => b.WeatherEffects!.Nodes.Count == 0, token);
        display.Accept(a.WeatherEffects!, Resolve); Assert.Same(root, display.Groups[0].Units[0]); Assert.Equal(moved.Position, root.Position);
        ulong publication = a.Connection.State.View!.Publication; await RefreshBaseline(a, token);
        display.Accept(a.WeatherEffects!, Resolve); Assert.Same(root, display.Groups[0].Units[0]);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.RemoveMembership(aa.Player, aa.Grant.Faction), token);
        // The transport may withdraw between presentation frames; getters must not expose its old roots until Advance runs.
        await WaitRemote(a.Connection, s => s.View is null && !s.CanControl, token);
        Assert.Empty(a.WeatherEffects!.Nodes);
        a.Advance(0); Assert.False(a.CanControl);
        display.Accept(a.WeatherEffects, Resolve); Assert.Empty(display.Groups);
    }
}
