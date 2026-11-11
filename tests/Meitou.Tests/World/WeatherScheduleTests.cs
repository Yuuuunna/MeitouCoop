using System.Numerics;
using Meitou.Data;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Runtime;
using ParticleEffectType = Meitou.Data.Particles.EffectType;

namespace Meitou.Tests.World;

public class WeatherScheduleTests
{
    internal static WeatherWorld MakeWorld()
    {
        var acid = new WeatherDef { StringId = "acid", Name = "Acid", AffectType = WeatherAffecting.Acid,
            WindSpeedMin = 4, WindSpeedMax = 20, WindUpdateMinutes = 10, WindUpdateLimit = 4 };
        var clear = new WeatherDef { StringId = "clear", Name = "Clear" };
        var season = SeasonDef.Build("season", "Season", [new SeasonWeather(acid, 1, 7, 20), new SeasonWeather(clear, 1, 5, 18)], 0.2f, 1, clear);
        var region = new RegionDef { StringId = "region", Name = "Region", Index = 1,
            Seasons = [new RegionSeason(season, 0, 1, 100)] };
        return new WeatherWorld(WeatherData.FromDefinitions([acid, clear], [season], [region]),
            new WeatherAreas(Enumerable.Repeat(1, WeatherAreas.Cells * WeatherAreas.Cells).ToArray()), 13, WeatherTime.At(0));
    }

    [Fact]
    public void LongAdvanceMatchesMinuteSteppingWithoutAnyCameraUpdates()
    {
        var jumped = new WeatherSchedule(MakeWorld());
        var stepped = new WeatherSchedule(MakeWorld());
        jumped.Advance(WeatherTime.FromHours(12.5));
        for (int minute = 1; minute <= 750; minute++) stepped.Advance(WeatherTime.FromHours(minute / 60.0));
        Assert.Equal(stepped.World.Snapshot(), jumped.World.Snapshot());
        Assert.Equal(stepped.Time, jumped.Time);
    }

    [Fact]
    public void PresentationUsesAuthoritativeTimeWithoutAdvancingEitherRegionalSchedule()
    {
        var authority = new WeatherSchedule(MakeWorld());
        authority.Advance(WeatherTime.At(1, 12));
        var snapshot = authority.World.Snapshot();
        var view = new Meitou.Game.Rendering.Sky.WorldWeather(MakeWorld()) { Day = 999, Log = false };
        view.AcceptAuthority(snapshot, authority.Time);
        var before = view.World.Snapshot();
        for (int i = 0; i < 20; i++) view.Update(new Vector3(i * 200, 100, i * -300), 23, 1, held: true);
        Assert.Equal(snapshot, authority.World.Snapshot());
        Assert.Equal(before, view.World.Snapshot());
        Assert.Equal(authority.Time, view.World.Time);
    }

    [Fact]
    public void RepeatedPresentationImportsDoNotRestartWeatherAndInvalidCataloguesAreAtomic()
    {
        var source = new WeatherSchedule(MakeWorld()); source.Advance(WeatherTime.At(1, 12));
        var frame = new WeatherFrame(source.Time, source.World.Snapshot());
        var view = new Meitou.Game.Rendering.Sky.WorldWeather(MakeWorld()) { Log = false };
        view.AcceptRemoteAuthority(frame);
        var serials = view.World.Regions.Select(r => r.Serial).ToArray();
        for (int i = 0; i < 5; i++) view.AcceptRemoteAuthority(frame);
        Assert.Equal(serials, view.World.Regions.Select(r => r.Serial));
        Assert.Equal(frame.Regions, view.World.Snapshot());
        var bad = frame with { Regions = frame.Regions.Select((r, i) => i == frame.Regions.Count - 1 ? r with { WeatherId = "missing" } : r).ToArray() };
        Assert.Throws<InvalidDataException>(() => view.AcceptRemoteAuthority(bad));
        Assert.Equal(frame.Regions, view.World.Snapshot()); Assert.Equal(frame.Time, view.Schedule.Time);
        Assert.Equal("The server controls the weather.", view.Choose(0));
        view.AcceptRemoteAuthority(new(frame.Time, []) { Enabled = false });
        Assert.Same(view.World.Data.Default, view.World.ForcedWeather);
        view.AcceptRemoteAuthority(frame); Assert.Null(view.World.ForcedWeather);
    }

    [Fact]
    public void PausedRuntimeInitializesWeatherAndReplacementCannotReuseALiveWeatherOwner()
    {
        var source = new SimulationWeather(new WeatherSchedule(MakeWorld()), Simulation.SyntheticTown.Database(), new ParticleLibrary(), (_, _) => 0, []);
        using var runtime = new WorldRuntime(new Meitou.Simulation.WorldSettings { StartHours = 37 }, new Meitou.Simulation.OpenGroundWalkability((_, _) => 0), [source]);
        runtime.Speed = 0;
        Assert.Same(source, runtime.World.Weather); Assert.Equal(WeatherTime.FromHours(37), source.Schedule.Time);
        var before = source.CaptureWeather(); var world = runtime.World;
        runtime.Advance(1); Assert.Equal(0, runtime.World.Tick); Assert.Equal(before.Regions, source.CaptureWeather().Regions);
        Assert.ThrowsAny<Exception>(() => runtime.ReplaceWorld([source]));
        Assert.Same(world, runtime.World); Assert.Same(source, world.Weather); Assert.Equal(before.Regions, source.CaptureWeather().Regions);
        var next = source.CreateSibling(); runtime.ReplaceWorld([next]);
        Assert.Same(next, runtime.World.Weather); Assert.NotSame(world, runtime.World);
    }

    [Fact]
    public void FixedAffectingMapNodesExistWithoutAViewerOrGpu()
    {
        var library = new ParticleLibrary();
        library.AddScript("system gas\n{\n technique t\n {\n visual_particle_quota 4\n }\n}\n", "test.pu");
        Assert.NotNull(library.FindSystem("gas"));
        var effect = new EffectRecord("Gas", ParticleEffectType.Point, "gas", false, false, 1, 0, 0, Vector3.One, 1,
            false, 100, 0, 0, 0, 0) { AffectType = WeatherAffecting.Gas, EffectRadius = 80, EffectStrength = 0.5f };
        var at = new Vector3(1000, 20, -1000);
        var source = new SimulationWeather(new WeatherSchedule(MakeWorld()), Simulation.SyntheticTown.Database(), library,
            (_, _) => 20, [new MapEffectPlacement(effect, at, "gas vent")]);
        source.Advance(WeatherTime.At(1, 12), 1f / 30);
        var zone = Assert.Single(source.Zones);
        Assert.Equal(at, zone.Position);
        Assert.Equal(WeatherAffecting.Gas, zone.Affect);
        Assert.Equal(80, zone.Radius);
        Assert.Equal(0.5f, zone.Strength);
    }
}
