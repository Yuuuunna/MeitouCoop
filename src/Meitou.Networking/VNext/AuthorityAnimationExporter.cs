using Meitou.Simulation;

namespace Meitou.Networking.VNext;
[Flags]
public enum AnimationExportReason : ushort
{
    None = 0,
    Initial = 1,
    Parameters = 2,
    Rate = 4,
    Phase = 8,
    Weight = 16,
    Expiry = 32,
    TickDuration = 64,
    Unpredictable = 128,
}

public sealed record AnimationExportDiagnostics(long Captures, long Suppressed, IReadOnlyDictionary<AnimationExportReason, long> Reasons);
/// <summary>
/// Per-recipient export ledger. Withdraw it with the view; never reuse across interest generations.
/// </summary>
public sealed class AuthorityAnimationExporter
{
    readonly Dictionary<ulong, AnimationPlaybackState> _previous = [];
    readonly bool _diagnostics;
    readonly double _phaseTolerance, _weightTolerance, _rateTolerance, _targetWeightTolerance;
    readonly Dictionary<AnimationExportReason, long> _reasons = [];
    long _captures, _suppressed;
    public AuthorityAnimationExporter(bool diagnostics = false, double phaseTolerance = .0001, double weightTolerance = .0001, double rateTolerance = 0, double targetWeightTolerance = 0)
    {
        if (!double.IsFinite(phaseTolerance) || phaseTolerance < 0 || !double.IsFinite(weightTolerance) || weightTolerance < 0 || !double.IsFinite(rateTolerance) || rateTolerance < 0 || !double.IsFinite(targetWeightTolerance) || targetWeightTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(phaseTolerance));
        this._diagnostics = diagnostics || Environment.GetEnvironmentVariable("MEITOU_ANIMATION_EXPORT_DIAGNOSTICS") == "1";
        this._phaseTolerance = phaseTolerance;
        this._weightTolerance = weightTolerance;
        this._rateTolerance = rateTolerance;
        this._targetWeightTolerance = targetWeightTolerance;
    }

    public AnimationExportDiagnostics Diagnostics => new(_captures, _suppressed, new Dictionary<AnimationExportReason, long>(_reasons));

    public void Withdraw(ulong entity) => _previous.Remove(entity);
    public void Clear() => _previous.Clear();
    /// <summary>Null means existing playback still describes the raw authority state. Unknown external
    /// clocks expire immediately and are renewed explicitly; a stale inferred rate is never transmitted.</summary>
    public AnimationPlaybackState? Capture(ulong entity, AnimationPlaybackCapture capture, ulong tick, float tickSeconds)
    {
        var layers = capture.Layers.Select(l => new AnimationPlaybackLayer(l.Name, l.Definition, l.Length, l.Time, l.Rate, l.Loop ? AnimationWrap.Loop : AnimationWrap.Clamp, l.End, l.Weight, l.Target, l.BlendSeconds, (AnimationBlendArea)l.Area, l.Wanted, l.ConsumesLower)).ToArray();
        bool predictable = capture.Layers.All(l => l.Predictable);
        AnimationExportReason reason = _previous.TryGetValue(entity, out var old) ? Compare(old, layers, tick) : AnimationExportReason.Initial;
        if (old is not null && old.TickSeconds != tickSeconds)
            reason |= AnimationExportReason.TickDuration;
        if (old is not null && tick > old.ValidThroughTick)
            reason |= AnimationExportReason.Expiry;
        if (!predictable)
            reason |= AnimationExportReason.Unpredictable;
        if (_diagnostics)
            _captures++;
        if (reason == AnimationExportReason.None)
        {
            if (_diagnostics)
                _suppressed++;
            return null;
        }

        if (_diagnostics)
            foreach (var flag in Enum.GetValues<AnimationExportReason>())
                if (flag != AnimationExportReason.None && (reason & flag) != 0)
                    _reasons[flag] = _reasons.GetValueOrDefault(flag) + 1;
        // The contract already bounds prediction to 60 simulation seconds. Every source capture checks
        // parameters and phase/weight error; semantic changes replace the segment immediately.
        // Stable deterministic playback therefore renews at the protocol horizon, not on a wall timer.
        ulong duration = predictable ? (ulong)Math.Floor(AnimationPlaybackValidation.MaximumSegmentSeconds / tickSeconds) : 0;
        var state = new AnimationPlaybackState((old?.Epoch ?? 0) + 1, tick, tickSeconds, 0, checked(tick + duration), layers);
        AnimationPlaybackValidation.Validate(state);
        _previous[entity] = state;
        return state;
    }

    AnimationExportReason Compare(AnimationPlaybackState old, AnimationPlaybackLayer[] layers, ulong tick)
    {
        if (old.Layers.Count != layers.Length || tick < old.StartTick)
            return AnimationExportReason.Parameters;
        AnimationExportReason reason = AnimationExportReason.None;
        double seconds = (tick - old.StartTick) * (double)old.TickSeconds;
        for (int i = 0; i < layers.Length; i++)
        {
            var a = old.Layers[i];
            var b = layers[i];
            bool sameTarget = _targetWeightTolerance == 0 ? a.TargetWeight == b.TargetWeight : (a.TargetWeight <= 0) == (b.TargetWeight <= 0) && (a.TargetWeight >= 1) == (b.TargetWeight >= 1) && Math.Abs(a.TargetWeight - b.TargetWeight) <= _targetWeightTolerance;
            if (a.Name != b.Name || a.Definition != b.Definition || a.LengthSeconds != b.LengthSeconds || a.Wrap != b.Wrap || a.EndSeconds != b.EndSeconds || !sameTarget || a.Area != b.Area || a.Wanted != b.Wanted || a.ConsumesLower != b.ConsumesLower)
                reason |= AnimationExportReason.Parameters;
            bool sameRate = _rateTolerance == 0 ? a.Rate == b.Rate : (a.Rate == 0) == (b.Rate == 0) && Math.Sign(a.Rate) == Math.Sign(b.Rate) && Math.Abs(a.Rate - b.Rate) <= _rateTolerance;
            if (!sameRate)
                reason |= AnimationExportReason.Rate;
            double time = a.PhaseSeconds + seconds * a.Rate;
            time = a.Wrap == AnimationWrap.Loop ? time - Math.Floor(time / a.LengthSeconds) * a.LengthSeconds : Math.Clamp(time, 0, a.EndSeconds);
            double weight = a.StartWeight + (a.TargetWeight - a.StartWeight) * (a.WeightSeconds == 0 ? 1 : Math.Min(1, seconds / a.WeightSeconds));
            double phaseError = Math.Abs(time - b.PhaseSeconds);
            if (a.Wrap == AnimationWrap.Loop)
                phaseError = Math.Min(phaseError, Math.Abs(phaseError - a.LengthSeconds));
            if (phaseError > _phaseTolerance)
                reason |= AnimationExportReason.Phase;
            if (Math.Abs(weight - b.StartWeight) > _weightTolerance || Math.Abs(Math.Max(0, a.WeightSeconds - seconds) - b.WeightSeconds) > _weightTolerance)
                reason |= AnimationExportReason.Weight;
        }

        return reason;
    }
}