using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.World;

public class WeatherHazardCheckpointTests
{
    internal static (GameDatabase Data, SimulationWeather Weather) Scene()
    {
        var record = SyntheticTown.Rec("saved-gas", FcsRecordType.Effect, "Saved gas");
        record.Ints["type"] = (int)Meitou.Data.Particles.EffectType.WanderingGas;
        record.Ints["affect type"] = (int)WeatherAffecting.Gas;
        record.Strings["particle system"] = "saved_gas";
        record.Floats["effect radius"] = 30; record.Floats["effect strength min"] = 0.5f;
        record.Floats["min time to live"] = 0.6f; record.Floats["max time to live"] = 1.1f;
        record.Floats["min altitude"] = 0; record.Floats["wandering speed"] = 25;
        var db = SyntheticTown.Database(extra: [record]);
        var weather = new WeatherDef { StringId = "gas-weather", Name = "Gas weather", Effects =
            [new(new EffectDef { StringId = "saved-gas", Name = "Saved gas" }, 2, 0, 0)] };
        var season = SeasonDef.Build("gas-season", "Gas season", [new(weather, 1, 1000, 1000)], 0.2f, 1, weather);
        var region = new RegionDef { StringId = "gas-region", Name = "Gas region", Index = 1, Seasons = [new(season, 0, 1, 100)] };
        var schedule = new WeatherSchedule(new WeatherWorld(WeatherData.FromDefinitions([weather], [season], [region]),
            new WeatherAreas(Enumerable.Repeat(1, WeatherAreas.Cells * WeatherAreas.Cells).ToArray()), 13, WeatherTime.At(0)));
        return (db, new SimulationWeather(schedule, db, ParticleCheckpointTests.Library(), (_, _) => 20,
            [new MapEffectPlacement(EffectRecord.From(db.Find("saved-gas")!), new Vector3(1000, 20, 1000), "saved vent")]));
    }

    [Fact]
    public void WholeWeatherRestoresGenerationNodesAndFutureSimulationWithoutAnExtraUpdate()
    {
        var (_, first) = Scene(); var time = WeatherTime.FromHours(61.2);
        for (int i = 0; i < 24; i++) first.Advance(time, 1f / 30);
        var saved = first.CaptureState();
        Assert.Contains(saved.Regions, r => r.Groups.Length > 0);
        Assert.Contains(saved.Regions.SelectMany(r => r.Groups), g => g.Made > 0);
        Assert.Contains(saved.Regions, r => r.Generation > 1);
        var second = first.CreateSibling(); second.RestoreState(saved);
        Assert.Equal(ParticleCheckpointTests.Encoded(saved), ParticleCheckpointTests.Encoded(second.CaptureState()));
        for (int i = 0; i < 400; i++)
        {
            first.Advance(time, 1f / 30); second.Advance(time, 1f / 30);
            Assert.Equal(first.Zones, second.Zones);
        }
        Assert.Equal(ParticleCheckpointTests.Encoded(first.CaptureState()), ParticleCheckpointTests.Encoded(second.CaptureState()));
        Assert.Contains(second.CaptureState().Regions.SelectMany(r => r.Groups), g => g.Made > 2);
    }
}
