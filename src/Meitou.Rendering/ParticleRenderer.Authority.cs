using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;
using Meitou.Data.World;
using WeatherEffectEntry = Meitou.Data.Particles.WeatherEffectEntry;

namespace Meitou.Rendering;

public sealed unsafe partial class ParticleRenderer
{
    readonly PresentedEffectSet presented;
    readonly HashSet<EffectGroup> mapPlacerGroups = [];
    WeatherEffectFrame? authorityFrame;
    object? authorityOwner;
    IReadOnlyList<WeatherEffectEntry>? filteredSource;
    IReadOnlyList<WeatherEffectEntry> filteredEffects = [];
    IEnumerable<EffectGroup> AllGroups => weatherGroups.Groups.Concat(placerGroups).Where(ShowLocal).Concat(presented.Groups);
    static bool Affecting(EffectRecord effect) => effect.AffectType != WeatherAffecting.None && effect.EffectRadius > 0;
    bool ShowLocal(EffectGroup group) => authorityFrame is null || !Affecting(group.Effect) ||
        !(group.Entry is not null && !group.Effect.IsCameraGroup || mapPlacerGroups.Contains(group));

    /// <summary>Null restores local scheduling; an empty frame explicitly removes all authoritative roots.</summary>
    public void SetAuthorityEffects(WeatherEffectFrame? frame, GameDatabase? content, object? owner)
    {
        if (ReferenceEquals(frame, authorityFrame) && ReferenceEquals(owner, authorityOwner)) return;
        Sync();
        if (!ReferenceEquals(owner, authorityOwner)) presented.Clear();
        if (frame is null) presented.Clear();
        else presented.Accept(frame, id => content?.Find(id) is { Type: FcsRecordType.EFFECT } record ? EffectRecord.From(record, content) : null);
        if ((frame is null) != (authorityFrame is null)) filteredSource = null;
        authorityOwner = owner; authorityFrame = frame;
        PreloadTextures(presented.Groups);
    }

    WeatherEffectInput FilterWeather(WeatherEffectInput input)
    {
        if (authorityFrame is null) return input;
        if (!ReferenceEquals(filteredSource, input.Effects))
        {
            filteredSource = input.Effects;
            filteredEffects = input.Effects.Where(e => e.Effect.IsCameraGroup || !Affecting(e.Effect)).ToArray();
        }
        return new() { Effects = filteredEffects, Strength = input.Strength, Wind = input.Wind, Version = input.Version, Region = input.Region };
    }
}
