using System.Numerics;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation;

/// <summary>
/// Presentation of an authority-published line over a character's head.
/// </summary>
public enum SpeechStyle { Normal, Whisper, Shout }

/// <summary>
/// A short-lived pause leaves the underlying route intact. Serial phases own the map. Movement workers only read it.
/// </summary>
internal sealed class TalkPauses(World world)
{
    /// <summary>
    /// A listener's target, expiry and initial blood percentage for detecting damage outside combat.
    /// </summary>
    readonly record struct Pause(CharacterId Target, long Until, float Health);

    /// <summary>
    /// Active pauses in stable character order.
    /// </summary>
    readonly SortedDictionary<CharacterId, Pause> _active =
        new(Comparer<CharacterId>.Create((a, b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Generation.CompareTo(b.Generation)));

    /// <summary>
    /// Whether ordinary combat contacts must be reported for interruption.
    /// </summary>
    public bool Active => _active.Count > 0;

    /// <summary>
    /// Grants a pause of up to thirty seconds to an available listener.
    /// </summary>
    public bool Give(CharacterId who, CharacterId target, int ticks)
    {
        var table = world.Characters;
        if (who == target || ticks <= 0 || ticks > MathF.Ceiling(30 / world.TickSeconds) || world.StarterAvatars.IsProtected(who)
            || !table.TryResolveNext(who, out int slot) || !table.TryResolveNext(target, out int other)
            || table.Cold(other) is not { DiedTick: < 0 } speaker || speaker.Medical is { Incapacitated: true }
            || table.Cold(slot) is not { } cold || !Available(cold, table.Next[slot])) return false;

        _active[who] = new(target, world.Tick + ticks, table.Next[slot].Health);
        return true;
    }

    /// <summary>
    /// A listener must be conscious, free to move and outside combat, flight or urgent work.
    /// </summary>
    static bool Available(CharacterCold cold, in CharacterHot hot) =>
        cold.DiedTick < 0 && cold.Medical is not { Incapacitated: true } && !cold.InCombat && !cold.Fleeing && cold.CarriedBy.IsNone
        && (CharacterTask)hot.Task is CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow or CharacterTask.GoTo;

    /// <summary>
    /// Interrupts the listener on a hit or manual instruction.
    /// </summary>
    public void Cancel(CharacterId who) => _active.Remove(who);

    /// <summary>
    /// Removes invalid or interrupted pauses before movement.
    /// </summary>
    public void Prune()
    {
        if (!Active) return;
        var table = world.Characters;
        foreach (var (who, p) in _active.ToArray())
        {
            if (p.Until <= world.Tick || !table.TryResolveNext(who, out int slot) || !table.TryResolveNext(p.Target, out int target)
                || table.Cold(target) is not { DiedTick: < 0 } speaker || speaker.Medical is { Incapacitated: true }
                || world.StarterAvatars.IsProtected(who) || !Available(table.Cold(slot)!, table.Next[slot])
                || table.Next[slot].Health < p.Health) Cancel(who);
        }
    }

    /// <summary>
    /// Applies standing and facing in this listener's own movement partition, using the other slot's immutable position.
    /// </summary>
    public bool Hold(int slot, in CharacterHot previous, ref CharacterHot next, CharacterCold cold)
    {
        if (!Active || !_active.TryGetValue(new(slot, previous.Generation), out var pause) || !Available(cold, next)) return false;

        var direction = world.Characters.Previous[pause.Target.Slot].Position - previous.Position;
        cold.AgentSpeed = 0;
        next.Velocity = Vector3.Zero;
        next.Animation = 0;
        next.AnimationTime = previous.Animation == 0 ? previous.AnimationTime + world.StepSeconds(slot) : 0;
        if (direction.X != 0 || direction.Z != 0) next.Yaw = MathF.Atan2(direction.X, direction.Z);
        return true;
    }

    /// <summary>
    /// Hash every remaining movement-affecting pause.
    /// </summary>
    public void Hash(ref StateHasher hash)
    {
        foreach (var (id, pause) in _active)
        {
            hash.Add(id);
            hash.Add(pause.Target);
            hash.Add(pause.Until);
            hash.Add(pause.Health);
        }
    }
}
