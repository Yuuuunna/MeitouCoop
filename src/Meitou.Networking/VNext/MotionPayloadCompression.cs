using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Meitou.Networking.VNext;

/// <summary>
/// Independent lossless motion envelopes. Adjacent records are XORed and gathered into byte planes without interpreting float bits,
/// changing record order or retaining a dictionary between packets. The canonical codec remains the validation and checkpoint format.
/// </summary>
internal static class MotionPayloadCompression
{
    /// <summary>
    /// Distinguishes byte-plane motion from canonical MTN2 packets and the generic absolute envelope.
    /// </summary>
    static ReadOnlySpan<byte> Magic => "MTBP"u8;

    /// <summary>
    /// Four magic bytes, one version byte and the exact canonical length.
    /// </summary>
    const int HeaderBytes = 9;

    /// <summary>
    /// The canonical header and record count stay together before the transposed records.
    /// </summary>
    const int PrefixBytes = MotionDatagramCodec.HeaderBytes + 2;

    /// <summary>
    /// Each record includes its canonical two-byte length.
    /// </summary>
    const int Stride = MotionDatagramCodec.RecordBytes + 2;

    /// <summary>
    /// The largest independently expanded production batch; the canonical datagram and checkpoint limits remain unchanged.
    /// </summary>
    internal const int MaximumRecords = 32;

    /// <summary>
    /// Version 3 and 4 envelopes expand to at most 1,716 bytes, including the canonical header and record count.
    /// </summary>
    internal const int MaximumExpandedBytes = PrefixBytes + MaximumRecords * Stride;

    /// <summary>
    /// Keeps the ordinary encoding when its canonical size fits the MTU, unless byte planes save a further sixteen bytes. A larger batch needs
    /// version 3 or 4 and a compressed wire size within the MTU. Version 4 also predicts DrawYaw from Yaw's bits, only when saving another
    /// sixteen bytes. Null asks the producer to retry with its ordinary record capacity.
    /// </summary>
    internal static byte[]? Encode(byte[] canonical, int maximumBytes)
    {
        byte[] ordinary = AbsolutePayloadCompression.Encode(canonical, minimumBytes: 256);
        byte[]? fallback = canonical.Length <= maximumBytes ? ordinary : null;
        if (canonical.Length < 256) return fallback;

        int capacity = Math.Min(ordinary.Length - 16, maximumBytes) - HeaderBytes;
        if (capacity <= 0) return fallback;

        int count = (canonical.Length - PrefixBytes) / Stride;
        byte[] planes = ArrayPool<byte>.Shared.Rent(canonical.Length);
        byte[] compressed = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            Transpose(canonical, planes.AsSpan(0, canonical.Length), count);
            byte[]? best = fallback;
            if (BrotliEncoder.TryCompress(planes.AsSpan(0, canonical.Length), compressed.AsSpan(0, capacity),
                out int written, quality: 4, window: 22))
                best = Envelope(compressed.AsSpan(0, written), canonical.Length, 3);

            int predictedCapacity = Math.Min((best?.Length ?? ordinary.Length) - 16, maximumBytes) - HeaderBytes;
            if (predictedCapacity <= 0) return best;
            Span<byte> predicted = stackalloc byte[MaximumExpandedBytes];
            canonical.CopyTo(predicted);
            AdjustDrawYaw(predicted[..canonical.Length], count, restore: false);
            Transpose(predicted[..canonical.Length], planes.AsSpan(0, canonical.Length), count);
            if (BrotliEncoder.TryCompress(planes.AsSpan(0, canonical.Length), compressed.AsSpan(0, predictedCapacity),
                out written, quality: 4, window: 22))
                best = Envelope(compressed.AsSpan(0, written), canonical.Length, 4);
            return best;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(planes);
            ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    /// <summary>
    /// Copies one successful bounded candidate before the scratch buffer is reused for another independent encoding.
    /// </summary>
    static byte[] Envelope(ReadOnlySpan<byte> compressed, int canonicalLength, byte version)
    {
        byte[] result = new byte[HeaderBytes + compressed.Length];
        Magic.CopyTo(result);
        result[4] = version;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(5), canonicalLength);
        compressed.CopyTo(result.AsSpan(HeaderBytes));
        return result;
    }

    /// <summary>
    /// Recognizes only the motion-specific envelope; recursive envelopes are not accepted.
    /// </summary>
    internal static bool IsCompressed(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);

    /// <summary>
    /// Bounds both input and expanded canonical size before allocation, consumes exactly one Brotli stream, then reverses the byte planes.
    /// </summary>
    internal static byte[] Expand(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        if (bytes.Length <= HeaderBytes || bytes.Length > maximumBytes || !IsCompressed(bytes) || bytes[4] is not (1 or 2 or 3 or 4))
            throw new InvalidDataException("Invalid packed motion envelope.");

        int expandedLimit = bytes[4] >= 3 ? MaximumExpandedBytes : maximumBytes;
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]);
        if (length < PrefixBytes || length > expandedLimit || (length - PrefixBytes) % Stride != 0)
            throw new InvalidDataException("Invalid expanded motion size.");

        byte[] planes = new byte[length];
        using var decoder = new BrotliDecoder();
        var payload = bytes[HeaderBytes..];
        var status = decoder.Decompress(payload, planes, out int consumed, out int written);
        if (status != OperationStatus.Done || consumed != payload.Length || written != length)
            throw new InvalidDataException("Invalid packed motion stream.");

        int count = (length - PrefixBytes) / Stride;
        if (BinaryPrimitives.ReadUInt16LittleEndian(planes.AsSpan(MotionDatagramCodec.HeaderBytes)) != count)
            throw new InvalidDataException("Packed motion record count does not match its size.");

        byte[] canonical = new byte[length];
        Restore(planes, canonical, count, xor: bytes[4] >= 2);
        if (bytes[4] == 4) AdjustDrawYaw(canonical, count, restore: true);
        return canonical;
    }

    /// <summary>
    /// A packet-local predictor over raw uint32 bits, using wrapping subtraction before XOR and addition after restoration. No floating-point
    /// arithmetic occurs, so signed zero and every allowed DrawYaw NaN payload survive exactly.
    /// </summary>
    static void AdjustDrawYaw(Span<byte> canonical, int count, bool restore)
    {
        for (int record = 0; record < count; record++)
        {
            var value = canonical[(PrefixBytes + record * Stride + sizeof(ushort))..];
            uint yaw = BinaryPrimitives.ReadUInt32LittleEndian(value[40..]);
            uint draw = BinaryPrimitives.ReadUInt32LittleEndian(value[46..]);
            BinaryPrimitives.WriteUInt32LittleEndian(value[46..], restore ? unchecked(draw + yaw) : unchecked(draw - yaw));
        }
    }

    /// <summary>
    /// XORs each record byte with its predecessor and gathers matching positions into one plane; the first record is absolute.
    /// </summary>
    static void Transpose(ReadOnlySpan<byte> canonical, Span<byte> planes, int count)
    {
        canonical[..PrefixBytes].CopyTo(planes);
        for (int field = 0; field < Stride; field++)
            for (int record = 0; record < count; record++)
            {
                int offset = PrefixBytes + record * Stride + field;
                byte value = canonical[offset];
                if (record != 0) value ^= canonical[offset - Stride];
                planes[PrefixBytes + field * count + record] = value;
            }
    }

    /// <summary>
    /// Restores every canonical byte, including NaN payloads and signed zeros, in its original record position.
    /// </summary>
    static void Restore(ReadOnlySpan<byte> planes, Span<byte> canonical, int count, bool xor)
    {
        planes[..PrefixBytes].CopyTo(canonical);
        for (int field = 0; field < Stride; field++)
            for (int record = 0; record < count; record++)
            {
                int offset = PrefixBytes + record * Stride + field;
                byte value = planes[PrefixBytes + field * count + record];
                if (xor && record != 0) value ^= canonical[offset - Stride];
                canonical[offset] = value;
            }
    }
}
