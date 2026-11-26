using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Runtime.Placement;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Player;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Engine;

/// <summary>
/// Wall-line preview/authority agreement, including the trust boundary around submitted line intent and queued collision exemptions.
/// </summary>
public class WallPlacementAuthorityTests
{
    /// <summary>
    /// A small authority with deterministic synthetic wall records and real placement/command processing.
    /// </summary>
    sealed class Scene : IDisposable
    {
        public static readonly Vector3 At = new(1000, 300, 2000);
        public readonly SimWorld World;
        public readonly WorldPlacement Placement;
        public readonly WorldPlayer Actor;
        public readonly Dictionary<string, BuildingBuildData> Data;
        public readonly List<(string Key, string Record, Vector3 Position, Quaternion Rotation)> Level = [];
        readonly PlacementServices _services;

        public Scene()
        {
            var wall = Record("wall", 0, 255);
            wall.References["wall subsections"] = [SyntheticTown.Ref("short"), SyntheticTown.Ref("connector"), SyntheticTown.Ref("wedge")];
            var box = Record("box", 0, 10);
            box.Bools["links together"] = false;
            var records = new[] { wall, Record("short", 4, 40), Record("connector", 1, 0), Record("wedge", 2, 0), box };
            Data = records.Select(BuildingBuildData.From).ToDictionary(data => data.Id);
            var tech = SyntheticTown.Rec("wall-tech", FcsRecordType.Research, "Wall tech");
            tech.References["enable buildings"] = [SyntheticTown.Ref("wall"), SyntheticTown.Ref("box")];
            var db = SyntheticTown.Database(extra: [.. records, tech]);
            var constants = GameConstants.FromDatabase(db);
            _services = new PlacementServices
            {
                Ground = Ground,
                Footprint = Footprint,
                StaticAround = (_, _) => Level.Select(item => new PlacedFootprint(item.Key,
                    Footprint(item.Record).Place(item.Position, item.Rotation), item.Record, item.Position, item.Rotation)),
                LinkedAround = (_, _) => Level,
            };
            Placement = new WorldPlacement(db, _ => _services, (_, _, _) => null, _ => true, faction => faction.ToString(), () => []);
            World = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability(Ground),
                [new PlayerSystem(), new ConstructionSystem(db, constants, BodyOptions.Default, new ItemFactory(db, constants), [], "",
                    new ConstructionSettings { Placement = Placement })]);
            Actor = World.Players.Register(new PlayerId(2));
            World.Players.SetMembership(Actor.Id, 2, FactionPermission.All);
            Actor.State.FactionState.Research.Set(db, ["wall-tech"]);
            var role = World.Characters.Spawn(new CharacterHot { Position = At + new Vector3(-200, 0, -200) },
                new CharacterCold { IsPlayer = true, Faction = 2 }, World.Tick);
            World.Players.AssignInitial(Actor.Id, role);
        }

        static FcsRecord Record(string id, int type, float length)
        {
            var record = SyntheticTown.Rec(id, FcsRecordType.Building, id);
            record.Bools["links together"] = true;
            record.Ints["link type"] = type;
            record.Floats["link length"] = length;
            record.Floats["max slope"] = 90;
            record.Ints["build materials"] = 1;
            return record;
        }

        public static float Ground(float x, float z) => 300;

        BuildingFootprint Footprint(string record)
        {
            float half = record == "box" ? 5 : 60;
            float length = MathF.Max(10, Data[record].LinkLength);
            return new BuildingFootprint([new(-half, 0), new(half, 0), new(half, length), new(-half, length)], 0, 20);
        }

        public void Add(string id, string record, Vector3 position, Quaternion rotation, bool level = false)
        {
            if (level) Level.Add((id, record, position, rotation));
            else World.Buildings.Add(new BuildSite
            {
                Id = id, Record = record, Position = position, Rotation = rotation, Owner = 3, Complete = true, Materials = [],
            });
        }

        public PlaceBuildings Line(Vector3 start, Vector3 end, WallSnap? snappedStart = null, WallSnap? snappedEnd = null, bool reversed = false)
        {
            var pieces = WallLine.Layout(Data["wall"], Data.GetValueOrDefault, Ground, start, snappedStart, end, snappedEnd, reversed);
            var intent = new WallLineIntent("wall", start, end, snappedStart is not null, snappedEnd is not null, reversed);
            return new PlaceBuildings(pieces.Select(piece => new PlannedBuilding(piece.Data.Id, piece.Position, piece.Rotation)).ToArray())
            {
                WallLines = [new PlannedWallLine(0, pieces.Count, intent)],
            };
        }

        public void Send(PlaceBuildings command)
        {
            World.Commands.Enqueue(Actor.Id, command with { Tick = World.Tick, Faction = 2 });
            World.ApplyPausedInterfaceCommands();
        }

        public void Dispose() => World.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TurningLineUsesShortReachForLiveAndLevelWallsOfAnotherFaction(bool level)
    {
        using var scene = new Scene();
        var rotation = WallLine.LookRotation(Vector3.UnitX);
        scene.Add("penultimate", "short", Scene.At - new Vector3(80, 0, 0), rotation, level);
        scene.Add("last", "short", Scene.At - new Vector3(40, 0, 0), rotation, level);
        var command = scene.Line(Scene.At, Scene.At + new Vector3(0, 0, 800), new WallSnap(Scene.At, Vector3.UnitX, 1));
        Assert.Equal("short", command.Plans[0].Record);

        scene.Send(command);

        Assert.Equal(command.Plans.Count, scene.World.Buildings.All.Count(site => site.Owner == 2));
    }

    [Fact]
    public void ClaimedSnapWithoutAnActualNodeDoesNotAuthorizeAnyPiece()
    {
        using var scene = new Scene();
        var command = scene.Line(Scene.At, Scene.At + new Vector3(0, 0, 800), new WallSnap(Scene.At, Vector3.UnitX, 1));

        scene.Send(command);

        Assert.Empty(scene.World.Buildings.All);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedTransformOrReorderedPiecesCannotBorrowALinesPass(bool reordered)
    {
        using var scene = new Scene();
        var command = scene.Line(Scene.At, Scene.At + new Vector3(0, 0, 800));
        var plans = command.Plans.ToArray();
        if (reordered) (plans[0], plans[1]) = (plans[1], plans[0]);
        else plans[1] = plans[1] with { Position = plans[1].Position + new Vector3(10, 0, 0) };

        scene.Send(command with { Plans = plans });

        Assert.Empty(scene.World.Buildings.All);
    }

    [Fact]
    public void QueuedConnectorPassDoesNotExtendToAnAlreadyPlacedConnector()
    {
        using var placed = new Scene();
        var wall = new PlannedBuilding("wall", Scene.At, Quaternion.Identity);
        placed.Add("old-connector", "connector", Scene.At, Quaternion.Identity);
        placed.Send(new PlaceBuildings([wall]));
        Assert.DoesNotContain(placed.World.Buildings.All, site => site.Owner == 2);

        using var queued = new Scene();
        queued.Send(new PlaceBuildings([new PlannedBuilding("connector", Scene.At, Quaternion.Identity), wall]));
        Assert.Equal(2, queued.World.Buildings.All.Count());
    }

    [Fact]
    public void ForwardAndReversedLinesCanShareABatchWithAnOrdinaryBuilding()
    {
        using var scene = new Scene();
        var secondStart = Scene.At + new Vector3(800, 0, 0);
        scene.Add("reverse-anchor", "wall", secondStart, Quaternion.Identity);
        var first = scene.Line(Scene.At, Scene.At + new Vector3(0, 0, 800));
        var second = scene.Line(secondStart, secondStart - new Vector3(0, 0, 800),
            new WallSnap(secondStart, Vector3.UnitZ, 2), reversed: true);
        var ordinary = new PlannedBuilding("box", Scene.At - new Vector3(500, 0, 0), Quaternion.Identity);
        var plans = new[] { ordinary }.Concat(first.Plans).Concat(second.Plans).ToArray();
        var command = new PlaceBuildings(plans)
        {
            WallLines = [first.WallLines[0] with { FirstPlan = 1 }, second.WallLines[0] with { FirstPlan = first.Plans.Count + 1 }],
        };

        scene.Send(command);

        Assert.Equal(plans.Length, scene.World.Buildings.All.Count(site => site.Owner == 2));
    }

    [Fact]
    public void ALineCannotIgnoreAnOverlappingNonWallBuilding()
    {
        using var scene = new Scene();
        scene.Add("anchor", "short", Scene.At - new Vector3(40, 0, 0), WallLine.LookRotation(Vector3.UnitX));
        var command = scene.Line(Scene.At, Scene.At + new Vector3(0, 0, 800), new WallSnap(Scene.At, Vector3.UnitX, 1));
        scene.Add("obstacle", "box", Scene.At, Quaternion.Identity);

        var batch = scene.Placement.BeginBatch(scene.World, scene.Actor.Id, 2, command);

        Assert.Equal(PlacementFailure.TooCloseToBuilding, batch.Check(0));
    }

    [Fact]
    public void ARejectedEarlierLineCannotBeUsedAsAContinuationNode()
    {
        using var scene = new Scene();
        var first = scene.Line(Scene.At, Scene.At + new Vector3(0, 0, 800));
        var last = first.Plans[^1];
        var next = WallLine.Ends(scene.Data[last.Record], last.Position, last.Rotation).End;
        var second = scene.Line(next, next + new Vector3(800, 0, 0), new WallSnap(next, Vector3.UnitZ, 1));
        var bad = first.Plans.Select(plan => plan with { Position = plan.Position + new Vector3(10, 0, 0) });
        var command = new PlaceBuildings(bad.Concat(second.Plans).ToArray())
        {
            WallLines = [first.WallLines[0], second.WallLines[0] with { FirstPlan = first.Plans.Count }],
        };

        scene.Send(command);

        Assert.Empty(scene.World.Buildings.All);
    }

    [Fact]
    public void ChainedLineUsesTheAcceptedPreviousLinesOriginalDirection()
    {
        using var scene = new Scene();
        scene.Add("anchor", "short", Scene.At - new Vector3(40, 0, 0), WallLine.LookRotation(Vector3.UnitX));
        var first = scene.Line(Scene.At, Scene.At + new Vector3(0, 0, 800), new WallSnap(Scene.At, Vector3.UnitX, 1));
        var last = first.Plans[^1];
        var next = WallLine.Ends(scene.Data[last.Record], last.Position, last.Rotation).End;
        var second = scene.Line(next, next + new Vector3(800, 0, 0), new WallSnap(next, Vector3.UnitZ, 1));
        var command = new PlaceBuildings(first.Plans.Concat(second.Plans).ToArray())
        {
            WallLines = [first.WallLines[0], second.WallLines[0] with { FirstPlan = first.Plans.Count }],
        };

        scene.Send(command);

        Assert.Equal(command.Plans.Count, scene.World.Buildings.All.Count(site => site.Owner == 2));
    }

    [Fact]
    public void UndoCanRetainAVerifiedPrefixOfALine()
    {
        using var scene = new Scene();
        var full = scene.Line(Scene.At, Scene.At + new Vector3(0, 0, 800));
        var command = full with { Plans = full.Plans.Take(2).ToArray(), WallLines = [full.WallLines[0] with { PieceCount = 2 }] };

        scene.Send(command);

        Assert.Equal(2, scene.World.Buildings.All.Count());
    }
}
