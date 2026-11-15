using Meitou.Simulation.Bodies;

namespace Meitou.Networking.VNext;

/// <summary>
/// One entity generation's independently revisioned meter bars and explanatory text. Only a matching absolute table supplies tooltips;
/// a delayed component cannot attach older text to newer bars. Entity and view withdrawal discard this bounded state with the replica entity.
/// </summary>
internal sealed class MeterPresentationState
{
    /// <summary>
    /// The newest accepted absolute bar values and their tooltip-table digest.
    /// </summary>
    ModPresentationCodec.MeterBars _bars = new([], []);

    /// <summary>
    /// The newest accepted complete tooltip table, if any.
    /// </summary>
    ModPresentationCodec.MeterTooltips? _tooltips;

    /// <summary>
    /// Accepts a bar component already fenced by the replica's entity generation and component revision ledger.
    /// </summary>
    internal IReadOnlyList<MeterReading?> AcceptBars(byte[] bytes)
    {
        _bars = ModPresentationCodec.DecodeMeterBars(bytes);
        return Compose();
    }

    /// <summary>
    /// Accepts a tooltip component already fenced by the same per-field ledger, regardless of which component arrived first.
    /// </summary>
    internal IReadOnlyList<MeterReading?> AcceptTooltips(byte[] bytes)
    {
        _tooltips = ModPresentationCodec.DecodeMeterTooltips(bytes);
        return Compose();
    }

    /// <summary>
    /// Missing or mismatched tooltip metadata leaves the absolute bars usable without stale explanatory text. Matching tables must have
    /// exactly the same slot presence; this catches inconsistent publishers without making unrelated component arrival order significant.
    /// </summary>
    IReadOnlyList<MeterReading?> Compose()
    {
        if (_bars.Values.Count == 0 || _tooltips is null || !_bars.TooltipDigest.AsSpan().SequenceEqual(_tooltips.Digest))
            return _bars.Values;
        if (_bars.Values.Count != _tooltips.Slots.Count) throw new InvalidDataException("Mod meter tooltip slots disagree with their bars.");
        var result = new MeterReading?[_bars.Values.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var value = _bars.Values[i];
            var tooltip = _tooltips.Slots[i];
            if (value.HasValue != tooltip.Present) throw new InvalidDataException("Mod meter tooltip presence disagrees with its bar.");
            result[i] = value is { } reading ? reading with { Tooltip = tooltip.Text } : null;
        }

        return result;
    }
}
