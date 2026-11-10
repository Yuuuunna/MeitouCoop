using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Data.World;
namespace Meitou.Networking.VNext;

internal sealed record CollectionManifest(bool Present,bool Enabled,IReadOnlyList<string> Keys);
internal sealed class GameCollectionState
{
    internal ulong Revision;
    internal byte[] ManifestDigest=[];
    internal CollectionManifest Manifest=new(false,false,[]);
    internal readonly Dictionary<string,(ulong Revision,object? Value,byte[] Digest)> Items=new(StringComparer.Ordinal);
}
internal interface IGameCollection
{
    ushort Id{get;}
    (CollectionManifest Manifest,IReadOnlyList<(string Key,byte[] Value)> Items) Capture(WorldViewCodec codec,PlayerWorldView view);
    object Decode(WorldViewCodec codec,string key,byte[] value);
    PlayerWorldView Build(PlayerWorldView view,GameCollectionState state);
}
internal static class GameCollectionSchema
{
    sealed class Collection<T>(ushort id,Func<PlayerWorldView,IReadOnlyList<T>> get,Func<T,string> key,
        Func<PlayerWorldView,IReadOnlyList<T>,bool,bool,PlayerWorldView> set,Func<PlayerWorldView,bool>? present=null,Func<PlayerWorldView,bool>? enabled=null):IGameCollection
    {
        public ushort Id=>id;
        public (CollectionManifest Manifest,IReadOnlyList<(string Key,byte[] Value)> Items) Capture(WorldViewCodec codec,PlayerWorldView view)
        {
            var values=get(view);if(values.Count>65536)throw new InvalidDataException("Collection exceeds record bound.");
            var result=values.Select(value=>(Key:key(value),Value:codec.EncodeComponent(value))).ToArray();
            if(result.Select(p=>p.Key).Distinct(StringComparer.Ordinal).Count()!=result.Length)throw new InvalidDataException("Duplicate keyed collection identity.");
            foreach(var item in result)ValidateKey(item.Key);
            return(new(present?.Invoke(view)??true,enabled?.Invoke(view)??true,result.Select(p=>p.Key).ToArray()),result);
        }
        public object Decode(WorldViewCodec codec,string expectedKey,byte[] value)
        {
            WorldViewCodec.ValidateJson(value);var item=codec.DecodeComponent<T>(value);
            if(key(item)!=expectedKey)throw new InvalidDataException("Keyed value identity does not match its envelope.");
            return item!;
        }
        public PlayerWorldView Build(PlayerWorldView view,GameCollectionState state)=>set(view,state.Manifest.Keys.Where(k=>state.Items.TryGetValue(k,out var item)&&item.Value is not null).Select(k=>(T)state.Items[k].Value!).ToArray(),state.Manifest.Present,state.Manifest.Enabled);
    }
    internal static readonly IGameCollection[] All=
    [
        new Collection<GroundItemSnapshot>(1011,v=>v.Snapshot.GroundItems,v=>v.Id.ToString(CultureInfo.InvariantCulture),(v,x,_,_)=>v with{Snapshot=v.Snapshot with{GroundItems=x}}),
        new Collection<ContainerSpot>(1013,v=>v.Snapshot.Containers,v=>v.Id,(v,x,_,_)=>v with{Snapshot=v.Snapshot with{Containers=x}}),
        new Collection<BedSpot>(1014,v=>v.Snapshot.Beds,v=>v.Id,(v,x,_,_)=>v with{Snapshot=v.Snapshot with{Beds=x}}),
        new Collection<SeatSpot>(1016,v=>v.Snapshot.Seats,v=>v.Id,(v,x,_,_)=>v with{Snapshot=v.Snapshot with{Seats=x}}),
        new Collection<CageSpot>(1017,v=>v.Snapshot.Cages,v=>v.Id,(v,x,_,_)=>v with{Snapshot=v.Snapshot with{Cages=x}}),
        new Collection<WeatherRegionSnapshot>(9,v=>v.Weather?.Regions??[],v=>v.RegionId,(v,x,p,e)=>v with{Weather=p?new(v.Weather?.Time??default,x){Enabled=e}:null},v=>v.Weather is not null,v=>v.Weather?.Enabled??false),
    ];
    internal static readonly IReadOnlySet<ushort> Ids=All.Select(s=>s.Id).ToHashSet();
    internal static void ValidateKey(string key)
    {if(string.IsNullOrEmpty(key)||Encoding.UTF8.GetByteCount(key)>1024)throw new InvalidDataException("Invalid keyed collection identity.");}
    internal static byte[] Entry(string key,byte[] value)
    {
        byte[] name=Encoding.UTF8.GetBytes(key);byte[] bytes=new byte[2+name.Length+value.Length];System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes,(ushort)name.Length);name.CopyTo(bytes,2);value.CopyTo(bytes,2+name.Length);return bytes;
    }
    internal static (string Key,byte[] Value) ReadEntry(byte[] bytes)
    {
        if(bytes.Length<3)throw new InvalidDataException("Truncated keyed entry.");
        int length=System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if(length>1024||length>bytes.Length-2)throw new InvalidDataException("Invalid keyed entry bounds.");
        string key=new UTF8Encoding(false,true).GetString(bytes,2,length);ValidateKey(key);return(key,bytes.AsSpan(2+length).ToArray());
    }
}

public sealed partial class GameStatePublisher
{
    sealed class EncodedCollection(CollectionManifest manifest,Dictionary<string,byte[]> items)
    {internal CollectionManifest Manifest=manifest;internal Dictionary<string,byte[]> Items=items;}
    readonly Dictionary<ushort,EncodedCollection> collections=new();
    long CaptureCollections(PlayerWorldView view,Action<byte,ushort,byte[]> add)
    {
        long total=0;
        foreach(var schema in GameCollectionSchema.All)
        {
            var captured=schema.Capture(codec,view);var current=captured.Items.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
            total+=codec.EncodeComponent(captured.Manifest).Length+19;
            total+=current.Sum(p=>(long)p.Value.Length+Encoding.UTF8.GetByteCount(p.Key)+21);
            if(total>GameStateWire.MaximumBytes)throw new InvalidDataException("Keyed collection capture exceeds bulk bound.");
            collections.TryGetValue(schema.Id,out var previousCollection);
            if(previousCollection is null||captured.Manifest.Present!=previousCollection.Manifest.Present||captured.Manifest.Enabled!=previousCollection.Manifest.Enabled||!captured.Manifest.Keys.SequenceEqual(previousCollection.Manifest.Keys))
                add(6,schema.Id,codec.EncodeComponent(captured.Manifest));
            foreach(var item in captured.Items)
                if(previousCollection is null||!previousCollection.Items.TryGetValue(item.Key,out var old)||!old.AsSpan().SequenceEqual(item.Value))add(7,schema.Id,GameCollectionSchema.Entry(item.Key,item.Value));
            if(previousCollection is not null)
                foreach(var key in previousCollection.Items.Keys.Where(k=>!current.ContainsKey(k)))add(8,schema.Id,GameCollectionSchema.Entry(key,[]));
            collections[schema.Id]=new(captured.Manifest,current);
        }
        return total;
    }
}

public sealed partial class GameStateReplica
{
    readonly Dictionary<ushort,GameCollectionState> collections=new();
    void ApplyCollection(byte kind,ulong entity,uint generation,ushort field,byte[] value,ulong revision)
    {
        if(entity!=0||generation!=0||!GameCollectionSchema.Ids.Contains(field))throw new InvalidDataException("Invalid collection scope or generation.");
        var schema=GameCollectionSchema.All.Single(s=>s.Id==field);
        if(!collections.TryGetValue(field,out var state))collections[field]=state=new();
        if(kind==6)
        {
            if(state.Revision>revision)return;
            byte[] digest=SHA256.HashData(value);
            if(state.Revision==revision){if(!digest.AsSpan().SequenceEqual(state.ManifestDigest))throw new InvalidDataException("Conflicting collection manifest.");return;}
            WorldViewCodec.ValidateJson(value);var manifest=codec.DecodeComponent<CollectionManifest>(value);
            if(field!=9&&(!manifest.Present||!manifest.Enabled)||!manifest.Present&&manifest.Enabled)throw new InvalidDataException("Invalid collection presence flags.");
            if(manifest.Keys is null||manifest.Keys.Count>65536||manifest.Keys.Distinct(StringComparer.Ordinal).Count()!=manifest.Keys.Count||!manifest.Present&&manifest.Keys.Count!=0||!manifest.Enabled&&manifest.Keys.Count!=0)throw new InvalidDataException("Invalid collection manifest.");
            foreach(var key in manifest.Keys)GameCollectionSchema.ValidateKey(key);
            state.Manifest=manifest;state.Revision=revision;state.ManifestDigest=digest;revisions[(0,field)]=revision;valueDigests[(0,field)]=digest;
        }
        else
        {
            var entry=GameCollectionSchema.ReadEntry(value);state.Items.TryGetValue(entry.Key,out var old);
            if(old.Revision>revision)return;
            byte[] digest=SHA256.HashData(value);
            if(old.Revision==revision){if(!digest.AsSpan().SequenceEqual(old.Digest))throw new InvalidDataException("Conflicting keyed entry revision.");return;}
            if(collections.Values.Sum(s=>s.Items.Count)>=65536&&!state.Items.ContainsKey(entry.Key))throw new InvalidDataException("Keyed entry fence bound exceeded.");
            if(kind==8&&entry.Value.Length!=0||kind==7&&entry.Value.Length==0)throw new InvalidDataException("Invalid keyed entry operation.");
            state.Items[entry.Key]=(revision,kind==7?schema.Decode(codec,entry.Key,entry.Value):null,digest);
        }
    }
    void RefreshCollections(bool requireComplete)
    {
        foreach(var schema in GameCollectionSchema.All)
            if(collections.TryGetValue(schema.Id,out var state)&&state.Revision!=0)
            {
                if(requireComplete&&state.Manifest.Keys.Any(k=>!state.Items.TryGetValue(k,out var item)||item.Value is null))throw new InvalidDataException("Incomplete keyed collection baseline.");
                view=schema.Build(view!,state);
            }
    }
}
