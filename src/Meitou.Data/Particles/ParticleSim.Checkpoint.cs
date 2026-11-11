using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Data.Particles;

public sealed record ParticleEmitterState(float Accumulator, float Clock, float? Cycle, bool Resting, float Age);
public sealed record ParticleTechniqueState(float[][] Channels, uint[] Seeds, byte[] Emitters, ParticleEmitterState[] Sources,
    float ObserverClock, float[] RandomiserClock, uint Step);
public sealed record ParticleSimulationState(int Version, string System, FoliageRandomState Random, Vector3 Origin, Vector3 WorldOffset,
    Quaternion Orientation, float Size, float LifeScale, bool EmissionStopped, float Time, ParticleTechniqueState[] Techniques);
