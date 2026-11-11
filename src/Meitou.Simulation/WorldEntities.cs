namespace Meitou.Simulation;

/// <summary>
/// A persistent identity within a world. Network references must also carry that world's incarnation.
/// </summary>
public readonly record struct EntityId(ulong Value)
{
    public bool IsNone => Value == 0;
}

/// <summary>
/// Persistent character identities, allocated on first authoritative registration. Entries outlive loaded slots.
/// Save restoration must reserve all identities (including unloaded characters) before allocating new ones.
/// </summary>
public sealed class WorldEntities
{
    readonly CharacterTable _table;
    readonly SortedSet<ulong> _known = [];
    ulong _lastId;

    internal WorldEntities(CharacterTable table) => this._table = table;

    public IEnumerable<EntityId> All => _known.Select(value => new EntityId(value));
    public ulong LastAllocated => _lastId;
    public bool Contains(EntityId id) => !id.IsNone && _known.Contains(id.Value);

    public EntityId Track(CharacterId character)
    {
        EnsureMutable();
        if (!_table.TryResolveNext(character, out int slot)) throw new InvalidOperationException("The character is not loaded.");
        var cold = _table.Cold(slot)!;
        if (!cold.Entity.IsNone)
        {
            Reserve(cold.Entity);
            return cold.Entity;
        }
        var id = new EntityId(checked(_lastId + 1));
        _table.BindEntity(character, id);
        Reserve(id);
        return id;
    }

    /// <summary>
    /// Reserves a saved identity whether or not its character is currently loaded.
    /// </summary>
    public void Reserve(EntityId id)
    {
        EnsureMutable();
        if (id.IsNone) throw new ArgumentException("An entity identity cannot be empty.", nameof(id));
        _known.Add(id.Value);
        _lastId = Math.Max(_lastId, id.Value);
    }

    public bool TryResolve(EntityId id, out CharacterId character)
    {
        character = CharacterId.None;
        return Contains(id) && _table.TryResolveEntity(id, out character);
    }

    internal void Restore(ulong highWater, IReadOnlyList<ulong> identities)
    {
        EnsureMutable();
        if (_known.Count != 0 || _lastId != 0 || _table.Count != 0)
            throw new InvalidOperationException("Entity allocation is restored only into an empty world.");
        foreach (ulong value in identities) _known.Add(value);
        _lastId = highWater;
    }

    void EnsureMutable()
    {
        if (_table.Phase is TablePhase.Parallel) throw new InvalidOperationException("Entity identities change only in serial phases.");
    }

    internal void Hash(ref StateHasher h)
    {
        if (_known.Count == 0) return;
        h.Add(0x456E7469);
        h.Add(_lastId);
        h.Add(_known.Count);
        foreach (ulong value in _known)
        {
            h.Add(value);
            h.Add(_table.TryResolveEntity(new EntityId(value), out var character));
            h.Add(character);
        }
    }
}