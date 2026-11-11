using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
namespace Meitou.Simulation.FirstAid;

public sealed partial class FirstAidSystem
{
    readonly Dictionary<CharacterId, CharacterControl?> _activityAuthority = [];

    void RememberActivity(World world, int slot) =>
        _activityAuthority[world.Characters.IdOf(slot)] = world.Players.ControlOf(world.Characters.IdOf(slot));

    bool ActivityAllowed(World world, int slot) =>
        !world.Players.RequiresExplicitControl || world.Characters.Cold(slot) is { IsPlayer: false } && (!_activityAuthority.TryGetValue(world.Characters.IdOf(slot), out var prior) || prior is null)
        || world.Players.ControlOf(world.Characters.IdOf(slot)) is { } grant && world.Players.Validate(grant);

    bool JobsAllowed(World world, int slot) => ActivityAllowed(world, slot);

    void PruneAuthority(World world)
    {
        var table = world.Characters;
        foreach (var id in _activityAuthority.Keys.ToArray())
        {
            if (!table.TryResolveNext(id, out int slot)) { _activityAuthority.Remove(id); continue; }
            var task = (CharacterTask)table.Next[slot].Task;
            if (task is not (CharacterTask.Treat or CharacterTask.Rest)) { _activityAuthority.Remove(id); continue; }
            if (ActivityAllowed(world, slot)) continue;
            TaskStart.End(world, slot, TaskEnd.Stopped);
            MovementSystem.Stop(table, slot);
            table.Next[slot].Task = (byte)CharacterTask.Idle;
            _activityAuthority.Remove(id);
        }
    }

    // A patient's faction does not confer control and does not bar voluntary first aid.
    // Remote targeting is bounded by the medic's authoritative interaction/sight range.
    static bool AdmittedPatient(World world, SimCommand command, int medic, int patient)
    {
        if (command.Issuer is null || medic == patient) return true;
        var table = world.Characters;
        float range = AggroSystem.RangeOf(table.Cold(medic)!, AggroSystem.OutdoorSight);
        return float.IsFinite(range) && Gap(table.Next[medic], table.Next[patient]) <= range;
    }

    static float Gap(in CharacterHot a, in CharacterHot b) =>
        System.Numerics.Vector2.Distance(new(a.Position.X, a.Position.Z), new(b.Position.X, b.Position.Z));

    static bool MayShareKit(World world, int medic, CharacterId donor)
    {
        if (!world.Players.RequiresExplicitControl) return true;
        var control = world.Players.ControlOf(world.Characters.IdOf(medic));
        var other = world.Players.ControlOf(donor);
        return control is { } owner && other is { } source && owner.Player == source.Player;
    }
}