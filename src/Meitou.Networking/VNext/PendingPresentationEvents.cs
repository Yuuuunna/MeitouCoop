using Meitou.Simulation;

namespace Meitou.Networking.VNext;

/// <summary>
/// Recipient-only event retention until reliable admission. Admitted entries stay visible for the source journal lifetime;
/// only unadmitted entries extend beyond it. Overflow is explicit, never silent event loss.
/// </summary>
public sealed class PendingPresentationEvents(WorldViewCodec codec)
{
    /// <summary>
    /// Unadmitted floating text and damage, ordered by event identity.
    /// </summary>
    readonly SortedDictionary<ulong, FloaterEvent> _floaters = [];

    /// <summary>
    /// Unadmitted sound cues.
    /// </summary>
    readonly SortedDictionary<ulong, SoundEventEntry> _sounds = [];

    /// <summary>
    /// Unadmitted speech lines.
    /// </summary>
    readonly SortedDictionary<ulong, SpeechLogEvent> _speech = [];

    /// <summary>
    /// Unadmitted blood effects.
    /// </summary>
    readonly SortedDictionary<ulong, BloodVisualEvent> _blood = [];

    /// <summary>
    /// Unadmitted block sparks.
    /// </summary>
    readonly SortedDictionary<ulong, BlockSparkEvent> _sparks = [];

    /// <summary>
    /// Highest event ID admitted to reliable delivery.
    /// </summary>
    ulong _admitted;

    /// <summary>
    /// Highest source journal watermark observed, including expired events.
    /// </summary>
    ulong _highWater;

    /// <summary>
    /// Retains new events and projects current source entries with any older unadmitted entries.
    /// </summary>
    public PresentationEventBatch Observe(PresentationEventBatch source)
    {
        _highWater = Math.Max(_highWater, source.HighWater);
        Retain(source);

        long retained = (long)_floaters.Count + _sounds.Count + _speech.Count + _blood.Count + _sparks.Count;
        if (retained > PresentationEventValidation.MaximumCount)
        {
            string counts = $"floaters={_floaters.Count} sounds={_sounds.Count} speech={_speech.Count} blood={_blood.Count} sparks={_sparks.Count}";
            throw new IOException($"Recipient reliable event retention key limit exceeded: count={retained} " +
                $"maximum={PresentationEventValidation.MaximumCount} admitted={_admitted} highWater={_highWater} {counts}.");
        }

        var result = Project(source);
        CheckProjection(result);

        return result;
    }

    /// <summary>
    /// Copies only IDs that have not crossed the reliable admission watermark.
    /// </summary>
    void Retain(PresentationEventBatch source)
    {
        foreach (var value in source.Floaters)
            if (value.Id > _admitted) _floaters[value.Id] = value;

        foreach (var value in source.Sounds)
            if (value.Id > _admitted) _sounds[value.Id] = value;

        foreach (var value in source.Speech)
            if (value.Id > _admitted) _speech[value.Id] = value;

        foreach (var value in source.Blood)
            if (value.Id > _admitted) _blood[value.Id] = value;

        foreach (var value in source.Sparks)
            if (value.Id > _admitted) _sparks[value.Id] = value;
    }

    /// <summary>
    /// Current source entries take precedence when their retained ID is still visible.
    /// </summary>
    PresentationEventBatch Project(PresentationEventBatch source) =>
        new(_highWater, Merge(_floaters, source.Floaters, value => value.Id))
        {
            Sounds = Merge(_sounds, source.Sounds, value => value.Id),
            Speech = Merge(_speech, source.Speech, value => value.Id),
            Blood = Merge(_blood, source.Blood, value => value.Id),
            Sparks = Merge(_sparks, source.Sparks, value => value.Id)
        };

    /// <summary>
    /// Mirrors the wire's count limit and reports exact retention byte usage when a recipient falls behind.
    /// </summary>
    void CheckProjection(PresentationEventBatch result)
    {
        long count = PresentationEventValidation.Count(result);
        if (count > PresentationEventValidation.MaximumCount)
            throw new IOException($"Recipient reliable event projection key limit exceeded: count={count} " +
                $"maximum={PresentationEventValidation.MaximumCount} admitted={_admitted} {PresentationEventValidation.Describe(result)}.");

        int bytes = codec.EncodeComponent(result).Length;
        if (bytes > PresentationEventValidation.MaximumRetainedBytes)
            throw new IOException($"Recipient reliable event retention byte limit exceeded: bytes={bytes} " +
                $"maximum={PresentationEventValidation.MaximumRetainedBytes} admitted={_admitted} {PresentationEventValidation.Describe(result)}.");
    }

    /// <summary>
    /// Deduplicates each family while preserving monotonic delivery order.
    /// </summary>
    static T[] Merge<T>(SortedDictionary<ulong, T> retained, IReadOnlyList<T> current, Func<T, ulong> identity) => retained.Values
        .Concat(current)
        .GroupBy(identity)
        .OrderBy(group => group.Key)
        .Select(group => group.Last())
        .ToArray();

    /// <summary>
    /// Releases retained entries through a watermark only after reliable admission or explicit consumption.
    /// </summary>
    public void Admit(ulong watermark)
    {
        _admitted = Math.Max(_admitted, watermark);
        Remove(_floaters);
        Remove(_sounds);
        Remove(_speech);
        Remove(_blood);
        Remove(_sparks);
    }

    /// <summary>
    /// Removes the admitted prefix from one ordered family.
    /// </summary>
    void Remove<T>(SortedDictionary<ulong, T> values)
    {
        foreach (ulong id in values.Keys.TakeWhile(id => id <= _admitted).ToArray())
            values.Remove(id);
    }

    /// <summary>
    /// Drops recipient state when its authenticated view is fenced or replaced.
    /// </summary>
    public void Reset()
    {
        _floaters.Clear();
        _sounds.Clear();
        _speech.Clear();
        _blood.Clear();
        _sparks.Clear();
        _admitted = 0;
        _highWater = 0;
    }
}
