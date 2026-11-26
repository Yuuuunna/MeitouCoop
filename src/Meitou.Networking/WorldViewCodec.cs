using Meitou.Data.Physics;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;
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
using Meitou.Networking.VNext;

namespace Meitou.Networking;

/// <summary>
/// Typed authorized-view encoding. Content records travel as IDs resolved against the compatible local database.
/// </summary>
public sealed partial class WorldViewCodec
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    readonly JsonSerializerOptions _json;
    readonly GameDatabase _database;
    public WorldViewCodec(GameDatabase database)
    {
        this._database = database;
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(CharacterSnapshot))
                foreach (var property in info.Properties)
                    if (property.Set is null && typeof(CharacterSnapshot).GetProperty(property.Name)?.SetMethod is { } setter)
                        property.Set = (instance, value) => setter.Invoke(instance, [value]);
            for (int i = info.Properties.Count - 1; i >= 0; i--)
                if (info.Properties[i].Set is null)
                    info.Properties.RemoveAt(i);
            if (info.Type == typeof(InventoryItemView) || info.Type == typeof(Meitou.Simulation.Trading.TradeItemView)
                || info.Type == typeof(Meitou.Simulation.Items.InventoryItemToken)
                || info.Type == typeof(Meitou.Simulation.Items.GroundItemSnapshot)
                || info.Type == typeof(Meitou.Simulation.Items.HeldItemView))
            {
                if (info.Type == typeof(InventoryItemView))
                    info.Properties.Single(p => p.Name == nameof(InventoryItemView.StealChance)).ShouldSerialize = (_, value) => value is not null;
                if (info.Type == typeof(InventoryItemView) || info.Type == typeof(Meitou.Simulation.Trading.TradeItemView))
                    info.Properties.Single(p => p.Name == nameof(InventoryItemView.StolenOwner)).ShouldSerialize = (_, value) => value is not null;
                info.Properties.Single(p => p.Name == nameof(InventoryItemView.ModTag)).ShouldSerialize = (_, _) => false;
                info.Properties.Single(p => p.Name == nameof(InventoryItemView.ModTag)).CustomConverter = new ItemTagCodec.NoInlineTag();
                var tag = info.Properties.Single(p => p.Name == nameof(InventoryItemView.ModTagDigest));
                tag.Get = value => value switch
                {
                    InventoryItemView item => !string.IsNullOrEmpty(item.ModTag) ? Meitou.Simulation.Items.ItemTagIdentity.Digest(item.ModTag) : item.ModTagDigest ?? "",
                    Meitou.Simulation.Trading.TradeItemView item => !string.IsNullOrEmpty(item.ModTag) ? Meitou.Simulation.Items.ItemTagIdentity.Digest(item.ModTag) : item.ModTagDigest ?? "",
                    Meitou.Simulation.Items.InventoryItemToken item => !string.IsNullOrEmpty(item.ModTag) ? Meitou.Simulation.Items.ItemTagIdentity.Digest(item.ModTag) : item.ModTagDigest ?? "",
                    Meitou.Simulation.Items.GroundItemSnapshot item => !string.IsNullOrEmpty(item.ModTag) ? Meitou.Simulation.Items.ItemTagIdentity.Digest(item.ModTag) : item.ModTagDigest ?? "",
                    Meitou.Simulation.Items.HeldItemView item => !string.IsNullOrEmpty(item.ModTag) ? Meitou.Simulation.Items.ItemTagIdentity.Digest(item.ModTag) : item.ModTagDigest ?? "",
                    _ => "",
                };
                tag.ShouldSerialize = (_, value) => value is string { Length: > 0 };
                tag.CustomConverter = new ItemTagDigest();
            }
            if (info.Type == typeof(Meitou.Simulation.Bodies.StatusEffectView))
                info.Properties.Single(p => p.Name == nameof(Meitou.Simulation.Bodies.StatusEffectView.HoursLeft)).CustomConverter = new EffectHours();
            if (info.Type == typeof(SoundEvent))
                info.Properties.Single(p => p.Name == nameof(SoundEvent.Posted)).ShouldSerialize = (_, value) => value is not null;
            if (info.Type == typeof(CharacterSnapshot))
            {
                info.Properties.Single(p => p.Name == nameof(CharacterSnapshot.DrawYaw)).CustomConverter = new SingleNumber(allowNaN: true);
                info.Properties.Single(p => p.Name == nameof(CharacterSnapshot.ModSkills)).CustomConverter = new ModPresentationJson.Skills();
                // Entries replace whole characters, so omitted optional fields reset to their
                // declared defaults. Do not serialize dozens of absent HUD fields for every NPC.
                var defaults = new CharacterSnapshot(default, null, default, 0, []);
                foreach (var property in info.Properties)
                {
                    if (property.Name is nameof(CharacterSnapshot.Id) or nameof(CharacterSnapshot.Appearance) or nameof(CharacterSnapshot.Position) or nameof(CharacterSnapshot.Yaw) or nameof(CharacterSnapshot.Animations))
                        continue;
                    var defaultValue = property.Get!(defaults);
                    property.ShouldSerialize = (_, value) => !Equals(value, defaultValue) && !(value is System.Collections.ICollection { Count: 0 } && defaultValue is System.Collections.ICollection { Count: 0 });
                }
            }
        });
        _json = new()
        {
            TypeInfoResolver = resolver,
            IncludeFields = true,
            MaxDepth = 48,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        _json.Converters.Add(new ItemTagCodec.FullJson());
        _json.Converters.Add(new RecordReference(database));
        _json.Converters.Add(new TownBuildingDisplay(database));
        _json.Converters.Add(new SingleNumber());
        _json.Converters.Add(new DoubleNumber());
        _json.Converters.Add(new Appearance(database));
        _json.Converters.Add(new Pose());
        _json.Converters.Add(new Sets());
        _json.Converters.Add(new Doors());
        _json.Converters.Add(new Job());
        _json.Converters.Add(new SurveyMap());
        _json.Converters.Add(new CharacterMaps());
        _json.Converters.Add(new ModPresentationJson.Meters());
    }

    // Mutable presentation objects are detached on the owner before worker encoding. The recipient's
    // ragdoll cache supplies owned poses and reuses them only while their transmitted values are unchanged.
    internal PlayerWorldView Detach(PlayerWorldView view, Func<CharacterSnapshot, BonePose?> detachRagdoll) => view with
    {
        Snapshot = view.Snapshot with
        {
            Characters = view.Snapshot.Characters.Select(c => c with
            {
                Animations = c.Animations.ToArray(),
                Ragdoll = detachRagdoll(c),
                Appearance = c.Appearance is { } look ? CharacterAppearance.FromDisplay(CaptureAppearance(look)) : null,
                Turret = c.Turret is { } turret ? turret with { Angles = turret.Angles?.ToArray() } : null,
                Meters = c.Meters.ToArray(),
                ModSkills = c.ModSkills.ToArray(),
                Effects = StatusEffectPresentationCodec.Detach(c.Effects),
                BoneMeshes = c.BoneMeshes.ToArray(),
            }).ToArray()
        },
    };

    sealed record TownDisplay(string Instance, string Record, string Name, Meitou.Data.World.BuildingPlacement Placement, bool Destroyed, string? Owner, string Interior, string Exterior, Meitou.Data.World.BuildingNaming? Naming);
    sealed class TownBuildingDisplay(GameDatabase database) : JsonConverter<Meitou.Data.World.TownBuilding>
    {
        public override Meitou.Data.World.TownBuilding Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var d = JsonSerializer.Deserialize<TownDisplay>(ref reader, options) ?? throw new JsonException("Missing town building display.");
            var record = database.Find(d.Record) ?? throw new JsonException("Missing building content.");
            var p = d.Placement;
            var position = p.Position with
            {
                Y = p.WorldY ?? p.Position.Y
            };
            return new(d.Instance, d.Record, d.Name, position, 2 * MathF.Atan2(p.Rotation.Y, p.Rotation.W), false, false, [])
            {
                Zone = p.Zone,
                OwnerFactionId = d.Owner,
                Destroyed = d.Destroyed,
                Naming = d.Naming,
                InteriorLayout = d.Interior,
                LevelLayouts = (d.Interior, d.Exterior),
                Resolved = new(p, record, position, d.Destroyed),
            };
        }

        public override void Write(Utf8JsonWriter writer, Meitou.Data.World.TownBuilding value, JsonSerializerOptions options)
        {
            var placement = value.Resolved?.Placement ?? new(value.Zone, value.InstanceId, value.BuildingId, value.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, value.Yaw), null, value.Position.Y);
            placement = placement with
            {
                InteriorLayout = value.LayoutName(false),
                ExteriorLayout = value.LayoutName(true)
            };
            JsonSerializer.Serialize(writer, new TownDisplay(value.InstanceId, value.BuildingId, value.Name, placement, value.Destroyed, value.OwnerFactionId, placement.InteriorLayout ?? "", placement.ExteriorLayout ?? "", value.Naming), options);
        }
    }

    internal CharacterDisplay CaptureAppearance(CharacterAppearance look)
    {
        var display = look.CaptureDisplay(_database);
        return display with
        {
            Shape = display.Shape with
            {
                Sliders = new Dictionary<string, float>(display.Shape.Sliders),
                Missing = display.Shape.Missing.ToHashSet()
            }
        };
    }

    public byte[] Encode(PlayerWorldView view)
    {
        var bytes = EncodeReplicationView(view);
        ValidateJson(bytes);
        return bytes;
    }

    internal byte[] EncodeComponent<T>(T value) => EncodeBoundedComponent(value, MaximumBytes);

    /// <summary>
    /// Serializes one component under its own allocation bound, including the event journal's smaller raw JSON retention allowance.
    /// </summary>
    internal byte[] EncodeBoundedComponent<T>(T value, int maximumBytes)
    {
        if (maximumBytes is <= 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var buffer = new BoundedBuffer(maximumBytes);
        JsonSerializer.Serialize(buffer, value, _json);
        return buffer.ToArray();
    }

    internal T DecodeComponent<T>(ReadOnlySpan<byte> bytes)
    {
        // Replication components came from a structurally validated patch (or our own writer).
        // Rewalking every component repeats the patch's duplicate/token/depth checks and allocates
        // a field-name cache for each actor. Typed deserialization and final view checks still run.
        return JsonSerializer.Deserialize<T>(bytes, _json) ?? throw new InvalidDataException("Missing state component.");
    }

    // Trusted typed snapshots are already domain-checked and the serializer writes valid JSON with
    // a depth/byte bound. Do not parse our own output again before splitting it into state entries.
    // Incoming packets and reconstructed views still receive the full structural validation.
    internal byte[] EncodeReplicationView(PlayerWorldView view)
    {
        Validate(view);
        view = ItemTagProjection.Capture(view);
        using var stream = new BoundedBuffer(MaximumBytes);
        JsonSerializer.Serialize(stream, view, _json);
        return stream.ToArray();
    }

    internal byte[] EncodePresentation<T>(T value)
    {
        using var buffer = new BoundedBuffer(WireFrame.MaximumPayload);
        JsonSerializer.Serialize(buffer, value, _json);
        var bytes = buffer.ToArray();
        ValidateJson(bytes);
        return bytes;
    }

    internal T DecodePresentation<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > WireFrame.MaximumPayload)
            throw new InvalidDataException("Presentation message is too large.");
        try
        {
            ValidateJson(bytes);
            return JsonSerializer.Deserialize<T>(bytes, _json) ?? throw new InvalidDataException("Missing presentation message.");
        }
        catch (Exception e)when (e is JsonException or ArgumentException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException("Malformed presentation message.", e);
        }
    }

    public PlayerWorldView Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("World view is too large.");
        try
        {
            ValidateJson(bytes);
            var view = JsonSerializer.Deserialize<PlayerWorldView>(bytes, _json) ?? throw new InvalidDataException("Empty world view.");
            Validate(view);
            return new ItemTagProjection().Join(view);
        }
        catch (Exception e)when (e is JsonException or NotSupportedException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Malformed world view.", e);
        }
    }

    internal static void ValidateJson(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 48 });
        // Reuse sets by nesting depth and canonicalize repeated field names within this document.
        // A snapshot repeats the same schema for every character; allocating every field string
        // and every object's set used to dominate both encoding checks and incoming validation.
        var objects = new HashSet<string>? [48];
        int objectDepth = -1;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var fieldLookup = fields.GetAlternateLookup<ReadOnlySpan<char>>();
        Span<char> fieldBuffer = stackalloc char[4096];
        var arrays = new Stack<bool>();
        int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > 2000000)
                throw new InvalidDataException("Too many state values.");
            if (reader.TokenType == JsonTokenType.Null && arrays.TryPeek(out bool inArray) && inArray)
                throw new InvalidDataException("Null state array element.");
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects[++objectDepth] ??= new(StringComparer.Ordinal);
                    arrays.Push(false);
                    break;
                case JsonTokenType.EndObject:
                    objects[objectDepth--]!.Clear();
                    arrays.Pop();
                    break;
                case JsonTokenType.StartArray:
                    arrays.Push(true);
                    break;
                case JsonTokenType.EndArray:
                    arrays.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (reader.ValueSpan.Length > 4096)
                        throw new InvalidDataException("Duplicate or oversized state field.");
                    var field = fieldBuffer[..reader.CopyString(fieldBuffer)];
                    if (!fieldLookup.TryGetValue(field, out string? name))
                    {
                        name = new string (field);
                        if (fields.Count < 8192)
                            fieldLookup.TryAdd(field, name);
                    }

                    if (!objects[objectDepth]!.Add(name))
                        throw new InvalidDataException("Duplicate or oversized state field.");
                    break;
                case JsonTokenType.String:
                    if (reader.ValueSpan.Length > 65536)
                        throw new InvalidDataException("State text is too long.");
                    break;
            }
        }
    }

    internal static void Validate(PlayerWorldView view)
    {
        if (view.World == Guid.Empty || view.Player.IsNone || view.ConnectionGeneration == 0 || view.Publication == 0 || !double.IsFinite(view.GameHours) || view.TimeScale is not (0 or 1 or 2 or 5) || view.ActiveFaction < -1 || view.Snapshot is null || view.Snapshot.Tick < 0 || view.Roles is null || view.Factions is null || view.Messages is null || view.Messages.Items is null || view.FactionCatalogue is null)
            throw new InvalidDataException("Invalid view identity.");
        if (!PlayerModViews.Valid(view.Snapshot.ModViews)) throw new InvalidDataException("Invalid recipient mod view.");
        ItemTagProjection.Validate(view.Snapshot.ItemTags);
        view.Weather?.Validate();
        view.WeatherEffects?.Validate();
        if (!float.IsFinite(view.SimulationStepSeconds) || view.SimulationStepSeconds is <= 0 or > 1)
            throw new InvalidDataException("Invalid simulation presentation timing.");
        if (view.Snapshot.Characters is null || view.Snapshot.Characters.Count > 65536 || view.Snapshot.Characters.Any(c => c is null || c.Id.IsNone || !Finite(c.Position) || !float.IsFinite(c.Yaw) || float.IsInfinity(c.DrawYaw) || c.Path is null || c.PathExact < 0 || c.PathExact != int.MaxValue && c.PathExact > c.Path.Count) || view.Snapshot.Characters.Select(c => c.Id).Distinct().Count() != view.Snapshot.Characters.Count || view.Roles.Any(r => r is null || r.Entity.IsNone) || view.Roles.Select(r => r.Entity).Distinct().Count() != view.Roles.Count || view.Factions.Any(f => f is null || f.Faction < 0 || f.UnlockedBuildings is null || !Enum.IsDefined((SquadFormation)f.Formation)) || view.Factions.Select(f => f.Faction).Distinct().Count() != view.Factions.Count || view.ActiveFaction >= 0 && !view.Factions.Any(f => f.Faction == view.ActiveFaction))
            throw new InvalidDataException("Invalid view records.");
        ValidateContainerOpener(view);
        ValidateSceneMetadata(view.Snapshot);
        ValidateSquadPresentation(view);
        ValidateDrawAnchors(view.Snapshot);
        ValidateModPresentation(view.Snapshot);
        ValidateModVisuals(view.Snapshot.ModVisuals);
        ValidateJobOffers(view.Snapshot.ModJobOffers);
        if (view.Snapshot.GroundItems is null || view.Snapshot.GroundItems.Any(item => item.Material is null || item.Material.Length > 4096))
            throw new InvalidDataException("Invalid ground item material identity.");
        if (view.Snapshot.Projectiles is null || view.Snapshot.Projectiles.Any(p => !float.IsFinite(p.Speed) || p.Speed < 0))
            throw new InvalidDataException("Invalid projectile presentation speed.");
        if (view.FactionCatalogue.Count > 65536 || view.FactionCatalogue.Any(f => f is null || f.Faction < 0 || f.Name is null || f.Name.Length > 4096 || f.RecordId is { Length: > 4096 }) || view.FactionCatalogue.Select(f => f.Faction).Distinct().Count() != view.FactionCatalogue.Count || view.FactionCatalogue.Where(f => f.RecordId is not null).Select(f => f.RecordId).Distinct(StringComparer.Ordinal).Count() != view.FactionCatalogue.Count(f => f.RecordId is not null))
            throw new InvalidDataException("Invalid public faction catalogue.");
        var disclosed = view.FactionCatalogue.Select(f => f.Faction).ToHashSet();
        if (view.Factions.Any(f => f.Standings is null || f.Standings.Count > 65536 || f.Standings.Any(s => s is null || !disclosed.Contains(s.Faction) || !float.IsFinite(s.Relation) || s.Ally && s.Hostile) || f.Standings.Select(s => s.Faction).Distinct().Count() != f.Standings.Count))
            throw new InvalidDataException("Invalid member faction standings.");
        foreach (var faction in view.Factions)
        {
            if (faction.Contracts is null || faction.UpcomingEvents is null || faction.Contracts.Count + faction.UpcomingEvents.Count > 65536 || faction.Contracts.Concat(faction.UpcomingEvents).Any(line => line is null || !Enum.IsDefined(line.Kind) || line.Label is null || line.Value is null || line.Hours is { } hours && !double.IsFinite(hours)))
                throw new InvalidDataException("Invalid faction information panel.");
        }

        ValidatePresentationEvents(view);
        if (view.Messages.Items.Count > 200 || view.Messages.Items.Any(m => m is null || m.Id == 0 || m.Id > view.Messages.HighWater || !Enum.IsDefined(m.Kind) || m.Owner is null || m.Text is null || m.Owner.Length > 256 || m.Text.Length > 4096) || view.Messages.Items.Select(m => m.Id).Distinct().Count() != view.Messages.Items.Count)
            throw new InvalidDataException("Invalid private message history.");
        if (view.Prospected is { } report)
        {
            if (report.Serial <= 0 || !Finite(report.Position) || report.Environment is null || report.Resources is null)
                throw new InvalidDataException("Invalid private survey report.");
            if (report.Map is { } map && (map.Size < 1 || map.Heights is null || (long)map.Size * map.Size != map.Heights.Length || map.Extent <= 0 || !float.IsFinite(map.Extent) || !float.IsFinite(map.X) || !float.IsFinite(map.Z) || map.Fields is null || map.Rocks is null || map.Fields.Count != report.Resources.Count || map.Rocks.Count != report.Resources.Count || map.Fields.Any(f => f is not null && f.Length != map.Heights.Length) || map.Rocks.Any(r => r is null)))
                throw new InvalidDataException("Invalid private survey map.");
        }
    }

    /// <summary>
    /// The authority's immutable job menu table remains within the same type, text and record bounds used by registration.
    /// </summary>
    static void ValidateJobOffers(IReadOnlyList<Meitou.Simulation.Production.ModJobOffer> offers)
    {
        if (offers is null || offers.Count > SimulationExtensions.MaximumJobTypes
            || offers.Any(offer => offer is null || (int)offer.Type < SimulationExtensions.FirstModCode || offer.Refusal() is not null
                || offer.Buildings.Count > 4096 || offer.Buildings.Any(id => id.Length > 4096))
            || offers.Select(offer => offer.Type).Distinct().Count() != offers.Count)
            throw new InvalidDataException("Invalid authoritative mod job offers.");
    }

    /// <summary>
    /// Visual lifetimes and anchors are validated without requiring other component references to arrive on the same lane.
    /// </summary>
    static void ValidateModVisuals(ModVisualSnapshot visuals)
    {
        if (visuals?.Effects is null || visuals.Glows is null || visuals.Trails is null || visuals.Audio is null
            || (long)visuals.Effects.Length + visuals.Glows.Length + visuals.Trails.Length + visuals.Audio.Length > 65536)
            throw new InvalidDataException("Invalid shared visual collection.");
        if (visuals.Effects.Any(effect => effect is null || effect.Id <= 0 || !Name(effect.Owner, 256)
                || !Name(effect.Effect, 4096) || !Name(effect.System, 4096) || !ModVisuals.ValidAnchor(effect.Anchor)
                || !Times(effect.StartedAt, effect.ExpiresAt) || effect.Tint is { } tint && !Colour(tint))
            || visuals.Effects.Select(effect => effect.Id).Distinct().Count() != visuals.Effects.Length)
            throw new InvalidDataException("Invalid shared mod effect.");
        if (visuals.Glows.Any(glow => glow is null || glow.Id <= 0 || !Name(glow.Owner, 256) || !ModVisuals.ValidTarget(glow.Target)
                || !Times(glow.StartedAt, glow.ExpiresAt) || !Colour(glow.Colour) || glow.Strength is not (>= 0 and <= 4)
                || !ModVisuals.ValidGlow(glow.Mode, glow.Target, glow.Colour, glow.Strength, glow.ThroughWalls))
            || visuals.Glows.Select(glow => glow.Id).Distinct().Count() != visuals.Glows.Length)
            throw new InvalidDataException("Invalid shared mod glow.");
        if (visuals.Trails.Any(trail => trail is null || trail.Id <= 0 || !Name(trail.Owner, 256)
                || !ModVisuals.ValidAnchor(trail.Anchor) || !ModVisuals.ValidTrail(trail.Style) || !Times(trail.StartedAt, trail.ExpiresAt))
            || visuals.Trails.Select(trail => trail.Id).Distinct().Count() != visuals.Trails.Length)
            throw new InvalidDataException("Invalid shared mod trail.");
        if (visuals.Audio.Any(audio => audio is null || audio.Id <= 0 || !Name(audio.Owner, 256)
                || audio.Modulation != audio.Modulation.Clamped())
            || visuals.Audio.Select(audio => audio.Id).Distinct().Count() != visuals.Audio.Length
            || visuals.Audio.Select(audio => audio.Owner).Distinct(StringComparer.Ordinal).Count() != visuals.Audio.Length)
            throw new InvalidDataException("Invalid shared mod audio.");

        static bool Name(string value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
        static bool Times(double started, double expires) => double.IsFinite(started) && double.IsFinite(expires) && expires >= started;
        static bool Colour(Vector3 value) => value.X is >= 0 and <= 4 && value.Y is >= 0 and <= 4 && value.Z is >= 0 and <= 4;
    }

    /// <summary>
    /// Component references may arrive on independent lanes; the wire requires a canonical, structurally valid character identity.
    /// </summary>
    static void ValidateContainerOpener(PlayerWorldView view)
    {
        var opener = view.Snapshot.ContainerOpener;
        if (opener != CharacterId.None && (opener.Slot < 0 || opener.Generation < 0))
            throw new InvalidDataException("Invalid private container opener identity.");
    }

    /// <summary>
    /// Public work targets and map markers remain bounded, unique and structurally valid even when their scene components arrive independently.
    /// </summary>
    static void ValidateSceneMetadata(WorldSnapshot snapshot)
    {
        if (snapshot.WorkSites is null || snapshot.WorkSites.Count > 65536
            || snapshot.WorkSites.Any(s => string.IsNullOrEmpty(s.Id) || s.Id.Length > 4096 || !Finite(s.Position)
                || s.Owner < -1 || s.SiteRevision == 0 || s.Record is null || s.Record.Length > 4096
                || s.Name is null || s.Name.Length > 4096 || !Enum.IsDefined(s.Kind))
            || snapshot.WorkSites.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.WorkSites.Count)
            throw new InvalidDataException("Invalid public work targets.");
        if (snapshot.MapSquads is null || snapshot.MapSquads.Count > 65536
            || snapshot.MapSquads.Any(s => !float.IsFinite(s.Position.X) || !float.IsFinite(s.Position.Y) || s.Faction < -1)
            || snapshot.MapSquads.Select(s => s.Key).Distinct().Count() != snapshot.MapSquads.Count)
            throw new InvalidDataException("Invalid map squad markers.");
        if (snapshot.FoliageOutposts is null || snapshot.FoliageOutposts.Count > 65536
            || snapshot.FoliageOutposts.Any(t => !float.IsFinite(t.At.X) || !float.IsFinite(t.At.Y)
                || !float.IsFinite(t.Range) || t.Range < 0))
            throw new InvalidDataException("Invalid outpost foliage geometry.");
    }

    /// <summary>
    /// Per-recipient squad lists and static mod job labels remain bounded without requiring same-frame component references.
    /// </summary>
    static void ValidateSquadPresentation(PlayerWorldView view)
    {
        if (view.Snapshot.InventoriesClosed < 0 || view.Snapshot.ModJobNames is not { } names
            || names.Count > SimulationExtensions.MaximumJobTypes
            || names.Any(p => p.Key < SimulationExtensions.FirstModCode || string.IsNullOrWhiteSpace(p.Value)
                || p.Value.Length > SimulationExtensions.MaximumJobNameLength))
            throw new InvalidDataException("Invalid job labels or inventory close serial.");

        CheckSquads(view.Snapshot.IndependentSquads);
        if (view.Snapshot.FactionHeads < 0 || view.Snapshot.CappedHeads < 0)
            throw new InvalidDataException("Invalid faction head counts.");
        foreach (var faction in view.Factions)
        {
            CheckSquads(faction.IndependentSquads);
            if (faction.FactionHeads < 0 || faction.CappedHeads < 0 || faction.CappedHeads > faction.FactionHeads)
                throw new InvalidDataException("Invalid faction head counts.");
        }

        static void CheckSquads(IReadOnlyList<SquadView>? squads)
        {
            if (squads is null || squads.Count > 65536 || squads.Any(s => s is null || s.Id < 0 || s.Name is null
                || s.Name.Length > 4096 || s.Members is null || s.Members.Count > 65536
                || s.Members.Any(c => c.IsNone || c.Slot < 0 || c.Generation < 0)
                || s.Members.Distinct().Count() != s.Members.Count) || squads.Select(s => s.Id).Distinct().Count() != squads.Count)
                throw new InvalidDataException("Invalid independent squad presentation.");
        }
    }

    /// <summary>
    /// Draw transforms require finite values and canonical carrier IDs; the server separately removes references outside the recipient's view.
    /// </summary>
    static void ValidateDrawAnchors(WorldSnapshot snapshot)
    {
        foreach (var character in snapshot.Characters)
        {
            if (!float.IsFinite(character.TagHeight) || character.TagHeight < 0)
                throw new InvalidDataException("Invalid character tag height.");
            if (character.LookAt is { } lookAt) LookAtCodec.Validate(lookAt);
            if (character.Lean is { } lean && !Finite(lean)) throw new InvalidDataException("Invalid bed lean.");
            if (character.Anchor is { } anchor && (anchor.Carrier.IsNone || anchor.Carrier.Slot < 0 || anchor.Carrier.Generation < 0
                || anchor.Bone is null || anchor.Bone.Length > 4096 || !Finite(anchor.Offset) || !Finite(anchor.Rotation)))
                throw new InvalidDataException("Invalid character draw anchor.");
        }
    }

    /// <summary>
    /// New mod display values stay bounded and retain nullable slots; stealing chances follow the upstream unclamped finite roll value.
    /// </summary>
    static void ValidateModPresentation(WorldSnapshot snapshot)
    {
        foreach (var character in snapshot.Characters)
        {
            StatusEffectPresentationCodec.Validate(character.Effects);
            ModPresentationCodec.ValidateMeters(character.Meters);
            ModPresentationCodec.ValidateSkills(character.ModSkills);
            ModPresentationCodec.ValidateBoneMeshes(character.BoneMeshes);
            ValidateStealChances(character.ItemGrid);
        }

        if (snapshot.InspectedLevelPower is { } power) ModPresentationCodec.ValidatePower(power);
        foreach (var machine in snapshot.Machines) ValidateStealChances(machine.Storage);
    }

    /// <summary>
    /// A probability can exceed one under the original formula; nonfinite values must never reach tooltip arithmetic.
    /// </summary>
    static void ValidateStealChances(InventoryView? inventory)
    {
        if (inventory is null) return;
        if (inventory.Items is null || inventory.Items.Any(item => item is null || item.StealChance is { } chance && !float.IsFinite(chance)))
            throw new InvalidDataException("Invalid inventory stealing chance.");
    }

    /// <summary>
    /// Adds publication context to event failures without logging recipient payloads.
    /// </summary>
    static void ValidatePresentationEvents(PlayerWorldView view)
    {
        try
        {
            PresentationEventValidation.Validate(view.Events);
        }
        catch (InvalidDataException error)
        {
            string context = $"world={view.World} generation={view.ConnectionGeneration} publication={view.Publication} tick={view.Snapshot.Tick}";
            throw new InvalidDataException($"{error.Message} {context}", error);
        }
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>
    /// Whether each component of a display rotation is finite.
    /// </summary>
    static bool Finite(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    /// <summary>
    /// The wire tag identity has a fixed bound even when a code mod stores an arbitrarily long tag on the authority.
    /// </summary>
    sealed class ItemTagDigest : JsonConverter<string>
    {
        /// <summary>
        /// Validates the 128-bit SHA-256 prefix before it becomes a UI click token.
        /// </summary>
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            string value = reader.GetString() ?? throw new JsonException("Missing item tag identity.");
            if (value.Length != 0 && (value.Length != Meitou.Simulation.Items.ItemTagIdentity.DigestCharacters
                || value.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F'))))
                throw new JsonException("Invalid item tag identity.");
            return value;
        }

        /// <summary>
        /// The encoder supplies the bounded digest computed from the authoritative tag.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    sealed class SingleNumber(bool allowNaN = false) : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (allowNaN && reader.TokenType == JsonTokenType.String && reader.GetString() == "NaN")
                return float.NaN;
            float value = reader.GetSingle();
            return float.IsFinite(value) ? value : throw new JsonException("Non-finite state number.");
        }

        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
        {
            if (float.IsFinite(value))
                writer.WriteNumberValue(value);
            else if (allowNaN && float.IsNaN(value))
                writer.WriteStringValue("NaN");
            else
                throw new JsonException("Non-finite state number.");
        }
    }

    /// <summary>
    /// Only status-effect durations accept the permanent-effect sentinel; ordinary state doubles remain finite-only.
    /// </summary>
    sealed class EffectHours : JsonConverter<double>
    {
        /// <summary>
        /// Reads the exact finite countdown or the explicit permanent sentinel.
        /// </summary>
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() == "Forever") return double.PositiveInfinity;
            if (reader.TokenType != JsonTokenType.Number) throw new JsonException("Invalid status effect duration.");
            double value = reader.GetDouble();
            return double.IsFinite(value) && value >= 0 ? value : throw new JsonException("Invalid status effect duration.");
        }

        /// <summary>
        /// Writes permanent effects without changing validation of unrelated numeric state.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsPositiveInfinity(value)) writer.WriteStringValue("Forever");
            else if (double.IsFinite(value) && value >= 0) writer.WriteNumberValue(value);
            else throw new JsonException("Invalid status effect duration.");
        }
    }

    sealed class DoubleNumber : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            double value = reader.GetDouble();
            return double.IsFinite(value) ? value : throw new JsonException("Non-finite state number.");
        }

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (!double.IsFinite(value))
                throw new JsonException("Non-finite state number.");
            writer.WriteNumberValue(value);
        }
    }

    sealed class RecordReference(GameDatabase database) : JsonConverter<GameRecord>
    {
        public override GameRecord Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String && reader.GetString()is { } id && database.Find(id)is { } record ? record : throw new JsonException("Missing compatible content record.");
        public override void Write(Utf8JsonWriter writer, GameRecord value, JsonSerializerOptions options) => writer.WriteStringValue(value.StringId);
    }

    sealed class Appearance(GameDatabase database) : JsonConverter<CharacterAppearance>
    {
        public override CharacterAppearance Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => CharacterAppearance.FromDisplay(JsonSerializer.Deserialize<CharacterDisplay>(ref reader, options) ?? throw new JsonException("Missing display."));
        public override void Write(Utf8JsonWriter writer, CharacterAppearance value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.CaptureDisplay(database), options);
    }

    sealed record PoseData(bool[] Set, Quaternion[] Rotation, Vector3[] Position, float Weight);
    sealed class Pose : JsonConverter<BonePose>
    {
        public override BonePose Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var d = JsonSerializer.Deserialize<PoseData>(ref reader, options) ?? throw new JsonException("Missing pose.");
            if (d.Set.Length > 1024 || d.Rotation.Length != d.Set.Length || d.Position.Length != d.Set.Length || !float.IsFinite(d.Weight))
                throw new JsonException("Invalid pose.");
            var pose = new BonePose(d.Set.Length)
            {
                Weight = d.Weight
            };
            d.Set.CopyTo(pose.Set, 0);
            d.Rotation.CopyTo(pose.Rotation, 0);
            d.Position.CopyTo(pose.Position, 0);
            return pose;
        }

        public override void Write(Utf8JsonWriter writer, BonePose value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, new PoseData(value.Set, value.Rotation, value.Position, value.Weight), options);
    }

    sealed class Sets : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) => (JsonConverter)Activator.CreateInstance(typeof(Set<>).MakeGenericType(type.GetGenericArguments()))!;
    }

    sealed class Set<T> : JsonConverter<IReadOnlySet<T>>
    {
        public override IReadOnlySet<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var array = JsonSerializer.Deserialize<T[]>(ref reader, options) ?? throw new JsonException("Missing set.");
            var set = array.ToHashSet();
            if (set.Count != array.Length || array.Any(v => v is null))
                throw new JsonException("Invalid set.");
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
                if (e is null || !result.TryAdd((e.Building, e.Door), e.State))
                    throw new JsonException("Duplicate door.");
            return result;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<(string Building, int Door), DoorView> value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.OrderBy(p => p.Key.Building, StringComparer.Ordinal).ThenBy(p => p.Key.Door).Select(p => new DoorEntry(p.Key.Building, p.Key.Door, p.Value)).ToArray(), options);
    }

    sealed record JobData(TaskType Type, string? Site, Hand Saved, CharacterId Character);
    sealed class Job : JsonConverter<PermaJob>
    {
        public override PermaJob Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var d = JsonSerializer.Deserialize<JobData>(ref reader, options) ?? throw new JsonException("Missing job.");
            return new(d.Type, d.Site, d.Saved)
            {
                Character = d.Character
            };
        }

        public override void Write(Utf8JsonWriter writer, PermaJob value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, new JobData(value.Type, value.Site, value.Saved, value.Character), options);
    }
}

internal sealed class BoundedBuffer(int maximum) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Length + count > maximum)
            throw new InvalidDataException("Encoded state exceeds its byte limit.");
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Length + buffer.Length > maximum)
            throw new InvalidDataException("Encoded state exceeds its byte limit.");
        // Avoid MemoryStream's span overload forwarding to this override again.
        var bytes = buffer.ToArray();
        base.Write(bytes, 0, bytes.Length);
    }
}
