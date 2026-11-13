namespace Meitou.Data.World;

/// <summary>Camera-independent minute-by-minute weather advancement, shared by rendering and simulation hosts.</summary>
public sealed class WeatherSchedule(WeatherWorld world)
{
    public WeatherWorld World { get; } = world;
    public WeatherTime Time { get; private set; } = world.Time;

    public void Advance(WeatherTime now)
    {
        if (!double.IsFinite(now.Minutes)) throw new ArgumentOutOfRangeException(nameof(now));
        // Keeping every intervening minute preserves season/choice/wind order across long host stalls.
        for (double minute = Math.Floor(Time.Minutes) + 1; minute <= now.Minutes; minute++)
            foreach (var region in World.Regions) region.Update(WeatherTime.FromHours(minute / 60));
        foreach (var region in World.Regions) region.Update(now);
        Time = now;
    }

    public void Restore(IEnumerable<WeatherRegionSnapshot> regions, WeatherTime now)
    {
        if (!double.IsFinite(now.Minutes)) throw new ArgumentOutOfRangeException(nameof(now));

        var snapshots = regions.ToArray();
        var saved = snapshots.Select(region => region.RegionId).ToHashSet(StringComparer.Ordinal);
        var replayed = World.Regions.Where(region => !saved.Contains(region.Def.StringId)).ToArray();

        // Saved regions replace their complete schedules below; only missing regions need their intervening random draws.
        for (double minute = Math.Floor(Time.Minutes) + 1; replayed.Length > 0 && minute <= now.Minutes; minute++)
            foreach (var region in replayed) region.Update(WeatherTime.FromHours(minute / 60));
        foreach (var region in replayed) region.Update(now);

        World.Restore(snapshots, now);
        Time = now;
    }

    public void AcceptPresentation(WeatherFrame frame)
    {
        World.AcceptPresentation(frame);
        Time = frame.Time;
    }
}
