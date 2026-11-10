using System.Globalization;
using Meitou.Modding;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Saving;

namespace Meitou.Runtime;

public readonly record struct CheckpointPoint(long Tick, ulong Revision, double GameHours, bool Daytime);

/// <summary>Opaque detached payload. Only its originating coordinator may commit it.</summary>
public sealed class CapturedCheckpoint
{
    internal CapturedCheckpoint(WorldCheckpointCoordinator producer, long sequence, CheckpointPoint point, SaveGame world, byte[] metadata, SimulationWeatherState? weather)
        => (Producer, Sequence, Point, World, Metadata, Weather) = (producer, sequence, point, world, metadata, weather);
    internal WorldCheckpointCoordinator Producer { get; }
    internal SaveGame World { get; }
    internal byte[] Metadata { get; }
    internal SimulationWeatherState? Weather { get; }
    public long Sequence { get; }
    public CheckpointPoint Point { get; }
}

/// <summary>CleanupFailure is reported separately when the new world loaded/activated successfully but old-world disposal failed.</summary>
public sealed record CheckpointRestoreResult(WorldCheckpointCoordinator Coordinator, Exception? CleanupFailure);

/// <summary>
/// Capture/Restore run on the serialized world owner. Commit may run on a worker: its payload is detached and world stepping can
/// continue. Replacement/disposal and producer handoff wait for a committing checkpoint; stale captures cannot overwrite newer ones.
/// </summary>
public sealed class WorldCheckpointCoordinator
{
    const string FormatKey = "meitou runtime checkpoint", HoursKey = "meitou precise game hours",
        TickKey = "meitou checkpoint tick", RevisionKey = "meitou checkpoint revision", DaytimeKey = "meitou checkpoint daytime";
    readonly WorldRuntime runtime;
    readonly World source;
    readonly PopulationData data;
    readonly CheckpointStore store;
    readonly SaveCaptureOptions options;
    readonly long owner;
    readonly ModHost? mods;
    long nextCapture, committed;
    Guid committedId;

    public WorldCheckpointCoordinator(WorldRuntime runtime, PopulationData data, CheckpointStore store, SaveCaptureOptions options, LoadedSave? loaded = null, ModHost? mods = null)
    {
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(options);
        this.runtime = runtime; this.data = data; this.store = store; this.options = options;
        source = runtime.World;
        if (source.Systems.OfType<PopulationSystem>().Any(p => !ReferenceEquals(p.Data, data)))
            throw new ArgumentException("The checkpoint catalogue must belong to the active world.", nameof(data));
        Loaded = loaded;
        this.mods = mods;
        owner = runtime.ClaimCheckpointOwner(source);
    }

    public LoadedSave? Loaded { get; }
    internal World Source => source;
    internal PopulationData Population => data;
    internal WorldRuntime Runtime => runtime;
    internal void VerifyActive() => runtime.VerifyCheckpointOwner(source, owner);
    /// <summary>Validates the active runtime before a server service uses this coordinator for critical persistence.</summary>
    public void VerifyOwner(WorldRuntime candidate)
    {
        if (!ReferenceEquals(runtime, candidate)) throw new ArgumentException("Checkpoint coordinator belongs to another runtime.", nameof(candidate));
        VerifyActive();
    }
    internal World.DurableMutation BeginDurableMutation() => runtime.WithCheckpointOwner(source, owner, source.BeginDurableMutation);

    public CapturedCheckpoint Capture() => CaptureCore(null);
    internal CapturedCheckpoint CaptureDurable(World.DurableMutation token) => CaptureCore(token);

    CapturedCheckpoint CaptureCore(World.DurableMutation? token)
    {
        runtime.VerifyCheckpointOwner(source, owner);
        if (!source.AtTickBoundary) throw new InvalidOperationException("Checkpoint capture requires a world boundary.");
        if (token is null) runtime.Synchronize();
        else source.VerifyDurableMutation(token);
        source.ReturnAllHeldItems();
        var point = Point(source);
        var clock = Clock(point.GameHours);
        var save = SaveCapture.Capture(source, data, clock, Loaded, options);
        // Make record/item/portrait ownership independent of the running world before handing work to a writer.
        mods?.CaptureSaveData(source, save.ModData, strict: true);
        save = save.Clone();
        var weather = WeatherCheckpoint.Capture(source, data, save);
        if (options.Levels is null)
            foreach (var zone in source.VisitedZones)
                if (!save.Zones.ContainsKey(zone)) throw new InvalidDataException("A visited region has no saved zone; provide its level data when capturing.");
        var metadata = PlayerRegistrySave.Capture(source, data);
        runtime.VerifyCheckpointOwner(source, owner);
        if (Point(source) != point || !source.AtTickBoundary)
            throw new InvalidOperationException("The world changed during checkpoint capture.");
        save.Camera.Record.Ints[FormatKey] = 1;
        save.Camera.Record.Strings[HoursKey] = point.GameHours.ToString("R", CultureInfo.InvariantCulture);
        save.Camera.Record.Strings[TickKey] = point.Tick.ToString(CultureInfo.InvariantCulture);
        save.Camera.Record.Strings[RevisionKey] = point.Revision.ToString(CultureInfo.InvariantCulture);
        save.Camera.Record.Bools[DaytimeKey] = point.Daytime;
        return new CapturedCheckpoint(this, checked(++nextCapture), point, save, metadata, weather);
    }

    public Guid Commit(CapturedCheckpoint captured, Action<CheckpointStage>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if (!ReferenceEquals(captured.Producer, this)) throw new ArgumentException("Capture belongs to a different checkpoint producer.", nameof(captured));
        return runtime.WithCheckpointOwner(source, owner, () =>
        {
            if (captured.Sequence < committed) throw new InvalidOperationException("A newer capture has already committed.");
            if (captured.Sequence == committed) return committedId;
            if (captured.Weather is { } weather) WeatherCheckpoint.Encode(weather, captured.World);
            var id = store.Commit(captured.World, captured.Metadata, observer);
            committed = captured.Sequence;
            committedId = id;
            return id;
        });
    }

    /// <summary>Candidate data must be independent of the live catalogue; factory runs after dynamic faction reconstruction.</summary>
    public CheckpointRestoreResult Restore(StoredCheckpoint checkpoint, PopulationData candidateData,
        Func<PopulationData, IEnumerable<ITickSystem>> systems, SaveCaptureOptions candidateOptions,
        IAppearanceSource? appearances = null, bool deferPlayerRoles = false, Action<World>? configureWorld = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint); ArgumentNullException.ThrowIfNull(candidateData);
        ArgumentNullException.ThrowIfNull(systems); ArgumentNullException.ThrowIfNull(candidateOptions);
        if (ReferenceEquals(data, candidateData) || ReferenceEquals(data.Relations, candidateData.Relations))
            throw new ArgumentException("Restore needs an independent candidate catalogue.", nameof(candidateData));
        var save = checkpoint.World.Clone();
        var metadata = checkpoint.Metadata.ToArray();
        var point = ReadPoint(save);
        return mods is null ? RestoreCandidate() : mods.PrepareWorld(candidateData, RestoreCandidate);

        CheckpointRestoreResult RestoreCandidate() => runtime.WithCheckpointOwner(source, owner, () =>
        {
            PlayerRegistrySave.PrepareCatalogue(candidateData, metadata);
            LoadedSave? loaded = null;
            World? prepared = null;
            Exception? cleanup = null;
            try
            {
                runtime.ReplaceWorld(systems(candidateData), world =>
                {
                    prepared = world;
                    world.GameHours = point.GameHours;
                    world.Daytime = point.Daytime;
                    loaded = SaveLoader.Load(world, save, candidateData, new SaveLoadOptions { GameHoursOverride = point.GameHours, MultiplayerMetadata = metadata, Appearances = appearances, DeferPlayerRoles = deferPlayerRoles });
                    if (loaded.MissingMods.Count != 0) throw new InvalidDataException("Checkpoint content files are missing from the candidate.");
                    PlayerRegistrySave.Restore(world, candidateData, metadata);
                    WeatherCheckpoint.Restore(world, save, point.GameHours);
                    configureWorld?.Invoke(world);
                    mods?.RestoreSaveData(world, save.ModData, strict: true);
                });
            }
            catch (Exception e) when (prepared is not null && ReferenceEquals(runtime.World, prepared))
            {
                // Replacement already succeeded. Preserve access to the valid new world and report disposal failure to the host.
                cleanup = e;
            }
            return new CheckpointRestoreResult(new WorldCheckpointCoordinator(runtime, candidateData, store, candidateOptions, loaded, mods), cleanup);
        });
    }

    static CheckpointPoint Point(World world) => new(world.Tick, world.StateRevision, world.GameHours, world.Daytime);
    static SaveClock Clock(double hours)
    {
        if (!double.IsFinite(hours) || hours < 0 || hours / 24 >= int.MaxValue) throw new InvalidDataException("Invalid checkpoint game time.");
        long minutes = (long)Math.Floor(hours * 60);
        return new SaveClock((int)(minutes / 1440), (int)(minutes / 60 % 24), (int)(minutes % 60));
    }
    static CheckpointPoint ReadPoint(SaveGame save)
    {
        var record = save.Camera.Record;
        if (record.Ints.GetValueOrDefault(FormatKey) != 1 ||
            !double.TryParse(record.Strings.GetValueOrDefault(HoursKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double hours) ||
            !long.TryParse(record.Strings.GetValueOrDefault(TickKey), NumberStyles.None, CultureInfo.InvariantCulture, out long tick) ||
            !ulong.TryParse(record.Strings.GetValueOrDefault(RevisionKey), NumberStyles.None, CultureInfo.InvariantCulture, out ulong revision) ||
            !record.Bools.TryGetValue(DaytimeKey, out bool daytime)) throw new InvalidDataException("Missing or invalid runtime checkpoint timing.");
        var clock = Clock(hours);
        if (clock.Day != save.Camera.Day || clock.Hour != save.Camera.Hour || clock.Minute != save.Camera.Minute)
            throw new InvalidDataException("Checkpoint timing does not match the world file.");
        return new CheckpointPoint(tick, revision, hours, daytime);
    }
}
