using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Simulation.Production;

namespace Meitou.Networking;
public sealed partial class WorldViewCodec
{
    // Rock resources legitimately have no grid. Wrap each optional grid so the generic wire validator
    // can continue rejecting null elements in character, inventory, event and all other arrays.
    sealed record SurveyField(float[]? Samples);
    sealed record SurveyMapWire(int Size, float X, float Z, float Extent, float[] Heights, SurveyField[] Fields, IReadOnlyList<IReadOnlyList<ProspectRock>> Rocks);
    sealed class SurveyMap : JsonConverter<ProspectMap>
    {
        public override ProspectMap Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<SurveyMapWire>(ref reader, options) ?? throw new JsonException("Missing survey map.");
            return new(value.Size, value.X, value.Z, value.Extent, value.Heights, value.Fields.Select(f => f.Samples).ToArray(), value.Rocks);
        }

        public override void Write(Utf8JsonWriter writer, ProspectMap value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, new SurveyMapWire(value.Size, value.X, value.Z, value.Extent, value.Heights, value.Fields.Select(f => new SurveyField(f)).ToArray(), value.Rocks), options);
    }
}