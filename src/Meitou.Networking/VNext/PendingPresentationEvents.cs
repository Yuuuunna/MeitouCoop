using Meitou.Simulation;
namespace Meitou.Networking.VNext;

/// <summary>Recipient-only event retention until a reliable critical batch has been admitted. Admitted source-ring entries stay visible for the source journal lifetime, while only unadmitted
/// entries extend beyond that lifetime. Overflow is explicit, never silent event loss.</summary>
public sealed class PendingPresentationEvents(WorldViewCodec codec)
{
    readonly SortedDictionary<ulong, FloaterEvent> floaters = [];
    readonly SortedDictionary<ulong, SoundEventEntry> sounds = [];
    readonly SortedDictionary<ulong, SpeechLogEvent> speech = [];
    readonly SortedDictionary<ulong, BloodVisualEvent> blood = [];
    readonly SortedDictionary<ulong, BlockSparkEvent> sparks = [];
    ulong admitted, highWater;
    public PresentationEventBatch Observe(PresentationEventBatch source)
    {
        highWater = Math.Max(highWater, source.HighWater);
        foreach (var value in source.Floaters) if (value.Id > admitted) floaters[value.Id] = value;
        foreach (var value in source.Sounds) if (value.Id > admitted) sounds[value.Id] = value;
        foreach (var value in source.Speech) if (value.Id > admitted) speech[value.Id] = value;
        foreach (var value in source.Blood) if (value.Id > admitted) blood[value.Id] = value;
        foreach (var value in source.Sparks) if (value.Id > admitted) sparks[value.Id] = value;
        if (floaters.Count + sounds.Count + speech.Count + blood.Count + sparks.Count > 4096) throw new IOException("Recipient reliable event retention key limit exceeded.");
        var result = new PresentationEventBatch(highWater, Merge(floaters, source.Floaters, value => value.Id))
        { Sounds = Merge(sounds, source.Sounds, value => value.Id), Speech = Merge(speech, source.Speech, value => value.Id),
            Blood = Merge(blood, source.Blood, value => value.Id), Sparks = Merge(sparks, source.Sparks, value => value.Id) };
        if (result.Floaters.Count + result.Sounds.Count + result.Speech.Count + result.Blood.Count + result.Sparks.Count > 4096) throw new IOException("Recipient reliable event projection key limit exceeded.");
        if (codec.EncodeComponent(result).Length > 256 * 1024) throw new IOException("Recipient reliable event retention byte limit exceeded.");
        return result;
    }
    static T[] Merge<T>(SortedDictionary<ulong, T> retained, IReadOnlyList<T> current, Func<T, ulong> identity) =>
        retained.Values.Concat(current).GroupBy(identity).OrderBy(group => group.Key).Select(group => group.Last()).ToArray();
    public void Admit(ulong watermark)
    {
        admitted = Math.Max(admitted, watermark);
        Remove(floaters); Remove(sounds); Remove(speech); Remove(blood); Remove(sparks);
    }
    void Remove<T>(SortedDictionary<ulong, T> values) { foreach (ulong id in values.Keys.TakeWhile(id => id <= admitted).ToArray()) values.Remove(id); }
    public void Reset() { floaters.Clear(); sounds.Clear(); speech.Clear(); blood.Clear(); sparks.Clear(); admitted = highWater = 0; }
}
