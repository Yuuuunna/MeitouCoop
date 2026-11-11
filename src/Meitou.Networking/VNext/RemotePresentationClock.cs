namespace Meitou.Networking.VNext;

/// <summary>Shared monotonic authority-time estimate with a 100 ms interpolation reservoir (at least
/// three authority ticks). Receive sampling never marks a rendered frame. Before the first presentation,
/// late-entry/cold-loading samples establish the current reservoir; thereafter correction changes speed
/// by at most five percent while confirmed lag stays within 300 ms. Larger lag explicitly
/// re-anchors forward to the reservoir; presentation never moves backwards.</summary>
public sealed record PresentationClockCorrection(double Before, double After, ulong ReceivedTick);
public sealed class RemotePresentationClock
{
    TimeSpan _last;
    double _authorityEstimate, _latest, _scale, _tickSeconds, _bufferTicks, _unspentTicks;
    bool _initialized, _presented;
    public double Tick { get; private set; }
    public ulong CorrectionCount { get; private set; }
    public PresentationClockCorrection? LastCorrection { get; private set; }

    public void MarkPresented(double? sampledTick = null)
    {
        if (_presented)
            return;
        if (sampledTick is { } tick)
        {
            if (!double.IsFinite(tick) || tick < 0 || tick > _latest)
                throw new ArgumentOutOfRangeException(nameof(sampledTick));
            Tick = tick;
        }

        _presented = true;
    }

    public void Reset()
    {
        _initialized = _presented = false;
        _authorityEstimate = _latest = Tick = _unspentTicks = 0;
    }

    public double Observe(ulong serverTick, double timeScale, double simulationStepSeconds, TimeSpan now)
    {
        if (!double.IsFinite(timeScale) || timeScale < 0 || !double.IsFinite(simulationStepSeconds) || simulationStepSeconds <= 0)
            throw new InvalidDataException("Invalid authority clock.");
        if (_initialized && now < _last)
            throw new InvalidOperationException("Presentation monotonic clock moved backwards.");
        if (_initialized && _presented)
            Advance(now);
        else
        {
            _initialized = true;
            _last = now;
        }

        _latest = Math.Max(_latest, serverTick);
        _scale = timeScale;
        _tickSeconds = simulationStepSeconds;
        _bufferTicks = Math.Max(3, Math.Ceiling(.100 * _scale / _tickSeconds));
        // Keep elapsed presentation credit when receipt/JIT stalls cap sampling at an old packet.
        // A later trusted timestamp can spend that credit without waiting minutes for a 5% slew.
        if (_authorityEstimate - _latest > _bufferTicks * 2)
            _authorityEstimate = _latest;
        else
            _authorityEstimate = Math.Max(_authorityEstimate, _latest);
        if (_scale == 0)
            _unspentTicks = 0;
        if (_presented && _unspentTicks > 0)
        {
            double catchUp = Math.Min(_unspentTicks, Math.Max(0, _latest - _bufferTicks - Tick));
            Tick += catchUp;
            _unspentTicks -= catchUp;
        }

        // Packet loss/receiver stalls are not the only source of lag: the authority may repay
        // simulation debt faster than wall time. Confirmed lag beyond 300 ms is an explicit
        // clock discontinuity, sampled consistently by both motion and animation before apply.
        double maximumLag = Math.Max(9, Math.Ceiling(.300 * _scale / _tickSeconds));
        if (_presented && _latest - Tick > maximumLag)
        {
            double before = Tick;
            Tick = Math.Max(Tick, Math.Max(0, _latest - _bufferTicks));
            _unspentTicks = 0;
            CorrectionCount = checked(CorrectionCount + 1);
            LastCorrection = new(before, Tick, serverTick);
        }

        if (!_presented)
        {
            _authorityEstimate = _latest;
            Tick = Math.Max(Tick, Math.Max(0, _latest - _bufferTicks));
        }

        return Tick;
    }

    public double Advance(TimeSpan now)
    {
        if (!_initialized)
            return Tick;
        if (now < _last)
            throw new InvalidOperationException("Presentation monotonic clock moved backwards.");
        double elapsedTicks = (now - _last).TotalSeconds * _scale / _tickSeconds;
        _last = now;
        _authorityEstimate += elapsedTicks;
        double target = Math.Max(0, _authorityEstimate - _bufferTicks);
        if (target <= Tick || elapsedTicks == 0)
            return Tick;
        double correction = Math.Clamp(1 + (target - Tick - elapsedTicks) / Math.Max(1, _bufferTicks * 20), .95, 1.05);
        double requested = elapsedTicks * correction;
        double applied = Math.Max(0, Math.Min(_latest - Tick, requested));
        Tick += applied;
        if (applied < requested)
            _unspentTicks = Math.Max(_unspentTicks, Math.Max(0, target - Tick));
        return Tick;
    }
}