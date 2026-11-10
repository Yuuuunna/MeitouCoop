using System.Numerics;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Tests.Engine;

namespace Meitou.Tests.NetworkingNext;

public class RecipientComponentCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public void SyntheticSceneReportsFieldBytesAndExposesAnimationTimeChurn()
    {
        var db = WorldViewCodecTests.Database(); var seed = WorldViewCodecTests.View(db);
        var characters = Enumerable.Range(0, 124).Select(i => seed.Snapshot.Characters[0] with { Id = new(i + 1, 1) }).ToArray();
        var view = seed with { Snapshot = seed.Snapshot with { Characters = characters } };
        var capture = new RecipientComponentCapture(new WorldViewCodec(db), id => (ulong)id.Slot + 1);
        var baseline = capture.Capture(view);
        var tick = capture.Capture(view with { Publication = 2, GameHours = 13,
            Snapshot = view.Snapshot with { Tick = 5, StateRevision = 8 } }).Difference(baseline);
        var motion = capture.Capture(view with { Snapshot = view.Snapshot with {
            Characters = characters.Select(c => c with { Position = c.Position + Vector3.One }).ToArray() } }).Difference(baseline);
        var animation = capture.Capture(view with { Snapshot = view.Snapshot with {
            Characters = characters.Select(c => c with { Animations = c.Animations.Select(a => a with { Time = a.Time + .05f }).ToArray() }).ToArray() } }).Difference(baseline);
        static long Bytes(IEnumerable<CapturedField> fields) => fields.Sum(f => (long)f.Bytes.Length);
        output.WriteLine($"Synthetic 124-character field value bytes only (no key/framing/transport): baseline={Bytes(baseline.Fields.Values)}, tick={Bytes(tick.Changes.Values)}, motion={Bytes(motion.Changes.Values)}, animation-step={Bytes(animation.Changes.Values)}");
        foreach (var family in baseline.Fields.GroupBy(p => p.Key.Family))
            output.WriteLine($"Baseline {family.Key}: {Bytes(family.Select(p => p.Value))}");
        Assert.Empty(tick.Changes); Assert.Empty(motion.Changes);
        Assert.Equal(124, animation.Changes.Count);
        Assert.All(animation.Changes.Keys, k => Assert.Equal("Animations", k.Field));
        Assert.True(Bytes(animation.Changes.Values) > 0);
    }

    [Fact]
    public void ClockAndRootMotionNeverDirtyStaticFields()
    {
        var db = WorldViewCodecTests.Database(); var view = WorldViewCodecTests.View(db);
        var capture = new RecipientComponentCapture(new WorldViewCodec(db), _ => 123);
        var before = capture.Capture(view);
        var next = capture.Capture(view with { Publication = 99, GameHours = 20, TimeScale = 1,
            Snapshot = view.Snapshot with { Tick = 50, StateRevision = 200,
                Characters = [view.Snapshot.Characters[0] with { Position = Vector3.One, Yaw = 2, DrawYaw = 1 }] } });
        var delta = next.Difference(before);
        Assert.Empty(delta.Changes); Assert.Empty(delta.Withdrawals);
        Assert.Equal(50, next.Clock.Tick); Assert.Equal(99ul, next.Clock.Publication);
    }

    [Fact]
    public void ChangedFieldsAndPrivateWithdrawalDoNotReplaceCharacters()
    {
        var db = WorldViewCodecTests.Database(); var view = WorldViewCodecTests.View(db);
        var capture = new RecipientComponentCapture(new WorldViewCodec(db), _ => 123);
        var before = capture.Capture(view);
        var next = capture.Capture(view with { Snapshot = view.Snapshot with {
            Characters = [view.Snapshot.Characters[0] with { Name = "Changed", ItemGrid = null, Jobs = [] }] } });
        var delta = next.Difference(before);
        Assert.Equal(new[] { "Jobs", "Name" }, delta.Changes.Keys.Select(k => k.Field).Order(StringComparer.Ordinal));
        Assert.Equal("ItemGrid", Assert.Single(delta.Withdrawals).Field);
        Assert.All(delta.Changes.Keys, k => Assert.Equal(123ul, k.Entity));
        var gone = capture.Capture(view with { Snapshot = view.Snapshot with { Characters = [] } }).Difference(before);
        Assert.NotEmpty(gone.Withdrawals);
        Assert.All(gone.Withdrawals, k => Assert.Equal("character", k.Domain));
    }

    [Fact]
    public void CapturesAreDetachedAndRecipientEpochsCannotMix()
    {
        var db = WorldViewCodecTests.Database(); var view = WorldViewCodecTests.View(db);
        var capture = new RecipientComponentCapture(new WorldViewCodec(db), _ => 123);
        var before = capture.Capture(view);
        view.Snapshot.Characters[0].Appearance!.Parts.Clear();
        Assert.Contains(capture.Capture(view).Difference(before).Changes.Keys, k => k.Field == "Appearance");
        Assert.Throws<InvalidDataException>(() => capture.Capture(view with { World = Guid.NewGuid() }).Difference(before));
        Assert.Throws<InvalidDataException>(() => capture.Capture(view with { Player = new(99) }).Difference(before));
        Assert.Throws<InvalidDataException>(() => capture.Capture(view with { ConnectionGeneration = 2 }).Difference(before));
        Assert.Throws<InvalidDataException>(() => new RecipientComponentCapture(new WorldViewCodec(db), _ => 0).Capture(view));
    }
}
