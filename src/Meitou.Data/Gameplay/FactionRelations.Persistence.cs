namespace Meitou.Data.Gameplay;

/// <summary>Raw directed tables; no single-player row alias is applied while capturing or restoring.</summary>
public sealed record FactionDiplomacyState(int Version, string[] Factions, float[] Relations, bool[] Coexistence,
    bool[] Wars, int[] Ranks, float[] GlobalTrust, int PlayerFaction, long Changes);

public sealed partial class FactionRelations
{
    public FactionDiplomacyState Capture() => new(1, Factions.Select(f => f.Id).ToArray(),
        relation.ToArray(), coexist.ToArray(), war.ToArray(), rank.ToArray(), trust.ToArray(), PlayerFaction, Changes);

    public static void ValidateState(FactionDiplomacyState state, IReadOnlyList<string> identities)
    {
        int count = identities.Count;
        long cells = (long)count * count;
        if (state is null || state.Version != 1 || state.Factions is null || !state.Factions.SequenceEqual(identities, StringComparer.Ordinal) ||
            state.Relations is null || state.Coexistence is null || state.Wars is null || state.Ranks is null || state.GlobalTrust is null ||
            state.Relations.LongLength != cells || state.Coexistence.LongLength != cells || state.Wars.LongLength != cells ||
            state.Ranks.Length != count || state.GlobalTrust.Length != count || state.PlayerFaction < -1 || state.PlayerFaction >= count || state.Changes < 0 ||
            !state.Relations.All(float.IsFinite) || !state.GlobalTrust.All(float.IsFinite))
            throw new InvalidDataException("Invalid faction diplomacy state.");
        for (int i = 0; i < count; i++)
            if (state.Wars[i * count + i]) throw new InvalidDataException("A faction cannot be at war with itself.");
    }

    /// <summary>Exclusive world boundary. Catalogue restoration must precede table restoration.</summary>
    public void Restore(FactionDiplomacyState state)
    {
        ValidateState(state, Factions.Select(f => f.Id).ToArray());
        var nextRelations = state.Relations.ToArray();
        var nextCoexist = state.Coexistence.ToArray();
        var nextWar = state.Wars.ToArray();
        var nextRank = state.Ranks.ToArray();
        var nextTrust = state.GlobalTrust.ToArray();
        (relation, coexist, war, rank, trust) = (nextRelations, nextCoexist, nextWar, nextRank, nextTrust);
        PlayerFaction = state.PlayerFaction;
        Changes = state.Changes;
    }
}
