using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Aggro;

/// <summary>
/// The tick's census for the looks of the <see cref="AggroSystem"/> (taken in its <c>Sync</c>, read only during the looks): the player's
/// characters, the fighters, where the awake sneakers stand, and which factions present have an enemy among them.
/// </summary>
sealed class LookCensus
{
    /// <summary>
    /// The factions with a character present this census, the same of the census the enemies were worked out for, and which of them have an enemy
    /// among the factions present.
    /// </summary>
    bool[] _present = [], _presentBefore = [], _hasEnemy = [];

    /// <summary>
    /// The relation changes the enemies were worked out for.
    /// </summary>
    long _changesBefore = -1;

    /// <summary>
    /// The factions present, in order, while the enemies are worked out.
    /// </summary>
    readonly List<int> _presentList = [];

    /// <summary>
    /// Where the awake sneaking characters stand this tick (read only during the looks): a looker that picks no fights looks only when one is in its
    /// range.
    /// </summary>
    readonly List<Vector3> _sneakers = [];

    /// <summary>
    /// The player's characters this tick: a player's character always senses the others (<c>VisitKin</c>).
    /// </summary>
    public List<int> PlayerSlots { get; } = [];

    /// <summary>
    /// The characters in a fight this tick, in slot order, while their position index is built.
    /// </summary>
    readonly List<int> _fighterSlots = [];

    /// <summary>
    /// The tick's fighters indexed by their previous positions, as read by the parallel sense pass; immutable until the next census.
    /// </summary>
    public SpatialGrid Fighters { get; } = new(250);

    /// <summary>
    /// Whether any awake sneaking character stands about this tick.
    /// </summary>
    public bool AnySneakers => _sneakers.Count > 0;

    /// <summary>
    /// The number of factions the enemies are kept for (the relation table's when the census was taken).
    /// </summary>
    public int FactionCount => _hasEnemy.Length;

    /// <summary>
    /// Whether the faction has an enemy among the factions present (a peaceful crowd skips its looks).
    /// </summary>
    public bool HasEnemy(int faction) => _hasEnemy[faction];

    /// <summary>
    /// Starts a census for <paramref name="factions"/> factions: nobody counted yet.
    /// </summary>
    public void Begin(int factions)
    {
        if (_present.Length != factions)
        {
            _present = new bool[factions];
            _hasEnemy = new bool[factions];
        }

        Array.Clear(_present);
        _sneakers.Clear();
        PlayerSlots.Clear();
        _fighterSlots.Clear();
    }

    /// <summary>
    /// Counts the living character in <paramref name="i"/>.
    /// </summary>
    public void Add(CharacterTable table, int i, FactionRelations relations)
    {
        if (table.Cold(i) is { } pc)
        {
            if (pc.IsPlayer) PlayerSlots.Add(i);
            if (pc.InCombat) _fighterSlots.Add(i);
        }

        if (table.Cold(i) is { Medical: not null } c && relations.Contains(c.Faction))
        {
            _present[c.Faction] = true;
            if ((c.Orders & CharacterOrders.Sneak) != 0 && !c.Medical.Incapacitated)
                _sneakers.Add(table.Next[i].Position);
        }
    }

    /// <summary>
    /// Seals the fighters' position index and updates faction hostility only when the present factions or their relations changed.
    /// </summary>
    public void Settle(FactionRelations relations, CharacterTable table)
    {
        Fighters.Build(table.Previous, _fighterSlots);
        if (_present.AsSpan().SequenceEqual(_presentBefore) && relations.Changes == _changesBefore) return;

        _presentBefore = (bool[])_present.Clone();
        _changesBefore = relations.Changes;
        _presentList.Clear();
        for (int f = 0; f < _present.Length; f++) if (_present[f]) _presentList.Add(f);

        foreach (int f in _presentList)
        {
            _hasEnemy[f] = false;

            foreach (int g in _presentList)
            {
                if (AggroSystem.AttacksOnSight(relations, f, g))
                {
                    _hasEnemy[f] = true;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Whether an awake sneaker stands within <paramref name="range"/> of <paramref name="eye"/>.
    /// </summary>
    public bool SneakerWithin(Vector3 eye, float range)
    {
        foreach (var p in _sneakers) if (Vector3.DistanceSquared(p, eye) <= range * range) return true;

        return false;
    }
}
