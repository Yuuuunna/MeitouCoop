using System.Buffers.Binary;
using Meitou.Simulation;
namespace Meitou.Networking.VNext;

/// <summary>Exact IEEE754 body values and explicit flag bits; null is an authorization withdrawal.</summary>
public static class BodyStatusCodec
{
    public const int ValueBytes=20;
    static BodyStatusCodec()
    {
        string[] fields="Blood WorstPart Hunger Unconscious Dead NeedsAid Care MedicJob RescueJob NeedsSplint KoTimer PlayingDead StayingLow NeedsRobotAid NeedsOrganicAid NeedsRepairs BeingEaten Coma Crawling RightArmDisabled".Split(' ');
        if(!fields.Order().SequenceEqual(typeof(BodyStatus).GetProperties().Select(p=>p.Name).Order())) throw new InvalidOperationException("Body wire schema coverage changed.");
    }
    public static byte[] Encode(BodyStatus? value)
    {
        if(value is not { } body)return [0];
        Validate(body); byte[] bytes=new byte[ValueBytes]; bytes[0]=1;bytes[1]=(byte)body.Care;
        bool[] flags=[body.Unconscious,body.Dead,body.NeedsAid,body.MedicJob,body.RescueJob,body.NeedsSplint,body.PlayingDead,body.StayingLow,body.NeedsRobotAid,body.NeedsOrganicAid,body.NeedsRepairs,body.BeingEaten,body.Coma,body.Crawling,body.RightArmDisabled];
        ushort bits=0;for(int i=0;i<flags.Length;i++)if(flags[i])bits|=(ushort)(1<<i);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2),bits);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4),body.Blood);BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8),body.WorstPart);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12),body.Hunger);BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16),body.KoTimer);return bytes;
    }
    public static BodyStatus? Decode(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length==1&&bytes[0]==0)return null;
        if(bytes.Length!=ValueBytes||bytes[0]!=1||bytes[1]>(byte)CareKind.Resting)throw new InvalidDataException("Invalid body component.");
        ushort bits=BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);if((bits&0x8000)!=0)throw new InvalidDataException("Unknown body flags.");
        bool Flag(int bit)=>(bits&(1<<bit))!=0;
        var value=new BodyStatus(BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]),BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]),
            Flag(0),Flag(1),Flag(2),(CareKind)bytes[1],Flag(3),Flag(4),Flag(5),BinaryPrimitives.ReadSingleLittleEndian(bytes[16..]),Flag(6),Flag(7),Flag(8))
            {NeedsOrganicAid=Flag(9),NeedsRepairs=Flag(10),BeingEaten=Flag(11),Coma=Flag(12),Crawling=Flag(13),RightArmDisabled=Flag(14)};
        Validate(value);return value;
    }
    static void Validate(BodyStatus value)
    {
        if(!Enum.IsDefined(value.Care)||!float.IsFinite(value.Blood)||!float.IsFinite(value.WorstPart)||!float.IsFinite(value.Hunger)||!float.IsFinite(value.KoTimer))throw new InvalidDataException("Invalid body values.");
    }
}
