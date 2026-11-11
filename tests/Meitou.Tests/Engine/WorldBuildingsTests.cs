using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Engine;

public class WorldBuildingsTests
{
    sealed class Sink : IRuntimeBuildingSink
    {
        public Dictionary<string, BuildingPlacement> Entries { get; } = new(StringComparer.Ordinal);
        public string? FailNextIdentity;
        public string? FailNextRemoval;
        public int Adds;
        public void Add(BuildingPlacement placement)
        {
            Entries[placement.Key] = placement; Adds++;
            if (FailNextIdentity != placement.Key) return;
            FailNextIdentity = null;
            throw new InvalidDataException("failed after the overlay write");
        }
        public void Remove(string identity)
        {
            if (FailNextRemoval == identity) { FailNextRemoval = null; throw new IOException("transient remove failure"); }
            Entries.Remove(identity);
        }
    }

    static GameDatabase Database()
    {
        var solid = SyntheticTown.Rec("solid", FcsRecordType.Building, "Solid"); solid.Ints["path mode"] = 2; solid.Ints["build materials"] = 1;
        var threshold = SyntheticTown.Rec("threshold", FcsRecordType.Building, "Threshold"); threshold.Ints["path mode"] = 2; threshold.Floats["build threshold"] = 50;
        var ignored = SyntheticTown.Rec("ignored", FcsRecordType.Building, "Ignored"); ignored.Ints["path mode"] = 0;
        var unlock = SyntheticTown.Rec("unlock", FcsRecordType.Research, "Unlock"); unlock.References["enable buildings"] = [SyntheticTown.Ref("solid")];
        return SyntheticTown.Database(extra: [solid, threshold, ignored, unlock]);
    }
    static BuildSite Site(string id, string record = "solid", bool complete = false, BuildingPlacement? level = null) => new()
    { Id = id, Record = record, Position = new(1000, 300, 2000), Owner = 2, Materials = [], Complete = complete, LevelPlacement = level };
    static WorldRuntime Runtime(GameDatabase db, Sink sink, params ITickSystem[] systems) => new(new WorldSettings { PublishSnapshots = false },
        new OpenGroundWalkability((_, _) => 300), [.. systems, new WorldBuildings(db, sink)]);

    [Fact]
    public void PauseEditsUpdateNavigationWithoutATickOrPublishedSnapshot()
    {
        var db = Database(); var sink = new Sink(); var constants = GameConstants.FromDatabase(db);
        using var runtime = Runtime(db, sink, new ConstructionSystem(db, constants, BodyOptions.Default, new ItemFactory(db, constants), [], ""));
        var world = runtime.World; world.Player.Faction = 2; world.Research.Set(db, ["unlock"]);
        world.Commands.Enqueue(new PlaceBuildings([new("solid", new(1000, 300, 2000), Quaternion.Identity)]) { Tick = 0 });
        runtime.ApplyPausedInterfaceCommands();
        var site = Assert.Single(world.Buildings.All);
        Assert.Equal(site.Id, Assert.Single(sink.Entries).Key); Assert.Equal(0, world.Tick); Assert.Empty(world.Snapshot.Buildings);
        world.Commands.Enqueue(new ConfirmDismantle(site.Id) { Tick = 0 }); runtime.ApplyPausedInterfaceCommands();
        Assert.Empty(world.Buildings.All); Assert.Empty(sink.Entries); Assert.Equal(0, world.Tick);
    }

    [Fact]
    public void CompletionUpgradeRemovalAndRuinedLevelOverridesFollowTheLiveWorld()
    {
        var db = Database(); var sink = new Sink(); using var runtime = Runtime(db, sink);
        var site = Site("a", "threshold"); runtime.World.Buildings.Add(site); runtime.Synchronize(); Assert.Empty(sink.Entries);
        site.Complete = true; runtime.World.Buildings.Changed(); runtime.Step();
        Assert.Equal("threshold", Assert.Single(sink.Entries).Value.BuildingId);
        int writes = sink.Adds; site.Progress++; runtime.World.Buildings.Touch(); runtime.Step(); Assert.Equal(writes, sink.Adds);
        site.Record = "solid"; site.Complete = false; runtime.World.Buildings.Changed(); runtime.Synchronize();
        Assert.Equal("solid", Assert.Single(sink.Entries).Value.BuildingId);
        site.Record = "ignored"; runtime.World.Buildings.Changed(); runtime.Synchronize(); Assert.Empty(sink.Entries);
        var placement = new BuildingPlacement(WorldLayout.ZoneOf(1000, 2000), "level", "solid", new(1000, 300, 2000), Quaternion.Identity, null, 300) { Key = "level-key" };
        var level = Site("level-key", "solid", level: placement); level.Destroyed = true;
        runtime.World.Buildings.Add(level); runtime.Synchronize();
        Assert.True(Assert.Single(sink.Entries).Value.DestroyedOverride);
        runtime.World.Buildings.Remove(level); runtime.Synchronize(); Assert.Empty(sink.Entries);
    }

    [Fact]
    public void CandidateSetupCannotPublishGeometryAndSuccessfulReplacementRetiresTheOldWorld()
    {
        var db = Database(); var sink = new Sink(); using var runtime = Runtime(db, sink);
        var old = runtime.World; old.Buildings.Add(Site("old")); runtime.Synchronize();
        Assert.Throws<InvalidDataException>(() => runtime.ReplaceWorld([new WorldBuildings(db, sink)], next =>
        {
            next.Buildings.Add(Site("candidate")); next.RunTick();
            Assert.Equal("old", Assert.Single(sink.Entries).Key);
            throw new InvalidDataException("bad save");
        }));
        Assert.Same(old, runtime.World); Assert.Equal("old", Assert.Single(sink.Entries).Key);
        runtime.ReplaceWorld([new WorldBuildings(db, sink)], next => next.Buildings.Add(Site("new")));
        Assert.Equal("new", Assert.Single(sink.Entries).Key);
        runtime.Dispose(); Assert.Empty(sink.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartiallyFailedActivationRestoresTheOldOverlayAndRuntime(bool cleanupFailsOnce)
    {
        var db = Database(); var sink = new Sink(); using var runtime = Runtime(db, sink);
        var old = runtime.World; old.Buildings.Add(Site("old")); runtime.Synchronize();
        sink.FailNextIdentity = "candidate";
        if (cleanupFailsOnce) sink.FailNextRemoval = "candidate";
        Assert.Throws<AggregateException>(() => runtime.ReplaceWorld([new WorldBuildings(db, sink)], next => next.Buildings.Add(Site("candidate"))));
        Assert.Same(old, runtime.World); Assert.False(runtime.Faulted); Assert.Equal("old", Assert.Single(sink.Entries).Key);
        runtime.Step(); Assert.Equal(1, old.Tick);
    }

    [Fact]
    public void AFailedPausedBoundaryFaultsTheRuntimeUntilReplacement()
    {
        var db = Database(); var sink = new Sink(); using var runtime = Runtime(db, sink);
        runtime.World.Buildings.Add(Site("bad")); sink.FailNextIdentity = "bad";
        Assert.Throws<InvalidDataException>(() => runtime.ApplyPausedInterfaceCommands()); Assert.True(runtime.Faulted);
        Assert.Throws<InvalidOperationException>(() => runtime.Step());
        runtime.ReplaceWorld([new WorldBuildings(db, sink)]);
        Assert.False(runtime.Faulted); Assert.Empty(sink.Entries); runtime.Step();
    }
}
