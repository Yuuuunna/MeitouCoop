using Meitou.Networking.VNext;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Tests.NetworkingNext;

public class AnimationTimelineTests
{
    static readonly AnimationTimelineFence Fence = new(1, 2, 3, 4);
    static AnimationPlaybackState State(ulong epoch = 1, ulong start = 100, float phase = .25f, float rate = 1) =>
        new(epoch, start, .1f, 99, start + 100, [new("walk lower", 7, 2, phase, rate, AnimationWrap.Loop, 1.998f, .2f, .8f, 1, AnimationBlendArea.Overlay, true, false)]);
    static float Time(AnimationTimeline timeline, double tick) => Assert.Single(timeline.Sample(tick)).Time;

    [Fact]
    public void BurstDuplicateAndLostIntermediateStateUseAuthorityTimeNotArrival()
    {
        var timeline = new AnimationTimeline(Fence);
        Assert.Equal(AnimationApplyResult.Applied, timeline.Apply(Fence, State()));
        Assert.Equal(.75f, Time(timeline, 105), 5);
        Assert.Equal(AnimationApplyResult.Duplicate, timeline.Apply(Fence, State()));
        Assert.Equal(.75f, Time(timeline, 105), 5);
        Assert.Equal(.75f, Time(timeline, 103), 5); // backward clock correction cannot rewind
        Assert.Equal(AnimationApplyResult.Applied, timeline.Apply(Fence, State(3, 108, .5f)));
        Assert.Equal(.9f, Time(timeline, 112), 5); // epoch 2 lost, epoch 3 arrived late
        Assert.Equal(AnimationApplyResult.Stale, timeline.Apply(Fence, State(2)));
        Assert.Equal(.9f, Time(timeline, 112), 5);
        var uninterrupted = new AnimationTimeline(Fence);
        uninterrupted.Apply(Fence, State(3, 108, .5f));
        Assert.Equal(Time(uninterrupted, 112), Time(timeline, 112));
    }

    [Fact]
    public void FutureTransitionsPreserveOldPlaybackAndReorderedIntermediates()
    {
        var timeline = new AnimationTimeline(Fence); timeline.Apply(Fence, State());
        Assert.Equal(.75f, Time(timeline, 105), 5);
        timeline.Apply(Fence, State(3, 120, .8f));
        timeline.Apply(Fence, State(2, 110, .4f));
        Assert.Equal(.75f, Time(timeline, 105), 5);
        Assert.Equal(.4f, Time(timeline, 110), 5); Assert.Equal(2ul, timeline.AppliedEpoch);
        Assert.Equal(.9f, Time(timeline, 115), 5);
        Assert.Equal(.8f, Time(timeline, 120), 5); Assert.Equal(3ul, timeline.AppliedEpoch);
        Assert.Equal(AnimationApplyResult.Stale, timeline.Apply(Fence, State(2, 110, .4f)));
        var bounded = new AnimationTimeline(Fence);
        for (ulong i = 1; i <= AnimationTimeline.MaximumPendingTransitions; i++)
            Assert.Equal(AnimationApplyResult.Applied, bounded.Apply(Fence, State(i, 100 + i)));
        Assert.Equal(AnimationApplyResult.NeedsResync, bounded.Apply(Fence, State(100, 500)));
        Assert.Empty(bounded.Sample(100));
    }

    [Fact]
    public void MatureAbsoluteTransitionDoesNotConsumeFutureQueueCapacity()
    {
        var timeline=new AnimationTimeline(Fence);timeline.AdvanceTo(100);
        for(ulong epoch=2;epoch<=33;epoch++)timeline.Apply(Fence,State(epoch,200+epoch));
        Assert.Equal(32,timeline.PendingTransitions);
        Assert.Equal(AnimationApplyResult.Applied,timeline.Apply(Fence,State(1,100)));
        Assert.Equal(1ul,timeline.AppliedEpoch);Assert.Equal(32,timeline.PendingTransitions);
        Assert.Equal(AnimationApplyResult.NeedsResync,timeline.Apply(Fence,State(34,300)));
        timeline.AdvanceTo(250);Assert.Equal(0,timeline.PendingTransitions);Assert.Equal(33ul,timeline.AppliedEpoch);
    }

    [Fact]
    public void ExplicitDiscontinuityAndWithdrawalFenceOldPlayback()
    {
        var timeline = new AnimationTimeline(Fence); timeline.Apply(Fence, State());
        Assert.Equal(.75f, Time(timeline, 105), 5);
        timeline.Discontinue(2); Assert.Empty(timeline.Sample(105));
        Assert.Equal(AnimationApplyResult.Stale, timeline.Apply(Fence, State()));
        timeline.Apply(Fence, State(2, 105, 0)); Assert.Equal(0, Time(timeline, 105));
        Assert.Equal(AnimationApplyResult.Fenced, timeline.Apply(Fence with { InterestGeneration = 3 }, State(9)));
        Assert.Equal(AnimationApplyResult.Fenced, timeline.Apply(Fence with { ViewEpoch = 1 }, State(9)));
        timeline.Withdraw(); Assert.Empty(timeline.Sample(110));
        Assert.Equal(AnimationApplyResult.Fenced, timeline.Apply(Fence, State(9)));
    }

    [Fact]
    public void SignedPlaybackWeightsAndFiniteSegmentsProduceConsumerLayers()
    {
        var timeline = new AnimationTimeline(Fence);
        var state = State(rate: -1) with { ValidThroughTick = 110 };
        timeline.Apply(Fence, state);
        var layer = Assert.Single(timeline.Sample(105));
        Assert.Equal(1.75f, layer.Time, 5); Assert.Equal(.5f, layer.Weight, 5);
        Assert.Equal(7, layer.Definition); Assert.Equal(2, layer.Length);
        var end = Assert.Single(timeline.Sample(110));
        Assert.Equal(end, Assert.Single(timeline.Sample(200))); Assert.True(timeline.IsHoldingExpiredSegment);
        var hold = state with { Epoch = 2, StartTick = 200, ValidThroughTick = 210,
            Layers = [state.Layers[0] with { Wrap = AnimationWrap.Clamp, Rate = 10 }] };
        timeline.Apply(Fence, hold);
        Assert.Equal(1.998f, Time(timeline, 210), 5);
    }

    [Theory]
    [InlineData(.1f, false)]
    [InlineData(1f, false)]
    [InlineData(1f, true)]
    public void RawWeightSamplingMatchesRealSimulationPublicationAcrossAllAreas(float rawScale, bool allFading)
    {
        var areas = new[] { AnimationArea.All, AnimationArea.Lower, AnimationArea.Upper, AnimationArea.Overlay,
            AnimationArea.Other, AnimationArea.Upper, AnimationArea.Upper, AnimationArea.Lower };
        var definitions = areas.Select((area, i) => new AnimationDefinition { Id = $"a{i}", Name = $"a{i}", Clip = $"a{i}",
            Area = area, IsAction = i is 5 or 6, Technique = i == 6 }).ToArray();
        var publisher = new AnimationSystem(new AnimationLibrary(definitions), new AnimationLengths());
        var metadata = definitions.Select((d, i) => new AnimationPlaybackLayer(d.Name, i, 2, .25f, 0,
            AnimationWrap.Clamp, 1.998f, (.15f + i * .07f) * rawScale, allFading ? 0 : (.7f - i * .06f) * rawScale, 1,
            (AnimationBlendArea)d.Area, !allFading && (i % 2 == 0 || i == 7), d.Area == AnimationArea.All || d.IsAction && !d.Technique)).ToArray();
        var state = new AnimationPlaybackState(1, 100, .1f, 0, 110, metadata);
        var timeline = new AnimationTimeline(Fence);
        timeline.Apply(Fence, AnimationPlaybackCodec.Decode(AnimationPlaybackCodec.Encode(state)));
        foreach (double tick in new[] { 100d, 102.5, 105, 107.5, 110 })
        {
            var raw = new CharacterAnimation { Count = metadata.Length };
            for (int i = 0; i < metadata.Length; i++)
            {
                var m = metadata[i]; float blend = (float)((tick - 100) * state.TickSeconds);
                raw.Layers[i] = new() { Definition = i, Time = .25f, Length = 2,
                    Weight = m.StartWeight + (m.TargetWeight - m.StartWeight) * Math.Min(1, blend), Target = m.Wanted ? 1 : 0 };
            }
            var expected = publisher.Publish(raw, []); var actual = timeline.Sample(tick);
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            { Assert.Equal(expected[i].Name, actual[i].Name); Assert.Equal(expected[i].Weight, actual[i].Weight, 6); }
        }
        var invalidFlags = AnimationPlaybackCodec.Encode(state); invalidFlags[^1] = 128;
        Assert.Throws<InvalidDataException>(() => AnimationPlaybackCodec.Decode(invalidFlags));
    }

    [Fact]
    public void CodecAndReducerRejectMalformedConflictingStatesAtomically()
    {
        var original = State(); byte[] bytes = AnimationPlaybackCodec.Encode(original);
        var decoded = AnimationPlaybackCodec.Decode(bytes);
        Assert.Equal(original.Epoch, decoded.Epoch); Assert.Equal(original.Layers, decoded.Layers);
        var timeline = new AnimationTimeline(Fence); timeline.Apply(Fence, decoded);
        Assert.Throws<InvalidDataException>(() => timeline.Apply(Fence, State(phase: .5f)));
        Assert.Equal(.25f, Time(timeline, 100));
        foreach (var invalid in new[] {
            original with { Epoch = 0 }, original with { ValidThroughTick = 10000 }, original with { TickSeconds = float.NaN }, original with { ValidThroughTick = 0 },
            original with { Layers = [original.Layers[0] with { Rate = float.PositiveInfinity }] },
            original with { Layers = [original.Layers[0] with { Name = new string('a', 129) }] },
            original with { Layers = [original.Layers[0] with { Wrap = (AnimationWrap)9 }] } })
            Assert.Throws<InvalidDataException>(() => AnimationPlaybackCodec.Encode(invalid));
        Assert.Throws<InvalidDataException>(() => AnimationPlaybackCodec.Decode(bytes[..^1]));
        Assert.Throws<InvalidDataException>(() => AnimationPlaybackCodec.Decode([.. bytes, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.Sample(double.NaN));
    }
}
