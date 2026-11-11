using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Data.Fcs;

namespace Meitou.Simulation;

/// <summary>
/// Detached editor intent only. Read-only FCS collections are never serialized as mutable game definitions.
/// </summary>
public sealed class CommandAppearanceJson : JsonConverter<FcsRecord>
{
    sealed record Appearance(string Race, bool Female, Dictionary<string, float> Floats,
        Dictionary<string, Vector3> Colours, Dictionary<string, string> Styles);

    public override FcsRecord Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var value = JsonSerializer.Deserialize<Appearance>(ref reader, options) ?? throw new JsonException("Missing appearance.");
        if (value.Race is null || value.Floats is null || value.Colours is null || value.Styles is null ||
            value.Floats.Count > 512 || value.Colours.Count > 32 || value.Styles.Count > 32)
            throw new JsonException("Invalid appearance intent.");
        var record = new FcsRecord { Type = (int)FcsRecordType.CharacterAppearance };
        record.References["race"] = [new(value.Race, 0, 0, 0)];
        record.Bools["sex female"] = value.Female;
        foreach (var (key, number) in value.Floats)
        {
            if (!float.IsFinite(number)) throw new JsonException("Invalid appearance number.");
            record.Floats[key] = number;
        }
        foreach (var (key, colour) in value.Colours)
        {
            if (!float.IsFinite(colour.X) || !float.IsFinite(colour.Y) || !float.IsFinite(colour.Z)) throw new JsonException("Invalid appearance colour.");
            record.Vector3S[key] = colour;
        }
        foreach (var (key, style) in value.Styles) record.Strings[key] = style;
        return record;
    }

    public override void Write(Utf8JsonWriter writer, FcsRecord value, JsonSerializerOptions options)
    {
        string race = value.References.TryGetValue("race", out var races) ? races.FirstOrDefault().TargetStringId ?? "" : "";
        JsonSerializer.Serialize(writer, new Appearance(race, value.Bools.GetValueOrDefault("sex female"),
            value.Floats.ToDictionary(), value.Vector3S.ToDictionary(), value.Strings.ToDictionary()), options);
    }
}