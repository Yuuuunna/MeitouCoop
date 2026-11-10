namespace Meitou.Networking.VNext;

/// <summary>One session/view's immutable, additive clip metadata. IDs never change meaning or get reused.</summary>
public sealed class AnimationDescriptorCatalog
{
    public const int MaximumDescriptors = 4096;
    readonly record struct Descriptor(string Name, int Definition, int Length, AnimationWrap Wrap, int End,
        AnimationBlendArea Area, bool ConsumesLower)
    {
        internal static Descriptor Of(AnimationPlaybackLayer l) => new(l.Name,l.Definition,BitConverter.SingleToInt32Bits(l.LengthSeconds),l.Wrap,
            BitConverter.SingleToInt32Bits(l.EndSeconds),l.Area,l.ConsumesLower);
        internal AnimationPlaybackLayer Layer => new(Name,Definition,BitConverter.Int32BitsToSingle(Length),0,0,Wrap,
            BitConverter.Int32BitsToSingle(End),0,0,0,Area,false,ConsumesLower);
    }
    readonly Dictionary<Descriptor,ushort> ids = [];
    readonly Dictionary<ushort,Descriptor> definitions = [];
    public int Count => definitions.Count;
    public void Clear() { ids.Clear(); definitions.Clear(); }
    internal ushort Register(AnimationPlaybackLayer layer)
    {
        var descriptor=Descriptor.Of(layer);
        if(ids.TryGetValue(descriptor,out ushort id)) return id;
        if(definitions.Count>=MaximumDescriptors) throw new InvalidDataException("Animation descriptor catalog exhausted.");
        id=checked((ushort)(definitions.Count+1)); ids.Add(descriptor,id);definitions.Add(id,descriptor);return id;
    }
    internal AnimationPlaybackLayer Resolve(ushort id) => definitions.TryGetValue(id,out var descriptor)
        ? descriptor.Layer : throw new InvalidDataException("Missing animation descriptor.");
    public byte[] EncodeDefinition(ushort id)
    {
        if(!definitions.TryGetValue(id,out var d)) throw new InvalidDataException("Missing animation descriptor.");
        using var stream=new MemoryStream();using var w=new BinaryWriter(stream,AnimationPlaybackValidation.Utf8);
        w.Write((byte)1);CompactAnimationPlaybackCodec.WriteUnsigned(w,id);
        byte[] name=AnimationPlaybackValidation.Utf8.GetBytes(d.Name);CompactAnimationPlaybackCodec.WriteUnsigned(w,(ulong)name.Length);w.Write(name);
        CompactAnimationPlaybackCodec.WriteUnsigned(w,(ulong)((long)d.Definition+1));w.Write(d.Length);w.Write((byte)d.Wrap);w.Write(d.End);w.Write((byte)d.Area);w.Write(d.ConsumesLower);
        return stream.ToArray();
    }
    public void ApplyDefinition(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length>160) throw new InvalidDataException("Oversized animation descriptor.");
        using var stream=new MemoryStream(bytes.ToArray(),false);using var r=new BinaryReader(stream,AnimationPlaybackValidation.Utf8);
        try
        {
            if(r.ReadByte()!=1)throw new InvalidDataException("Unknown animation descriptor version.");
            ulong rawId=CompactAnimationPlaybackCodec.ReadUnsigned(r),nameLength=CompactAnimationPlaybackCodec.ReadUnsigned(r);
            if(rawId==0||rawId>MaximumDescriptors||nameLength==0||nameLength>AnimationPlaybackValidation.MaximumNameBytes)throw new InvalidDataException("Invalid animation descriptor identity.");
            string name=AnimationPlaybackValidation.Utf8.GetString(r.ReadBytes((int)nameLength));
            ulong definition=CompactAnimationPlaybackCodec.ReadUnsigned(r);if(definition>(ulong)int.MaxValue+1)throw new InvalidDataException("Invalid animation definition index.");
            int length=r.ReadInt32();var wrap=(AnimationWrap)r.ReadByte();int end=r.ReadInt32();var area=(AnimationBlendArea)r.ReadByte();byte consumes=r.ReadByte();
            if(consumes>1||stream.Position!=stream.Length)throw new InvalidDataException("Invalid animation descriptor data.");
            var descriptor=new Descriptor(name,(int)((long)definition-1),length,wrap,end,area,consumes!=0);
            AnimationPlaybackValidation.Validate(new(1,0,.1f,0,1,[descriptor.Layer]));
            ushort id=(ushort)rawId;
            if(definitions.TryGetValue(id,out var old))
            {if(old!=descriptor)throw new InvalidDataException("Conflicting immutable animation descriptor.");return;}
            if(ids.ContainsKey(descriptor)||definitions.Count>=MaximumDescriptors)throw new InvalidDataException("Duplicate or exhausted animation descriptor catalog.");
            definitions.Add(id,descriptor);ids.Add(descriptor,id);
        }
        catch(Exception error)when(error is EndOfStreamException or System.Text.DecoderFallbackException)
        {throw new InvalidDataException("Truncated animation descriptor.",error);}
    }
}

/// <summary>Absolute dynamic playback: every state stands alone once its immutable descriptors are known.
/// All floats retain their IEEE bits. Varints shorten counters; exact +0 and +1 have compact codes.</summary>
public static class CompactAnimationPlaybackCodec
{
    public static byte[] Encode(AnimationPlaybackState state,AnimationDescriptorCatalog catalog,out IReadOnlyList<ushort> descriptors)
    {
        AnimationPlaybackValidation.Validate(state);
        using var stream=new MemoryStream();using var w=new BinaryWriter(stream);
        w.Write((byte)1);WriteUnsigned(w,state.Epoch);WriteUnsigned(w,state.StartTick);w.Write(state.TickSeconds);
        WriteUnsigned(w,state.ActionInstance);WriteUnsigned(w,state.ValidThroughTick-state.StartTick);w.Write((byte)state.Layers.Count);
        var used=new ushort[state.Layers.Count];
        Span<float> values=stackalloc float[5];
        for(int i=0;i<state.Layers.Count;i++)
        {
            var layer=state.Layers[i];used[i]=catalog.Register(layer);WriteUnsigned(w,used[i]);
            values[0]=layer.PhaseSeconds;values[1]=layer.Rate;values[2]=layer.StartWeight;values[3]=layer.TargetWeight;values[4]=layer.WeightSeconds;
            ushort flags=(ushort)(layer.Wanted?1<<10:0);
            for(int k=0;k<values.Length;k++)flags|=(ushort)(Code(values[k])<<(k*2));
            w.Write(flags);
            for(int k=0;k<values.Length;k++)if(Code(values[k])==2)w.Write(values[k]);
        }
        descriptors=used;
        if(stream.Length>AnimationPlaybackCodec.MaximumBytes)throw new InvalidDataException("Oversized compact animation state.");
        return stream.ToArray();
    }
    public static AnimationPlaybackState Decode(ReadOnlySpan<byte> bytes,AnimationDescriptorCatalog catalog)
    {
        if(bytes.Length<10||bytes.Length>AnimationPlaybackCodec.MaximumBytes)throw new InvalidDataException("Invalid compact animation size.");
        using var stream=new MemoryStream(bytes.ToArray(),false);using var r=new BinaryReader(stream);
        try
        {
            if(r.ReadByte()!=1)throw new InvalidDataException("Unknown compact animation version.");
            ulong epoch=ReadUnsigned(r),tick=ReadUnsigned(r);float step=r.ReadSingle();ulong action=ReadUnsigned(r),duration=ReadUnsigned(r);
            if(duration>AnimationPlaybackValidation.MaximumTick||tick>AnimationPlaybackValidation.MaximumTick-duration)throw new InvalidDataException("Invalid animation expiry.");
            int count=r.ReadByte();if(count>AnimationPlaybackValidation.MaximumLayers)throw new InvalidDataException("Too many animation layers.");
            var layers=new AnimationPlaybackLayer[count];
            Span<float> values=stackalloc float[5];
            for(int i=0;i<count;i++)
            {
                ulong id=ReadUnsigned(r);if(id==0||id>AnimationDescriptorCatalog.MaximumDescriptors)throw new InvalidDataException("Invalid animation descriptor reference.");
                var layer=catalog.Resolve((ushort)id);ushort flags=r.ReadUInt16();if((flags&~2047)!=0)throw new InvalidDataException("Unknown dynamic animation flags.");

                for(int k=0;k<values.Length;k++)values[k]=((flags>>(k*2))&3) switch{0=>0,1=>1,2=>r.ReadSingle(),_=>throw new InvalidDataException("Invalid animation float code.")};
                layers[i]=layer with{PhaseSeconds=values[0],Rate=values[1],StartWeight=values[2],TargetWeight=values[3],WeightSeconds=values[4],Wanted=(flags&(1<<10))!=0};
            }
            if(stream.Position!=stream.Length)throw new InvalidDataException("Trailing compact animation data.");
            var state=new AnimationPlaybackState(epoch,tick,step,action,tick+duration,Array.AsReadOnly(layers));AnimationPlaybackValidation.Validate(state);return state;
        }
        catch(EndOfStreamException error){throw new InvalidDataException("Truncated compact animation.",error);}
    }
    static int Code(float value)=>BitConverter.SingleToInt32Bits(value) switch{0=>0,0x3f800000=>1,_=>2};
    internal static void WriteUnsigned(BinaryWriter writer,ulong value)
    {while(value>=128){writer.Write((byte)((value&127)|128));value>>=7;}writer.Write((byte)value);}
    internal static ulong ReadUnsigned(BinaryReader reader)
    {
        ulong result=0;
        for(int shift=0;shift<70;shift+=7)
        {
            byte value=reader.ReadByte();if(shift==63&&value>1)throw new InvalidDataException("Oversized animation integer.");
            result|=(ulong)(value&127)<<shift;
            if((value&128)==0){if(shift!=0&&value==0)throw new InvalidDataException("Noncanonical animation integer.");return result;}
        }
        throw new InvalidDataException("Invalid animation integer.");
    }
}
