using System.Security.Cryptography;
using System.Text;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;

namespace Meitou.Networking.VNext;

/// <summary>
/// Separate absolute effect definitions and tick-anchored readings. Clock advancement does not retransmit descriptions or countdowns.
/// </summary>
internal static class StatusEffectPresentationCodec
{
    /// <summary>
    /// Decode allocation bounds follow the existing collection and state-text bounds; overflow is rejected, never truncated.
    /// </summary>
    internal const int MaximumEffects = 4096, MaximumDescriptionBytes = 65536;

    /// <summary>
    /// Text must round-trip exactly across peers.
    /// </summary>
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// A complete metadata table and the digest that binds independently arriving readings to this exact order.
    /// </summary>
    internal sealed record Definitions(IReadOnlyList<StatusEffect> Values, byte[] Digest);

    /// <summary>
    /// One effect's authoritative duration and stacks at the enclosing clock anchor.
    /// </summary>
    internal readonly record struct Reading(ushort Definition, double HoursLeft, int Stacks);

    /// <summary>
    /// Tick-based countdowns follow the simulation's effect lifetime, including pause, without relying on wall-clock time.
    /// </summary>
    internal sealed record Times(long Tick, float StepSeconds, byte[] DefinitionDigest, IReadOnlyList<Reading> Values);

    /// <summary>
    /// Validates selected/inspected effect views before worker encoding or exposing decoded JSON.
    /// </summary>
    internal static void Validate(IReadOnlyList<StatusEffectView>? values)
    {
        if (values is null || values.Count > MaximumEffects) throw new InvalidDataException("Invalid status effect count.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            ValidateDefinition(value.Effect);
            if (!ids.Add(value.Effect.Id) || !ValidHours(value.HoursLeft) || value.Stacks < 1 || value.Stacks > value.Effect.MaxStacks)
                throw new InvalidDataException("Invalid status effect reading.");
        }
    }

    /// <summary>
    /// Definition strings and modifiers have the same structural constraints as registered effects, plus explicit wire allocation bounds.
    /// </summary>
    static void ValidateDefinition(StatusEffect? effect)
    {
        if (effect is null || effect.Id is null || effect.Id.Length is 0 or > 48
            || !effect.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')
            || string.IsNullOrWhiteSpace(effect.Name) || effect.Name.Length > 32 || effect.Description is null
            || StrictUtf8.GetByteCount(effect.Description) > MaximumDescriptionBytes
            || !Enum.IsDefined(effect.Tone) || !Enum.IsDefined(effect.Stacking) || effect.MaxStacks < 1
            || effect.Modifiers is null || effect.Modifiers.Count > MaximumEffects
            || effect.Modifiers.Any(m => !Enum.IsDefined(m.Stat) || !float.IsFinite(m.Amount)
                || !float.IsFinite(m.Amount * effect.MaxStacks)))
            throw new InvalidDataException("Invalid status effect definition.");
    }

    /// <summary>
    /// Detaches mutable modifier arrays before an encoding worker or an equality cache retains them.
    /// </summary>
    internal static IReadOnlyList<StatusEffectView> Detach(IReadOnlyList<StatusEffectView> values) => values.Select(value => value with
    {
        Effect = value.Effect with { Modifiers = Array.AsReadOnly(value.Effect.Modifiers.ToArray()) },
    }).ToArray();

    /// <summary>
    /// Compares only metadata; changing stacks or time never changes this component.
    /// </summary>
    internal static bool SameDefinition(StatusEffect a, StatusEffect b) => a.Id == b.Id && a.Name == b.Name
        && a.Description == b.Description && a.Tone == b.Tone && a.Stacking == b.Stacking && a.MaxStacks == b.MaxStacks
        && a.Modifiers.SequenceEqual(b.Modifiers);

    /// <summary>
    /// Cached inactive definitions must fit one metadata component; reaching this bound evicts only inactive cache entries.
    /// </summary>
    internal static bool DefinitionsFit(IReadOnlyList<StatusEffectView> values) => values.Count <= MaximumEffects
        && 2L + values.Sum(value => 20L + StrictUtf8.GetByteCount(value.Effect.Id) + StrictUtf8.GetByteCount(value.Effect.Name)
            + StrictUtf8.GetByteCount(value.Effect.Description) + 8L * value.Effect.Modifiers.Count) <= GameStateWire.MaximumBytes;

    /// <summary>
    /// Complete metadata uses a u16 count, bounded UTF-8 strings and exact modifier values. No gameplay simulation runs on the replica.
    /// </summary>
    internal static byte[] EncodeDefinitions(IReadOnlyList<StatusEffectView> values)
    {
        Validate(values);
        using var stream = new BoundedBuffer(GameStateWire.MaximumBytes);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)values.Count);
        foreach (var value in values) WriteDefinition(writer, value.Effect);
        return AbsolutePayloadCompression.Encode(stream.ToArray());
    }

    /// <summary>
    /// Writes one definition without duplicating countdown state.
    /// </summary>
    static void WriteDefinition(BinaryWriter writer, StatusEffect effect)
    {
        WriteText(writer, effect.Id);
        WriteText(writer, effect.Name);
        WriteText(writer, effect.Description);
        writer.Write((byte)effect.Tone);
        writer.Write((byte)effect.Stacking);
        writer.Write(effect.MaxStacks);
        writer.Write((ushort)effect.Modifiers.Count);
        foreach (var modifier in effect.Modifiers)
        {
            writer.Write((int)modifier.Stat);
            writer.Write(modifier.Amount);
        }
    }

    /// <summary>
    /// Reads definitions under byte/count bounds and rejects duplicate identities before storing the table.
    /// </summary>
    internal static Definitions DecodeDefinitions(byte[] bytes)
    {
        using var stream = new MemoryStream(AbsolutePayloadCompression.Decode(bytes), false);
        using var reader = new BinaryReader(stream);
        try
        {
            int count = reader.ReadUInt16();
            if (count > MaximumEffects || count > (stream.Length - stream.Position) / 20)
                throw new InvalidDataException("Invalid status effect definition count.");
            var effects = new StatusEffect[count];
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < effects.Length; i++)
            {
                effects[i] = ReadDefinition(reader);
                if (!ids.Add(effects[i].Id)) throw new InvalidDataException("Duplicate status effect definition.");
            }

            RequireEnd(stream);
            return new(effects, SHA256.HashData(bytes));
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("Truncated status effect definitions.", error);
        }
    }

    /// <summary>
    /// Allocates a modifier array only after its count fits the remaining payload.
    /// </summary>
    static StatusEffect ReadDefinition(BinaryReader reader)
    {
        string id = ReadText(reader, 48), name = ReadText(reader, 128), description = ReadText(reader, MaximumDescriptionBytes);
        var tone = (StatusEffectTone)reader.ReadByte();
        var stacking = (StatusEffectStacking)reader.ReadByte();
        int stacks = reader.ReadInt32(), count = reader.ReadUInt16();
        if (count > MaximumEffects || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 8)
            throw new InvalidDataException("Invalid status effect modifier count.");
        var modifiers = new StatusEffectModifier[count];
        for (int i = 0; i < count; i++) modifiers[i] = new((StatsEnumerated)reader.ReadInt32(), reader.ReadSingle());
        var effect = new StatusEffect(id, name)
        {
            Description = description, Tone = tone, Stacking = stacking, MaxStacks = stacks,
            Modifiers = Array.AsReadOnly(modifiers),
        };
        ValidateDefinition(effect);
        return effect;
    }

    /// <summary>
    /// A nonempty reading costs 46 + 14N bytes: count, tick, step, metadata digest, then a definition index, exact hours and stacks per effect.
    /// </summary>
    internal static byte[] EncodeTimes(IReadOnlyList<StatusEffectView> values, long tick, float stepSeconds, byte[]? definitions = null,
        IReadOnlyDictionary<string, int>? indices = null)
    {
        Validate(values);
        ValidateClock(tick, stepSeconds);
        if (values.Count == 0) return [0, 0];
        indices ??= values.Select((value, index) => (value.Effect.Id, index)).ToDictionary(p => p.Id, p => p.index, StringComparer.Ordinal);
        using var stream = new MemoryStream(46 + 14 * values.Count);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)values.Count);
        writer.Write(tick);
        writer.Write(stepSeconds);
        writer.Write(SHA256.HashData(definitions ?? EncodeDefinitions(values)));
        foreach (var value in values)
        {
            if (!indices.TryGetValue(value.Effect.Id, out int index) || index < 0 || index >= MaximumEffects)
                throw new InvalidDataException("Status effect definition is missing.");
            writer.Write((ushort)index);
            writer.Write(value.HoursLeft);
            writer.Write(value.Stacks);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Validates the exact count-derived length before allocating readings.
    /// </summary>
    internal static Times DecodeTimes(byte[] bytes)
    {
        if (bytes.Length == 2 && bytes[0] == 0 && bytes[1] == 0) return new(0, 1f / 30, [], []);
        if (bytes.Length < 46) throw new InvalidDataException("Truncated status effect readings.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        int count = reader.ReadUInt16();
        if (count is 0 or > MaximumEffects || bytes.Length != 46 + 14 * count)
            throw new InvalidDataException("Invalid status effect reading count.");
        long tick = reader.ReadInt64();
        float step = reader.ReadSingle();
        ValidateClock(tick, step);
        byte[] digest = reader.ReadBytes(32);
        var values = new Reading[count];
        var seen = new HashSet<ushort>();
        for (int i = 0; i < values.Length; i++)
        {
            ushort definition = reader.ReadUInt16();
            if (definition >= MaximumEffects || !seen.Add(definition)) throw new InvalidDataException("Invalid status effect definition index.");
            double hours = reader.ReadDouble();
            int stacks = reader.ReadInt32();
            if (!ValidHours(hours) || stacks < 1) throw new InvalidDataException("Invalid status effect reading.");
            values[i] = new(definition, hours, stacks);
        }

        return new(tick, step, digest, values);
    }

    /// <summary>
    /// Generic schema application validates the readings but cannot expose them without the separate definitions; the replica joins both.
    /// </summary>
    internal static IReadOnlyList<StatusEffectView> DecodeWithoutDefinitions(byte[] bytes)
    {
        _ = DecodeTimes(bytes);
        return [];
    }

    /// <summary>
    /// Only sub-tick floating-point roundoff is ignored; an actual one-tick lifetime extension or stack change publishes immediately.
    /// </summary>
    internal static bool SameTimes(Times previous, IReadOnlyList<StatusEffectView> values, long tick, float stepSeconds, byte[] definitionDigest,
        IReadOnlyDictionary<string, int> indices)
    {
        if (previous.Values.Count != values.Count) return false;
        if (values.Count == 0) return true;
        if (previous.StepSeconds != stepSeconds || tick < previous.Tick) return false;
        if (!previous.DefinitionDigest.AsSpan().SequenceEqual(definitionDigest)) return false;
        double tolerance = stepSeconds / World.SecondsPerGameHour / 4;
        for (int i = 0; i < values.Count; i++)
        {
            var before = previous.Values[i];
            var current = values[i];
            if (before.Stacks != current.Stacks || !indices.TryGetValue(current.Effect.Id, out int index) || before.Definition != index)
                return false;
            double projected = HoursAt(previous, before, tick);
            if (projected == current.HoursLeft) continue;
            if (!double.IsFinite(projected) || !double.IsFinite(current.HoursLeft) || Math.Abs(projected - current.HoursLeft) > tolerance)
                return false;
        }

        return true;
    }

    /// <summary>
    /// A replica never removes an effect on its own; zero remains visible until authority sends removal.
    /// </summary>
    internal static double HoursAt(Times terms, Reading value, long tick) => double.IsPositiveInfinity(value.HoursLeft) || tick == terms.Tick
        ? value.HoursLeft : Math.Max(0, value.HoursLeft - (tick - (double)terms.Tick) * terms.StepSeconds / World.SecondsPerGameHour);

    /// <summary>
    /// Permanent effects use positive infinity; all finite countdowns are nonnegative.
    /// </summary>
    internal static bool ValidHours(double hours) => hours >= 0 && (double.IsFinite(hours) || double.IsPositiveInfinity(hours));

    /// <summary>
    /// Tick duration is the same finite interval carried in the authoritative publication clock.
    /// </summary>
    static void ValidateClock(long tick, float step)
    {
        if (tick < 0 || !float.IsFinite(step) || step is <= 0 or > 1) throw new InvalidDataException("Invalid status effect clock.");
    }

    /// <summary>
    /// Length-prefixes strict UTF-8 with an explicit four-byte count.
    /// </summary>
    static void WriteText(BinaryWriter writer, string text)
    {
        byte[] bytes = StrictUtf8.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>
    /// Rejects oversized or truncated text before allocating its byte array.
    /// </summary>
    static string ReadText(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid status effect text length.");
        try
        {
            return StrictUtf8.GetString(reader.ReadBytes(count));
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Invalid status effect UTF-8.", error);
        }
    }

    /// <summary>
    /// One component contains exactly one definition table.
    /// </summary>
    static void RequireEnd(Stream stream)
    {
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing status effect metadata.");
    }
}
