using System.Numerics;
using Meitou.Data;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Simulation;
using WeatherEffectEntry = Meitou.Data.Particles.WeatherEffectEntry;
using Meitou.Content;

namespace Meitou.Runtime;

/// <summary>
/// World-wide weather and affecting effect nodes, advanced on simulation ticks without renderer callbacks.
/// </summary>
public sealed partial class SimulationWeather : IRuntimeWorldService, IWeatherSource, IWeatherPresentationSource
{
    readonly GameDatabase _database;
    readonly ParticleLibrary _particles;
    readonly Func<float, float, float> _ground;
    readonly List<WeatherAffectZone> _zones = [];
    readonly Dictionary<int, RegionGroups> _regional = [];
    readonly List<EffectGroup> _placed;
    readonly MapEffectPlacement[] _placements;
    WeakReference<World>? _owner;
    bool _initialized;
    WeatherEffectFrame? _capturedEffects;
    readonly Dictionary<int, RegionArea> _areas = [];
    readonly Dictionary<int, Vector3> _centres = [];
    readonly Dictionary<string, IReadOnlyList<WeatherEffectEntry>> _entries = new(StringComparer.Ordinal);
    readonly int _seed;
    public SimulationWeather(WeatherSchedule schedule, GameDatabase database, ParticleLibrary particles, Func<float, float, float> ground, IEnumerable<MapEffectPlacement> placements, int seed = 1)
    {
        Schedule = schedule;
        this._database = database;
        this._particles = particles;
        this._ground = ground;
        this._seed = seed;
        this._placements = placements.ToArray();
        var cells = new Dictionary<int, List<(float, float, float, float)>>();
        for (int z = 0; z < WeatherAreas.Cells; z++)
            for (int x = 0; x < WeatherAreas.Cells; x++)
            {
                var region = schedule.World.RegionOf(schedule.World.Areas.RegionOf(schedule.World.Data, x, z));
                if (!cells.TryGetValue(region.Index, out var list))
                    cells.Add(region.Index, list = []);
                list.Add(WeatherAreas.CellRect(x, z));
            }

        foreach (var(index, rects)in cells)
        {
            _areas.Add(index, new RegionArea(rects));
            var first = rects[0];
            _centres.Add(index, new Vector3((first.Item1 + first.Item3) / 2, 0, (first.Item2 + first.Item4) / 2));
        }

        _placed = CreatePlaced();
    }

    public static SimulationWeather Load(GameInstall install, GameDatabase db, Func<float, float, float> ground, int seed = 1) => new(new WeatherSchedule(WeatherWorld.Create(db, install, seed, WeatherTime.At(0))), db, ParticleLibrary.Load(install), ground, MapEffectPlacers.Find(db, MapFeatureFile.Open(install)), seed);
    sealed record RegionGroups(int Serial, List<EffectGroup> Groups);
    List<EffectGroup> CreatePlaced() => [.._placements.Where(p => Affects(p.Effect)).GroupBy(p => Schedule.World.RegionAt(p.Position.X, p.Position.Z).Index).OrderBy(g => g.Key).SelectMany(g => MapEffectPlacers.Groups(g, _particles, EffectWorld(null), unchecked(_seed + g.Key * 7919)))];
    public WeatherSchedule Schedule { get; private set; }
    public IReadOnlyList<WeatherAffectZone> Zones => _zones;
    public WeatherHere? Forced { get; set; }
    public bool HasWorldOwner => _owner is not null;

    public WeatherFrame CaptureWeather() => new(Schedule.Time, Schedule.World.Snapshot());
    public SimulationWeather CreateSibling() => new(new WeatherSchedule(new WeatherWorld(Schedule.World.Data, Schedule.World.Areas, Schedule.World.Seed, WeatherTime.At(0))), _database, _particles, _ground, _placements, _seed);
    void Claim(World world)
    {
        if (_owner is not null && (!_owner.TryGetTarget(out var existing) || !ReferenceEquals(existing, world)))
            throw new InvalidOperationException("A replacement world requires an independent weather instance.");
        _owner ??= new(world);
        world.Weather = this;
    }

    public void Synchronize(World world)
    {
        if (world.HasPendingDurableMutation)
            return;
        Claim(world);
        var now = WeatherTime.FromHours(world.GameHours);
        if (now != Schedule.Time)
            Advance(now, 0);
        else if (!_initialized)
            RefreshEffects(0);
    }

    public void Suspend(World world)
    {
        if (ReferenceEquals(world.Weather, this))
            world.Weather = null;
    }

    public void Reset()
    {
        var world = Schedule.World;
        Schedule = new WeatherSchedule(new WeatherWorld(world.Data, world.Areas, world.Seed, WeatherTime.At(0)));
        _regional.Clear();
        _zones.Clear();
        _capturedEffects = null;
        _placed.Clear();
        _placed.AddRange(CreatePlaced());
        _initialized = false;
        Forced = null;
    }

    public WeatherHere? At(float x, float z)
    {
        if (Forced is { } forced)
            return forced;
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
        if (!float.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        if (now.Minutes < Schedule.Time.Minutes)
        {
            var forced = Forced;
            Reset();
            Forced = forced;
        }

        Schedule.Advance(now);
        RefreshEffects(seconds);
    }

    void RefreshEffects(float seconds)
    {
        _capturedEffects = null;
        _initialized = true;
        _zones.Clear();
        foreach (var region in Schedule.World.Regions)
        {
            if (!_areas.TryGetValue(region.Index, out var area))
                continue;
            if (!_regional.TryGetValue(region.Index, out var state) || state.Serial != region.Serial)
            {
                _regional[region.Index] = state = new RegionGroups(region.Serial, CreateRegional(region, area));
            }

            var input = new WeatherEffectInput
            {
                Strength = region.Strength,
                Wind = region.WindDirection * region.WindSpeed
            };
            var camera = new EffectCamera(_centres[region.Index], -Vector3.UnitZ);
            foreach (var group in state.Groups)
                Update(group, seconds, camera, input);
        }

        // Fixed map effects have their own position and read their local region's wind.
        foreach (var group in _placed)
        {
            var position = group.Units.Count > 0 ? group.Units[0].Position : Vector3.Zero;
            var r = Schedule.World.RegionAt(position.X, position.Z);
            Update(group, seconds, new EffectCamera(position, -Vector3.UnitZ), new WeatherEffectInput { Strength = r.Strength, Wind = r.WindDirection * r.WindSpeed });
        }
    }

    public void Restore(IEnumerable<WeatherRegionSnapshot> regions, WeatherTime now)
    {
        _capturedEffects = null;
        Schedule.Restore(regions, now);
        _regional.Clear();
        _zones.Clear();
        _initialized = false;
    }

    internal void RestoreCandidate(WeatherFrame frame)
    {
        if (HasWorldOwner)
            throw new InvalidOperationException("Candidate weather must not belong to an existing world.");
        frame.Validate();
        if (!frame.Enabled)
            throw new InvalidDataException("A saved simulation weather frame must be enabled.");
        Schedule.AcceptPresentation(frame); // validate exact content before applying the save's random-source reset
        Schedule.Restore(frame.Regions, frame.Time);
        _regional.Clear();
        _zones.Clear();
        _initialized = false;
        _capturedEffects = null;
    }

    IReadOnlyList<WeatherEffectEntry> Effects(WeatherDef weather)
    {
        if (_entries.TryGetValue(weather.StringId, out var found))
            return found;
        var list = new List<WeatherEffectEntry>();
        foreach (var e in weather.Effects)
            if (_database.Find(e.Effect.StringId)is { } record)
                list.Add(new WeatherEffectEntry(EffectRecord.From(record, _database), e.MaxCount, (int)e.RespawnMin, (int)e.RespawnMax));
        return _entries[weather.StringId] = list;
    }

    static bool Affects(EffectRecord effect) => effect.AffectType != WeatherAffecting.None && effect.EffectRadius > 0;
    EffectWorld EffectWorld(IEffectArea? area) => new()
    {
        Persistent = true,
        GroundHeight = _ground,
        Area = area,
        ActiveRadius = float.PositiveInfinity
    };
    List<EffectGroup> CreateRegional(WeatherRegion region, IEffectArea area)
    {
        var groups = new List<EffectGroup>();
        int number = 0;
        foreach (var entry in Effects(region.Weather))
            if (Affects(entry.Effect) && !entry.Effect.IsCameraGroup && EffectGroups.Create(entry, _particles, unchecked(_seed + region.Index * 7919 + region.Serial * 31 + number++), EffectWorld(area))is { } group)
                groups.Add(group);
        return groups;
    }

    void Update(EffectGroup group, float seconds, EffectCamera camera, WeatherEffectInput weather)
    {
        group.Update(seconds, camera, weather);
        group.Simulate();
        foreach (var unit in group.Units)
            if (!unit.Stopped)
                _zones.Add(new WeatherAffectZone(unit.Position, group.Effect.EffectRadius, group.Effect.AffectType, group.Effect.EffectStrength));
    }
}