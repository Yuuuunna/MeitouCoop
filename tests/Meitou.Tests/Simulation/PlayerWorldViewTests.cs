using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class PlayerWorldViewTests
{
    [Fact]
    public void OnePinnedBaselineSurvivesMotionCaptureHistoryWithoutPinningEveryPublication()
    {
        using var s = new Scene();
        var authority = s.World.Authority;
        ulong baseline = s.View().Publication;
        Assert.True(authority.PinPublishedView(s.ConnectionA, baseline));
        ulong newest = baseline;
        for (int i = 0; i < 20; i++) newest = s.View().Publication;
        Assert.False(authority.PinPublishedView(s.ConnectionA, newest));
        authority.ReleasePublishedView(s.ConnectionA, newest); // late/unrelated completion cannot release the pin
        Assert.True(authority.CanRetainPublishedView(s.ConnectionA, baseline));
        Assert.True(authority.ActivateAfterView(s.ConnectionA, baseline));
        authority.ReleasePublishedView(s.ConnectionA, baseline);
        Assert.False(authority.CanRetainPublishedView(s.ConnectionA, baseline));
        Assert.True(authority.CanRetainPublishedView(s.ConnectionA, newest));
    }

    [Fact]
    public void PinnedEvidenceNeverPreservesRevokedAuthorityOrSurvivesWithdrawal()
    {
        using var s = new Scene();
        var authority = s.World.Authority;
        ulong baseline = s.View().Publication;
        Assert.True(authority.PinPublishedView(s.ConnectionA, baseline));
        for (int i = 0; i < 12; i++) s.View();
        s.World.Players.RemoveMembership(s.A.Id, 7);
        Assert.False(authority.CanRetainPublishedView(s.ConnectionA, baseline));
        Assert.False(authority.ActivateAfterView(s.ConnectionA, baseline));
        authority.WithdrawView(s.ConnectionA);
        Assert.False(authority.PinPublishedView(s.ConnectionA, baseline));
    }

    [Fact]
    public void EffectNodesFollowAuthorizedObservationAndCannotBeRevealedByRequestedZones()
    {
        using var s = new Scene();
        s.World.Characters.Place(s.Second.Slot, new(20000, 0, 0), 0);
        var near = new Meitou.Data.Particles.EffectNodeSnapshot("near", "gas", "gas-system", 1, Vector3.Zero);
        var far = near with { Id = "far", Seed = 2, Position = new(20000, 0, 0) };
        var edge = near with { Id = "edge", Position = new(110, 0, 0), AffectRadius = 20 };
        s.World.Weather = new World.SyntheticEffectWeather { Frame = new([near, far, edge]) };
        Assert.Equal(new[] { near, edge }, s.View().WeatherEffects!.Nodes);
        Assert.Equal(far, Assert.Single(s.View(s.ConnectionB).WeatherEffects!.Nodes));
        Assert.Empty(s.View(request: new([WorldLayout.ZoneOf(20000, 0)])).WeatherEffects!.Nodes);
        s.World.Players.RemoveMembership(s.A.Id, 7);
        Assert.Null(s.View());
    }

    sealed class Scene : IDisposable
    {
        public SimWorld World { get; }
        public WorldPlayer A { get; }
        public WorldPlayer B { get; }
        public CharacterId First { get; }
        public CharacterId Second { get; }
        public AuthorityConnection ConnectionA { get; }
        public AuthorityConnection ConnectionB { get; }
        public Scene(bool sameFaction = false, params ITickSystem[] systems)
        {
            World = new(new WorldSettings { PublishSnapshots = false, Threads = 4 }, new OpenGroundWalkability((_, _) => 0), systems);
            A = World.Players.Register(new(2)); B = World.Players.Register(new(3));
            World.Players.SetMembership(A.Id, 7, FactionPermission.All);
            World.Players.SetMembership(B.Id, sameFaction ? 7 : 8, FactionPermission.All);
            World.Factions.GetOrCreate(7).Money = 123;
            if (!sameFaction) World.Factions.GetOrCreate(8).Money = 456;
            First = Spawn(A, Vector3.Zero, "first-private"); Second = Spawn(B, new(20, 0, 0), "second-private");
            A.State.Selection.Add(First); B.State.Selection.Add(Second);
            ConnectionA = Connect(A.Id); ConnectionB = Connect(B.Id);
        }
        public CharacterId Spawn(WorldPlayer player, Vector3 position, string item = "item")
        {
            var inventory = new Inventory(); inventory.Items.Add(new ItemInstance { Record = item, Name = item, Quantity = 3 });
            var cold = new CharacterCold { Faction = player.State.Faction, IsPlayer = true, Inventory = inventory, Name = item };
            cold.Jobs.Add(PermaJob.Untargeted(TaskType.JOB_MEDIC));
            var id = World.Characters.Spawn(new CharacterHot { Position = position }, cold, World.Tick);
            World.Players.AssignInitial(player.Id, id); return id;
        }
        public AuthorityConnection Connect(PlayerId player)
        {
            var connection = World.Authority.Open(player);
            Assert.True(World.Authority.BeginSynchronization(connection));
            return connection;
        }
        public PlayerWorldView View(AuthorityConnection? connection = null, PlayerViewRequest? request = null) =>
            World.Authority.CaptureView(connection ?? ConnectionA, request ?? new(), new(100))!;
        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void EachRecipientGetsOwnPrivateDetailsAndFundsWhileInspectionKeepsOthersPrivateDataHidden()
    {
        using var s = new Scene();
        // Even a stale or forged view selection cannot turn another player's character into private detail.
        s.A.State.InventoryShown.Add(s.Second); s.A.State.Inspected = s.Second;
        var a = s.View(); var b = s.View(s.ConnectionB);
        Assert.Equal(123, Assert.Single(a.Factions).Money); Assert.Equal(456, Assert.Single(b.Factions).Money);
        var own = a.Snapshot.Characters.Single(c => c.Id == s.First);
        var other = a.Snapshot.Characters.Single(c => c.Id == s.Second);
        Assert.True(own.Selected); Assert.NotNull(own.ItemGrid); Assert.Single(own.Jobs);
        Assert.Null(other.ItemGrid); Assert.Empty(other.Jobs); Assert.Equal(0ul, other.JobsRevision);
        Assert.False(other.Selected); Assert.False(other.IsPlayer);
        Assert.NotNull(b.Snapshot.Characters.Single(c => c.Id == s.Second).ItemGrid);
        Assert.Null(b.Snapshot.Characters.Single(c => c.Id == s.First).ItemGrid);
        Assert.Empty(s.World.Player.Selection); Assert.Empty(s.World.Snapshot.Characters);
        var savedQuantity = Assert.Single(own.ItemGrid.Items).Quantity;
        s.World.Characters.Cold(s.First.Slot)!.Inventory!.Items[0].Quantity++;
        var next = s.View().Snapshot.Characters.Single(c => c.Id == s.First).ItemGrid!;
        Assert.Equal(savedQuantity, Assert.Single(own.ItemGrid.Items).Quantity);
        Assert.True(next.Revision > own.ItemGrid.Revision);
    }

    [Fact]
    public void SharedMembershipDoesNotGrantOtherMembersInventoriesAndReadFundsIsIndependent()
    {
        using var s = new Scene(sameFaction: true);
        s.World.Players.SetMembership(s.B.Id, 7, FactionPermission.SpendFunds);
        var view = s.View(s.ConnectionB);
        Assert.Null(Assert.Single(view.Factions).Money);
        Assert.NotNull(view.Snapshot.Characters.Single(c => c.Id == s.Second).ItemGrid);
        Assert.Null(view.Snapshot.Characters.Single(c => c.Id == s.First).ItemGrid);
        s.World.Players.SetMembership(s.B.Id, 7, FactionPermission.ReadFunds);
        Assert.Equal(123, Assert.Single(s.View(s.ConnectionB).Factions).Money);
    }

    [Fact]
    public void ZoneFiltersCannotGrantRemoteVisionOrActivateSimulationRegions()
    {
        using var s = new Scene();
        s.World.Characters.Place(s.Second.Slot, new(20000, 0, 0), 0);
        var before = s.World.Regions.Capture().Keys.ToHashSet();
        var farZone = WorldLayout.ZoneOf(20000, 0);
        var view = s.View(request: new([farZone]));
        Assert.Single(view.Snapshot.Characters); Assert.Equal(s.First, view.Snapshot.Characters[0].Id);
        Assert.True(before.SetEquals(s.World.Regions.Capture().Keys));
        Assert.Single(s.View().Snapshot.Characters);
        s.World.Characters.Place(s.Second.Slot, new(10, 0, 0), 0);
        Assert.Equal(2, s.View().Snapshot.Characters.Count);
        Assert.Throws<ArgumentException>(() => s.View(request: new([new(-1, 0)])));
    }

    [Fact]
    public void PreparationReconnectAndLossOfLastRoleDoNotExposeWorldState()
    {
        using var s = new Scene();
        var replacement = s.World.Authority.Open(s.A.Id);
        Assert.Null(s.World.Authority.CaptureView(s.ConnectionA, new(), new()));
        Assert.Null(s.World.Authority.CaptureView(replacement, new(), new()));
        Assert.True(s.World.Authority.BeginSynchronization(replacement));
        Assert.NotNull(s.World.Authority.CaptureView(replacement, new(), new()));
        s.World.Characters.Cold(s.First.Slot)!.DiedTick = 1;
        Assert.Null(s.World.Authority.CaptureView(replacement, new(), new()));
        Assert.Equal(AdmissionPhase.Preparing, s.World.Authority.GetPhase(replacement));
    }

    [Fact]
    public void TransferImmediatelyMovesPrivateDetailAndDoesNotCreateDuplicateCharacter()
    {
        using var s = new Scene(sameFaction: true);
        var remaining = s.Spawn(s.A, new(30, 0, 0));
        var grant = s.World.Players.ControlOf(s.First)!.Value;
        Assert.True(s.World.Players.TryTransfer(s.A.Id, grant, s.B.Id, out var next));
        var a = s.View(); var b = s.View(s.ConnectionB);
        Assert.Null(a.Snapshot.Characters.Single(c => c.Id == s.First).ItemGrid);
        Assert.NotNull(b.Snapshot.Characters.Single(c => c.Id == s.First).ItemGrid);
        Assert.DoesNotContain(a.Roles, r => r.Entity == grant.Entity);
        Assert.Equal(next.Revision, b.Roles.Single(r => r.Entity == grant.Entity).Revision);
        Assert.Equal(3, s.World.Characters.Count); Assert.NotNull(a.Snapshot.Characters.Single(c => c.Id == remaining).ItemGrid);
    }

    [Fact]
    public void DeadRolesDoNotKeepRemoteObservationOpenButLivingCaptivesKeepTheirActualPosition()
    {
        using var s = new Scene();
        var extra = s.Spawn(s.A, new(20000, 0, 0));
        s.World.Characters.Place(s.Second.Slot, new(20010, 0, 0), 0);
        Assert.Contains(s.View().Snapshot.Characters, c => c.Id == s.Second);
        s.World.Characters.Cold(extra.Slot)!.DiedTick = 1;
        var view = s.View();
        Assert.DoesNotContain(view.Snapshot.Characters, c => c.Id == s.Second);
        Assert.True(view.Roles.Single(r => r.Character == extra).Dead);
        s.World.Characters.Cold(s.First.Slot)!.CarriedBy = s.Second;
        Assert.Contains(s.View().Snapshot.Characters, c => c.Id == s.Second);
    }

    [Fact]
    public void NearbyMachinePresentationDoesNotExposeOtherFactionsStockAndRevocationRemovesDetails()
    {
        var function = SyntheticTown.Rec("store-fn", FcsRecordType.BUILDING_FUNCTIONALITY, "Storage");
        function.Ints["function"] = TownBuildings.GeneralStorageFunction;
        var store = SyntheticTown.Rec("store", FcsRecordType.BUILDING, "Storage");
        store.Bools["has inventory"] = true; store.References["functionality"] = [SyntheticTown.Ref("store-fn")];
        var data = SyntheticTown.Data(SyntheticTown.Database(extra: [store, function]));
        var production = new ProductionSystem(data.Db, data.Bodies.Constants, data.BodyOptions, data.Items, null);
        using var s = new Scene(false, production);
        Machine Storage(int faction)
        {
            var site = new BuildSite { Id = s.World.Buildings.NewId(WorldLayout.ZoneOf(0, 0)), Record = "store", Owner = faction,
                Position = Vector3.Zero, Complete = true, Materials = [new MaterialSlot("secret-material", 10) { Delivered = 10 }] };
            s.World.Buildings.Add(site);
            var machine = production.Ensure(s.World, site)!;
            machine.Storage!.Items.Add(new ItemInstance { Record = "secret-stock", Quantity = faction });
            return machine;
        }
        var a = Storage(7); var b = Storage(8);
        var first = s.View().Snapshot;
        Assert.Equal(7, first.Machines.Single(m => m.Site == a.Site).AuthorityFaction);
        Assert.Equal(8, first.Machines.Single(m => m.Site == b.Site).AuthorityFaction);
        Assert.Equal(7, first.Buildings.Single(p => p.Id == a.Site).AuthorityFaction);
        Assert.Equal(8, first.Buildings.Single(p => p.Id == b.Site).AuthorityFaction);
        Assert.NotNull(first.Machines.Single(m => m.Site == a.Site).Storage);
        var other = first.Machines.Single(m => m.Site == b.Site);
        Assert.Null(other.Storage); Assert.Empty(other.Inputs); Assert.Null(other.Craft); Assert.Equal(0ul, other.StorageRevision);
        Assert.Single(first.Buildings.Single(p => p.Id == a.Site).Materials);
        Assert.Empty(first.Buildings.Single(p => p.Id == b.Site).Materials);
        s.World.Players.SetMembership(s.A.Id, 7, FactionPermission.ReadFunds);
        var next = s.View().Snapshot;
        Assert.All(next.Machines, m => Assert.Null(m.Storage));
        Assert.NotNull(first.Machines.Single(m => m.Site == a.Site).Storage);
        Assert.Empty(next.OwnedTownBuildings); Assert.Empty(next.Outposts);
        // A corrupt/stale cross-owner wall link has no valid root; the client must not guess from the child owner.
        s.World.Buildings.Find(a.Site)!.WallParent = b.Site;
        Assert.Equal(-1, s.View().Snapshot.Buildings.Single(p => p.Id == a.Site).AuthorityFaction);
    }

    [Fact]
    public void RevisionStampsAreStableAcrossParallelCharacterProjection()
    {
        using var s = new Scene();
        for (int i = 0; i < 600; i++) s.Spawn(s.A, new(i % 50, 0, 0));
        var first = s.View(); var second = s.View();
        Assert.Equal(first.Snapshot.StateRevision, second.Snapshot.StateRevision);
        Assert.True(second.Publication > first.Publication);
        var privateCharacters = first.Snapshot.Characters.Where(c => c.ItemGrid is not null).ToArray();
        Assert.Equal(601, privateCharacters.Length);
        Assert.Equal(601, privateCharacters.Select(c => c.ItemGrid!.Revision).Distinct().Count());
        Assert.Equal(601, privateCharacters.Select(c => c.JobsRevision).Distinct().Count());
        foreach (var c in privateCharacters)
        {
            var same = second.Snapshot.Characters.Single(n => n.Id == c.Id);
            Assert.Equal(c.ItemGrid!.Revision, same.ItemGrid!.Revision); Assert.Equal(c.JobsRevision, same.JobsRevision);
        }
    }
}
