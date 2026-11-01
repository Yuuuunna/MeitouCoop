using System.Numerics;

namespace Meitou.Data.World;

/// <summary>Public regional weather state. Importing it for presentation must never roll a local schedule.</summary>
public sealed record WeatherFrame(WeatherTime Time, IReadOnlyList<WeatherRegionSnapshot> Regions)
{
    public bool Enabled { get; init; } = true;
    public void Validate()
    {
        if (!double.IsFinite(Time.Hours) || Time.Hours < 0 || Time.Hours >= 24 || Regions is null || Regions.Count > 4096 || !Enabled && Regions.Count != 0 ||
            Regions.Any(r => r is null || r.RegionId is null || r.RegionId.Length > 1000 || r.SeasonId is null || r.SeasonId.Length > 4096 ||
                r.WeatherId is null || r.WeatherId.Length > 4096 || r.SeasonIndex < 0 || r.WeatherEntryIndex < 0 ||
                !double.IsFinite(r.WeatherStartMinute) || !double.IsFinite(r.WeatherEndMinute) || !float.IsFinite(r.Strength) ||
                !double.IsFinite(r.NextWindUpdateMinute) || !double.IsFinite(r.FogFadeMinutes) ||
                !double.IsFinite(r.Wind.StartMinute) || !double.IsFinite(r.Wind.DurationMinutes) ||
                !float.IsFinite(r.Wind.Speed0) || !float.IsFinite(r.Wind.Speed1) || !float.IsFinite(r.Wind.Heading0) || !float.IsFinite(r.Wind.Heading1) ||
                !float.IsFinite(r.FogFrom.Enabled) || !float.IsFinite(r.FogFrom.Distance) || !Finite(r.FogFrom.Colour)) ||
            Regions.Select(r => r.RegionId).Distinct(StringComparer.Ordinal).Count() != Regions.Count)
            throw new InvalidDataException("Invalid regional weather frame.");
    }
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
