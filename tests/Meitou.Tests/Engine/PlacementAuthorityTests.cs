using Meitou.Simulation.Player;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Navigation;
using Meitou.Runtime.Placement;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Engine;

public class PlacementAuthorityTests
{
    sealed class Scene : IDisposable
    {
        public SimWorld World { get; }
        public WorldPlacement Placement { get; }
        public WorldPlayer A { get; }
        public WorldPlayer B { get; }
        public bool Ready = true, MissingGeometry;
        public Func<Vector3, bool>? Region;
        public float? Resource;
        public Func<float, float, float> Ground = (_, _) => 300;
        public List<PlacedFootprint> Static { get; } = [];
        public static readonly Vector3 At = new(1020, 300, 2000);
        public static PlannedBuilding Plan => new("build", At, Quaternion.Identity);
        readonly BuildingFootprint _footprint = new([new(-5, -5), new(5, -5), new(5, 5), new(-5, 5)], 0, 20);

        public Scene(bool validator = true)
        {
            var build = SyntheticTown.Rec("build", FcsRecordType.Building, "Build");
            build.Ints["build materials"] = 1; build.Floats["max slope"] = 5;
            var parent = SyntheticTown.Rec("parent", FcsRecordType.Building, "Parent");
            var tech = SyntheticTown.Rec("tech", FcsRecordType.Research, "Tech");
            tech.References["enable buildings"] = [SyntheticTown.Ref("build")];
            var db = SyntheticTown.Database(extra: [build, parent, tech]);
            Placement = new WorldPlacement(db, _ => new PlacementServices
            {
                Ground = (x, z) => Ground(x, z),
                Footprint = _ => MissingGeometry ? throw new InvalidDataException("missing collision") : _footprint,
                StaticAround = (_, _) => Static,
                Readout = (_, _) => Resource is { } value ? new PlacementReadout(1, value) : null,
            }, (world, faction, plan) => world.Buildings.Find(plan.Parent!) is { } site
                ? new PlacementSurface(site.Position.Y + 60, new GhostFloor(site.Id,
                    [new(900, 1900), new(1100, 1900), new(1100, 2100), new(900, 2100)], 1, Owned: site.Owner == faction)) : null,
                p => Ready && (Region?.Invoke(p) ?? true), f => f.ToString(), () => []);
            var constants = GameConstants.FromDatabase(db);
            World = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((x, z) => Ground(x, z)),
                [new PlayerSystem(), new ConstructionSystem(db, constants, BodyOptions.Default, new ItemFactory(db, constants), [], "",
                    new ConstructionSettings { Placement = validator ? Placement : null })]);
            A = World.Players.Register(new PlayerId(2)); B = World.Players.Register(new PlayerId(3));
            World.Players.SetMembership(A.Id, 2, FactionPermission.All); World.Players.SetMembership(B.Id, 3, FactionPermission.All);
            A.State.FactionState.Research.Set(db, ["tech"]); B.State.FactionState.Research.Set(db, ["tech"]);
            Role(A, new Vector3(1000, 300, 2000)); Role(B, new Vector3(1000, 300, 2010));
        }
        public CharacterId Role(WorldPlayer owner, Vector3 at)
        {
            var id = World.Characters.Spawn(new CharacterHot { Position = at }, new CharacterCold { IsPlayer = true, Faction = owner.State.Faction }, World.Tick);
            World.Players.AssignInitial(owner.Id, id); return id;
        }
        public void Send(WorldPlayer player, params PlannedBuilding[] plans) => World.Commands.Enqueue(player.Id,
            new PlaceBuildings(plans) { Tick = World.Tick, Faction = player.State.Faction });
        public void Apply() => World.ApplyPausedInterfaceCommands();
        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void CompetingRequestsAndDuplicatesSeeAlreadyCommittedBuildings()
    {
        using var s = new Scene();
        Assert.Empty(s.World.Snapshot.Buildings);
        s.Send(s.A, Scene.Plan, Scene.Plan); s.Send(s.B, Scene.Plan); s.Apply();
        Assert.Equal(2, Assert.Single(s.World.Buildings.All).Owner);
        Assert.Empty(s.World.Snapshot.Buildings); // the checks used live state even with publication disabled
    }

    [Fact]
    public void LiveCharactersAndStaticGeometryBlockPlacement()
    {
        using var s = new Scene();
        s.Send(s.A, Scene.Plan);
        s.Role(s.B, Scene.At); // appeared after the preview/request, before execution
        s.Apply(); Assert.Empty(s.World.Buildings.All);
        var elsewhere = Scene.Plan with { Position = Scene.At + new Vector3(100, 0, 0) };
        s.Static.Add(new("level-building", [new(1110, 1990), new(1130, 1990), new(1130, 2010), new(1110, 2010)]));
        s.Send(s.A, elsewhere); s.Apply(); Assert.Empty(s.World.Buildings.All);
    }

    [Fact]
    public void MissingAuthorityGeometryAndUnreadyRegionsCannotAcceptAPlan()
    {
        using (var noValidator = new Scene(validator: false))
        { noValidator.Send(noValidator.A, Scene.Plan); noValidator.Apply(); Assert.Empty(noValidator.World.Buildings.All); }
        using var s = new Scene();
        s.Ready = false;
        Assert.Equal(PlacementFailure.RegionNotReady, s.Placement.Check(s.World, s.A.Id, 2, Scene.Plan));
        s.Send(s.A, Scene.Plan); s.Apply(); Assert.Empty(s.World.Buildings.All);
        s.Ready = true; s.MissingGeometry = true;
        Assert.Equal(PlacementFailure.NotAvailable, s.Placement.Check(s.World, s.A.Id, 2, Scene.Plan));
        s.Send(s.A, Scene.Plan); s.Apply(); Assert.Empty(s.World.Buildings.All);
        s.MissingGeometry = false; s.Region = p => p.X <= Scene.At.X + 2;
        Assert.Equal(PlacementFailure.RegionNotReady, s.Placement.Check(s.World, s.A.Id, 2, Scene.Plan));
    }

    [Fact]
    public void ResourceSlopeTransformAndObservationAreServerChecked()
    {
        using var s = new Scene();
        s.Resource = 0;
        Assert.Equal(PlacementFailure.NoResources, s.Placement.Check(s.World, s.A.Id, 2, Scene.Plan));
        s.Resource = null; s.Ground = (x, _) => 300 + x - 1000;
        Assert.Equal(PlacementFailure.NotLevel, s.Placement.Check(s.World, s.A.Id, 2, Scene.Plan with { Position = new(1020, 320, 2000) }));
        Assert.Equal(PlacementFailure.InvalidPlan, s.Placement.Check(s.World, s.A.Id, 2, Scene.Plan with { Rotation = default }));
        Assert.Equal(PlacementFailure.InvalidGround, s.Placement.Check(s.World, null, 2, Scene.Plan with { Position = new(1020, 20000, 2000) }));
        Assert.Equal(PlacementFailure.InvalidPlan, s.Placement.Check(s.World, s.A.Id, 2, Scene.Plan with { Position = new(50000, 300, 50000) }));
    }

    [Fact]
    public void ParentOwnershipFloorAndCompletionComeFromCurrentWorldState()
    {
        using var s = new Scene();
        var parent = new BuildSite { Id = "home", Record = "parent", Position = new(1000, 300, 2000), Owner = 2, Complete = true, Materials = [] };
        s.World.Buildings.Add(parent);
        var plan = Scene.Plan with { Parent = parent.Id, Floor = 1, Position = new(1020, 360, 2000) };
        s.Role(s.B, Scene.At); // the floor below does not occupy the upstairs ghost
        Assert.Equal(PlacementFailure.None, s.Placement.Check(s.World, s.A.Id, 2, plan));
        Assert.Equal(PlacementFailure.InvalidFloor, s.Placement.Check(s.World, s.A.Id, 2, plan with { Floor = 3 }));
        s.Send(s.A, plan); parent.Owner = 3; s.Apply(); Assert.Single(s.World.Buildings.All);
        parent.Owner = 2; parent.Complete = false;
        Assert.Equal(PlacementFailure.InsideIncomplete, s.Placement.Check(s.World, s.A.Id, 2, plan));
    }

    [Fact]
    public void RevokedFactionPermissionCannotBeRestoredForAnOldPlacementRequest()
    {
        using var s = new Scene();
        s.Send(s.A, Scene.Plan);
        s.World.Players.SetMembership(s.A.Id, 2, FactionPermission.None);
        s.World.Players.SetMembership(s.A.Id, 2, FactionPermission.All);
        s.Apply(); Assert.Empty(s.World.Buildings.All);
        s.Send(s.A, Scene.Plan); s.Apply(); Assert.Single(s.World.Buildings.All);
    }
}
