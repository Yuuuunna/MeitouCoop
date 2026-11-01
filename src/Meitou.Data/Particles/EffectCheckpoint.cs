using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Data.Particles;

public enum EffectGroupKind { Placed, Respawning, Global, GlobalPoint }
public sealed record EffectUnitState(int Seed, Vector3 Position, Vector3 Anchor, Vector3 Tint, float Alpha, Quaternion Orientation,
    float Size, float LifeScale, float? Radius, float? Life, float Age, bool Stopped, float StoppedFor, ParticleEnvironment Environment,
    float DistanceToCamera, Vector2 Heading, float TurnTimer, float GroundTimer, UnitWrap Wrap, Vector3 WrapCentre, float WrapSize,
    float? WrapMaxHeight, Vector3 WrapAhead, float Pending, int WrapSalt, ParticleSimulationState? Simulation);
public sealed record EffectGroupState(int Version, EffectGroupKind Kind, string? EffectId, string System, EffectType Type, int Seed, int Made,
    FoliageRandomState Random, float Timer, Vector3? LastEye, EffectUnitState[] Units);

public sealed partial class EffectUnit
{
    static float? Optional(float value) => float.IsPositiveInfinity(value) ? null : value;
    public EffectUnitState Capture() => new(Seed, Position, Anchor, Tint, Alpha, Orientation, Size, LifeScale, Optional(Radius), Optional(Life),
        Age, Stopped, StoppedFor, Environment, DistanceToCamera, Heading, TurnTimer, GroundTimer, Wrap, WrapCentre, WrapSize,
        Optional(WrapMaxHeight), WrapAhead, Pending, wrapSalt, Simulation?.Capture());

    internal static EffectUnit FromState(EffectRecord effect, PuSystemDef system, EffectUnitState s)
    {
        if (s is null || !Finite(s.Position) || !Finite(s.Anchor) || !Finite(s.Tint) || !Finite(s.WrapCentre) || !Finite(s.WrapAhead) ||
            !Finite(s.Heading) || !Finite(new Vector4(s.Orientation.X, s.Orientation.Y, s.Orientation.Z, s.Orientation.W)) ||
            !Finite(s.Environment.Wind) || !float.IsFinite(s.Environment.WindSpeedMultiplier) || !float.IsFinite(s.Environment.EmissionScale) ||
            new[] { s.Alpha, s.Size, s.LifeScale, s.Age, s.StoppedFor, s.DistanceToCamera, s.TurnTimer, s.GroundTimer, s.WrapSize, s.Pending }.Any(v => !float.IsFinite(v)) ||
            s.Radius is { } radius && !float.IsFinite(radius) || s.Life is { } life && !float.IsFinite(life) || s.WrapMaxHeight is { } height && !float.IsFinite(height) ||
            s.Age < 0 || s.StoppedFor < 0 || s.Pending < 0 || !Enum.IsDefined(s.Wrap)) throw new InvalidDataException("Invalid effect unit checkpoint.");
        ParticleSimulation? simulation = null;
        if (s.Simulation is { } saved)
        {
            simulation = new ParticleSimulation(system, s.Seed, persistent: true); simulation.Restore(saved);
        }
        return new EffectUnit(effect, system, s.Seed, persistent: true)
        {
            Position = s.Position, Anchor = s.Anchor, Tint = s.Tint, Alpha = s.Alpha, Orientation = s.Orientation,
            Size = s.Size, LifeScale = s.LifeScale, Radius = s.Radius ?? float.PositiveInfinity, Life = s.Life ?? float.PositiveInfinity,
            Age = s.Age, Stopped = s.Stopped, StoppedFor = s.StoppedFor, Environment = s.Environment, DistanceToCamera = s.DistanceToCamera,
            Heading = s.Heading, TurnTimer = s.TurnTimer, GroundTimer = s.GroundTimer, Wrap = s.Wrap, WrapCentre = s.WrapCentre,
            WrapSize = s.WrapSize, WrapMaxHeight = s.WrapMaxHeight ?? float.PositiveInfinity, WrapAhead = s.WrapAhead,
            Pending = s.Pending, wrapSalt = s.WrapSalt, Simulation = simulation,
        };
    }
    static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
}

public abstract partial class EffectGroup
{
    EffectGroupKind CheckpointKind => this switch
    {
        PlacerEffectGroup => EffectGroupKind.Placed, RespawningEffectGroup => EffectGroupKind.Respawning,
        GlobalEffectGroup => EffectGroupKind.Global, GlobalPointEffectGroup => EffectGroupKind.GlobalPoint,
        _ => throw new NotSupportedException("This effect group is not an authoritative world group."),
    };
    public EffectGroupState Capture()
    {
        if (random is not CheckpointRandom stored) throw new InvalidOperationException("This effect group is not checkpointable.");
        return new(1, CheckpointKind, Effect.RecordId, System.Name, Effect.Type, seed, made, stored.Capture(),
            this is RespawningEffectGroup spawning ? spawning.CheckpointTimer : 0,
            this is GlobalEffectGroup global ? global.CheckpointEye : null, units.Select(u => u.Capture()).ToArray());
    }
    public void Restore(EffectGroupState state)
    {
        if (random is not CheckpointRandom stored) throw new InvalidOperationException("This effect group is not checkpointable.");
        if (state is null || state.Version != 1 || state.Kind != CheckpointKind || state.EffectId != Effect.RecordId || state.System != System.Name ||
            state.Type != Effect.Type || state.Seed != seed || state.Made < 0 || state.Units is null || state.Units.Length > 65536 || state.Made < state.Units.Length ||
            !float.IsFinite(state.Timer) || state.LastEye is { } eye && (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z)))
            throw new InvalidDataException("Effect checkpoint does not match its group.");
        var probe = new CheckpointRandom(0); probe.Restore(state.Random);
        var restored = state.Units.Select(s => EffectUnit.FromState(Effect, System, s)).ToArray();
        if (restored.Select(u => u.Seed).Distinct().Count() != restored.Length) throw new InvalidDataException("Duplicate effect unit identity.");
        stored.Restore(state.Random); made = state.Made;
        units.Clear(); units.AddRange(restored);
        if (this is RespawningEffectGroup spawning) spawning.CheckpointTimer = state.Timer;
        if (this is GlobalEffectGroup global) global.CheckpointEye = state.LastEye;
    }
}
