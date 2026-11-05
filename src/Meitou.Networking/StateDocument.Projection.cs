using System.Globalization;
using System.Text.Json;
using Meitou.Simulation;
using Meitou.Data.Characters;
using Meitou.Data.World;
using Meitou.Data.Particles;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;

namespace Meitou.Networking;

internal sealed partial class StateDocument
{
    internal sealed record AppearanceComponent(CharacterId Id, CharacterAppearance Appearance);
    internal sealed record DisplayComponent(CharacterId Id, CharacterDisplay Appearance);
    internal sealed record AppearanceEncoding(CharacterDisplay Display, byte[] Bytes);
    readonly Dictionary<string, AppearanceEncoding> encodedAppearances = new(StringComparer.Ordinal);

    static string ComponentKey(string prefix, JsonElement node) => prefix + (prefix switch
    {
        "c:" or "a:" => FormattableString.Invariant($"{node.GetProperty("Id").GetProperty("Slot").GetInt32()}:{node.GetProperty("Id").GetProperty("Generation").GetInt32()}"),
        "b:" or "e:" => node.GetProperty("Id").GetString(),
        "m:" => node.GetProperty("Site").GetString(),
        "p:" => node.GetProperty("Id").GetInt32().ToString(CultureInfo.InvariantCulture),
        "f:" => node.GetProperty("Faction").GetInt32().ToString(CultureInfo.InvariantCulture),
        "w:" => node.GetProperty("RegionId").GetString(),
        _ => throw new InvalidDataException("Unknown component."),
    });

    // Walk only envelope properties. Large character/item trees remain UTF-8 data rather than
    // thousands of mutable JsonNodes that would immediately be written out and parsed again.
    static byte[] RewriteCollections(JsonElement root, Action<string, JsonElement, Utf8JsonWriter> collection,
        Action<string, JsonElement, Utf8JsonWriter> single)
    {
        using var buffer = new BoundedBuffer(WorldViewCodec.MaximumBytes);
        using (var writer = new Utf8JsonWriter(buffer)) WriteObject(root, "view", writer);
        return buffer.ToArray();

        void WriteObject(JsonElement value, string scope, Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                string? prefix = (scope, property.Name) switch
                {
                    ("Snapshot", "Characters") => "c:", ("Snapshot", "Buildings") => "b:",
                    ("Snapshot", "Machines") => "m:", ("Snapshot", "Projectiles") => "p:",
                    ("view", "FactionCatalogue") => "f:", ("Weather", "Regions") => "w:",
                    ("WeatherEffects", "Nodes") => "e:", _ => null,
                };
                if (prefix is not null) collection(prefix, property.Value, writer);
                else if (scope == "view" && property.Name is "Prospected" or "Messages")
                    single(property.Name == "Prospected" ? "prospect" : "messages", property.Value, writer);
                else if (scope == "view" && property.Name is "Snapshot" or "Weather" or "WeatherEffects" && property.Value.ValueKind != JsonValueKind.Null)
                    WriteObject(property.Value, property.Name, writer);
                else property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
    }

    internal static StateDocument Freeze(WorldViewCodec codec, PlayerWorldView view) => FreezeAgainst(codec, view, null);

    internal static StateDocument FreezeAgainst(WorldViewCodec codec, PlayerWorldView view, StateDocument? baseline)
    {
        WorldViewCodec.Validate(view);
        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        Dictionary<string, AppearanceEncoding> looks = new(StringComparer.Ordinal);
        void Add<T>(string key, T value)
        {
            if (!entries.TryAdd(key, codec.EncodeComponent(value))) throw new InvalidDataException("Duplicate state component.");
        }
        foreach (var c in view.Snapshot.Characters)
        {
            Add(FormattableString.Invariant($"c:{c.Id.Slot}:{c.Id.Generation}"), c with { Appearance = null });
            if (c.Appearance is { } look)
            {
                string key = FormattableString.Invariant($"a:{c.Id.Slot}:{c.Id.Generation}");
                var display = codec.CaptureAppearance(look);
                var encoded = baseline is not null && baseline.encodedAppearances.TryGetValue(key, out var previous) && SameDisplay(display, previous.Display)
                    ? previous : new AppearanceEncoding(display, codec.EncodeComponent(new DisplayComponent(c.Id, display)));
                entries.Add(key, encoded.Bytes); looks.Add(key, encoded);
            }
        }
        foreach (var b in view.Snapshot.Buildings) Add("b:" + b.Id, b);
        foreach (var m in view.Snapshot.Machines) Add("m:" + m.Site, m);
        foreach (var p in view.Snapshot.Projectiles) Add("p:" + p.Id.ToString(CultureInfo.InvariantCulture), p);
        foreach (var f in view.FactionCatalogue) Add("f:" + f.Faction.ToString(CultureInfo.InvariantCulture), f);
        if (view.Weather is { } weather) foreach (var w in weather.Regions) Add("w:" + w.RegionId, w);
        if (view.WeatherEffects is { } effects) foreach (var e in effects.Nodes) Add("e:" + e.Id, e);
        if (view.Prospected is { } prospect) Add("prospect", prospect);
        if (view.Messages.HighWater > 0) Add("messages", view.Messages);
        var shell = view with
        {
            Snapshot = view.Snapshot with { Characters = [], Buildings = [], Machines = [], Projectiles = [] },
            FactionCatalogue = [], Prospected = null, Messages = PlayerMessageBatch.Empty,
            Weather = view.Weather is { } shellWeather ? shellWeather with { Regions = [] } : null,
            WeatherEffects = view.WeatherEffects is { } shellEffects ? shellEffects with { Nodes = [] } : null,
        };
        using var envelope = JsonDocument.Parse(codec.EncodeComponent(shell));
        entries.Add("view", RewriteCollections(envelope.RootElement, (_, value, writer) => value.WriteTo(writer), (_, _, writer) => writer.WriteNullValue()));
        var result = new StateDocument(entries);
        foreach (var pair in looks) result.encodedAppearances.Add(pair.Key, pair.Value);
        return result;
    }

    static bool SameDisplay(CharacterDisplay a, CharacterDisplay b)
        => a.Parts.SequenceEqual(b.Parts) && a.BodyLayers.SequenceEqual(b.BodyLayers) && a.Lights.SequenceEqual(b.Lights)
        && SameMap(a.AttachmentPoints, b.AttachmentPoints) && SameMap(a.PoseWeights, b.PoseWeights)
        && SameMap(a.Shape.Sliders, b.Shape.Sliders) && a.Shape.Missing.SetEquals(b.Shape.Missing)
        && a.Shape with { Sliders = b.Shape.Sliders, Missing = b.Shape.Missing } == b.Shape
        && a with { Parts = b.Parts, BodyLayers = b.BodyLayers, Lights = b.Lights, AttachmentPoints = b.AttachmentPoints,
            PoseWeights = b.PoseWeights, Shape = b.Shape } == b;

    static bool SameMap<T>(IReadOnlyDictionary<string, T> a, IReadOnlyDictionary<string, T> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && EqualityComparer<T>.Default.Equals(p.Value, value));

    internal sealed record Decoded(byte[] Bytes, object Value);

    internal PlayerWorldView Restore(WorldViewCodec codec) => RestoreCached(codec, [], out _);

    internal PlayerWorldView RestoreCached(WorldViewCodec codec, Dictionary<string, Decoded> previous,
        out Dictionary<string, Decoded> next)
    {
        var decoded = new Dictionary<string, Decoded>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(Entries["view"]);
        var root = document.RootElement;
        var expandedCollections = new HashSet<string>(StringComparer.Ordinal);
        var expandedSingles = new HashSet<string>(StringComparer.Ordinal);
        // Decode only the small envelope; components are typed directly and can retain their
        // already-validated objects when their complete authorized bytes are unchanged.
        var shell = RewriteCollections(root, Empty, Single);
        var view = codec.DecodeComponent<PlayerWorldView>(shell);
        var characters = new List<CharacterSnapshot>();
        var buildings = new List<BuildingSnapshot>();
        var machines = new List<MachineSnapshot>();
        var projectiles = new List<ProjectileSnapshot>();
        var factions = new List<FactionIdentityView>();
        var weather = new List<WeatherRegionSnapshot>();
        var effects = new List<EffectNodeSnapshot>();
        foreach (var (key, bytes) in Entries.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (key is "view" or "prospect" or "messages") continue;
            string prefix = key[..2];
            if (!expandedCollections.Contains(prefix == "a:" ? "c:" : prefix)) throw new InvalidDataException("Component has no envelope.");
            previous.TryGetValue(key, out var old);
            Decoded value;
            if (old is not null && (ReferenceEquals(bytes, old.Bytes) || bytes.AsSpan().SequenceEqual(old.Bytes))) value = old;
            else
            {
                using var component = JsonDocument.Parse(bytes);
                if (ComponentKey(prefix, component.RootElement) != key) throw new InvalidDataException("Component identity does not match its key.");
                value = prefix switch
                {
                    "c:" => new(bytes, codec.DecodeComponent<CharacterSnapshot>(bytes)),
                    "a:" => new(bytes, codec.DecodeComponent<AppearanceComponent>(bytes)),
                    "b:" => new(bytes, codec.DecodeComponent<BuildingSnapshot>(bytes)),
                    "m:" => new(bytes, codec.DecodeComponent<MachineSnapshot>(bytes)),
                    "p:" => new(bytes, codec.DecodeComponent<ProjectileSnapshot>(bytes)),
                    "f:" => new(bytes, codec.DecodeComponent<FactionIdentityView>(bytes)),
                    "w:" => new(bytes, codec.DecodeComponent<WeatherRegionSnapshot>(bytes)),
                    "e:" => new(bytes, codec.DecodeComponent<EffectNodeSnapshot>(bytes)),
                    _ => throw new InvalidDataException("Unknown component."),
                };
            }
            decoded.Add(key, value);
            switch (prefix)
            {
                case "c:": characters.Add((CharacterSnapshot)value.Value); break;
                case "b:": buildings.Add((BuildingSnapshot)value.Value); break;
                case "m:": machines.Add((MachineSnapshot)value.Value); break;
                case "p:": projectiles.Add((ProjectileSnapshot)value.Value); break;
                case "f:": factions.Add((FactionIdentityView)value.Value); break;
                case "w:": weather.Add((WeatherRegionSnapshot)value.Value); break;
                case "e:": effects.Add((EffectNodeSnapshot)value.Value); break;
            }
        }
        if (new[] { "c:", "b:", "m:", "p:", "f:" }.Any(p => !expandedCollections.Contains(p)) ||
            new[] { "prospect", "messages" }.Any(k => Entries.ContainsKey(k) && !expandedSingles.Contains(k)))
            throw new InvalidDataException("State shell is missing a component collection.");
        var visible = characters.Select(c => c.Id).ToHashSet();
        foreach (var appearance in decoded.Where(p => p.Key.StartsWith("a:", StringComparison.Ordinal)).Select(p => (AppearanceComponent)p.Value.Value))
            if (!visible.Contains(appearance.Id)) throw new InvalidDataException("Appearance has no visible character.");
        for (int i = 0; i < characters.Count; i++)
        {
            var c = characters[i];
            if (c.Appearance is not null) throw new InvalidDataException("Character contains an embedded appearance.");
            if (decoded.TryGetValue(FormattableString.Invariant($"a:{c.Id.Slot}:{c.Id.Generation}"), out var appearance))
                characters[i] = c with { Appearance = ((AppearanceComponent)appearance.Value).Appearance };
        }
        view = view with
        {
            Snapshot = view.Snapshot with { Characters = characters.ToArray(), Buildings = buildings.ToArray(), Machines = machines.ToArray(), Projectiles = projectiles.ToArray() },
            FactionCatalogue = factions.ToArray(),
            Weather = view.Weather is { } w ? w with { Regions = weather.ToArray() } : null,
            WeatherEffects = view.WeatherEffects is { } e ? e with { Nodes = effects.ToArray() } : null,
        };
        WorldViewCodec.Validate(view);
        next = decoded; // current authorized components only; no hidden historical typed objects
        return view;

        void Empty(string prefix, JsonElement empty, Utf8JsonWriter writer)
        {
            expandedCollections.Add(prefix);
            if (empty.ValueKind != JsonValueKind.Array || empty.GetArrayLength() != 0) throw new InvalidDataException("State shell contains embedded components.");
            writer.WriteStartArray(); writer.WriteEndArray();
        }
        void Single(string key, JsonElement empty, Utf8JsonWriter writer)
        {
            expandedSingles.Add(key);
            if (empty.ValueKind != JsonValueKind.Null) throw new InvalidDataException("State shell contains embedded content.");
            if (Entries.TryGetValue(key, out var bytes)) writer.WriteRawValue(bytes, skipInputValidation: true);
            else if (key == "messages") JsonSerializer.Serialize(writer, PlayerMessageBatch.Empty);
            else writer.WriteNullValue();
        }
    }
}
