using System.Buffers.Binary;
using System.Globalization;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Networking.VNext;

/// <summary>
/// Independently revisioned absolute digest-prefix buckets withdraw tag definitions without resending a whole membership table.
/// </summary>
internal sealed class ItemTagCollection : IGameCollection
{
    /// <summary>
    /// The fixed manifest has one record for each possible first digest byte.
    /// </summary>
    internal const int BucketCount = 256;

    /// <summary>
    /// All publications use these exact metadata keys; definition keys are their canonical 32-character digest.
    /// </summary>
    static readonly string[] BucketKeys = Enumerable.Range(0, BucketCount)
        .Select(i => "b:" + i.ToString("X2", CultureInfo.InvariantCulture)).ToArray();

    /// <summary>
    /// A bucket is an absolute membership value, not a delta against an earlier packet.
    /// </summary>
    sealed record Bucket(byte Prefix, IReadOnlyList<string> Digests, IReadOnlySet<string> Members);

    /// <summary>
    /// The shared item-tag field; full item updates contain only references to its definitions.
    /// </summary>
    public ushort Id => 1069;

    /// <summary>
    /// Emits buckets before definitions so a replacement first releases its withdrawn definitions' count and byte allowance.
    /// </summary>
    public (CollectionManifest Manifest, IReadOnlyList<(string Key, byte[] Value)> Items) Capture(
        WorldViewCodec codec, PlayerWorldView view, IReadOnlyDictionary<string, byte[]>? previous)
    {
        var definitions = view.Snapshot.ItemTags;
        ItemTagProjection.Validate(definitions);
        var groups = definitions.OrderBy(t => t.Digest, StringComparer.Ordinal)
            .GroupBy(t => t.Digest[..2]).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var entries = new List<(string Key, byte[] Value)>(BucketCount + definitions.Count);
        foreach (string key in BucketKeys)
        {
            var tags = groups.GetValueOrDefault(key[2..]) ?? [];
            var bytes = new byte[6 + tags.Length * 16];
            bytes[0] = 2;
            bytes[1] = byte.Parse(key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), tags.Length);
            for (int i = 0; i < tags.Length; i++) Convert.FromHexString(tags[i].Digest).CopyTo(bytes, 6 + i * 16);
            entries.Add((key, bytes));
        }
        foreach (var tag in definitions) entries.Add((tag.Digest, ItemTagCodec.Encode(tag)));
        return (new(true, true, BucketKeys), entries);
    }

    /// <summary>
    /// Validates the fixed outer manifest, leaving actual membership to the independently revisioned buckets.
    /// </summary>
    internal static void ValidateManifest(CollectionManifest manifest)
    {
        if (!manifest.Keys.SequenceEqual(BucketKeys)) throw new InvalidDataException("Invalid item tag bucket manifest.");
    }

    /// <summary>
    /// Only canonical metadata keys consume the separate bucket allowance.
    /// </summary>
    internal static bool IsBucket(string key) => key.Length == 4 && key.StartsWith("b:", StringComparison.Ordinal)
        && key.AsSpan(2).IndexOfAnyExcept("0123456789ABCDEF") < 0;

    /// <summary>
    /// Rejects definitions already withdrawn by a newer bucket while allowing entries that arrive before their own bucket update.
    /// </summary>
    internal static bool Accepts(GameCollectionState state, string key, ulong revision)
    {
        if (IsBucket(key)) return true;
        if (!ItemTagCodec.ValidDigest(key)) throw new InvalidDataException("Invalid item tag entry key.");
        return !state.Items.TryGetValue("b:" + key[..2], out var bucket) || bucket.Revision < revision
            || bucket.Value is Bucket membership && membership.Members.Contains(key);
    }

    /// <summary>
    /// Decodes either one exact tag definition or one bounded absolute bucket.
    /// </summary>
    public object Decode(WorldViewCodec codec, string key, byte[] value)
    {
        if (IsBucket(key)) return DecodeBucket(key, value);
        if (!ItemTagCodec.ValidDigest(key)) throw new InvalidDataException("Invalid item tag entry key.");
        var tag = ItemTagCodec.Decode(value);
        if (tag.Digest != key) throw new InvalidDataException("Item tag identity does not match its envelope.");
        return tag;
    }

    /// <summary>
    /// A bucket's member order is canonical and its prefix prevents one bucket from withdrawing another's definitions.
    /// </summary>
    static Bucket DecodeBucket(string key, ReadOnlySpan<byte> value)
    {
        if (value.Length < 6 || value[0] != 2 || value[1].ToString("X2", CultureInfo.InvariantCulture) != key[2..])
            throw new InvalidDataException("Invalid item tag membership bucket.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(value[2..]);
        if (count < 0 || count > 65536 || value.Length != 6 + count * 16)
            throw new InvalidDataException("Invalid item tag membership count.");
        var keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = Convert.ToHexString(value.Slice(6 + i * 16, 16));
            if (!keys[i].StartsWith(key[2..], StringComparison.Ordinal)
                || i > 0 && string.CompareOrdinal(keys[i - 1], keys[i]) >= 0)
                throw new InvalidDataException("Invalid item tag membership order.");
        }
        return new(value[1], keys, keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Stores bounded state and prunes old definitions as soon as their replacement bucket is accepted.
    /// </summary>
    internal static void Apply(GameCollectionState state, string key, object value, ulong revision, byte[] digest, int bytes)
    {
        state.Items.TryGetValue(key, out var previous);
        if (value is Bucket bucket)
        {
            int count = state.ItemTagMembers - (previous.Value is Bucket old ? old.Digests.Count : 0) + bucket.Digests.Count;
            // All bucket replacements in a publication precede definitions. A cross-bucket retag can temporarily count both
            // memberships; enforce the logical count only after this publication has applied every bucket.
            state.ItemTagMembers = count;
            string prefix = key[2..];
            foreach (var removed in state.Items.Where(p => p.Key.Length == 32 && p.Key.StartsWith(prefix, StringComparison.Ordinal)
                         && p.Value.Revision <= revision && !bucket.Members.Contains(p.Key)).ToArray())
            {
                state.ItemTagBytes -= removed.Value.Bytes;
                state.Items.Remove(removed.Key);
            }
            if (previous.Value is not Bucket) state.ItemTagBuckets++;
        }
        long total = state.ItemTagBytes - previous.Bytes + bytes;
        if (total > GameStateWire.MaximumBytes) throw new InvalidDataException("Item tag retention exceeds the collection byte bound.");
        state.ItemTagBytes = total;
        state.Items[key] = (revision, value, digest, bytes);
    }

    /// <summary>
    /// A baseline must contain every announced definition; ordinary out-of-order updates may temporarily show empty tag text.
    /// </summary>
    internal static void RequireComplete(GameCollectionState state)
    {
        foreach (string key in BucketKeys)
        {
            if (!state.Items.TryGetValue(key, out var entry) || entry.Value is not Bucket bucket)
                throw new InvalidDataException("Incomplete item tag bucket baseline.");
            foreach (string member in bucket.Digests)
                if (!state.Items.TryGetValue(member, out var definition) || definition.Value is not ItemTagView)
                    throw new InvalidDataException("Incomplete item tag definition baseline.");
        }
    }

    /// <summary>
    /// Only current members with available definitions reach the presentation lookup; pending and withdrawn entries remain private.
    /// </summary>
    public PlayerWorldView Build(PlayerWorldView view, GameCollectionState state)
    {
        if (state.ItemTagMembers > 65536) throw new InvalidDataException("Item tag membership exceeds the collection bound.");
        var tags = new List<ItemTagView>();
        foreach (string key in BucketKeys)
            if (state.Items.TryGetValue(key, out var entry) && entry.Value is Bucket bucket)
                foreach (string member in bucket.Digests)
                    if (state.Items.TryGetValue(member, out var definition) && definition.Value is ItemTagView tag) tags.Add(tag);
        return ItemTagProjection.WithDefinitions(view, tags);
    }
}
