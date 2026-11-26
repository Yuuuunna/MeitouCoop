using System.Globalization;
using Meitou.Game.Host;
using Meitou.Game.Settings;
using Meitou.Localisation;

namespace Meitou.Game.Ui;

/// <summary>
/// Formats server-owned settings for the read-only remote settings rows.
/// </summary>
static class ServerRuleValues
{
    /// <summary>
    /// The authoritative setting value, or null when the session does not disclose it.
    /// </summary>
    internal static string? Value(IPlayerSession session, string label)
    {
        if (label == "Fast speed") return session.TimeScales[1].ToString("0.##", CultureInfo.InvariantCulture);
        if (label == "Fastest speed") return session.TimeScales[2].ToString("0.##", CultureInfo.InvariantCulture);
        if (label == "Max faction size") return session.CurrentSnapshot.MaxFactionSize.ToString(CultureInfo.InvariantCulture);
        if (label == "Max squad size") return session.CurrentSnapshot.MaxSquadSize.ToString(CultureInfo.InvariantCulture);
        if (label == "Max squads") return session.CurrentSnapshot.MaxSquads.ToString(CultureInfo.InvariantCulture);
        if (session.AuthorityRules is not { } rules) return null;
        foreach (var (skill, stat, _, _) in SkillXp.Skills)
        {
            if (label != "Squad XP " + skill) continue;
            float rate = (int)stat < rules.SquadSkillXp.Length ? rules.SquadSkillXp[(int)stat] : SkillXp.Follows;
            return rate >= 0 ? rate.ToString("0.##", CultureInfo.InvariantCulture) : rules.SquadXp.ToString("0.##", CultureInfo.InvariantCulture);
        }
        object? value = label switch
        {
            "Squad size multiplier" => rules.SquadSize,
            "Global population multiplier" => rules.Population,
            "Town raid events: size" => rules.RaidSize,
            "Town raid events: frequency" => rules.RaidFrequency,
            "Town attacks frequency" => GameplaySettings.TownAttackLabels[Math.Clamp(rules.TownAttacks, 0, GameplaySettings.TownAttackLabels.Length - 1)],
            "Dismemberment" => rules.Dismemberment.ToString(),
            "Auto save" => rules.AutoSaveSeconds > 0,
            "Auto save time (minutes)" => rules.AutoSaveSeconds / 60f,
            "Fast zone hopping" => rules.FastZoneHopping,
            "Interior lamps" => rules.InteriorLampsAllDay,
            "Attackers per target" => rules.AttackSlots ?? UserConfig.FaithfulAttackSlots,
            "Harpoon limit" => rules.HarpoonLimit,
            "Late dodges and blocks" => rules.LateDefences,
            "Squads keep their people" => rules.KeepRoamers,
            "Fights far away" => rules.OffscreenFights,
            "Healing far away" => rules.OffscreenHealing,
            "Wild squads stay" => rules.KeepWild,
            "Knocked out stay" => rules.KeepDowned,
            "Towns keep their schedule" => rules.ResidentSchedule,
            "Towns change far away" => rules.OffscreenOverrides,
            "Production far away" => rules.UnloadedProduction,
            "Guards spare their own" => rules.CagesSpareOwn,
            "Decapitation" => rules.Decapitation,
            "Advanced ragdolls" => rules.AdvancedRagdolls,
            "luckyren Mode" => rules.Luckyren,
            "Dropped items kept" => rules.GroundItemLimit,
            "Raids as written" => rules.CampaignAncestorsOnce,
            "Weak punch reach" => rules.PunchReachFix,
            "Swamp turtle attacks" => rules.AnimalMovesFix,
            "Prisoners hold still" => rules.PrisonPoseFix,
            "Caged animals idle" => rules.CagedAnimalFix,
            "Raids keep their progress" => rules.KeepCampaignClocks,
            "Guards stop passing bodies" => rules.ArrestLiftFix,
            "Holes and needles in the ground" => rules.GroundRepair,
            "Bench facing" => rules.BenchFacingFix,
            "Beds used as seats" => rules.SeatBedFix,
            "Pick-up thefts from the owner" => rules.PickUpTheftFix,
            "Guards give up lost suspects" => rules.ArrestLostFix,
            "Bags in the inventory" => rules.BagSearchFix,
            "Talk across shop counters" => rules.CounterTalk,
            "Turret fire is a crime" => rules.TurretAssault,
            "Squad XP" => rules.SquadXp,
            "Others' XP" => rules.OthersXp,
            "Turret gunners turn" => rules.GunnerFacesBarrel,
            _ => null,
        };
        return value switch
        {
            bool enabled => Loc.Tr(enabled ? "On" : "Off"),
            float number => number.ToString("0.##", CultureInfo.InvariantCulture),
            string text => Loc.Tr(text),
            IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}
