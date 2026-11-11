using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Runtime;
public sealed record RegionEffectState(int Index, string RegionId, int Generation, EffectGroupState[] Groups);
public sealed record SimulationWeatherState(int Version, WeatherFrame Weather, RegionEffectState[] Regions, EffectGroupState[] Placed, WeatherHere? Forced);
public sealed partial class SimulationWeather
{
    public WeatherEffectFrame CaptureEffects()
    {
        if (_capturedEffects is not null)
            return _capturedEffects;
        var nodes = new List<EffectNodeSnapshot>();
        foreach (var(index, region)in _regional.OrderBy(p => p.Key))
            for (int i = 0; i < region.Groups.Count; i++)
                foreach (var unit in region.Groups[i].Units)
                    nodes.Add(EffectNodeSnapshot.Capture(FormattableString.Invariant($"r:{index}:{region.Serial}:{i}:{unit.Seed}"), true, unit));
        for (int i = 0; i < _placed.Count; i++)
            foreach (var unit in _placed[i].Units)
                nodes.Add(EffectNodeSnapshot.Capture(FormattableString.Invariant($"p:{i}:{unit.Seed}"), false, unit));
        return _capturedEffects = new(nodes.ToArray());
    }

    public SimulationWeatherState CaptureState()
    {
        if (!_initialized)
            throw new InvalidOperationException("Weather must be synchronized before capture.");
        return new(1, CaptureWeather(), Schedule.World.Regions.Select(r => new RegionEffectState(r.Index, r.Def.StringId, r.Serial, _regional.TryGetValue(r.Index, out var state) ? state.Groups.Select(g => g.Capture()).ToArray() : [])).ToArray(), _placed.Select(g => g.Capture()).ToArray(), Forced);
    }

    /// <summary>
    /// Restores into a detached candidate only. The owner switches worlds after all services validate.
    /// </summary>
    public void RestoreState(SimulationWeatherState state)
    {
        if (HasWorldOwner)
            throw new InvalidOperationException("Weather restore requires a detached candidate.");
        if (state is null || state.Version != 1 || state.Weather is null || state.Regions is null || state.Placed is null || state.Regions.Length != Schedule.World.Regions.Count || state.Regions.Any(r => r is null || r.Groups is null) || state.Regions.Select(r => r.Index).Distinct().Count() != state.Regions.Length || state.Placed.Length != _placed.Count)
            throw new InvalidDataException("Weather effect checkpoint does not match its world.");
        if (state.Forced is { } forced && (!Enum.IsDefined(forced.Affect) || new[]
        {
            forced.Strength,
            forced.AcidicGround,
            forced.AcidicWater,
            forced.WindSpeed,
            forced.Wetness
        }.Any(v => !float.IsFinite(v))))
            throw new InvalidDataException("Invalid forced weather checkpoint.");
        RestoreCandidate(state.Weather);
        foreach (var region in Schedule.World.Regions)
        {
            var saved = state.Regions.SingleOrDefault(r => r.Index == region.Index);
            if (saved is null || saved.RegionId != region.Def.StringId)
                throw new InvalidDataException("Unknown weather effect region.");
            var snapshot = state.Weather.Regions.Single(r => r.RegionId == region.Def.StringId);
            region.Restore(snapshot, state.Weather.Time, saved.Generation);
            var groups = _areas.TryGetValue(region.Index, out var area) ? CreateRegional(region, area) : [];
            if (groups.Count != saved.Groups.Length)
                throw new InvalidDataException("Weather effect catalogue changed.");
            for (int i = 0; i < groups.Count; i++)
                groups[i].Restore(saved.Groups[i]);
            _regional.Add(region.Index, new RegionGroups(region.Serial, groups));
        }

        for (int i = 0; i < _placed.Count; i++)
            _placed[i].Restore(state.Placed[i]);
        Forced = state.Forced;
        // Do not Update(0): an expired timer or empty tail can spawn/remove a node even with no elapsed time.
        foreach (var group in _regional.Values.SelectMany(r => r.Groups).Concat(_placed))
            foreach (var unit in group.Units)
                if (!unit.Stopped)
                    _zones.Add(new(unit.Position, group.Effect.EffectRadius, group.Effect.AffectType, group.Effect.EffectStrength));
        _initialized = true;
    }
}