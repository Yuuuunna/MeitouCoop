namespace Meitou.Simulation.FirstAid;

public sealed partial class FirstAidSystem
{
    readonly Dictionary<CharacterId, CharacterControl?> activityAuthority = [];
    readonly Dictionary<CharacterId, CharacterControl?> jobAuthority = [];

    void RememberActivity(World world, int slot) =>
        activityAuthority[world.Characters.IdOf(slot)] = world.Players.ControlOf(world.Characters.IdOf(slot));

    void RememberJobs(World world, int slot) =>
        jobAuthority[world.Characters.IdOf(slot)] = world.Players.ControlOf(world.Characters.IdOf(slot));

    bool ActivityAllowed(World world, int slot)
    {
        var id = world.Characters.IdOf(slot);
        if (activityAuthority.TryGetValue(id, out var grant))
            return grant is { } control ? world.Players.Validate(control) :
                !world.Players.RequiresExplicitControl || world.Characters.Cold(slot) is { IsPlayer: false };
        return !world.Players.RequiresExplicitControl || world.Characters.Cold(slot) is { IsPlayer: false };
    }

    bool JobsAllowed(World world, int slot)
    {
        var id = world.Characters.IdOf(slot);
        if (!jobAuthority.ContainsKey(id)) RememberJobs(world, slot);
        return jobAuthority[id] is { } grant ? world.Players.Validate(grant) : !world.Players.RequiresExplicitControl;
    }

    void PruneAuthority(World world)
    {
        var table = world.Characters;
        foreach (var id in activityAuthority.Keys.ToArray())
        {
            if (!table.TryResolveNext(id, out int slot)) { activityAuthority.Remove(id); continue; }
            var task = (CharacterTask)table.Next[slot].Task;
            if (task is not (CharacterTask.Treat or CharacterTask.Rest)) { activityAuthority.Remove(id); continue; }
            if (ActivityAllowed(world, slot)) continue;
            End(table, slot);
            MovementSystem.Stop(table, slot);
            table.Next[slot].Task = (byte)CharacterTask.Idle;
            activityAuthority.Remove(id);
        }
        foreach (var id in jobAuthority.Keys.ToArray())
            if (!table.TryResolveNext(id, out _)) jobAuthority.Remove(id);
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
