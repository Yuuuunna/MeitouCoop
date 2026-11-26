using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Player;
using static Meitou.Tests.Combat.CombatFixtures;

namespace Meitou.Tests.Combat;

/// <summary>
/// Combat callers distinguish the stat getter, raw weapon skills and scaled ratings (docs/game/character-stats.md "The stat getter").
/// </summary>
public class StatGetterCombatTests
{
    /// <summary>
    /// The damage caller must not scale a person's weapon skill or apply the gear's damage and dexterity factors a second time.
    /// </summary>
    [Theory]
    [InlineData(false, 80)]
    [InlineData(true, 30)]
    public void Armed_damage_reads_raw_weapon_skill_and_modified_dexterity_once(bool animal, float weaponSkill)
    {
        using var world = DuelFixture.NewWorld(42, 1, out var combat);
        var gear = BodyPlate with { DexterityMult = 0.5f, DamageOutputMult = 0.8f };
        var cold = new Combatant { WeaponSkill = 80, Martial = 60, Dexterity = 60, Strength = 50 }.MakeCold();
        cold.Stats!.Scale = 0.5f;
        cold.Fighter = new Fighter(KatanaAt(0.5f), [ArmourPiece.Create(gear, 0.5f)]) { IsAnimal = animal };
        cold.Medical!.Light = 0;
        cold.Medical.DustStorm(1, 0);
        var id = world.Characters.Spawn(new CharacterHot { Health = 100 }, cold, 0);

        Assert.Equal(weaponSkill, CombatSystem.WeaponSkillLevel(cold, cold.Fighter), 4);
        Assert.Equal(15f, CombatSystem.Eff(cold, StatsEnumerated.Dexterity), 4);

        // Independently chosen packet inputs: dex = 60 x age 0.5 x gear 0.5; strength = 50 x age 0.5.
        float expected = DamageFormulas.Packet(weaponSkill, 15, 25, cold.Fighter.Weapon!, TargetKind.None, null, 0.8f, Constants).Sum;
        Assert.Equal(expected, combat.HudDamage(world, id.Slot, cold), 4);
    }

    /// <summary>
    /// Ranged skills and perception stay unscaled; the perception bonus participates in the same dust multiplier as its trained level.
    /// </summary>
    [Fact]
    public void Ranged_getter_keeps_skill_scale_separate_from_body_scale_and_perception_bonus()
    {
        var gear = BodyPlate with { DexterityMult = 0.5f, PerceptionBonus = 10, RangedSkillMult = 0.5f };
        var cold = new Combatant { Dexterity = 60, Armour = [gear] }.MakeCold();
        cold.Stats!.Scale = 0.25f;
        cold.Stats[StatsEnumerated.Crossbows] = 60;
        cold.Stats[StatsEnumerated.Perception] = 40;

        Assert.Equal(30f, CombatSystem.Eff(cold, StatsEnumerated.Crossbows), 4);
        Assert.Equal(50f, CombatSystem.Eff(cold, StatsEnumerated.Perception), 4);
        Assert.Equal(7.5f, CombatSystem.Eff(cold, StatsEnumerated.Dexterity), 4);

        cold.Medical!.DustStorm(1, 0);
        Assert.Equal(33.25f, CombatSystem.Eff(cold, StatsEnumerated.Perception), 4);
        Assert.Equal(30f, CombatSystem.Eff(cold, StatsEnumerated.Crossbows), 4);
        Assert.Equal(7.5f, CombatSystem.Eff(cold, StatsEnumerated.Dexterity), 4);
        Assert.Equal(40f, StatGetter.Get(cold, StatsEnumerated.Perception, unmodified: true), 4);
    }

    /// <summary>
    /// The whole unarmed rating, including worn bonuses and the weather penalty, takes the stats scale before damage and contests use it.
    /// </summary>
    [Fact]
    public void Unarmed_attack_and_defence_scale_the_complete_rating()
    {
        using var world = DuelFixture.NewWorld(42, 1, out _);
        var cold = new Combatant { Weapon = null, Martial = 60, Armour = [BodyPlate with { UnarmedBonus = 10 }] }.MakeCold();
        cold.Stats!.Scale = 0.5f;
        cold.Fighter!.EncumbranceFactor = 0.5f;
        cold.Medical!.DustStorm(0.6f, 0);
        var id = world.Characters.Spawn(new CharacterHot { Health = 100 }, cold, 0);

        // Load contributes 0.6, then (60 + 10 - 6) and the age scale 0.5: 19.2 for both contests.
        Assert.Equal(19.2f, CombatSystem.AttackSkill(world, id.Slot)!.Value, 4);
        Assert.Equal(19.2f, CombatSystem.MeleeDefence(world, id.Slot)!.Value, 4);
    }

    /// <summary>
    /// Dodge's gear and encumbrance terms use the stored dodge stat, and only unarmed guard adds its twenty-point bonus.
    /// </summary>
    [Theory]
    [InlineData(false, 20)]
    [InlineData(true, 40)]
    public void Dodge_uses_raw_stat_and_applies_the_unarmed_guard_term(bool guard, float expected)
    {
        using var world = DuelFixture.NewWorld(42, 1, out _);
        var cold = new Combatant { Weapon = null, Dodge = 80, Armour = [BodyPlate with { DodgeMult = 0.5f }] }.MakeCold();
        cold.Stats!.Scale = 0.25f;
        cold.Fighter!.EncumbranceFactor = 0.75f;
        cold.Fighter.Guarding = true;
        var id = world.Characters.Spawn(new CharacterHot { Health = 100 }, cold, 0);

        Assert.Equal(expected, CombatSystem.EffectiveDodge(world, id.Slot, guard)!.Value, 4);
    }

    /// <summary>
    /// Age changes strength and dexterity, but a healthy character's same stealth level and equipment must keep the same sneak term.
    /// </summary>
    [Fact]
    public void Stealth_getter_and_sneak_term_do_not_multiply_the_stored_level_by_age()
    {
        var cold = new Combatant { Weapon = null }.MakeCold();
        cold.Stats![StatsEnumerated.Stealth] = 40;
        Assert.Equal(0.48f, Orders.Stealth(cold), 4);

        cold.Stats.Scale = 0.25f;
        Assert.Equal(0.48f, Orders.Stealth(cold), 4);
        Assert.Equal(48f, StatGetter.Get(cold, StatsEnumerated.Stealth, unmodified: false), 4);
    }
}
