using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Modding;

/// <summary>
/// Skill identities and experience overrides shared by graphical and authoritative hosts.
/// </summary>
public static class XpSkills
{
    /// <summary>
    /// Labels, stat identities, legacy port keys and RE_Kenshi plugin keys in display order.
    /// </summary>
    public static readonly (string Label, StatsEnumerated Stat, string PortSetting, string PluginKey)[] Skills =
    [
        ("Strength", StatsEnumerated.Strength, "Strength", "STRENGTH"),
        ("Dexterity", StatsEnumerated.Dexterity, "Dexterity", "DEXTERITY"),
        ("Toughness", StatsEnumerated.Toughness, "Toughness", "TOUGHNESS"),
        ("Perception", StatsEnumerated.Perception, "Perception", "PERCEPTION"),
        ("Melee attack", StatsEnumerated.MeleeAttack, "MeleeAttack", "MELEE_ATTACK"),
        ("Melee defence", StatsEnumerated.MeleeDefence, "MeleeDefence", "MELEE_DEFENCE"),
        ("Dodge", StatsEnumerated.Dodge, "Dodge", "DODGE"),
        ("Martial arts", StatsEnumerated.MartialArts, "MartialArts", "MARTIALARTS"),
        ("Katanas", StatsEnumerated.Katanas, "Katanas", "KATANAS"),
        ("Sabres", StatsEnumerated.Sabres, "Sabres", "SABRES"),
        ("Hackers", StatsEnumerated.Hackers, "Hackers", "HACKERS"),
        ("Heavy weapons", StatsEnumerated.HeavyWeapons, "HeavyWeapons", "HEAVYWEAPONS"),
        ("Blunt", StatsEnumerated.Blunt, "Blunt", "BLUNT"),
        ("Polearms", StatsEnumerated.Polearms, "Polearms", "POLEARMS"),
        ("Crossbows", StatsEnumerated.Crossbows, "Crossbows", "CROSSBOWS"),
        ("Turrets", StatsEnumerated.Turrets, "Turrets", "TURRETS"),
        ("Precision shooting", StatsEnumerated.FriendlyFire, "PrecisionShooting", "FRIENDLY_FIRE"),
        ("Stealth", StatsEnumerated.Stealth, "Stealth", "STEALTH"),
        ("Lockpicking", StatsEnumerated.Lockpicking, "Lockpicking", "LOCKPICKING"),
        ("Thievery", StatsEnumerated.Thieving, "Thievery", "THIEVING"),
        ("Assassination", StatsEnumerated.Assassination, "Assassination", "ASSASSINATION"),
        ("Athletics", StatsEnumerated.Athletics, "Athletics", "ATHLETICS"),
        ("Swimming", StatsEnumerated.Swimming, "Swimming", "SWIMMING"),
        ("Field medic", StatsEnumerated.Medic, "FieldMedic", "MEDIC"),
        ("Robotics", StatsEnumerated.Robotics, "Robotics", "ROBOTICS"),
        ("Engineering", StatsEnumerated.Engineering, "Engineering", "ENGINEERING"),
        ("Science", StatsEnumerated.Science, "Science", "SCIENCE"),
        ("Weapon smith", StatsEnumerated.SmithingWeapon, "WeaponSmith", "SMITHING_WEAPON"),
        ("Armour smith", StatsEnumerated.SmithingArmour, "ArmourSmith", "SMITHING_ARMOUR"),
        ("Bow smith", StatsEnumerated.SmithingBow, "BowSmith", "SMITHING_BOW"),
        ("Labouring", StatsEnumerated.Labouring, "Labouring", "LABOURING"),
        ("Farming", StatsEnumerated.Farming, "Farming", "FARMING"),
        ("Cooking", StatsEnumerated.Cooking, "Cooking", "COOKING"),
    ];

    /// <summary>
    /// A negative rate follows the faction-wide multiplier.
    /// </summary>
    public const float Follows = -0.25f;

    /// <summary>
    /// The STATS field name used to persist a skill override.
    /// </summary>
    public static string Key(StatsEnumerated stat) => StatsInfo.FieldName(stat)!;

    /// <summary>
    /// Retains finite nonnegative overrides for known skills within the supported range.
    /// </summary>
    public static Dictionary<string, float> Kept(Dictionary<string, float>? saved)
    {
        var kept = new Dictionary<string, float>();
        if (saved is null) return kept;

        foreach (var (_, stat, _, _) in Skills)
        {
            string key = Key(stat);
            if (saved.TryGetValue(key, out float value) && float.IsFinite(value) && value >= 0)
                kept[key] = value == 0 ? 0 : Math.Min(value, BuiltInModSettings.MaxXpRate);   // -0 kept as 0
        }

        return kept;
    }

    /// <summary>
    /// Converts field-name overrides to the simulation's array indexed by stat number.
    /// </summary>
    public static float[] Rates(IReadOnlyDictionary<string, float> saved)
    {
        var rates = new float[StatsInfo.Count];
        Array.Fill(rates, Follows);
        foreach (var (_, stat, _, _) in Skills)
            if (saved.TryGetValue(Key(stat), out float value) && value >= 0) rates[(int)stat] = value;

        return rates;
    }
    /// <summary>
    /// Converts the authority's stat-indexed array to persisted field-name overrides.
    /// </summary>
    public static Dictionary<string, float> Saved(IReadOnlyList<float> rates)
    {
        var saved = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (_, stat, _, _) in Skills)
            if ((int)stat < rates.Count && float.IsFinite(rates[(int)stat]) && rates[(int)stat] >= 0)
                saved[Key(stat)] = Math.Min(rates[(int)stat], BuiltInModSettings.MaxXpRate);
        return saved;
    }
}
