using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Networking.VNext;

/// <summary>
/// Absolute skin tints, separated from meshes and resolved pose weights. Every finite float retains its exact bits; no earlier packet is needed.
/// </summary>
internal static class CharacterTintsCodec
{
    /// <summary>
    /// One count byte and, per tint, one flag byte and at most nine float32 values.
    /// </summary>
    const int MaximumBytes = 1 + ModLooks.MaxTints * 37;

    /// <summary>
    /// The ordinary appearance component retains resolved poses but leaves tints to their independently revisioned binary component.
    /// </summary>
    internal static byte[] EncodeAppearance(WorldViewCodec codec, CharacterAppearance? appearance)
    {
        if (appearance is null) return [0];
        var display = codec.CaptureAppearance(appearance) with { Tints = [] };
        return [1, .. JsonComponentCompression.Encode(codec, display)];
    }

    /// <summary>
    /// Reads the base descriptor without invoking any code mod and rejects a second, conflicting source for tint data.
    /// </summary>
    internal static CharacterAppearance? DecodeAppearance(WorldViewCodec codec, byte[] bytes)
    {
        if (bytes.Length == 1 && bytes[0] == 0) return null;
        if (bytes.Length <= 1 || bytes[0] != 1) throw new InvalidDataException("Invalid appearance component.");
        var display = JsonComponentCompression.Decode<CharacterDisplay>(codec, bytes.AsSpan(1));
        if (display.Tints is not { Length: 0 }) throw new InvalidDataException("Unexpected tints in the base appearance component.");
        return CharacterAppearance.FromDisplay(display);
    }

    /// <summary>
    /// Packs area and side into flags and omits only exact default strength, softness and lightness values. The maximum payload is 149 bytes.
    /// </summary>
    internal static byte[] Encode(IReadOnlyList<LookTint> tints)
    {
        if (tints.Count > ModLooks.MaxTints) throw new InvalidDataException("Too many appearance tints.");
        if (tints.Count == 0) return [0];
        using var stream = new MemoryStream(MaximumBytes);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)tints.Count);
        foreach (var tint in tints)
        {
            Validate(tint);
            byte flags = (byte)((int)tint.Area | (int)tint.Side << 1);
            if (!SameBits(tint.Strength, 1)) flags |= 8;
            if (!SameBits(tint.Softness, .2f)) flags |= 16;
            if (!SameBits(tint.MaxLightness, 1)) flags |= 32;
            writer.Write(flags);
            writer.Write(tint.Centre.X);
            writer.Write(tint.Centre.Y);
            writer.Write(tint.Radius);
            writer.Write(tint.Colour.X);
            writer.Write(tint.Colour.Y);
            writer.Write(tint.Colour.Z);
            if ((flags & 8) != 0) writer.Write(tint.Strength);
            if ((flags & 16) != 0) writer.Write(tint.Softness);
            if ((flags & 32) != 0) writer.Write(tint.MaxLightness);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Validates the count, reserved bits, enums, finite values and exact payload length before retaining a tint component.
    /// </summary>
    internal static LookTint[] Decode(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw new InvalidDataException("Invalid appearance tint component size.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        int count = reader.ReadByte();
        if (count > ModLooks.MaxTints) throw new InvalidDataException("Too many appearance tints.");
        var tints = new LookTint[count];
        try
        {
            for (int i = 0; i < count; i++) tints[i] = ReadTint(reader);
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("Truncated appearance tint component.", error);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing appearance tint data.");
        return tints;
    }

    /// <summary>
    /// Restores an individual tint, including omitted defaults, without quantizing or changing floating-point representations.
    /// </summary>
    static LookTint ReadTint(BinaryReader reader)
    {
        byte flags = reader.ReadByte();
        if ((flags & 0xc0) != 0 || ((flags >> 1) & 3) > (int)LookSide.Right)
            throw new InvalidDataException("Invalid appearance tint flags.");
        var tint = new LookTint((LookArea)(flags & 1), new(reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle(),
            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()))
        {
            Side = (LookSide)((flags >> 1) & 3),
            Strength = (flags & 8) != 0 ? reader.ReadSingle() : 1,
            Softness = (flags & 16) != 0 ? reader.ReadSingle() : .2f,
            MaxLightness = (flags & 32) != 0 ? reader.ReadSingle() : 1,
        };
        Validate(tint);
        return tint;
    }

    /// <summary>
    /// Matches the mod appearance contract before decoded data can reach the renderer.
    /// </summary>
    static void Validate(LookTint? tint)
    {
        if (tint is null || !Enum.IsDefined(tint.Area) || !Enum.IsDefined(tint.Side)
            || !float.IsFinite(tint.Centre.X) || !float.IsFinite(tint.Centre.Y) || !float.IsFinite(tint.Radius) || tint.Radius <= 0
            || !float.IsFinite(tint.Colour.X) || !float.IsFinite(tint.Colour.Y) || !float.IsFinite(tint.Colour.Z)
            || !float.IsFinite(tint.Strength) || tint.Strength is < 0 or > 1
            || !float.IsFinite(tint.Softness) || tint.Softness is < 0 or > 1
            || !float.IsFinite(tint.MaxLightness) || tint.MaxLightness is < 0 or > 1)
            throw new InvalidDataException("Invalid appearance tint.");
    }

    /// <summary>
    /// Omitting a default must not change signed-zero or other finite float bits.
    /// </summary>
    static bool SameBits(float left, float right) => BitConverter.SingleToUInt32Bits(left) == BitConverter.SingleToUInt32Bits(right);
}
