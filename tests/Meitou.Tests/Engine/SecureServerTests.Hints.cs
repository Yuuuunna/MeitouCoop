using System.Numerics;
using Meitou.Game.Host;
using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task RemoteTalkHintsAreNonblockingReadOnlyAndCannotBorrowAnotherPlayersActor()
    {
        await using var scene = new Scene(dialogue: true); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var actor = scene.Actors[0];
        var npc = await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Spawn(new() { Position = new(20, 300, 0), Health = 100 },
            new() { RecordId = "hint-npc", Faction = scene.Data.PlayerFaction, Name = "Speaker" }, 0), token);
        await using var a = new RemotePlayerSession(await ConnectRemote(scene, actor.Credential, token), (_, _) => 300);
        await using var b = new RemotePlayerSession(await ConnectRemote(scene, scene.Actors[1].Credential, token), (_, _) => 300);
        a.Connection.RequestEntry(actor.Grant.Entity); b.Connection.RequestEntry(scene.Actors[1].Grant.Entity);
        await PumpPlayer(a, () => a.CanControl, token); await PumpPlayer(b, () => b.CanControl, token);
        ulong before = await scene.Loop.InvokeAsync(() => scene.Runtime.World.StateHash(), token);
        Assert.False(a.CanTalk(npc, actor.Role));
        await PumpPlayer(a, () => a.CanTalk(npc, actor.Role), token);
        Assert.Equal(before, await scene.Loop.InvokeAsync(() => scene.Runtime.World.StateHash(), token));
        Assert.Equal(0ul, a.Connection.State.Admission.State.CommandHighWater);
        var membership = a.Connection.State.View!.Factions.Single(f => f.Faction == actor.Grant.Faction).MembershipRevision;
        var query = new TalkHintQuery(Guid.NewGuid(), actor.Role, npc, new(actor.Grant.Entity, actor.Grant.Faction, actor.Grant.Revision), membership);
        var borrowed = await b.Connection.TalkHintAsync(query, token);
        Assert.Equal(TalkHintStatus.StaleAuthority, borrowed.Hint.Status);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Place(npc.Slot, new Vector3(20000, 300, 0), 0), token);
        var hidden = await a.Connection.TalkHintAsync(query with { Query = Guid.NewGuid() }, token);
        Assert.Equal(TalkHintStatus.Unknown, hidden.Hint.Status);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.SetMembership(actor.Player, actor.Grant.Faction, FactionPermission.ReadFunds), token);
        await PumpPlayer(a, () => a.CanControl && a.Connection.State.View?.Factions.FirstOrDefault(f => f.Faction == actor.Grant.Faction)?.MembershipRevision is { } revision && revision != membership, token);
        var stale = await a.Connection.TalkHintAsync(query with { Query = Guid.NewGuid() }, token);
        Assert.Equal(TalkHintStatus.StaleAuthority, stale.Hint.Status);
    }
}
