namespace Meitou.Data.Gameplay;

/// <summary>Raw directed tables; no single-player row alias is applied while capturing or restoring.</summary>
public sealed record FactionDiplomacyState(int Version, string[] Factions, float[] Relations, bool[] Coexistence,
    bool[] Wars, int[] Ranks, float[] GlobalTrust, int PlayerFaction, long Changes);
