using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Data.Characters;
using Meitou.Simulation;

namespace Meitou.Networking;

public sealed record ReplicationIdentity(Guid World, PlayerId Player, ulong Generation)
{
    public static ReplicationIdentity Of(PlayerWorldView view) => new(view.World, view.Player, view.ConnectionGeneration);
    internal void Validate()
    {
        if (World == Guid.Empty || Player.IsNone || Generation == 0) throw new InvalidDataException("Invalid replication identity.");
    }
}
public enum StateRemovalReason { OutOfScope, Destroyed }
public sealed record StateRemoval(string Key, StateRemovalReason Reason);
public sealed record StateChange(string Key, JsonElement Value);
public sealed record StateAcknowledgement(ReplicationIdentity Identity, ulong Publication, string Hash);
public sealed record StatePatch(ReplicationIdentity Identity, ulong Publication, ulong BasePublication, string Hash,
    StateChange[] Changes, StateRemoval[] Removals);
public sealed record EncodedPublication(ulong Publication, bool Baseline, string Hash, byte[] Payload);
public enum ReplicaResult { Applied, Stale, NeedsBaseline }

/// <summary>Frozen keyed components; replace whole entries so omitted/revoked private fields cannot survive a delta merge.</summary>
internal sealed partial class StateDocument
{
    internal readonly Dictionary<string, byte[]> Entries;
    internal int ByteCount { get; }
    internal string Hash { get; }
    internal StateDocument(Dictionary<string, byte[]> entries)
    {
        if (!entries.ContainsKey("view") || entries.Count > 131072) throw new InvalidDataException("Incomplete or oversized state document.");
        Entries = entries;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var pair in entries.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ValidateKey(pair.Key);
            byte[] key = Encoding.UTF8.GetBytes(pair.Key);
            ByteCount = checked(ByteCount + key.Length + pair.Value.Length + 8);
            if (ByteCount > WorldViewCodec.MaximumBytes) throw new InvalidDataException("State document is too large.");
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, key.Length); hash.AppendData(length); hash.AppendData(key);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, pair.Value.Length); hash.AppendData(length); hash.AppendData(pair.Value);
        }
        Hash = Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 1024 || key is not ("view" or "prospect" or "messages") && !(key.StartsWith("c:", StringComparison.Ordinal) ||
            key.StartsWith("a:", StringComparison.Ordinal) || key.StartsWith("b:", StringComparison.Ordinal) || key.StartsWith("m:", StringComparison.Ordinal) || key.StartsWith("p:", StringComparison.Ordinal) || key.StartsWith("f:", StringComparison.Ordinal) || key.StartsWith("w:", StringComparison.Ordinal) || key.StartsWith("e:", StringComparison.Ordinal)))
            throw new InvalidDataException("Unknown state component key.");
    }
    internal StatePatch Difference(ReplicationIdentity identity, ulong publication, StateDocument? baseline, ulong basePublication, Func<CharacterId, bool>? destroyed)
    {
        var changes = Entries.Where(p => baseline is null || !baseline.Entries.TryGetValue(p.Key, out var before) || !before.AsSpan().SequenceEqual(p.Value))
            .Select(p => new StateChange(p.Key, JsonSerializer.Deserialize<JsonElement>(p.Value))).ToArray();
        var removed = baseline?.Entries.Keys.Where(k => !Entries.ContainsKey(k)).Select(k =>
        {
            var parts = k.Split(':');
            bool dead = parts.Length == 3 && parts[0] == "c" && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out int slot)
                && int.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out int generation)
                && destroyed?.Invoke(new(slot, generation)) == true;
            return new StateRemoval(k, dead ? StateRemovalReason.Destroyed : StateRemovalReason.OutOfScope);
        }).ToArray() ?? [];
        return new(identity, publication, basePublication, Hash, changes, removed);
    }
    internal static StateDocument Apply(StatePatch patch, StateDocument? baseline)
    {
        var entries = baseline is null ? new Dictionary<string, byte[]>(StringComparer.Ordinal) : new(baseline.Entries, StringComparer.Ordinal);
        foreach (var removal in patch.Removals)
            if (removal.Key == "view" || !entries.Remove(removal.Key)) throw new InvalidDataException("Unknown removed component.");
        foreach (var change in patch.Changes) entries[change.Key] = Encoding.UTF8.GetBytes(change.Value.GetRawText());
        var result = new StateDocument(entries);
        if (!string.Equals(result.Hash, patch.Hash, StringComparison.Ordinal)) throw new InvalidDataException("State hash does not match.");
        return result;
    }
}

public static class StatePatchProtocol
{
    public const int MaximumBytes = WorldViewCodec.MaximumBytes + 4 * 1024 * 1024;
    static readonly JsonSerializerOptions json = new()
    {
        MaxDepth = 48, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    internal static byte[] Encode(StatePatch patch)
    {
        Validate(patch);
        using var buffer = new BoundedBuffer(MaximumBytes); JsonSerializer.Serialize(buffer, patch, json);
        // This internal writer receives typed metadata and JsonElements from frozen documents.
        // Parsing the resulting JSON again cannot add trust; Decode performs the inbound checks.
        return buffer.ToArray();
    }
    public static StatePatch Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumBytes) throw new InvalidDataException("State patch is too large.");
        try
        {
            WorldViewCodec.ValidateJson(payload);
            var patch = JsonSerializer.Deserialize<StatePatch>(payload, json) ?? throw new InvalidDataException("Missing state patch.");
            Validate(patch); return patch;
        }
        catch (JsonException e) { throw new InvalidDataException("Malformed state patch.", e); }
    }
    static void Validate(StatePatch p)
    {
        if (p.Identity is null) throw new InvalidDataException("Missing replication identity.");
        p.Identity.Validate();
        if (p.Publication == 0 || p.BasePublication >= p.Publication || p.Hash is null || p.Hash.Length != 64 || p.Hash.Any(c => !char.IsAsciiHexDigitUpper(c)) ||
            p.Changes is null || p.Removals is null || p.Changes.Length + p.Removals.Length > 131072 || p.BasePublication == 0 && p.Removals.Length != 0)
            throw new InvalidDataException("Invalid patch envelope.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in p.Changes)
        {
            if (c is null || c.Value.ValueKind != JsonValueKind.Object || !keys.Add(c.Key)) throw new InvalidDataException("Invalid or repeated changed component.");
            StateDocument.ValidateKey(c.Key);
        }
        foreach (var r in p.Removals)
        {
            if (r is null || !Enum.IsDefined(r.Reason) || !keys.Add(r.Key)) throw new InvalidDataException("Invalid or repeated removal.");
            StateDocument.ValidateKey(r.Key);
        }
    }
}

/// <summary>One authenticated connection's bounded state history. Called serially by its host.</summary>
public sealed class ServerStateReplicator(ReplicationIdentity identity, WorldViewCodec codec)
{
    const int HistoryLimit = 8, HistoryBytes = 64 * 1024 * 1024;
    readonly SortedDictionary<ulong, StateDocument> pending = [];
    StateDocument? acknowledged;
    ulong acknowledgedSequence, lastSequence;
    bool baselineRequested;
    long epoch;
    public bool HasAcknowledgedBaseline => acknowledged is not null;
    public EncodedPublication Publish(PlayerWorldView view, Func<CharacterId, bool>? confirmedDestroyed = null)
    {
        var work = Prepare(view, confirmedDestroyed);
        work.Encode(split: false);
        return Commit(work) ?? throw new InvalidOperationException("Publication was superseded.");
    }
    internal PublicationWork Prepare(PlayerWorldView view, Func<CharacterId, bool>? confirmedDestroyed)
    {
        if (ReplicationIdentity.Of(view) != identity || view.Publication <= lastSequence) throw new InvalidOperationException("State belongs to another or older connection publication.");
        bool full = acknowledged is null || baselineRequested;
        var baseline = full ? null : acknowledged;
        var present = view.Snapshot.Characters.Select(c => c.Id).ToHashSet();
        var destroyed = new HashSet<CharacterId>();
        if (baseline is not null && confirmedDestroyed is not null)
            foreach (var key in baseline.Entries.Keys.Where(k => k.StartsWith("c:", StringComparison.Ordinal)))
            {
                var parts = key.Split(':');
                var id = new CharacterId(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
                if (!present.Contains(id) && confirmedDestroyed(id)) destroyed.Add(id);
            }
        return new(this, epoch, identity, codec, view, baseline, full ? 0 : acknowledgedSequence, destroyed);
    }
    internal EncodedPublication? Commit(PublicationWork work)
    {
        if (!ReferenceEquals(work.Owner, this) || work.Epoch != epoch || work.Publication <= lastSequence) return null;
        var document = work.Document ?? throw new InvalidOperationException("Publication has not been encoded.");
        pending.Add(work.Publication, document); lastSequence = work.Publication; baselineRequested = false;
        while (pending.Count > HistoryLimit || pending.Values.Sum(d => (long)d.ByteCount) + (acknowledged?.ByteCount ?? 0) > HistoryBytes)
            pending.Remove(pending.Keys.First());
        return work.Encoded;
    }
    public bool Acknowledge(StateAcknowledgement ack)
    {
        if (ack.Identity != identity) return false;
        if (ack.Publication == acknowledgedSequence) return acknowledged is not null && ack.Hash == acknowledged.Hash;
        if (ack.Publication < acknowledgedSequence || !pending.TryGetValue(ack.Publication, out var doc) || ack.Hash != doc.Hash) return false;
        acknowledged = doc; acknowledgedSequence = ack.Publication;
        foreach (var number in pending.Keys.Where(n => n <= acknowledgedSequence).ToArray()) pending.Remove(number);
        return true;
    }
    public void RequestBaseline() { baselineRequested = true; epoch++; }
    public void Withdraw(ulong publication)
    {
        lastSequence = Math.Max(lastSequence, publication); pending.Clear(); acknowledged = null; acknowledgedSequence = 0; RequestBaseline();
    }
}

/// <summary>Detached encoding job. Encode once off the owner; commit and authority checks remain on the owner.</summary>
public sealed class PublicationWork
{
    internal ServerStateReplicator Owner { get; }
    internal long Epoch { get; }
    public ulong Publication => view.Publication;
    readonly ReplicationIdentity identity;
    readonly WorldViewCodec codec;
    readonly PlayerWorldView view;
    readonly StateDocument? baseline;
    readonly ulong basePublication;
    readonly HashSet<CharacterId> destroyed;
    internal StateDocument? Document { get; private set; }
    internal EncodedPublication? Encoded { get; private set; }
    internal IReadOnlyList<WireFrame> Frames { get; private set; } = [];
    internal PublicationWork(ServerStateReplicator owner, long epoch, ReplicationIdentity identity, WorldViewCodec codec,
        PlayerWorldView view, StateDocument? baseline, ulong basePublication, HashSet<CharacterId> destroyed)
        => (Owner, Epoch, this.identity, this.codec, this.view, this.baseline, this.basePublication, this.destroyed)
            = (owner, epoch, identity, codec, view, baseline, basePublication, destroyed);
    public void Encode() => Encode(split: true);
    internal void Encode(bool split)
    {
        Document = StateDocument.FreezeAgainst(codec, view, baseline);
        var patch = Document.Difference(identity, Publication, baseline, basePublication, destroyed.Contains);
        Encoded = new(Publication, basePublication == 0, Document.Hash, StatePatchProtocol.Encode(patch));
        if (split) Frames = StateTransport.Split(identity, Encoded);
    }
}

/// <summary>Client-only replica; never owns or advances a Simulation.World.</summary>
public sealed class ClientWorldReplica(ReplicationIdentity identity, WorldViewCodec codec)
{
    const int HistoryLimit = 8, HistoryBytes = 64 * 1024 * 1024;
    readonly SortedDictionary<ulong, StateDocument> history = [];
    Dictionary<string, StateDocument.Decoded> components = [];
    ulong highestPublication;
    public ulong HighestPublication => highestPublication;
    public PlayerWorldView? Current { get; private set; }
    public bool NeedsBaseline { get; private set; } = true;
    public IReadOnlyList<StateRemoval> LastRemovals { get; private set; } = [];
    public StateAcknowledgement? Acknowledgement => Current is { } view && history.TryGetValue(view.Publication, out var doc)
        ? new(identity, view.Publication, doc.Hash) : null;
    public ReplicaResult Receive(ReadOnlySpan<byte> payload, ulong? expectedPublication = null)
    {
        try
        {
            var patch = StatePatchProtocol.Decode(payload);
            if (patch.Identity != identity) throw new InvalidDataException("State addressed to another connection.");
            if (expectedPublication is { } expected && patch.Publication != expected)
                throw new InvalidDataException("Chunk identity disagrees with publication.");
            if (patch.Publication <= highestPublication) return ReplicaResult.Stale;
            highestPublication = patch.Publication;
            StateDocument? baseline = null;
            if (patch.BasePublication != 0 && (NeedsBaseline || !history.TryGetValue(patch.BasePublication, out baseline)))
            {
                if (!NeedsBaseline)
                    Console.WriteLine($"network   {DateTime.UtcNow:O} missing-base publication={patch.Publication} base={patch.BasePublication} current={Current?.Publication} history=[{string.Join(',', history.Keys)}] historyBytes={history.Values.Sum(d => (long)d.ByteCount)}; clearing view and requesting baseline");
                Reset(); return ReplicaResult.NeedsBaseline;
            }
            var document = StateDocument.Apply(patch, baseline);
            var view = document.RestoreCached(codec, components, out var nextComponents);
            if (ReplicationIdentity.Of(view) != identity || view.Publication != patch.Publication) throw new InvalidDataException("State identity disagrees with its envelope.");
            components = nextComponents;
            // A newer baseline may cross an ACK for an earlier one. Keep bounded history for deltas
            // still based on that acknowledged state; the new baseline itself never merges old entries.
            history[patch.Publication] = document;
            while (history.Count > HistoryLimit || history.Values.Sum(d => (long)d.ByteCount) > HistoryBytes) history.Remove(history.Keys.First());
            if (NeedsBaseline)
                Console.WriteLine($"network   {DateTime.UtcNow:O} replica-restored publication={patch.Publication} base={patch.BasePublication} documentBytes={document.ByteCount} historyCount={history.Count} characters={view.Snapshot.Characters.Count} tick={view.Snapshot.Tick} speed={view.TimeScale}");
            Current = view; NeedsBaseline = false; LastRemovals = patch.Removals;
            return ReplicaResult.Applied;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException or InvalidDataException or OverflowException or FormatException)
        {
            Console.WriteLine($"network   {DateTime.UtcNow:O} invalid-state publication={highestPublication} errorType={e.GetType().Name}; clearing view");
            Reset(); throw new InvalidDataException("Invalid replicated state; a fresh baseline is required.", e);
        }
    }
    public void Reset() { Current = null; history.Clear(); components.Clear(); NeedsBaseline = true; LastRemovals = []; }
    public ReplicaResult Withdraw(ulong publication)
    {
        if (publication <= highestPublication) return ReplicaResult.Stale;
        if (!NeedsBaseline)
            Console.WriteLine($"network   {DateTime.UtcNow:O} server-withdrawal publication={publication} current={Current?.Publication}; clearing view");
        highestPublication = publication; Reset(); return ReplicaResult.NeedsBaseline;
    }
}
