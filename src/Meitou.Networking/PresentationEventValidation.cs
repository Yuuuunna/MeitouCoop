using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Networking;

/// <summary>
/// Shared recipient event bounds and payload checks, with diagnostics that never include display text or positions.
/// </summary>
internal static class PresentationEventValidation
{
    /// <summary>
    /// A recipient may retain more events than the simulation's short-lived source journal.
    /// </summary>
    internal const int MaximumCount = 4096;

    /// <summary>
    /// Maximum encoded bytes held by a recipient's reliable event retention queue.
    /// </summary>
    internal const int MaximumRetainedBytes = 256 * 1024;

    /// <summary>
    /// Checks each family against one shared ID space and reports the first invalid event precisely.
    /// </summary>
    internal static void Validate(PresentationEventBatch? batch)
    {
        if (batch is null) throw new InvalidDataException("Invalid presentation event batch: reason=missing-batch.");
        if (batch.Floaters is null) throw Failure(batch, "missing-collection", "floater");
        if (batch.Sounds is null) throw Failure(batch, "missing-collection", "sound");
        if (batch.Speech is null) throw Failure(batch, "missing-collection", "speech");
        if (batch.Blood is null) throw Failure(batch, "missing-collection", "blood");
        if (batch.Sparks is null) throw Failure(batch, "missing-collection", "spark");

        long count = Count(batch);
        if (count > MaximumCount) throw Failure(batch, $"count-limit count={count} maximum={MaximumCount}");

        var identities = new HashSet<ulong>();
        ValidateEntries(batch, batch.Floaters, "floater", identities, value => value.Id, FloaterProblem);
        ValidateEntries(batch, batch.Sounds, "sound", identities, value => value.Id, SoundProblem);
        ValidateEntries(batch, batch.Speech, "speech", identities, value => value.Id, SpeechProblem);
        ValidateEntries(batch, batch.Blood, "blood", identities, value => value.Id, BloodProblem);
        ValidateEntries(batch, batch.Sparks, "spark", identities, value => value.Id, SparkProblem);
    }

    /// <summary>
    /// Counts all event families without overflowing on untrusted collection sizes.
    /// </summary>
    internal static long Count(PresentationEventBatch batch) =>
        (long)batch.Floaters.Count + batch.Sounds.Count + batch.Speech.Count + batch.Blood.Count + batch.Sparks.Count;

    /// <summary>
    /// Numeric batch context, including missing collections, safe for server error logs.
    /// </summary>
    internal static string Describe(PresentationEventBatch batch) =>
        $"highWater={batch.HighWater} floaters={batch.Floaters?.Count ?? -1} sounds={batch.Sounds?.Count ?? -1} " +
        $"speech={batch.Speech?.Count ?? -1} blood={batch.Blood?.Count ?? -1} sparks={batch.Sparks?.Count ?? -1}";

    /// <summary>
    /// Rejects null entries, out-of-range IDs, duplicates across families and invalid typed payloads.
    /// </summary>
    static void ValidateEntries<T>(PresentationEventBatch batch, IReadOnlyList<T> entries, string category,
        HashSet<ulong> identities, Func<T, ulong> identity, Func<T, string?> problem) where T : class
    {
        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null) throw Failure(batch, "null-entry", category, index);

            ulong id = identity(entry);
            if (id == 0) throw Failure(batch, "zero-id", category, index, id);
            if (id > batch.HighWater) throw Failure(batch, "id-above-high-water", category, index, id);
            if (!identities.Add(id)) throw Failure(batch, "duplicate-id", category, index, id);
            if (problem(entry) is { } reason) throw Failure(batch, reason, category, index, id);
        }
    }

    /// <summary>
    /// Floaters at fixed world positions include machine labels and building damage; other kinds require a character target.
    /// </summary>
    static string? FloaterProblem(FloaterEvent entry)
    {
        var value = entry.Value;
        if (!Enum.IsDefined(value.Kind)) return $"unknown-kind kind={(byte)value.Kind}";
        if (!Finite(value.Position)) return "non-finite-position";
        if (value.Part is null) return "missing-part";
        if (value.Part.Length > 4096) return $"part-length length={value.Part.Length} maximum=4096";
        if (value.Target.IsNone && value.Kind is not (FloaterKind.Label or FloaterKind.Building))
            return $"missing-target kind={value.Kind}";

        return null;
    }

    /// <summary>
    /// Checks positional sound payloads without disclosing item identities.
    /// </summary>
    static string? SoundProblem(SoundEventEntry entry)
    {
        var value = entry.Value;
        if (!Enum.IsDefined(value.Kind) || (byte)value.Kind > 31) return $"unknown-kind kind={(int)value.Kind}";
        if (!Finite(value.Position)) return "non-finite-position";
        if (value.Item?.Length > 4096) return $"item-length length={value.Item.Length} maximum=4096";
        if (value.Armour is < 0 or > 3) return "invalid-armour-material";
        if (value.ShootSound is < 0 or > 3) return "invalid-turret-shoot-sound";
        bool posted = value.Kind is SoundKind.Posted or SoundKind.PostedInterface;
        if (posted != (value.Posted is not null)) return "invalid-posted-sound-kind";
        if (value.Posted is { } sound)
        {
            if (string.IsNullOrEmpty(sound.Event) || !SoundNameFits(sound.Event)) return "invalid-sound-event-name";
            if (sound.Volume is not (>= 0 and <= 4)) return "invalid-sound-volume";
            if (sound.FadeSeconds is not (>= 0 and <= SoundHandle.MaxFadeSeconds) || !sound.Stop && sound.FadeSeconds != 0)
                return "invalid-sound-fade";
            if (sound.Stop && (sound.PlaybackId == 0 || value.Kind != SoundKind.PostedInterface || !value.Subject.IsNone
                || !value.Other.IsNone || value.Position != Vector3.Zero || sound.Event != "stop" || sound.ModId is not null
                || sound.SwitchGroup is not null || sound.SwitchValue is not null)) return "invalid-sound-stop";
            if (sound.ModId is { } mod && (string.IsNullOrWhiteSpace(mod) || mod.Length > 256 || !PostedSound.ValidRelativeFile(sound.Event)))
                return "invalid-mod-sound-path";
            if (sound.Loose && sound.ModId is null) return "absolute-shared-sound-path";
            if ((sound.SwitchGroup is null) != (sound.SwitchValue is null)) return "incomplete-sound-switch";
            if (sound.SwitchGroup is { } group && (group.Length == 0 || !SoundNameFits(group))) return "invalid-sound-switch-group";
            if (sound.SwitchValue is { } item && (item.Length == 0 || !SoundNameFits(item))) return "invalid-sound-switch-value";
        }

        return null;
    }

    /// <summary>
    /// Named sound events and switches use a bounded UTF-8 payload.
    /// </summary>
    static bool SoundNameFits(string value) => System.Text.Encoding.UTF8.GetByteCount(value) <= 4096;

    /// <summary>
    /// Checks speaker and text bounds while keeping names and dialogue out of diagnostics.
    /// </summary>
    static string? SpeechProblem(SpeechLogEvent entry)
    {
        if (entry.Speaker.IsNone) return "missing-speaker";
        if (entry.Line.Owner is null) return "missing-owner";
        if (entry.Line.Text is null) return "missing-text";
        if (entry.Line.Owner.Length > 256) return $"owner-length length={entry.Line.Owner.Length} maximum=256";
        if (entry.Line.Text.Length > 4096) return $"text-length length={entry.Line.Text.Length} maximum=4096";

        return null;
    }

    /// <summary>
    /// Checks blood timing, geometry and optional attacker identity independently of delivery lanes.
    /// The server's recipient projection enforces attacker visibility.
    /// </summary>
    static string? BloodProblem(BloodVisualEvent entry)
    {
        var value = entry.Value;
        var mark = value.Mark;
        if (value.Tick < 0) return "negative-tick";
        if (value.CorpseTicks < 0) return "negative-corpse-ticks";
        if (!float.IsFinite(value.TickSeconds) || value.TickSeconds is <= 0 or > 1) return "invalid-tick-seconds";
        if (!Enum.IsDefined(mark.Kind)) return $"unknown-kind kind={(int)mark.Kind}";
        if (!Finite(mark.Position)) return "non-finite-position";
        if (!float.IsFinite(mark.Direction.X) || !float.IsFinite(mark.Direction.Y)) return "non-finite-direction";
        if (!float.IsFinite(mark.Amount) || mark.Amount < 0) return "invalid-amount";
        if (mark.Race?.Length > 4096) return $"race-length length={mark.Race.Length} maximum=4096";
        if (mark.Attacker is { } attacker && (mark.Kind != BloodMarkKind.Splash || attacker.IsNone || attacker.Generation < 0))
            return "invalid-attacker";

        return null;
    }

    /// <summary>
    /// Block sparks always require a character subject.
    /// </summary>
    static string? SparkProblem(BlockSparkEvent entry) => entry.Who.IsNone ? "missing-subject" : null;

    /// <summary>
    /// Creates a bounded, payload-free diagnostic shared by every event family.
    /// </summary>
    static InvalidDataException Failure(PresentationEventBatch batch, string reason, string category = "batch", int index = -1, ulong id = 0) =>
        new($"Invalid presentation event batch: reason={reason} category={category} index={index} id={id} {Describe(batch)}.");

    /// <summary>
    /// World-space coordinates must be finite on all three axes.
    /// </summary>
    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
