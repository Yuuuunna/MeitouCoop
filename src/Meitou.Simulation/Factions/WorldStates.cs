using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Factions;

/// <summary>
/// The WORLD_EVENT_STATEs answered live, as the original's <c>FUN_1409a7e00</c> (docs/game/factions-squads-towns.md "Unique characters",
/// queries): a state holds when every <c>NPC is</c> unique has its state (and, with <c>player involvement</c>, one other than alive has the player
/// flag), no <c>NPC is NOT</c> unique has its state (with the player flag needed for an alive one), and each <c>player ally</c> / <c>player enemy</c>
/// faction is or is not the player's ally (relation ≥ 50) / hostile to it (≤ -30) as the entry says. A unique with no entry is alive and the player
/// not involved, so a new game answers by the data alone. Nothing is saved: the unique table and the relations are.
/// <para>The population refreshes the unique states it reads every tick (<see cref="Refresh"/>, serial; a look's own changes to the table are taken
/// again at once); a query reads that copy, so the build threads may ask at any time.</para>
/// </summary>
public sealed class WorldStates(GameDatabase db, FactionRelations relations) : IWorldStates
{
    /// <summary>
    /// The WORLD_EVENT_STATE records by id, read once each (see <see cref="Find"/>).
    /// </summary>
    readonly ConcurrentDictionary<string, WorldEventStateData?> _states = new(StringComparer.Ordinal);
    /// <summary>
    /// The unique states and the player's faction a query reads (see <see cref="Refresh"/>).
    /// </summary>
    volatile Snapshot _current = new(new Dictionary<string, UniqueFact>(StringComparer.Ordinal), [], new Dictionary<string, int>(StringComparer.Ordinal), -1);

    /// <summary>
    /// The unique states (state and player flag by record id) and the player's faction, as taken by <see cref="Refresh"/>.
    /// </summary>
    sealed record Snapshot(IReadOnlyDictionary<string, UniqueFact> Uniques, FactionFact[] Players,
        IReadOnlyDictionary<string, int> FactionIndices, int LegacyPlayer);

    /// <summary>
    /// Takes the unique table's states and the player's faction (serial phase); cheap when nothing changed.
    /// </summary>
    public void Refresh(UniqueCharacters uniques, int playerFaction) => Capture(uniques, [playerFaction], playerFaction);

    /// <summary>
    /// The state's record, read once (null: not a WORLD_EVENT_STATE).
    /// </summary>
    public WorldEventStateData? Find(string id) =>
        _states.GetOrAdd(id, static (key, d) => d.Find(key) is { Type: FcsRecordType.WorldEventState } r ? WorldEventStateData.From(d, r) : null, db);

    /// <summary>
    /// Whether the state holds by the live answers (see the class remarks); false for an id that is no WORLD_EVENT_STATE.
    /// </summary>
    public bool IsTrue(string worldStateId)
    {
        if (Find(worldStateId) is not { } state) return false;
        var snapshot = _current; bool needsFaction = state.PlayerAlly.Count + state.PlayerEnemy.Count > 0
            || state.PlayerInvolvement && (state.NpcIs.Any(n => n.State != 1) || state.NpcIsNot.Any(n => n.State == 1));
        if (!needsFaction) return MatchesUniques(state, snapshot, -1);
        foreach (var faction in snapshot.Players)
            if (MatchesUniques(state, snapshot, faction.Index) && MatchesRelations(state, snapshot, faction))
                return true;
        return false;
    }

    /// <summary>
    /// Evaluates every player-dependent clause using this one persistent faction witness.
    /// </summary>
    public bool IsTrueForFaction(string worldStateId, int faction)
    {
        if (Find(worldStateId) is not { } state) return false;
        var snapshot = _current;
        var player = snapshot.Players.FirstOrDefault(p => p.Index == faction);
        return player is not null && MatchesUniques(state, snapshot, faction) && MatchesRelations(state, snapshot, player);
    }


    /// <summary>
    /// Player factions are persistent membership/ownership, not the currently connected clients.
    /// </summary>
    public void Refresh(UniqueCharacters uniques, IEnumerable<int> playerFactions, int legacyPlayerFaction = -1) =>
        Capture(uniques, playerFactions.Where(relations.Contains).Distinct().Order().ToArray(), legacyPlayerFaction);


    sealed record UniqueFact(UniqueState State, bool LegacyPlayer, IReadOnlySet<int> Factions);


    static bool MatchesRelations(WorldEventStateData state, Snapshot snapshot, FactionFact player)
    {
        if (state.PlayerAlly.Count + state.PlayerEnemy.Count > 0 && player.Relations.Length == 0) return false;
        foreach (var (id, expected) in state.PlayerAlly)
            if (snapshot.FactionIndices.TryGetValue(id, out int f) && (player.Relations[f] >= FactionRelations.AllyThreshold) != expected) return false;
        foreach (var (id, expected) in state.PlayerEnemy)
            if (snapshot.FactionIndices.TryGetValue(id, out int f) && (player.Relations[f] <= FactionRelations.HostileThreshold) != expected) return false;
        return true;
    }


    static bool MatchesUniques(WorldEventStateData state, Snapshot snapshot, int faction)
    {
        UniqueFact Of(string id) => snapshot.Uniques.TryGetValue(id, out var value) ? value : NeverMade;
        bool Involved(UniqueFact fact) => fact.Factions.Count > 0 ? fact.Factions.Contains(faction)
            : fact.LegacyPlayer && faction == snapshot.LegacyPlayer;
        foreach (var (id, wanted) in state.NpcIs)
        {
            var fact = Of(id);
            if ((int)fact.State != wanted || state.PlayerInvolvement && wanted != 1 && !Involved(fact)) return false;
        }
        foreach (var (id, unwanted) in state.NpcIsNot)
        {
            var fact = Of(id);
            if ((int)fact.State == unwanted || state.PlayerInvolvement && unwanted == 1 && !Involved(fact)) return false;
        }
        return true;
    }


    void Capture(UniqueCharacters uniques, int[] players, int legacyPlayer)
    {
        var copy = _current.Uniques;
        if (_uniqueVersion != uniques.Version)
        {
            var made = new Dictionary<string, UniqueFact>(uniques.Count, StringComparer.Ordinal);
            foreach (var (id, entry) in uniques.All)
                made[id] = new UniqueFact(entry.State, entry.Player, entry.InvolvedFactions.ToHashSet());
            copy = made;
            _uniqueVersion = uniques.Version;
        }
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int f = 0; f < relations.Count; f++) indices.TryAdd(relations.Factions[f].Id, f);
        var facts = new FactionFact[players.Length];
        for (int p = 0; p < players.Length; p++)
        {
            var values = relations.Contains(players[p]) ? new float[relations.Count] : [];
            for (int f = 0; f < values.Length; f++) values[f] = relations.Get(f, players[p]);
            facts[p] = new FactionFact(players[p], values);
        }
        _current = new Snapshot(copy, facts, indices, legacyPlayer);
    }

    long _uniqueVersion = -1;

    sealed record FactionFact(int Index, float[] Relations);

    static readonly UniqueFact NeverMade = new(UniqueState.Alive, false, new HashSet<int>());

}