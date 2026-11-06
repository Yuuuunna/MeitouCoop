using System.Globalization;
using Meitou.Localisation;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    internal string? AuthorityRuleValue(string label)
    {
        if (session.AuthorityRules is not { } rules) return null;
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
            "Towns keep their schedule" => rules.ResidentSchedule,
            "Production far away" => rules.UnloadedProduction,
            "Doors close behind" => rules.DoorsCloseBehind,
            "Decapitation" => rules.Decapitation,
            "Advanced ragdolls" => rules.AdvancedRagdolls,
            "luckyren Mode" => rules.Luckyren,
            "Dropped items kept" => rules.GroundItemLimit,
            "Raids as written" => rules.CampaignAncestorsOnce,
            "Weak punch reach" => rules.PunchReachFix,
            "Bench facing" => rules.BenchFacingFix,
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
