using System.Security.Cryptography;
using Meitou.Simulation;
namespace Meitou.Networking.VNext;

public sealed partial class GameStatePublisher
{
    readonly record struct CriticalRecord(byte Kind, ulong Entity, uint Generation, ushort Field, byte[] Value);
    const int CriticalPartPayload = 48 * 1024, CriticalPublicationBytes = 60 * 1024, CriticalOutstandingBytes = 256 * 1024;
    void WritePublicationClock(BinaryWriter writer, PlayerWorldView view)
    {
        writer.Write(view.World.ToByteArray()); WriteValue(writer, codec.EncodeComponent(view.Player));
        writer.Write(view.ConnectionGeneration); writer.Write(view.Publication); writer.Write(view.GameHours);
        writer.Write(view.TimeScale); writer.Write(view.SimulationStepSeconds); writer.Write(view.Snapshot.Tick); writer.Write(view.Snapshot.StateRevision);
        writer.Write(view.Weather?.Time.Day ?? 0); writer.Write(view.Weather?.Time.Hours ?? 0);
    }
    /// <summary>Bounded reliable critical changes while bulk is busy. Only events, announced entities' playback,
    /// departures and clocks advance; unencoded scene/private fields retain their prior comparison baseline.</summary>
    public IReadOnlyList<GameStatePublication> CaptureCritical(PlayerWorldView view,
        IReadOnlyDictionary<ulong, AnimationPlaybackState>? animations = null, IReadOnlySet<ulong>? destroyedEntities = null)
    {
        WorldViewCodec.Validate(view);
        if (identity != new RecipientCaptureIdentity(view.World, view.Player, view.ConnectionGeneration) || sequence == 0)
            throw new InvalidDataException("Critical capture requires the same already-announced recipient view.");
        var roots = CaptureMotionOnly(view);
        var records = new List<CriticalRecord>();
        var committed = new Dictionary<(ulong Entity, ushort Field), byte[]>();
        var gone = new List<ulong>();
        var events = GameComponentSchema.ViewFields.Single(f => f.Id == 7).Encode(codec, view);
        if (!previous.TryGetValue((0,7), out var oldEvents) || !oldEvents.AsSpan().SequenceEqual(events))
        {
            if (events.Length > CriticalOutstandingBytes - 4096) throw new InvalidDataException("Critical event history exceeds lifecycle capacity.");
            committed[(0,7)] = events;
            if (events.Length <= CriticalPartPayload) records.Add(new(2,0,0,7,events));
            else
            {
                var hash = SHA256.HashData(events);
                for (int offset = 0; offset < events.Length; offset += CriticalPartPayload)
                {
                    using var fragment = new MemoryStream(); using var w = new BinaryWriter(fragment);
                    w.Write(0ul); w.Write(events.Length); w.Write(offset); w.Write(hash);
                    w.Write(events, offset, Math.Min(CriticalPartPayload,events.Length-offset));
                    records.Add(new(5,0,0,7,fragment.ToArray()));
                }
            }
        }
        var current = view.Snapshot.Characters.ToDictionary(entity => entityOf(entity.Id));
        foreach (var known in entities)
        {
            if (!current.TryGetValue(known.Key, out var character) || character.Id != known.Value.Id)
            {
                records.Add(new((byte)(destroyedEntities?.Contains(known.Key) == true ? 4 : 3),known.Key,known.Value.Generation,0,[]));
                gone.Add(known.Key); continue;
            }
            if (motionRetired.Contains(known.Key) || animations?.TryGetValue(known.Key,out var state) != true) continue;
            var value = CompactAnimationPlaybackCodec.Encode(state!, animationDescriptors, out var used);
            foreach (ushort descriptor in used.Distinct())
                if (criticalAnimationDescriptors.Add(descriptor)) records.Add(new(9,0,0,3003,animationDescriptors.EncodeDefinition(descriptor)));
            if (!previous.TryGetValue((known.Key,3002),out var old) || !old.AsSpan().SequenceEqual(value))
            { records.Add(new(2,known.Key,known.Value.Generation,3002,value)); committed[(known.Key,3002)] = value; }
        }
        records = records.OrderBy(r => r.Kind == 9 ? 0 : 1).ToList();
        // Reserve clock, record-count and framing overhead before packing. No part can require bulk.
        var parts = new List<List<CriticalRecord>> { new() }; int retained = 1024;
        foreach (var record in records)
        {
            int bytes = record.Value.Length + 19;
            if (bytes > CriticalPublicationBytes - 1024) throw new InvalidDataException("Critical record exceeds lifecycle bound.");
            if (retained + bytes > CriticalPublicationBytes) { parts.Add([]); retained = 1024; }
            parts[^1].Add(record); retained += bytes;
        }
        if ((ulong)parts.Count > ulong.MaxValue - sequence) throw new InvalidDataException("Publication sequence exhausted.");
        ulong finalRevision = sequence + (ulong)parts.Count;
        foreach (var fragment in records.Where(r=>r.Kind==5)) System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(fragment.Value,finalRevision);
        var result = new List<GameStatePublication>(); long total = 0;
        for (int i=0;i<parts.Count;i++)
        {
            using var stream = new BoundedBuffer(CriticalPublicationBytes); using var writer = new BinaryWriter(stream);
            WritePublicationClock(writer,view); writer.Write(parts[i].Count);
            foreach (var record in parts[i]) { writer.Write(record.Kind); writer.Write(record.Entity); writer.Write(record.Generation); writer.Write(record.Field); WriteValue(writer,record.Value); }
            var frames = GameStateWire.Split(sessionEpoch,viewEpoch,sequence+(ulong)i+1,false,stream.ToArray());
            int bytes = frames.Sum(f=>f.Length); total += bytes;
            if (bytes > 64*1024 || total > CriticalOutstandingBytes) throw new InvalidDataException("Critical lifecycle publication exceeds outstanding capacity.");
            result.Add(new(sequence+(ulong)i+1,false,frames) {
                Motion = i==0 ? roots.Motion : [], WithdrawnMotion = i==0 ? roots.WithdrawnMotion : [],
                ComponentBytes = parts[i].GroupBy(r=>r.Field).ToDictionary(g=>g.Key,g=>g.Sum(r=>19L+r.Value.Length)),
            });
        }
        sequence = finalRevision;
        foreach (var pair in committed) previous[pair.Key] = pair.Value;
        foreach (var entity in gone)
        {
            entities.Remove(entity); motion.Remove(entity); motionRetired.Remove(entity);
            foreach (var key in previous.Keys.Where(k=>k.Entity==entity).ToArray()) previous.Remove(key);
        }
        return result.AsReadOnly();
    }
}

public sealed partial class GameStateReplica
{
    sealed record CriticalEventFragment(ulong Revision, byte[] Bytes, byte[] Hash) { internal int Offset; }
    CriticalEventFragment? criticalEventFragment;
    void ApplyCriticalEventFragment(ulong entity,uint generation,ushort field,byte[] value,ulong publicationRevision)
    {
        if (entity!=0 || generation!=0 || field!=7 || value.Length<=48) throw new InvalidDataException("Invalid critical event fragment.");
        using var stream=new MemoryStream(value,false); using var reader=new BinaryReader(stream);
        ulong revision=reader.ReadUInt64(); int length=reader.ReadInt32(),offset=reader.ReadInt32(); byte[] hash=reader.ReadBytes(32);
        int count=value.Length-48;
        if (revision<publicationRevision || revision-publicationRevision>64 || length<=0 || length>256*1024 || offset<0 || offset>length-count)
            throw new InvalidDataException("Invalid critical event fragment bounds.");
        var key=(0ul,(ushort)7); ulong applied=revisions.GetValueOrDefault(key);
        if (applied>revision) return;
        if (applied==revision)
        { if (!hash.AsSpan().SequenceEqual(valueDigests[key])) throw new InvalidDataException("Conflicting critical event revision."); return; }
        if (criticalEventFragment is { } old && old.Revision>revision) return;
        if (offset==0)
        {
            if (criticalEventFragment is { } active && active.Revision==revision) throw new InvalidDataException("Repeated incomplete critical event fragment.");
            criticalEventFragment=new(revision,new byte[length],hash);
        }
        if (criticalEventFragment is not { } fragment || fragment.Revision!=revision || fragment.Bytes.Length!=length || fragment.Offset!=offset || !hash.AsSpan().SequenceEqual(fragment.Hash))
            throw new InvalidDataException("Noncontiguous critical event fragments.");
        value.AsSpan(48).CopyTo(fragment.Bytes.AsSpan(offset)); fragment.Offset+=count;
        if (fragment.Offset!=length) return;
        if (!SHA256.HashData(fragment.Bytes).AsSpan().SequenceEqual(hash)) throw new InvalidDataException("Critical event digest mismatch.");
        view=GameComponentSchema.ViewFields.Single(f=>f.Id==7).Apply(codec,view!,fragment.Bytes);
        revisions[key]=revision; valueDigests[key]=hash; criticalEventFragment=null;
    }
}
