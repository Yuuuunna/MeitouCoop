using System.Numerics;
using System.Text.Json;
using Meitou.Data.Particles;
using Meitou.Data.World;
using ParticleEffectType = Meitou.Data.Particles.EffectType;

namespace Meitou.Tests.World;

public class ParticleCheckpointTests
{
    internal const string Script = """
        system saved_gas
        {
            technique
            {
                visual_particle_quota 64
                emitter Box
                {
                    emission_rate 12
                    time_to_live dyn_random
                    {
                        min 1
                        max 3
                    }
                    velocity dyn_random
                    {
                        min 1
                        max 10
                    }
                }
            }
        }
        """;
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    internal static string Encoded<T>(T state) => JsonSerializer.Serialize(state, Json);
    static T RoundTrip<T>(T state) => JsonSerializer.Deserialize<T>(Encoded(state), Json)!;
    internal static ParticleLibrary Library() { var result = new ParticleLibrary(); result.AddScript(Script, "checkpoint-test.pu"); return result; }
    internal static EffectRecord Effect(ParticleEffectType type) => new("Saved gas", type, "saved_gas", true, false, 1, 0, 0,
        Vector3.One, 1, false, 100, 0, 0.6f, 1.1f, 25)
        { RecordId = "saved-gas", MinAltitude = 0, AffectType = WeatherAffecting.Gas, EffectRadius = 30, EffectStrength = 0.5f };

    [Fact]
    public void RandomStateResumesAcrossTwistsAndMixedCallsWithoutAliasing()
    {
        var first = new CheckpointRandom(83);
        for (int i = 0; i < 623; i++) first.Next();
        var state = RoundTrip(first.Capture());
        var second = new CheckpointRandom(9); second.Restore(state);
        Array.Clear(state.Words);
        for (int i = 0; i < 800; i++)
        {
            Assert.Equal(first.Next(), second.Next());
            Assert.Equal(first.Next(int.MinValue, int.MaxValue), second.Next(int.MinValue, int.MaxValue));
            Assert.Equal(first.NextSingle(), second.NextSingle()); Assert.Equal(first.NextDouble(), second.NextDouble());
            Assert.Equal(first.NextInt64(long.MinValue, long.MaxValue), second.NextInt64(long.MinValue, long.MaxValue));
            var a = new byte[11]; var b = new byte[11]; first.NextBytes(a); second.NextBytes(b.AsSpan()); Assert.Equal(a, b);
        }
        Assert.Throws<InvalidDataException>(() => second.Restore(state));
        Assert.Equal(first.Next(), second.Next());
    }

    [Theory]
    [InlineData(EffectGroupKind.Respawning)]
    [InlineData(EffectGroupKind.Global)]
    [InlineData(EffectGroupKind.GlobalPoint)]
    [InlineData(EffectGroupKind.Placed)]
    public void EffectCheckpointPreservesParticleTailsRespawnsAndWrapping(EffectGroupKind kind)
    {
        var system = Library().FindSystem("saved_gas")!;
        var world = new EffectWorld { Persistent = true, ActiveRadius = float.PositiveInfinity, GroundHeight = (x, z) => 20 + x * 0.001f,
            Area = new DiscArea(100, 50, 200) };
        EffectGroup Create()
        {
            var type = kind switch { EffectGroupKind.Global => ParticleEffectType.Global, EffectGroupKind.GlobalPoint => ParticleEffectType.GlobalPoint, _ => ParticleEffectType.WanderingGas };
            var effect = Effect(type); var entry = new Meitou.Data.Particles.WeatherEffectEntry(effect, 2, 0, 0);
            return kind switch
            {
                EffectGroupKind.Respawning => new RespawningEffectGroup(entry, system, 83, world),
                EffectGroupKind.Global => new GlobalEffectGroup(entry, system, 83, world),
                EffectGroupKind.GlobalPoint => new GlobalPointEffectGroup(entry, system, 83, world),
                _ => new PlacerEffectGroup(effect, system, new[] { Vector3.Zero }, 83, world),
            };
        }
        void Tick(EffectGroup group, int i)
        {
            group.Update(1f / 30, new(new Vector3(i * 2, 30, i), -Vector3.UnitZ), new() { Wind = new(3, 1) }); group.Simulate();
        }
        var first = Create(); for (int i = 0; i < 24; i++) Tick(first, i);
        Assert.True(first.ParticleCount > 0);
        var checkpoint = RoundTrip(first.Capture()); var second = Create(); second.Restore(checkpoint);
        Assert.Equal(Encoded(first.Capture()), Encoded(second.Capture()));
        for (int i = 24; i < 400; i++)
        {
            Tick(first, i); Tick(second, i);
            if (i % 29 == 0) Assert.Equal(Encoded(first.Capture()), Encoded(second.Capture()));
        }
        Assert.Equal(Encoded(first.Capture()), Encoded(second.Capture()));
        var before = Encoded(second.Capture());
        var bad = RoundTrip(second.Capture()); bad.Units[0] = bad.Units[0] with { Pending = float.NaN };
        Assert.Throws<InvalidDataException>(() => second.Restore(bad));
        Assert.Equal(before, Encoded(second.Capture()));
    }
}
