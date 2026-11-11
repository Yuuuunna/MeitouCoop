using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Engine;

public class RegionRetentionTests
{
    static readonly Vector3 Here = new(1000, 300, 2000), Far = new(50000, 300, 50000);
    sealed class Navigation : IRegionNavigation
    {
        public HashSet<ZoneCoordinate> Loaded { get; } = [];
        public HashSet<ZoneCoordinate> Hints { get; } = [];
        public List<ZoneCoordinate> Removed { get; } = [];
        public IReadOnlyCollection<ZoneCoordinate> Tracked => Loaded.ToArray();
        public IEnumerable<ZoneCoordinate> RouteHints => Hints;
        public bool Ready(ZoneCoordinate zone) => Loaded.Contains(zone);
        public void Request(ZoneCoordinate zone) => Loaded.Add(zone);
        public void Unload(ZoneCoordinate zone) { Loaded.Remove(zone); Removed.Add(zone); }
    }
    static CharacterId Spawn(SimWorld world, Vector3 at, bool player = true) => world.Characters.Spawn(new CharacterHot { Position = at },
        new CharacterCold { Faction = 2, IsPlayer = player }, world.Tick);

    [Fact]
    public void OnePlayerLeavingCannotReleaseTheOtherPlayersZoneAndIdleZonesExpireWhilePaused()
    {
        double now = 0; var navigation = new Navigation();
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300),
            [new WorldRegionNavigation(navigation, idleSeconds: 30, clock: () => now)]);
        var world = runtime.World;
        world.Players.Register(new PlayerId(2)); world.Players.SetMembership(new PlayerId(2), 2, FactionPermission.All);
        world.Players.Register(new PlayerId(3)); world.Players.SetMembership(new PlayerId(3), 2, FactionPermission.All);
        var a = Spawn(world, Here); var b = Spawn(world, Here);
        world.Players.AssignInitial(new PlayerId(2), a); world.Players.AssignInitial(new PlayerId(3), b);
        var zone = WorldLayout.ZoneOf(Here.X, Here.Z);
        runtime.Speed = 0; runtime.Advance(0);
        Assert.Equal(2, world.Regions.Capture()[zone].Sources); Assert.Contains(zone, navigation.Loaded);
        world.Characters.Place(a.Slot, Far, 0); now = 100; runtime.Advance(0);
        Assert.Contains(zone, navigation.Loaded); Assert.DoesNotContain(zone, navigation.Removed);
        world.Characters.Place(b.Slot, Far, 0); now = 129; runtime.Advance(0); Assert.Contains(zone, navigation.Loaded);
        now = 131; runtime.Advance(0); Assert.DoesNotContain(zone, navigation.Loaded);
        Assert.Contains(WorldLayout.ZoneOf(Far.X, Far.Z), navigation.Loaded); Assert.Equal(0, world.Tick);
    }

    [Fact]
    public void PreparationLeasesAreIndependentAndNavigationOnlyLeasesDoNotActivatePopulation()
    {
        double now = 0; var navigation = new Navigation();
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300),
            [new WorldRegionNavigation(navigation, idleSeconds: 10, clock: () => now)]);
        var zone = WorldLayout.ZoneOf(Far.X, Far.Z);
        using var first = runtime.World.Regions.Retain([zone], simulation: false);
        using var second = runtime.World.Regions.Retain([zone], simulation: false);
        Assert.Empty(runtime.World.Regions.Capture()); Assert.Equal(2, runtime.World.Regions.Capture(simulation: false)[zone].Sources);
        runtime.Synchronize(); first.Dispose(); now = 100; runtime.Synchronize(); Assert.Contains(zone, navigation.Loaded);
        second.Dispose(); now = 111; runtime.Synchronize(); Assert.DoesNotContain(zone, navigation.Loaded);
    }

    [Fact]
    public void OldWorldLeasesCannotPinTheReplacementWorld()
    {
        double now = 0; var navigation = new Navigation();
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300),
            [new WorldRegionNavigation(navigation, idleSeconds: 10, clock: () => now)]);
        var zone = WorldLayout.ZoneOf(Far.X, Far.Z);
        using var oldLease = runtime.World.Regions.Retain([zone]); runtime.Synchronize(); Assert.Contains(zone, navigation.Loaded);
        runtime.ReplaceWorld([new WorldRegionNavigation(navigation, idleSeconds: 10, clock: () => now)]);
        now = 11; runtime.Synchronize(); Assert.DoesNotContain(zone, navigation.Loaded);
    }

    [Fact]
    public void CapturedRolesFollowTheirCarrierAndCombatHoldsARegionWithoutAnObserver()
    {
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300));
        var body = Spawn(world, Here); var carrier = Spawn(world, Far, player: false);
        world.Characters.Cold(body.Slot)!.CarriedBy = carrier;
        var demand = world.Regions.Capture();
        Assert.DoesNotContain(WorldLayout.ZoneOf(Here.X, Here.Z), demand.Keys);
        Assert.True((demand[WorldLayout.ZoneOf(Far.X, Far.Z)].Reasons & RegionReason.PlayerRole) != 0);
        world.Characters.Cold(body.Slot)!.DiedTick = 0; world.Characters.Cold(carrier.Slot)!.InCombat = true;
        Assert.True((world.Regions.Capture()[WorldLayout.ZoneOf(Far.X, Far.Z)].Reasons & RegionReason.Combat) != 0);
        world.Characters.Cold(carrier.Slot)!.InCombat = false; Assert.Empty(world.Regions.Capture());
        Assert.True((world.Regions.Capture(simulation: false)[WorldLayout.ZoneOf(Far.X, Far.Z)].Reasons & RegionReason.LoadedEntity) != 0);
    }

    [Fact]
    public void ActiveBaseKeepsItsOwnSupportBuildingsButNotAnotherFactionsIdleBase()
    {
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300));
        world.Players.SetMembership(PlayerId.Local, 2, FactionPermission.All);
        var site = new BuildSite { Id = "furnace", Record = "furnace", Position = Here, Owner = 2, Town = 1, Complete = true, Materials = [] };
        world.Buildings.Add(site);
        world.Buildings.Add(new BuildSite { Id = "support", Record = "generator", Position = Far, Owner = 2, Town = 1, Complete = true, Materials = [] });
        var foreign = new Vector3(-50000, 300, -50000);
        world.Buildings.Add(new BuildSite { Id = "foreign", Record = "generator", Position = foreign, Owner = 3, Town = 1, Complete = true, Materials = [] });
        var machine = new Machine { Site = site.Id, Position = Here, Burning = true,
            Data = new MachineData { Id = "furnace", Name = "Furnace", Function = MachineData.ItemFurnaceFunction, Output = new("iron", 10) } };
        world.Machines.Add(machine);
        var demand = world.Regions.Capture();
        Assert.Contains(WorldLayout.ZoneOf(Here.X, Here.Z), demand.Keys); Assert.Contains(WorldLayout.ZoneOf(Far.X, Far.Z), demand.Keys);
        Assert.DoesNotContain(WorldLayout.ZoneOf(foreign.X, foreign.Z), demand.Keys);
        machine.Burning = false; Assert.Empty(world.Regions.Capture());
    }

    [Fact]
    public void SinglePlayerPopulationZonesRequestNavigationBeforeZoneGateOpens()
    {
        var navigation = new Navigation();
        var data = Meitou.Tests.Simulation.SyntheticTown.Data(Meitou.Tests.Simulation.SyntheticTown.Database());
        var population = new PopulationSystem(data, new PopulationSettings { CheckEveryTicks = 1 }) { ZoneGate = navigation.Ready };
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300),
            [population, new WorldRegionNavigation(navigation, idleSeconds: 10, clock: () => 0)]);
        Assert.False(runtime.World.Players.RequiresExplicitControl);
        Assert.False(runtime.World.PreparePopulation(Meitou.Tests.Simulation.SyntheticTown.Centre));
        Assert.Empty(runtime.World.ActiveZones!);
        Assert.NotEmpty(population.ActiveZones);
        runtime.Synchronize();
        Assert.Subset(navigation.Loaded, population.ActiveZones.ToHashSet());
        Assert.True(runtime.World.PreparePopulation(Meitou.Tests.Simulation.SyntheticTown.Centre));
        Assert.True(runtime.World.Characters.Count > 0);
    }
}
