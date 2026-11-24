using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Meitou.Data.Characters;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Networking.VNext;

/// <summary>
/// Independently revisioned definitions and readings of keyed authority visuals. Exact binary floats avoid decimal expansion;
/// a shared definition version prevents a reading from borrowing an unrelated anchor or glow target.
/// </summary>
internal static class ModVisualCodec
{
    /// <summary>
    /// Strict UTF-8 preserves names without silently replacing malformed input.
    /// </summary>
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Three bounded 4096-character names, the owner and fixed geometry fit within one existing critical record.
    /// </summary>
    const int MaximumBytes = 52 * 1024;

    /// <summary>
    /// Version, family, identity and definition version precede every part. The collection independently checks family and identity.
    /// </summary>
    const int HeaderBytes = 18, DefinitionVersionOffset = 10;

    /// <summary>
    /// Encodes exactly one visual family and part. A zero version is an encoder-only draft, finalized after comparing its definition.
    /// </summary>
    internal static byte[] Encode(ModEffectView? effect, ModGlowView? glow, bool definition, ulong definitionVersion)
    {
        if ((effect is null) == (glow is null)) throw new InvalidDataException("Invalid shared visual family.");
        long id = effect?.Id ?? glow!.Id;
        if (id <= 0) throw new InvalidDataException("Invalid shared visual identity.");
        using var stream = new BoundedBuffer(MaximumBytes);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)2);
        writer.Write((byte)(definition ? effect is null ? 2 : 1 : effect is null ? 4 : 3));
        writer.Write(id);
        writer.Write(definitionVersion);
        if (effect is not null)
        {
            if (definition) WriteEffectDefinition(writer, effect);
            else WriteEffectState(writer, effect);
        }
        else
        {
            if (definition) WriteGlowDefinition(writer, glow!);
            else WriteGlowState(writer, glow!);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Rejects drafts, unknown families, trailing bytes and every length before its allocation. The joined recipient value is validated semantically.
    /// </summary>
    internal static ModVisualPart Decode(byte[] bytes)
    {
        ulong version = DefinitionVersion(bytes);
        long id = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(2, 8));
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        try
        {
            stream.Position = HeaderBytes;
            ModVisualPart result = bytes[1] switch
            {
                1 => new(ReadEffectDefinition(reader, id), null, true, version),
                2 => new(null, ReadGlowDefinition(reader, id), true, version),
                3 => new(ReadEffectState(reader, id), null, false, version),
                4 => new(null, ReadGlowState(reader, id), false, version),
                _ => throw new InvalidDataException("Invalid shared visual family."),
            };
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing shared visual bytes.");
            return result;
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("Truncated shared visual.", error);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Invalid shared visual UTF-8.", error);
        }
    }

    /// <summary>
    /// Reads the positive version binding a definition to its readings, without decoding its strings or geometry.
    /// </summary>
    internal static ulong DefinitionVersion(ReadOnlySpan<byte> bytes)
    {
        ValidateHeader(bytes);
        ulong version = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(DefinitionVersionOffset, 8));
        if (version == 0) throw new InvalidDataException("Unversioned shared visual draft.");
        return version;
    }

    /// <summary>
    /// Compares complete definition contents while excluding their locally assigned version. Drafts are allowed for this comparison.
    /// </summary>
    internal static bool SameDefinition(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        ValidateHeader(first);
        ValidateHeader(second);
        if (first[1] > 2 || second[1] > 2) throw new InvalidDataException("Expected shared visual definitions.");
        return first.Length == second.Length && first[..DefinitionVersionOffset].SequenceEqual(second[..DefinitionVersionOffset])
            && first[HeaderBytes..].SequenceEqual(second[HeaderBytes..]);
    }

    /// <summary>
    /// Finalizes an encoder draft without changing any identity or content bytes.
    /// </summary>
    internal static void SetDefinitionVersion(Span<byte> bytes, ulong version)
    {
        ValidateHeader(bytes);
        if (version == 0) throw new InvalidDataException("Invalid shared visual definition version.");
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(DefinitionVersionOffset, 8), version);
    }

    /// <summary>
    /// Bounds every part and checks its fixed header before reading its identity or definition version.
    /// </summary>
    static void ValidateHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes || bytes.Length > MaximumBytes || bytes[0] != 2 || bytes[1] is < 1 or > 4
            || BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(2, 8)) <= 0)
            throw new InvalidDataException("Invalid shared visual header.");
    }

    /// <summary>
    /// Stable effect metadata and its exact anchor are independent of ticking tint and expiry readings.
    /// </summary>
    static void WriteEffectDefinition(BinaryWriter writer, ModEffectView effect)
    {
        WriteName(writer, effect.Owner, 256);
        WriteName(writer, effect.Effect, 4096);
        WriteName(writer, effect.System, 4096);
        WriteAnchor(writer, effect.Anchor);
        writer.Write(effect.Kept);
    }

    /// <summary>
    /// Restores a definition; the matching reading supplies tint and authority clock anchors before publication.
    /// </summary>
    static ModEffectView ReadEffectDefinition(BinaryReader reader, long id) => new(id,
        ReadName(reader, 256) ?? throw new InvalidDataException("Missing shared visual owner."),
        ReadName(reader, 4096) ?? throw new InvalidDataException("Missing shared effect identity."),
        ReadName(reader, 4096) ?? throw new InvalidDataException("Missing shared particle system."),
        ReadAnchor(reader), null, 0, 0, ReadFlag(reader));

    /// <summary>
    /// A complete effect reading carries its optional tint and both authority clock anchors without resending its definition.
    /// </summary>
    static void WriteEffectState(BinaryWriter writer, ModEffectView effect)
    {
        writer.Write(effect.Tint.HasValue);
        if (effect.Tint is { } tint) WriteVector(writer, tint);
        writer.Write(effect.StartedAt);
        writer.Write(effect.ExpiresAt);
    }

    /// <summary>
    /// Unused definition members remain empty until this reading is joined to the exact matching version.
    /// </summary>
    static ModEffectView ReadEffectState(BinaryReader reader, long id)
    {
        Vector3? tint = ReadFlag(reader) ? ReadVector(reader) : null;
        return new(id, string.Empty, string.Empty, string.Empty, default, tint, reader.ReadDouble(), reader.ReadDouble(), false);
    }

    /// <summary>
    /// A glow definition identifies its owner and exact target independently of its fade and colour readings.
    /// </summary>
    static void WriteGlowDefinition(BinaryWriter writer, ModGlowView glow)
    {
        WriteName(writer, glow.Owner, 256);
        writer.Write((byte)glow.Target.Kind);
        WriteCharacter(writer, glow.Target.On);
        writer.Write((int)glow.Target.Slot);
    }

    /// <summary>
    /// Preserves attach-slot integers for ordinary semantic enum validation after the two parts have been joined.
    /// </summary>
    static ModGlowView ReadGlowDefinition(BinaryReader reader, long id)
    {
        string owner = ReadName(reader, 256) ?? throw new InvalidDataException("Missing shared visual owner.");
        var target = new GlowTarget { Kind = (GlowTargetKind)reader.ReadByte(), On = ReadCharacter(reader), Slot = (AttachSlot)reader.ReadInt32() };
        return new(id, owner, target, default, 0, 0, 0);
    }

    /// <summary>
    /// Every glow reading is absolute and retains all float and double bits.
    /// </summary>
    static void WriteGlowState(BinaryWriter writer, ModGlowView glow)
    {
        WriteVector(writer, glow.Colour);
        writer.Write(glow.Strength);
        writer.Write(glow.StartedAt);
        writer.Write(glow.ExpiresAt);
    }

    /// <summary>
    /// Unused owner and target members are supplied only by the matching definition.
    /// </summary>
    static ModGlowView ReadGlowState(BinaryReader reader, long id) =>
        new(id, string.Empty, default, ReadVector(reader), reader.ReadSingle(), reader.ReadDouble(), reader.ReadDouble());

    /// <summary>
    /// All anchor members remain explicit, including nullable transform values and otherwise unused identity fields.
    /// </summary>
    static void WriteAnchor(BinaryWriter writer, EffectAnchor anchor)
    {
        writer.Write((byte)anchor.Kind);
        WriteCharacter(writer, anchor.Character);
        WriteName(writer, anchor.Name, 4096);
        WriteVector(writer, anchor.Offset);
        writer.Write(anchor.Turn.HasValue);
        if (anchor.Turn is { } turn)
        {
            writer.Write(turn.X);
            writer.Write(turn.Y);
            writer.Write(turn.Z);
            writer.Write(turn.W);
        }
        writer.Write(anchor.Stretch.HasValue);
        if (anchor.Stretch is { } stretch) writer.Write(stretch);
    }

    /// <summary>
    /// Restores every transmitted float bit; no quaternion normalization or position quantization occurs on the wire.
    /// </summary>
    static EffectAnchor ReadAnchor(BinaryReader reader)
    {
        var kind = (EffectAnchorKind)reader.ReadByte();
        var character = ReadCharacter(reader);
        string? name = ReadName(reader, 4096);
        var offset = ReadVector(reader);
        Quaternion? turn = ReadFlag(reader) ? new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) : null;
        float? stretch = ReadFlag(reader) ? reader.ReadSingle() : null;
        return new() { Kind = kind, Character = character, Name = name, Offset = offset, Turn = turn, Stretch = stretch };
    }

    /// <summary>
    /// A uint16 UTF-8 length keeps null and empty distinct; all accepted names are below the null sentinel.
    /// </summary>
    static void WriteName(BinaryWriter writer, string? value, int maximumCharacters)
    {
        if (value is null)
        {
            writer.Write(ushort.MaxValue);
            return;
        }
        if (value.Length > maximumCharacters) throw new InvalidDataException("Shared visual name exceeds its bound.");
        byte[] bytes = StrictUtf8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }

    /// <summary>
    /// Character and byte bounds both hold, including multibyte Unicode names.
    /// </summary>
    static string? ReadName(BinaryReader reader, int maximumCharacters)
    {
        int length = reader.ReadUInt16();
        if (length == ushort.MaxValue) return null;
        if (length > maximumCharacters * 4 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid shared visual name length.");
        string value = StrictUtf8.GetString(reader.ReadBytes(length));
        if (value.Length > maximumCharacters) throw new InvalidDataException("Shared visual name exceeds its bound.");
        return value;
    }

    /// <summary>
    /// Presence and booleans accept only their two canonical representations.
    /// </summary>
    static bool ReadFlag(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Invalid shared visual flag."),
    };

    /// <summary>
    /// Writes a generation-scoped character reference without collapsing its sentinel.
    /// </summary>
    static void WriteCharacter(BinaryWriter writer, CharacterId id)
    {
        writer.Write(id.Slot);
        writer.Write(id.Generation);
    }

    /// <summary>
    /// Reads the existing pair of signed character identity integers.
    /// </summary>
    static CharacterId ReadCharacter(BinaryReader reader) => new(reader.ReadInt32(), reader.ReadInt32());

    /// <summary>
    /// Writes three exact IEEE float32 values.
    /// </summary>
    static void WriteVector(BinaryWriter writer, Vector3 vector)
    {
        writer.Write(vector.X);
        writer.Write(vector.Y);
        writer.Write(vector.Z);
    }

    /// <summary>
    /// Reads three exact IEEE float32 values.
    /// </summary>
    static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
