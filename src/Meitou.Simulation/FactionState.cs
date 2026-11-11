namespace Meitou.Simulation;

/// <summary>
/// Mutable resources belonging to one runtime faction, independent of its members' interface state.
/// </summary>
public sealed class FactionState
{
    readonly SortedDictionary<int, int> _ranks = [];
    public IReadOnlyDictionary<int, int> Ranks => _ranks;
    internal void RestoreRanks(IEnumerable<KeyValuePair<int, int>> values)
    {
        _ranks.Clear();
        foreach (var (faction, rank) in values) _ranks.Add(faction, rank);
    }
    public int Rank(int otherFaction) => _ranks.GetValueOrDefault(otherFaction);
    public bool RaiseRank(int otherFaction, int cap)
    {
        int rank = Rank(otherFaction);
        if (otherFaction < 0 || rank == int.MaxValue || cap > 0 && rank >= cap) return false;
        _ranks[otherFaction] = rank + 1;
        return true;
    }
    internal FactionState(int index) => Index = index;

    public int Index { get; }
    /// <summary>
    /// Player-faction classification survives removal of its last member; funds and assets remain in the world.
    /// </summary>
    public bool PlayerOwned { get; internal set; }
    public string? Name { get; set; }
    public long Money { get; set; }
    public bool TryDebit(long amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (Money < amount) return false;
        Money -= amount;
        return true;
    }

    public bool TryCredit(long amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (Money > long.MaxValue - amount) return false;
        Money += amount;
        return true;
    }
    public Construction.ResearchState Research { get; } = new();

    internal void Hash(ref StateHasher h)
    {
        h.Add(Index);
        h.Add(PlayerOwned);
        h.Add(Money);
        h.Add(Name is not null);
        if (Name is not null) h.Add(Rng.StableHash(Name));
        Research.Hash(ref h);
    }
}

/// <summary>
/// World-owned faction resources. Indices address the world's faction catalogue, not network identities.
/// </summary>
public sealed class FactionStates
{
    readonly SortedDictionary<int, FactionState> _states = [];

    public IEnumerable<FactionState> All => _states.Values;
    public FactionState? Find(int index) => _states.GetValueOrDefault(index);
    internal void DiscardUnowned(int index)
    {
        if (_states.TryGetValue(index, out var state) && state.PlayerOwned) throw new InvalidOperationException("Cannot discard an owned faction resource account.");
        _states.Remove(index);
    }

    public FactionState GetOrCreate(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (!_states.TryGetValue(index, out var state)) _states.Add(index, state = new FactionState(index));
        return state;
    }

    internal void HashExcept(ref StateHasher h, int localFaction)
    {
        foreach (var (index, state) in _states)
        {
            if (index == localFaction) continue; // the existing single-player hash includes its resources
            if (!state.PlayerOwned && state.Money == 0 && state.Name is null && !state.Research.Changed) continue; // querying default NPC resources does not change world state
            h.Add(0x46616374);
            state.Hash(ref h);
        }
    }

    internal void HashRanks(ref StateHasher h)
    {
        foreach (var state in _states.Values)
        {
            if (state.Ranks.Count == 0) continue;
            h.Add(0x52616E6B); h.Add(state.Index); h.Add(state.Ranks.Count);
            foreach (var (other, rank) in state.Ranks) { h.Add(other); h.Add(rank); }
        }
    }
}