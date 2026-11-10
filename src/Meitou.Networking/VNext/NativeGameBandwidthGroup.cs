using System.Diagnostics;

namespace Meitou.Networking.VNext;

/// <summary>Shared server egress pacer. Application estimates are reconciled against actual encrypted
/// UDP payload counters; IP header accounting remains outside these native counters.</summary>
public sealed class NativeGameBandwidthGroup
{
    readonly object gate = new();
    readonly Dictionary<ulong, (ulong Sent, long Reserved)> peers = [];
    long last = Stopwatch.GetTimestamp();
    double credit = BandwidthScheduler.GlobalBurstBytes;
    ulong nextPeer;
    public ulong Register() { lock (gate) { ulong id = checked(++nextPeer); peers.Add(id, default); return id; } }
    public void Remove(ulong peer) { lock (gate) peers.Remove(peer); }
    void Advance()
    {
        long timestamp = Stopwatch.GetTimestamp();
        credit = Math.Min(BandwidthScheduler.GlobalBurstBytes, credit + Stopwatch.GetElapsedTime(last, timestamp).TotalSeconds * BandwidthScheduler.GlobalWireBytesPerSecond);
        last = timestamp;
    }
    public bool TryReserve(ulong peer, int bytes)
    {
        lock (gate)
        {
            Advance();
            if (credit < bytes) return false;
            var state = peers[peer];
            credit -= bytes; peers[peer] = (state.Sent, state.Reserved + bytes);
            return true;
        }
    }
    public void Observe(ulong peer, ulong cumulativeSent)
    {
        lock (gate)
        {
            Advance();
            var state = peers[peer];
            if (cumulativeSent < state.Sent) throw new InvalidDataException("Native egress counter reset.");
            if (cumulativeSent == state.Sent) return;
            credit -= Math.Max(0, cumulativeSent - state.Sent - (double)state.Reserved);
            peers[peer] = (cumulativeSent, 0);
        }
    }
}
