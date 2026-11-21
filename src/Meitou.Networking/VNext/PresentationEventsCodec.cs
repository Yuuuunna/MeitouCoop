using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Simulation;

namespace Meitou.Networking.VNext;

/// <summary>
/// One complete event history, with exact binary sounds and packet-local names. No earlier packet, admission watermark or shared dictionary is
/// needed to decode it; the original JSON size still governs recipient retention and publication accounting.
/// </summary>
internal static class PresentationEventsCodec
{
    /// <summary>
    /// Four magic bytes, a version byte and the original JSON length. The containing field keeps its ordinary presence byte.
    /// </summary>
    static ReadOnlySpan<byte> Magic => "\0MPE"u8;

    /// <summary>
    /// Version one interleaves sound cores and posted references; version two puts XORed sound cores in byte planes before those references.
    /// </summary>
    const byte Version = 1, PlaneVersion = 2;

    /// <summary>
    /// Envelope bytes and the plain body's marker, JSON length, sound count and name count.
    /// </summary>
    const int HeaderBytes = 9, BodyHeaderBytes = 9;

    /// <summary>
    /// An ID, kind, packed flags/armour, two character IDs, three float32 coordinates and an optional item name reference.
    /// Posted sounds additionally carry event, switch group and switch value name references (six bytes).
    /// </summary>
    const int SoundBytes = 40, PostedBytes = 6;

    /// <summary>
    /// Items allow 4096 UTF-16 code units, requiring at most three UTF-8 bytes each; posted names retain their stricter 4096-byte validation.
    /// </summary>
    const int MaximumNameBytes = 3 * 4096;

    /// <summary>
    /// Invalid Unicode cannot silently replace a name with a different one on the receiving client.
    /// </summary>
    static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>
    /// Encodes the entire absolute batch, retaining every event and each finite sound coordinate's exact bits.
    /// </summary>
    internal static byte[] Encode(WorldViewCodec codec, PresentationEventBatch batch)
    {
        PresentationEventValidation.Validate(batch);
        int retained = codec.EncodeBoundedComponent(batch, PresentationEventValidation.MaximumRetainedBytes).Length;
        byte[] others = codec.EncodeBoundedComponent(batch with { Sounds = [] }, retained);
        var names = SoundNames(batch.Sounds);
        byte[] plain = EncodeBody(batch.Sounds, others, names, retained, out int soundOffset);
        byte[] body = AbsolutePayloadCompression.Encode(plain);
        byte version = Version;
        if (batch.Sounds.Count > 1 && plain.Length >= 1024)
        {
            byte[] packed = AbsolutePayloadCompression.Encode(PackSoundPlanes(plain, soundOffset, batch.Sounds));
            if (packed.Length < body.Length)
            {
                body = packed;
                version = PlaneVersion;
            }
        }

        byte[] result = new byte[HeaderBytes + body.Length];
        Magic.CopyTo(result);
        result[4] = version;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(5), retained);
        body.CopyTo(result, HeaderBytes);
        return result;
    }

    /// <summary>
    /// Bounds both the wire and expanded body before decompression, then charges at least the complete original JSON retention size.
    /// </summary>
    internal static int ExpandedSize(ReadOnlySpan<byte> bytes)
    {
        int retained = RetainedLength(bytes);
        var body = bytes[HeaderBytes..];
        if (body[0] != 0 && !AbsolutePayloadCompression.IsCompressed(body))
            throw new InvalidDataException("Invalid presentation event body marker.");
        int expanded = AbsolutePayloadCompression.ExpandedLength(body, retained + BodyHeaderBytes);
        return Math.Max(retained, HeaderBytes + expanded);
    }

    /// <summary>
    /// Reconstructs all five event families, validates their shared ID space, and verifies the declared retention length with a bounded writer.
    /// </summary>
    internal static PresentationEventBatch Decode(WorldViewCodec codec, ReadOnlySpan<byte> bytes)
    {
        _ = ExpandedSize(bytes);
        int retained = RetainedLength(bytes);
        ReadOnlySpan<byte> body = bytes[HeaderBytes..];
        if (AbsolutePayloadCompression.IsCompressed(body))
            body = AbsolutePayloadCompression.Expand(body, retained + BodyHeaderBytes);

        try
        {
            var batch = DecodeBody(codec, body, retained, bytes[4] == PlaneVersion);
            PresentationEventValidation.Validate(batch);
            if (codec.EncodeBoundedComponent(batch, PresentationEventValidation.MaximumRetainedBytes).Length != retained)
                throw new InvalidDataException("Presentation event retention length mismatch.");
            return batch;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException("Malformed presentation events.", error);
        }
    }

    /// <summary>
    /// Rejects invalid envelopes before their claimed size can allocate an output buffer.
    /// </summary>
    static int RetainedLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes + 2 || bytes.Length > HeaderBytes + BodyHeaderBytes + PresentationEventValidation.MaximumRetainedBytes
            || !bytes.StartsWith(Magic) || bytes[4] is not (Version or PlaneVersion))
            throw new InvalidDataException("Invalid presentation event header.");
        int retained = BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]);
        if (retained is <= 0 or > PresentationEventValidation.MaximumRetainedBytes)
            throw new InvalidDataException("Invalid presentation event retention size.");
        return retained;
    }

    /// <summary>
    /// Assigns names in first-use order. Zero denotes null; an empty item name remains a distinct, valid table entry.
    /// </summary>
    static Dictionary<string, ushort> SoundNames(IReadOnlyList<SoundEventEntry> sounds)
    {
        var names = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (var entry in sounds)
        {
            Add(entry.Value.Item);
            if (entry.Value.Posted is not { } posted) continue;
            Add(posted.Event);
            Add(posted.SwitchGroup);
            Add(posted.SwitchValue);
        }
        return names;

        void Add(string? value)
        {
            if (value is not null && !names.ContainsKey(value)) names.Add(value, checked((ushort)(names.Count + 1)));
        }
    }

    /// <summary>
    /// The uncompressed body starts with zero, so it can never be mistaken for an absolute compression envelope.
    /// </summary>
    static byte[] EncodeBody(IReadOnlyList<SoundEventEntry> sounds, byte[] others, Dictionary<string, ushort> names, int retained,
        out int soundOffset)
    {
        long size = BodyHeaderBytes + others.Length + (long)SoundBytes * sounds.Count;
        size += PostedBytes * sounds.Count(entry => entry.Value.Posted is not null);
        foreach (string name in names.Keys) size += sizeof(ushort) + NameLength(name);
        if (size > retained + BodyHeaderBytes) throw new InvalidDataException("Presentation event body exceeds its retention bound.");

        using var stream = new MemoryStream((int)size);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(others.Length);
        writer.Write((ushort)sounds.Count);
        writer.Write((ushort)names.Count);
        writer.Write(others);
        foreach (var name in names.OrderBy(pair => pair.Value).Select(pair => pair.Key))
        {
            byte[] encoded = Utf8.GetBytes(name);
            writer.Write((ushort)encoded.Length);
            writer.Write(encoded);
        }
        soundOffset = checked((int)stream.Position);
        foreach (var entry in sounds) WriteSound(writer, entry, names);
        return stream.ToArray();
    }

    /// <summary>
    /// Preserves the JSON and name table, then gathers each fixed core byte into one plane, XORed with the preceding sound's same byte.
    /// Posted references follow the complete planes in sound order. This only rearranges bytes within this one absolute packet.
    /// </summary>
    static byte[] PackSoundPlanes(byte[] body, int soundOffset, IReadOnlyList<SoundEventEntry> sounds)
    {
        byte[] packed = new byte[body.Length];
        body.AsSpan(0, soundOffset).CopyTo(packed);
        Span<byte> previous = stackalloc byte[SoundBytes];
        previous.Clear();
        int source = soundOffset, postedOffset = soundOffset + sounds.Count * SoundBytes;
        for (int record = 0; record < sounds.Count; record++)
        {
            var core = body.AsSpan(source, SoundBytes);
            for (int field = 0; field < SoundBytes; field++)
                packed[soundOffset + field * sounds.Count + record] = (byte)(core[field] ^ previous[field]);
            core.CopyTo(previous);
            source += SoundBytes;
            if (sounds[record].Value.Posted is null) continue;
            body.AsSpan(source, PostedBytes).CopyTo(packed.AsSpan(postedOffset));
            source += PostedBytes;
            postedOffset += PostedBytes;
        }
        return packed;
    }

    /// <summary>
    /// Validates a name before encoding or sizing it; malformed UTF-16 does not become a replacement-character alias.
    /// </summary>
    static int NameLength(string name)
    {
        try
        {
            int bytes = Utf8.GetByteCount(name);
            if (name.Length > 4096 || bytes > MaximumNameBytes) throw new InvalidDataException("Presentation sound name is too large.");
            return bytes;
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException("Invalid presentation sound name encoding.", error);
        }
    }

    /// <summary>
    /// Packs the five booleans and two armour bits without altering their values; bit seven is reserved.
    /// </summary>
    static void WriteSound(BinaryWriter writer, SoundEventEntry entry, IReadOnlyDictionary<string, ushort> names)
    {
        var value = entry.Value;
        byte flags = (byte)((value.Heavy ? 1 : 0) | (value.AttackerUnarmed ? 2 : 0) | (value.AttackerRobot ? 4 : 0)
            | (value.Armoured ? 8 : 0) | (value.Fatal ? 16 : 0) | (value.Armour << 5));
        writer.Write(entry.Id);
        writer.Write((byte)value.Kind);
        writer.Write(flags);
        writer.Write(value.Subject.Slot);
        writer.Write(value.Subject.Generation);
        writer.Write(value.Other.Slot);
        writer.Write(value.Other.Generation);
        writer.Write(value.Position.X);
        writer.Write(value.Position.Y);
        writer.Write(value.Position.Z);
        writer.Write(value.Item is { } item ? names[item] : (ushort)0);
        if (value.Posted is not { } posted) return;
        writer.Write(names[posted.Event]);
        writer.Write(posted.SwitchGroup is { } group ? names[group] : (ushort)0);
        writer.Write(posted.SwitchValue is { } name ? names[name] : (ushort)0);
    }

    /// <summary>
    /// The counts and fixed record minima are checked before allocating arrays or deserializing the other event families.
    /// </summary>
    static PresentationEventBatch DecodeBody(WorldViewCodec codec, ReadOnlySpan<byte> body, int retained, bool planes)
    {
        var reader = new Reader(body);
        if (reader.Byte() != 0) throw new InvalidDataException("Invalid presentation event body marker.");
        int jsonLength = reader.Int32();
        int count = reader.UInt16(), nameCount = reader.UInt16();
        if (jsonLength <= 0 || jsonLength > retained || count > PresentationEventValidation.MaximumCount || nameCount > count * 4
            || (long)jsonLength + SoundBytes * count + sizeof(ushort) * nameCount > reader.Remaining)
            throw new InvalidDataException("Invalid presentation event body counts.");

        var json = reader.Take(jsonLength);
        WorldViewCodec.ValidateJson(json);
        var others = codec.DecodeComponent<PresentationEventBatch>(json);
        PresentationEventValidation.Validate(others);
        if (others.Sounds.Count != 0 || PresentationEventValidation.Count(others) + count > PresentationEventValidation.MaximumCount)
            throw new InvalidDataException("Invalid presentation event family counts.");

        string[] names = ReadNames(ref reader, nameCount);
        int used = 0;
        var sounds = planes ? ReadSoundPlanes(ref reader, count, names, ref used) : ReadSounds(ref reader, count, names, ref used);
        if (reader.Remaining != 0 || used != names.Length) throw new InvalidDataException("Trailing presentation event data.");
        return others with { Sounds = sounds };
    }

    /// <summary>
    /// Version one's interleaved cores and optional posted references remain independently decodable.
    /// </summary>
    static SoundEventEntry[] ReadSounds(ref Reader reader, int count, string[] names, ref int used)
    {
        var sounds = new SoundEventEntry[count];
        for (int i = 0; i < count; i++)
        {
            var value = ReadSoundCore(ref reader, names, ref used, out ulong id);
            sounds[i] = new(id, ReadPosted(ref reader, value, names, ref used));
        }
        return sounds;
    }

    /// <summary>
    /// Restores one exact core at a time from bounded planes. Name references still run in original first-use order, including posted names.
    /// </summary>
    static SoundEventEntry[] ReadSoundPlanes(ref Reader reader, int count, string[] names, ref int used)
    {
        var planes = reader.Take(count * SoundBytes);
        var sounds = new SoundEventEntry[count];
        Span<byte> core = stackalloc byte[SoundBytes];
        core.Clear();
        for (int record = 0; record < count; record++)
        {
            for (int field = 0; field < SoundBytes; field++) core[field] ^= planes[field * count + record];
            var values = new Reader(core);
            var value = ReadSoundCore(ref values, names, ref used, out ulong id);
            sounds[record] = new(id, ReadPosted(ref reader, value, names, ref used));
        }
        return sounds;
    }

    /// <summary>
    /// Reads bounded, strictly valid UTF-8 names and rejects duplicate table entries.
    /// </summary>
    static string[] ReadNames(ref Reader reader, int count)
    {
        var names = new string[count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int length = reader.UInt16();
            if (length > MaximumNameBytes) throw new InvalidDataException("Presentation sound name is too large.");
            string name = Utf8.GetString(reader.Take(length));
            if (name.Length > 4096 || !seen.Add(name)) throw new InvalidDataException("Invalid presentation sound name table.");
            names[i] = name;
        }
        return names;
    }

    /// <summary>
    /// Reads exact IDs and float bits. The shared event validator checks kind, positions, armour and the complete posted-name contract.
    /// </summary>
    static SoundEvent ReadSoundCore(ref Reader reader, string[] names, ref int used, out ulong id)
    {
        id = reader.UInt64();
        var kind = (SoundKind)reader.Byte();
        byte flags = reader.Byte();
        if ((flags & 0x80) != 0) throw new InvalidDataException("Invalid presentation sound flags.");
        var subject = new CharacterId(reader.Int32(), reader.Int32());
        var other = new CharacterId(reader.Int32(), reader.Int32());
        var position = new Vector3(reader.Single(), reader.Single(), reader.Single());
        string? item = ReadName(ref reader, names, ref used);
        return new SoundEvent(kind, subject, other, position, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0,
            (flags & 8) != 0, (flags & 16) != 0, item, (flags >> 5) & 3);
    }

    /// <summary>
    /// Consumes the three optional posted references from their row (version one) or separate suffix (version two).
    /// </summary>
    static SoundEvent ReadPosted(ref Reader reader, SoundEvent value, string[] names, ref int used)
    {
        if (value.Kind is not (SoundKind.Posted or SoundKind.PostedInterface)) return value;
        string name = ReadName(ref reader, names, ref used) ?? throw new InvalidDataException("Missing presentation sound event name.");
        string? group = ReadName(ref reader, names, ref used);
        string? switchValue = ReadName(ref reader, names, ref used);
        return value with { Posted = new(name, group, switchValue) };
    }

    /// <summary>
    /// Names are referenced in canonical first-use order, and zero alone denotes null.
    /// </summary>
    static string? ReadName(ref Reader reader, string[] names, ref int used)
    {
        int index = reader.UInt16();
        if (index == 0) return null;
        index--;
        if (index >= names.Length || index > used) throw new InvalidDataException("Invalid presentation sound name reference.");
        if (index == used) used++;
        return names[index];
    }

    /// <summary>
    /// Bounds every read without copying the already bounded body into another buffer.
    /// </summary>
    ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        /// <summary>
        /// The unread suffix of this one complete body.
        /// </summary>
        ReadOnlySpan<byte> _bytes = bytes;

        /// <summary>
        /// The exact number of bytes left for the remaining fields.
        /// </summary>
        internal readonly int Remaining => _bytes.Length;

        /// <summary>
        /// Consumes one bounded slice; truncated values fail before they are read or allocated.
        /// </summary>
        internal ReadOnlySpan<byte> Take(int count)
        {
            if ((uint)count > (uint)_bytes.Length) throw new InvalidDataException("Truncated presentation events.");
            var value = _bytes[..count];
            _bytes = _bytes[count..];
            return value;
        }

        /// <summary>
        /// One unsigned byte.
        /// </summary>
        internal byte Byte() => Take(1)[0];

        /// <summary>
        /// One little-endian table reference or count.
        /// </summary>
        internal ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));

        /// <summary>
        /// One little-endian character component or length.
        /// </summary>
        internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));

        /// <summary>
        /// One full unsigned event identity.
        /// </summary>
        internal ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(sizeof(ulong)));

        /// <summary>
        /// One exact IEEE-754 float32 value.
        /// </summary>
        internal float Single() => BinaryPrimitives.ReadSingleLittleEndian(Take(sizeof(float)));
    }
}
