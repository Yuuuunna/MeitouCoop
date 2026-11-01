using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation;

/// <summary>
/// One global answer to WORLD_EVENT_STATE queries. Original single-player meanings are in docs/game/factions-squads-towns.md.
/// Multiplayer evaluates every player-dependent condition against the same faction witness; it never combines different players' deeds or diplomacy.
/// Worker queries read an immutable snapshot, including diplomacy, prepared at the population boundary.
/// </summary>
public sealed class WorldStates(GameDatabase db, FactionRelations relations) : IWorldStates
{
    readonly ConcurrentDictionary<string, WorldEventStateData?> states = new(StringComparer.Ordinal);
    volatile Snapshot current = new(new Dictionary<string, UniqueFact>(StringComparer.Ordinal), [], new Dictionary<string, int>(StringComparer.Ordinal), -1);
    long uniqueVersion = -1;

    sealed record UniqueFact(UniqueState State, bool LegacyPlayer, IReadOnlySet<int> Factions);
    static readonly UniqueFact NeverMade = new(UniqueState.Alive, false, new HashSet<int>());
    sealed record FactionFact(int Index, float[] Relations);
    sealed record Snapshot(IReadOnlyDictionary<string, UniqueFact> Uniques, FactionFact[] Players,
        IReadOnlyDictionary<string, int> FactionIndices, int LegacyPlayer);

    public void Refresh(UniqueCharacters uniques, int playerFaction) => Capture(uniques, [playerFaction], playerFaction);

    /// <summary>Player factions are persistent membership/ownership, not the currently connected clients.</summary>
    public void Refresh(UniqueCharacters uniques, IEnumerable<int> playerFactions, int legacyPlayerFaction = -1) =>
        Capture(uniques, playerFactions.Where(relations.Contains).Distinct().Order().ToArray(), legacyPlayerFaction);

    void Capture(UniqueCharacters uniques, int[] players, int legacyPlayer)
    {
        var copy = current.Uniques;
        if (uniqueVersion != uniques.Version)
        {
            var made = new Dictionary<string, UniqueFact>(uniques.Count, StringComparer.Ordinal);
            foreach (var (id, entry) in uniques.All)
                made[id] = new UniqueFact(entry.State, entry.Player, entry.InvolvedFactions.ToHashSet());
            copy = made;
            uniqueVersion = uniques.Version;
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
        current = new Snapshot(copy, facts, indices, legacyPlayer);
    }

    public WorldEventStateData? Find(string id) =>
        states.GetOrAdd(id, static (key, d) => d.Find(key) is { Type: FcsRecordType.WORLD_EVENT_STATE } r ? WorldEventStateData.From(d, r) : null, db);

    public bool IsTrue(string worldStateId)
    {
        if (Find(worldStateId) is not { } state) return false;
        var snapshot = current;
        bool needsFaction = state.PlayerAlly.Count + state.PlayerEnemy.Count > 0
            || state.PlayerInvolvement && (state.NpcIs.Any(n => n.State != 1) || state.NpcIsNot.Any(n => n.State == 1));
        if (!needsFaction) return MatchesUniques(state, snapshot, -1);
        foreach (var faction in snapshot.Players)
            if (MatchesUniques(state, snapshot, faction.Index) && MatchesRelations(state, snapshot, faction)) return true;
        return false;
    }

    /// <summary>Evaluates every player-dependent clause using this one persistent faction witness.</summary>
    public bool IsTrueForFaction(string worldStateId, int faction)
    {
        if (Find(worldStateId) is not { } state) return false;
        var snapshot = current;
        var player = snapshot.Players.FirstOrDefault(p => p.Index == faction);
        return player is not null && MatchesUniques(state, snapshot, faction) && MatchesRelations(state, snapshot, player);
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

    static bool MatchesRelations(WorldEventStateData state, Snapshot snapshot, FactionFact player)
    {
        if (state.PlayerAlly.Count + state.PlayerEnemy.Count > 0 && player.Relations.Length == 0) return false;
        foreach (var (id, expected) in state.PlayerAlly)
            if (snapshot.FactionIndices.TryGetValue(id, out int f) && (player.Relations[f] >= FactionRelations.AllyThreshold) != expected) return false;
        foreach (var (id, expected) in state.PlayerEnemy)
            if (snapshot.FactionIndices.TryGetValue(id, out int f) && (player.Relations[f] <= FactionRelations.HostileThreshold) != expected) return false;
        return true;
    }
}
