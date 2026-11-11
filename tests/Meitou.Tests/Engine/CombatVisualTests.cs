using System.Numerics;
using Meitou.Game.Host;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;

namespace Meitou.Tests.Engine;

public class CombatVisualTests
{
    [Fact]
    public void LocalSessionForwardsVisualsAndTogglesSinksWithoutWorldAccessFromTheConsumer()
    {
        using var local = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0);
        IPlayerSession session = local; session.EnableBlood(true); session.EnableBlockSparks(true);
        var who = new CharacterId(1, 1); var mark = new BloodMark(BloodMarkKind.Splash, who, Vector3.One, Vector2.UnitX, .3f, 0xFF0000);
        local.World.EmitBlood(mark); local.World.EmitBlockSpark(who);
        Assert.True(session.TryDequeueBlood(out var visual)); Assert.Equal(mark, visual.Mark);
        Assert.Equal(local.World.TickSeconds, visual.TickSeconds); Assert.Equal(BodySystem.CorpseTicksOf(local.World), visual.CorpseTicks);
        Assert.True(session.TryDequeueBlockSpark(out var spark)); Assert.Equal(who, spark);
        session.EnableBlood(false); session.EnableBlockSparks(false);
        Assert.Null(local.World.BloodMarks); Assert.Null(local.World.BlockSparks);
    }

    [Fact]
    public void VisualCodecPreservesTimingAndRejectsMalformedOrCrossChannelDuplicateEvents()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        var who = new CharacterId(1, 1);
        var blood = new BloodVisual(new(BloodMarkKind.Pool, who, Vector3.One, Vector2.Zero, 1, 0xFF0000), 5, 1f / 30, 1000);
        var events = new PresentationEventBatch(2, []) { Blood = [new(1, blood)], Sparks = [new(2, who)] };
        var restored = codec.Decode(codec.Encode(view with { Events = events }));
        Assert.Equal(blood, Assert.Single(restored.Events.Blood).Value); Assert.Equal(who, Assert.Single(restored.Events.Sparks).Who);
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Events = events with { Sparks = [new(1, who)] } }));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Events = events with { Blood = [new(1, blood with { TickSeconds = float.NaN })] } }));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { SimulationStepSeconds = 0 }));
    }
}
