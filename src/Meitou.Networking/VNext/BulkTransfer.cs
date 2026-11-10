using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Meitou.Networking.VNext;

public readonly record struct BulkTransferIdentity(ulong SessionEpoch, ulong ViewEpoch, ulong BaselineId,
    ulong CaptureTick, ulong LifecycleWatermark);

public enum BulkTransferOutcome
{
    Accepted, Duplicate, Busy, StaleEpoch, InvalidIdentity, InvalidLength, OutOfOrder, Conflict,
    NotReady, DigestMismatch,
}

/// <summary>Manifest digest is copied; construction validates before a receiver allocates staging.</summary>
public sealed class BulkTransferManifest
{
    readonly byte[] digest;
    public BulkTransferIdentity Identity { get; }
    public int TotalBytes { get; }
    public ReadOnlyMemory<byte> Digest => digest;

    public BulkTransferManifest(BulkTransferIdentity identity, int totalBytes, ReadOnlySpan<byte> sha256)
    {
        BulkChunkCodec.ValidateIdentity(identity);
        if (totalBytes <= 0 || totalBytes > BulkTransferSender.MaximumTransferBytes)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        if (sha256.Length != SHA256.HashSizeInBytes) throw new ArgumentException("Expected SHA256 digest.", nameof(sha256));
        Identity = identity;
        TotalBytes = totalBytes;
        digest = sha256.ToArray();
    }
}

/// <summary>Independent chunk copy ownership. Retaining or releasing this object never acknowledges a peer.</summary>
public sealed class BulkChunk
{
    readonly byte[] bytes;
    public BulkTransferIdentity Identity { get; }
    public int Offset { get; }
    public ReadOnlyMemory<byte> Payload => bytes;

    public BulkChunk(BulkTransferIdentity identity, int offset, ReadOnlySpan<byte> payload)
    {
        BulkChunkCodec.ValidateIdentity(identity);
        if (offset < 0 || offset % BulkTransferSender.ChunkBytes != 0 ||
            offset > BulkTransferSender.MaximumTransferBytes - payload.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (payload.Length <= 0 || payload.Length > BulkTransferSender.ChunkBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));
        Identity = identity;
        Offset = offset;
        bytes = payload.ToArray();
    }
}

/// <summary>Fixed little-endian framing on an independent reliable stream. No UDP retransmission protocol.</summary>
public static class BulkChunkCodec
{
    public const int HeaderBytes = 52;
    public const int MaximumFrameBytes = HeaderBytes + BulkTransferSender.ChunkBytes;

    public static byte[] Encode(BulkChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        byte[] frame = new byte[HeaderBytes + chunk.Payload.Length];
        "MTB2"u8.CopyTo(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 21);
        WriteIdentity(frame.AsSpan(6), chunk.Identity);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(46), chunk.Offset);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(50), (ushort)chunk.Payload.Length);
        chunk.Payload.Span.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    public static BulkChunk Decode(ReadOnlySpan<byte> frame)
    {
        // All framing and bounds checks precede the only payload allocation (BulkChunk's copy).
        if (frame.Length < HeaderBytes || frame.Length > MaximumFrameBytes ||
            !frame[..4].SequenceEqual("MTB2"u8) || BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]) != 21)
            throw new InvalidDataException("Invalid bulk frame header.");
        int length = BinaryPrimitives.ReadUInt16LittleEndian(frame[50..]);
        if (length <= 0 || length > BulkTransferSender.ChunkBytes || frame.Length != HeaderBytes + length)
            throw new InvalidDataException("Invalid bulk payload length.");
        var identity = ReadIdentity(frame[6..]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(frame[46..]);
        if (!ValidIdentity(identity) || offset < 0 || offset % BulkTransferSender.ChunkBytes != 0 ||
            offset > BulkTransferSender.MaximumTransferBytes - length)
            throw new InvalidDataException("Invalid bulk identity or offset.");
        return new(identity, offset, frame[HeaderBytes..]);
    }

    internal static bool ValidIdentity(BulkTransferIdentity id) => id.SessionEpoch != 0 && id.ViewEpoch != 0 && id.BaselineId != 0;
    internal static void ValidateIdentity(BulkTransferIdentity id)
    {
        if (!ValidIdentity(id)) throw new ArgumentException("Bulk epochs and baseline ID must be nonzero.", nameof(id));
    }
    static void WriteIdentity(Span<byte> bytes, BulkTransferIdentity id)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, id.SessionEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], id.ViewEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], id.BaselineId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], id.CaptureTick);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], id.LifecycleWatermark);
    }
    static BulkTransferIdentity ReadIdentity(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]));
}

/// <summary>
/// Baseline IDs strictly increase within each view; empty baselines are rejected.
/// One owner/peer; immutable authorized source is copied once, at most 32MiB. Application ACKs alone
/// release 256KiB issued-byte credit. Chunk copies belong to the caller and need separate native-submit
/// bounds (BandwidthScheduler); this class cannot bound arbitrary copies retained by its consumer.
/// </summary>
public sealed class BulkTransferSender
{
    public const int MaximumTransferBytes = 32 * 1024 * 1024;
    public const int ChunkBytes = 4096;
    public const int MaximumUnacknowledgedBytes = 256 * 1024;
    byte[]? source;
    BulkTransferManifest? manifest;
    ulong session, view, lastBaseline;
    int issued, acknowledged;
    public int UnacknowledgedBytes => issued - acknowledged;
    public bool Active => source is not null;

    public BulkTransferSender(ulong sessionEpoch, ulong viewEpoch) => ResetSession(sessionEpoch, viewEpoch);

    public BulkTransferOutcome Begin(BulkTransferIdentity identity, ReadOnlySpan<byte> authorizedSource,
        out BulkTransferManifest? transfer)
    {
        transfer = null;
        if (!BulkChunkCodec.ValidIdentity(identity)) return BulkTransferOutcome.InvalidIdentity;
        if (identity.SessionEpoch != session || identity.ViewEpoch != view) return BulkTransferOutcome.StaleEpoch;
        if (authorizedSource.Length <= 0 || authorizedSource.Length > MaximumTransferBytes) return BulkTransferOutcome.InvalidLength;
        if (Active)
        {
            if (manifest!.Identity != identity) return BulkTransferOutcome.Busy;
            if (authorizedSource.Length != manifest.TotalBytes || !SHA256.HashData(authorizedSource).AsSpan().SequenceEqual(manifest.Digest.Span))
                return BulkTransferOutcome.Conflict;
            transfer = manifest;
            return BulkTransferOutcome.Duplicate;
        }
        if (identity.BaselineId <= lastBaseline) return BulkTransferOutcome.InvalidIdentity;
        byte[] owned = authorizedSource.ToArray();
        var prepared = new BulkTransferManifest(identity, owned.Length, SHA256.HashData(owned));
        source = owned;
        manifest = prepared;
        lastBaseline = identity.BaselineId;
        issued = acknowledged = 0;
        transfer = manifest;
        return BulkTransferOutcome.Accepted;
    }

    public bool TryTakeChunk(out BulkChunk? chunk)
    {
        chunk = null;
        if (source is null || issued == source.Length) return false;
        int length = Math.Min(ChunkBytes, source.Length - issued);
        if (UnacknowledgedBytes + length > MaximumUnacknowledgedBytes) return false;
        chunk = new(manifest!.Identity, issued, source.AsSpan(issued, length));
        issued += length;
        return true;
    }

    public BulkTransferOutcome Acknowledge(BulkTransferIdentity identity, int contiguousOffset)
    {
        if (identity.SessionEpoch != session || identity.ViewEpoch != view) return BulkTransferOutcome.StaleEpoch;
        if (manifest is null || identity != manifest.Identity) return BulkTransferOutcome.InvalidIdentity;
        if (contiguousOffset < 0 || contiguousOffset > issued ||
            (contiguousOffset % ChunkBytes != 0 && contiguousOffset != source!.Length)) return BulkTransferOutcome.InvalidLength;
        if (contiguousOffset <= acknowledged) return BulkTransferOutcome.Duplicate;
        acknowledged = contiguousOffset;
        return BulkTransferOutcome.Accepted;
    }

    public BulkTransferOutcome Finish()
    {
        if (source is null || acknowledged != source.Length) return BulkTransferOutcome.NotReady;
        Discard();
        return BulkTransferOutcome.Accepted;
    }

    public BulkTransferOutcome AdvanceView(ulong newView)
    {
        if (newView <= view) return BulkTransferOutcome.StaleEpoch;
        Discard();
        view = newView;
        lastBaseline = 0;
        return BulkTransferOutcome.Accepted;
    }

    /// <summary>Only after authenticating a new session; clears all old source bytes and credit.</summary>
    public void ResetSession(ulong sessionEpoch, ulong viewEpoch)
    {
        if (sessionEpoch == 0 || viewEpoch == 0) throw new ArgumentOutOfRangeException(nameof(sessionEpoch));
        if (session != 0 && sessionEpoch == session) throw new ArgumentException("A reset requires a distinct authenticated session epoch.", nameof(sessionEpoch));
        Discard();
        session = sessionEpoch;
        view = viewEpoch;
        lastBaseline = 0;
    }

    /// <summary>Revokes retained work immediately; baseline IDs remain consumed within this view.</summary>
    public void Withdraw() => Discard();

    void Discard()
    {
        if (source is not null) CryptographicOperations.ZeroMemory(source);
        source = null;
        manifest = null;
        issued = acknowledged = 0;
    }
}

/// <summary>
/// Validated staged bytes only; no replica apply. Caller owns this artifact and must dispose it on
/// withdrawal/disconnect or after consumption, and fence its identity/lifecycle before commit.
/// </summary>
public sealed class StagedBulkTransfer : IDisposable
{
    byte[]? bytes;
    public BulkTransferIdentity Identity { get; }
    public ReadOnlyMemory<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(bytes is null, this);
            return bytes;
        }
    }
    internal StagedBulkTransfer(BulkTransferIdentity identity, byte[] ownedBytes) { Identity = identity; bytes = ownedBytes; }
    public void Dispose()
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        bytes = null;
    }
}
/// <summary>One bounded staging buffer per peer. No decoder, replica mutation or readiness grant occurs here.</summary>
public sealed class BulkTransferReceiver
{
    byte[]? staging;
    BulkTransferManifest? manifest, completed;
    ulong session, view, lastBaseline;
    int received;
    public int ContiguousBytes => received;
    public bool Active => staging is not null;

    public BulkTransferReceiver(ulong sessionEpoch, ulong viewEpoch) => ResetSession(sessionEpoch, viewEpoch);

    public BulkTransferOutcome Begin(BulkTransferManifest transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (transfer.Identity.SessionEpoch != session || transfer.Identity.ViewEpoch != view) return BulkTransferOutcome.StaleEpoch;
        if (manifest is not null)
        {
            if (manifest.Identity == transfer.Identity && manifest.TotalBytes == transfer.TotalBytes &&
                manifest.Digest.Span.SequenceEqual(transfer.Digest.Span)) return BulkTransferOutcome.Duplicate;
            return manifest.Identity == transfer.Identity ? BulkTransferOutcome.Conflict : BulkTransferOutcome.Busy;
        }
        if (completed?.Identity == transfer.Identity)
            return completed.TotalBytes == transfer.TotalBytes && completed.Digest.Span.SequenceEqual(transfer.Digest.Span)
                ? BulkTransferOutcome.Duplicate : BulkTransferOutcome.Conflict;
        if (transfer.Identity.BaselineId <= lastBaseline) return BulkTransferOutcome.InvalidIdentity;
        // Manifest construction validated the size and digest before this allocation.
        byte[] allocated = new byte[transfer.TotalBytes];
        manifest = transfer;
        staging = allocated;
        lastBaseline = transfer.Identity.BaselineId;
        received = 0;
        return BulkTransferOutcome.Accepted;
    }

    public BulkTransferOutcome AcceptChunk(BulkChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Identity.SessionEpoch != session || chunk.Identity.ViewEpoch != view) return BulkTransferOutcome.StaleEpoch;
        if (manifest is null || chunk.Identity != manifest.Identity) return BulkTransferOutcome.InvalidIdentity;
        int expected = Math.Min(BulkTransferSender.ChunkBytes, manifest.TotalBytes - chunk.Offset);
        if (expected <= 0 || chunk.Payload.Length != expected) return BulkTransferOutcome.InvalidLength;
        if (chunk.Offset > received) return BulkTransferOutcome.OutOfOrder;
        if (chunk.Offset < received)
            return staging!.AsSpan(chunk.Offset, expected).SequenceEqual(chunk.Payload.Span) ? BulkTransferOutcome.Duplicate : BulkTransferOutcome.Conflict;
        chunk.Payload.Span.CopyTo(staging!.AsSpan(received));
        received += expected;
        return BulkTransferOutcome.Accepted;
    }

    public BulkTransferOutcome TryComplete(out StagedBulkTransfer? transfer)
    {
        transfer = null;
        if (staging is null || received != staging.Length) return BulkTransferOutcome.NotReady;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(staging), manifest!.Digest.Span))
        {
            Discard();
            return BulkTransferOutcome.DigestMismatch;
        }
        transfer = new(manifest!.Identity, staging);
        completed = manifest;
        staging = null;
        manifest = null;
        received = 0;
        return BulkTransferOutcome.Accepted;
    }

    public BulkTransferOutcome AdvanceView(ulong newView)
    {
        if (newView <= view) return BulkTransferOutcome.StaleEpoch;
        Discard();
        view = newView;
        lastBaseline = 0;
        return BulkTransferOutcome.Accepted;
    }

    /// <summary>Only after authenticating a new session; zeroes the old incomplete staging buffer.</summary>
    public void ResetSession(ulong sessionEpoch, ulong viewEpoch)
    {
        if (sessionEpoch == 0 || viewEpoch == 0) throw new ArgumentOutOfRangeException(nameof(sessionEpoch));
        if (session != 0 && sessionEpoch == session) throw new ArgumentException("A reset requires a distinct authenticated session epoch.", nameof(sessionEpoch));
        Discard();
        session = sessionEpoch;
        view = viewEpoch;
        lastBaseline = 0;
    }

    /// <summary>Revokes retained work immediately; baseline IDs remain consumed within this view.</summary>
    public void Withdraw() => Discard();

    void Discard()
    {
        if (staging is not null) CryptographicOperations.ZeroMemory(staging);
        staging = null;
        manifest = null;
        completed = null;
        received = 0;
    }
}
