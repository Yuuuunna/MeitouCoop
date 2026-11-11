using System.Numerics;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Factions;

namespace Meitou.Tests.World;

internal sealed class SyntheticEffectWeather : IWeatherSource, IWeatherPresentationSource
{
    public WeatherEffectFrame Frame { get; set; } = WeatherEffectFrame.Empty;
    public WeatherHere? At(float x, float z) => null;
    public IReadOnlyList<WeatherAffectZone> Zones => [];
    public WeatherFrame CaptureWeather() => new(WeatherTime.At(0), WeatherScheduleTests.MakeWorld().Snapshot());
    public WeatherEffectFrame CaptureEffects() => Frame;
}

public class PresentedEffectTests
{
    [Fact]
    public void PresentedNodesOnlyMoveStopOrDisappearWhenAuthorityChangesThem()
    {
        var (db, authority) = WeatherHazardCheckpointTests.Scene();
        var now = WeatherTime.FromHours(61.2); for (int i = 0; i < 10; i++) authority.Advance(now, 1f / 30);
        var frame = authority.CaptureEffects(); frame.Validate();
        var presented = new PresentedEffectSet(ParticleCheckpointTests.Library(), new EffectWorld { ActiveRadius = float.PositiveInfinity });
        EffectRecord? Resolve(string id) => db.Find(id) is { } record ? EffectRecord.From(record, db) : null;
        presented.Accept(frame, Resolve);
        var roots = presented.Groups.SelectMany(g => g.Units).ToArray();
        var initial = roots.Select(u => (u.Position, u.Age, u.Life, u.Stopped)).ToArray();
        for (int i = 0; i < 300; i++)
            foreach (var g in presented.Groups) { g.Update(1f / 30, new(new Vector3(i * 50, 30, i), Vector3.UnitX), WeatherEffectInput.None); g.Simulate(); }
        Assert.Equal(initial, roots.Select(u => (u.Position, u.Age, u.Life, u.Stopped)));
        Assert.True(presented.Groups.Sum(g => g.ParticleCount) > 0);
        var particles = roots.Select(u => u.Simulation).ToArray();
        presented.Accept(frame, Resolve);
        Assert.Equal(roots, presented.Groups.SelectMany(g => g.Units)); Assert.Equal(particles, roots.Select(u => u.Simulation));
        var changed = frame.Nodes.Select((n, i) => i == 0 ? n with { Position = n.Position + new Vector3(100, 0, 0), Stopped = true, StoppedFor = 0.1f } : n).ToArray();
        presented.Accept(new(changed), Resolve);
        Assert.Equal(changed[0].Position, roots[0].Position); Assert.True(roots[0].Stopped);
        var invalid = changed.Append(changed[0] with { Id = "unknown", EffectId = "missing" }).ToArray();
        Assert.Throws<InvalidDataException>(() => presented.Accept(new(invalid), Resolve));
        Assert.Equal(roots, presented.Groups.SelectMany(g => g.Units)); Assert.Equal(changed[0].Position, roots[0].Position);
        presented.Accept(WeatherEffectFrame.Empty, Resolve); Assert.Empty(presented.Groups);
        presented.Accept(frame, Resolve); Assert.NotSame(roots[0], presented.Groups[0].Units[0]);
        var restored = authority.CreateSibling(); restored.RestoreState(authority.CaptureState());
        Assert.Equal(frame.Nodes, restored.CaptureEffects().Nodes);
    }
}
