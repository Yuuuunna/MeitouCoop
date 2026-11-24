using Meitou.Content;
using Meitou.Localisation;

namespace Meitou.Modding;

/// <summary>
/// Mods whose feature Meitou has built in (docs/formats/rekenshi.md). When one is enabled, Meitou takes its settings into its own once
/// (<see cref="BuiltInModSettings.AdoptedMods"/>) and says so; the caller persists those settings. A code mod among them is not run, so the feature never applies twice, and a save made while it
/// ran does not report it gone.
/// </summary>
public static class BuiltInMods
{
    /// <summary>
    /// A mod Meitou covers (its entry in <see cref="CoveredMods.BuiltIn"/>), what the notice says became of its settings, and how they are taken in
    /// (from the mod and the mods on disk by name). <see cref="Covered.Again"/>: what a later Meitou takes in from a mod already taken in, once (a feature
    /// grown since: the XP mods' per-skill settings), and <see cref="Covered.AgainTaken"/> its notice.
    /// </summary>
    sealed record Covered(BuiltInMod Mod, string Taken, Action<BuiltInModSettings, ModInfo, IReadOnlyDictionary<string, ModInfo>> Adopt,
        Action<BuiltInModSettings, ModInfo, IReadOnlyDictionary<string, ModInfo>>? Again = null, string AgainTaken = "");

    /// <summary>
    /// What marks a mod's second takeover (<see cref="Covered.Again"/>) in <see cref="BuiltInModSettings.AdoptedMods"/>, after its name.
    /// </summary>
    const string AgainMark = " (again)";

    /// <summary>
    /// What the XP mods' notice says became of their settings.
    /// </summary>
    const string XpTaken = "its multipliers are now Squad XP in the Advanced settings (open it for each skill's own) and Show Squad XP";

    /// <summary>
    /// What the XP mods' second notice says (their settings were taken in before Squad XP had a multiplier per skill).
    /// </summary>
    const string XpTakenAgain = "its per-skill multipliers are now under Squad XP in the Advanced settings, and its XP label is Show Squad XP";

    /// <summary>
    /// The mods Meitou covers.
    /// </summary>
    static readonly Covered[] All =
    [
        new(CoveredMods.RekenshiXp, XpTaken, (config, mod, _) => XpModSettings.AdoptPlugin(config, mod.Folder),
            (config, mod, _) => XpModSettings.AdoptPlugin(config, mod.Folder, main: false), XpTakenAgain),
        // The port uses the plugin's file over its own saved settings when the plugin is installed.
        new(CoveredMods.XpPort, XpTaken, (config, _, onDisk) => XpModSettings.AdoptPort(config, onDisk),
            (config, _, onDisk) => XpModSettings.AdoptPort(config, onDisk, main: false), XpTakenAgain),
        new(CoveredMods.SwampTurtleFix, "Swamp turtle attacks on the Fixes page is on", (config, _, _) => config.FixAnimalMoves = true),
        new(CoveredMods.CharacterHighlight, "Hover outline in the Advanced settings is on", (config, _, _) => config.HoverOutline = true),
    ];

    /// <summary>
    /// Takes in the settings of each enabled covered mod not taken in before and returns the notices saying so; the caller saves the config when a notice was returned.
    /// A mod folder that cannot be read takes nothing in.
    /// </summary>
    public static List<string> Adopt(BuiltInModSettings config, GameInstall install)
    {
        List<string> notices = [];
        var onDisk = ModsOnDisk(install);
        foreach (var mod in ModList.ReadEnabled(install).Select(onDisk.GetValueOrDefault).OfType<ModInfo>())
        {
            if (CoveredOf(mod) is not { } covered || AdoptOnce(config, covered, mod, onDisk) is not { } taken) continue;

            Console.WriteLine($"mods      {mod.Name} is built into Meitou: {taken}");
            notices.Add(Loc.Tr("{1} is built into Meitou now: {2}.", mod.Name, Loc.Tr(taken)));
        }

        return notices;
    }

    /// <summary>
    /// Takes in <paramref name="mod"/>'s settings if they were not taken in before, else what <see cref="Covered.Again"/> takes if that was not;
    /// marks it in <see cref="BuiltInModSettings.AdoptedMods"/> and returns the notice's text, or null when there was nothing to take.
    /// </summary>
    static string? AdoptOnce(BuiltInModSettings config, Covered covered, ModInfo mod, IReadOnlyDictionary<string, ModInfo> onDisk)
    {
        string again = mod.Name + AgainMark;
        if (!config.AdoptedMods.Contains(mod.Name))
        {
            covered.Adopt(config, mod, onDisk);
            config.AdoptedMods.Add(mod.Name);
            if (covered.Again is not null) config.AdoptedMods.Add(again);
            return covered.Taken;
        }

        if (covered.Again is not { } adoptAgain || config.AdoptedMods.Contains(again)) return null;

        adoptAgain(config, mod, onDisk);
        config.AdoptedMods.Add(again);
        return covered.AgainTaken;
    }

    /// <summary>
    /// Moves the covered code mods of <paramref name="enabled"/> to <paramref name="disabled"/>, each with a log line, so they are not run and the
    /// dependency checks count them as switched off.
    /// </summary>
    public static void SkipCovered(List<ModInfo> enabled, List<ModInfo> disabled)
    {
        foreach (var mod in enabled.Where(IsCovered).ToList())
        {
            Console.WriteLine($"mods      {mod.Name} not run: built into Meitou");
            enabled.Remove(mod);
            disabled.Add(mod);
        }
    }

    /// <summary>
    /// Whether <see cref="SkipCovered"/> leaves <paramref name="mod"/> out (a code mod whose feature Meitou has built in); no log line.
    /// </summary>
    public static bool IsCovered(ModInfo mod) => CoveredOf(mod) is { Mod.CodeId: not null };

    /// <summary>
    /// <paramref name="saved"/> without the covered code mods, so a save made while one ran does not report it gone.
    /// </summary>
    public static CodeModEnvironment WithoutCovered(CodeModEnvironment saved) =>
        saved with { Mods = [.. saved.Mods.Where(m => !All.Any(c => c.Mod.CodeId == m.Id))] };

    /// <summary>
    /// The mods on disk by name; none when the folders cannot be read.
    /// </summary>
    static Dictionary<string, ModInfo> ModsOnDisk(GameInstall install)
    {
        try
        {
            return ModList.Discover(install).ToDictionary(m => m.Name, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"mods      the mod folders could not be read, no built-in mods taken in: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// The entry covering <paramref name="mod"/> (<see cref="CoveredMods.BuiltInOf"/>); null when Meitou does not cover it.
    /// </summary>
    static Covered? CoveredOf(ModInfo mod) => CoveredMods.BuiltInOf(mod) is { } built ? All.FirstOrDefault(c => c.Mod == built) : null;
}

/// <summary>
/// Settings shared by the graphical and headless hosts when adopting a feature supplied by an older mod.
/// </summary>
public sealed class BuiltInModSettings
{
    /// <summary>
    /// Supported experience range, shared with both hosts' settings validation.
    /// </summary>
    public const float MinXpRate = 0, MaxXpRate = 1000;

    /// <summary>
    /// Player faction experience rate, updated only for a mod not adopted before.
    /// </summary>
    public float SquadXp { get; set; } = 1;

    /// <summary>
    /// Per-skill experience overrides keyed by the original STATS field name.
    /// </summary>
    public Dictionary<string, float> SquadSkillXp { get; set; } = [];

    /// <summary>
    /// Client presentation preference adopted from the legacy XP indicator.
    /// </summary>
    public bool ShowSquadXp { get; set; }

    /// <summary>
    /// Client-only hover outline, enabled when the graphical host adopts the character-highlight mod.
    /// </summary>
    public bool HoverOutline { get; set; }

    /// <summary>The built-in animal move eligibility fix, shared with authoritative gameplay rules.</summary>
    public bool FixAnimalMoves { get; set; } = true;

    /// <summary>
    /// Persisted mod names whose settings have already been adopted.
    /// </summary>
    public List<string> AdoptedMods { get; init; } = [];

    /// <summary>
    /// Saved settings of the earlier code mods, read without executing their code.
    /// </summary>
    public IReadOnlyDictionary<string, Dictionary<string, string>> ModSettings { get; init; } =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
}
