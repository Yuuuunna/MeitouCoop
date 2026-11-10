using Meitou.Simulation;

namespace Meitou.Networking.VNext;

[Flags]
public enum AnimationExportReason : ushort
{
    None = 0, Initial = 1, Parameters = 2, Rate = 4, Phase = 8, Weight = 16,
    Expiry = 32, TickDuration = 64, Unpredictable = 128,
}
public sealed record AnimationExportDiagnostics(long Captures, long Suppressed,
    IReadOnlyDictionary<AnimationExportReason, long> Reasons);

/// <summary>Per-recipient export ledger. Withdraw it with the view; never reuse across interest generations.</summary>
public sealed class AuthorityAnimationExporter
{
    readonly Dictionary<ulong, AnimationPlaybackState> previous = [];
    readonly bool diagnostics;
    readonly Dictionary<AnimationExportReason, long> reasons = [];
    long captures, suppressed;
    public AuthorityAnimationExporter(bool diagnostics = false) => this.diagnostics = diagnostics ||
        Environment.GetEnvironmentVariable("MEITOU_ANIMATION_EXPORT_DIAGNOSTICS") == "1";
    public AnimationExportDiagnostics Diagnostics => new(captures, suppressed, new Dictionary<AnimationExportReason, long>(reasons));
    public void Withdraw(ulong entity) => previous.Remove(entity);
    public void Clear() => previous.Clear();

    /// <summary>Null means existing playback still describes the raw authority state. Unknown external
    /// clocks expire immediately and are renewed explicitly; a stale inferred rate is never transmitted.</summary>
    public AnimationPlaybackState? Capture(ulong entity, AnimationPlaybackCapture capture, ulong tick, float tickSeconds)
    {
        var layers = capture.Layers.Select(l => new AnimationPlaybackLayer(l.Name, l.Definition, l.Length,
            l.Time, l.Rate, l.Loop ? AnimationWrap.Loop : AnimationWrap.Clamp, l.End,
            l.Weight, l.Target, l.BlendSeconds, (AnimationBlendArea)l.Area, l.Wanted, l.ConsumesLower)).ToArray();
        bool predictable = capture.Layers.All(l => l.Predictable);
        AnimationExportReason reason = previous.TryGetValue(entity, out var old)
            ? Compare(old, layers, tick) : AnimationExportReason.Initial;
        if (old is not null && old.TickSeconds != tickSeconds) reason |= AnimationExportReason.TickDuration;
        if (old is not null && tick > old.ValidThroughTick) reason |= AnimationExportReason.Expiry;
        if (!predictable) reason |= AnimationExportReason.Unpredictable;
        if (diagnostics) captures++;
        if (reason == AnimationExportReason.None)
        {
            if (diagnostics) suppressed++;
            return null;
        }
        if (diagnostics)
            foreach (var flag in Enum.GetValues<AnimationExportReason>())
                if (flag != AnimationExportReason.None && (reason & flag) != 0) reasons[flag] = reasons.GetValueOrDefault(flag) + 1;
        // The contract already bounds prediction to 60 simulation seconds. Every source capture checks
        // exact parameters and phase/weight error; semantic changes replace the segment immediately.
        // Stable deterministic playback therefore renews at the protocol horizon, not on a wall timer.
        ulong duration = predictable ? (ulong)Math.Floor(AnimationPlaybackValidation.MaximumSegmentSeconds / tickSeconds) : 0;
        var state = new AnimationPlaybackState((old?.Epoch ?? 0) + 1, tick, tickSeconds, 0, checked(tick + duration), layers);
        AnimationPlaybackValidation.Validate(state);
        previous[entity] = state;
        return state;
    }

    static AnimationExportReason Compare(AnimationPlaybackState old, AnimationPlaybackLayer[] layers, ulong tick)
    {
        if (old.Layers.Count != layers.Length || tick < old.StartTick) return AnimationExportReason.Parameters;
        AnimationExportReason reason = AnimationExportReason.None;
        double seconds = (tick - old.StartTick) * (double)old.TickSeconds;
        for (int i = 0; i < layers.Length; i++)
        {
            var a = old.Layers[i]; var b = layers[i];
            if (a.Name != b.Name || a.Definition != b.Definition || a.LengthSeconds != b.LengthSeconds ||
                a.Wrap != b.Wrap || a.EndSeconds != b.EndSeconds || a.TargetWeight != b.TargetWeight ||
                a.Area != b.Area || a.Wanted != b.Wanted || a.ConsumesLower != b.ConsumesLower) reason |= AnimationExportReason.Parameters;
            if (a.Rate != b.Rate) reason |= AnimationExportReason.Rate;
            double time = a.PhaseSeconds + seconds * a.Rate;
            time = a.Wrap == AnimationWrap.Loop ? time - Math.Floor(time / a.LengthSeconds) * a.LengthSeconds : Math.Clamp(time, 0, a.EndSeconds);
            double weight = a.StartWeight + (a.TargetWeight - a.StartWeight) * (a.WeightSeconds == 0 ? 1 : Math.Min(1, seconds / a.WeightSeconds));
            double phaseError = Math.Abs(time - b.PhaseSeconds);
            if (a.Wrap == AnimationWrap.Loop) phaseError = Math.Min(phaseError, Math.Abs(phaseError - a.LengthSeconds));
            if (phaseError > .0001) reason |= AnimationExportReason.Phase;
            if (Math.Abs(weight - b.StartWeight) > .0001 ||
                Math.Abs(Math.Max(0, a.WeightSeconds - seconds) - b.WeightSeconds) > .0001) reason |= AnimationExportReason.Weight;
        }
        return reason;
    }
}
