using Meitou.Data.World;

namespace Meitou.Data.Particles;

/// <summary>Engine-owned MT19937 adapter for authoritative effects. State is portable; ordinary renderer randomness is unchanged.</summary>
public sealed class CheckpointRandom(int seed) : Random
{
    readonly FoliageRandom _generator = new(unchecked((uint)seed));
    public FoliageRandomState Capture() => _generator.Capture();
    public void Restore(FoliageRandomState state) => _generator.Restore(state);
    protected override double Sample() => NextDouble();
    public override int Next() => (int)_generator.IntUpTo(int.MaxValue - 1);
    public override int Next(int maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxValue);
        return maxValue == 0 ? 0 : (int)_generator.IntUpTo((uint)maxValue - 1);
    }
    public override int Next(int minValue, int maxValue)
    {
        if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(maxValue));
        ulong range = (ulong)((long)maxValue - minValue);
        return range == 0 ? minValue : (int)(minValue + (long)_generator.IntUpTo((uint)(range - 1)));
    }
    public override float NextSingle() => (_generator.Next() >> 8) * (1f / 16777216f);
    public override double NextDouble() => ((ulong)(_generator.Next() >> 5) * 67108864UL + (_generator.Next() >> 6)) * (1.0 / 9007199254740992.0);
    public override long NextInt64() => NextInt64(long.MaxValue);
    public override long NextInt64(long maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxValue);
        return maxValue == 0 ? 0 : (long)Bound((ulong)maxValue);
    }
    public override long NextInt64(long minValue, long maxValue)
    {
        if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(maxValue));
        ulong range = unchecked((ulong)maxValue - (ulong)minValue);
        return range == 0 ? minValue : unchecked((long)(Bound(range) + (ulong)minValue));
    }
    ulong Bound(ulong range)
    {
        ulong threshold = unchecked(0UL - range) % range, value;
        do { value = (ulong)_generator.Next() << 32 | _generator.Next(); } while (value < threshold);
        return value % range;
    }
    public override void NextBytes(byte[] buffer) { ArgumentNullException.ThrowIfNull(buffer); NextBytes(buffer.AsSpan()); }
    public override void NextBytes(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length;)
        {
            uint word = _generator.Next();
            for (int n = 0; n < 4 && i < buffer.Length; n++, i++, word >>= 8) buffer[i] = (byte)word;
        }
    }
}
