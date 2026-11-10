using Meitou.Engine;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task RemoteFactionPresentationUsesDirectedServerRelationsAndKeepsResourcesMembershipScoped()
    {
        await using var scene = new Scene(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var aa = scene.Actors[0]; var bb = scene.Actors[1]; var cc = scene.Actors[2];
        await scene.Loop.InvokeAsync(() =>
        {
            scene.Runtime.World.Factions.Find(aa.Grant.Faction)!.Name = "Amber";
            scene.Runtime.World.Factions.Find(bb.Grant.Faction)!.Name = "Blue";
            scene.Data.Relations.Set(aa.Grant.Faction, bb.Grant.Faction, -45);
            scene.Data.Relations.Set(bb.Grant.Faction, aa.Grant.Faction, 80);
            scene.Runtime.World.Characters.Place(cc.Role.Slot, new(20000, 300, 0), 0);
        }, token);
        await using var a = new RemotePlayerSession(await ConnectRemote(scene, aa.Credential, token), (_, _) => 300);
        await using var b = new RemotePlayerSession(await ConnectRemote(scene, bb.Credential, token), (_, _) => 300);
        a.Connection.RequestEntry(aa.Grant.Entity); b.Connection.RequestEntry(bb.Grant.Entity);
        await PumpPlayer(a, () => a.CanControl, token); await PumpPlayer(b, () => b.CanControl, token);
        Assert.Equal("Blue", a.DescribeFaction(bb.Grant.Faction)!.Name);
        Assert.Equal("peer:1", a.DescribeFaction(bb.Grant.Faction)!.RecordId);
        Assert.True(a.StandingTowards(bb.Grant.Faction)!.Hostile);
        Assert.Equal(-45, a.StandingTowards(bb.Grant.Faction)!.Relation);
        Assert.True(b.StandingTowards(aa.Grant.Faction)!.Ally);
        Assert.Null(a.DescribeFaction(cc.Grant.Faction)); Assert.Null(a.StandingTowards(cc.Grant.Faction));
        Assert.Equal(aa.Grant.Faction, Assert.Single(a.Connection.State.View!.Factions).Faction);
        await scene.Loop.InvokeAsync(() =>
        {
            scene.Runtime.World.Factions.Find(bb.Grant.Faction)!.Name = "Blue renamed";
            scene.Data.Relations.Set(aa.Grant.Faction, bb.Grant.Faction, 70);
        }, token);
        await PumpPlayer(a, () => a.DescribeFaction(bb.Grant.Faction)?.Name == "Blue renamed" && a.StandingTowards(bb.Grant.Faction)?.Ally == true, token);
        a.Connection.Observe(new([]));
        await PumpPlayer(a, () => a.DescribeFaction(bb.Grant.Faction) is null, token);
        Assert.Null(a.StandingTowards(bb.Grant.Faction));
        a.Connection.Observe(new());
        await PumpPlayer(a, () => a.DescribeFaction(bb.Grant.Faction) is not null, token);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.SetMembership(aa.Player, bb.Grant.Faction, FactionPermission.ReadFunds), token);
        await PumpPlayer(a, () => a.CanControl && a.Connection.State.View?.Factions.Count == 2, token);
        a.SelectFaction(bb.Grant.Faction);
        Assert.Equal(200, a.Money); Assert.True(a.StandingTowards(aa.Grant.Faction)!.Ally);
        Assert.Single(a.Connection.State.View!.Roles);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.SetMembership(aa.Player, bb.Grant.Faction, FactionPermission.None), token);
        await PumpPlayer(a, () => a.Money is null, token);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.RemoveMembership(aa.Player, bb.Grant.Faction), token);
        await PumpPlayer(a, () => a.ActiveFaction == aa.Grant.Faction, token);
        Assert.Equal(100, a.Money); Assert.Equal("Blue renamed", a.DescribeFaction(bb.Grant.Faction)!.Name);
        Assert.DoesNotContain(a.Connection.State.View!.Factions, f => f.Faction == bb.Grant.Faction);
    }
}
