namespace Meitou.Networking.VNext;

/// <summary>Cumulative counters from one UDP endpoint. Payload includes QUIC headers, encryption,
/// ACKs and retransmits. Count each packet once, not again at the remote endpoint.</summary>
public readonly record struct WireCounterSample(TimeSpan Elapsed, long SentUdpBytes, long ReceivedUdpBytes,
    long SentPackets, long ReceivedPackets, long SentIpHeaderBytes, long ReceivedIpHeaderBytes);

public sealed record WireBudgetResult(bool Passed, double MeanBitsPerSecond, double PeakWindowBitsPerSecond,
    IReadOnlyList<double> WindowBitsPerSecond, IReadOnlyList<string> Failures);

/// <summary>Offline wire-budget gate, independent from scheduler estimates. This proves only throughput,
/// never replication completeness or latency. IP header totals come from capture or a verified path.</summary>
public static class WireBudgetAcceptance
{
    public const long PerPlayerBitsPerSecond = 500_000;
    public const long ServerEgressBitsPerSecond = 25_000_000;

    public static WireBudgetResult Evaluate(IReadOnlyList<WireCounterSample> samples, bool completeCapture,
        bool serverEgressOnly = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var failures = new List<string>();
        if (!completeCapture) failures.Add("Wire capture is incomplete.");
        if (samples.Count < 2)
            return new(false, 0, 0, [], [.. failures, "At least two cumulative counter samples are required."]);

        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.Elapsed < TimeSpan.Zero || s.SentUdpBytes < 0 || s.ReceivedUdpBytes < 0 ||
                s.SentPackets < 0 || s.ReceivedPackets < 0 || s.SentIpHeaderBytes < 0 || s.ReceivedIpHeaderBytes < 0 ||
                s.SentIpHeaderBytes / 20 < s.SentPackets || s.ReceivedIpHeaderBytes / 20 < s.ReceivedPackets ||
                (s.SentUdpBytes != 0 && s.SentPackets == 0) || (s.ReceivedUdpBytes != 0 && s.ReceivedPackets == 0))
                throw new ArgumentException("Counters must include nonnegative UDP payload, packets and IP headers.", nameof(samples));
            if (i == 0) continue;
            var p = samples[i - 1];
            if (s.Elapsed <= p.Elapsed || s.SentUdpBytes < p.SentUdpBytes || s.ReceivedUdpBytes < p.ReceivedUdpBytes ||
                s.SentPackets < p.SentPackets || s.ReceivedPackets < p.ReceivedPackets ||
                s.SentIpHeaderBytes < p.SentIpHeaderBytes || s.ReceivedIpHeaderBytes < p.ReceivedIpHeaderBytes)
                throw new ArgumentException("Counter epochs cannot reset or overlap within a capture.", nameof(samples));
            if ((s.SentIpHeaderBytes - p.SentIpHeaderBytes) / 20 < s.SentPackets - p.SentPackets ||
                (s.ReceivedIpHeaderBytes - p.ReceivedIpHeaderBytes) / 20 < s.ReceivedPackets - p.ReceivedPackets ||
                (s.SentUdpBytes > p.SentUdpBytes && s.SentPackets == p.SentPackets) ||
                (s.ReceivedUdpBytes > p.ReceivedUdpBytes && s.ReceivedPackets == p.ReceivedPackets))
                throw new ArgumentException("Each counter interval must include packet and IP-header accounting.", nameof(samples));
        }

        double duration = (samples[^1].Elapsed - samples[0].Elapsed).TotalSeconds;
        if (duration < 120) failures.Add("Steady-state capture must span at least 120 seconds.");
        double mean = Rate(samples[0], samples[^1], serverEgressOnly);
        var windows = new List<double>();
        int start = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            double span = (samples[i].Elapsed - samples[start].Elapsed).TotalSeconds;
            if (span < 10) continue;
            if (span > 10.001) failures.Add("Missing exact 10-second counter boundary; window rate is unavailable.");
            else windows.Add(Rate(samples[start], samples[i], serverEgressOnly));
            start = i;
        }
        if (start != samples.Count - 1)
            failures.Add("Capture ends inside a 10-second window.");
        long limit = serverEgressOnly ? ServerEgressBitsPerSecond : PerPlayerBitsPerSecond;
        if (mean > limit) failures.Add("Mean wire rate exceeds the hard budget.");
        return new(failures.Count == 0, mean, windows.Count == 0 ? 0 : windows.Max(), windows, failures);
    }

    static double Rate(WireCounterSample from, WireCounterSample to, bool egressOnly)
    {
        // Subtract integers before conversion, avoiding precision loss on long-running counters.
        decimal bytes = (decimal)to.SentUdpBytes - from.SentUdpBytes +
            ((decimal)to.SentPackets - from.SentPackets) * 8 + to.SentIpHeaderBytes - from.SentIpHeaderBytes;
        if (!egressOnly)
            bytes += (decimal)to.ReceivedUdpBytes - from.ReceivedUdpBytes +
                ((decimal)to.ReceivedPackets - from.ReceivedPackets) * 8 + to.ReceivedIpHeaderBytes - from.ReceivedIpHeaderBytes;
        return (double)(bytes * 8) / (to.Elapsed - from.Elapsed).TotalSeconds;
    }
}
