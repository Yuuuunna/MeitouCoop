using Meitou.Simulation.Aggro;
using Meitou.Simulation.Perception;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// The victim's sense-entry flags for a stealth KO (FUN_1408852f0 and FUN_140855830, docs/game/combat.md "Stealth knock-out").
/// </summary>
internal readonly record struct StealthKoAwareness
{
    /// <summary>
    /// The victim's current entry sees the attacker (+0x38); this also cancels the task before its roll.
    /// </summary>
    public bool Seen { get; init; }
    /// <summary>
    /// The victim's current entry hears the attacker (+0x39), halving the chance when not seen.
    /// </summary>
    public bool Heard { get; init; }
    /// <summary>
    /// The entry exists, including a remembered entry with both current flags clear (FUN_140855960 with its flag clear).
    /// </summary>
    public bool Known { get; init; }
    /// <summary>
    /// The victim is fighting, knows the attacker and the attacker is exposed: the chance is zero.
    /// </summary>
    public bool NoticedInFight { get; init; }

    /// <summary>
    /// Reads one generation-checked entry and the attacker's current exposure, shared by publication, task cancellation and the strike.
    /// </summary>
    public static StealthKoAwareness Read(World world, int attacker, int target)
    {
        var table = world.Characters;
        var id = table.IdOf(attacker);
        var aggro = world.SystemOf<AggroSystem>();
        var senses = aggro?.SensesOf(target);
        var entry = default(SenseEntry);
        bool known = senses is not null && senses.TryGet(id, out entry);

        return new StealthKoAwareness
        {
            Seen = entry.Seen,
            Heard = entry.Heard,
            Known = known,
            NoticedInFight = table.Cold(target) is { InCombat: true } && known && aggro?.Unseen(table, id) == false,
        };
    }

    /// <summary>
    /// The original's unclamped percentage using exactly these sense flags and the shared skill formula.
    /// </summary>
    public float Chance(CharacterCold attacker, CharacterCold target) =>
        CarrySystem.StealthKoChance(attacker, target, Seen, Heard, NoticedInFight);
}
