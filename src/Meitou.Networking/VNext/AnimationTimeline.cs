using Meitou.Simulation.Movement;
using Meitou.Simulation;

namespace Meitou.Networking.VNext;
public enum AnimationWrap : byte
{
    Loop,
    Clamp
}

public enum AnimationBlendArea : byte
{
    All,
    Lower,
    Upper,
    Overlay,
    Other
}

public readonly record struct AnimationPlaybackLayer(string Name, int Definition, float LengthSeconds, float PhaseSeconds, float Rate, AnimationWrap Wrap, float EndSeconds, float StartWeight, float TargetWeight, float WeightSeconds, AnimationBlendArea Area, bool Wanted, bool ConsumesLower);
public sealed record AnimationPlaybackState(ulong Epoch, ulong StartTick, float TickSeconds, ulong ActionInstance, ulong ValidThroughTick, IReadOnlyList<AnimationPlaybackLayer> Layers);
public readonly record struct AnimationTimelineFence(ulong SessionEpoch, ulong ViewEpoch, ulong Entity, uint InterestGeneration);
public enum AnimationApplyResult
{
    Applied,
    Duplicate,
    Stale,
    Fenced,
    NeedsResync
}

/// <summary>One authorized entity's presentation-only timeline. Playback parameters are prescribed by authority,
/// not inferred from snapshot differences. Root motion, combat timing and audio event delivery remain authoritative.
/// Raw weight envelopes are normalized by the current simulation publication contract, including whole-body
/// consumption of the lower manager. Authority supplies area/wanted/consumption metadata explicitly.
/// Source fence metadata must be checked by the authenticated adapter; this reducer does not authenticate it.</summary>
public sealed class AnimationTimeline
{
    readonly AnimationTimelineFence _fence;
    AnimationPlaybackState? _current;
    readonly SortedDictionary<ulong, AnimationPlaybackState> _pending = new();
    public const int MaximumPendingTransitions = 32;
    ulong _minimumEpoch = 1;
    double _presentedTick = double.NegativeInfinity;
    bool _withdrawn;
    public AnimationTimeline(AnimationTimelineFence fence)
    {
        if (fence.SessionEpoch == 0 || fence.ViewEpoch == 0 || fence.Entity == 0 || fence.InterestGeneration == 0)
            throw new ArgumentException("Missing animation authorization fence.", nameof(fence));
        this._fence = fence;
    }

    public ulong AppliedEpoch => _current?.Epoch ?? 0;
    public ulong LatestReceivedEpoch => _pending.Count == 0 ? AppliedEpoch : Math.Max(AppliedEpoch, _pending.Keys.Last());
    public ulong ActionInstance => _current?.ActionInstance ?? 0;
    public int PendingTransitions => _pending.Count;
    public double PresentedTick => _presentedTick;
    public bool IsHoldingExpiredSegment => _current is not null && _presentedTick > _current.ValidThroughTick;

    public AnimationApplyResult Apply(AnimationTimelineFence source, AnimationPlaybackState state)
    {
        if (_withdrawn || source != _fence)
            return AnimationApplyResult.Fenced;
        AnimationPlaybackValidation.Validate(state);
        if (state.Epoch < _minimumEpoch || _current is not null && state.Epoch < _current.Epoch)
            return AnimationApplyResult.Stale;
        var same = _current?.Epoch == state.Epoch ? _current : _pending.GetValueOrDefault(state.Epoch);
        if (same is not null)
        {
            if (state.StartTick != same.StartTick || state.TickSeconds != same.TickSeconds || state.ActionInstance != same.ActionInstance || state.ValidThroughTick != same.ValidThroughTick || !state.Layers.SequenceEqual(same.Layers))
                throw new InvalidDataException("Conflicting animation state at the same epoch.");
            return AnimationApplyResult.Duplicate;
        }

        // Epoch order must agree with transition time, but future transitions may arrive out of order.
        // Keep intermediates until the presentation clock reaches them; a newer received epoch cannot erase them.
        if (_current is not null && state.StartTick < _current.StartTick || _pending.Any(p => p.Key < state.Epoch ? p.Value.StartTick > state.StartTick : p.Value.StartTick < state.StartTick))
            throw new InvalidDataException("Animation epoch and transition time disagree.");
        if (state.StartTick <= _presentedTick)
        {
            _current = state with
            {
                Layers = Array.AsReadOnly(state.Layers.ToArray())
            };
            return AnimationApplyResult.Applied;
        }

        if (_pending.Count >= MaximumPendingTransitions)
            return AnimationApplyResult.NeedsResync;
        _pending.Add(state.Epoch, state with { Layers = Array.AsReadOnly(state.Layers.ToArray()) });
        return AnimationApplyResult.Applied;
    }

    /// <summary>Teleport/carry/ragdoll discontinuity: clear old playback immediately and reject its late packets.
    /// Authority must send an absolute state at or above the supplied strictly newer epoch.</summary>
    public void Discontinue(ulong nextEpoch)
    {
        if (nextEpoch == 0 || nextEpoch < _minimumEpoch || nextEpoch <= LatestReceivedEpoch)
            throw new ArgumentOutOfRangeException(nameof(nextEpoch));
        _minimumEpoch = nextEpoch;
        _current = null;
        _pending.Clear();
    }

    /// <summary>
    /// Revocation is terminal for this interest generation; re-entry requires a new fenced timeline.
    /// </summary>
    public void Withdraw()
    {
        _current = null;
        _pending.Clear();
        _withdrawn = true;
    }

    /// <summary>Bridge for existing consumers of named AnimationLayers. Clock corrections cannot run the
    /// presentation tick backwards. A new epoch may deliberately reset clip phase without resetting this clock.
    /// Future transitions remain staged until StartTick; no arrival timestamp participates.</summary>
    public IReadOnlyList<AnimationLayer> Sample(double presentationTick)
    {
        AdvanceTo(presentationTick);
        if (_withdrawn || _current is null)
            return Array.Empty<AnimationLayer>();
        return SampleCurrent();
    }

    /// <summary>Consumes only transitions that are mature at the shared trusted presentation clock.
    /// Receive adapters call this before applying a burst, including when no renderer is running.</summary>
    public void AdvanceTo(double presentationTick)
    {
        if (!double.IsFinite(presentationTick) || presentationTick < 0 || presentationTick > AnimationPlaybackValidation.MaximumTick)
            throw new ArgumentOutOfRangeException(nameof(presentationTick));
        _presentedTick = Math.Max(_presentedTick, presentationTick);
        if (_withdrawn)
            return;
        while (_pending.Count != 0)
        {
            KeyValuePair<ulong, AnimationPlaybackState> pair;
            using (var first = _pending.GetEnumerator())
            {
                if (!first.MoveNext())
                    break;
                pair = first.Current;
            }

            if (pair.Value.StartTick > _presentedTick)
                break;
            _current = pair.Value;
            _pending.Remove(pair.Key);
        }
    }

    IReadOnlyList<AnimationLayer> SampleCurrent()
    {
        // Presentation ticks come from the authoritative simulation clock (paused ticks do not advance).
        // Never extrapolate beyond the authority-prescribed segment: hold its exact endpoint until renewed.
        var current = this._current!;
        double seconds = Math.Max(0, Math.Min(_presentedTick, current.ValidThroughTick) - current.StartTick) * current.TickSeconds;
        var result = new AnimationLayer[current.Layers.Count];
        var visible = new bool[result.Length];
        int visibleCount = 0;
        for (int i = 0; i < result.Length; i++)
        {
            var layer = current.Layers[i];
            double phase = layer.PhaseSeconds + seconds * layer.Rate;
            phase = layer.Wrap == AnimationWrap.Loop ? phase - Math.Floor(phase / layer.LengthSeconds) * layer.LengthSeconds : Math.Clamp(phase, 0, layer.EndSeconds);
            double blend = layer.WeightSeconds == 0 ? 1 : Math.Min(1, seconds / layer.WeightSeconds);
            float weight = (float)(layer.StartWeight + (layer.TargetWeight - layer.StartWeight) * blend);
            visible[i] = weight > 0;
            if (visible[i])
                visibleCount++;
            result[i] = new(layer.Name, (float)phase, weight)
            {
                Length = layer.LengthSeconds,
                Definition = layer.Definition
            };
        }

        AnimationWeightNormalization.Normalize(current.Layers, result);
        return visibleCount == result.Length ? result : result.Where((_, i) => visible[i]).ToArray();
    }
}

internal static class AnimationPlaybackValidation
{
    internal const ulong MaximumTick = 9007199254740991;
    internal const int MaximumLayers = 16, MaximumNameBytes = 128;
    internal const double MaximumSegmentSeconds = 60;
    internal static readonly System.Text.UTF8Encoding Utf8 = new(false, true);
    internal static void Validate(AnimationPlaybackState state)
    {
        if (state is null || state.Epoch == 0 || state.StartTick > MaximumTick || state.ValidThroughTick < state.StartTick || state.ValidThroughTick > MaximumTick || !float.IsFinite(state.TickSeconds) || state.TickSeconds < .001f || state.TickSeconds > 1 || (state.ValidThroughTick - state.StartTick) * (double)state.TickSeconds > MaximumSegmentSeconds || state.Layers is null || state.Layers.Count > MaximumLayers)
            throw new InvalidDataException("Invalid animation state.");
        foreach (var layer in state.Layers)
        {
            if (!ValidName(layer.Name) || layer.Definition < -1 || !Range(layer.LengthSeconds, .000001f, 86400) || !Range(layer.PhaseSeconds, 0, layer.LengthSeconds) || !Range(layer.Rate, -1024, 1024) || layer.Wrap is not (AnimationWrap.Loop or AnimationWrap.Clamp) || !Range(layer.EndSeconds, 0, layer.LengthSeconds) || !Range(layer.StartWeight, 0, 16) || !Range(layer.TargetWeight, 0, 16) || !Range(layer.WeightSeconds, 0, 86400) || layer.Area is < AnimationBlendArea.All or > AnimationBlendArea.Other || layer.ConsumesLower && layer.Area is AnimationBlendArea.Lower or AnimationBlendArea.Overlay)
                throw new InvalidDataException("Invalid animation playback layer.");
        }
    }

    static bool ValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            return false;
        try
        {
            return Utf8.GetByteCount(name) <= MaximumNameBytes;
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false;
        }
    }

    static bool Range(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
}