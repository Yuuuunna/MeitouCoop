using System.Numerics;

namespace Meitou.Simulation.Crime;

/// <summary>
/// Pending, awake suspects collected lazily after the crime sweep and indexed from the same slow-world phase's current positions.
/// </summary>
sealed class InvestigationCandidates
{
    /// <summary>
    /// The collected suspects, unique and in slot order.
    /// </summary>
    readonly List<int> _slots = [];

    /// <summary>
    /// The candidates returned to one serial investigator, in the original scan order.
    /// </summary>
    readonly List<int> _nearby = [];

    /// <summary>
    /// The suspects' current positions; separate from the world's previous-position movement grid.
    /// </summary>
    readonly SpatialGrid _grid = new(250);

    /// <summary>
    /// Whether the current sweep's positions have been indexed.
    /// </summary>
    bool _built;

    /// <summary>
    /// Invalidates the previous slow-world phase's candidates; no scan or spatial work is needed until an investigator searches.
    /// </summary>
    public void Clear() => _built = false;

    /// <summary>
    /// The suspects near <paramref name="at"/>, ordered by slot so all qualifying enemies are tagged and threatened in the original scan order.
    /// The returned scratch list is valid until the next query.
    /// </summary>
    public List<int> Near(World world, Vector3 at, float range)
    {
        if (!_built)
        {
            Collect(world);
            _grid.Build(world.Characters.Next, _slots);
            _built = true;
        }

        _grid.QueryInSlotOrder(at.X, at.Z, range, _nearby);
        return _nearby;
    }

    /// <summary>
    /// Takes the complete post-sweep suspect set once, including crimes created or cleared by another character's witness processing.
    /// </summary>
    void Collect(World world)
    {
        _slots.Clear();
        var table = world.Characters;
        var next = table.Next;
        for (int slot = 0; slot < table.HighWater; slot++)
        {
            if (next[slot].Alive && table.Cold(slot) is { Pending: not null, Medical.Incapacitated: false }) _slots.Add(slot);
        }
    }
}
