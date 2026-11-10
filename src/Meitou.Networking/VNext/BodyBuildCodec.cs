using System.Buffers.Binary;
using Meitou.Data.Characters;

namespace Meitou.Networking.VNext;

/// <summary>Compact little-endian IEEE754 wire codec for <see cref="BodyBuild"/>; null encodes as [0].</summary>
public static class BodyBuildCodec
{
    public const int ValueBytes = 21;

    static BodyBuildCodec()
    {
        string[] fields = "Muscle Definition Starvation ShapeMuscle ShapeStarvation".Split(' ');
        if (!fields.Order().SequenceEqual(typeof(BodyBuild).GetProperties().Select(p => p.Name).Order()))
            throw new InvalidOperationException("Build wire schema coverage changed.");
    }

    public static byte[] Encode(BodyBuild? value)
    {
        if (value is not { } build) return [0];
        Validate(build);
        byte[] bytes = new byte[ValueBytes];
        bytes[0] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(1), build.Muscle);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(5), build.Definition);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(9), build.Starvation);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(13), build.ShapeMuscle);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(17), build.ShapeStarvation);
        return bytes;
    }

    public static BodyBuild? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 1 && bytes[0] == 0) return null;
        if (bytes.Length != ValueBytes || bytes[0] != 1)
            throw new InvalidDataException("Invalid build component.");
        var value = new BodyBuild(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[1..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[5..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[13..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[17..]));
        Validate(value);
        return value;
    }

    static void Validate(BodyBuild value)
    {
        if (!float.IsFinite(value.Muscle) || !float.IsFinite(value.Definition) || !float.IsFinite(value.Starvation) ||
            !float.IsFinite(value.ShapeMuscle) || !float.IsFinite(value.ShapeStarvation))
            throw new InvalidDataException("Invalid build values.");
    }
}
