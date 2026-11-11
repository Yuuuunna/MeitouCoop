using Meitou.Simulation;

namespace Meitou.Networking.VNext;

/// <summary>Recipient-only event retention until a reliable critical batch has been admitted. Admitted source-ring entries stay visible for the source journal lifetime, while only unadmitted
/// entries extend beyond that lifetime. Overflow is explicit, never silent event loss.</summary>
public sealed class PendingPresentationEvents(WorldViewCodec codec)
{
    readonly SortedDictionary<ulong, FloaterEvent> _floaters = [];
    readonly SortedDictionary<ulong, SoundEventEntry> _sounds = [];
    readonly SortedDictionary<ulong, SpeechLogEvent> _speech = [];
    readonly SortedDictionary<ulong, BloodVisualEvent> _blood = [];
    readonly SortedDictionary<ulong, BlockSparkEvent> _sparks = [];
    ulong _admitted, _highWater;
    public PresentationEventBatch Observe(PresentationEventBatch source)
    {
        _highWater = Math.Max(_highWater, source.HighWater);
        foreach (var value in source.Floaters)
            if (value.Id > _admitted)
                _floaters[value.Id] = value;
        foreach (var value in source.Sounds)
            if (value.Id > _admitted)
                _sounds[value.Id] = value;
        foreach (var value in source.Speech)
            if (value.Id > _admitted)
                _speech[value.Id] = value;
        foreach (var value in source.Blood)
            if (value.Id > _admitted)
                _blood[value.Id] = value;
        foreach (var value in source.Sparks)
            if (value.Id > _admitted)
                _sparks[value.Id] = value;
        if (_floaters.Count + _sounds.Count + _speech.Count + _blood.Count + _sparks.Count > 4096)
            throw new IOException("Recipient reliable event retention key limit exceeded.");
        var result = new PresentationEventBatch(_highWater, Merge(_floaters, source.Floaters, value => value.Id))
        {
            Sounds = Merge(_sounds, source.Sounds, value => value.Id),
            Speech = Merge(_speech, source.Speech, value => value.Id),
            Blood = Merge(_blood, source.Blood, value => value.Id),
            Sparks = Merge(_sparks, source.Sparks, value => value.Id)
        };
        if (result.Floaters.Count + result.Sounds.Count + result.Speech.Count + result.Blood.Count + result.Sparks.Count > 4096)
            throw new IOException("Recipient reliable event projection key limit exceeded.");
        if (codec.EncodeComponent(result).Length > 256 * 1024)
            throw new IOException("Recipient reliable event retention byte limit exceeded.");
        return result;
    }

    static T[] Merge<T>(SortedDictionary<ulong, T> retained, IReadOnlyList<T> current, Func<T, ulong> identity) => retained.Values.Concat(current).GroupBy(identity).OrderBy(group => group.Key).Select(group => group.Last()).ToArray();
    public void Admit(ulong watermark)
    {
        _admitted = Math.Max(_admitted, watermark);
        Remove(_floaters);
        Remove(_sounds);
        Remove(_speech);
        Remove(_blood);
        Remove(_sparks);
    }

    void Remove<T>(SortedDictionary<ulong, T> values)
    {
        foreach (ulong id in values.Keys.TakeWhile(id => id <= _admitted).ToArray())
            values.Remove(id);
    }

    public void Reset()
    {
        _floaters.Clear();
        _sounds.Clear();
        _speech.Clear();
        _blood.Clear();
        _sparks.Clear();
        _admitted = _highWater = 0;
    }
}