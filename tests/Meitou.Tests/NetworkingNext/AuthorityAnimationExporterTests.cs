using Meitou.Data.Gameplay;
using Meitou.Networking.VNext;
using Meitou.Simulation;

namespace Meitou.Tests.NetworkingNext;

public class AuthorityAnimationExporterTests
{
    [Fact]
    public void RestoredPausedCharacterKeepsPublishedInitialPoseUntilFirstDriverStep()
    {
        var definition = new AnimationDefinition { Id = "idle", Name = "idle", Clip = "idle", Area = AnimationArea.All,
            Idle = true, IdleChance = 100, IdleTimeMin = 10, IdleTimeMax = 10, PlaySpeed = 1, Loop = true, WeaponRight = 2 };
        var system = new AnimationSystem(new AnimationLibrary([definition]), new AnimationLengths().Add("idle", 2));
        using var world = new Meitou.Simulation.World(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), [system]);
        var cold = new CharacterCold();
        var id = world.Characters.Spawn(default, cold, 0);
        var initial = new WorldSnapshot(0, [new CharacterSnapshot(id, null, default, 0, [new("restored static pose", .7f, .6f)])]);
        var before = Assert.Single(AnimationPlaybackExport.Capture(world, initial)[id].Layers);
        Assert.Equal(.7f, before.Time); Assert.Equal(.6f, before.Weight); Assert.Equal(0, before.Rate);
        cold.Animation = new CharacterAnimation();
        system.Update(cold.Animation, cold, 0, 1f / 30, 1, 1, 1);
        var after = Assert.Single(AnimationPlaybackExport.Capture(world, initial)[id].Layers);
        Assert.Equal("idle", after.Name); Assert.Equal(1, after.Rate); Assert.True(after.Predictable);
    }

    [Fact]
    public void WorldWithoutAnimationDriverExportsLiteralStaticSnapshotWeights()
    {
        using var world = new Meitou.Simulation.World(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
        var id = new CharacterId(1, 0);
        var snapshot = new WorldSnapshot(10, [new CharacterSnapshot(id, null, default, 0,
            [new("static pose", 3, .25f), new("static overlay", 0, .4f)])]);
        var capture = AnimationPlaybackExport.Capture(world, snapshot)[id];
        var exporter = new AuthorityAnimationExporter(diagnostics: true);
        var state = exporter.Capture(1, capture, 10, 1f / 30)!;
        var fence = new AnimationTimelineFence(1, 1, 1, 1); var timeline = new AnimationTimeline(fence);
        timeline.Apply(fence, state);
        Assert.Equal(snapshot.Characters[0].Animations.Select(l => (l.Time, l.Weight)), timeline.Sample(20).Select(l => (l.Time, l.Weight)));
        Assert.Null(exporter.Capture(1, capture, 20, 1f / 30));
        Assert.Null(exporter.Capture(1, capture, 1000, 1f / 30));
        Assert.True(state.ValidThroughTick > 1000);
        Assert.NotNull(exporter.Capture(1, capture, state.ValidThroughTick + 1, 1f / 30));
        Assert.Equal(1, exporter.Diagnostics.Reasons[AnimationExportReason.Initial]);
        Assert.Equal(1, exporter.Diagnostics.Reasons[AnimationExportReason.Expiry]);
        Assert.Equal(2, exporter.Diagnostics.Suppressed);
    }

    [Theory]
    [InlineData(CharacterTask.PickUp, 2f)]
    [InlineData(CharacterTask.PutDown, -2f)]
    public void ActualCarryClockExportsDirectionAndRawBlend(CharacterTask task, float rate)
    {
        var definition = new AnimationDefinition { Id = "lift", Name = "shoulder lift left", Clip = "lift", Area = AnimationArea.All, PlaySpeed = 2 };
        var system = new AnimationSystem(new AnimationLibrary([definition]), new AnimationLengths().Add("lift", 2));
        var animation = new CharacterAnimation(); var cold = new CharacterCold { LiftTime = .25f };
        system.Update(animation, cold, 0, 1f / 30, 1, 1, 1, task: task);
        var layer = Assert.Single(AnimationPlaybackExport.Capture(system, animation).Layers);
        Assert.True(layer.Predictable); Assert.Equal(rate, layer.Rate);
        Assert.Equal(task == CharacterTask.PickUp ? .5f : 1.5f, layer.Time);
        Assert.True(layer.ConsumesLower); Assert.True(layer.Wanted);
    }

    [Fact]
    public void ActualMovementAndCombatStepsMatchExportedPlaybackWithoutPerTickRenewal()
    {
        var walk = new AnimationDefinition { Id = "walk", Name = "walk", Clip = "walk", Area = AnimationArea.Lower,
            MoveSpeed = 14, PlaySpeed = .06f, Synchs = true, MinSpeed = -50, MaxSpeed = 40,
            WeaponRight = 2, LeftLegMax = 100, RightLegMax = 100, Chance = 100 };
        var cut = walk with { Id = "cut", Name = "cut", Clip = "cut", Area = AnimationArea.All,
            MoveSpeed = 0, Synchs = false, IsAction = true, Technique = true, CombatMode = 2, StealthMode = 2 };
        var system = new AnimationSystem(new AnimationLibrary([walk, cut]), new AnimationLengths().Add("walk", 1.4f).Add("cut", 2));
        foreach (bool combat in new[] { false, true })
        {
            var animation = new CharacterAnimation(); var cold = new CharacterCold();
            var exporter = new AuthorityAnimationExporter();
            var fence = new AnimationTimelineFence(1, 1, 1, 1); var timeline = new AnimationTimeline(fence);
            int sent = 0;
            for (ulong tick = 1; tick <= 20; tick++)
            {
                var fight = combat ? CombatView.None with { Technique = 1, Progress = tick / 30f, ProgressRate = 1 } : CombatView.None;
                system.Update(animation, cold, combat ? 0 : 14, 1f / 30, 1, 1, (long)tick, combatView: fight);
                var state = exporter.Capture(1, AnimationPlaybackExport.Capture(system, animation), tick, 1f / 30);
                if (state is not null) { timeline.Apply(fence, state); sent++; }
                var expected = system.Publish(animation, []); var actual = timeline.Sample(tick);
                Assert.NotEmpty(expected); Assert.Equal(expected.Count, actual.Count);
                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.Equal(expected[i].Name, actual[i].Name);
                    Assert.Equal(expected[i].Time, actual[i].Time, 4);
                    Assert.Equal(expected[i].Weight, actual[i].Weight, 4);
                }
            }
            Assert.True(sent < 5, $"Expected stable source clocks to avoid per-tick export; sent {sent}.");
        }
    }
}
