using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Data.Particles;

public sealed record ParticleEmitterState(float Accumulator, float Clock, float? Cycle, bool Resting, float Age);
public sealed record ParticleTechniqueState(float[][] Channels, uint[] Seeds, byte[] Emitters, ParticleEmitterState[] Sources,
    float ObserverClock, float[] RandomiserClock, uint Step);
public sealed record ParticleSimulationState(int Version, string System, FoliageRandomState Random, Vector3 Origin, Vector3 WorldOffset,
    Quaternion Orientation, float Size, float LifeScale, bool EmissionStopped, float Time, ParticleTechniqueState[] Techniques);

public sealed partial class ParticleSimulation
{
    static float[][] Channels(TechniqueState t) => [t.X, t.Y, t.Z, t.Vx, t.Vy, t.Vz, t.Age, t.Life, t.Width, t.Height, t.Depth,
        t.Rotation, t.RotationSpeed, t.R, t.G, t.B, t.A, t.ER, t.EG, t.EB, t.EA];

    public ParticleSimulationState Capture()
    {
        if (random is not CheckpointRandom stored) throw new InvalidOperationException("This particle simulation does not use checkpointable randomness.");
        return new(1, Definition.Name, stored.Capture(), Origin, WorldOffset, Orientation, Size, LifeScale, EmissionStopped, Time,
            techniques.Select(t => new ParticleTechniqueState(Channels(t).Select(c => c.AsSpan(0, t.Count).ToArray()).ToArray(),
                t.Seed.AsSpan(0, t.Count).ToArray(), t.Emitter.AsSpan(0, t.Count).ToArray(),
                t.Emitters.Select(e => new ParticleEmitterState(e.Accumulator, e.Clock, float.IsPositiveInfinity(e.Cycle) ? null : e.Cycle, e.Resting, e.Age)).ToArray(),
                t.ObserverClock, t.RandomiserClock.ToArray(), t.Step)).ToArray());
    }

    public void Restore(ParticleSimulationState state)
    {
        if (random is not CheckpointRandom stored) throw new InvalidOperationException("This particle simulation does not use checkpointable randomness.");
        if (state is null || state.Version != 1 || state.System != Definition.Name || state.Techniques is null || state.Techniques.Length != techniques.Length ||
            !Finite(state.Origin) || !Finite(state.WorldOffset) || !Finite(state.Orientation) || !float.IsFinite(state.Size) ||
            !float.IsFinite(state.LifeScale) || !float.IsFinite(state.Time) || state.Time < 0) throw new InvalidDataException("Invalid particle simulation checkpoint.");
        var probe = new CheckpointRandom(0); probe.Restore(state.Random);
        long particles = 0;
        for (int i = 0; i < techniques.Length; i++)
        {
            var t = techniques[i]; var s = state.Techniques[i];
            if (s is null || s.Seeds is null || s.Emitters is null || s.Channels is not { Length: 21 } || s.Sources is null || s.RandomiserClock is null ||
                s.Seeds.Length > t.Capacity || s.Emitters.Length != s.Seeds.Length || s.Sources.Length != t.Emitters.Length || s.RandomiserClock.Length != t.RandomiserClock.Length ||
                s.Channels.Any(c => c is null || c.Length != s.Seeds.Length || c.Any(v => !float.IsFinite(v))) ||
                s.Emitters.Any(e => e >= t.Emitters.Length) || !float.IsFinite(s.ObserverClock) || s.RandomiserClock.Any(v => !float.IsFinite(v)) ||
                s.Sources.Any(e => e is null || !float.IsFinite(e.Accumulator) || !float.IsFinite(e.Clock) ||
                    e.Cycle is { } cycle && !float.IsFinite(cycle) || e.Cycle is null && !e.Resting || !float.IsFinite(e.Age)))
                throw new InvalidDataException("Invalid particle technique checkpoint.");
            particles += s.Seeds.Length;
            if (particles > 1000000) throw new InvalidDataException("Particle checkpoint exceeds its pool budget.");
        }
        stored.Restore(state.Random);
        Origin = state.Origin; WorldOffset = state.WorldOffset; Orientation = state.Orientation;
        Size = state.Size; LifeScale = state.LifeScale; EmissionStopped = state.EmissionStopped; Time = state.Time;
        for (int i = 0; i < techniques.Length; i++)
        {
            var t = techniques[i]; var s = state.Techniques[i];
            t.Resize(Math.Max(s.Seeds.Length, Math.Min(t.Capacity, InitialPool))); t.Count = s.Seeds.Length;
            var channels = Channels(t);
            for (int k = 0; k < channels.Length; k++) s.Channels[k].CopyTo(channels[k], 0);
            s.Seeds.CopyTo(t.Seed, 0); s.Emitters.CopyTo(t.Emitter, 0);
            for (int k = 0; k < t.Emitters.Length; k++)
            {
                var e = t.Emitters[k]; var saved = s.Sources[k];
                e.Accumulator = saved.Accumulator; e.Clock = saved.Clock; e.Cycle = saved.Cycle ?? float.PositiveInfinity; e.Resting = saved.Resting; e.Age = saved.Age;
            }
            t.ObserverClock = s.ObserverClock; s.RandomiserClock.CopyTo(t.RandomiserClock, 0); t.Step = s.Step;
        }
    }
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
}
