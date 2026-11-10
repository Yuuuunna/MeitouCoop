using Meitou.Data.World;
using Meitou.Networking;
using Meitou.Data.Particles;
using System.Numerics;

namespace Meitou.Tests.Engine;

public class WeatherReplicationTests
{
    [Fact]
    public void EffectNodeChangesAndWithdrawalDoNotRetainStaleRoots()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db);
        var first = new EffectNodeSnapshot("r:1:2:0:33", "gas", "gas-system", 33, Vector3.One);
        var second = first with { Id = "r:1:2:0:34", Seed = 34, Position = new(20, 0, 0) };
        var view = WorldViewCodecTests.View(db) with { WeatherEffects = new([first, second]) };
        var peer = new GameReplicationFixture(codec);
        peer.Apply(peer.Capture(view));
        Assert.Equal(new[] { first, second }, peer.Client.View!.WeatherEffects!.Nodes);
        var moved = first with { Position = new(3, 4, 5), Age = 1 };
        var next = view with { Publication = 2, WeatherEffects = new([moved, second]) };
        var patch = peer.Capture(next);
        Assert.True(GameReplicationFixture.Changes(patch, "WeatherEffects"));
        peer.Apply(patch);
        Assert.Equal(moved, peer.Client.View!.WeatherEffects!.Nodes[0]);
        var removed = peer.Capture(next with { Publication = 3, WeatherEffects = new([second]) });
        Assert.True(GameReplicationFixture.Changes(removed, "WeatherEffects"));
        peer.Apply(removed); Assert.Equal(second, Assert.Single(peer.Client.View!.WeatherEffects!.Nodes));
        peer.Apply(peer.Capture(next with { Publication = 4, WeatherEffects = WeatherEffectFrame.Empty }));
        Assert.Empty(peer.Client.View!.WeatherEffects!.Nodes);
    }

    [Fact]
    public void InvalidEffectNodesAreRejectedBeforePublication()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        var node = new EffectNodeSnapshot("n", "gas", "gas-system", 33, Vector3.One);
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { WeatherEffects = new([node, node]) }));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { WeatherEffects = new([node with { Position = new(float.NaN, 0, 0) }]) }));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { WeatherEffects = new([node with { Wrap = (UnitWrap)99 }]) }));
    }

    [Fact]
    public void RegionalWeatherIsKeyedIndependentlyOfTimeAndCanBeExplicitlyDisabled()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db);
        var weather = new WeatherFrame(WeatherTime.At(0), World.WeatherScheduleTests.MakeWorld().Snapshot());
        var view = WorldViewCodecTests.View(db) with { GameHours = 0, Weather = weather };
        var peer = new GameReplicationFixture(codec);
        peer.Apply(peer.Capture(view));
        Assert.Equal(weather.Regions.OrderBy(r => r.RegionId), peer.Client.View!.Weather!.Regions.OrderBy(r => r.RegionId));
        var next = view with { Publication = 2, GameHours = .01, Weather = weather with { Time = WeatherTime.At(0, .01) } };
        var patch = peer.Capture(next);
        Assert.False(GameReplicationFixture.Changes(patch, "Weather"));
        peer.Apply(patch); Assert.Equal(next.Weather.Time, peer.Client.View!.Weather!.Time);
        var off = peer.Capture(next with { Publication = 3, Weather = new(next.Weather.Time, []) { Enabled = false } });
        Assert.True(GameReplicationFixture.Changes(off, "Weather"));
        peer.Apply(off); Assert.False(peer.Client.View!.Weather!.Enabled); Assert.Empty(peer.Client.View.Weather.Regions);
    }

    [Fact]
    public void MalformedRegionalWeatherCannotReachPresentation()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db); var view = WorldViewCodecTests.View(db);
        var region = World.WeatherScheduleTests.MakeWorld().Snapshot()[0];
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Weather = new(WeatherTime.At(0), [region, region]) }));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Weather = new(WeatherTime.At(0, 24), [region]) }));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Weather = new(WeatherTime.At(0), [region with { Strength = float.NaN }]) }));
        Assert.Throws<InvalidDataException>(() => codec.Encode(view with { Weather = new(WeatherTime.At(0), [region]) { Enabled = false } }));
    }
}
