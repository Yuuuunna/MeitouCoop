using System.Buffers.Binary;
using System.Security.Cryptography;
using Meitou.Diagnostics;

namespace Meitou.Networking;

/// <summary>
/// Transfer outcomes for server-initiated collection; disabled clients are never enabled remotely.
/// </summary>
public enum DiagnosticTransferStatus : byte
{
    /// <summary>
    /// The immutable client package is ready for bounded pulls.
    /// </summary>
    Ready,
    /// <summary>
    /// Client collection is off or its host has no collector.
    /// </summary>
    Disabled,
    /// <summary>
    /// Another transfer or the local capture rate limit prevents this request.
    /// </summary>
    Busy,
    /// <summary>
    /// Collection, transfer or persistence failed.
    /// </summary>
    Failed,
    /// <summary>
    /// Server verified and durably saved the package.
    /// </summary>
    Complete
}

/// <summary>
/// Fixed-size offer; the digest covers the exact immutable ZIP bytes.
/// </summary>
public sealed record DiagnosticTransferOffer(Guid Report, DiagnosticTransferStatus Status, int Length, byte[] Digest);

/// <summary>
/// Typed operator collection metadata and a bounded client-to-server bulk frame.
/// </summary>
public static class DiagnosticTransferProtocol
{
    /// <summary>
    /// Chunk size fits the existing 64 KiB reliable bulk lane, including both headers.
    /// </summary>
    public const int ChunkBytes = 60 * 1024;
    /// <summary>
    /// Distinguishes diagnostic upload chunks from other bulk protocols.
    /// </summary>
    const uint Magic = 0x3147444D;

    /// <summary>
    /// Operator collection is bound to the authenticated connection, independent of gameplay view replacement.
    /// </summary>
    public static bool IsControl(WireMessage kind) => kind is WireMessage.DiagnosticCollect or WireMessage.DiagnosticOffer
        or WireMessage.DiagnosticPull or WireMessage.DiagnosticFinished;

    /// <summary>
    /// Requests one batch ID; no file selector or upload address crosses the connection.
    /// </summary>
    public static WireFrame Collect(Guid report) => new(WireMessage.DiagnosticCollect, Id(report));

    /// <summary>
    /// Reads the server's collection request.
    /// </summary>
    public static Guid ReadCollect(WireFrame frame)
    {
        if (frame.Kind != WireMessage.DiagnosticCollect || frame.Payload.Length != 16) throw new InvalidDataException("Invalid diagnostic collect.");
        return ReadId(frame.Payload.Span);
    }

    /// <summary>
    /// Offers either a complete package or an explicit refusal.
    /// </summary>
    public static WireFrame Offer(DiagnosticTransferOffer offer)
    {
        byte[] bytes = new byte[53];
        Id(offer.Report).CopyTo(bytes, 0);
        bytes[16] = (byte)offer.Status;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(17), offer.Length);
        if (offer.Digest.Length != SHA256.HashSizeInBytes) throw new InvalidDataException("Invalid diagnostic digest.");
        offer.Digest.CopyTo(bytes, 21);
        var frame = new WireFrame(WireMessage.DiagnosticOffer, bytes);
        _ = ReadOffer(frame);
        return frame;
    }

    /// <summary>
    /// Rejects oversized or inconsistent offers before staging any bytes.
    /// </summary>
    public static DiagnosticTransferOffer ReadOffer(WireFrame frame)
    {
        if (frame.Kind != WireMessage.DiagnosticOffer || frame.Payload.Length != 53) throw new InvalidDataException("Invalid diagnostic offer.");
        var span = frame.Payload.Span;
        var result = new DiagnosticTransferOffer(ReadId(span), (DiagnosticTransferStatus)span[16],
            BinaryPrimitives.ReadInt32LittleEndian(span[17..]), span[21..].ToArray());
        if (!Enum.IsDefined(result.Status) || result.Status == DiagnosticTransferStatus.Complete ||
            (result.Status == DiagnosticTransferStatus.Ready ? result.Length <= 0 || result.Length > DiagnosticStore.MaximumBytes : result.Length != 0))
            throw new InvalidDataException("Invalid diagnostic offer bounds.");
        return result;
    }

    /// <summary>
    /// Pulls one aligned chunk; the server admits only one outstanding chunk per client.
    /// </summary>
    public static WireFrame Pull(Guid report, int offset)
    {
        ValidateOffset(offset);
        byte[] bytes = new byte[20];
        Id(report).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), offset);
        return new(WireMessage.DiagnosticPull, bytes);
    }

    /// <summary>
    /// Reads and validates a bounded pull request.
    /// </summary>
    public static (Guid Report, int Offset) ReadPull(WireFrame frame)
    {
        if (frame.Kind != WireMessage.DiagnosticPull || frame.Payload.Length != 20) throw new InvalidDataException("Invalid diagnostic pull.");
        int offset = BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.Span[16..]);
        ValidateOffset(offset);
        return (ReadId(frame.Payload.Span), offset);
    }

    /// <summary>
    /// Writes a frame for the independent bulk lane.
    /// </summary>
    public static byte[] Chunk(Guid report, int offset, ReadOnlySpan<byte> payload)
    {
        ValidateOffset(offset);
        if (payload.Length is <= 0 or > ChunkBytes || offset + payload.Length > DiagnosticStore.MaximumBytes)
            throw new InvalidDataException("Invalid diagnostic chunk size.");
        byte[] bytes = new byte[24 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        Id(report).CopyTo(bytes, 4);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), offset);
        payload.CopyTo(bytes.AsSpan(24));
        return bytes;
    }

    /// <summary>
    /// Reads a bulk frame without accepting arbitrary paths or allocating from a claimed length.
    /// </summary>
    public static (Guid Report, int Offset, ReadOnlyMemory<byte> Bytes) ReadChunk(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is <= 24 or > ChunkBytes + 24 || BinaryPrimitives.ReadUInt32LittleEndian(payload.Span) != Magic)
            throw new InvalidDataException("Invalid diagnostic bulk frame.");
        int offset = BinaryPrimitives.ReadInt32LittleEndian(payload.Span[20..]);
        ValidateOffset(offset);
        if (offset + payload.Length - 24 > DiagnosticStore.MaximumBytes) throw new InvalidDataException("Diagnostic chunk exceeds package bound.");
        return (ReadId(payload.Span[4..]), offset, payload[24..]);
    }

    /// <summary>
    /// Acknowledges durable completion or cancels an unsuccessful transfer.
    /// </summary>
    public static WireFrame Finished(Guid report, bool saved)
    {
        byte[] bytes = new byte[17];
        Id(report).CopyTo(bytes, 0);
        bytes[16] = saved ? (byte)1 : (byte)0;
        return new(WireMessage.DiagnosticFinished, bytes);
    }

    /// <summary>
    /// Reads a completion acknowledgement for the exact outstanding report.
    /// </summary>
    public static (Guid Report, bool Saved) ReadFinished(WireFrame frame)
    {
        if (frame.Kind != WireMessage.DiagnosticFinished || frame.Payload.Length != 17 || frame.Payload.Span[16] > 1)
            throw new InvalidDataException("Invalid diagnostic completion.");
        return (ReadId(frame.Payload.Span), frame.Payload.Span[16] != 0);
    }

    /// <summary>
    /// Validates a nonempty correlation ID.
    /// </summary>
    static byte[] Id(Guid report) => report != Guid.Empty ? report.ToByteArray() : throw new InvalidDataException("Empty diagnostic report.");

    /// <summary>
    /// Decodes an ID from a previously length-checked message.
    /// </summary>
    static Guid ReadId(ReadOnlySpan<byte> bytes)
    {
        var report = new Guid(bytes[..16]);
        if (report == Guid.Empty) throw new InvalidDataException("Empty diagnostic report.");
        return report;
    }

    /// <summary>
    /// Requires exact sequential chunk boundaries within the package cap.
    /// </summary>
    static void ValidateOffset(int offset)
    {
        if (offset < 0 || offset >= DiagnosticStore.MaximumBytes || offset % ChunkBytes != 0)
            throw new InvalidDataException("Invalid diagnostic chunk offset.");
    }
}
