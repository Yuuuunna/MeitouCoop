using System.Buffers.Binary;
using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Networking.VNext;

/// <summary>
/// Exact little-endian ragdoll poses. Only set bones carry transforms; unset bones have no presentation meaning and decode to zero.
/// </summary>
public static class RagdollPoseCodec
{
    /// <summary>
    /// Matches the full-view pose reader's skeleton bound, checked before allocating arrays.
    /// </summary>
    public const int MaximumBones = 1024;

    /// <summary>
    /// Presence, skeleton length, set-bone count and the exact blend weight.
    /// </summary>
    const int HeaderBytes = 9;

    /// <summary>
    /// A bone index, four rotation floats and three position floats; positions are retained without assuming a particular rig.
    /// </summary>
    const int BoneBytes = 30;

    /// <summary>
    /// Encodes null as one zero byte; a present pose preserves every set transform's IEEE754 bits without quantization.
    /// </summary>
    public static byte[] Encode(BonePose? pose)
    {
        if (pose is null) return [0];

        Validate(pose);
        int count = pose.Set.Count(set => set);
        byte[] bytes = new byte[HeaderBytes + count * BoneBytes];
        bytes[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), (ushort)pose.Set.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(3), (ushort)count);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(5), pose.Weight);

        int offset = HeaderBytes;
        for (int bone = 0; bone < pose.Set.Length; bone++)
        {
            if (!pose.Set[bone]) continue;

            WriteBone(bytes.AsSpan(offset, BoneBytes), bone, pose.Rotation[bone], pose.Position[bone]);
            offset += BoneBytes;
        }

        return bytes;
    }

    /// <summary>
    /// Rejects oversized skeletons, truncated/trailing data, duplicate or unordered indices and non-finite transmitted values.
    /// </summary>
    public static BonePose? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 1 && bytes[0] == 0) return null;
        if (bytes.Length < HeaderBytes || bytes[0] != 1) throw new InvalidDataException("Invalid ragdoll component.");

        int bones = BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[3..]);
        float weight = BinaryPrimitives.ReadSingleLittleEndian(bytes[5..]);
        if (bones > MaximumBones || count > bones || bytes.Length != HeaderBytes + count * BoneBytes || !float.IsFinite(weight))
            throw new InvalidDataException("Invalid ragdoll component bounds.");

        var pose = new BonePose(bones) { Weight = weight };
        int previous = -1;
        for (int offset = HeaderBytes; offset < bytes.Length; offset += BoneBytes)
        {
            var value = bytes.Slice(offset, BoneBytes);
            int bone = BinaryPrimitives.ReadUInt16LittleEndian(value);
            if (bone <= previous || bone >= bones) throw new InvalidDataException("Invalid ragdoll bone index.");

            pose.Set[bone] = true;
            pose.Rotation[bone] = new(ReadFloat(value, 2), ReadFloat(value, 6), ReadFloat(value, 10), ReadFloat(value, 14));
            pose.Position[bone] = new(ReadFloat(value, 18), ReadFloat(value, 22), ReadFloat(value, 26));
            previous = bone;
        }

        Validate(pose);
        return pose;
    }

    /// <summary>
    /// Takes an owned value snapshot; subsequent changes to the source cannot change a worker's capture or comparison baseline.
    /// </summary>
    internal static BonePose? Copy(BonePose? source)
    {
        if (source is null) return null;

        Validate(source);
        var copy = new BonePose(source.Set.Length) { Weight = source.Weight };
        for (int bone = 0; bone < source.Set.Length; bone++)
        {
            if (!source.Set[bone]) continue;

            copy.Set[bone] = true;
            copy.Rotation[bone] = source.Rotation[bone];
            copy.Position[bone] = source.Position[bone];
        }

        return copy;
    }

    /// <summary>
    /// Compares the transmitted values before encoding, including signed zero; inactive array entries never dirty a pose.
    /// </summary>
    internal static bool Same(BonePose? previous, BonePose? current)
    {
        if (current is null) return previous is null;

        Validate(current);
        if (previous is null || previous.Set.Length != current.Set.Length || !SameBits(previous.Weight, current.Weight)) return false;
        for (int bone = 0; bone < current.Set.Length; bone++)
        {
            if (previous.Set[bone] != current.Set[bone]) return false;
            if (!current.Set[bone]) continue;
            if (!SameBits(previous.Rotation[bone], current.Rotation[bone]) || !SameBits(previous.Position[bone], current.Position[bone]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks only values that affect posing; unset array entries are never read by the renderer or transmitted.
    /// </summary>
    static void Validate(BonePose pose)
    {
        if (pose.Set.Length > MaximumBones || pose.Rotation.Length != pose.Set.Length || pose.Position.Length != pose.Set.Length
            || !float.IsFinite(pose.Weight)) throw new InvalidDataException("Invalid ragdoll pose.");

        for (int bone = 0; bone < pose.Set.Length; bone++)
        {
            if (!pose.Set[bone]) continue;

            var rotation = pose.Rotation[bone];
            var position = pose.Position[bone];
            if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W)
                || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new InvalidDataException("Non-finite ragdoll transform.");
        }
    }

    /// <summary>
    /// Writes one complete active transform, in increasing bone-index order.
    /// </summary>
    static void WriteBone(Span<byte> bytes, int bone, Quaternion rotation, Vector3 position)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)bone);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[2..], rotation.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[6..], rotation.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[10..], rotation.Z);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[14..], rotation.W);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[18..], position.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[22..], position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[26..], position.Z);
    }

    /// <summary>
    /// Reads a validated entry's exact float bits.
    /// </summary>
    static float ReadFloat(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);

    /// <summary>
    /// Float equality that preserves the wire distinction between positive and negative zero.
    /// </summary>
    static bool SameBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    /// <summary>
    /// Exact component equality for a model-space position.
    /// </summary>
    static bool SameBits(Vector3 a, Vector3 b) => SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z);

    /// <summary>
    /// Exact component equality for a model-space rotation, without normalization or sign folding.
    /// </summary>
    static bool SameBits(Quaternion a, Quaternion b) => SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z) && SameBits(a.W, b.W);
}
