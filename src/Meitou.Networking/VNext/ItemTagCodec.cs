using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Simulation.Items;

namespace Meitou.Networking.VNext;

/// <summary>
/// Exact code-unit encoding: JSON's replacement of isolated UTF-16 surrogates must never change a mod tag's identity.
/// </summary>
internal static class ItemTagCodec
{
    /// <summary>
    /// The format byte, fixed digest and code-unit count preceding the little-endian UTF-16 body.
    /// </summary>
    internal const int HeaderBytes = 21;

    /// <summary>
    /// Immutable tag strings share their encoded definition until no published snapshot references them.
    /// </summary>
    static readonly ConditionalWeakTable<string, byte[]> Encoded = new();

    /// <summary>
    /// Encodes a definition after verifying both its identity and its existing world-view allocation bound.
    /// </summary>
    internal static byte[] Encode(ItemTagView tag)
    {
        Validate(tag);
        return Encoded.GetValue(tag.Value, value =>
        {
            var bytes = new byte[checked(HeaderBytes + value.Length * 2)];
            bytes[0] = 1;
            Convert.FromHexString(ItemTagIdentity.Digest(value)).CopyTo(bytes, 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(17), value.Length);
            for (int i = 0; i < value.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(HeaderBytes + i * 2), value[i]);
            return bytes;
        });
    }

    /// <summary>
    /// Decodes raw code units without Unicode normalization or fallback, then verifies the transmitted digest.
    /// </summary>
    internal static ItemTagView Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes || bytes.Length > WorldViewCodec.MaximumBytes || bytes[0] != 1)
            throw new InvalidDataException("Invalid item tag definition.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[17..]);
        if (count <= 0 || count > (WorldViewCodec.MaximumBytes - HeaderBytes) / 2 || bytes.Length != HeaderBytes + count * 2)
            throw new InvalidDataException("Invalid item tag length.");

        var chars = new char[count];
        for (int i = 0; i < count; i++)
            chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[(HeaderBytes + i * 2)..]);
        var tag = new ItemTagView(Convert.ToHexString(bytes.Slice(1, 16)), new string(chars));
        Validate(tag);
        return tag;
    }

    /// <summary>
    /// The same identity and byte checks apply to typed authority views and decoded definitions.
    /// </summary>
    internal static void Validate(ItemTagView tag)
    {
        if (tag.Value is null || tag.Value.Length == 0 || tag.Value.Length > (WorldViewCodec.MaximumBytes - HeaderBytes) / 2
            || !ValidDigest(tag.Digest) || tag.Digest != ItemTagIdentity.Digest(tag.Value))
            throw new InvalidDataException("Invalid item tag identity.");
    }

    /// <summary>
    /// Canonical uppercase hex also determines the definition's membership bucket.
    /// </summary>
    internal static bool ValidDigest(string digest) => digest is { Length: ItemTagIdentity.DigestCharacters }
        && digest.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    /// <summary>
    /// Legacy full-view JSON carries bounded base64 chunks, preserving code units and the JSON string-token limit.
    /// </summary>
    internal sealed class FullJson : JsonConverter<ItemTagView>
    {
        /// <summary>
        /// A 48 KiB chunk has at most 65,536 base64 characters, matching the shared JSON token bound.
        /// </summary>
        const int ChunkBytes = 48 * 1024;

        /// <summary>
        /// Reads the bounded sequence into the same binary decoder used by keyed definitions.
        /// </summary>
        public override ItemTagView Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Invalid item tag chunks.");
            using var stream = new MemoryStream();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String || !reader.TryGetBytesFromBase64(out var bytes)
                    || bytes.Length == 0 || bytes.Length > ChunkBytes || stream.Length + bytes.Length > WorldViewCodec.MaximumBytes)
                    throw new JsonException("Invalid item tag chunk.");
                stream.Write(bytes);
            }
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Incomplete item tag chunks.");
            return Decode(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        }

        /// <summary>
        /// Writes the byte-identical definition without passing its tag through JSON string normalization.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, ItemTagView value, JsonSerializerOptions options)
        {
            var bytes = Encode(value);
            writer.WriteStartArray();
            for (int offset = 0; offset < bytes.Length; offset += ChunkBytes)
                writer.WriteBase64StringValue(bytes.AsSpan(offset, Math.Min(ChunkBytes, bytes.Length - offset)));
            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// An inline value could bypass definition withdrawal; item payloads accept only the empty legacy default.
    /// </summary>
    internal sealed class NoInlineTag : JsonConverter<string>
    {
        /// <summary>
        /// Rejects a second, untracked tag channel in any decoded item.
        /// </summary>
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && reader.GetString() == "" ? ""
                : throw new JsonException("Item tags require a shared definition.");

        /// <summary>
        /// Inline tags are suppressed by the containing property's serialization policy.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue("");
    }
}
