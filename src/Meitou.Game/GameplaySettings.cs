using Meitou.Content;
using Meitou.Simulation.Bodies;

namespace Meitou.Game;

/// <summary>
/// The original's Gameplay options page (docs/formats/settings.md "Gameplay"): the population and difficulty sliders, the two drop-downs and the autosave, with
/// the game's slider ranges and missing-key defaults. All apply to the running world when changed.
/// </summary>
sealed record GameplaySettings(float SquadSize = 1, float Population = 1, float RaidSize = 1, float RaidFrequency = 1, int TownAttacks = 2,
    Dismemberment Dismemberment = Dismemberment.Rare, bool AutoSave = true, float AutoSaveMinutes = 10)
{
    public const float MinSquadSize = 0.5f, MaxSquadSize = 3, MinPopulation = 0.5f, MaxPopulation = 4,
        MinRaidSize = 0.25f, MaxRaidSize = 4, MinRaidFrequency = 0.25f, MaxRaidFrequency = 3, MinAutoSaveMinutes = 1, MaxAutoSaveMinutes = 30;

    /// <summary>The original NPC rendering range; values below 1000 use its 3000-unit fallback.</summary>
    public static float NpcRange(GameInstall install)
    {
        float range = Meitou.Audio.AudioSystem.Setting(install, "npc range", 3250);
        return range < 1000 ? 3000 : range;
    }

    /// <summary>The drop-downs' items, by the stored number.</summary>
    public static readonly string[] TownAttackLabels = ["Bombardment", "High", "Normal", "Fewer", "Rare", "Never"];
    public static readonly string[] DismembermentLabels = ["Never", "Rare", "Frequent"];

    /// <summary>The install's <c>settings.cfg</c>; every missing key takes the original's default.</summary>
    public static GameplaySettings FromInstall(GameInstall install)
    {
        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = Path.Combine(install.Root, "settings.cfg");
            if (File.Exists(file))
                foreach (var line in File.ReadLines(file))
                    if (line.IndexOf('=') is > 0 and var eq && float.TryParse(line[(eq + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) && float.IsFinite(v))
                        values[line[..eq].Trim()] = v;
        }
        catch (IOException) { }
        float Get(string key, float fallback, float min, float max) => values.TryGetValue(key, out float v) ? Math.Clamp(v, min, max) : fallback;
        return new(
            SquadSize: Get("Squad size multiplier", 1, MinSquadSize, MaxSquadSize),
            Population: Get("Global population multiplier", 1, MinPopulation, MaxPopulation),
            RaidSize: Get("raidSizeMult", 1, MinRaidSize, MaxRaidSize),
            RaidFrequency: Get("raidFrequencyMult", 1, MinRaidFrequency, MaxRaidFrequency),
            TownAttacks: (int)Get("attacks", 2, 0, 5),
            Dismemberment: (Dismemberment)(int)Get("Dismemberment", 1, 0, 2),
            AutoSave: Get("Auto save", 1, 0, 1) != 0,
            AutoSaveMinutes: Get("Auto save time (minutes)", 10, MinAutoSaveMinutes, MaxAutoSaveMinutes));
    }
}
