using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Production;

namespace Meitou.Networking.VNext;

/// <summary>
/// Bounded, absolute presentation values for code mods and the inspected power consumer. Unchanged values use the existing component ledger.
/// </summary>
internal static class ModPresentationCodec
{
    /// <summary>
    /// Registration-order levels share the simulation's registration limit, also used by the client stats panel.
    /// </summary>
    internal const int MaximumSkills = ModSkills.Max;

    /// <summary>
    /// One visible character's attachment declarations; bone matrices remain local animation output.
    /// </summary>
    internal const int MaximumBoneMeshes = BoneMesh.MaxPerCharacter;

    /// <summary>
    /// Attachment keys, asset paths and skeleton bone names have separate UTF-8 limits.
    /// </summary>
    internal const int MaximumBoneKeyBytes = BoneMesh.MaxKeyBytes, MaximumMeshPathBytes = BoneMesh.MaxMeshBytes, MaximumBoneNameBytes = BoneMesh.MaxBoneBytes;

    /// <summary>
    /// UTF-8 byte limits for a meter's value, arrows and explanatory text.
    /// </summary>
    internal const int MaximumValueBytes = ModMeters.MaxValueBytes, MaximumArrowBytes = ModMeters.MaxArrowsBytes,
        MaximumTooltipBytes = ModMeters.MaxTooltipBytes;

    /// <summary>
    /// UTF-8 identifiers follow the existing content-identity bound.
    /// </summary>
    internal const int MaximumIdentityBytes = 4096;

    /// <summary>
    /// Invalid UTF-8 is rejected rather than replaced differently across clients.
    /// </summary>
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// An absolute bar component and the exact tooltip table it references. A missing table never supplies a stale tooltip.
    /// </summary>
    internal sealed record MeterBars(IReadOnlyList<MeterReading?> Values, byte[] TooltipDigest);

    /// <summary>
    /// A tooltip slot distinguishes a hidden meter from a visible meter without explanatory text.
    /// </summary>
    internal readonly record struct MeterTooltip(bool Present, string? Text);

    /// <summary>
    /// A complete tooltip table, bounded to four slots and identified by the SHA256 of its encoded bytes.
    /// </summary>
    internal sealed record MeterTooltips(IReadOnlyList<MeterTooltip> Slots, byte[] Digest);

    /// <summary>
    /// One count byte, then a 32-byte tooltip-table digest when nonempty, then a presence byte per slot. A present slot carries three floats
    /// and the u16-length value and arrow strings. The explanatory text is a separately revisioned, absolute component.
    /// </summary>
    internal static byte[] EncodeMeterBars(IReadOnlyList<MeterReading?> values, byte[]? tooltipTable = null)
    {
        ValidateMeters(values);
        if (values.Count == 0) return [0];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)values.Count);
        writer.Write(SHA256.HashData(tooltipTable ?? EncodeMeterTooltips(values)));
        foreach (var value in values)
        {
            writer.Write((byte)(value.HasValue ? 1 : 0));
            if (value is not { } meter) continue;

            writer.Write(meter.Green);
            writer.Write(meter.Yellow);
            writer.Write(meter.Red);
            WriteText(writer, meter.Value);
            WriteText(writer, meter.Arrows);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Bounds counts and strings before allocation and retains the exact tooltip-table reference alongside the absolute dynamic values.
    /// </summary>
    internal static MeterBars DecodeMeterBars(byte[] bytes)
    {
        if (bytes.Length == 1 && bytes[0] == 0) return new([], []);
        if (bytes.Length < 1 || bytes[0] > ModMeters.Max) throw new InvalidDataException("Invalid mod meter count.");
        if (bytes[0] == 0 || bytes.Length < 33 + bytes[0]) throw new InvalidDataException("Invalid mod meter component length.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        var values = new MeterReading?[reader.ReadByte()];
        byte[] tooltipDigest = reader.ReadBytes(32);
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                byte present = reader.ReadByte();
                if (present > 1) throw new InvalidDataException("Invalid mod meter presence.");
                if (present == 0) continue;

                float green = reader.ReadSingle(), yellow = reader.ReadSingle(), red = reader.ReadSingle();
                string value = ReadText(reader, MaximumValueBytes);
                string arrows = ReadText(reader, MaximumArrowBytes);
                values[i] = new(green, value) { Yellow = yellow, Red = red, Arrows = arrows };
            }

            RequireEnd(stream);
            ValidateMeters(values);
            return new(values, tooltipDigest);
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("Truncated mod meter component.", error);
        }
    }

    /// <summary>
    /// A complete tooltip table is sent only when its slot presence or text changes; bar movement never resends static tooltips.
    /// </summary>
    internal static byte[] EncodeMeterTooltips(IReadOnlyList<MeterReading?> values)
    {
        ValidateMeters(values);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)values.Count);
        foreach (var value in values)
        {
            writer.Write((byte)(value.HasValue ? 1 : 0));
            if (value is not { } meter) continue;
            if (meter.Tooltip is null) writer.Write(ushort.MaxValue);
            else WriteText(writer, meter.Tooltip);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Decodes a complete bounded table. It does not apply text until a bar component references this exact table.
    /// </summary>
    internal static MeterTooltips DecodeMeterTooltips(byte[] bytes)
    {
        if (bytes.Length < 1 || bytes[0] > ModMeters.Max) throw new InvalidDataException("Invalid mod meter tooltip count.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        var slots = new MeterTooltip[reader.ReadByte()];
        try
        {
            for (int i = 0; i < slots.Length; i++)
            {
                byte present = reader.ReadByte();
                if (present > 1) throw new InvalidDataException("Invalid mod meter tooltip presence.");
                if (present == 0) continue;

                int count = reader.ReadUInt16();
                string? text = count == ushort.MaxValue ? null : ReadText(reader, MaximumTooltipBytes, count);
                slots[i] = new(true, text);
            }

            RequireEnd(stream);
            return new(slots, SHA256.HashData(bytes));
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("Truncated mod meter tooltip component.", error);
        }
    }

    /// <summary>
    /// Exact little-endian float levels cost 2 + 4N bytes. NaN retains the upstream unavailable-level sentinel; infinity is invalid.
    /// </summary>
    internal static byte[] EncodeSkills(IReadOnlyList<float> values)
    {
        ValidateSkills(values);
        byte[] bytes = new byte[2 + values.Count * 4];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)values.Count);
        for (int i = 0; i < values.Count; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(2 + i * 4), values[i]);
        return bytes;
    }

    /// <summary>
    /// Validates the exact count-derived length before allocating a level array.
    /// </summary>
    internal static IReadOnlyList<float> DecodeSkills(byte[] bytes)
    {
        if (bytes.Length < 2) throw new InvalidDataException("Truncated mod skill component.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (count > MaximumSkills || bytes.Length != 2 + count * 4) throw new InvalidDataException("Invalid mod skill count.");
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(2 + i * 4));
        ValidateSkills(values);
        return values;
    }

    /// <summary>
    /// Static attachment declarations cost one count byte plus 34 bytes and their UTF-8 identities per mesh.
    /// </summary>
    internal static byte[] EncodeBoneMeshes(IReadOnlyList<BoneMesh> values)
    {
        ValidateBoneMeshes(values);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)values.Count);
        foreach (var value in values)
        {
            WriteText(writer, value.Key);
            WriteText(writer, value.Mesh);
            WriteText(writer, value.Bone);
            writer.Write(value.Offset.X);
            writer.Write(value.Offset.Y);
            writer.Write(value.Offset.Z);
            writer.Write(value.Rotation.X);
            writer.Write(value.Rotation.Y);
            writer.Write(value.Rotation.Z);
            writer.Write(value.Rotation.W);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Decodes bounded attachment references and local transforms, never per-frame bone poses.
    /// </summary>
    internal static IReadOnlyList<BoneMesh> DecodeBoneMeshes(byte[] bytes)
    {
        if (bytes.Length < 1 || bytes[0] > MaximumBoneMeshes) throw new InvalidDataException("Invalid bone mesh count.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        var values = new BoneMesh[reader.ReadByte()];
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                string key = ReadText(reader, MaximumBoneKeyBytes);
                string mesh = ReadText(reader, MaximumMeshPathBytes);
                string bone = ReadText(reader, MaximumBoneNameBytes);
                var offset = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                values[i] = new(key, mesh, bone, offset, rotation);
            }

            RequireEnd(stream);
            ValidateBoneMeshes(values);
            return values;
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("Truncated bone mesh component.", error);
        }
    }

    /// <summary>
    /// An absent inspected consumer costs one byte; a present one adds its u16-length identity and two exact floats.
    /// </summary>
    internal static byte[] EncodePower(LevelPowerView? value)
    {
        if (value is not { } power) return [0];
        ValidatePower(power);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        WriteText(writer, power.Instance);
        writer.Write(power.Demand);
        writer.Write(power.Granted);
        return stream.ToArray();
    }

    /// <summary>
    /// Decodes a nullable absolute power readout without depending on independently arriving inspection components.
    /// </summary>
    internal static LevelPowerView? DecodePower(byte[] bytes)
    {
        if (bytes.Length == 1 && bytes[0] == 0) return null;
        if (bytes.Length < 11 || bytes[0] != 1) throw new InvalidDataException("Invalid inspected power component.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        try
        {
            _ = reader.ReadByte();
            string instance = ReadText(reader, MaximumIdentityBytes);
            var value = new LevelPowerView(instance, reader.ReadSingle(), reader.ReadSingle());
            RequireEnd(stream);
            ValidatePower(value);
            return value;
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("Truncated inspected power component.", error);
        }
    }

    /// <summary>
    /// A mod meter has the four-slot upstream limit and finite fractions; unbounded mod text cannot enter the transport.
    /// </summary>
    internal static void ValidateMeters(IReadOnlyList<MeterReading?>? values)
    {
        if (values is null || values.Count > ModMeters.Max) throw new InvalidDataException("Invalid mod meter count.");
        foreach (var value in values)
        {
            if (value is not { } meter) continue;
            if (!Fraction(meter.Green) || !Fraction(meter.Yellow) || !Fraction(meter.Red))
                throw new InvalidDataException("Invalid mod meter fraction.");
            ValidateText(meter.Value, MaximumValueBytes);
            ValidateText(meter.Arrows, MaximumArrowBytes);
            if (meter.Tooltip is not null) ValidateText(meter.Tooltip, MaximumTooltipBytes);
        }
    }

    /// <summary>
    /// Rejects unbounded skill arrays and infinity while preserving intentional NaN levels.
    /// </summary>
    internal static void ValidateSkills(IReadOnlyList<float>? values)
    {
        if (values is null || values.Count > MaximumSkills || values.Any(float.IsInfinity))
            throw new InvalidDataException("Invalid mod skill levels.");
    }

    /// <summary>
    /// Attachments have unique nonempty keys and finite local transforms; they carry references, never meshes or skeletal poses.
    /// </summary>
    internal static void ValidateBoneMeshes(IReadOnlyList<BoneMesh>? values)
    {
        if (values is null || values.Count > MaximumBoneMeshes) throw new InvalidDataException("Invalid bone mesh count.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ValidateText(value.Key, MaximumBoneKeyBytes);
            ValidateText(value.Mesh, MaximumMeshPathBytes);
            ValidateText(value.Bone, MaximumBoneNameBytes);
            if (value.Key.Length == 0 || value.Mesh.Length == 0 || value.Bone.Length == 0 || !keys.Add(value.Key)
                || !float.IsFinite(value.Offset.X) || !float.IsFinite(value.Offset.Y) || !float.IsFinite(value.Offset.Z)
                || !float.IsFinite(value.Rotation.X) || !float.IsFinite(value.Rotation.Y)
                || !float.IsFinite(value.Rotation.Z) || !float.IsFinite(value.Rotation.W))
                throw new InvalidDataException("Invalid bone mesh declaration.");
        }
    }

    /// <summary>
    /// The displayed demand and allocation are nonnegative finite power amounts.
    /// </summary>
    internal static void ValidatePower(LevelPowerView value)
    {
        ValidateText(value.Instance, MaximumIdentityBytes);
        if (value.Instance.Length == 0 || !float.IsFinite(value.Demand) || value.Demand < 0
            || !float.IsFinite(value.Granted) || value.Granted < 0)
            throw new InvalidDataException("Invalid inspected power values.");
    }

    /// <summary>
    /// The upstream meter API defines all three layers as fractions of the bar.
    /// </summary>
    static bool Fraction(float value) => float.IsFinite(value) && value is >= 0 and <= 1;

    /// <summary>
    /// Checks the encoded bound before any wire allocation.
    /// </summary>
    static void ValidateText(string? value, int maximum)
    {
        if (value is null || value.Length > maximum || StrictUtf8.GetByteCount(value) > maximum)
            throw new InvalidDataException("Invalid mod presentation text.");
    }

    /// <summary>
    /// Writes an already validated UTF-8 string with a two-byte length.
    /// </summary>
    static void WriteText(BinaryWriter writer, string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>
    /// Checks both the field cap and the remaining input before allocating string bytes.
    /// </summary>
    static string ReadText(BinaryReader reader, int maximum, int? knownLength = null)
    {
        int count = knownLength ?? reader.ReadUInt16();
        if (count > maximum || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid mod presentation text length.");
        try
        {
            return StrictUtf8.GetString(reader.ReadBytes(count));
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Invalid mod presentation UTF-8.", error);
        }
    }

    /// <summary>
    /// No hidden tail is accepted after a component's declared values.
    /// </summary>
    static void RequireEnd(MemoryStream stream)
    {
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing mod presentation bytes.");
    }
}
