using System.Numerics;

namespace Meitou.Modding;

/// <summary>
/// How a code mod's ribbon trail looks (<c>ModContext.KeepTrail</c>, docs/modding.md "Trails").
/// </summary>
public sealed record TrailStyle
{
    /// <summary>
    /// 0: a ribbon through the anchor's point, facing the camera, <see cref="Width"/> wide. Above 0: a band swept by the anchor's +Y axis from its
    /// origin this far (times the anchor's stretch; on <see cref="EffectAnchor.Weapon"/> 8.5 reaches the tip), as a blade sweeps.
    /// </summary>
    public float Span { get; init; }

    /// <summary>
    /// A point ribbon's width at the head, in units.
    /// </summary>
    public float Width { get; init; } = 2;

    /// <summary>
    /// A point ribbon's width at the tail (null: <see cref="Width"/>).
    /// </summary>
    public float? EndWidth { get; init; }

    /// <summary>
    /// How long a place the anchor passed stays in the trail, in seconds: the trail's length in time.
    /// </summary>
    public float Seconds { get; init; } = 0.3f;

    /// <summary>
    /// The colour at the head (rgb up to 4, alpha 0 to 1).
    /// </summary>
    public Vector4 Colour { get; init; } = Vector4.One;

    /// <summary>
    /// The colour at the tail (null: <see cref="Colour"/> with alpha 0, so the trail fades out).
    /// </summary>
    public Vector4? EndColour { get; init; }

    /// <summary>
    /// A particle material (an Ogre <c>.material</c> of the particles, the base game's or a mod's) whose texture, blend and colour draw the trail;
    /// null: Meitou's soft additive streak.
    /// </summary>
    public string? Material { get; init; }
}
