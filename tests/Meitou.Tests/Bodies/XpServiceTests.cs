using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Tests.Bodies;

/// <summary>
/// Combat XP routing and the defensive event's skill and race factors.
/// </summary>
public class XpServiceTests
{
    /// <summary>
    /// Hits train the defensive skill for the defender's current equipment, retaining strength and toughness gains.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hit_taken_trains_dodge_when_unarmed_and_melee_defence_when_armed(bool unarmed)
    {
        var stats = new CharacterStats();
        var race = new RaceData();
        var xp = new XpService(GameConstants.Default, BodyOptions.Default);

        xp.Combat(stats, race, XpEvent.HitTaken, unarmed, StatsEnumerated.None, 1, 1, damageTaken: 1);

        var trained = unarmed ? StatsEnumerated.Dodge : StatsEnumerated.MeleeDefence;
        var untouched = unarmed ? StatsEnumerated.MeleeDefence : StatsEnumerated.Dodge;
        Assert.Equal(CharacterStats.CoreGain(1, xp.EventUnit * 2), stats.Stored(trained));
        Assert.Equal(1, stats.Stored(untouched));
        Assert.Equal(CharacterStats.CoreGain(1, 0.075f * 0.55f * 2 * GameConstants.Default.XpRateStrength * 0.1f), stats.Strength);
        Assert.Equal(CharacterStats.CoreGain(1, xp.EventUnit * GameConstants.Default.XpRateToughness), stats.Toughness);
        Assert.Equal(1, stats.Stored(StatsEnumerated.MeleeAttack));
        Assert.Equal(1, stats.Stored(StatsEnumerated.MartialArts));
        Assert.Equal(1, stats.Dexterity);
    }

    /// <summary>
    /// The selected defensive skill supplies its stored level and racial XP factor; temporary modifiers are not trained in.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hit_taken_uses_the_selected_skills_level_and_race_multiplier(bool unarmed)
    {
        var stats = new CharacterStats();
        stats[StatsEnumerated.Dodge] = 10;
        stats[StatsEnumerated.MeleeDefence] = 20;
        stats.Modifiers.Set("perk", StatsEnumerated.Dodge, 15);
        stats.Modifiers.Set("perk", StatsEnumerated.MeleeDefence, -5);
        var race = new RaceData();
        race.StatMultipliers[(int)StatsEnumerated.Dodge] = 1.2f;
        race.StatMultipliers[(int)StatsEnumerated.MeleeDefence] = 0.8f;
        var options = new BodyOptions { GlobalDamageMultiplier = 1.5f };
        var xp = new XpService(GameConstants.Default, options);

        xp.Combat(stats, race, XpEvent.HitTaken, unarmed, StatsEnumerated.None, 20, 0);

        // Dodge 10 against attack 20 gives a 2x skill difference; defence 20 against attack 20 gives 1x.
        float expectedDodge = unarmed ? CharacterStats.CoreGain(10, xp.EventUnit * 1.5f * 2 * 2 * 1.2f) : 10;
        float expectedDefence = unarmed ? 20 : CharacterStats.CoreGain(20, xp.EventUnit * 1.5f * 2 * 0.8f);
        Assert.Equal(expectedDodge, stats.Stored(StatsEnumerated.Dodge));
        Assert.Equal(expectedDefence, stats.Stored(StatsEnumerated.MeleeDefence));
    }

    /// <summary>
    /// A defended event keeps its melee defence calculation even for an unarmed fighter that a mod allows to block.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Defended_keeps_melee_defence_and_its_race_factor_inside_the_skill_difference(bool unarmed)
    {
        var stats = new CharacterStats();
        stats[StatsEnumerated.MeleeDefence] = 10;
        var race = new RaceData();
        race.StatMultipliers[(int)StatsEnumerated.MeleeDefence] = 1.2f;
        var xp = new XpService(GameConstants.Default, BodyOptions.Default);

        xp.Combat(stats, race, XpEvent.Defended, unarmed, StatsEnumerated.None, 12, 0);

        Assert.Equal(CharacterStats.CoreGain(10, xp.EventUnit * 0.25f), stats.Stored(StatsEnumerated.MeleeDefence));
        Assert.Equal(1, stats.Stored(StatsEnumerated.Dodge));
    }
}
