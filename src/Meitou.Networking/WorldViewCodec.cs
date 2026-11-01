using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;

namespace Meitou.Networking;

/// <summary>Typed authorized-view encoding. Content records travel as IDs resolved against the compatible local database.</summary>
public sealed partial class WorldViewCodec
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    readonly JsonSerializerOptions json;
    public WorldViewCodec(GameDatabase database)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            for (int i = info.Properties.Count - 1; i >= 0; i--)
                if (info.Properties[i].Set is null) info.Properties.RemoveAt(i);
            if (info.Type == typeof(CharacterSnapshot))
                info.Properties.Single(p => p.Name == nameof(CharacterSnapshot.DrawYaw)).CustomConverter = new SingleNumber(allowNaN: true);
        });
        json = new()
        {
            TypeInfoResolver = resolver, IncludeFields = true, MaxDepth = 48,
            RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        json.Converters.Add(new RecordReference(database));
        json.Converters.Add(new SingleNumber()); json.Converters.Add(new DoubleNumber());
        json.Converters.Add(new Appearance(database));
        json.Converters.Add(new Pose()); json.Converters.Add(new Sets()); json.Converters.Add(new Doors()); json.Converters.Add(new Job());
        json.Converters.Add(new SurveyMap());
    }

    public byte[] Encode(PlayerWorldView view)
    {
        Validate(view);
        using var stream = new BoundedBuffer(MaximumBytes);
        JsonSerializer.Serialize(stream, view, json);
        var bytes = stream.ToArray(); ValidateJson(bytes); return bytes;
    }

    internal byte[] EncodePresentation<T>(T value)
    {
        using var buffer = new BoundedBuffer(WireFrame.MaximumPayload); JsonSerializer.Serialize(buffer, value, json);
        var bytes = buffer.ToArray(); ValidateJson(bytes); return bytes;
    }
    internal T DecodePresentation<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > WireFrame.MaximumPayload) throw new InvalidDataException("Presentation message is too large.");
        try { ValidateJson(bytes); return JsonSerializer.Deserialize<T>(bytes, json) ?? throw new InvalidDataException("Missing presentation message."); }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException or OverflowException)
        { throw new InvalidDataException("Malformed presentation message.", e); }
    }

    public PlayerWorldView Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("World view is too large.");
        try
        {
            ValidateJson(bytes);
            var view = JsonSerializer.Deserialize<PlayerWorldView>(bytes, json) ?? throw new InvalidDataException("Empty world view.");
            Validate(view); return view;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or OverflowException)
        { throw new InvalidDataException("Malformed world view.", e); }
    }

    internal static void ValidateJson(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 48 });
        var objects = new Stack<HashSet<string>>();
        var arrays = new Stack<bool>();
        int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > 2000000) throw new InvalidDataException("Too many state values.");
            if (reader.TokenType == JsonTokenType.Null && arrays.TryPeek(out bool inArray) && inArray)
                throw new InvalidDataException("Null state array element.");
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: objects.Push(new(StringComparer.Ordinal)); arrays.Push(false); break;
                case JsonTokenType.EndObject: objects.Pop(); arrays.Pop(); break;
                case JsonTokenType.StartArray: arrays.Push(true); break;
                case JsonTokenType.EndArray: arrays.Pop(); break;
                case JsonTokenType.PropertyName:
                    if (reader.ValueSpan.Length > 4096 || !objects.Peek().Add(reader.GetString()!)) throw new InvalidDataException("Duplicate or oversized state field.");
                    break;
                case JsonTokenType.String:
                    if (reader.ValueSpan.Length > 65536) throw new InvalidDataException("State text is too long.");
                    break;
            }
        }
    }

    static void Validate(PlayerWorldView view)
    {
        if (view.World == Guid.Empty || view.Player.IsNone || view.ConnectionGeneration == 0 || view.Publication == 0 ||
            !double.IsFinite(view.GameHours) || view.TimeScale is not (0 or 1 or 2 or 5) || view.ActiveFaction < -1 ||
            view.Snapshot is null || view.Snapshot.Tick < 0 || view.Roles is null || view.Factions is null || view.Events is null || view.Events.Floaters is null || view.Events.Sounds is null || view.Events.Speech is null ||
            view.Messages is null || view.Messages.Items is null || view.FactionCatalogue is null)
            throw new InvalidDataException("Invalid view identity.");
        view.Weather?.Validate();
        view.WeatherEffects?.Validate();
        if (!float.IsFinite(view.SimulationStepSeconds) || view.SimulationStepSeconds is <= 0 or > 1 || view.Events.Blood is null || view.Events.Sparks is null)
            throw new InvalidDataException("Invalid simulation presentation timing.");
        if (view.Snapshot.Characters is null || view.Snapshot.Characters.Count > 65536 ||
            view.Snapshot.Characters.Any(c => c is null || c.Id.IsNone || !Finite(c.Position) || !float.IsFinite(c.Yaw) || float.IsInfinity(c.DrawYaw)) ||
            view.Snapshot.Characters.Select(c => c.Id).Distinct().Count() != view.Snapshot.Characters.Count ||
            view.Roles.Any(r => r is null || r.Entity.IsNone) || view.Roles.Select(r => r.Entity).Distinct().Count() != view.Roles.Count ||
            view.Factions.Any(f => f is null || f.Faction < 0 || f.UnlockedBuildings is null || !Enum.IsDefined((SquadFormation)f.Formation)) || view.Factions.Select(f => f.Faction).Distinct().Count() != view.Factions.Count ||
            view.ActiveFaction >= 0 && !view.Factions.Any(f => f.Faction == view.ActiveFaction))
            throw new InvalidDataException("Invalid view records.");
        if (view.FactionCatalogue.Count > 65536 || view.FactionCatalogue.Any(f => f is null || f.Faction < 0 || f.Name is null ||
            f.Name.Length > 4096 || f.RecordId is { Length: > 4096 }) ||
            view.FactionCatalogue.Select(f => f.Faction).Distinct().Count() != view.FactionCatalogue.Count ||
            view.FactionCatalogue.Where(f => f.RecordId is not null).Select(f => f.RecordId).Distinct(StringComparer.Ordinal).Count() != view.FactionCatalogue.Count(f => f.RecordId is not null))
            throw new InvalidDataException("Invalid public faction catalogue.");
        var disclosed = view.FactionCatalogue.Select(f => f.Faction).ToHashSet();
        if (view.Factions.Any(f => f.Standings is null || f.Standings.Count > 65536 || f.Standings.Any(s => s is null || !disclosed.Contains(s.Faction) ||
            !float.IsFinite(s.Relation) || s.Ally && s.Hostile) || f.Standings.Select(s => s.Faction).Distinct().Count() != f.Standings.Count))
            throw new InvalidDataException("Invalid member faction standings.");
        foreach (var faction in view.Factions)
        {
            if (faction.Contracts is null || faction.UpcomingEvents is null || faction.Contracts.Count + faction.UpcomingEvents.Count > 65536 ||
                faction.Contracts.Concat(faction.UpcomingEvents).Any(line => line is null || !Enum.IsDefined(line.Kind) || line.Label is null || line.Value is null ||
                    line.Hours is { } hours && !double.IsFinite(hours))) throw new InvalidDataException("Invalid faction information panel.");
        }
        if (view.Events.Floaters.Count + view.Events.Sounds.Count + view.Events.Speech.Count + view.Events.Blood.Count + view.Events.Sparks.Count > 512 || view.Events.Floaters.Any(e => e is null || e.Id == 0 || e.Id > view.Events.HighWater ||
            !Enum.IsDefined(e.Value.Kind) || !Finite(e.Value.Position) || e.Value.Part is null || e.Value.Part.Length > 4096 ||
            e.Value.Target.IsNone && e.Value.Kind != FloaterKind.Label) ||
            view.Events.Floaters.Select(e => e.Id).Distinct().Count() != view.Events.Floaters.Count)
            throw new InvalidDataException("Invalid presentation event batch.");
        if (view.Events.Sounds.Any(e => e is null || e.Id == 0 || e.Id > view.Events.HighWater || !Enum.IsDefined(e.Value.Kind) ||
            !Finite(e.Value.Position) || e.Value.Item?.Length > 4096))
            throw new InvalidDataException("Invalid sound event batch.");
        if (view.Events.Blood.Any(e => e is null || e.Id == 0 || e.Id > view.Events.HighWater || e.Value.Tick < 0 || e.Value.CorpseTicks < 0 ||
            !float.IsFinite(e.Value.TickSeconds) || e.Value.TickSeconds is <= 0 or > 1 || !Enum.IsDefined(e.Value.Mark.Kind) ||
            !Finite(e.Value.Mark.Position) || !float.IsFinite(e.Value.Mark.Direction.X) || !float.IsFinite(e.Value.Mark.Direction.Y) ||
            !float.IsFinite(e.Value.Mark.Amount) || e.Value.Mark.Amount < 0 || e.Value.Mark.Race?.Length > 4096) ||
            view.Events.Sparks.Any(e => e is null || e.Id == 0 || e.Id > view.Events.HighWater || e.Who.IsNone))
            throw new InvalidDataException("Invalid combat visual event batch.");
        if (view.Events.Speech.Any(e => e is null || e.Id == 0 || e.Id > view.Events.HighWater || e.Speaker.IsNone ||
            e.Line.Owner is null || e.Line.Text is null || e.Line.Owner.Length > 256 || e.Line.Text.Length > 4096) ||
            view.Events.Floaters.Select(e => e.Id).Concat(view.Events.Sounds.Select(e => e.Id)).Concat(view.Events.Speech.Select(e => e.Id))
                .Concat(view.Events.Blood.Select(e => e.Id)).Concat(view.Events.Sparks.Select(e => e.Id)).Distinct().Count() !=
                view.Events.Floaters.Count + view.Events.Sounds.Count + view.Events.Speech.Count + view.Events.Blood.Count + view.Events.Sparks.Count)
            throw new InvalidDataException("Invalid speech event batch.");
        if (view.Messages.Items.Count > 200 || view.Messages.Items.Any(m => m is null || m.Id == 0 || m.Id > view.Messages.HighWater ||
            !Enum.IsDefined(m.Kind) || m.Owner is null || m.Text is null || m.Owner.Length > 256 || m.Text.Length > 4096) ||
            view.Messages.Items.Select(m => m.Id).Distinct().Count() != view.Messages.Items.Count)
            throw new InvalidDataException("Invalid private message history.");
        if (view.Prospected is { } report)
        {
            if (report.Serial <= 0 || !Finite(report.Position) || report.Environment is null || report.Resources is null)
                throw new InvalidDataException("Invalid private survey report.");
            if (report.Map is { } map && (map.Size < 1 || map.Heights is null || (long)map.Size * map.Size != map.Heights.Length ||
                map.Extent <= 0 || !float.IsFinite(map.Extent) || !float.IsFinite(map.X) || !float.IsFinite(map.Z) ||
                map.Fields is null || map.Rocks is null || map.Fields.Count != report.Resources.Count || map.Rocks.Count != report.Resources.Count ||
                map.Fields.Any(f => f is not null && f.Length != map.Heights.Length) || map.Rocks.Any(r => r is null)))
                throw new InvalidDataException("Invalid private survey map.");
        }
    }
    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    sealed class SingleNumber(bool allowNaN = false) : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (allowNaN && reader.TokenType == JsonTokenType.String && reader.GetString() == "NaN") return float.NaN;
            float value = reader.GetSingle(); return float.IsFinite(value) ? value : throw new JsonException("Non-finite state number.");
        }
        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
        {
            if (float.IsFinite(value)) writer.WriteNumberValue(value);
            else if (allowNaN && float.IsNaN(value)) writer.WriteStringValue("NaN");
            else throw new JsonException("Non-finite state number.");
        }
    }
    sealed class DoubleNumber : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            double value = reader.GetDouble(); return double.IsFinite(value) ? value : throw new JsonException("Non-finite state number.");
        }
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (!double.IsFinite(value)) throw new JsonException("Non-finite state number."); writer.WriteNumberValue(value);
        }
    }

    sealed class RecordReference(GameDatabase database) : JsonConverter<GameRecord>
    {
        public override GameRecord Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.String && reader.GetString() is { } id && database.Find(id) is { } record
                ? record : throw new JsonException("Missing compatible content record.");
        public override void Write(Utf8JsonWriter writer, GameRecord value, JsonSerializerOptions options) => writer.WriteStringValue(value.StringId);
    }
    sealed class Appearance(GameDatabase database) : JsonConverter<CharacterAppearance>
    {
        public override CharacterAppearance Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => CharacterAppearance.FromDisplay(JsonSerializer.Deserialize<CharacterDisplay>(ref reader, options) ?? throw new JsonException("Missing display."));
        public override void Write(Utf8JsonWriter writer, CharacterAppearance value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.CaptureDisplay(database), options);
    }
    sealed record PoseData(bool[] Set, Quaternion[] Rotation, Vector3[] Position, float Weight);
    sealed class Pose : JsonConverter<BonePose>
    {
        public override BonePose Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var d = JsonSerializer.Deserialize<PoseData>(ref reader, options) ?? throw new JsonException("Missing pose.");
            if (d.Set.Length > 1024 || d.Rotation.Length != d.Set.Length || d.Position.Length != d.Set.Length || !float.IsFinite(d.Weight)) throw new JsonException("Invalid pose.");
            var pose = new BonePose(d.Set.Length) { Weight = d.Weight };
            d.Set.CopyTo(pose.Set, 0); d.Rotation.CopyTo(pose.Rotation, 0); d.Position.CopyTo(pose.Position, 0); return pose;
        }
        public override void Write(Utf8JsonWriter writer, BonePose value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new PoseData(value.Set, value.Rotation, value.Position, value.Weight), options);
    }
    sealed class Sets : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(Set<>).MakeGenericType(type.GetGenericArguments()))!;
    }
    sealed class Set<T> : JsonConverter<IReadOnlySet<T>>
    {
        public override IReadOnlySet<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var array = JsonSerializer.Deserialize<T[]>(ref reader, options) ?? throw new JsonException("Missing set.");
            var set = array.ToHashSet();
            if (set.Count != array.Length || array.Any(v => v is null)) throw new JsonException("Invalid set.");
            return set;
        }
        public override void Write(Utf8JsonWriter writer, IReadOnlySet<T> value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
    sealed record DoorEntry(string Building, int Door, DoorView State);
    sealed class Doors : JsonConverter<IReadOnlyDictionary<(string Building, int Door), DoorView>>
    {
        public override IReadOnlyDictionary<(string Building, int Door), DoorView> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var entries = JsonSerializer.Deserialize<DoorEntry[]>(ref reader, options) ?? throw new JsonException("Missing doors.");
            var result = new Dictionary<(string, int), DoorView>();
            foreach (var e in entries)
                if (e is null || !result.TryAdd((e.Building, e.Door), e.State)) throw new JsonException("Duplicate door.");
            return result;
        }
        public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<(string Building, int Door), DoorView> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.OrderBy(p => p.Key.Building, StringComparer.Ordinal).ThenBy(p => p.Key.Door)
                .Select(p => new DoorEntry(p.Key.Building, p.Key.Door, p.Value)).ToArray(), options);
    }
    sealed record JobData(TaskType Type, string? Site, Hand Saved, CharacterId Character);
    sealed class Job : JsonConverter<PermaJob>
    {
        public override PermaJob Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var d = JsonSerializer.Deserialize<JobData>(ref reader, options) ?? throw new JsonException("Missing job.");
            return new(d.Type, d.Site, d.Saved) { Character = d.Character };
        }
        public override void Write(Utf8JsonWriter writer, PermaJob value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new JobData(value.Type, value.Site, value.Saved, value.Character), options);
    }
}

internal sealed class BoundedBuffer(int maximum) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Length + count > maximum) throw new InvalidDataException("Encoded state exceeds its byte limit.");
        base.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Length + buffer.Length > maximum) throw new InvalidDataException("Encoded state exceeds its byte limit.");
        // Avoid MemoryStream's span overload forwarding to this override again.
        var bytes = buffer.ToArray(); base.Write(bytes, 0, bytes.Length);
    }
}
