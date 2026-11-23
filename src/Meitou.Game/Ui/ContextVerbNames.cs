namespace Meitou.Game.Ui;

/// <summary>
/// Original context-menu captions and their order numbers, shared with code-mod registration validation.
/// </summary>
static class ContextVerbNames
{
    /// <summary>
    /// Original order numbers keyed by the untranslated verb caption.
    /// </summary>
    internal static readonly Dictionary<string, int> Ordered = new()
    {
        ["Build"] = 2,
        ["Steal"] = 3,
        ["Attack Target"] = 5,
        ["Talk To"] = 12,
        ["Attack All"] = 16,
        ["First Aid"] = 25,
        ["Inventory"] = 26,
        ["Trade"] = 26,
        ["Loot"] = 26,
        ["Stand Up"] = 28,
        ["Follow"] = 31,
        ["Bodyguard"] = 45,
        ["Repairs"] = 60,
        ["Attack Unprovoked"] = 61,
        ["Put Down"] = 69,
        ["Open"] = 72,
        ["Close"] = 73,
        ["Pick Lock"] = 76,
        ["Lock"] = 77,
        ["Unlock"] = 78,
        ["Use"] = 87,
        ["Repair"] = 95,
        ["Dismantle"] = 96,
        ["Training"] = 97,
        ["Put in Bed"] = 99,
        ["Get in"] = 107,
        ["Put in"] = 108,
        ["Knock Out"] = 109,
        ["Set Free"] = 110,
        ["Break Out"] = 111,
        ["Auto-Haul"] = 124,
        ["Corpse Disposal"] = 136,
        ["Man Turret"] = 146,
        ["Capture"] = 166,
        ["Use Tools"] = 185,
        ["Use Strength"] = 186,
        ["Unlock Shackles"] = 201,
        ["Escape"] = 207,
        ["Throw out of home"] = 223,
        ["Pick Up"] = 225,
        ["Smash"] = 226,
        ["Stealth KO"] = 228,
        ["Stealth Kill"] = 229,
        ["Shoot At"] = 235,
        ["Kidnap"] = 246,
        ["Splint Injuries"] = 249,
        ["Escape (skill)"] = 251,
        ["Escape (strength)"] = 252,
        ["Sleep"] = 258,
        ["Throw out of town"] = 273,
    };
}
