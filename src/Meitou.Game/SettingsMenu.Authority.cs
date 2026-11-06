using Meitou.Localisation;

namespace Meitou.Game;

sealed partial class SettingsMenu
{
    static bool AuthoritySetting(string label) => label is
        "Squad size multiplier" or "Global population multiplier" or "Town raid events: size" or "Town raid events: frequency" or
        "Fast zone hopping" or "Turret gunners turn" or "Town attacks frequency" or "Dismemberment" or "Auto save" or "Auto save time (minutes)" or
        "Interior lamps" or "Attackers per target" or "Harpoon limit" or "Late dodges and blocks" or
        "Squads keep their people" or "Fights far away" or "Healing far away" or "Wild squads stay" or
        "Towns keep their schedule" or "Production far away" or "Doors close behind" or "Decapitation" or
        "Advanced ragdolls" or "luckyren Mode" or "Dropped items kept" or "Raids as written" or "Weak punch reach" or "Bench facing";

    Entry AuthorityEntry(Entry entry)
    {
        if (!AuthoritySetting(entry.Label)) return entry;
        string value = player.AuthorityRuleValue(entry.Label) ?? Loc.Tr("Unavailable");
        return entry with { Hint = entry.Hint + "\n" + Loc.Tr("Server") + ": " + value + ". " + Loc.Tr("Controlled by the server."),
            Slider = null, Toggle = null, Choice = null, Run = null, Reset = null, Changed = false, Locked = true };
    }
}
