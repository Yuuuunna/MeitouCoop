using System.Numerics;
using Meitou.Data;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Simulation;
using WeatherEffectEntry = Meitou.Data.Particles.WeatherEffectEntry;
using Meitou.Content;

namespace Meitou.Runtime;

/// <summary>World-wide weather and affecting effect nodes, advanced on simulation ticks without renderer callbacks.</summary>
public sealed partial class SimulationWeather : IRuntimeWorldService, IWeatherSource, IWeatherPresentationSource
{
    public static SimulationWeather Load(GameInstall install, GameDatabase db, Func<float, float, float> ground, int seed = 1) =>
        new(new WeatherSchedule(WeatherWorld.Create(db, install, seed, WeatherTime.At(0))), db,
            ParticleLibrary.Load(install), ground, MapEffectPlacers.Find(db, MapFeatureFile.Open(install)), seed);
    readonly GameDatabase database;
    readonly ParticleLibrary particles;
    readonly Func<float, float, float> ground;
    readonly List<WeatherAffectZone> zones = [];
    readonly Dictionary<int, RegionGroups> regional = [];
    readonly List<EffectGroup> placed;
    readonly MapEffectPlacement[] placements;
    WeakReference<World>? owner;
    bool initialized;
    WeatherEffectFrame? capturedEffects;
    readonly Dictionary<int, RegionArea> areas = [];
    readonly Dictionary<int, Vector3> centres = [];
    readonly Dictionary<string, IReadOnlyList<WeatherEffectEntry>> entries = new(StringComparer.Ordinal);
    readonly int seed;

    sealed record RegionGroups(int Serial, List<EffectGroup> Groups);

    public SimulationWeather(WeatherSchedule schedule, GameDatabase database, ParticleLibrary particles,
        Func<float, float, float> ground, IEnumerable<MapEffectPlacement> placements, int seed = 1)
    {
        Schedule = schedule;
        this.database = database;
        this.particles = particles;
        this.ground = ground;
        this.seed = seed;
        this.placements = placements.ToArray();
        var cells = new Dictionary<int, List<(float, float, float, float)>>();
        for (int z = 0; z < WeatherAreas.Cells; z++)
            for (int x = 0; x < WeatherAreas.Cells; x++)
            {
                var region = schedule.World.RegionOf(schedule.World.Areas.RegionOf(schedule.World.Data, x, z));
                if (!cells.TryGetValue(region.Index, out var list)) cells.Add(region.Index, list = []);
                list.Add(WeatherAreas.CellRect(x, z));
            }
        foreach (var (index, rects) in cells)
        {
            areas.Add(index, new RegionArea(rects));
            var first = rects[0];
            centres.Add(index, new Vector3((first.Item1 + first.Item3) / 2, 0, (first.Item2 + first.Item4) / 2));
        }
        placed = CreatePlaced();
    }

    List<EffectGroup> CreatePlaced() => [.. placements.Where(p => Affects(p.Effect))
            .GroupBy(p => Schedule.World.RegionAt(p.Position.X, p.Position.Z).Index).OrderBy(g => g.Key)
            .SelectMany(g => MapEffectPlacers.Groups(g, particles, EffectWorld(null), unchecked(seed + g.Key * 7919)))];

    public WeatherSchedule Schedule { get; private set; }
    public IReadOnlyList<WeatherAffectZone> Zones => zones;
    public WeatherHere? Forced { get; set; }
    public bool HasWorldOwner => owner is not null;
    public WeatherFrame CaptureWeather() => new(Schedule.Time, Schedule.World.Snapshot());
    public SimulationWeather CreateSibling() => new(new WeatherSchedule(new WeatherWorld(Schedule.World.Data, Schedule.World.Areas,
        Schedule.World.Seed, WeatherTime.At(0))), database, particles, ground, placements, seed);

    void Claim(World world)
    {
        if (owner is not null && (!owner.TryGetTarget(out var existing) || !ReferenceEquals(existing, world)))
            throw new InvalidOperationException("A replacement world requires an independent weather instance.");
        owner ??= new(world);
        world.Weather = this;
    }

    public void Synchronize(World world)
    {
        if (world.HasPendingDurableMutation) return;
        Claim(world);
        var now = WeatherTime.FromHours(world.GameHours);
        if (now != Schedule.Time) Advance(now, 0);
        else if (!initialized) RefreshEffects(0);
    }

    public void Suspend(World world)
    {
        if (ReferenceEquals(world.Weather, this)) world.Weather = null;
    }

    public void Reset()
    {
        var world = Schedule.World;
        Schedule = new WeatherSchedule(new WeatherWorld(world.Data, world.Areas, world.Seed, WeatherTime.At(0)));
        regional.Clear();
        zones.Clear();
        capturedEffects = null;
        placed.Clear(); placed.AddRange(CreatePlaced());
        initialized = false;
        Forced = null;
    }

    public WeatherHere? At(float x, float z)
    {
        if (Forced is { } forced) return forced;
        var r = Schedule.World.RegionAt(x, z);
        return new WeatherHere(r.Weather.AffectType, r.Strength, r.Def.AcidicGround, r.Def.AcidicWater, r.WindSpeed, r.Weather.Wetness);
    }

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        Claim(world);
        Advance(WeatherTime.FromHours(world.GameHours), world.TickSeconds);
    }

    public void Advance(WeatherTime now, float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (now.Minutes < Schedule.Time.Minutes)
        {
            var forced = Forced;
            Reset(); Forced = forced;
        }
        Schedule.Advance(now);
        RefreshEffects(seconds);
    }

    void RefreshEffects(float seconds)
    {
        capturedEffects = null;
        initialized = true;
        zones.Clear();
        foreach (var region in Schedule.World.Regions)
        {
            if (!areas.TryGetValue(region.Index, out var area)) continue;
            if (!regional.TryGetValue(region.Index, out var state) || state.Serial != region.Serial)
            {
                regional[region.Index] = state = new RegionGroups(region.Serial, CreateRegional(region, area));
            }
            var input = new WeatherEffectInput { Strength = region.Strength, Wind = region.WindDirection * region.WindSpeed };
            var camera = new EffectCamera(centres[region.Index], -Vector3.UnitZ);
            foreach (var group in state.Groups) Update(group, seconds, camera, input);
        }
        // Fixed map effects have their own position and read their local region's wind.
        foreach (var group in placed)
        {
            var position = group.Units.Count > 0 ? group.Units[0].Position : Vector3.Zero;
            var r = Schedule.World.RegionAt(position.X, position.Z);
            Update(group, seconds, new EffectCamera(position, -Vector3.UnitZ),
                new WeatherEffectInput { Strength = r.Strength, Wind = r.WindDirection * r.WindSpeed });
        }
    }

    public void Restore(IEnumerable<WeatherRegionSnapshot> regions, WeatherTime now)
    {
        capturedEffects = null;
        Schedule.Restore(regions, now);
        regional.Clear();
        zones.Clear();
        initialized = false;
    }

    internal void RestoreCandidate(WeatherFrame frame)
    {
        if (HasWorldOwner) throw new InvalidOperationException("Candidate weather must not belong to an existing world.");
        frame.Validate();
        if (!frame.Enabled) throw new InvalidDataException("A saved simulation weather frame must be enabled.");
        Schedule.AcceptPresentation(frame); // validate exact content before applying the save's random-source reset
        Schedule.Restore(frame.Regions, frame.Time);
        regional.Clear(); zones.Clear(); initialized = false; capturedEffects = null;
    }

    IReadOnlyList<WeatherEffectEntry> Effects(WeatherDef weather)
    {
        if (entries.TryGetValue(weather.StringId, out var found)) return found;
        var list = new List<WeatherEffectEntry>();
        foreach (var e in weather.Effects)
            if (database.Find(e.Effect.StringId) is { } record)
                list.Add(new WeatherEffectEntry(EffectRecord.From(record, database), e.MaxCount, (int)e.RespawnMin, (int)e.RespawnMax));
        return entries[weather.StringId] = list;
    }

    static bool Affects(EffectRecord effect) => effect.AffectType != WeatherAffecting.None && effect.EffectRadius > 0;
    EffectWorld EffectWorld(IEffectArea? area) => new() { Persistent = true, GroundHeight = ground, Area = area, ActiveRadius = float.PositiveInfinity };

    List<EffectGroup> CreateRegional(WeatherRegion region, IEffectArea area)
    {
        var groups = new List<EffectGroup>();
        int number = 0;
        foreach (var entry in Effects(region.Weather))
            if (Affects(entry.Effect) && !entry.Effect.IsCameraGroup &&
                EffectGroups.Create(entry, particles, unchecked(seed + region.Index * 7919 + region.Serial * 31 + number++), EffectWorld(area)) is { } group)
                groups.Add(group);
        return groups;
    }

    void Update(EffectGroup group, float seconds, EffectCamera camera, WeatherEffectInput weather)
    {
        group.Update(seconds, camera, weather);
        group.Simulate();
        foreach (var unit in group.Units)
            if (!unit.Stopped) zones.Add(new WeatherAffectZone(unit.Position, group.Effect.EffectRadius, group.Effect.AffectType, group.Effect.EffectStrength));
    }
}
