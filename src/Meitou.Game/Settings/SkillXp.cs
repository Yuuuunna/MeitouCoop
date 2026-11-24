using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Game.Settings;

/// <summary>
/// The skills' own Squad XP multipliers (Advanced settings, under "Squad XP"; <see cref="Host.UserConfig.SquadSkillXp"/>): which skills have one,
/// how the config keeps them and the list the simulation reads.
/// </summary>
static class SkillXp
{
    /// <summary>
    /// The skills with a slider, in the skills window's order: the label, the stat, the setting the Player-Only XP port saved it under and the
    /// plugin's key in its settings file (<c>stat_&lt;key&gt;</c>).
    /// </summary>
    internal static readonly (string Label, StatsEnumerated Stat, string PortSetting, string PluginKey)[] Skills = Meitou.Modding.XpSkills.Skills;

    /// <summary>
    /// The lowest slider value, one arrow-key step below 0: below 0 a skill follows Squad XP.
    /// </summary>
    internal const float Follows = -0.25f;

    /// <summary>
    /// The config's key of a skill: its STATS field name (<c>strength</c>, <c>attack</c>, <c>ff</c>...).
    /// </summary>
    internal static string Key(StatsEnumerated stat) => StatsInfo.FieldName(stat)!;

    /// <summary>
    /// A skill's row id in the settings menu, a member of the Squad XP group (<see cref="FeatureGrouping.FeatureGroups"/>).
    /// </summary>
    internal static string Raw(StatsEnumerated stat) => "xp:" + Key(stat);

    /// <summary>
    /// The rows of the skills' sliders, the Squad XP group's members.
    /// </summary>
    internal static string[] Rows => [.. Skills.Select(s => Raw(s.Stat))];

    /// <summary>
    /// <paramref name="saved"/> without what no skill reads: unknown keys, numbers that are not finite and skills that follow Squad XP (below 0);
    /// the rest held to the XP sliders' range.
    /// </summary>
    internal static Dictionary<string, float> Kept(Dictionary<string, float>? saved) => Meitou.Modding.XpSkills.Kept(saved);

    /// <summary>
    /// The Squad XP label during play ("XP x10", <see cref="PlayerInterface.XpLabel"/>); null while Show Squad XP is off.
    /// </summary>
    internal static string? Label(Host.UserConfig config, Simulation.GameplayRules? authority = null) =>
        config.ShowSquadXp ? "XP x" + (authority?.SquadXp ?? config.SquadXp).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// The skills' multipliers as the simulation reads them (<see cref="Simulation.Bodies.BodyOptions.SquadSkillXp"/>): by stat number, each
    /// skill's own or <see cref="Follows"/>.
    /// </summary>
    internal static float[] Rates(IReadOnlyDictionary<string, float> saved) => Meitou.Modding.XpSkills.Rates(saved);
}
