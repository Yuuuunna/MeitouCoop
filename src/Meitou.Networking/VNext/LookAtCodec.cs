using System.Buffers.Binary;
using System.Numerics;

namespace Meitou.Networking.VNext;

/// <summary>
/// A nullable authoritative head target: one absence byte or one presence byte and three exact little-endian floats.
/// </summary>
internal static class LookAtCodec
{
    /// <summary>
    /// Encodes a complete target; unchanged targets remain in the existing component revision ledger.
    /// </summary>
    internal static byte[] Encode(Vector3? value)
    {
        if (value is not { } target) return [0];
        Validate(target);
        byte[] bytes = new byte[13];
        bytes[0] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(1), target.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(5), target.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(9), target.Z);
        return bytes;
    }

    /// <summary>
    /// Requires an exact payload length and finite coordinates before exposing a target to the pose renderer.
    /// </summary>
    internal static Vector3? Decode(byte[] bytes)
    {
        if (bytes.Length == 1 && bytes[0] == 0) return null;
        if (bytes.Length != 13 || bytes[0] != 1) throw new InvalidDataException("Invalid head target component.");
        var target = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(1)),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(5)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(9)));
        Validate(target);
        return target;
    }

    /// <summary>
    /// NaN and infinity cannot participate in head-angle geometry.
    /// </summary>
    internal static void Validate(Vector3 target)
    {
        if (!float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z))
            throw new InvalidDataException("Invalid head target coordinates.");
    }
}
