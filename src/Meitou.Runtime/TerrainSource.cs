using Meitou.Content;
using Meitou.Data.World;

namespace Meitou.Runtime;

/// <summary>Full-resolution terrain at arbitrary player locations, with a bounded thread-safe CPU tile cache.</summary>
public sealed class TerrainSource : IDisposable
{
    const int Cells = 64;
    readonly TerrainHeightmap map;
    readonly int capacity;
    readonly object gate = new();
    readonly Dictionary<(int X, int Z), (ushort[] Samples, LinkedListNode<(int, int)> Node)> tiles = [];
    readonly LinkedList<(int, int)> lru = [];
    bool disposed;

    public TerrainSource(TerrainHeightmap map, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (map.Size < 2) throw new ArgumentException("Terrain needs at least two samples per axis.", nameof(map));
        this.map = map;
        this.capacity = capacity;
    }

    public static TerrainSource Open(GameInstall install, int capacity = 256) => new(TerrainHeightmap.Open(install), capacity);
    public int CachedTiles { get { lock (gate) return tiles.Count; } }

    public float HeightAt(float x, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z)) throw new ArgumentOutOfRangeException(nameof(x), "Terrain coordinates must be finite.");
        double scale = (map.Size - 1) / (double)WorldLayout.WorldSize;
        double column = Math.Clamp((x + (double)WorldLayout.HalfWorldSize) * scale, 0, map.Size - 1);
        double row = Math.Clamp((z + (double)WorldLayout.HalfWorldSize) * scale, 0, map.Size - 1);
        int c = Math.Min((int)column, map.Size - 2), r = Math.Min((int)row, map.Size - 2);
        var key = (c / Cells, r / Cells);
        ushort[] samples;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!tiles.TryGetValue(key, out var tile))
            {
                samples = map.ReadWindow(key.Item1 * Cells, key.Item2 * Cells, Cells + 1, Cells + 1).Raw;
                if (tiles.Count >= capacity)
                {
                    tiles.Remove(lru.Last!.Value);
                    lru.RemoveLast();
                }
                var node = lru.AddFirst(key);
                tiles.Add(key, (samples, node));
            }
            else
            {
                samples = tile.Samples;
                lru.Remove(tile.Node);
                lru.AddFirst(tile.Node);
            }
        }
        int offset = (r % Cells) * (Cells + 1) + c % Cells;
        double fx = column - c, fz = row - r;
        double height = samples[offset] * (1 - fx) * (1 - fz) + samples[offset + 1] * fx * (1 - fz)
            + samples[offset + Cells + 1] * (1 - fx) * fz + samples[offset + Cells + 2] * fx * fz;
        return (float)(height * (WorldLayout.MaxHeight / ushort.MaxValue));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            tiles.Clear();
            lru.Clear();
            map.Dispose();
        }
    }
}
