using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Data.Particles;

/// <summary>
/// Emitter clocks and emission history, including the one-shot and finite-duration decisions.
/// </summary>
public sealed record ParticleEmitterState(float Accumulator, float Clock, float? Cycle, bool Resting, float Age,
    bool Forced = false, bool CycleEmitted = false);
/// <summary>
/// Live particle channels and the technique's emitter and affector state.
/// </summary>
public sealed record ParticleTechniqueState(float[][] Channels, uint[] Seeds, byte[] Emitters, ParticleEmitterState[] Sources,
    float ObserverClock, float[] RandomiserClock, uint Step);
/// <summary>
/// A deterministic particle checkpoint; version 3 includes node transforms and emitter emission history.
/// </summary>
public sealed record ParticleSimulationState(int Version, string System, FoliageRandomState Random, Vector3 Origin, Vector3 WorldOffset,
    Quaternion Orientation, float Size, float LifeScale, bool EmissionStopped, float Time, ParticleTechniqueState[] Techniques,
    float Stretch = 1, bool KeepLocal = false);
