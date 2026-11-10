namespace Meitou.Networking.VNext;

/// <summary>Bounded lifecycle component body. The authenticated lifecycle envelope supplies session/view/entity/
/// interest fencing and reliable delivery; this codec does not imply transport integration or event delivery.</summary>
public static class AnimationPlaybackCodec
{
    public const int MaximumBytes = 4096;
    public static byte[] Encode(AnimationPlaybackState state)
    {
        AnimationPlaybackValidation.Validate(state);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, AnimationPlaybackValidation.Utf8, leaveOpen: true);
        writer.Write((ushort)21); writer.Write(state.Epoch); writer.Write(state.StartTick);
        writer.Write(state.TickSeconds); writer.Write(state.ActionInstance); writer.Write(state.ValidThroughTick); writer.Write((byte)state.Layers.Count);
        foreach (var layer in state.Layers)
        {
            var name = AnimationPlaybackValidation.Utf8.GetBytes(layer.Name);
            writer.Write((byte)name.Length); writer.Write(name); writer.Write(layer.Definition);
            writer.Write(layer.LengthSeconds); writer.Write(layer.PhaseSeconds); writer.Write(layer.Rate);
            writer.Write((byte)layer.Wrap); writer.Write(layer.EndSeconds); writer.Write(layer.StartWeight);
            writer.Write(layer.TargetWeight); writer.Write(layer.WeightSeconds);
            writer.Write((byte)layer.Area); writer.Write((byte)((layer.Wanted ? 1 : 0) | (layer.ConsumesLower ? 2 : 0)));
        }
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Oversized animation state.");
        return stream.ToArray();
    }
    public static AnimationPlaybackState Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 39 || bytes.Length > MaximumBytes) throw new InvalidDataException("Invalid animation component size.");
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, AnimationPlaybackValidation.Utf8);
        try
        {
            if (reader.ReadUInt16() != 21) throw new InvalidDataException("Unknown animation component version.");
            ulong epoch = reader.ReadUInt64(), tick = reader.ReadUInt64();
            float step = reader.ReadSingle(); ulong action = reader.ReadUInt64(), through = reader.ReadUInt64();
            int count = reader.ReadByte();
            if (count > AnimationPlaybackValidation.MaximumLayers) throw new InvalidDataException("Too many animation layers.");
            var layers = new AnimationPlaybackLayer[count];
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadByte();
                if (length == 0 || length > AnimationPlaybackValidation.MaximumNameBytes || stream.Length - stream.Position < length + 35)
                    throw new InvalidDataException("Invalid animation layer size.");
                string name = AnimationPlaybackValidation.Utf8.GetString(reader.ReadBytes(length));
                int definition = reader.ReadInt32();
                float duration = reader.ReadSingle(), phase = reader.ReadSingle(), rate = reader.ReadSingle();
                var wrap = (AnimationWrap)reader.ReadByte();
                float end = reader.ReadSingle(), from = reader.ReadSingle(), target = reader.ReadSingle(), blend = reader.ReadSingle();
                var area = (AnimationBlendArea)reader.ReadByte(); byte flags = reader.ReadByte();
                if ((flags & ~3) != 0) throw new InvalidDataException("Unknown animation weight flags.");
                layers[i] = new(name, definition, duration, phase, rate, wrap, end, from, target, blend,
                    area, (flags & 1) != 0, (flags & 2) != 0);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing animation component data.");
            var result = new AnimationPlaybackState(epoch, tick, step, action, through, Array.AsReadOnly(layers));
            AnimationPlaybackValidation.Validate(result);
            return result;
        }
        catch (Exception ex) when (ex is EndOfStreamException or System.Text.DecoderFallbackException)
        { throw new InvalidDataException("Truncated or malformed animation state.", ex); }
    }
}
