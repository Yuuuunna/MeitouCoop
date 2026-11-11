using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;
using Meitou.Data.World;
using WeatherEffectEntry = Meitou.Data.Particles.WeatherEffectEntry;

namespace Meitou.Game.Rendering.Particles;

public sealed unsafe partial class ParticleRenderer
{
    int GroupCount => LocalGroupCount + _presented.Groups.Count;

    EffectGroup GroupAt(int i) => i < LocalGroupCount ? _groups.At(i) : _presented.Groups[i - LocalGroupCount];
    readonly PresentedEffectSet _presented;
    readonly HashSet<EffectGroup> _mapPlacerGroups = [];
    WeatherEffectFrame? _authorityFrame;
    object? _authorityOwner;
    IReadOnlyList<WeatherEffectEntry>? _filteredSource;
    IReadOnlyList<WeatherEffectEntry> _filteredEffects = [];
    IEnumerable<EffectGroup> AllGroups => Enumerable.Range(0, _groups.Count).Select(_groups.At).Where(ShowLocal).Concat(_presented.Groups);

    static bool Affecting(EffectRecord effect) => effect.AffectType != WeatherAffecting.None && effect.EffectRadius > 0;
    bool ShowLocal(EffectGroup group) => _authorityFrame is null || !Affecting(group.Effect) || !(group.Entry is not null && !group.Effect.IsCameraGroup || _mapPlacerGroups.Contains(group));
    /// <summary>Null restores local scheduling; an empty frame explicitly removes all authoritative roots.</summary>
    public void SetAuthorityEffects(WeatherEffectFrame? frame, GameDatabase? content, object? owner)
    {
        if (ReferenceEquals(frame, _authorityFrame) && ReferenceEquals(owner, _authorityOwner))
            return;
        Sync();
        if (!ReferenceEquals(owner, _authorityOwner))
            _presented.Clear();
        if (frame is null)
            _presented.Clear();
        else
            _presented.Accept(frame, id => content?.Find(id)is { Type: FcsRecordType.Effect } record ? EffectRecord.From(record, content) : null);
        if ((frame is null) != (_authorityFrame is null))
            _filteredSource = null;
        _authorityOwner = owner;
        _authorityFrame = frame;
        PreloadTextures(_presented.Groups);
    }

    WeatherEffectInput FilterWeather(WeatherEffectInput input)
    {
        if (_authorityFrame is null)
            return input;
        if (!ReferenceEquals(_filteredSource, input.Effects))
        {
            _filteredSource = input.Effects;
            _filteredEffects = input.Effects.Where(e => e.Effect.IsCameraGroup || !Affecting(e.Effect)).ToArray();
        }

        return new()
        {
            Effects = _filteredEffects,
            Strength = input.Strength,
            Wind = input.Wind,
            Version = input.Version,
            Region = input.Region
        };
    }

    // Include replica roots without allocating LINQ iterators in the per-frame walks.
    int LocalGroupCount => _groups.Count;

    bool GroupVisible(int i, EffectGroup group) => i >= LocalGroupCount || ShowLocal(group);
    bool HasGroups
    {
        get
        {
            for (int i = 0; i < GroupCount; i++)
                if (GroupVisible(i, GroupAt(i)))
                    return true;
            return false;
        }
    }

    /// <summary>Removes transient units when their world is replaced or its view is withdrawn.</summary>
    public void ClearBursts()
    {
        Sync();
        _groups.ClearBursts();
    }
}
