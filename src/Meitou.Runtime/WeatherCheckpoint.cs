using Meitou.Simulation.Population;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.IO.Compression;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Runtime;

/// <summary>
/// Regional state and bounded compressed effect pools alongside legacy BIOMES; restores detached candidates only.
/// </summary>
internal static class WeatherCheckpoint
{
    const string Key = "meitou regional weather";
    const string EffectsKey = "meitou weather effects";
    const int MaximumCharacters = 8 * 1024 * 1024;
    const int MaximumEffectCharacters = 48 * 1024 * 1024; // below the FCS reader's 64 MiB string bound
    const int MaximumExpandedBytes = 512 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        MaxDepth = 16,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    sealed record Document(int Version, WeatherFrame Frame);
    internal static SimulationWeatherState? Capture(World world, PopulationData data, SaveGame save)
    {
        if (world.Systems.OfType<SimulationWeather>().SingleOrDefault()is not { } weather)
        {
            save.Camera.Record.Strings.Remove(Key);
            save.Camera.Record.Strings.Remove(EffectsKey);
            return null;
        }

        var frame = weather.CaptureWeather();
        frame.Validate();
        if (frame.Time != WeatherTime.FromHours(world.GameHours))
            throw new InvalidOperationException("Weather is not synchronized with the checkpoint clock.");
        if (save.Biomes is { } biomes)
            SaveWeather.Write(biomes, frame.Regions, frame.Time, data.Db);
        return weather.CaptureState();
    }

    // The pools captured above own their arrays. Encoding/compression belongs to the writer,
    // not the world boundary; no live simulation state is read here.
    internal static void Encode(SimulationWeatherState state, SaveGame save)
    {
        var frame = state.Weather;
        string encoded = JsonSerializer.Serialize(new Document(2, frame), Json);
        if (encoded.Length > MaximumCharacters)
            throw new InvalidDataException("Weather checkpoint is too large.");
        using var expanded = new LimitedMemoryStream(MaximumExpandedBytes);
        JsonSerializer.Serialize(expanded, state, Json);
        using var compressed = new LimitedMemoryStream(MaximumEffectCharacters / 4 * 3);
        using (var compressor = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            expanded.Position = 0;
            expanded.CopyTo(compressor);
        }

        save.Camera.Record.Strings[EffectsKey] = Convert.ToBase64String(compressed.GetBuffer(), 0, checked((int)compressed.Length));
        save.Camera.Record.Strings[Key] = encoded;
    }

    internal static void Restore(World candidate, SaveGame save, double hours)
    {
        if (candidate.Systems.OfType<SimulationWeather>().SingleOrDefault()is not { } weather)
            return;
        if (weather.HasWorldOwner)
            throw new InvalidOperationException("Weather restore requires a fresh candidate instance.");
        var now = WeatherTime.FromHours(hours);
        if (save.Camera.Record.Strings.TryGetValue(Key, out string? encoded))
        {
            if (encoded.Length > MaximumCharacters)
                throw new InvalidDataException("Weather checkpoint is too large.");
            Document document;
            try
            {
                ValidateFields(Encoding.UTF8.GetBytes(encoded));
                document = JsonSerializer.Deserialize<Document>(encoded, Json) ?? throw new InvalidDataException("Missing weather checkpoint.");
            }
            catch (JsonException e)
            {
                throw new InvalidDataException("Malformed weather checkpoint.", e);
            }

            if (document.Version is not (1 or 2) || document.Frame.Time != now)
                throw new InvalidDataException("Weather checkpoint version or clock is inconsistent.");
            if (save.Camera.Record.Strings.TryGetValue(EffectsKey, out string? effects))
            {
                if (effects.Length > MaximumEffectCharacters)
                    throw new InvalidDataException("Weather effect checkpoint is too large.");
                SimulationWeatherState state;
                try
                {
                    using var compressed = new MemoryStream(Convert.FromBase64String(effects));
                    using var decompressor = new BrotliStream(compressed, CompressionMode.Decompress);
                    using var expanded = new LimitedMemoryStream(MaximumExpandedBytes);
                    decompressor.CopyTo(expanded);
                    var bytes = expanded.GetBuffer().AsSpan(0, checked((int)expanded.Length));
                    ValidateFields(bytes);
                    state = JsonSerializer.Deserialize<SimulationWeatherState>(bytes, Json) ?? throw new InvalidDataException("Missing weather effects.");
                }
                catch (Exception e)when (e is JsonException or FormatException)
                {
                    throw new InvalidDataException("Malformed weather effects.", e);
                }

                if (state.Weather is null || state.Weather.Time != document.Frame.Time || state.Weather.Enabled != document.Frame.Enabled || state.Weather.Regions is null || !state.Weather.Regions.SequenceEqual(document.Frame.Regions))
                    throw new InvalidDataException("Weather effects and regional checkpoint disagree.");
                weather.RestoreState(state);
            }
            else if (document.Version == 2)
                throw new InvalidDataException("Weather effect checkpoint is missing.");
            else
                weather.RestoreCandidate(document.Frame);
        }
        else if (save.Camera.Record.Strings.ContainsKey(EffectsKey))
            throw new InvalidDataException("Weather effects require a regional checkpoint.");
        else if (save.Biomes is { } biomes && SaveWeather.Read(biomes)is { Count: > 0 } regions)
            weather.Restore(regions, now); // legacy records retain the save loader's index fallback
    }

    static void ValidateFields(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
        var fields = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                fields.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject)
                fields.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !fields.Peek().Add(reader.GetString()!))
                throw new InvalidDataException("Duplicate weather checkpoint field.");
        }
    }

    sealed class LimitedMemoryStream(int maximum) : MemoryStream
    {
        void Check(int count)
        {
            if (count > maximum - Position)
                throw new InvalidDataException($"Weather checkpoint exceeds its {maximum} byte budget.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }
    }
}