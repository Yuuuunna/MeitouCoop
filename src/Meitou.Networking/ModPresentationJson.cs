using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Networking.VNext;
using Meitou.Simulation.Bodies;

namespace Meitou.Networking;

/// <summary>
/// The whole-view diagnostic codec preserves hidden meter slots and the mod-skill NaN sentinel without relaxing generic array validation.
/// </summary>
internal static class ModPresentationJson
{
    /// <summary>
    /// A hidden meter is an optional object property, not a null array entry that the general state validator would reject.
    /// </summary>
    internal sealed class Meters : JsonConverter<IReadOnlyList<MeterReading?>>
    {
        /// <summary>
        /// Reads at most four explicitly wrapped slots and preserves their order.
        /// </summary>
        public override IReadOnlyList<MeterReading?> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Invalid mod meter array.");
            var values = new List<MeterReading?>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (values.Count >= ModMeters.Max || reader.TokenType != JsonTokenType.StartObject
                    || !reader.Read() || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("Reading")
                    || !reader.Read()) throw new JsonException("Invalid mod meter slot.");
                var value = JsonSerializer.Deserialize<MeterReading?>(ref reader, options);
                if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Invalid mod meter slot.");
                values.Add(value);
            }

            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Truncated mod meter array.");
            ModPresentationCodec.ValidateMeters(values);
            return values.ToArray();
        }

        /// <summary>
        /// Writes the slot wrapper so generic null-array rejection remains intact for every other state collection.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, IReadOnlyList<MeterReading?> value, JsonSerializerOptions options)
        {
            ModPresentationCodec.ValidateMeters(value);
            writer.WriteStartArray();
            foreach (var reading in value)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("Reading");
                JsonSerializer.Serialize(writer, reading, options);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// Only the ModSkills property accepts the upstream NaN skill sentinel. Other float fields retain finite-only JSON validation.
    /// </summary>
    internal sealed class Skills : JsonConverter<IReadOnlyList<float>>
    {
        /// <summary>
        /// Reads a bounded skill list; infinity and every string other than NaN are rejected.
        /// </summary>
        public override IReadOnlyList<float> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Invalid mod skill array.");
            var values = new List<float>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (values.Count >= ModPresentationCodec.MaximumSkills) throw new JsonException("Too many mod skills.");
                float value;
                if (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("NaN")) value = float.NaN;
                else if (reader.TokenType == JsonTokenType.Number && reader.TryGetSingle(out float number) && float.IsFinite(number)) value = number;
                else throw new JsonException("Invalid mod skill level.");
                values.Add(value);
            }

            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Truncated mod skill array.");
            return values.ToArray();
        }

        /// <summary>
        /// Preserves finite floats and writes a named sentinel for intentional NaN.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, IReadOnlyList<float> value, JsonSerializerOptions options)
        {
            ModPresentationCodec.ValidateSkills(value);
            writer.WriteStartArray();
            foreach (float level in value)
            {
                if (float.IsNaN(level)) writer.WriteStringValue("NaN");
                else writer.WriteNumberValue(level);
            }

            writer.WriteEndArray();
        }
    }
}
