using System.Numerics;

namespace Meitou.Data.Particles;

/// <summary>Public authoritative effect roots, without particle pools or random generator state.</summary>
public sealed record EffectNodeSnapshot(string Id, string EffectId, string System, int Seed, Vector3 Position)
{
    public bool Weather { get; init; }
    public Quaternion Orientation { get; init; } = Quaternion.Identity;
    public Vector3 Tint { get; init; } = Vector3.One;
    public float Alpha { get; init; } = 1;
    public float Size { get; init; } = 1;
    public float LifeScale { get; init; } = 1;
    public float? Radius { get; init; }
    public float AffectRadius { get; init; }
    public float? Life { get; init; }
    public float Age { get; init; }
    public bool Stopped { get; init; }
    public float StoppedFor { get; init; }
    public ParticleEnvironment Environment { get; init; } = ParticleEnvironment.None;
    public UnitWrap Wrap { get; init; }
    public Vector3 WrapCentre { get; init; }
    public float WrapSize { get; init; }
    public float? WrapMaxHeight { get; init; }
    public Vector3 WrapAhead { get; init; }

    public static EffectNodeSnapshot Capture(string id, bool weather, EffectUnit unit) =>
        new(id, unit.Effect.RecordId ?? throw new InvalidOperationException("An authoritative effect requires a content identity."), unit.System.Name, unit.Seed, unit.Position)
        {
            Weather = weather, Orientation = unit.Orientation, Tint = unit.Tint, Alpha = unit.Alpha, Size = unit.Size,
            LifeScale = unit.LifeScale, Radius = Optional(unit.Radius), AffectRadius = unit.Effect.EffectRadius, Life = Optional(unit.Life), Age = unit.Age, Stopped = unit.Stopped,
            StoppedFor = unit.StoppedFor, Environment = unit.Environment, Wrap = unit.Wrap, WrapCentre = unit.WrapCentre,
            WrapSize = unit.WrapSize, WrapMaxHeight = Optional(unit.WrapMaxHeight), WrapAhead = unit.WrapAhead,
        };
    static float? Optional(float value) => float.IsPositiveInfinity(value) ? null : value;

    internal void Validate()
    {
        if (string.IsNullOrEmpty(Id) || Id.Length > 160 || string.IsNullOrEmpty(EffectId) || EffectId.Length > 4096 ||
            string.IsNullOrEmpty(System) || System.Length > 4096 || !Finite(Position) || !Finite(Tint) || !Finite(WrapCentre) || !Finite(WrapAhead) ||
            !float.IsFinite(Orientation.X) || !float.IsFinite(Orientation.Y) || !float.IsFinite(Orientation.Z) || !float.IsFinite(Orientation.W) ||
            !float.IsFinite(Environment.Wind.X) || !float.IsFinite(Environment.Wind.Y) || !float.IsFinite(Environment.WindSpeedMultiplier) || !float.IsFinite(Environment.EmissionScale) ||
            new[] { Alpha, Size, LifeScale, Age, StoppedFor, WrapSize, AffectRadius }.Any(v => !float.IsFinite(v)) || AffectRadius < 0 ||
            Radius is { } radius && (!float.IsFinite(radius) || radius < 0) || Life is { } life && !float.IsFinite(life) ||
            WrapMaxHeight is { } height && !float.IsFinite(height) || Age < 0 || StoppedFor < 0 || !Enum.IsDefined(Wrap))
            throw new InvalidDataException("Invalid authoritative effect node.");
    }
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

public sealed record WeatherEffectFrame(IReadOnlyList<EffectNodeSnapshot> Nodes)
{
    public static readonly WeatherEffectFrame Empty = new([]);
    public void Validate()
    {
        if (Nodes is null || Nodes.Count > 16384 || Nodes.Any(n => n is null) || Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != Nodes.Count)
            throw new InvalidDataException("Invalid authoritative effect frame.");
        foreach (var node in Nodes) node.Validate();
    }
}
