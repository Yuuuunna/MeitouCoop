using Meitou.Networking.VNext;

namespace Meitou.Tests.NetworkingNext;

public class CompactAnimationPlaybackCodecTests
{
    static AnimationPlaybackState State(ulong epoch=1) => new(epoch,100,.033333335f,7,1800,
        [new("walk lower",3,1.4f,.7f,-0f,AnimationWrap.Loop,1.3986f,.3f,1,0,AnimationBlendArea.Lower,true,false),
         new("guard",8,2,1,-.75f,AnimationWrap.Clamp,1.998f,1,0,.4f,AnimationBlendArea.Upper,false,false)]);
    [Fact]
    public void AbsoluteDynamicStateRoundTripsExactReferenceBytesWithoutAnyPreviousState()
    {
        var sender=new AnimationDescriptorCatalog();var receiver=new AnimationDescriptorCatalog();
        var old=State();CompactAnimationPlaybackCodec.Encode(old,sender,out var ids);
        foreach(ushort id in ids)receiver.ApplyDefinition(sender.EncodeDefinition(id));
        var latest=State(300) with { StartTick=1000,ValidThroughTick=2000,
            Layers=old.Layers.Select(l=>l with{PhaseSeconds=.2f,Rate=-0f,StartWeight=-0f}).ToArray() };
        byte[] compact=CompactAnimationPlaybackCodec.Encode(latest,sender,out _);
        var decoded=CompactAnimationPlaybackCodec.Decode(compact,receiver);
        Assert.Equal(AnimationPlaybackCodec.Encode(latest),AnimationPlaybackCodec.Encode(decoded));
        Assert.True(compact.Length<AnimationPlaybackCodec.Encode(latest).Length/2);
        Assert.Equal(2,receiver.Count);
    }
    [Fact]
    public void HighChurnAbsoluteDynamicsKeepDescriptorCostConstantAndReferenceBitsExact()
    {
        var sender=new AnimationDescriptorCatalog();var receiver=new AnimationDescriptorCatalog();var announced=new HashSet<ushort>();
        long referenceBytes=0,compactBytes=0,definitionBytes=0;
        for(ulong i=1;i<=1000;i++)
        {
            var state=State(i) with{StartTick=i,ValidThroughTick=i+900,Layers=State().Layers.Select(l=>l with
                {PhaseSeconds=(float)(i%20)*.031f,Rate=(float)i*.001f,StartWeight=(float)(i%7)*.12f,TargetWeight=(float)(i%11)*.08f,WeightSeconds=(float)(i%13)*.01f}).ToArray()};
            byte[] compact=CompactAnimationPlaybackCodec.Encode(state,sender,out var used);
            foreach(ushort id in used)if(announced.Add(id))
            {byte[] definition=sender.EncodeDefinition(id);definitionBytes+=definition.Length+19;receiver.ApplyDefinition(definition);}
            var decoded=CompactAnimationPlaybackCodec.Decode(compact,receiver);
            byte[] reference=AnimationPlaybackCodec.Encode(state);Assert.Equal(reference,AnimationPlaybackCodec.Encode(decoded));
            referenceBytes+=reference.Length;compactBytes+=compact.Length;
        }
        Assert.Equal(2,sender.Count);Assert.True(compactBytes+definitionBytes<referenceBytes*.6);
    }
    [Fact]
    public void CatalogIsAdditiveAcrossLateBaselinesAndRejectsImmutableConflicts()
    {
        var sender=new AnimationDescriptorCatalog();var receiver=new AnimationDescriptorCatalog();
        CompactAnimationPlaybackCodec.Encode(State(),sender,out var ids);
        receiver.ApplyDefinition(sender.EncodeDefinition(ids[1]));receiver.ApplyDefinition(sender.EncodeDefinition(ids[0]));
        receiver.ApplyDefinition(sender.EncodeDefinition(ids[0]));Assert.Equal(2,receiver.Count);
        var conflicting=new AnimationDescriptorCatalog();
        CompactAnimationPlaybackCodec.Encode(State() with{Layers=[State().Layers[0] with{Name="different"}]},conflicting,out _);
        Assert.Throws<InvalidDataException>(()=>receiver.ApplyDefinition(conflicting.EncodeDefinition(1)));
        Assert.Equal(2,receiver.Count);receiver.Clear();Assert.Equal(0,receiver.Count);
    }
    [Fact]
    public void MissingDescriptorsTruncationAndNoncanonicalCountersReject()
    {
        var catalog=new AnimationDescriptorCatalog();byte[] bytes=CompactAnimationPlaybackCodec.Encode(State(),catalog,out _);
        Assert.Throws<InvalidDataException>(()=>CompactAnimationPlaybackCodec.Decode(bytes,new()));
        for(int length=0;length<bytes.Length;length++)Assert.Throws<InvalidDataException>(()=>CompactAnimationPlaybackCodec.Decode(bytes.AsSpan(0,length),catalog));
        byte[] overlong=[bytes[0],0x81,0,..bytes.AsSpan(2).ToArray()];
        Assert.Throws<InvalidDataException>(()=>CompactAnimationPlaybackCodec.Decode(overlong,catalog));
        Assert.Throws<InvalidDataException>(()=>CompactAnimationPlaybackCodec.Decode([..bytes,0],catalog));
    }
    [Fact]
    public void DescriptorBoundRejectsNewIdentityWithoutChangingExistingMeanings()
    {
        var catalog=new AnimationDescriptorCatalog();
        for(int i=0;i<AnimationDescriptorCatalog.MaximumDescriptors;i++)
            CompactAnimationPlaybackCodec.Encode(State() with{Layers=[State().Layers[0] with{Name=$"clip{i}"}]},catalog,out _);
        Assert.Equal(AnimationDescriptorCatalog.MaximumDescriptors,catalog.Count);
        Assert.Throws<InvalidDataException>(()=>CompactAnimationPlaybackCodec.Encode(State(),catalog,out _));
        Assert.Equal(AnimationDescriptorCatalog.MaximumDescriptors,catalog.Count);
    }
}
