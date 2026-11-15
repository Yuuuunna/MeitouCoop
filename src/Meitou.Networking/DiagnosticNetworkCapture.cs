using System.Diagnostics;
using Meitou.Diagnostics;
using Meitou.Networking.VNext;

namespace Meitou.Networking;

/// <summary>
/// Reuses cached QUIC statistics; diagnostics do not query the native transport or gameplay from another thread.
/// </summary>
public static class DiagnosticNetworkCapture
{
    /// <summary>
    /// Records a bounded sample for the authenticated connection; the recorder handles one-second sampling.
    /// </summary>
    public static void Sample(NativeGameConnection connection, DiagnosticScope scope, long tick, double presentationTick)
    {
        if (DiagnosticSession.Current is not { Recorder.Enabled: true } diagnostics) return;
        var statistics = connection.Statistics;
        diagnostics.Recorder.Network(new(Stopwatch.GetTimestamp(), scope, statistics.SentUdpPayloadBytes,
            statistics.ReceivedUdpPayloadBytes, statistics.RttMicroseconds, statistics.NativeOutstandingBytes,
            statistics.QueuedReceiveBytes, tick, presentationTick));
    }
}
