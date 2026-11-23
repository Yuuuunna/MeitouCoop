using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Data.World;

namespace Meitou.Networking.VNext;
internal sealed record CollectionManifest(bool Present, bool Enabled, IReadOnlyList<string> Keys);
internal sealed class GameCollectionState
{
    internal ulong Revision;
    internal byte[] ManifestDigest = [];
    internal CollectionManifest Manifest = new(false, false, []);
    /// <summary>
    /// Current visual membership lets its manifest revision replace unbounded per-event deletion tombstones.
    /// </summary>
    internal IReadOnlySet<string> VisualKeys = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>
    /// Ahead-of-manifest visuals are staged separately from live membership for the existing count and byte limits.
    /// </summary>
    internal readonly Dictionary<string, int> VisualPendingSizes = new(StringComparer.Ordinal);
    /// <summary>
    /// Sum of staged entry envelopes; replacements update this ledger without scanning the pending set.
    /// </summary>
    internal long VisualPendingBytes;
    /// <summary>
    /// Distinct logical staged visuals, independent of whether one or both of their parts has arrived.
    /// </summary>
    internal int VisualPendingCount;
    internal readonly Dictionary<string, (ulong Revision, object? Value, byte[] Digest, int Bytes)> Items = new(StringComparer.Ordinal);
}

internal interface IGameCollection
{
    ushort Id { get; }

    (CollectionManifest Manifest, IReadOnlyList<(string Key, byte[] Value)> Items) Capture(WorldViewCodec codec, PlayerWorldView view, IReadOnlyDictionary<string, byte[]>? previous);
    object Decode(WorldViewCodec codec, string key, byte[] value);
    PlayerWorldView Build(PlayerWorldView view, GameCollectionState state);
}

internal static class GameCollectionSchema
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static readonly IGameCollection[] All = [new Collection<GroundItemSnapshot>(1011, v => v.Snapshot.GroundItems, v => v.Id.ToString(CultureInfo.InvariantCulture), (v, x, _, _) => v with { Snapshot = v.Snapshot with { GroundItems = x } }), new Collection<ContainerSpot>(1013, v => v.Snapshot.Containers, v => v.Id, (v, x, _, _) => v with { Snapshot = v.Snapshot with { Containers = x } }), new Collection<BedSpot>(1014, v => v.Snapshot.Beds, v => v.Id, (v, x, _, _) => v with { Snapshot = v.Snapshot with { Beds = x } }), new Collection<SeatSpot>(1016, v => v.Snapshot.Seats, v => v.Id, (v, x, _, _) => v with { Snapshot = v.Snapshot with { Seats = x } }), new Collection<CageSpot>(1017, v => v.Snapshot.Cages, v => v.Id, (v, x, _, _) => v with { Snapshot = v.Snapshot with { Cages = x } }), new Collection<WeatherRegionSnapshot>(9, v => v.Weather?.Regions ?? [], v => v.RegionId, (v, x, p, e) => v with { Weather = p ? new(v.Weather?.Time ?? default, x) { Enabled = e } : null }, v => v.Weather is not null, v => v.Weather?.Enabled ?? false),
        new ModVisualCollection(), ];
    internal static readonly IReadOnlySet<ushort> Ids = All.Select(s => s.Id).ToHashSet();

    /// <summary>
    /// Shared visuals have two independently revisioned records per logical identity; other collections retain one record per identity.
    /// </summary>
    internal static int MaximumKeys(ushort field) => field == 1068 ? ModVisualCollection.MaximumKeys : 65536;

    sealed class Collection<T>(ushort id, Func<PlayerWorldView, IReadOnlyList<T>> get, Func<T, string> key,
        Func<PlayerWorldView, IReadOnlyList<T>, bool, bool, PlayerWorldView> set, Func<PlayerWorldView, bool>? present = null,
        Func<PlayerWorldView, bool>? enabled = null, Func<T, byte[]>? encode = null, Func<byte[], T>? decode = null) : IGameCollection
    {
        public ushort Id => id;

        public (CollectionManifest Manifest, IReadOnlyList<(string Key, byte[] Value)> Items) Capture(WorldViewCodec codec, PlayerWorldView view, IReadOnlyDictionary<string, byte[]>? previous)
        {
            var values = get(view);
            if (values.Count > 65536)
                throw new InvalidDataException("Collection exceeds record bound.");
            var result = values.Select(value => (Key: key(value), Value: encode?.Invoke(value) ?? JsonComponentCompression.Encode(codec, value))).ToArray();
            if (result.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != result.Length)
                throw new InvalidDataException("Duplicate keyed collection identity.");
            foreach (var item in result)
                ValidateKey(item.Key);
            return (new(present?.Invoke(view) ?? true, enabled?.Invoke(view) ?? true, result.Select(p => p.Key).ToArray()), result);
        }

        public object Decode(WorldViewCodec codec, string expectedKey, byte[] value)
        {
            var item = decode is null ? JsonComponentCompression.Decode<T>(codec, value) : decode(value);
            if (key(item) != expectedKey)
                throw new InvalidDataException("Keyed value identity does not match its envelope.");
            return item!;
        }

        public PlayerWorldView Build(PlayerWorldView view, GameCollectionState state) => set(view, state.Manifest.Keys.Where(k => state.Items.TryGetValue(k, out var item) && item.Value is not null).Select(k => (T)state.Items[k].Value!).ToArray(), state.Manifest.Present, state.Manifest.Enabled);
    }

    internal static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) > 1024)
            throw new InvalidDataException("Invalid keyed collection identity.");
    }

    internal static byte[] Entry(string key, byte[] value)
    {
        byte[] name = Encoding.UTF8.GetBytes(key);
        byte[] bytes = new byte[2 + name.Length + value.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)name.Length);
        name.CopyTo(bytes, 2);
        value.CopyTo(bytes, 2 + name.Length);
        return bytes;
    }

    internal static (string Key, byte[] Value) ReadEntry(byte[] bytes)
    {
        if (bytes.Length < 3)
            throw new InvalidDataException("Truncated keyed entry.");
        int length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (length > 1024 || length > bytes.Length - 2)
            throw new InvalidDataException("Invalid keyed entry bounds.");
        string key = StrictUtf8.GetString(bytes, 2, length);
        ValidateKey(key);
        return (key, bytes.AsSpan(2 + length).ToArray());
    }

    /// <summary>
    /// The wire or expanded byte budget of one upsert, including its key. Shared visuals are exact binary; other collections use JSON.
    /// </summary>
    internal static int ExpandedEntrySize(ushort field, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 3) throw new InvalidDataException("Truncated keyed entry.");

        int length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (length > 1024 || length >= bytes.Length - 2)
            throw new InvalidDataException("Invalid keyed entry bounds.");

        return 2 + length + ExpandedItemSize(field, bytes[(2 + length)..]);
    }

    /// <summary>
    /// A binary visual has no nested compression; only the complete lifecycle publication may use its bounded compression envelope.
    /// </summary>
    internal static int ExpandedItemSize(ushort field, ReadOnlySpan<byte> bytes) => field == 1068 ? bytes.Length
        : Math.Max(bytes.Length, JsonComponentCompression.DecodedLength(bytes));

    internal static byte[] EncodeManifest(CollectionManifest manifest, int maximumKeys = 65536)
    {
        int count = manifest.Keys.Count;
        if (count > maximumKeys)
            throw new InvalidDataException("Collection exceeds record bound.");
        long[]? ints = count > 0 ? new long[count] : null;
        if (ints is not null)
        {
            for (int i = 0; i < count; i++)
            {
                string key = manifest.Keys[i];
                if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out long v) || v < 0 || v.ToString(CultureInfo.InvariantCulture) != key)
                {
                    ints = null;
                    break;
                }

                ints[i] = v;
            }
        }

        using var stream = new MemoryStream(ints is not null ? 32 : Math.Min(65536, 8 + count * 16));
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        byte flags = (byte)((manifest.Present ? 1 : 0) | (manifest.Enabled ? 2 : 0) | (ints is not null ? 4 : 0));
        writer.Write(flags);
        writer.Write7BitEncodedInt(count);
        if (ints is not null)
        {
            var runs = new List<(long Start, int Extra)>();
            for (int i = 0; i < ints.Length;)
            {
                long start = ints[i];
                int extra = 0;
                while (i + 1 + extra < ints.Length && ints[i + extra] < long.MaxValue && ints[i + 1 + extra] == ints[i + extra] + 1)
                    extra++;
                runs.Add((start, extra));
                i += 1 + extra;
            }

            writer.Write7BitEncodedInt(runs.Count);
            long prevEnd = 0;
            foreach (var(start, extra)in runs)
            {
                long delta = start - prevEnd;
                ulong zigzag = (ulong)((delta << 1) ^ (delta >> 63));
                writer.Write7BitEncodedInt64(unchecked((long)zigzag));
                writer.Write7BitEncodedInt(extra);
                prevEnd = start + extra;
            }
        }
        else
        {
            foreach (var key in manifest.Keys)
            {
                ValidateKey(key);
                byte[] utf8 = StrictUtf8.GetBytes(key);
                writer.Write7BitEncodedInt(utf8.Length);
                writer.Write(utf8);
            }
        }

        return stream.ToArray();
    }

    internal static CollectionManifest DecodeManifest(byte[] bytes, int maximumKeys = 65536)
    {
        if (bytes.Length < 3 || bytes[0] != 1 || (bytes[1] & ~0x07) != 0)
            throw new InvalidDataException("Invalid collection manifest header.");
        bool present = (bytes[1] & 1) != 0, enabled = (bytes[1] & 2) != 0, integerKeys = (bytes[1] & 4) != 0;
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        try
        {
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            int count = reader.Read7BitEncodedInt();
            if (count < 0 || count > maximumKeys || (count == 0 && integerKeys))
                throw new InvalidDataException("Invalid collection manifest count.");
            var keys = new string[count];
            if (integerKeys)
            {
                int runs = reader.Read7BitEncodedInt();
                if (runs <= 0 || runs > count)
                    throw new InvalidDataException("Invalid collection manifest runs.");
                long prevEnd = 0;
                int index = 0;
                for (int r = 0; r < runs; r++)
                {
                    ulong zigzag = unchecked((ulong)reader.Read7BitEncodedInt64());
                    long delta = (long)(zigzag >> 1) ^ -(long)(zigzag & 1UL);
                    int extra = reader.Read7BitEncodedInt();
                    Int128 start128 = (Int128)prevEnd + delta;
                    if (extra < 0 || extra > count - 1 - index || start128 < 0 || start128 + extra > long.MaxValue)
                        throw new InvalidDataException("Invalid collection manifest integer run.");
                    long start = (long)start128;
                    for (int j = 0; j <= extra; j++)
                        keys[index++] = (start + j).ToString(CultureInfo.InvariantCulture);
                    prevEnd = start + extra;
                }

                if (index != count)
                    throw new InvalidDataException("Incomplete collection manifest runs.");
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int len = reader.Read7BitEncodedInt();
                    if (len <= 0 || len > 1024 || len > stream.Length - stream.Position)
                        throw new InvalidDataException("Invalid collection manifest key length.");
                    keys[i] = StrictUtf8.GetString(reader.ReadBytes(len));
                }
            }

            if (stream.Position != stream.Length)
                throw new InvalidDataException("Trailing collection manifest bytes.");
            return new(present, enabled, keys);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Truncated collection manifest.", e);
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("Invalid collection manifest varint.", e);
        }
        catch (DecoderFallbackException e)
        {
            throw new InvalidDataException("Invalid UTF-8 in collection manifest.", e);
        }
    }
}

public sealed partial class GameStatePublisher
{
    readonly Dictionary<ushort, EncodedCollection> _collections = new();
    /// <summary>
    /// Definitions and membership announced through ordered lifecycle delivery, independent of newer values captured into delayed bulk.
    /// The initial baseline seeds both ledgers under its Ready transaction.
    /// </summary>
    readonly Dictionary<string, ulong> _criticalVisualDefinitions = new(StringComparer.Ordinal);
    CollectionManifest? _criticalVisualMembership;
    /// <summary>
    /// Ordinary bulk can encode A, then B, then A before lifecycle capture. A lane-local dirty fence reasserts the latest value despite equality.
    /// </summary>
    readonly HashSet<string> _criticalVisualDirty = new(StringComparer.Ordinal);
    bool _criticalVisualMembershipDirty;
    sealed class EncodedCollection(CollectionManifest manifest, Dictionary<string, byte[]> items)
    {
        internal CollectionManifest Manifest = manifest;
        internal Dictionary<string, byte[]> Items = items;
    }

    long CaptureCollections(PlayerWorldView view, Action<byte, ushort, byte[]> add, ushort? onlyField = null)
    {
        long total = 0;
        foreach (var schema in GameCollectionSchema.All)
        {
            if (onlyField is { } field && field != schema.Id) continue;
            _collections.TryGetValue(schema.Id, out var previousCollection);
            var captured = schema.Capture(codec, view, previousCollection?.Items);
            var current = captured.Items.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            byte[]? encodedManifest = null;
            bool criticalVisuals = schema.Id == 1068 && onlyField == 1068;
            bool changedMembership = previousCollection is null || !SameManifest(captured.Manifest, previousCollection.Manifest);
            if (schema.Id == 1068 && !criticalVisuals && _sequence != 1 && changedMembership) _criticalVisualMembershipDirty = true;
            bool announceVisualMembership = criticalVisuals && (_criticalVisualMembershipDirty || !SameManifest(captured.Manifest, _criticalVisualMembership));
            if (changedMembership || announceVisualMembership)
            {
                encodedManifest = GameCollectionSchema.EncodeManifest(captured.Manifest, GameCollectionSchema.MaximumKeys(schema.Id));
                add(6, schema.Id, encodedManifest);
            }

            total += (encodedManifest?.Length ?? 16) + 19;
            total += current.Sum(p => (long)GameCollectionSchema.ExpandedItemSize(schema.Id, p.Value)
                + Encoding.UTF8.GetByteCount(p.Key) + 21);
            if (total > GameStateWire.MaximumBytes)
                throw new InvalidDataException("Keyed collection capture exceeds bulk bound.");
            foreach (var item in captured.Items)
            {
                bool changed = previousCollection is null || !previousCollection.Items.TryGetValue(item.Key, out var old)
                    || !old.AsSpan().SequenceEqual(item.Value);
                if (schema.Id == 1068 && !criticalVisuals && _sequence != 1 && changed) _criticalVisualDirty.Add(item.Key);
                if (changed || criticalVisuals && (NeedsVisualDefinition(item.Key, item.Value) || _criticalVisualDirty.Contains(item.Key)))
                    add(7, schema.Id, GameCollectionSchema.Entry(item.Key, item.Value));
            }
            // Transient visuals withdraw through their complete manifest. Separate removals would break the complete old manifest while
            // a large critical burst is still delivering its new entries, and would retain redundant per-event deletion tombstones.
            if (previousCollection is not null && schema.Id != 1068)
                foreach (var key in previousCollection.Items.Keys.Where(k => !current.ContainsKey(k)))
                {
                    total += Encoding.UTF8.GetByteCount(key) + 21L;
                    if (total > GameStateWire.MaximumBytes)
                        throw new InvalidDataException("Keyed collection capture exceeds bulk bound.");
                    add(8, schema.Id, GameCollectionSchema.Entry(key, []));
                }
            _collections[schema.Id] = new(captured.Manifest, current);
            if (schema.Id == 1068) RememberVisualDefinitions(current, captured.Manifest, criticalVisuals || _sequence == 1);
        }

        return total;
    }

    /// <summary>
    /// Membership equality does not depend on allocation identity.
    /// </summary>
    static bool SameManifest(CollectionManifest value, CollectionManifest? previous) => previous is not null
        && value.Present == previous.Present && value.Enabled == previous.Enabled && value.Keys.SequenceEqual(previous.Keys);

    /// <summary>
    /// A dynamic reading may have been captured after its definition entered ordinary bulk. The lifecycle lane must declare both together.
    /// </summary>
    bool NeedsVisualDefinition(string key, byte[] value)
    {
        string definition = ModVisualCollection.IsDefinition(key) ? key : ModVisualCollection.Partner(key);
        return _criticalVisualDefinitions.GetValueOrDefault(definition) != ModVisualCodec.DefinitionVersion(value);
    }

    /// <summary>
    /// Commits only the lane's own declarations and discards departed identities, so transient effects cannot grow this ledger indefinitely.
    /// </summary>
    void RememberVisualDefinitions(Dictionary<string, byte[]> current, CollectionManifest membership, bool announced)
    {
        foreach (string key in _criticalVisualDefinitions.Keys.Where(key => !current.ContainsKey(key)).ToArray())
            _criticalVisualDefinitions.Remove(key);
        _criticalVisualDirty.RemoveWhere(key => !current.ContainsKey(key));
        if (!announced) return;
        foreach (var item in current)
            if (ModVisualCollection.IsDefinition(item.Key)) _criticalVisualDefinitions[item.Key] = ModVisualCodec.DefinitionVersion(item.Value);
        _criticalVisualMembership = membership;
        _criticalVisualDirty.Clear();
        _criticalVisualMembershipDirty = false;
    }
}

public sealed partial class GameStateReplica
{
    readonly Dictionary<ushort, GameCollectionState> _collections = new();
    void ApplyCollection(byte kind, ulong entity, uint generation, ushort field, byte[] value, ulong revision)
    {
        if (entity != 0 || generation != 0 || !GameCollectionSchema.Ids.Contains(field))
            throw new InvalidDataException("Invalid collection scope or generation.");
        var schema = GameCollectionSchema.All.Single(s => s.Id == field);
        if (!_collections.TryGetValue(field, out var state))
            _collections[field] = state = new();
        if (kind == 6)
        {
            if (state.Revision > revision)
                return;
            byte[] digest = SHA256.HashData(value);
            if (state.Revision == revision)
            {
                if (!digest.AsSpan().SequenceEqual(state.ManifestDigest))
                    throw new InvalidDataException("Conflicting collection manifest.");
                return;
            }

            var manifest = GameCollectionSchema.DecodeManifest(value, GameCollectionSchema.MaximumKeys(field));
            if (field != 9 && (!manifest.Present || !manifest.Enabled) || !manifest.Present && manifest.Enabled)
                throw new InvalidDataException("Invalid collection presence flags.");
            if (manifest.Keys is null || manifest.Keys.Count > GameCollectionSchema.MaximumKeys(field) || manifest.Keys.Distinct(StringComparer.Ordinal).Count() != manifest.Keys.Count || !manifest.Present && manifest.Keys.Count != 0 || !manifest.Enabled && manifest.Keys.Count != 0)
                throw new InvalidDataException("Invalid collection manifest.");
            foreach (var key in manifest.Keys)
                GameCollectionSchema.ValidateKey(key);
            state.Manifest = manifest;
            state.Revision = revision;
            state.ManifestDigest = digest;
            if (field == 1068)
            {
                state.VisualKeys = ModVisualCollection.ValidateManifest(manifest.Keys);
                foreach (var removed in state.Items.Where(pair => pair.Value.Revision <= revision && !state.VisualKeys.Contains(pair.Key)).ToArray())
                    state.Items.Remove(removed.Key);
                RebuildVisualPending(state);
                if (LiveCollectionItems() > 65536) throw new InvalidDataException("Keyed entry fence bound exceeded.");
            }
            _revisions[(0, field)] = revision;
            _valueDigests[(0, field)] = digest;
        }
        else
        {
            var entry = GameCollectionSchema.ReadEntry(value);
            // A newer manifest already withdrew this transient ID. Its global revision fences late bulk without keeping one tombstone per effect.
            if (field == 1068 && state.Revision >= revision && !state.VisualKeys.Contains(entry.Key)) return;
            state.Items.TryGetValue(entry.Key, out var old);
            if (old.Revision > revision)
                return;
            byte[] digest = SHA256.HashData(value);
            if (old.Revision == revision)
            {
                if (!digest.AsSpan().SequenceEqual(old.Digest))
                    throw new InvalidDataException("Conflicting keyed entry revision.");
                return;
            }

            bool pendingVisual = field == 1068 && !state.VisualKeys.Contains(entry.Key);
            if (field != 1068 && LiveCollectionItems() >= 65536 && !state.Items.ContainsKey(entry.Key))
                throw new InvalidDataException("Keyed entry fence bound exceeded.");
            if (kind == 8 && entry.Value.Length != 0 || kind == 7 && entry.Value.Length == 0)
                throw new InvalidDataException("Invalid keyed entry operation.");
            int bytes = checked(value.Length + 19);
            if (pendingVisual) ReserveVisualPending(state, entry.Key, bytes);
            state.Items[entry.Key] = (revision, kind == 7 ? schema.Decode(codec, entry.Key, entry.Value) : null, digest, bytes);
        }
    }

    /// <summary>
    /// Pending visual values do not consume the active-collection identity allowance; they have their own bounded staging ledger.
    /// </summary>
    int LiveCollectionItems() => _collections.Sum(pair => pair.Key == 1068 ? pair.Value.VisualKeys.Count / 2 : pair.Value.Items.Count);

    /// <summary>
    /// A complete new manifest may activate staged entries, retire old live values or leave newer out-of-scope entries pending.
    /// This linear pass runs once per membership change, never for every upsert in a large burst.
    /// </summary>
    static void RebuildVisualPending(GameCollectionState state)
    {
        state.VisualPendingSizes.Clear();
        state.VisualPendingBytes = 0;
        state.VisualPendingCount = 0;
        foreach (var item in state.Items)
            if (!state.VisualKeys.Contains(item.Key)) ReserveVisualPending(state, item.Key, item.Value.Bytes);
    }

    /// <summary>
    /// Reserves the exact staged entry envelope before decoding. A valid replacement can stage one full next collection while keeping
    /// the old manifest complete, without extending either the 65536 live-identity limit or the 32 MiB pending-byte limit.
    /// </summary>
    static void ReserveVisualPending(GameCollectionState state, string key, int bytes)
    {
        long total = state.VisualPendingBytes - state.VisualPendingSizes.GetValueOrDefault(key) + bytes;
        string partner = ModVisualCollection.Partner(key);
        bool newIdentity = !state.VisualPendingSizes.ContainsKey(key) && !state.VisualPendingSizes.ContainsKey(partner);
        if (total > GameStateWire.MaximumBytes || newIdentity && state.VisualPendingCount >= 65536)
            throw new InvalidDataException("Shared visual staging exceeds collection bounds.");
        if (newIdentity) state.VisualPendingCount++;
        state.VisualPendingSizes[key] = bytes;
        state.VisualPendingBytes = total;
    }

    void RefreshCollections(bool requireComplete)
    {
        foreach (var schema in GameCollectionSchema.All)
            if (_collections.TryGetValue(schema.Id, out var state) && state.Revision != 0)
            {
                if (requireComplete && state.Manifest.Keys.Any(k => !state.Items.TryGetValue(k, out var item) || item.Value is null))
                    throw new InvalidDataException("Incomplete keyed collection baseline.");
                _view = schema.Build(_view!, state);
            }
    }
}
