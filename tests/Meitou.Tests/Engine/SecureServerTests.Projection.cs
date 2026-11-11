using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Game.Host;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task RemoteDisplayRevisionsTrackScopeChangesWithoutRegistryMutationsOrBaselineChurn()
    {
        await using var scene = new Scene(production: true); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var actor = scene.Actors[0]; int owner = scene.Actors[1].Grant.Faction;
        await scene.Loop.InvokeAsync(() =>
        {
            var function = SyntheticTown.Rec("projection-fn", FcsRecordType.BuildingFunctionality, "Storage");
            function.Ints["function"] = TownBuildings.GeneralStorageFunction;
            var record = SyntheticTown.Rec("projection-store", FcsRecordType.Building, "Storage");
            record.Bools["has inventory"] = true; record.References["functionality"] = [SyntheticTown.Ref(function.StringId)];
            var file = new FcsFile(); file.Records.AddRange([function, record]); scene.Data.Db.Apply(file, "projection.mod");
            var world = scene.Runtime.World; var production = world.Systems.OfType<ProductionSystem>().Single();
            foreach (int x in new[] { 0, 5000 })
            {
                var site = new BuildSite { Id = "scope-" + x, Record = record.StringId, Position = new(x, 300, 0), Owner = owner, Complete = true, Materials = [] };
                world.Buildings.Add(site); Assert.NotNull(production.Ensure(world, site));
                world.GroundItems.Load(scene.Data.Items.Create("70-t", 1)!, new(x, 300, 0), Quaternion.Identity);
            }
        }, token);
        await using var remote = new RemotePlayerSession(await ConnectRemote(scene, actor.Credential, token), (_, _) => 300);
        remote.Connection.RequestEntry(actor.Grant.Entity);
        await WaitRemote(remote.Connection, s => s.CanControl, token);
        remote.Advance(0); // covered loading frames must ingest readiness without consuming control ticks
        Assert.True(remote.CanControl); Assert.Equal(0, remote.Ticks.TotalTicks);
        var first = remote.CurrentSnapshot; var authority = remote.Connection.State.View!.Snapshot;
        Assert.Equal("scope-0", Assert.Single(first.Buildings).Id); Assert.Equal("scope-0", Assert.Single(first.Machines).Site); Assert.Single(first.GroundItems);
        ulong publication = remote.Connection.State.View!.Publication; await RefreshBaseline(remote, token);
        Assert.True(remote.CurrentSnapshot.BuildingsRevision >= first.BuildingsRevision);
        Assert.True(remote.CurrentSnapshot.MachinesVersion >= first.MachinesVersion); Assert.True(remote.CurrentSnapshot.GroundItemsRevision >= first.GroundItemsRevision);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Characters.Place(actor.Role.Slot, new(5000, 300, 0), 0), token);
        await PumpPlayer(remote, () => remote.CurrentSnapshot.Buildings.Any(b => b.Id == "scope-5000"), token);
        var moved = remote.CurrentSnapshot;
        Assert.Equal("scope-5000", Assert.Single(moved.Buildings).Id); Assert.Equal("scope-5000", Assert.Single(moved.Machines).Site);
        Assert.NotEqual(first.GroundItems[0].Id, Assert.Single(moved.GroundItems).Id);
        Assert.Equal(authority.BuildingsRevision, remote.Connection.State.View!.Snapshot.BuildingsRevision);
        Assert.Equal(authority.MachinesVersion, remote.Connection.State.View.Snapshot.MachinesVersion);
        Assert.Equal(authority.GroundItemsRevision, remote.Connection.State.View.Snapshot.GroundItemsRevision);
        Assert.True(moved.BuildingsRevision > first.BuildingsRevision); Assert.True(moved.MachinesVersion > first.MachinesVersion);
        Assert.True(moved.GroundItemsRevision > first.GroundItemsRevision);
        var frame = remote.Connection.CapturePresentation();
        var receivedAtMove = frame.State.View;
        Assert.NotNull(receivedAtMove);
        Assert.NotNull(frame.Snapshot);
        Assert.Equal(receivedAtMove.Snapshot.Buildings.Select(b => (b.Id, b.SiteRevision)), frame.Snapshot.Buildings.Select(b => (b.Id, b.SiteRevision)));
        Assert.Equal(receivedAtMove.Snapshot.Machines.Select(m => (m.Site, m.SiteRevision)), frame.Snapshot.Machines.Select(m => (m.Site, m.SiteRevision)));
        Assert.Equal(receivedAtMove.Snapshot.Buildings[0].SiteRevision, moved.Buildings[0].SiteRevision);
        Assert.Equal(receivedAtMove.Snapshot.Machines[0].SiteRevision, moved.Machines[0].SiteRevision);
        await scene.Loop.InvokeAsync(() => scene.Runtime.World.Players.RemoveMembership(actor.Player, actor.Grant.Faction), token);
        await PumpPlayer(remote, () => !remote.HasWorldView && remote.CurrentSnapshot.Buildings.Count == 0, token);
        Assert.NotEqual(moved.BuildingsRevision, remote.CurrentSnapshot.BuildingsRevision);
        Assert.NotEqual(moved.MachinesVersion, remote.CurrentSnapshot.MachinesVersion);
        Assert.NotEqual(moved.GroundItemsRevision, remote.CurrentSnapshot.GroundItemsRevision);
        var restoredGrant = await scene.Loop.InvokeAsync(() =>
        {
            scene.Runtime.World.Players.SetMembership(actor.Player, actor.Grant.Faction, FactionPermission.All);
            return scene.Runtime.World.Players.AssignInitial(actor.Player, actor.Role);
        }, token);
        remote.Connection.RequestEntry(restoredGrant.Entity);
        await PumpPlayer(remote, () => remote.CanControl && remote.CurrentSnapshot.Buildings.Count == 1, token);
        Assert.Equal("scope-5000", remote.CurrentSnapshot.Buildings[0].Id);
        Assert.True(remote.CurrentSnapshot.BuildingsRevision > moved.BuildingsRevision);
        Assert.True(remote.CurrentSnapshot.MachinesVersion > moved.MachinesVersion);
        Assert.True(remote.CurrentSnapshot.GroundItemsRevision > moved.GroundItemsRevision);
    }
}
