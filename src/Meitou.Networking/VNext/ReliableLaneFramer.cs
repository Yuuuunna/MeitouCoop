using System.Buffers.Binary;

namespace Meitou.Networking.VNext;

/// <summary>Length framing only. Authentication, session/view fences and typed body validation are
/// mandatory above this boundary. Native stream receive callbacks are arbitrary byte fragments.</summary>
public sealed class ReliableLaneFramer
{
    readonly int maximumFrameBytes;
    readonly byte[] prefix = new byte[4];
    int prefixLength, bodyLength;
    byte[]? body;
    bool ended, feeding;

    public ReliableLaneFramer(ScheduledLane lane)
    {
        maximumFrameBytes = lane switch
        {
            ScheduledLane.Control => 16 * 1024,
            ScheduledLane.Lifecycle or ScheduledLane.Bulk => 64 * 1024,
            _ => throw new ArgumentException("Datagrams must not use stream framing.", nameof(lane)),
        };
    }

    public byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > maximumFrameBytes - 4)
            throw new InvalidDataException("Reliable frame exceeds lane limit or is empty.");
        byte[] result = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)payload.Length);
        payload.CopyTo(result.AsSpan(4));
        return result;
    }

    /// <summary>Serial, non-reentrant owner API. At most one bounded frame is buffered; the receiver
    /// must consume or separately bound retained frames. A failed callback permanently faults ingress.</summary>
    public void Feed(ReadOnlySpan<byte> bytes, Action<ReadOnlyMemory<byte>> receive)
    {
        ArgumentNullException.ThrowIfNull(receive);
        if (ended || feeding) throw new InvalidOperationException("Framer is closed, faulted or re-entered.");
        feeding = true;
        try
        {
            while (!bytes.IsEmpty)
            {
                if (body is null)
                {
                    int take = Math.Min(4 - prefixLength, bytes.Length);
                    bytes[..take].CopyTo(prefix.AsSpan(prefixLength));
                    prefixLength += take;
                    bytes = bytes[take..];
                    if (prefixLength != 4) continue;
                    uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
                    if (length == 0 || length > maximumFrameBytes - 4)
                        throw new InvalidDataException("Invalid reliable frame length.");
                    body = new byte[(int)length];
                }
                int count = Math.Min(body.Length - bodyLength, bytes.Length);
                bytes[..count].CopyTo(body.AsSpan(bodyLength));
                bodyLength += count;
                bytes = bytes[count..];
                if (bodyLength != body.Length) continue;
                byte[] complete = body;
                body = null;
                prefixLength = bodyLength = 0;
                receive(complete);
            }
        }
        catch
        {
            ended = true;
            body = null;
            throw;
        }
        finally { feeding = false; }
    }

    public void Complete()
    {
        if (ended || feeding) throw new InvalidOperationException("Framer is closed, faulted or receiving.");
        ended = true;
        bool truncated = prefixLength != 0 || body is not null;
        body = null;
        if (truncated) throw new InvalidDataException("Reliable stream ended within a frame.");
    }
}
