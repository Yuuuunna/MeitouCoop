using System.Diagnostics;

namespace Meitou.Diagnostics;

/// <summary>
/// Cheap owner-thread counters, copied once per second by the diagnostic worker without reading a live world.
/// </summary>
sealed class DiagnosticHealthSampler(DiagnosticRecorder recorder)
{
    /// <summary>
    /// Protects the interval counters shared by the owner loop and sampler.
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// Last observed switch generation, preventing interval data from spanning disabled periods.
    /// </summary>
    long _epoch;
    /// <summary>
    /// Frame count and largest business queue observed in this interval.
    /// </summary>
    int _frames, _queue;
    /// <summary>
    /// Last frame timestamp and latest authoritative tick.
    /// </summary>
    long _lastFrame, _tick;
    /// <summary>
    /// Interval maxima and most recent simulation clock observations.
    /// </summary>
    double _frameMax, _workMax, _tickMax, _debt, _presentation;

    /// <summary>
    /// Observes one frame without retaining work while disabled.
    /// </summary>
    public void Frame(long tick, double presentationTick)
    {
        if (!recorder.Enabled) return;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            CheckEpoch();
            if (_lastFrame != 0) _frameMax = Math.Max(_frameMax, Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds);
            _lastFrame = now;
            _frames++;
            _tick = tick;
            _presentation = presentationTick;
        }
    }

    /// <summary>
    /// Observes one server pass; callers supply already measured values from the world owner thread.
    /// </summary>
    public void ServerPass(long tick, double workMilliseconds, double tickMilliseconds, int queue, double debt)
    {
        if (!recorder.Enabled) return;
        lock (_gate)
        {
            CheckEpoch();
            _tick = tick;
            _workMax = Math.Max(_workMax, workMilliseconds);
            _tickMax = Math.Max(_tickMax, tickMilliseconds);
            _queue = Math.Max(_queue, queue);
            _debt = debt;
        }
    }

    /// <summary>
    /// Copies process counters and resets interval aggregates; CPU and GC counts are cumulative.
    /// </summary>
    public void Sample(Process process)
    {
        lock (_gate)
        {
            CheckEpoch();
            recorder.Sample(new(Stopwatch.GetTimestamp(), process.WorkingSet64, GC.GetTotalMemory(false), process.TotalProcessorTime.TotalMilliseconds,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), _frames, _frameMax, _tick, _workMax, _tickMax, _queue, _debt, _presentation));
            _frames = _queue = 0;
            _frameMax = _workMax = _tickMax = 0;
        }
    }

    /// <summary>
    /// Drops interval-only state after either side of a runtime switch.
    /// </summary>
    void CheckEpoch()
    {
        if (_epoch == recorder.Epoch) return;
        _epoch = recorder.Epoch;
        _lastFrame = _tick = 0;
        _frames = _queue = 0;
        _frameMax = _workMax = _tickMax = _debt = _presentation = 0;
    }
}
