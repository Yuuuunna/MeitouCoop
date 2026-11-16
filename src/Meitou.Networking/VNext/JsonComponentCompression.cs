using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Meitou.Networking.VNext;

/// <summary>
/// Stateless compression of an absolute JSON component. Its revision, authorization withdrawal and publication fences stay in the containing
/// record; decoding never depends on a previous component or a shared dictionary.
/// </summary>
internal static class JsonComponentCompression
{
    /// <summary>
    /// A zero byte cannot begin JSON. It identifies this envelope before its three ASCII magic bytes and version.
    /// </summary>
    static ReadOnlySpan<byte> Magic => "\0MJC"u8;

    /// <summary>
    /// Envelope version, followed by the exact uncompressed JSON length as a little-endian int32 and one Brotli stream.
    /// </summary>
    const byte Version = 1;

    /// <summary>
    /// Four magic bytes, one version byte and four length bytes.
    /// </summary>
    const int HeaderBytes = 9;

    /// <summary>
    /// Smaller components keep their JSON. A compressed component must save at least 128 bytes and one eighth of its original size.
    /// </summary>
    const int MinimumBytes = 1024, MinimumSaving = 128;

    /// <summary>
    /// Encodes one complete value, using low-cost Brotli only when it fits within the required saving. The ordinary JSON codec remains unchanged
    /// for event-retention accounting and other callers outside component replication.
    /// </summary>
    internal static byte[] Encode<T>(WorldViewCodec codec, T value)
    {
        byte[] json = codec.EncodeComponent(value);
        if (json.Length < MinimumBytes) return json;

        int capacity = json.Length - Math.Max(MinimumSaving, (json.Length + 7) / 8) - HeaderBytes;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            if (!BrotliEncoder.TryCompress(json, scratch.AsSpan(0, capacity), out int written, quality: 1, window: 22)) return json;

            byte[] encoded = new byte[HeaderBytes + written];
            Magic.CopyTo(encoded);
            encoded[4] = Version;
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(5), json.Length);
            scratch.AsSpan(0, written).CopyTo(encoded.AsSpan(HeaderBytes));
            return encoded;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Accepts plain JSON or one bounded compressed envelope, then runs the same structural and typed validation as an uncompressed component.
    /// </summary>
    internal static T Decode<T>(WorldViewCodec codec, ReadOnlySpan<byte> bytes)
    {
        int length = DecodedLength(bytes);
        ReadOnlySpan<byte> json = bytes[0] == 0 ? Expand(bytes, length) : bytes;
        WorldViewCodec.ValidateJson(json);
        return codec.DecodeComponent<T>(json);
    }

    /// <summary>
    /// Checks the envelope without allocating output, so publication and deferred-history budgets can charge the expanded size before decoding.
    /// </summary>
    internal static int DecodedLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > WorldViewCodec.MaximumBytes)
            throw new InvalidDataException("Invalid JSON component size.");
        if (bytes[0] != 0) return bytes.Length;
        if (bytes.Length <= HeaderBytes || !bytes.StartsWith(Magic) || bytes[4] != Version)
            throw new InvalidDataException("Invalid compressed JSON component header.");

        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]);
        if (length <= 0 || length > WorldViewCodec.MaximumBytes)
            throw new InvalidDataException("Invalid compressed JSON component length.");

        return length;
    }

    /// <summary>
    /// Allocates only the declared bounded output and rejects truncated, concatenated, trailing or incorrectly sized compressed streams.
    /// </summary>
    static byte[] Expand(ReadOnlySpan<byte> bytes, int length)
    {
        byte[] json = new byte[length];
        using var decoder = new BrotliDecoder();
        var payload = bytes[HeaderBytes..];
        var status = decoder.Decompress(payload, json, out int consumed, out int written);
        if (status != OperationStatus.Done || consumed != payload.Length || written != length)
            throw new InvalidDataException("Invalid compressed JSON component payload.");

        return json;
    }
}
