using Meitou.Simulation.Bodies;

namespace Meitou.Networking.VNext;

/// <summary>
/// One visible entity generation's independently revisioned effect definitions and readings. Withdrawal discards the entire pair.
/// </summary>
internal sealed class StatusEffectPresentationState
{
    /// <summary>
    /// Current metadata, which is never applied to readings that reference another table.
    /// </summary>
    StatusEffectPresentationCodec.Definitions? _definitions;

    /// <summary>
    /// Current complete stack/duration state and its authoritative tick anchor.
    /// </summary>
    StatusEffectPresentationCodec.Times _times = new(0, 1f / 30, [], []);

    /// <summary>
    /// Accepts metadata only after the replica's entity generation and field revision checks.
    /// </summary>
    internal void AcceptDefinitions(byte[] bytes) => _definitions = StatusEffectPresentationCodec.DecodeDefinitions(bytes);

    /// <summary>
    /// Accepts current readings independently of metadata delivery order.
    /// </summary>
    internal void AcceptTimes(byte[] bytes) => _times = StatusEffectPresentationCodec.DecodeTimes(bytes);

    /// <summary>
    /// Missing metadata hides the effects until a matching pair arrives; stale definitions never attach to new readings.
    /// </summary>
    internal IReadOnlyList<StatusEffectView> Project(long tick)
    {
        if (_times.Values.Count == 0 || _definitions is null
            || !_times.DefinitionDigest.AsSpan().SequenceEqual(_definitions.Digest)) return [];
        var result = new StatusEffectView[_times.Values.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var reading = _times.Values[i];
            if (reading.Definition >= _definitions.Values.Count) throw new InvalidDataException("Missing status effect definition.");
            var definition = _definitions.Values[reading.Definition];
            if (reading.Stacks > definition.MaxStacks) throw new InvalidDataException("Invalid status effect stacks.");
            result[i] = new(definition, StatusEffectPresentationCodec.HoursAt(_times, reading, tick), reading.Stacks);
        }

        return result;
    }
}
