using System.Numerics;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Tests.Engine;
namespace Meitou.Tests.NetworkingNext;

public sealed class RemotePresentationClockTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(9000L)]
    public void ThirtyHertzAuthorityTwentyHertzMotionAndSixtyHertzRenderingAdvanceBetweenJitteredArrivals(long initialTick)
    {
        var database = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(database);
        var source = WorldViewCodecTests.View(database);
        var character = source.Snapshot.Characters[0] with { Animations = [] };
        source = source with { Publication = 1, SimulationStepSeconds = 1f / 30, Snapshot = source.Snapshot with { Tick = initialTick, Characters = [character] } };
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22);
        var replica = new GameStateReplica(codec, 11, 22);
        foreach (var frame in publisher.Capture(source).Frames) replica.Apply(frame);
        var clock = new RemotePresentationClock(); clock.Observe((ulong)initialTick, 1, 1d / 30, TimeSpan.Zero); clock.MarkPresented();
        int packet = 1, advancingFrames = 0;
        float previousX = character.Position.X;
        for (int frame = 1; frame <= 180; frame++)
        {
            double time = frame / 60d; int before = packet;
            while (packet <= 60)
            {
                double sent = packet * .05; double jitter = packet % 3 == 0 ? -.01 : packet % 3 == 1 ? .01 : 0;
                if (sent + jitter > time + 1e-8) break;
                ulong tick = (ulong)initialTick + (ulong)Math.Floor(sent * 30 + 1e-8);
                var position = character.Position + new Vector3((tick - (ulong)initialTick) * .1f, 0, 0);
                replica.ApplyMotion(new(new(11, 22, (ulong)packet, tick, (ulong)packet + 1),
                    [new(42, 1, (uint)packet, position, new(3, 0, 0), 0, MotionFlags.Moving)]));
                clock.Observe(tick, 1, 1d / 30, TimeSpan.FromSeconds(time)); packet++;
            }
            clock.Advance(TimeSpan.FromSeconds(time));
            float x = replica.Present(clock.Tick)!.Snapshot.Characters[0].Position.X;
            if (time > .35 && packet == before) { Assert.True(x > previousX + .0001f, $"Root froze between packet arrivals at frame {frame}."); advancingFrames++; }
            Assert.True(x >= previousX); previousX = x;
        }
        Assert.True(advancingFrames >= 80); Assert.Equal(0ul, clock.CorrectionCount);
    }
    [Fact]
    public void RendererMarkedAtTickZeroRecoversElapsedCreditBeforeApplyingAnEightySecondReceiptBurst()
    {
        var clock = new RemotePresentationClock(); clock.Observe(0, 1, 1d / 30, TimeSpan.Zero); clock.MarkPresented();
        clock.Advance(TimeSpan.FromSeconds(80)); Assert.Equal(0, clock.Tick);
        for (ulong tick = 3; tick <= 2400; tick += 3)
        {
            clock.Observe(tick, 1, 1d / 30, TimeSpan.FromSeconds(80));
            Assert.InRange(tick - clock.Tick, 0, 3.000001);
        }
        Assert.Equal(2397, clock.Tick, 6);
        double before = clock.Tick; clock.Advance(TimeSpan.FromSeconds(80 + 1d / 60));
        Assert.InRange(clock.Tick - before, .49, .51);
    }

    [Fact]
    public void PausedToRunningAuthorityDebtCatchupPrunesBeforeEachAnimationApplyWithoutPacketLoss()
    {
        var database = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(database);
        var source = WorldViewCodecTests.View(database);
        source = source with { Publication = 1, TimeScale = 0, Snapshot = source.Snapshot with {
            Tick = 0, Characters = source.Snapshot.Characters.Select(character => character with { Animations = [] }).ToArray() } };
        var clock = new RemotePresentationClock(); TimeSpan now = TimeSpan.Zero;
        var replica = new GameStateReplica(codec, 11, 22) { ObservePresentationClock = (tick, scale, step) => clock.Observe(tick, scale, step, now) };
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22);
        foreach (var frame in publisher.Capture(source).Frames) replica.Apply(frame);
        clock.MarkPresented(); double previous = clock.Tick; int maximumPending = 0;
        for (int index = 1; index <= 500; index++)
        {
            ulong tick = (ulong)index * 3; now = TimeSpan.FromSeconds(index * .02);
            var playback = new AnimationPlaybackState((ulong)index, tick, 1f / 30, 0, tick + 900,
                [new("walk", -1, 1, 0, 1, AnimationWrap.Loop, 1, 1, 1, 0, AnimationBlendArea.All, true, false)]);
            var publication = publisher.Capture(source with { Publication = (ulong)index + 1, TimeScale = 1,
                Snapshot = source.Snapshot with { Tick = (long)tick } }, new Dictionary<ulong, AnimationPlaybackState> { [42] = playback });
            foreach (var frame in publication.Frames) replica.Apply(frame);
            Assert.True(clock.Tick >= previous); previous = clock.Tick;
            Assert.InRange(tick - clock.Tick, 0, 9.000001);
            maximumPending = Math.Max(maximumPending, replica.AnimationTimelines[42].PendingTransitions);
        }
        Assert.True(clock.CorrectionCount > 0); Assert.InRange(maximumPending, 0, 4);
        Assert.NotNull(replica.Present(clock.Tick));
    }

}
