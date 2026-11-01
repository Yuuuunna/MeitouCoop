using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
internal sealed class StateDocument
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
            key.StartsWith("b:", StringComparison.Ordinal) || key.StartsWith("m:", StringComparison.Ordinal) || key.StartsWith("p:", StringComparison.Ordinal) || key.StartsWith("f:", StringComparison.Ordinal) || key.StartsWith("w:", StringComparison.Ordinal) || key.StartsWith("e:", StringComparison.Ordinal)))
            throw new InvalidDataException("Unknown state component key.");
    }
    internal static StateDocument Freeze(WorldViewCodec codec, PlayerWorldView view)
    {
        var root = JsonNode.Parse(codec.Encode(view))!.AsObject();
        var snapshot = root["Snapshot"]!.AsObject();
        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        void Extract(string field, Func<JsonNode, string> key)
        {
            foreach (var node in snapshot[field]!.AsArray())
                if (node is null || !entries.TryAdd(key(node), Encoding.UTF8.GetBytes(node.ToJsonString()))) throw new InvalidDataException("Duplicate state component.");
            snapshot[field] = new JsonArray();
        }
        Extract("Characters", n => FormattableString.Invariant($"c:{n["Id"]!["Slot"]!.GetValue<int>()}:{n["Id"]!["Generation"]!.GetValue<int>()}"));
        Extract("Buildings", n => "b:" + n["Id"]!.GetValue<string>());
        Extract("Machines", n => "m:" + n["Site"]!.GetValue<string>());
        Extract("Projectiles", n => "p:" + n["Id"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (root["Prospected"] is { } report) entries.Add("prospect", Encoding.UTF8.GetBytes(report.ToJsonString()));
        root["Prospected"] = null;
        if (view.Messages.HighWater > 0) entries.Add("messages", Encoding.UTF8.GetBytes(root["Messages"]!.ToJsonString()));
        root["Messages"] = null;
        foreach (var faction in root["FactionCatalogue"]!.AsArray())
            if (faction is null || !entries.TryAdd("f:" + faction["Faction"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8.GetBytes(faction.ToJsonString())))
                throw new InvalidDataException("Duplicate faction identity.");
        root["FactionCatalogue"] = new JsonArray();
        if (root["Weather"] is { } weather)
        {
            foreach (var region in weather["Regions"]!.AsArray())
                if (region is null || !entries.TryAdd("w:" + region["RegionId"]!.GetValue<string>(), Encoding.UTF8.GetBytes(region.ToJsonString())))
                    throw new InvalidDataException("Duplicate weather region.");
            weather["Regions"] = new JsonArray();
        }
        if (root["WeatherEffects"] is { } effects)
        {
            foreach (var node in effects["Nodes"]!.AsArray())
                if (node is null || !entries.TryAdd("e:" + node["Id"]!.GetValue<string>(), Encoding.UTF8.GetBytes(node.ToJsonString())))
                    throw new InvalidDataException("Duplicate effect node.");
            effects["Nodes"] = new JsonArray();
        }
        entries.Add("view", Encoding.UTF8.GetBytes(root.ToJsonString()));
        return new(entries);
    }
    internal PlayerWorldView Restore(WorldViewCodec codec)
    {
        var root = JsonNode.Parse(Entries["view"])!.AsObject();
        var snapshot = root["Snapshot"]!.AsObject();
        if (root["Prospected"] is not null) throw new InvalidDataException("State shell contains an embedded survey.");
        if (root["Messages"] is not null) throw new InvalidDataException("State shell contains embedded messages.");
        if (root["FactionCatalogue"] is not JsonArray { Count: 0 }) throw new InvalidDataException("State shell contains embedded factions.");
        if (root["Weather"] is { } weather && weather["Regions"] is not JsonArray { Count: 0 }) throw new InvalidDataException("State shell contains embedded weather regions.");
        if (root["WeatherEffects"] is { } effects && effects["Nodes"] is not JsonArray { Count: 0 }) throw new InvalidDataException("State shell contains embedded effect nodes.");
        foreach (string field in new[] { "Characters", "Buildings", "Machines", "Projectiles" })
            if (snapshot[field] is not JsonArray { Count: 0 }) throw new InvalidDataException("State shell contains embedded components.");
        foreach (var (key, bytes) in Entries.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (key == "view") continue;
            var node = JsonNode.Parse(bytes) ?? throw new InvalidDataException("Empty component.");
            if (key == "prospect") { root["Prospected"] = node; continue; }
            if (key == "messages") { root["Messages"] = node; continue; }
            if (key.StartsWith("e:", StringComparison.Ordinal))
            {
                if (root["WeatherEffects"] is not { } envelope || key != "e:" + node["Id"]!.GetValue<string>()) throw new InvalidDataException("Effect node does not match its envelope.");
                envelope["Nodes"]!.AsArray().Add(node); continue;
            }
            if (key.StartsWith("w:", StringComparison.Ordinal))
            {
                if (root["Weather"] is not { } envelope || key != "w:" + node["RegionId"]!.GetValue<string>()) throw new InvalidDataException("Weather region does not match its envelope.");
                envelope["Regions"]!.AsArray().Add(node); continue;
            }
            if (key.StartsWith("f:", StringComparison.Ordinal))
            {
                if (key != "f:" + node["Faction"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new InvalidDataException("Faction identity does not match its key.");
                root["FactionCatalogue"]!.AsArray().Add(node); continue;
            }
            string field = key[..2] switch { "c:" => "Characters", "b:" => "Buildings", "m:" => "Machines", "p:" => "Projectiles", _ => throw new InvalidDataException("Unknown component.") };
            string identity = field switch
            {
                "Characters" => FormattableString.Invariant($"c:{node["Id"]!["Slot"]!.GetValue<int>()}:{node["Id"]!["Generation"]!.GetValue<int>()}"),
                "Buildings" => "b:" + node["Id"]!.GetValue<string>(), "Machines" => "m:" + node["Site"]!.GetValue<string>(),
                _ => "p:" + node["Id"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (identity != key) throw new InvalidDataException("Component identity does not match its key.");
            snapshot[field]!.AsArray().Add(node);
        }
        root["Messages"] ??= JsonSerializer.SerializeToNode(PlayerMessageBatch.Empty);
        using var buffer = new BoundedBuffer(WorldViewCodec.MaximumBytes);
        using (var writer = new Utf8JsonWriter(buffer)) root.WriteTo(writer);
        return codec.Decode(buffer.ToArray());
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
        var bytes = buffer.ToArray(); WorldViewCodec.ValidateJson(bytes); return bytes;
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
    public bool HasAcknowledgedBaseline => acknowledged is not null;
    public EncodedPublication Publish(PlayerWorldView view, Func<CharacterId, bool>? confirmedDestroyed = null)
    {
        if (ReplicationIdentity.Of(view) != identity || view.Publication <= lastSequence) throw new InvalidOperationException("State belongs to another or older connection publication.");
        var document = StateDocument.Freeze(codec, view);
        bool full = acknowledged is null || baselineRequested;
        var patch = document.Difference(identity, view.Publication, full ? null : acknowledged, full ? 0 : acknowledgedSequence, confirmedDestroyed);
        var encoded = StatePatchProtocol.Encode(patch);
        pending.Add(view.Publication, document); lastSequence = view.Publication; baselineRequested = false;
        while (pending.Count > HistoryLimit || pending.Values.Sum(d => (long)d.ByteCount) + (acknowledged?.ByteCount ?? 0) > HistoryBytes)
            pending.Remove(pending.Keys.First());
        return new(view.Publication, full, document.Hash, encoded);
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
    public void RequestBaseline() => baselineRequested = true;
    public void Withdraw(ulong publication)
    {
        lastSequence = Math.Max(lastSequence, publication); pending.Clear(); acknowledged = null; acknowledgedSequence = 0; baselineRequested = true;
    }
}

/// <summary>Client-only replica; never owns or advances a Simulation.World.</summary>
public sealed class ClientWorldReplica(ReplicationIdentity identity, WorldViewCodec codec)
{
    const int HistoryLimit = 8, HistoryBytes = 64 * 1024 * 1024;
    readonly SortedDictionary<ulong, StateDocument> history = [];
    ulong highestPublication;
    public ulong HighestPublication => highestPublication;
    public PlayerWorldView? Current { get; private set; }
    public bool NeedsBaseline { get; private set; } = true;
    public IReadOnlyList<StateRemoval> LastRemovals { get; private set; } = [];
    public StateAcknowledgement? Acknowledgement => Current is { } view && history.TryGetValue(view.Publication, out var doc)
        ? new(identity, view.Publication, doc.Hash) : null;
    public ReplicaResult Receive(ReadOnlySpan<byte> payload)
    {
        try
        {
            var patch = StatePatchProtocol.Decode(payload);
            if (patch.Identity != identity) throw new InvalidDataException("State addressed to another connection.");
            if (patch.Publication <= highestPublication) return ReplicaResult.Stale;
            highestPublication = patch.Publication;
            StateDocument? baseline = null;
            if (patch.BasePublication != 0 && (NeedsBaseline || !history.TryGetValue(patch.BasePublication, out baseline)))
            { Reset(); return ReplicaResult.NeedsBaseline; }
            var document = StateDocument.Apply(patch, baseline);
            var view = document.Restore(codec);
            if (ReplicationIdentity.Of(view) != identity || view.Publication != patch.Publication) throw new InvalidDataException("State identity disagrees with its envelope.");
            // A newer baseline may cross an ACK for an earlier one. Keep bounded history for deltas
            // still based on that acknowledged state; the new baseline itself never merges old entries.
            history[patch.Publication] = document;
            while (history.Count > HistoryLimit || history.Values.Sum(d => (long)d.ByteCount) > HistoryBytes) history.Remove(history.Keys.First());
            Current = view; NeedsBaseline = false; LastRemovals = patch.Removals;
            return ReplicaResult.Applied;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException or InvalidDataException or OverflowException or FormatException)
        {
            Reset(); throw new InvalidDataException("Invalid replicated state; a fresh baseline is required.", e);
        }
    }
    public void Reset() { Current = null; history.Clear(); NeedsBaseline = true; LastRemovals = []; }
    public ReplicaResult Withdraw(ulong publication)
    {
        if (publication <= highestPublication) return ReplicaResult.Stale;
        highestPublication = publication; Reset(); return ReplicaResult.NeedsBaseline;
    }
}
