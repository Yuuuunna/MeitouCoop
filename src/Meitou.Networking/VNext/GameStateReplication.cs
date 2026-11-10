using System.Numerics;
using System.Security.Cryptography;
using Meitou.Simulation;

namespace Meitou.Networking.VNext;

public readonly record struct GamePresentationSource(ulong Publication, ulong Tick, ulong PacketSequence = 0, uint Revision = 0, bool Checkpoint = false);
public enum GameDepartureReason { OutOfScope, Destroyed }
public readonly record struct GameEntityDeparture(ulong Entity, uint InterestGeneration, GameDepartureReason Reason);
public sealed record GameMotionCapture(MotionRecord Record, MotionRelevance Relevance);
public sealed record GameStateMotionPublication(IReadOnlyList<GameMotionCapture> Motion, IReadOnlyList<(ulong Entity, uint Generation)> WithdrawnMotion);
public sealed record GameStatePublication(ulong Sequence, bool Baseline, IReadOnlyList<byte[]> Frames)
{
    public IReadOnlyDictionary<ushort, long> ComponentBytes { get; init; } = new Dictionary<ushort, long>();
    public bool RequiresBulk => Baseline || Frames.Sum(f => (long)f.Length) > 64 * 1024;
    public IReadOnlyList<GameMotionCapture> Motion { get; init; } = [];
    public IReadOnlyList<(ulong Entity, uint Generation)> WithdrawnMotion { get; init; } = [];
}

/// <summary>Recipient-only, detached per-field publication. Reliable frames must be retained by the transport until delivered.
/// Field IDs and typed delegates are static; capture never reflects or encodes a whole view/character.</summary>
public sealed partial class GameStatePublisher(WorldViewCodec codec, Func<CharacterId, ulong> entityOf, ulong sessionEpoch, ulong viewEpoch)
{
    readonly AnimationDescriptorCatalog animationDescriptors = new();
    readonly HashSet<ushort> bulkAnimationDescriptors = [];
    readonly HashSet<ushort> criticalAnimationDescriptors = [];
    readonly Dictionary<(ulong Entity, ushort Field), byte[]> previous = new();
    readonly Dictionary<ulong, (CharacterId Id, uint Generation)> entities = new();
    readonly Dictionary<ulong, uint> generations = new();
    readonly HashSet<ulong> motionRetired = new();
    RecipientCaptureIdentity? identity;
    ulong sequence;
    readonly Dictionary<ulong, (MotionRecord Record, long Tick)> motion = new();
    /// <summary>Call on the authority owner before handing the immutable recipient projection to the encoding worker.
    /// PublishCharacters/world publication own their value collections; the mutable appearance, pose and turret buffers are copied here.</summary>
    public static string ComponentName(ushort id) => id switch
    {
        0 => "EntityLifecycle", 3000 => "InitialMotion", 3001 => "DrawYaw", 3002 => "AnimationPlayback", 3003 => "AnimationDescriptors",
        _ => GameComponentSchema.ViewFields.FirstOrDefault(f => f.Id == id)?.Name ?? GameComponentSchema.SnapshotFields.FirstOrDefault(f => f.Id == id)?.Name ?? GameComponentSchema.CharacterFields.FirstOrDefault(f => f.Id == id)?.Name ?? throw new ArgumentOutOfRangeException(nameof(id)),
    };
    public PlayerWorldView DetachForEncoding(PlayerWorldView view) => codec.Detach(view);
    public GameStatePublication Capture(PlayerWorldView view, IReadOnlyDictionary<ulong, AnimationPlaybackState>? animations = null, IReadOnlySet<ulong>? destroyedEntities = null)
    {
        WorldViewCodec.Validate(view);
        if (view.Snapshot.Characters.Count > 4096) throw new InvalidDataException("Recipient entity subscription exceeds protocol bound.");
        var source = new RecipientCaptureIdentity(view.World, view.Player, view.ConnectionGeneration);
        if (identity is { } old && old != source) throw new InvalidDataException("Publisher recipient changed.");
        identity = source;
        if (sessionEpoch == 0 || viewEpoch == 0 || sequence == ulong.MaxValue) throw new InvalidDataException("Invalid publication epoch.");
        ulong revision = ++sequence;
        bool baseline = revision == 1;
        using var stream = new BoundedBuffer(GameStateWire.MaximumBytes); using var writer = new BinaryWriter(stream);
        WritePublicationClock(writer, view);
        var current = new HashSet<ulong>();
        var captures = new List<GameMotionCapture>();
        var withdrawals = new List<(ulong Entity, uint Generation)>();
        long capturedBytes = 0;
        var records = new List<(byte Kind, ulong Entity, uint Generation, ushort Field, byte[] Data)>();
        void Add(ulong entity, uint generation, ushort field, byte[] data)
        {
            capturedBytes += data.Length + 19;
            if (capturedBytes > GameStateWire.MaximumBytes) throw new InvalidDataException("Captured recipient components exceed bulk bound.");
            var key = (entity, field);
            if (!previous.TryGetValue(key, out var old) || !old.AsSpan().SequenceEqual(data))
            { records.Add((2, entity, generation, field, data)); previous[key] = data; }
        }
        foreach (var f in GameComponentSchema.ViewFields.Where(f => !GameCollectionSchema.Ids.Contains(f.Id) && (baseline || f.Id != 7))) Add(0, 0, f.Id, f.Encode(codec, view));
        foreach (var f in GameComponentSchema.SnapshotFields.Where(f => !GameCollectionSchema.Ids.Contains(f.Id))) Add(0, 0, f.Id, f.Encode(codec, view.Snapshot));
        capturedBytes += CaptureCollections(view, (kind, field, value) => records.Add((kind, 0, 0, field, value)));
        if (capturedBytes > GameStateWire.MaximumBytes) throw new InvalidDataException("Captured components exceed bulk bound.");
        foreach (var c in view.Snapshot.Characters)
        {
            ulong entity = entityOf(c.Id);
            if (entity == 0 || !current.Add(entity)) throw new InvalidDataException("Missing or duplicate character entity.");
            bool spawn = !entities.TryGetValue(entity, out var existing) || existing.Id != c.Id || motionRetired.Contains(entity);
            uint generation = spawn ? checked(generations.GetValueOrDefault(entity) + 1) : existing.Generation;
            if (spawn)
            {
                if (!generations.ContainsKey(entity) && generations.Count >= 4096) throw new InvalidDataException("Interest fence capacity requires a new view.");
                generations[entity] = generation; entities[entity] = (c.Id, generation); motionRetired.Remove(entity);
                foreach (var k in previous.Keys.Where(k => k.Entity == entity).ToArray()) previous.Remove(k);
            }
            captures.Add(CaptureRoot(view, c, entity, generation, spawn));
            // Identity is reliable lifecycle data, emitted only for a new interest generation.
            if (spawn) records.Add((1, entity, generation, 0, codec.EncodeComponent(c.Id)));
            foreach (var f in GameComponentSchema.CharacterFields) Add(entity, generation, f.Id, f.Encode(codec, c));
            // Initial roots are reliable; ongoing roots arrive through MotionDatagramCodec/checkpoints.
            if (spawn)
            {
                using var roots = new MemoryStream(); using var rw = new BinaryWriter(roots);
                rw.Write(c.Position.X); rw.Write(c.Position.Y); rw.Write(c.Position.Z); rw.Write(c.Yaw);
                Add(entity, generation, 3000, roots.ToArray());
            }
            // Draw facing is distinct from navigation yaw and may be NaN. Preserve its exact bit pattern.
            if (spawn) Add(entity, generation, 3001, BitConverter.GetBytes(c.DrawYaw));
            if (animations?.TryGetValue(entity, out var animation) == true)
            {
                byte[] dynamicState = CompactAnimationPlaybackCodec.Encode(animation, animationDescriptors, out var used);
                foreach (ushort descriptor in used.Distinct())
                    if (bulkAnimationDescriptors.Add(descriptor)) records.Add((9, 0, 0, 3003, animationDescriptors.EncodeDefinition(descriptor)));
                Add(entity, generation, 3002, dynamicState);
            }
            else if (c.Animations.Count != 0) throw new InvalidDataException("Authority animation timeline required for animated character.");
        }
        foreach (var gone in entities.Keys.Where(e => !current.Contains(e)).ToArray())
        {
            records.Add(((byte)(destroyedEntities?.Contains(gone) == true ? 4 : 3), gone, entities[gone].Generation, 0, []));
            withdrawals.Add((gone, entities[gone].Generation)); motion.Remove(gone); entities.Remove(gone); motionRetired.Remove(gone);
            foreach (var key in previous.Keys.Where(k => k.Entity == gone).ToArray()) previous.Remove(key);
        }
        records = records.OrderBy(r => r.Kind == 9 ? 0 : 1).ToList();
        writer.Write(records.Count);
        foreach (var r in records)
        { writer.Write(r.Kind); writer.Write(r.Entity); writer.Write(r.Generation); writer.Write(r.Field); WriteValue(writer, r.Data); }
        if (stream.Length > GameStateWire.MaximumBytes) throw new InvalidDataException("Game publication exceeds bulk bound.");
        return new(revision, baseline, GameStateWire.Split(sessionEpoch, viewEpoch, revision, baseline, stream.ToArray())) { Motion = captures.AsReadOnly(), WithdrawnMotion = withdrawals.AsReadOnly(), ComponentBytes = records.GroupBy(r => r.Field).ToDictionary(g => g.Key, g => g.Sum(r => 19L + r.Data.Length)) };
    }
    /// <summary>While reliable component bulk is busy, capture only already-announced roots without encoding fields or advancing their delivery baseline.</summary>
    public GameStateMotionPublication CaptureMotionOnly(PlayerWorldView view)
    {
        var source = new RecipientCaptureIdentity(view.World, view.Player, view.ConnectionGeneration);
        if (identity is null) return new([], []);
        if (identity != source) throw new InvalidDataException("Publisher recipient changed.");
        if (view.Snapshot.Characters.Count > 4096) throw new InvalidDataException("Recipient entity subscription exceeds protocol bound.");
        var current = new HashSet<ulong>(); var captures = new List<GameMotionCapture>(); var withdrawals = new List<(ulong Entity, uint Generation)>();
        foreach (var character in view.Snapshot.Characters)
        {
            ulong entity = entityOf(character.Id);
            if (entity == 0 || !current.Add(entity)) throw new InvalidDataException("Missing or duplicate character entity.");
            if (!entities.TryGetValue(entity, out var known) || motionRetired.Contains(entity)) continue;
            if (known.Id != character.Id)
            { withdrawals.Add((entity, known.Generation)); motionRetired.Add(entity); motion.Remove(entity); continue; }
            captures.Add(CaptureRoot(view, character, entity, known.Generation, spawn: false));
        }
        foreach (var gone in entities.Where(p => !current.Contains(p.Key) && !motionRetired.Contains(p.Key)))
        { withdrawals.Add((gone.Key, gone.Value.Generation)); motionRetired.Add(gone.Key); motion.Remove(gone.Key); }
        return new(captures.AsReadOnly(), withdrawals.AsReadOnly());
    }
    GameMotionCapture CaptureRoot(PlayerWorldView view, CharacterSnapshot c, ulong entity, uint generation, bool spawn)
    {
        motion.TryGetValue(entity, out var beforeMotion);
        Vector3 velocity = !spawn && beforeMotion.Tick == view.Snapshot.Tick && beforeMotion.Record.Position == c.Position ? beforeMotion.Record.Velocity : Vector3.Zero;
        if (!spawn && beforeMotion.Tick < view.Snapshot.Tick)
            velocity = (c.Position - beforeMotion.Record.Position) / ((view.Snapshot.Tick - beforeMotion.Tick) * view.SimulationStepSeconds);
        MotionFlags flags = velocity.LengthSquared() > .000001f ? MotionFlags.Moving : MotionFlags.None;
        if (!c.CarriedBy.IsNone) flags |= MotionFlags.Carried;
        if (c.Ragdoll is not null) flags |= MotionFlags.Ragdoll;
        var root = new MotionRecord(entity, generation, spawn ? 1 : beforeMotion.Record.Revision, c.Position, velocity, c.Yaw, flags, c.DrawYaw);
        if (!spawn && !MotionDatagramCodec.SamePayload(root, beforeMotion.Record)) root = root with { Revision = checked(root.Revision + 1) };
        motion[entity] = (root, view.Snapshot.Tick);
        return new(root, c.Controller == view.Player ? MotionRelevance.Controlled : c.InCombat || c.Selected ? MotionRelevance.Interactive : MotionRelevance.Near);
    }
    internal static void WriteValue(BinaryWriter writer, byte[] data) { writer.Write(data.Length); writer.Write(data); }
}

internal static class GameStateWire
{
    internal const int MaximumBytes = 32 * 1024 * 1024, FrameBytes = 4096, HeaderBytes = 76;
    internal static IReadOnlyList<byte[]> Split(ulong session, ulong view, ulong sequence, bool baseline, byte[] payload)
    {
        int capacity = FrameBytes - HeaderBytes;
        byte[] hash = SHA256.HashData(payload);
        var result = new List<byte[]>();
        for (int offset = 0; offset < payload.Length; offset += capacity)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write("MGC1"u8); w.Write((ushort)21); w.Write((byte)(baseline ? 1 : 0)); w.Write((byte)0);
            w.Write(session); w.Write(view); w.Write(sequence); w.Write(payload.Length); w.Write(offset);
            int count = Math.Min(capacity, payload.Length - offset); w.Write(count); w.Write(hash); w.Write(payload, offset, count);
            result.Add(stream.ToArray());
        }
        return result.AsReadOnly();
    }
}

/// <summary>Authorized domain replica and animation feed. Recreate for a new authenticated view epoch.
/// Per-field revisions and entity tombstones prevent late bulk from undoing private withdrawal or despawn.</summary>
public sealed partial class GameStateReplica(WorldViewCodec codec, ulong sessionEpoch, ulong viewEpoch)
{
    /// <summary>Presentation-only linear prediction cap, in simulation seconds. No angular prediction,
    /// carry or ragdoll extrapolation. Authority state and gameplay queries always remain unchanged.</summary>
    public const double MaximumMotionExtrapolationSeconds = .250;
    sealed class Entity(CharacterSnapshot character, uint generation)
    {
        internal CharacterSnapshot Character = character;
        internal uint Generation = generation;
        internal ulong DeclarationRevision;
        internal uint MotionRevision;
        internal AnimationTimeline? Animation;
        internal readonly List<(ulong Tick, Vector3 Position, float Yaw, float DrawYaw, ulong Publication, ulong PacketSequence, uint Revision, bool Checkpoint)> History = [];
        internal MotionRecord? LastMotion;
    }
    readonly Dictionary<ulong, Entity> entities = new();
    readonly Dictionary<ulong, (uint Generation, ulong Revision)> withdrawn = new();
    readonly Dictionary<(ulong Entity, ushort Field), ulong> revisions = new();
    readonly Dictionary<(ulong Entity, ushort Field), byte[]> valueDigests = new();
    readonly Dictionary<(ulong Entity, uint Generation, ushort Field), (ulong Revision, long Tick, byte[] Value)> deferred = new();
    int deferredBytes;
    readonly Dictionary<CharacterId, GamePresentationSource> presentationSources = new();
    public GamePresentationSource? LatestMotionSource(CharacterId id)
    {
        var entity = entities.Values.FirstOrDefault(e => e.Character.Id == id);
        if (entity is null || entity.History.Count == 0) return null;
        var root = entity.History[^1]; return new(root.Publication, root.Tick, root.PacketSequence, root.Revision, root.Checkpoint);
    }
    public GamePresentationSource? PresentationSource(CharacterId id) => presentationSources.TryGetValue(id, out var source) ? source : null;
    PlayerWorldView? view;
    ulong clockRevision, departuresRevision;
    public IReadOnlyList<GameEntityDeparture> LastDepartures { get; private set; } = [];
    Meitou.Data.World.WeatherTime weatherTime;
    byte[]? staged, digest;
    ulong stagedSequence;
    bool stagedBaseline;
    int stagedOffset;
    ulong latestServerTick;
    public ulong LatestServerTick => latestServerTick;
    double presentedTick;
    /// <summary>Authenticated receive adapter's shared monotonic clock. Invoked after validating a
    /// publication clock and before applying any playback records, including staged initial entry.</summary>
    public Func<ulong, double, float, double>? ObservePresentationClock { get; set; }
    bool initialized, failed;
    public PlayerWorldView? View => initialized && !failed ? view : null;
    public IReadOnlyDictionary<ulong, AnimationTimeline> AnimationTimelines => entities.Where(p => p.Value.Animation is not null).ToDictionary(p => p.Key, p => p.Value.Animation!);
    public bool Apply(ReadOnlySpan<byte> frame)
    {
        if (failed || sessionEpoch == 0 || viewEpoch == 0) throw new InvalidDataException("Game replica has failed closed or has no epoch.");
        if (frame.Length < GameStateWire.HeaderBytes || frame.Length > GameStateWire.FrameBytes) throw new InvalidDataException("Invalid game frame length.");
        using var input = new MemoryStream(frame.ToArray(), false); using var r = new BinaryReader(input);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual("MGC1"u8) || r.ReadUInt16() != 21) throw new InvalidDataException("Unknown game frame.");
        byte flags = r.ReadByte(), reserved = r.ReadByte();
        if (flags > 1 || reserved != 0) throw new InvalidDataException("Unknown game frame flags.");
        ulong session = r.ReadUInt64(), epoch = r.ReadUInt64(), sequence = r.ReadUInt64();
        int length = r.ReadInt32(), offset = r.ReadInt32(), count = r.ReadInt32(); byte[] hash = r.ReadBytes(32);
        if (session != sessionEpoch || epoch != viewEpoch) return false;
        if (sequence == 0 || length <= 0 || length > GameStateWire.MaximumBytes || count <= 0 || offset < 0 || offset > length - count || input.Length - input.Position != count)
            throw new InvalidDataException("Invalid game frame bounds.");
        if (offset == 0)
        {
            if (staged is not null) throw new InvalidDataException("Concurrent game publications exceed staging bound.");
            staged = new byte[length]; digest = hash; stagedSequence = sequence; stagedBaseline = flags == 1; stagedOffset = 0;
        }
        if (staged is null || stagedSequence != sequence || staged.Length != length || stagedOffset != offset || stagedBaseline != (flags == 1) || !hash.AsSpan().SequenceEqual(digest))
            throw new InvalidDataException("Noncontiguous game publication.");
        frame[GameStateWire.HeaderBytes..].CopyTo(staged.AsSpan(offset)); stagedOffset += count;
        if (stagedOffset != length) return false;
        byte[] complete = staged; staged = null;
        if (!SHA256.HashData(complete).AsSpan().SequenceEqual(digest)) throw new InvalidDataException("Game publication digest mismatch.");
        try { ApplyPublication(complete, sequence, stagedBaseline); return true; }
        catch
        {
            failed = true; initialized = false; view = null;
            foreach (var entity in entities.Values) entity.Animation?.Withdraw();
            entities.Clear(); revisions.Clear(); valueDigests.Clear(); withdrawn.Clear(); deferred.Clear(); deferredBytes = 0; presentationSources.Clear(); collections.Clear(); animationDescriptors.Clear(); criticalEventFragment = null; latestServerTick = 0;
            throw;
        }
    }
    readonly AnimationDescriptorCatalog animationDescriptors = new();
    void ApplyPublication(byte[] bytes, ulong revision, bool baseline)
    {
        using var stream = new MemoryStream(bytes, false); using var r = new BinaryReader(stream);
        try
        {
            var world = new Guid(r.ReadBytes(16)); var player = codec.DecodeComponent<PlayerId>(ReadValue(r));
            ulong connection = r.ReadUInt64(), publication = r.ReadUInt64(); double hours = r.ReadDouble(), scale = r.ReadDouble(); float step = r.ReadSingle();
            long tick = r.ReadInt64(); ulong stateRevision = r.ReadUInt64();
            var sourceWeatherTime = new Meitou.Data.World.WeatherTime(r.ReadInt32(), r.ReadDouble());
            if (!double.IsFinite(sourceWeatherTime.Hours) || sourceWeatherTime.Hours < 0 || sourceWeatherTime.Hours >= 24) throw new InvalidDataException("Invalid weather clock.");
            if (world == Guid.Empty || player.IsNone || connection == 0 || tick < 0 || !double.IsFinite(hours) || !double.IsFinite(scale) || scale < 0 || !float.IsFinite(step) || step <= 0)
                throw new InvalidDataException("Invalid game clock/recipient.");
            latestServerTick = Math.Max(latestServerTick, (ulong)tick);
            if (view is not null && (view.World != world || view.Player != player || view.ConnectionGeneration != connection)) throw new InvalidDataException("Game recipient changed.");
            view ??= new(world, player, connection, 0, hours, new(tick, []), [], []);
            if (revision > clockRevision)
            {
                view = view with { Publication = publication, GameHours = hours, TimeScale = scale, SimulationStepSeconds = step,
                    Snapshot = view.Snapshot with { Tick = tick, StateRevision = stateRevision } }; clockRevision = revision; weatherTime = sourceWeatherTime;
            }
            if (ObservePresentationClock is { } observeClock)
                AdvancePresentation(observeClock(latestServerTick, view.TimeScale, view.SimulationStepSeconds));
            var departures = new List<GameEntityDeparture>();
            int count = r.ReadInt32();
            if (count < 0 || count > 1_000_000 || count > (stream.Length - stream.Position) / 19) throw new InvalidDataException("Invalid game record count.");
            for (int i = 0; i < count; i++)
            {
                byte kind = r.ReadByte(); ulong entity = r.ReadUInt64(); uint generation = r.ReadUInt32(); ushort field = r.ReadUInt16(); byte[] value = ReadValue(r);
                if (kind == 9)
                {
                    if (entity != 0 || generation != 0 || field != 3003) throw new InvalidDataException("Invalid animation descriptor envelope.");
                    animationDescriptors.ApplyDefinition(value);
                }
                else if (kind == 1)
                {
                    if (entity == 0 || generation == 0 || field != 0) throw new InvalidDataException("Invalid entity declaration.");
                    if (withdrawn.TryGetValue(entity, out var fence) && (generation <= fence.Generation || revision <= fence.Revision)) continue;
                    var id = codec.DecodeComponent<CharacterId>(value);
                    if (!entities.TryGetValue(entity, out var e) || generation > e.Generation)
                    {
                        if (entities.Count + withdrawn.Count >= 4096 && !withdrawn.ContainsKey(entity) && !entities.ContainsKey(entity)) throw new InvalidDataException("Recipient entity fence bound exceeded.");
                        e?.Animation?.Withdraw(); entities[entity] = e = new(new(id, null, default, 0, []), generation);
                        foreach (var key in revisions.Keys.Where(k => k.Entity == entity).ToArray()) { revisions.Remove(key); valueDigests.Remove(key); }
                    }
                    if (e.Generation == generation && e.Character.Id != id) throw new InvalidDataException("Conflicting entity identity.");
                    e.DeclarationRevision = Math.Max(e.DeclarationRevision, revision);
                }
                else if (kind is 3 or 4)
                {
                    if (entity == 0 || generation == 0 || field != 0 || value.Length != 0) throw new InvalidDataException("Invalid entity withdrawal.");
                    if (!withdrawn.ContainsKey(entity) && !entities.ContainsKey(entity) && entities.Count + withdrawn.Count >= 4096) throw new InvalidDataException("Recipient withdrawal fence bound exceeded.");
                    if (!withdrawn.TryGetValue(entity, out var fence) || generation > fence.Generation || revision > fence.Revision)
                    { withdrawn[entity] = (generation, revision); departures.Add(new(entity, generation, kind == 4 ? GameDepartureReason.Destroyed : GameDepartureReason.OutOfScope)); }
                    if (entities.TryGetValue(entity, out var e) && e.Generation <= generation && e.DeclarationRevision <= revision)
                    { e.Animation?.Withdraw(); entities.Remove(entity); presentationSources.Remove(e.Character.Id);
                        foreach (var key in revisions.Keys.Where(k => k.Entity == entity).ToArray()) { revisions.Remove(key); valueDigests.Remove(key); }
                    }
                }
                else if (kind is 6 or 7 or 8) ApplyCollection(kind, entity, generation, field, value, revision);
                else if (kind == 5) ApplyCriticalEventFragment(entity, generation, field, value, revision);
                else if (kind == 2)
                {
                    var key = (entity, field);
                    ulong priorRevision = revisions.GetValueOrDefault(key);
                    if (priorRevision > revision) continue;
                    if (priorRevision == revision)
                    {
                        if (!SHA256.HashData(value).AsSpan().SequenceEqual(valueDigests[key])) throw new InvalidDataException("Conflicting component at the same revision.");
                        continue;
                    }
                    if (entity == 0)
                    {
                        if (generation != 0) throw new InvalidDataException("Invalid global field generation.");
                        var vf = GameComponentSchema.ViewFields.FirstOrDefault(f => f.Id == field);
                        if (vf is not null) view = vf.Apply(codec, view, value);
                        else if (GameComponentSchema.SnapshotFields.FirstOrDefault(f => f.Id == field) is { } sf) view = view with { Snapshot = sf.Apply(codec, view.Snapshot, value) };
                        else throw new InvalidDataException("Unknown global component.");
                    }
                    else
                    {
                        if (!entities.TryGetValue(entity, out var e) || e.Generation != generation)
                        {
                            if (generation == 0) throw new InvalidDataException("Missing character generation.");
                            if (withdrawn.TryGetValue(entity, out var fence) && generation <= fence.Generation) continue;
                            var pendingKey = (entity, generation, field);
                            if (!deferred.TryGetValue(pendingKey, out var prior) || revision > prior.Revision)
                            {
                                int retained = deferredBytes - (prior.Value?.Length ?? 0) + value.Length;
                                if (retained > 256 * 1024 || deferred.Count >= 4096 && !deferred.ContainsKey(pendingKey)) throw new InvalidDataException("Prebaseline lifecycle history overflow.");
                                deferred[pendingKey] = (revision, tick, value); deferredBytes = retained;
                            }
                            continue;
                        }
                        ApplyEntityField(e, entity, generation, field, value, tick, publication);
                    }
                    revisions[key] = revision; valueDigests[key] = SHA256.HashData(value);
                }
                else throw new InvalidDataException("Unknown game record kind.");
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing game publication data.");
            foreach (var pending in deferred.ToArray())
            {
                var key = (pending.Key.Entity, pending.Key.Field);
                bool fenced = withdrawn.TryGetValue(pending.Key.Entity, out var fence) && pending.Key.Generation <= fence.Generation;
                bool known = entities.TryGetValue(pending.Key.Entity, out var entity) && entity.Generation == pending.Key.Generation;
                if (!fenced && !known) continue;
                if (!fenced && pending.Value.Revision > revisions.GetValueOrDefault(key))
                {
                    ApplyEntityField(entity!, pending.Key.Entity, pending.Key.Generation, pending.Key.Field, pending.Value.Value, pending.Value.Tick, publication);
                    revisions[key] = pending.Value.Revision; valueDigests[key] = SHA256.HashData(pending.Value.Value);
                }
                deferred.Remove(pending.Key); deferredBytes -= pending.Value.Value.Length;
            }
            if (revision >= departuresRevision) { LastDepartures = departures.AsReadOnly(); departuresRevision = revision; }
            RefreshCollections(initialized || baseline);
            if (view.Weather is { } weather) view = view with { Weather = weather with { Time = weatherTime } };
            RefreshCharacters();
            if (baseline)
            {
                foreach (ushort field in GameComponentSchema.ViewFields.Select(f => f.Id).Concat(GameComponentSchema.SnapshotFields.Select(f => f.Id)))
                    if (!revisions.ContainsKey((0, field))) throw new InvalidDataException("Incomplete baseline global schema.");
                foreach (var entity in entities)
                    foreach (ushort field in GameComponentSchema.CharacterFields.Select(f => f.Id).Concat(new ushort[] { 3000, 3001 }))
                        if (!revisions.ContainsKey((entity.Key, field))) throw new InvalidDataException("Incomplete baseline character schema.");
                initialized = true;
            }
            if (initialized)
            {
                WorldViewCodec.Validate(view);
            }
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated game publication.", e); }
    }
    void ApplyEntityField(Entity e, ulong entity, uint generation, ushort field, byte[] value, long tick, ulong publication)
    {
        if (field == 3000)
        {
            if (value.Length != 16) throw new InvalidDataException("Invalid initial root.");
            using var roots = new BinaryReader(new MemoryStream(value, false));
            var position = new Vector3(roots.ReadSingle(), roots.ReadSingle(), roots.ReadSingle()); float yaw = roots.ReadSingle();
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || !float.IsFinite(yaw)) throw new InvalidDataException("Invalid root values.");
            if (e.MotionRevision == 0)
            {
                e.Character = e.Character with { Position = position, Yaw = yaw };
                if (e.History.Count == 0) e.History.Add(((ulong)tick, position, yaw, e.Character.DrawYaw, publication, 0, 0, true));
            }
        }
        else if (field == 3001)
        {
            if (value.Length != 4) throw new InvalidDataException("Invalid draw yaw.");
            float yaw = BitConverter.ToSingle(value); if (float.IsInfinity(yaw)) throw new InvalidDataException("Infinite draw yaw.");
            e.Character = e.Character with { DrawYaw = yaw };
            if (e.MotionRevision == 0 && e.History.Count == 1) e.History[0] = e.History[0] with { DrawYaw = yaw };
        }
        else if (field == 3002)
        {
            e.Animation ??= new(new(sessionEpoch, viewEpoch, entity, generation));
            e.Animation.AdvanceTo(presentedTick);
            var state = CompactAnimationPlaybackCodec.Decode(value, animationDescriptors);
            if (e.Animation.Apply(new(sessionEpoch, viewEpoch, entity, generation), state) == AnimationApplyResult.NeedsResync)
                throw new InvalidDataException($"Animation transition queue overflow: entity={entity}, epoch={state.Epoch}, startTick={state.StartTick}, timelineTick={e.Animation.PresentedTick:R}, pending={e.Animation.PendingTransitions}, replicaTick={presentedTick:R}, receivedTick={latestServerTick}.");
        }
        else if (GameComponentSchema.CharacterFields.FirstOrDefault(f => f.Id == field) is { } cf) e.Character = cf.Apply(codec, e.Character, value);
        else throw new InvalidDataException("Unknown character component.");
    }
    static byte[] ReadValue(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > GameStateWire.MaximumBytes || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid game component length.");
        return reader.ReadBytes(length);
    }
    void RefreshCharacters()
    { if (view is not null) view = view with { Snapshot = view.Snapshot with { Characters = entities.OrderBy(p => p.Key).Select(p => p.Value.Character).ToArray() } }; }
    public bool ApplyMotion(MotionDatagram packet, bool checkpoint = false)
    {
        if (packet.Header.SessionEpoch != sessionEpoch || packet.Header.ViewEpoch != viewEpoch || view is null || !initialized || failed) return false;
        if (packet.Header.AuthorityPublication == 0) throw new InvalidDataException("Game motion has no authority publication.");
        // Reuse the binary codec's complete validation before touching the domain replica.
        _ = MotionDatagramCodec.Encode(packet);
        foreach (var motion in packet.Records)
            if (entities.TryGetValue(motion.EntityId, out var current) && current.Generation == motion.InterestGeneration && motion.Revision == current.MotionRevision && current.LastMotion is { } old && !MotionDatagramCodec.SamePayload(old, motion))
                throw new InvalidDataException("Conflicting root at the same motion revision.");
        if (packet.Records.Any(m => entities.TryGetValue(m.EntityId, out var entity) && entity.Generation == m.InterestGeneration && m.Revision >= entity.MotionRevision))
            latestServerTick = Math.Max(latestServerTick, packet.Header.ServerTick);
        bool changed = false;
        foreach (var motion in packet.Records)
            if (entities.TryGetValue(motion.EntityId, out var e) && e.Generation == motion.InterestGeneration && motion.Revision > e.MotionRevision)
            {
                if (e.History.Count != 0 && packet.Header.ServerTick < e.History[^1].Tick) continue;
                if ((motion.Flags & MotionFlags.Discontinuity) != 0 || e.LastMotion is { } last && ((last.Flags ^ motion.Flags) & (MotionFlags.Carried | MotionFlags.Ragdoll)) != 0) e.History.Clear();
                if (e.History.Count != 0 && e.History[^1].Tick == packet.Header.ServerTick) e.History.RemoveAt(e.History.Count - 1);
                e.History.Add((packet.Header.ServerTick, motion.Position, motion.Yaw, motion.DrawYaw, packet.Header.AuthorityPublication, packet.Header.PacketSequence, motion.Revision, checkpoint));
                if (e.History.Count > 32) e.History.RemoveAt(0);
                e.LastMotion = motion; e.Character = e.Character with { Position = motion.Position, Yaw = motion.Yaw, DrawYaw = motion.DrawYaw };
                e.MotionRevision = motion.Revision; changed = true;
            }
        if (changed) RefreshCharacters(); return changed;
    }
    CharacterSnapshot Sample(Entity entity, double tick)
    {
        var character = entity.Character;
        if (entity.History.Count != 0)
        {
            var before = entity.History[0]; var after = before;
            foreach (var point in entity.History)
            {
                if (point.Tick <= tick) before = point;
                after = point;
                if (point.Tick >= tick) break;
            }
            presentationSources[character.Id] = new(after.Publication, after.Tick, after.PacketSequence, after.Revision, after.Checkpoint);
            float fraction = after.Tick > before.Tick ? (float)Math.Clamp((tick - before.Tick) / (after.Tick - before.Tick), 0, 1) : 0;
            float angle = MathF.IEEERemainder(after.Yaw - before.Yaw, MathF.Tau);
            character = character with { Position = Vector3.Lerp(before.Position, after.Position, fraction), Yaw = before.Yaw + angle * fraction,
                DrawYaw = InterpolateDrawYaw(before.DrawYaw, before.Yaw, after.DrawYaw, after.Yaw, fraction) };
            if (tick > after.Tick && view is { TimeScale: > 0 } && entity.LastMotion is { } motion &&
                motion.Revision == after.Revision && (motion.Flags & MotionFlags.Moving) != 0 &&
                (motion.Flags & (MotionFlags.Carried | MotionFlags.Ragdoll | MotionFlags.Discontinuity)) == 0)
            {
                double seconds = Math.Min(MaximumMotionExtrapolationSeconds, (tick - after.Tick) * view.SimulationStepSeconds);
                var predicted = after.Position + motion.Velocity * (float)seconds;
                if (float.IsFinite(predicted.X) && float.IsFinite(predicted.Y) && float.IsFinite(predicted.Z))
                    character = character with { Position = predicted };
            }
        }
        if (entity.Animation is { } animation) character = character with { Animations = animation.Sample(tick) };
        return character;
    }
    static float InterpolateDrawYaw(float before, float beforeYaw, float after, float afterYaw, float fraction)
    {
        if (float.IsNaN(before) && float.IsNaN(after)) return float.NaN;
        float from = float.IsNaN(before) ? beforeYaw : before;
        float to = float.IsNaN(after) ? afterYaw : after;
        return from + MathF.IEEERemainder(to - from, MathF.Tau) * fraction;
    }
    public PlayerWorldView? Present(double tick)
    {
        AdvancePresentation(tick);
        if (view is null || !initialized || failed) return null;
        return view with { Snapshot = view.Snapshot with { Characters = entities.OrderBy(p => p.Key).Select(p => Sample(p.Value, presentedTick)).ToArray() } };
    }

    public void AdvancePresentation(double tick)
    {
        if (!double.IsFinite(tick) || tick < 0 || tick > AnimationPlaybackValidation.MaximumTick) throw new ArgumentOutOfRangeException(nameof(tick));
        presentedTick = Math.Max(presentedTick, tick);
        foreach (var entity in entities.Values) entity.Animation?.AdvanceTo(presentedTick);
    }
}
