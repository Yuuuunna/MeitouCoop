using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Simulation;

namespace Meitou.Networking;
public sealed partial class WorldViewCodec
{
    // Character identities remain ordinary typed values, including in maps. JSON property names
    // cannot represent the existing Slot/Generation object, so encode these maps as entry arrays.
    sealed record CharacterEntry<T>(CharacterId Character, T Value);
    sealed class CharacterMaps : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<, >) && type.GetGenericArguments()[0] == typeof(CharacterId);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) => (JsonConverter)Activator.CreateInstance(typeof(CharacterMap<>).MakeGenericType(type.GetGenericArguments()[1]))!;
    }

    sealed class CharacterMap<T> : JsonConverter<IReadOnlyDictionary<CharacterId, T>>
    {
        public override IReadOnlyDictionary<CharacterId, T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var entries = JsonSerializer.Deserialize<CharacterEntry<T>[]>(ref reader, options) ?? throw new JsonException("Missing character map.");
            var result = new Dictionary<CharacterId, T>();
            foreach (var entry in entries)
                if (entry is null || entry.Character.Slot < 0 || entry.Character.Generation < 0 || entry.Value is null || !result.TryAdd(entry.Character, entry.Value))
                    throw new JsonException("Invalid or duplicate character map entry.");
            return result;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<CharacterId, T> value, JsonSerializerOptions options)
        {
            if (value.Any(p => p.Key.Slot < 0 || p.Key.Generation < 0 || p.Value is null))
                throw new JsonException("Invalid character map entry.");
            JsonSerializer.Serialize(writer, value.OrderBy(p => p.Key.Slot).ThenBy(p => p.Key.Generation).Select(p => new CharacterEntry<T>(p.Key, p.Value)).ToArray(), options);
        }
    }
}