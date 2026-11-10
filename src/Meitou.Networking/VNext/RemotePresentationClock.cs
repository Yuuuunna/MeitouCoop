namespace Meitou.Networking.VNext;

/// <summary>Shared monotonic authority-time estimate with a 100 ms interpolation reservoir (at least
/// three authority ticks). Receive sampling never marks a rendered frame. Before the first presentation,
/// late-entry/cold-loading samples establish the current reservoir; thereafter correction changes speed
/// by at most five percent while confirmed lag stays within 300 ms. Larger lag explicitly
/// re-anchors forward to the reservoir; presentation never moves backwards.</summary>
public sealed record PresentationClockCorrection(double Before, double After, ulong ReceivedTick);

public sealed class RemotePresentationClock
{
    TimeSpan last;
    double authorityEstimate, latest, scale, tickSeconds, bufferTicks, unspentTicks;
    bool initialized, presented;
    public double Tick { get; private set; }
    public ulong CorrectionCount { get; private set; }
    public PresentationClockCorrection? LastCorrection { get; private set; }
    public void MarkPresented(double? sampledTick = null)
    {
        if (presented) return;
        if (sampledTick is { } tick)
        {
            if (!double.IsFinite(tick) || tick < 0 || tick > latest) throw new ArgumentOutOfRangeException(nameof(sampledTick));
            Tick = tick;
        }
        presented = true;
    }
    public void Reset() { initialized = presented = false; authorityEstimate = latest = Tick = unspentTicks = 0; }
    public double Observe(ulong serverTick, double timeScale, double simulationStepSeconds, TimeSpan now)
    {
        if (!double.IsFinite(timeScale) || timeScale < 0 || !double.IsFinite(simulationStepSeconds) || simulationStepSeconds <= 0) throw new InvalidDataException("Invalid authority clock.");
        if (initialized && now < last) throw new InvalidOperationException("Presentation monotonic clock moved backwards.");
        if (initialized && presented) Advance(now);
        else { initialized = true; last = now; }
        latest = Math.Max(latest, serverTick);
        scale = timeScale; tickSeconds = simulationStepSeconds;
        bufferTicks = Math.Max(3, Math.Ceiling(.100 * scale / tickSeconds));
        // Keep elapsed presentation credit when receipt/JIT stalls cap sampling at an old packet.
        // A later trusted timestamp can spend that credit without waiting minutes for a 5% slew.
        if (authorityEstimate - latest > bufferTicks * 2) authorityEstimate = latest;
        else authorityEstimate = Math.Max(authorityEstimate, latest);
        if (scale == 0) unspentTicks = 0;
        if (presented && unspentTicks > 0)
        {
            double catchUp = Math.Min(unspentTicks, Math.Max(0, latest - bufferTicks - Tick));
            Tick += catchUp; unspentTicks -= catchUp;
        }
        // Packet loss/receiver stalls are not the only source of lag: the authority may repay
        // simulation debt faster than wall time. Confirmed lag beyond 300 ms is an explicit
        // clock discontinuity, sampled consistently by both motion and animation before apply.
        double maximumLag = Math.Max(9, Math.Ceiling(.300 * scale / tickSeconds));
        if (presented && latest - Tick > maximumLag)
        {
            double before = Tick; Tick = Math.Max(Tick, Math.Max(0, latest - bufferTicks)); unspentTicks = 0;
            CorrectionCount = checked(CorrectionCount + 1); LastCorrection = new(before, Tick, serverTick);
        }
        if (!presented) { authorityEstimate = latest; Tick = Math.Max(Tick, Math.Max(0, latest - bufferTicks)); }
        return Tick;
    }
    public double Advance(TimeSpan now)
    {
        if (!initialized) return Tick;
        if (now < last) throw new InvalidOperationException("Presentation monotonic clock moved backwards.");
        double elapsedTicks = (now - last).TotalSeconds * scale / tickSeconds;
        last = now; authorityEstimate += elapsedTicks;
        double target = Math.Max(0, authorityEstimate - bufferTicks);
        if (target <= Tick || elapsedTicks == 0) return Tick;
        double correction = Math.Clamp(1 + (target - Tick - elapsedTicks) / Math.Max(1, bufferTicks * 20), .95, 1.05);
        double requested = elapsedTicks * correction;
        double applied = Math.Max(0, Math.Min(latest - Tick, requested));
        Tick += applied;
        if (applied < requested) unspentTicks = Math.Max(unspentTicks, Math.Max(0, target - Tick));
        return Tick;
    }
}
