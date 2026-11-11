using System.Buffers.Binary;

namespace Meitou.Networking.VNext;

/// <summary>Length framing only. Authentication, session/view fences and typed body validation are
/// mandatory above this boundary. Native stream receive callbacks are arbitrary byte fragments.</summary>
public sealed class ReliableLaneFramer
{
    readonly int _maximumFrameBytes;
    readonly byte[] _prefix = new byte[4];
    int _prefixLength, _bodyLength;
    byte[]? _body;
    bool _ended, _feeding;
    public ReliableLaneFramer(ScheduledLane lane)
    {
        _maximumFrameBytes = lane switch
        {
            ScheduledLane.Control => 16 * 1024,
            ScheduledLane.Lifecycle or ScheduledLane.Bulk => 64 * 1024,
            _ => throw new ArgumentException("Datagrams must not use stream framing.", nameof(lane)),
        };
    }

    public byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > _maximumFrameBytes - 4)
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
        if (_ended || _feeding)
            throw new InvalidOperationException("Framer is closed, faulted or re-entered.");
        _feeding = true;
        try
        {
            while (!bytes.IsEmpty)
            {
                if (_body is null)
                {
                    int take = Math.Min(4 - _prefixLength, bytes.Length);
                    bytes[..take].CopyTo(_prefix.AsSpan(_prefixLength));
                    _prefixLength += take;
                    bytes = bytes[take..];
                    if (_prefixLength != 4)
                        continue;
                    uint length = BinaryPrimitives.ReadUInt32LittleEndian(_prefix);
                    if (length == 0 || length > _maximumFrameBytes - 4)
                        throw new InvalidDataException("Invalid reliable frame length.");
                    _body = new byte[(int)length];
                }

                int count = Math.Min(_body.Length - _bodyLength, bytes.Length);
                bytes[..count].CopyTo(_body.AsSpan(_bodyLength));
                _bodyLength += count;
                bytes = bytes[count..];
                if (_bodyLength != _body.Length)
                    continue;
                byte[] complete = _body;
                _body = null;
                _prefixLength = _bodyLength = 0;
                receive(complete);
            }
        }
        catch
        {
            _ended = true;
            _body = null;
            throw;
        }
        finally
        {
            _feeding = false;
        }
    }

    public void Complete()
    {
        if (_ended || _feeding)
            throw new InvalidOperationException("Framer is closed, faulted or receiving.");
        _ended = true;
        bool truncated = _prefixLength != 0 || _body is not null;
        _body = null;
        if (truncated)
            throw new InvalidDataException("Reliable stream ended within a frame.");
    }
}