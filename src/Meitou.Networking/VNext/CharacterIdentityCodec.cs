using System.Buffers.Binary;
using Meitou.Simulation;

namespace Meitou.Networking.VNext;

/// <summary>
/// Exact fixed-width display identity values; their components change independently of motion.
/// </summary>
internal static class CharacterIdentityCodec
{
    /// <summary>
    /// Encodes the full unsigned lasting ID without converting it through a floating-point JSON number.
    /// </summary>
    internal static byte[] EncodeLastingId(LastingId value)
    {
        byte[] bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value.Value);
        return bytes;
    }

    /// <summary>
    /// Reads exactly one lasting ID, including zero for characters that have never received one.
    /// </summary>
    internal static LastingId DecodeLastingId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != sizeof(ulong)) throw new InvalidDataException("Invalid lasting identity component.");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
    }

    /// <summary>
    /// Encodes the authority's name-tag height without rounding.
    /// </summary>
    internal static byte[] EncodeTagHeight(float value)
    {
        ValidateHeight(value);
        byte[] bytes = new byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>
    /// Reads and validates one name-tag height.
    /// </summary>
    internal static float DecodeTagHeight(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != sizeof(float)) throw new InvalidDataException("Invalid tag height component.");
        float value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
        ValidateHeight(value);
        return value;
    }

    /// <summary>
    /// Tag heights must be finite and nonnegative, including zero for a deliberately hidden tag.
    /// </summary>
    static void ValidateHeight(float value)
    {
        if (!float.IsFinite(value) || value < 0) throw new InvalidDataException("Invalid tag height.");
    }
}
