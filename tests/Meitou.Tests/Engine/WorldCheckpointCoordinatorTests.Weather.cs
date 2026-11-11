using Meitou.Simulation.Player;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using Meitou.Tests.Save;

namespace Meitou.Tests.Engine;

public partial class WorldCheckpointCoordinatorTests
{
    [Fact]
    public void CheckpointRestoresHazardsAndMalformedEffectPayloadKeepsTheLiveWorld()
    {
        string root = SaveSamples.Temp("hazard-checkpoint");
        var (db, weather) = World.WeatherHazardCheckpointTests.Scene(); var data = PopulationData.Create(db, []);
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 20), [new PlayerSystem(data), weather]);
        try
        {
            for (int i = 0; i < 24; i++) weather.Advance(weather.Schedule.Time, 1f / 30);
            weather.Forced = new(WeatherAffecting.Gas, 0.25f, 0, 0);
            var expected = weather.CaptureState();
            var store = new CheckpointStore(root); var coordinator = new WorldCheckpointCoordinator(runtime, data, store, Options(data));
            coordinator.Commit(coordinator.Capture()); var saved = store.LoadCurrent()!;
            var corrupted = saved.World.Clone(); corrupted.Camera.Record.Strings["meitou weather effects"] = "not-base64";
            var failed = PopulationData.Create(db, []); var oldWorld = runtime.World;
            Assert.Throws<InvalidDataException>(() => coordinator.Restore(new(saved.Id, corrupted, saved.Metadata), failed,
                d => [new PlayerSystem(d), weather.CreateSibling()], Options(failed)));
            Assert.Same(oldWorld, runtime.World);
            Assert.Equal(World.ParticleCheckpointTests.Encoded(expected), World.ParticleCheckpointTests.Encoded(weather.CaptureState()));
            var missing = saved.World.Clone(); missing.Camera.Record.Strings.Remove("meitou weather effects");
            Assert.Throws<InvalidDataException>(() => coordinator.Restore(new(saved.Id, missing, saved.Metadata), failed,
                d => [new PlayerSystem(d), weather.CreateSibling()], Options(failed)));
            Assert.Same(oldWorld, runtime.World);
            var candidate = PopulationData.Create(db, []); var next = weather.CreateSibling();
            coordinator.Restore(saved, candidate, d => [new PlayerSystem(d), next], Options(candidate));
            Assert.Same(next, runtime.World.Weather);
            Assert.Equal(World.ParticleCheckpointTests.Encoded(expected), World.ParticleCheckpointTests.Encoded(next.CaptureState()));
            for (int i = 0; i < 150; i++)
            {
                weather.Advance(weather.Schedule.Time, 1f / 30); next.Advance(next.Schedule.Time, 1f / 30);
                Assert.Equal(weather.Zones, next.Zones);
            }
            Assert.Equal(World.ParticleCheckpointTests.Encoded(weather.CaptureState()), World.ParticleCheckpointTests.Encoded(next.CaptureState()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CheckpointRestoresExactRegionalWeatherAndRejectsMalformedWeatherWithoutChangingTheOldWorld()
    {
        string root = SaveSamples.Temp("weather-checkpoint"); var data = Data();
        var weather = new SimulationWeather(new WeatherSchedule(World.WeatherScheduleTests.MakeWorld()), data.Db, new ParticleLibrary(), (_, _) => 0, []);
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), [new PlayerSystem(data), weather]);
        try
        {
            runtime.World.GameHours = 61.23456789; runtime.Synchronize();
            var expected = weather.CaptureWeather();
            var store = new CheckpointStore(root); var coordinator = new WorldCheckpointCoordinator(runtime, data, store, Options(data));
            coordinator.Commit(coordinator.Capture()); var saved = store.LoadCurrent()!;
            runtime.World.GameHours += 12; runtime.Synchronize();
            var oldWorld = runtime.World; var oldWeather = weather.CaptureWeather();
            var malformed = saved.World.Clone(); malformed.Camera.Record.Strings["meitou regional weather"] = "{}";
            var failedData = Data();
            Assert.Throws<InvalidDataException>(() => coordinator.Restore(new(saved.Id, malformed, saved.Metadata), failedData,
                d => [new PlayerSystem(d), weather.CreateSibling()], Options(failedData)));
            Assert.Same(oldWorld, runtime.World); Assert.Equal(oldWeather.Regions, weather.CaptureWeather().Regions);
            var candidate = Data(); var next = weather.CreateSibling();
            var restored = coordinator.Restore(saved, candidate, d => [new PlayerSystem(d), next], Options(candidate));
            Assert.Null(restored.CleanupFailure); Assert.Same(next, runtime.World.Weather);
            Assert.Equal(expected.Time, next.Schedule.Time); Assert.Equal(expected.Regions, next.CaptureWeather().Regions);
            var legacy = saved.World.Clone(); legacy.Camera.Record.Strings.Remove("meitou weather effects");
            legacy.Camera.Record.Strings["meitou regional weather"] = legacy.Camera.Record.Strings["meitou regional weather"].Replace("\"Version\":2", "\"Version\":1", StringComparison.Ordinal);
            var legacyData = Data(); var legacyWeather = weather.CreateSibling();
            restored.Coordinator.Restore(new(saved.Id, legacy, saved.Metadata), legacyData,
                d => [new PlayerSystem(d), legacyWeather], Options(legacyData));
            Assert.Equal(expected.Regions, legacyWeather.CaptureWeather().Regions);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
