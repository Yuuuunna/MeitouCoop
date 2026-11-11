using Meitou.Content;
using Meitou.Data.World;

namespace Meitou.Runtime;

/// <summary>
/// Full-resolution terrain at arbitrary player locations, with a bounded thread-safe CPU tile cache.
/// </summary>
public sealed class TerrainSource : IDisposable
{
    const int Cells = 64;
    // World-wide weather queries more than 256 tiles per sweep. Keep a bounded ~33 MiB
    // sample working set so every tick does not reread the same TIFF rows from disk.
    const int DefaultCapacity = 4096;
    readonly TerrainHeightmap _map;
    readonly int _capacity;
    readonly object _gate = new();
    readonly Dictionary<(int X, int Z), (ushort[] Samples, LinkedListNode<(int, int)> Node)> _tiles = [];
    readonly LinkedList<(int, int)> _lru = [];
    bool _disposed;
    public TerrainSource(TerrainHeightmap map, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (map.Size < 2)
            throw new ArgumentException("Terrain needs at least two samples per axis.", nameof(map));
        this._map = map;
        this._capacity = capacity;
    }

    public static TerrainSource Open(GameInstall install, int capacity = DefaultCapacity) => new(TerrainHeightmap.Open(install), capacity);
    public int CachedTiles
    {
        get
        {
            lock (_gate)
                return _tiles.Count;
        }
    }

    public float HeightAt(float x, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "Terrain coordinates must be finite.");
        double scale = (_map.Size - 1) / (double)WorldLayout.WorldSize;
        double column = Math.Clamp((x + (double)WorldLayout.HalfWorldSize) * scale, 0, _map.Size - 1);
        double row = Math.Clamp((z + (double)WorldLayout.HalfWorldSize) * scale, 0, _map.Size - 1);
        int c = Math.Min((int)column, _map.Size - 2), r = Math.Min((int)row, _map.Size - 2);
        var key = (c / Cells, r / Cells);
        ushort[] samples;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_tiles.TryGetValue(key, out var tile))
            {
                samples = _map.ReadWindow(key.Item1 * Cells, key.Item2 * Cells, Cells + 1, Cells + 1).Raw;
                if (_tiles.Count >= _capacity)
                {
                    _tiles.Remove(_lru.Last!.Value);
                    _lru.RemoveLast();
                }

                var node = _lru.AddFirst(key);
                _tiles.Add(key, (samples, node));
            }
            else
            {
                samples = tile.Samples;
                _lru.Remove(tile.Node);
                _lru.AddFirst(tile.Node);
            }
        }

        int offset = (r % Cells) * (Cells + 1) + c % Cells;
        double fx = column - c, fz = row - r;
        double height = samples[offset] * (1 - fx) * (1 - fz) + samples[offset + 1] * fx * (1 - fz) + samples[offset + Cells + 1] * (1 - fx) * fz + samples[offset + Cells + 2] * fx * fz;
        return (float)(height * (WorldLayout.MaxHeight / ushort.MaxValue));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _tiles.Clear();
            _lru.Clear();
            _map.Dispose();
        }
    }
}