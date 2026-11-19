using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Meitou.Networking.VNext;

/// <summary>
/// Stateless lossless compression of complete bulk publications, immutable effect definitions and individual motion datagrams.
/// Each payload decodes independently under its caller's allocation bound.
/// </summary>
internal static class AbsolutePayloadCompression
{
    /// <summary>
    /// This prefix cannot be a valid definition count, packed frame length or the canonical MTN2 motion magic.
    /// </summary>
    static ReadOnlySpan<byte> Magic => "MBCF"u8;

    /// <summary>
    /// A version byte and exact uncompressed length follow the four magic bytes.
    /// </summary>
    const int HeaderBytes = 9;

    /// <summary>
    /// Uses low-cost Brotli only when saving both 128 bytes and one eighth of the original. Bulk and metadata start at 1024 bytes;
    /// independently bounded motion batches also consider smaller packets, never one-character updates.
    /// </summary>
    internal static byte[] Encode(byte[] payload, int minimumBytes = 1024)
    {
        if (payload.Length < Math.Max(256, minimumBytes)) return payload;

        int capacity = payload.Length - Math.Max(128, (payload.Length + 7) / 8) - HeaderBytes;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            if (!BrotliEncoder.TryCompress(payload, scratch.AsSpan(0, capacity), out int written, quality: 1, window: 22))
                return payload;

            byte[] result = new byte[HeaderBytes + written];
            Magic.CopyTo(result);
            result[4] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(5), payload.Length);
            scratch.AsSpan(0, written).CopyTo(result.AsSpan(HeaderBytes));
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// The envelope is unambiguous in each supported binary format.
    /// </summary>
    internal static bool IsCompressed(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);

    /// <summary>
    /// Charges the complete expanded payload to the existing publication bound before any decompression allocates it.
    /// </summary>
    internal static int ExpandedLength(ReadOnlySpan<byte> bytes, int maximumBytes = GameStateWire.MaximumBytes)
    {
        if (bytes.Length < 2 || bytes.Length > maximumBytes)
            throw new InvalidDataException("Invalid absolute payload size.");
        if (!IsCompressed(bytes)) return bytes.Length;
        if (bytes.Length <= HeaderBytes || bytes[4] != 1)
            throw new InvalidDataException("Invalid compressed absolute payload header.");

        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]);
        if (length < 2 || length > maximumBytes)
            throw new InvalidDataException("Invalid expanded absolute payload size.");

        return Math.Max(length, bytes.Length);
    }

    /// <summary>
    /// Leaves plain definitions intact and validates any compressed envelope before decoding it.
    /// </summary>
    internal static byte[] Decode(byte[] bytes)
    {
        _ = ExpandedLength(bytes);
        return IsCompressed(bytes) ? Expand(bytes) : bytes;
    }

    /// <summary>
    /// Rejects truncated, concatenated, trailing or incorrectly sized streams before the ordinary frame or definition validation.
    /// </summary>
    internal static byte[] Expand(ReadOnlySpan<byte> bytes, int maximumBytes = GameStateWire.MaximumBytes)
    {
        _ = ExpandedLength(bytes, maximumBytes);
        if (!IsCompressed(bytes)) throw new InvalidDataException("Missing absolute payload compression envelope.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]);
        byte[] result = new byte[length];
        using var decoder = new BrotliDecoder();
        var payload = bytes[HeaderBytes..];
        var status = decoder.Decompress(payload, result, out int consumed, out int written);
        if (status != OperationStatus.Done || consumed != payload.Length || written != length)
            throw new InvalidDataException("Invalid compressed absolute payload.");

        return result;
    }
}
