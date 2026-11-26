using System.Text.Json.Serialization;

namespace Meitou.Modding;

/// <summary>
/// Playback modulation a code mod applies to active sounds (<c>ModContext.SetAudioModulation</c>, docs/modding.md "Audio modulation"):
/// <paramref name="Rate"/> multiplies playback speed and pitch (<see cref="MinRate"/> to <see cref="MaxRate"/>; 1 = unchanged),
/// <paramref name="LowPass"/> caps the per-sound one-pole low-pass coefficient (<see cref="MinLowPass"/> to 1; 1 = unfiltered),
/// <paramref name="Gain"/> multiplies volume (0 to <see cref="MaxGain"/>; 1 = unchanged).
/// Applies to world and ambience sounds by default; set <paramref name="AffectMusic"/> or <paramref name="AffectUi"/> to modulate music or UI
/// sounds as well.
/// </summary>
public readonly record struct AudioModulation(float Rate = 1, float LowPass = 1, float Gain = 1,
    bool AffectMusic = false, bool AffectUi = false)
{
    /// <summary>
    /// Lowest and highest allowed playback rate multiplier.
    /// </summary>
    public const float MinRate = 0.1f, MaxRate = 4f;

    /// <summary>
    /// Lowest allowed low-pass coefficient (1 = unfiltered).
    /// </summary>
    public const float MinLowPass = 0.01f;

    /// <summary>
    /// Highest allowed volume gain multiplier.
    /// </summary>
    public const float MaxGain = 2f;

    /// <summary>
    /// No modulation (all multipliers at 1).
    /// </summary>
    public static readonly AudioModulation Identity = new(1, 1, 1, false, false);

    /// <summary>
    /// Whether this modulation has no effect (rate, low-pass and gain are all 1).
    /// </summary>
    [JsonIgnore]
    public bool IsIdentity => Rate == 1 && LowPass == 1 && Gain == 1;

    /// <summary>
    /// Returns a copy with <see cref="Rate"/>, <see cref="LowPass"/> and <see cref="Gain"/> clamped to their valid ranges (non-finite values
    /// become 1).
    /// </summary>
    public AudioModulation Clamped() => new(
        float.IsFinite(Rate) ? Math.Clamp(Rate, MinRate, MaxRate) : 1,
        float.IsFinite(LowPass) ? Math.Clamp(LowPass, MinLowPass, 1) : 1,
        float.IsFinite(Gain) ? Math.Clamp(Gain, 0, MaxGain) : 1,
        AffectMusic,
        AffectUi);
}
