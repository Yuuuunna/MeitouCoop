using System.Numerics;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Tests.Engine;
namespace Meitou.Tests.NetworkingNext;

public class GameStateReplicationTests
{
    static PlayerWorldView WithoutAnimation(PlayerWorldView view) => view with { Snapshot = view.Snapshot with { Characters = view.Snapshot.Characters.Select(c => c with { Animations = [] }).ToArray() } };
    static void Apply(GameStateReplica replica, GameStatePublication publication) { foreach (var frame in publication.Frames) replica.Apply(frame); }
    [Fact]
    public void CompleteAuthorizedViewRoundTripsThroughBoundedBinaryFieldFrames()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var source = WithoutAnimation(WorldViewCodecTests.View(db));
        var publisher = new GameStatePublisher(codec, id => (ulong)id.Slot + 1, 11, 22);
        var baseline = publisher.Capture(source); var replica = new GameStateReplica(codec, 11, 22);
        Assert.All(baseline.Frames, f => Assert.InRange(f.Length, 77, 4096));
        Apply(replica, baseline);
        Assert.Equal(codec.Encode(source), codec.Encode(replica.View!));
        var unchanged = publisher.Capture(source with { Publication = 2, Snapshot = source.Snapshot with { Tick = source.Snapshot.Tick + 1 } });
        Assert.True(unchanged.Frames.Sum(f => f.Length) < baseline.Frames.Sum(f => f.Length));
        Assert.All(unchanged.Motion, m => Assert.Equal(1u, m.Record.Revision));
    }
    [Fact]
    public void PrivateWithdrawalAndDespawnFenceLateBulkCompletion()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var source = WithoutAnimation(WorldViewCodecTests.View(db));
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22); var baseline = publisher.Capture(source);
        var character = source.Snapshot.Characters[0];
        var changed = source with { Publication = 2, Snapshot = source.Snapshot with { Characters = [character with { ItemGrid = null, Name = "changed" }] } };
        var delta = publisher.Capture(changed); var replica = new GameStateReplica(codec, 11, 22);
        Apply(replica, delta); Assert.Null(replica.View); Apply(replica, baseline);
        Assert.Null(replica.View!.Snapshot.Characters[0].ItemGrid); Assert.Equal("changed", replica.View.Snapshot.Characters[0].Name);
        var gone = publisher.Capture(changed with { Publication = 3, Snapshot = changed.Snapshot with { Characters = [] } });
        var withdrawn = new GameStateReplica(codec, 11, 22); Apply(withdrawn, gone); Apply(withdrawn, baseline);
        Assert.Empty(withdrawn.View!.Snapshot.Characters);
    }
    [Fact]
    public void ReentryChangesGenerationAndRejectsOldMotion()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var source = WithoutAnimation(WorldViewCodecTests.View(db));
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22); var replica = new GameStateReplica(codec, 11, 22);
        Apply(replica, publisher.Capture(source));
        Apply(replica, publisher.Capture(source with { Publication = 2, Snapshot = source.Snapshot with { Characters = [] } }));
        var reentry = publisher.Capture(source with { Publication = 3 }); Apply(replica, reentry);
        Assert.Equal(2u, Assert.Single(reentry.Motion).Record.InterestGeneration);
        Assert.False(replica.ApplyMotion(new(new(11,22,1,100,2), [new(42,1,99,Vector3.One,Vector3.Zero,0,MotionFlags.None)])));
    }
    [Fact]
    public void MotionSamplesServerTickHistoryAndDiscontinuityClearsIt()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var source = WithoutAnimation(WorldViewCodecTests.View(db));
        var replica = new GameStateReplica(codec, 11, 22); var publisher = new GameStatePublisher(codec, _ => 42, 11, 22); Apply(replica, publisher.Capture(source));
        Apply(replica, publisher.Capture(source with { Publication = 2, Snapshot = source.Snapshot with { Tick = 100 } }));
        replica.ApplyMotion(new(new(11,22,1,100,2), [new(42,1,1,Vector3.Zero,Vector3.Zero,0,MotionFlags.Moving)]));
        Assert.True(replica.ApplyMotion(new(new(11,22,2,110,3), [new(42,1,2,new(10,0,0),Vector3.Zero,0,MotionFlags.Moving)])));
        // UDP motion immediately drives presentation even while unrelated reliable state is delayed.
        Assert.Equal(110ul, replica.LatestServerTick); Assert.Equal(100, replica.View!.Snapshot.Tick);
        Assert.Equal(5, replica.Present(105)!.Snapshot.Characters[0].Position.X);
        Assert.Equal(new GamePresentationSource(3,110,2,2), replica.PresentationSource(source.Snapshot.Characters[0].Id));
        Apply(replica, publisher.Capture(source with { Publication = 4, Snapshot = source.Snapshot with { Tick = 120 } }));
        replica.ApplyMotion(new(new(11,22,3,120,4), [new(42,1,3,new(100,0,0),Vector3.Zero,0,MotionFlags.Discontinuity)]));
        Assert.Equal(100, replica.Present(106)!.Snapshot.Characters[0].Position.X);
    }
    [Fact]
    public void DrawFacingSamplesAuthorityTickHistoryAcrossPiAndDelayedLifecycle()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var source = WithoutAnimation(WorldViewCodecTests.View(db));
        var replica = new GameStateReplica(codec, 11, 22); var publisher = new GameStatePublisher(codec, _ => 42, 11, 22);
        Apply(replica, publisher.Capture(source));
        replica.ApplyMotion(new(new(11,22,1,100,2), [new(42,1,1,Vector3.Zero,Vector3.Zero,0,MotionFlags.Moving, 3f)]));
        replica.ApplyMotion(new(new(11,22,2,110,3), [new(42,1,2,new(10,0,0),Vector3.Zero,0,MotionFlags.Moving, -3f)]));
        var midway = replica.Present(105)!.Snapshot.Characters[0];
        Assert.Equal(5, midway.Position.X); Assert.Equal(MathF.PI, midway.DrawYaw, 5);
        var changed = source with { Publication = 4, Snapshot = source.Snapshot with { Tick = 110,
            Characters = [source.Snapshot.Characters[0] with { DrawYaw = -2f }] } };
        Apply(replica, publisher.Capture(changed));
        Assert.Equal(MathF.PI, replica.Present(105)!.Snapshot.Characters[0].DrawYaw, 5);
        Assert.Equal(-3f, replica.Present(110)!.Snapshot.Characters[0].DrawYaw, 5);
    }

    [Fact]
    public void AnimationTimelineIsSampledWithoutRawLayerTimeChanges()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var source = WithoutAnimation(WorldViewCodecTests.View(db));
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22); var replica = new GameStateReplica(codec, 11, 22);
        var state = new AnimationPlaybackState(1,0,1f/30,0,1000,[new("walk",-1,1,0,1,AnimationWrap.Loop,1,1,1,0,AnimationBlendArea.All,true,false)]);
        Apply(replica, publisher.Capture(source, new Dictionary<ulong,AnimationPlaybackState> { [42] = state }));
        var sampled = Assert.Single(replica.Present(15)!.Snapshot.Characters[0].Animations);
        Assert.Equal(.5f, sampled.Time, 5);
    }
    [Fact]
    public void LargeSingleFieldUsesBoundedBulkFramesAndCapturesDetachedBytes()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var source = WithoutAnimation(WorldViewCodecTests.View(db));
        string[] inventory = Enumerable.Range(0, 6000).Select(i => $"item-reference-{i:D6}").ToArray();
        source = source with { Snapshot = source.Snapshot with { Characters = [source.Snapshot.Characters[0] with { Inventory = inventory }] } };
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22); var baseline = publisher.Capture(source);
        Assert.True(baseline.RequiresBulk); Assert.True(baseline.Frames.Count > 20); Assert.All(baseline.Frames, f => Assert.True(f.Length <= 4096));
        inventory[0] = "mutated-after-capture";
        var replica = new GameStateReplica(codec, 11, 22);
        foreach (var frame in baseline.Frames.Take(baseline.Frames.Count - 1)) { Assert.False(replica.Apply(frame)); Assert.Null(replica.View); }
        Assert.True(replica.Apply(baseline.Frames[^1]));
        Assert.Equal("item-reference-000000", replica.View!.Snapshot.Characters[0].Inventory[0]);
    }
    [Fact]
    public void PureClockCaptureDoesNotRepeatAnyEntityDeclarationsOrFields()
    {
        var db = WorldViewCodecTests.Database(); var source = WithoutAnimation(WorldViewCodecTests.View(db));
        var publisher = new GameStatePublisher(new WorldViewCodec(db), _ => 42, 11, 22); publisher.Capture(source);
        var delta = publisher.Capture(source with { Publication = 2, Snapshot = source.Snapshot with { Tick = source.Snapshot.Tick + 1 } });
        var frame = Assert.Single(delta.Frames);
        // Fixed frame + recipient/clock body only: no record (including entity identity) is repeated.
        Assert.Equal(0, BitConverter.ToInt32(frame, frame.Length - 4));
        Assert.True(frame.Length < 200);
    }

    [Fact]
    public void MotionOnlyContinuesDuringBulkWithoutAdvancingComponentBaselineOrAnnouncingUnknowns()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var source = WithoutAnimation(WorldViewCodecTests.View(db));
        var publisher = new GameStatePublisher(codec, id => (ulong)id.Slot + 1, 11, 22); publisher.Capture(source);
        var moved = source.Snapshot.Characters[0] with { Position = Vector3.One, Name = "deferred" };
        var next = source with { Publication = 2, Snapshot = source.Snapshot with { Tick = 10, Characters = [moved,moved with { Id = new(2,1) }] } };
        Assert.Equal(2ul, Assert.Single(publisher.CaptureMotionOnly(next).Motion).Record.EntityId);
        var delta = publisher.Capture(next); Assert.Equal(2ul,delta.Sequence); Assert.Contains(delta.ComponentBytes.Keys,id => GameStatePublisher.ComponentName(id)=="Name");
        var removed = publisher.CaptureMotionOnly(next with { Snapshot = next.Snapshot with { Characters = [] } }); Assert.Equal(2,removed.WithdrawnMotion.Count);
        Assert.Empty(publisher.CaptureMotionOnly(next).Motion);
        Assert.All(publisher.Capture(next with { Publication = 3 }).Motion,m=>Assert.Equal(2u,m.Record.InterestGeneration));
    }
    [Fact]
    public void ExplicitDestructionAndScopeWithdrawalRemainDistinctAcrossLateBaseline()
    {
        var db = WorldViewCodecTests.Database(); var source = WithoutAnimation(WorldViewCodecTests.View(db)); var codec = new WorldViewCodec(db);
        source = source with { Snapshot = source.Snapshot with { Characters = [source.Snapshot.Characters[0],source.Snapshot.Characters[0] with { Id = new(2,1) }] } };
        var publisher = new GameStatePublisher(codec,id=>(ulong)id.Slot+1,11,22); var baseline = publisher.Capture(source);
        var gone = publisher.Capture(source with { Publication = 2,Snapshot = source.Snapshot with { Characters = [] } },destroyedEntities:new HashSet<ulong>{3});
        var replica = new GameStateReplica(codec,11,22); Apply(replica,gone); Apply(replica,baseline);
        Assert.Empty(replica.View!.Snapshot.Characters);
        Assert.Contains(replica.LastDepartures,d=>d.Entity==2 && d.Reason==GameDepartureReason.OutOfScope);
        Assert.Contains(replica.LastDepartures,d=>d.Entity==3 && d.Reason==GameDepartureReason.Destroyed);
    }
    [Fact]
    public void ReceiverClockConsumesRenewalsForEightySecondsWithoutRendererCalls()
    {
        var db = WorldViewCodecTests.Database(); var source = WithoutAnimation(WorldViewCodecTests.View(db)); var codec = new WorldViewCodec(db);
        source = source with { TimeScale = 1,Snapshot = source.Snapshot with { Tick = 0 } };
        var publisher = new GameStatePublisher(codec,_=>42,11,22); var replica = new GameStateReplica(codec,11,22); var clock = new RemotePresentationClock();
        for (int i=0;i<=400;i++)
        {
            ulong tick=(ulong)i*6,epoch=(ulong)i+1;
            var animation=new AnimationPlaybackState(epoch,tick,1f/30,0,tick+900,[new("walk",-1,1,0,1,AnimationWrap.Loop,1,1,1,0,AnimationBlendArea.All,true,false)]);
            Apply(replica,publisher.Capture(source with { Publication=epoch,Snapshot=source.Snapshot with { Tick=(long)tick } },new Dictionary<ulong,AnimationPlaybackState>{{42,animation}}));
            double presentation=clock.Observe(replica.LatestServerTick,1,1d/30,TimeSpan.FromSeconds(i*.2));
            Assert.InRange(tick-presentation,0,3.000001); Assert.NotNull(replica.Present(presentation));
        }
        Assert.True(replica.AnimationTimelines[42].AppliedEpoch>=400);
    }

    [Fact]
    public void MotionPresentationExtrapolatesAuthoritativeVelocityOnlyWithinItsBound()
    {
        var db=WorldViewCodecTests.Database();var codec=new WorldViewCodec(db);var source=WithoutAnimation(WorldViewCodecTests.View(db));
        source=source with{TimeScale=1,Snapshot=source.Snapshot with{Tick=100}};
        var publisher=new GameStatePublisher(codec,_=>42,11,22);var replica=new GameStateReplica(codec,11,22);Apply(replica,publisher.Capture(source));
        replica.ApplyMotion(new(new(11,22,1,100,2),[new(42,1,1,Vector3.Zero,new(30,0,0),.75f,MotionFlags.Moving,1.25f)]));
        Assert.Equal(3,replica.Present(103)!.Snapshot.Characters[0].Position.X,5);
        var capped=replica.Present(130)!.Snapshot.Characters[0];Assert.Equal(7.5f,capped.Position.X);Assert.Equal(.75f,capped.Yaw);Assert.Equal(1.25f,capped.DrawYaw);
        Assert.Equal(0,replica.View!.Snapshot.Characters[0].Position.X);
        replica.ApplyMotion(new(new(11,22,2,140,3),[new(42,1,2,new(10,0,0),new(30,0,0),.75f,MotionFlags.None)]));
        Assert.Equal(10,replica.Present(150)!.Snapshot.Characters[0].Position.X);
    }
    [Theory]
    [InlineData(MotionFlags.Carried,1)]
    [InlineData(MotionFlags.Ragdoll,1)]
    [InlineData(MotionFlags.Discontinuity,1)]
    [InlineData(MotionFlags.None,0)]
    public void MotionPresentationDoesNotExtrapolateDiscontinuitiesOrPausedWorld(MotionFlags flags,double speed)
    {
        var db=WorldViewCodecTests.Database();var codec=new WorldViewCodec(db);var source=WithoutAnimation(WorldViewCodecTests.View(db));
        source=source with{TimeScale=speed,Snapshot=source.Snapshot with{Tick=100}};
        var publisher=new GameStatePublisher(codec,_=>42,11,22);var replica=new GameStateReplica(codec,11,22);Apply(replica,publisher.Capture(source));
        replica.ApplyMotion(new(new(11,22,1,100,2),[new(42,1,1,Vector3.Zero,new(30,0,0),0,MotionFlags.Moving|flags)]));
        Assert.Equal(0,replica.Present(130)!.Snapshot.Characters[0].Position.X);
    }
    [Fact]
    public void ReceiveClockConsumesMaturePlaybackBeforeBackloggedApplyWithoutRendererCalls()
    {
        var db=WorldViewCodecTests.Database();var source=WithoutAnimation(WorldViewCodecTests.View(db));var codec=new WorldViewCodec(db);
        var publisher=new GameStatePublisher(codec,_=>42,11,22);var replica=new GameStateReplica(codec,11,22)
        {ObservePresentationClock=(tick,_,_)=>Math.Max(0,(double)tick-3)};
        for(ulong epoch=1;epoch<=500;epoch++)
        {
            ulong tick=epoch*3;
            var state=new AnimationPlaybackState(epoch,tick,1f/30,0,tick+900,[new("walk",-1,1,.1f,1,AnimationWrap.Loop,1,1,1,0,AnimationBlendArea.All,true,false)]);
            var publication=publisher.Capture(source with{Publication=epoch,Snapshot=source.Snapshot with{Tick=(long)tick}},new Dictionary<ulong,AnimationPlaybackState>{{42,state}});
            Apply(replica,publication);
            Assert.InRange(replica.AnimationTimelines[42].PendingTransitions,0,1);
        }
        Assert.NotNull(replica.Present(1500));Assert.Equal(500ul,replica.AnimationTimelines[42].AppliedEpoch);
    }

    [Fact]
    public void CriticalEventsAndPlaybackPartitionBelowLifecycleBoundAndSurviveOlderInitialBulk()
    {
        var db=WorldViewCodecTests.Database(); var source=WithoutAnimation(WorldViewCodecTests.View(db)); var codec=new WorldViewCodec(db);
        var publisher=new GameStatePublisher(codec,_=>42,11,22); var replica=new GameStateReplica(codec,11,22);
        AnimationPlaybackState Playback(ulong epoch,string name)=>new(epoch,0,1f/30,0,900,[new(name,-1,1,0,1,AnimationWrap.Loop,1,1,1,0,AnimationBlendArea.All,true,false)]);
        var baseline=publisher.Capture(source,new Dictionary<ulong,AnimationPlaybackState>{{42,Playback(1,"old")}});
        var events=Enumerable.Range(1,128).Select(i=>new FloaterEvent((ulong)i,Floater.Text(source.Snapshot.Characters[0].Id,Vector3.Zero,new string('x',500)))).ToArray();
        var changed=source with { Publication=2,Events=new(128,events),Snapshot=source.Snapshot with { Characters=[source.Snapshot.Characters[0] with { Name="deferred" }] } };
        var critical=publisher.CaptureCritical(changed,new Dictionary<ulong,AnimationPlaybackState>{{42,Playback(2,"new")}});
        Assert.True(critical.Count>1);
        foreach(var part in critical)
        {
            Assert.False(part.Baseline); Assert.False(part.RequiresBulk); Assert.True(part.Frames.Sum(f=>f.Length)<=64*1024);
            Apply(replica,part); Assert.Null(replica.View);
        }
        Apply(replica,baseline);
        Assert.Equal(128,replica.View!.Events.Floaters.Count);
        Assert.Equal("new",Assert.Single(replica.Present(15)!.Snapshot.Characters[0].Animations).Name);
        Assert.NotEqual("deferred",replica.View.Snapshot.Characters[0].Name);
        var full=publisher.Capture(changed with { Publication=3 },new Dictionary<ulong,AnimationPlaybackState>{{42,Playback(2,"new")}});
        Assert.Contains(full.ComponentBytes.Keys,id=>GameStatePublisher.ComponentName(id)=="Name");
        Assert.DoesNotContain(full.ComponentBytes.Keys,id=>GameStatePublisher.ComponentName(id)=="Events");
        Apply(replica,full); Assert.Equal("deferred",replica.View.Snapshot.Characters[0].Name);
    }
    [Fact]
    public void CriticalDeparturesFenceBothDestructionAndScopeLossBeforeBulkCompletes()
    {
        var db=WorldViewCodecTests.Database(); var source=WithoutAnimation(WorldViewCodecTests.View(db)); var codec=new WorldViewCodec(db);
        source=source with { Snapshot=source.Snapshot with { Characters=[source.Snapshot.Characters[0],source.Snapshot.Characters[0] with { Id=new(2,1) }] } };
        var publisher=new GameStatePublisher(codec,id=>(ulong)id.Slot+1,11,22); var baseline=publisher.Capture(source);
        var critical=publisher.CaptureCritical(source with { Publication=2,Snapshot=source.Snapshot with { Characters=[] } },destroyedEntities:new HashSet<ulong>{3});
        var replica=new GameStateReplica(codec,11,22); foreach(var part in critical)Apply(replica,part);
        Assert.Null(replica.View); Apply(replica,baseline); Assert.Empty(replica.View!.Snapshot.Characters);
        Assert.Contains(replica.LastDepartures,d=>d.Entity==2 && d.Reason==GameDepartureReason.OutOfScope);
        Assert.Contains(replica.LastDepartures,d=>d.Entity==3 && d.Reason==GameDepartureReason.Destroyed);
    }

}
