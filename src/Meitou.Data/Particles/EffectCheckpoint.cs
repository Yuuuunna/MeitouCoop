using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Data.Particles;

/// <summary>
/// Authoritative world effect group implementations supported by checkpoints.
/// </summary>
public enum EffectGroupKind { Placed, Respawning, Global, GlobalPoint }
/// <summary>
/// A world effect unit and its optional live particle simulation.
/// </summary>
public sealed record EffectUnitState(int Seed, Vector3 Position, Vector3 Anchor, Vector3 Tint, float Alpha, Quaternion Orientation,
    float Size, float LifeScale, float? Radius, float? Life, float Age, bool Stopped, float StoppedFor, ParticleEnvironment Environment,
    float DistanceToCamera, Vector2 Heading, float TurnTimer, float GroundTimer, UnitWrap Wrap, Vector3 WrapCentre, float WrapSize,
    float? WrapMaxHeight, Vector3 WrapAhead, float Pending, int WrapSalt, ParticleSimulationState? Simulation,
    float Stretch = 1, bool KeepLocal = false);
/// <summary>
/// The group's deterministic generator and units; version 2 preserves retirement and unit node transforms.
/// </summary>
public sealed record EffectGroupState(int Version, EffectGroupKind Kind, string? EffectId, string System, EffectType Type, int Seed, int Made,
    FoliageRandomState Random, float Timer, Vector3? LastEye, EffectUnitState[] Units, bool Retiring = false);
