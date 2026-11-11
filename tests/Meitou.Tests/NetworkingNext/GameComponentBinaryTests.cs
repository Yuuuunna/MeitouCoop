using System.Numerics;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Tests.Engine;
namespace Meitou.Tests.NetworkingNext;

public sealed class GameComponentBinaryTests
{
    static PlayerWorldView Source(Meitou.Data.GameDatabase db)=>WorldViewCodecTests.View(db) with { Snapshot=WorldViewCodecTests.View(db).Snapshot with { Characters=[] } };
    static void Apply(GameStateReplica replica,GameStatePublication publication){foreach(var frame in publication.Frames)replica.Apply(frame);}
    [Fact]
    public void BinaryBodyRetainsEveryFlagCareAndExactFiniteFloatBitsAndNullWithdrawal()
    {
        var body=new BodyStatus(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),float.Epsilon,float.MaxValue,true,true,true,CareKind.Resting,true,true,true,-123.125f,true,true,true)
        {NeedsOrganicAid=true,NeedsRepairs=true,BeingEaten=true,Coma=true,Crawling=true,RightArmDisabled=true};
        var bytes=BodyStatusCodec.Encode(body);Assert.Equal(20,bytes.Length);var copy=BodyStatusCodec.Decode(bytes)!.Value;
        Assert.Equal(body,copy);Assert.Equal(BitConverter.SingleToInt32Bits(body.Blood),BitConverter.SingleToInt32Bits(copy.Blood));
        Assert.Null(BodyStatusCodec.Decode(BodyStatusCodec.Encode(null)));
        bytes[3]|=0x80;Assert.Throws<InvalidDataException>(()=>BodyStatusCodec.Decode(bytes));
        Assert.Throws<InvalidDataException>(()=>BodyStatusCodec.Encode(body with { Hunger=float.NaN }));
    }
    [Fact]
    public void OneOfThousandGroundItemChangesUsesOnlyOneSmallUpsertAndLateBaselineCannotRestoreDeletedItem()
    {
        var db=WorldViewCodecTests.Database();var source=Source(db);var codec=new WorldViewCodec(db);
        var items=Enumerable.Range(0,1000).Select(i=>new GroundItemSnapshot(i,"item","Item",new(i,0,0),Quaternion.Identity,false)).ToArray();
        source=source with{Snapshot=source.Snapshot with{GroundItems=items}};
        var publisher=new GameStatePublisher(codec,_=>42,11,22);var baseline=publisher.Capture(source);var replica=new GameStateReplica(codec,11,22);Apply(replica,baseline);
        items=(GroundItemSnapshot[])items.Clone();items[500]=items[500] with{Quality=.75f};
        var changed=source with{Publication=2,Snapshot=source.Snapshot with{GroundItems=items}};var delta=publisher.Capture(changed);
        Assert.True(delta.Frames.Sum(f=>f.Length)<1024);Apply(replica,delta);Assert.Equal(.75f,replica.View!.Snapshot.GroundItems[500].Quality);
        var removed=publisher.Capture(changed with{Publication=3,Snapshot=changed.Snapshot with{GroundItems=items.Where(i=>i.Id!=500).ToArray()}});
        var late=new GameStateReplica(codec,11,22);Apply(late,removed);Apply(late,baseline);Assert.Equal(999,late.View!.Snapshot.GroundItems.Count);Assert.DoesNotContain(late.View.Snapshot.GroundItems,i=>i.Id==500);
        var returned=publisher.Capture(changed with{Publication=4});Apply(late,returned);Apply(late,removed);Assert.Equal(1000,late.View!.Snapshot.GroundItems.Count);Assert.Equal(.75f,late.View.Snapshot.GroundItems[500].Quality);
        Apply(late,returned);Assert.Equal(1000,late.View.Snapshot.GroundItems.Count);
    }
    [Fact]
    public void CollectionReorderPreservesStableIdentityAndWeatherOneRegionChangesIndependently()
    {
        var db=WorldViewCodecTests.Database();var source=Source(db);var codec=new WorldViewCodec(db);var regions=Meitou.Tests.World.WeatherScheduleTests.MakeWorld().Snapshot();
        source=source with{Weather=new(Meitou.Data.World.WeatherTime.At(0),regions)};
        var publisher=new GameStatePublisher(codec,_=>42,11,22);var replica=new GameStateReplica(codec,11,22);Apply(replica,publisher.Capture(source));
        var changed=regions.Select((r,i)=>i==0?r with{Strength=.75f}:r).ToArray();var delta=publisher.Capture(source with{Publication=2,Weather=source.Weather with{Regions=changed}});
        Assert.True(delta.ComponentBytes[9]<2048);Apply(replica,delta);Assert.Equal(.75f,replica.View!.Weather!.Regions[0].Strength);
        Apply(replica,publisher.Capture(source with{Publication=3,Weather=source.Weather with{Regions=changed.Reverse().ToArray()}}));
        Assert.Equal(changed.Reverse().Select(r=>r.RegionId),replica.View!.Weather!.Regions.Select(r=>r.RegionId));
    }
    [Fact]
    public void BinaryBodyPrivateWithdrawalCannotBeUndoneByAnOldBaseline()
    {
        var db=WorldViewCodecTests.Database();var source=WorldViewCodecTests.View(db);source=source with{Snapshot=source.Snapshot with{Characters=[source.Snapshot.Characters[0] with{Animations=[],Body=new(.8f,.9f,2,false,false)}]}};
        var codec=new WorldViewCodec(db);var publisher=new GameStatePublisher(codec,_=>42,11,22);var baseline=publisher.Capture(source);
        var delta=publisher.Capture(source with{Publication=2,Snapshot=source.Snapshot with{Characters=[source.Snapshot.Characters[0] with{Body=null}]}});
        var replica=new GameStateReplica(codec,11,22);Apply(replica,delta);Apply(replica,baseline);Assert.Null(replica.View!.Snapshot.Characters[0].Body);
    }
    [Fact]
    public void WrongCollectionGenerationAndConflictingDuplicateRejectBeforeExposingCorruptState()
    {
        var db=WorldViewCodecTests.Database();var source=Source(db);var codec=new WorldViewCodec(db);
        var item=new GroundItemSnapshot(1,"item","Item",Vector3.Zero,Quaternion.Identity,false);
        source=source with{Snapshot=source.Snapshot with{GroundItems=[item]}};
        var publisher=new GameStatePublisher(codec,_=>42,11,22);var baseline=publisher.Capture(source);
        var delta=publisher.Capture(source with{Publication=2,Snapshot=source.Snapshot with{GroundItems=[item with{Quality=.75f}]}});
        byte[] frame=(byte[])Assert.Single(delta.Frames).Clone();
        using(var stream=new MemoryStream(frame,true))using(var reader=new BinaryReader(stream))
        {
            stream.Position=76+16;int playerBytes=reader.ReadInt32();stream.Position+=playerBytes+64;
            Assert.Equal(1,reader.ReadInt32());Assert.Equal(7,reader.ReadByte());stream.Position+=8;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan((int)stream.Position),1);
        }
        System.Security.Cryptography.SHA256.HashData(frame.AsSpan(76)).CopyTo(frame,44);
        var wrong=new GameStateReplica(codec,11,22);Apply(wrong,baseline);Assert.Throws<InvalidDataException>(()=>wrong.Apply(frame));Assert.Null(wrong.View);
        var duplicate=new GameStateReplica(codec,11,22);Apply(duplicate,baseline);Apply(duplicate,delta);
        frame=(byte[])delta.Frames[0].Clone();byte[] needle=System.Text.Encoding.UTF8.GetBytes("0.75");int valueAt=frame.AsSpan().IndexOf(needle);Assert.True(valueAt>=0);frame[valueAt+2]=(byte)'8';
        System.Security.Cryptography.SHA256.HashData(frame.AsSpan(76)).CopyTo(frame,44);
        Assert.Throws<InvalidDataException>(()=>duplicate.Apply(frame));Assert.Null(duplicate.View);
        Assert.Throws<InvalidDataException>(()=>publisher.Capture(source with{Publication=3,Snapshot=source.Snapshot with{GroundItems=[item,item]}}));
    }

}
