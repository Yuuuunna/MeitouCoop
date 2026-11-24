using System.Security.Cryptography;
using Meitou.Simulation;

namespace Meitou.Networking.VNext;
public sealed partial class GameStatePublisher
{
    const int CriticalPartPayload = 48 * 1024, CriticalPublicationBytes = 60 * 1024, CriticalOutstandingBytes = 256 * 1024;
    readonly record struct CriticalRecord(byte Kind, ulong Entity, uint Generation, ushort Field, byte[] Value);
    void WritePublicationClock(BinaryWriter writer, PlayerWorldView view)
    {
        writer.Write(view.World.ToByteArray());
        WriteValue(writer, codec.EncodeComponent(view.Player));
        writer.Write(view.ConnectionGeneration);
        writer.Write(view.Publication);
        writer.Write(view.GameHours);
        writer.Write(view.TimeScale);
        writer.Write(view.SimulationStepSeconds);
        writer.Write(view.Snapshot.Tick);
        writer.Write(view.Snapshot.StateRevision);
        writer.Write(view.Weather?.Time.Day ?? 0);
        writer.Write(view.Weather?.Time.Hours ?? 0);
    }

    /// <summary>Bounded reliable critical changes while bulk is busy. Only events, announced entities' playback,
    /// shared visual requests, departures and clocks advance; unencoded scene/private fields retain their prior comparison baseline.</summary>
    public IReadOnlyList<GameStatePublication> CaptureCritical(PlayerWorldView view, IReadOnlyDictionary<ulong, AnimationPlaybackState>? animations = null, IReadOnlySet<ulong>? destroyedEntities = null)
    {
        WorldViewCodec.Validate(view);
        if (_identity != new RecipientCaptureIdentity(view.World, view.Player, view.ConnectionGeneration) || _sequence == 0)
            throw new InvalidDataException("Critical capture requires the same already-announced recipient view.");
        var roots = CaptureMotionOnly(view);
        var records = new List<CriticalRecord>();
        var committed = new Dictionary<(ulong Entity, ushort Field), byte[]>();
        var committedObjects = new Dictionary<(ulong Entity, ushort Field), object?>();
        var gone = new List<ulong>();
        var events = GameComponentSchema.ViewFields.Single(f => f.Id == 7).Encode(codec, view);
        if (!_previous.TryGetValue((0, 7), out var oldEvents) || !oldEvents.AsSpan().SequenceEqual(events))
        {
            if (GameComponentSchema.ExpandedSize(7, events) > CriticalOutstandingBytes - 4096)
                throw new InvalidDataException("Critical event history exceeds lifecycle capacity.");
            committed[(0, 7)] = events;
            if (events.Length <= CriticalPartPayload)
                records.Add(new(2, 0, 0, 7, events));
            else
            {
                var hash = SHA256.HashData(events);
                for (int offset = 0; offset < events.Length; offset += CriticalPartPayload)
                {
                    using var fragment = new MemoryStream();
                    using var w = new BinaryWriter(fragment);
                    w.Write(0ul);
                    w.Write(events.Length);
                    w.Write(offset);
                    w.Write(hash);
                    w.Write(events, offset, Math.Min(CriticalPartPayload, events.Length - offset));
                    records.Add(new(5, 0, 0, 7, fragment.ToArray()));
                }
            }
        }

        CaptureCollections(view, (kind, field, value) =>
        {
            if (kind == 6 && value.Length > CriticalPartPayload)
            {
                var hash = SHA256.HashData(value);
                for (int offset = 0; offset < value.Length; offset += CriticalPartPayload)
                {
                    using var fragment = new MemoryStream();
                    using var writer = new BinaryWriter(fragment);
                    writer.Write(0ul);
                    writer.Write(value.Length);
                    writer.Write(offset);
                    writer.Write(hash);
                    writer.Write(value, offset, Math.Min(CriticalPartPayload, value.Length - offset));
                    records.Add(new(10, 0, 0, field, fragment.ToArray()));
                }
            }
            else records.Add(new(kind, 0, 0, field, value));
        }, onlyField: 1068);

        var current = view.Snapshot.Characters.ToDictionary(entity => entityOf(entity.Id));
        foreach (var known in _entities)
        {
            if (!current.TryGetValue(known.Key, out var character) || character.Id != known.Value.Id)
            {
                records.Add(new((byte)(destroyedEntities?.Contains(known.Key) == true ? 4 : 3), known.Key, known.Value.Generation, 0, []));
                gone.Add(known.Key);
                continue;
            }

            if (_motionRetired.Contains(known.Key) || animations?.TryGetValue(known.Key, out var state) != true)
                continue;
            byte[] value;
            ushort[] distinctUsed;
            if (_previousObjects.TryGetValue((known.Key, 3002), out var cachedAnim) && cachedAnim is TrackedAnimation prevAnim && ReferenceEquals(prevAnim.State, state))
            {
                value = prevAnim.Bytes;
                distinctUsed = prevAnim.Descriptors;
            }
            else
            {
                value = CompactAnimationPlaybackCodec.Encode(state!, _animationDescriptors, out var used);
                distinctUsed = used.Distinct().ToArray();
                committedObjects[(known.Key, 3002)] = new TrackedAnimation(state!, distinctUsed, value);
            }

            foreach (ushort descriptor in distinctUsed)
                if (_criticalAnimationDescriptors.Add(descriptor))
                    records.Add(new(9, 0, 0, 3003, _animationDescriptors.EncodeDefinition(descriptor)));
            if (!_previous.TryGetValue((known.Key, 3002), out var old) || !old.AsSpan().SequenceEqual(value))
            {
                records.Add(new(2, known.Key, known.Value.Generation, 3002, value));
                committed[(known.Key, 3002)] = value;
            }
        }

        // Publish new visual entries before activating their membership. Every intermediate publication still has a complete old manifest,
        // including when an older baseline arrives on the bulk lane. The final manifest performs visual withdrawals atomically.
        records = records.OrderBy(r => r.Kind == 9 ? 0 : r.Field == 1068 && r.Kind is 6 or 10 ? 2 : 1).ToList();
        // Reserve clock, record-count and framing overhead before packing. No part can require bulk.
        var parts = new List<List<CriticalRecord>>
        {
            new()
        };
        int retained = 1024;
        for (int index = 0; index < records.Count;)
        {
            var record = records[index];
            int paired = VisualPairLength(records, index);
            int bytes = record.Value.Length + 19;
            if (paired == 2) bytes += records[index + 1].Value.Length + 19;
            if (bytes > CriticalPublicationBytes - 1024)
                throw new InvalidDataException("Critical record or visual pair exceeds lifecycle bound.");
            if (retained + bytes > CriticalPublicationBytes)
            {
                parts.Add([]);
                retained = 1024;
            }

            parts[^1].Add(record);
            if (paired == 2) parts[^1].Add(records[index + 1]);
            retained += bytes;
            index += paired;
        }

        if ((ulong)parts.Count > ulong.MaxValue - _sequence)
            throw new InvalidDataException("Publication sequence exhausted.");
        ulong finalRevision = _sequence + (ulong)parts.Count;
        foreach (var fragment in records.Where(r => r.Kind is 5 or 10))
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(fragment.Value, finalRevision);
        var result = new List<GameStatePublication>();
        long total = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            using var stream = new BoundedBuffer(CriticalPublicationBytes);
            using var writer = new BinaryWriter(stream);
            WritePublicationClock(writer, view);
            writer.Write(parts[i].Count);
            foreach (var record in parts[i])
            {
                writer.Write(record.Kind);
                writer.Write(record.Entity);
                writer.Write(record.Generation);
                writer.Write(record.Field);
                WriteValue(writer, record.Value);
            }

            var frames = GameStateWire.Split(sessionEpoch, viewEpoch, _sequence + (ulong)i + 1, false, stream.ToArray(), compress: true);
            int bytes = frames.Sum(f => f.Length);
            total += Math.Max(bytes, stream.Length);
            if (bytes > 64 * 1024 || total > GameStateWire.MaximumBytes)
                throw new InvalidDataException("Critical lifecycle publication exceeds outstanding capacity.");
            result.Add(new(_sequence + (ulong)i + 1, false, frames) { Motion = i == 0 ? roots.Motion : [], WithdrawnMotion = i == 0 ? roots.WithdrawnMotion : [], ComponentBytes = parts[i].GroupBy(r => r.Field).ToDictionary(g => g.Key, g => g.Sum(r => 19L + r.Value.Length)), });
        }

        _sequence = finalRevision;
        foreach (var pair in committed)
            _previous[pair.Key] = pair.Value;
        foreach (var pair in committedObjects)
            _previousObjects[pair.Key] = pair.Value;
        foreach (var entity in gone)
        {
            _entities.Remove(entity);
            _motion.Remove(entity);
            _motionRetired.Remove(entity);
            foreach (var key in _previous.Keys.Where(k => k.Entity == entity).ToArray())
            {
                _previous.Remove(key);
                _previousObjects.Remove(key);
            }
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// A definition change forces its matching reading into the same publication. Neither framing nor a bulk-lane arrival can expose
    /// a new anchor with an old tint/lifetime, including when a definition was first captured into an undelivered ordinary bulk.
    /// </summary>
    static int VisualPairLength(IReadOnlyList<CriticalRecord> records, int index)
    {
        var record = records[index];
        if (record.Kind != 7 || record.Field != 1068) return 1;
        var definition = GameCollectionSchema.ReadEntry(record.Value);
        if (!ModVisualCollection.IsDefinition(definition.Key)) return 1;
        if (index + 1 == records.Count || records[index + 1] is not { Kind: 7, Field: 1068 } next)
            throw new InvalidDataException("A shared visual definition has no matching reading.");
        var reading = GameCollectionSchema.ReadEntry(next.Value);
        if (reading.Key != ModVisualCollection.Partner(definition.Key)
            || ModVisualCodec.DefinitionVersion(definition.Value) != ModVisualCodec.DefinitionVersion(reading.Value))
            throw new InvalidDataException("A shared visual definition has a mismatched reading.");
        return 2;
    }
}

public sealed partial class GameStateReplica
{
    CriticalEventFragment? _criticalEventFragment, _criticalVisualManifest;
    sealed record CriticalEventFragment(ulong Revision, byte[] Bytes, byte[] Hash)
    {
        internal int Offset;
    }

    void ApplyCriticalEventFragment(ulong entity, uint generation, ushort field, byte[] value, ulong publicationRevision,
        Action<long> reserveExpanded)
    {
        if (entity != 0 || generation != 0 || field != 7 || value.Length <= 48)
            throw new InvalidDataException("Invalid critical event fragment.");
        using var stream = new MemoryStream(value, false);
        using var reader = new BinaryReader(stream);
        ulong revision = reader.ReadUInt64();
        int length = reader.ReadInt32(), offset = reader.ReadInt32();
        byte[] hash = reader.ReadBytes(32);
        int count = value.Length - 48;
        if (revision < publicationRevision || revision - publicationRevision > GameStateWire.MaximumBytes / 19
            || length <= 0 || length > 256 * 1024 || offset < 0 || offset > length - count)
            throw new InvalidDataException("Invalid critical event fragment bounds.");
        var key = (0ul, (ushort)7);
        ulong applied = _revisions.GetValueOrDefault(key);
        if (applied > revision)
            return;
        if (applied == revision)
        {
            if (!hash.AsSpan().SequenceEqual(_valueDigests[key]))
                throw new InvalidDataException("Conflicting critical event revision.");
            return;
        }

        if (_criticalEventFragment is { } old && old.Revision > revision)
            return;
        if (offset == 0)
        {
            if (_criticalEventFragment is { } active && active.Revision == revision)
                throw new InvalidDataException("Repeated incomplete critical event fragment.");
            _criticalEventFragment = new(revision, new byte[length], hash);
        }

        if (_criticalEventFragment is not { } fragment || fragment.Revision != revision || fragment.Bytes.Length != length || fragment.Offset != offset || !hash.AsSpan().SequenceEqual(fragment.Hash))
            throw new InvalidDataException("Noncontiguous critical event fragments.");
        value.AsSpan(48).CopyTo(fragment.Bytes.AsSpan(offset));
        fragment.Offset += count;
        if (fragment.Offset != length)
            return;
        if (!SHA256.HashData(fragment.Bytes).AsSpan().SequenceEqual(hash))
            throw new InvalidDataException("Critical event digest mismatch.");
        // Earlier pieces may belong to other publications. Charge the assembled value before allocating its expanded JSON.
        int expanded = GameComponentSchema.ExpandedSize(field, fragment.Bytes);
        reserveExpanded(Math.Max(0, expanded - (long)count));
        _view = GameComponentSchema.ViewFields.Single(f => f.Id == 7).Apply(codec, _view!, fragment.Bytes);
        _revisions[key] = revision;
        _valueDigests[key] = hash;
        _criticalEventFragment = null;
    }

    /// <summary>
    /// Large visual membership changes use the same reliable staging and final-revision fence as fragmented events. The manifest changes
    /// atomically after its digest matches; individual visual values remain independently revisioned while the rest of the burst arrives.
    /// </summary>
    void ApplyCriticalVisualManifest(ulong entity, uint generation, ushort field, byte[] value, ulong publicationRevision,
        Action<long> reserveExpanded)
    {
        if (entity != 0 || generation != 0 || field != 1068 || value.Length <= 48)
            throw new InvalidDataException("Invalid shared visual manifest fragment.");
        using var stream = new MemoryStream(value, false);
        using var reader = new BinaryReader(stream);
        ulong revision = reader.ReadUInt64();
        int length = reader.ReadInt32(), offset = reader.ReadInt32();
        byte[] hash = reader.ReadBytes(32);
        int count = value.Length - 48;
        if (revision < publicationRevision || revision - publicationRevision > GameStateWire.MaximumBytes / 19
            || length <= 0 || length > GameStateWire.MaximumBytes || offset < 0 || offset > length - count)
            throw new InvalidDataException("Invalid shared visual manifest bounds.");
        ulong applied = _collections.GetValueOrDefault(field)?.Revision ?? 0;
        if (applied > revision || _criticalVisualManifest is { } newer && newer.Revision > revision) return;
        if (applied == revision)
        {
            if (!hash.AsSpan().SequenceEqual(_collections[field].ManifestDigest))
                throw new InvalidDataException("Conflicting shared visual manifest revision.");
            return;
        }
        if (offset == 0)
        {
            if (_criticalVisualManifest is { } active && active.Revision == revision)
                throw new InvalidDataException("Repeated incomplete shared visual manifest.");
            _criticalVisualManifest = new(revision, new byte[length], hash);
        }
        var fragment = _criticalVisualManifest;
        if (fragment is null || fragment.Revision != revision || fragment.Bytes.Length != length || fragment.Offset != offset
            || !hash.AsSpan().SequenceEqual(fragment.Hash))
            throw new InvalidDataException("Noncontiguous shared visual manifest fragments.");
        value.AsSpan(48).CopyTo(fragment.Bytes.AsSpan(offset));
        fragment.Offset += count;
        if (fragment.Offset != length) return;
        if (!SHA256.HashData(fragment.Bytes).AsSpan().SequenceEqual(hash))
            throw new InvalidDataException("Shared visual manifest digest mismatch.");
        reserveExpanded(Math.Max(0, length - (long)count));
        ApplyCollection(6, 0, 0, field, fragment.Bytes, revision);
        _criticalVisualManifest = null;
    }
}
