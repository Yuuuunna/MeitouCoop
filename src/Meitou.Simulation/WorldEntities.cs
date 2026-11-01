namespace Meitou.Simulation;

/// <summary>A persistent identity within a world. Network references must also carry that world's incarnation.</summary>
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
    readonly CharacterTable table;
    readonly SortedSet<ulong> known = [];
    ulong lastId;

    internal WorldEntities(CharacterTable table) => this.table = table;

    public IEnumerable<EntityId> All => known.Select(value => new EntityId(value));
    public ulong LastAllocated => lastId;
    public bool Contains(EntityId id) => !id.IsNone && known.Contains(id.Value);

    public EntityId Track(CharacterId character)
    {
        EnsureMutable();
        if (!table.TryResolveNext(character, out int slot)) throw new InvalidOperationException("The character is not loaded.");
        var cold = table.Cold(slot)!;
        if (!cold.Entity.IsNone)
        {
            Reserve(cold.Entity);
            return cold.Entity;
        }
        var id = new EntityId(checked(lastId + 1));
        table.BindEntity(character, id);
        Reserve(id);
        return id;
    }

    /// <summary>Reserves a saved identity whether or not its character is currently loaded.</summary>
    public void Reserve(EntityId id)
    {
        EnsureMutable();
        if (id.IsNone) throw new ArgumentException("An entity identity cannot be empty.", nameof(id));
        known.Add(id.Value);
        lastId = Math.Max(lastId, id.Value);
    }

    public bool TryResolve(EntityId id, out CharacterId character)
    {
        character = CharacterId.None;
        return Contains(id) && table.TryResolveEntity(id, out character);
    }

    internal void Restore(ulong highWater, IReadOnlyList<ulong> identities)
    {
        EnsureMutable();
        if (known.Count != 0 || lastId != 0 || table.Count != 0)
            throw new InvalidOperationException("Entity allocation is restored only into an empty world.");
        foreach (ulong value in identities) known.Add(value);
        lastId = highWater;
    }

    void EnsureMutable()
    {
        if (table.Phase is TablePhase.Parallel) throw new InvalidOperationException("Entity identities change only in serial phases.");
    }

    internal void Hash(ref StateHasher h)
    {
        if (known.Count == 0) return;
        h.Add(0x456E7469);
        h.Add(lastId);
        h.Add(known.Count);
        foreach (ulong value in known)
        {
            h.Add(value);
            h.Add(table.TryResolveEntity(new EntityId(value), out var character));
            h.Add(character);
        }
    }
}
