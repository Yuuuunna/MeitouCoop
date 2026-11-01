using Meitou.Data.World;
using Meitou.Networking;
using Meitou.Data.Particles;
using System.Numerics;

namespace Meitou.Tests.Engine;

public class WeatherReplicationTests
{
    [Fact]
    public void EffectNodesDeltaIndividuallyAndWithdrawWithoutRetainingStaleRoots()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db);
        var first = new EffectNodeSnapshot("r:1:2:0:33", "gas", "gas-system", 33, Vector3.One);
        var second = first with { Id = "r:1:2:0:34", Seed = 34, Position = new(20, 0, 0) };
        var view = WorldViewCodecTests.View(db) with { WeatherEffects = new([first, second]) };
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        Assert.Equal(ReplicaResult.Applied, client.Receive(server.Publish(view).Payload)); Assert.True(server.Acknowledge(client.Acknowledgement!));
        Assert.Equal(new[] { first, second }, client.Current!.WeatherEffects!.Nodes);
        var moved = first with { Position = new(3, 4, 5), Age = 1 };
        var next = view with { Publication = 2, WeatherEffects = new([moved, second]) };
        var patch = server.Publish(next); var decoded = StatePatchProtocol.Decode(patch.Payload);
        Assert.Equal("e:" + first.Id, Assert.Single(decoded.Changes, c => c.Key.StartsWith("e:", StringComparison.Ordinal)).Key);
        Assert.Equal(ReplicaResult.Applied, client.Receive(patch.Payload)); Assert.True(server.Acknowledge(client.Acknowledgement!));
        Assert.Equal(moved, client.Current!.WeatherEffects!.Nodes[0]);
        var removed = server.Publish(next with { Publication = 3, WeatherEffects = new([second]) });
        Assert.Equal("e:" + first.Id, Assert.Single(StatePatchProtocol.Decode(removed.Payload).Removals).Key);
        Assert.Equal(ReplicaResult.Applied, client.Receive(removed.Payload)); Assert.Equal(second, Assert.Single(client.Current!.WeatherEffects!.Nodes));
        server.RequestBaseline();
        Assert.Equal(ReplicaResult.Applied, client.Receive(server.Publish(next with { Publication = 4, WeatherEffects = WeatherEffectFrame.Empty }).Payload));
        Assert.Empty(client.Current!.WeatherEffects!.Nodes);
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
        var identity = ReplicationIdentity.Of(view); var server = new ServerStateReplicator(identity, codec); var client = new ClientWorldReplica(identity, codec);
        Assert.Equal(ReplicaResult.Applied, client.Receive(server.Publish(view).Payload)); Assert.True(server.Acknowledge(client.Acknowledgement!));
        Assert.Equal(weather.Regions.OrderBy(r => r.RegionId), client.Current!.Weather!.Regions.OrderBy(r => r.RegionId));
        var next = view with { Publication = 2, GameHours = .01, Weather = weather with { Time = WeatherTime.At(0, .01) } };
        var patch = server.Publish(next);
        Assert.DoesNotContain(StatePatchProtocol.Decode(patch.Payload).Changes, c => c.Key.StartsWith("w:", StringComparison.Ordinal));
        Assert.Equal(ReplicaResult.Applied, client.Receive(patch.Payload)); Assert.Equal(next.Weather.Time, client.Current!.Weather!.Time);
        Assert.True(server.Acknowledge(client.Acknowledgement!));
        var off = server.Publish(next with { Publication = 3, Weather = new(next.Weather.Time, []) { Enabled = false } });
        Assert.Equal(weather.Regions.Count, StatePatchProtocol.Decode(off.Payload).Removals.Count(r => r.Key.StartsWith("w:", StringComparison.Ordinal)));
        Assert.Equal(ReplicaResult.Applied, client.Receive(off.Payload)); Assert.False(client.Current!.Weather!.Enabled); Assert.Empty(client.Current.Weather.Regions);
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
