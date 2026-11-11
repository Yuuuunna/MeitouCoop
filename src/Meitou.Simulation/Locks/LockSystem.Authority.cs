using Meitou.Simulation.Ai;
using Meitou.Simulation.Player;
namespace Meitou.Simulation.Locks;

public sealed partial class LockSystem
{
    sealed record WorkAccess(SimCommand Command, Fixture? Fixture, int Owner, ulong FixtureRevision, CharacterId Target, CharacterCold? TargetCold, int TargetFaction, ulong Membership);
    readonly Dictionary<CharacterId, WorkAccess> _workAccess = [];
    readonly Dictionary<CharacterId, Fixture> _doorWalkers = [];

    void Remember(World world, SimCommand command, CharacterId actor, Fixture? fixture = null, CharacterId target = default)
    {
        CharacterCold? cold = world.Characters.TryResolveNext(target, out int slot) ? world.Characters.Cold(slot) : null;
        int faction = world.Characters.TryResolveNext(actor, out int worker) ? world.Characters.Cold(worker)!.Faction : -1;
        ulong membership = command.Issuer is { } issuer ? world.Players.Find(issuer)?.MembershipRevision(faction) ?? 0 : 0;
        _workAccess[actor] = new(command, fixture, fixture?.Owner ?? -1, fixture is null ? 0 : world.Fixtures.RevisionOf(fixture), target, cold, cold?.Faction ?? -1, membership);
    }

    static bool MayManageDoor(World world, SimCommand command, int owner) => command.Issuer is { } issuer
        ? world.Players.Find(issuer)?.HasPermission(owner, FactionPermission.ManageAssets) == true
        : owner == world.Player.Faction;

    void ValidateWork(World world)
    {
        foreach (var (actor, access) in _workAccess.ToArray())
        {
            var table = world.Characters;
            if (!table.TryResolveNext(actor, out int slot)) { _workAccess.Remove(actor); _doorWalkers.Remove(actor); continue; }
            var c = table.Cold(slot)!;
            bool valid = world.MayOrder(access.Command, actor)
                && (access.Command.Issuer is not { } issuer || access.Membership != 0 && world.Players.Find(issuer)?.MembershipRevision(c.Faction) == access.Membership)
                && (access.Fixture is not { } fixture || ReferenceEquals(world.Fixtures.Find(fixture.Id), fixture) && fixture.Owner == access.Owner && world.Fixtures.RevisionOf(fixture) == access.FixtureRevision)
                && (access.TargetCold is null || table.TryResolveNext(access.Target, out int target) && ReferenceEquals(table.Cold(target), access.TargetCold) && access.TargetCold.Faction == access.TargetFaction);
            if (valid && access.Command is DoorLockOrder && !MayManageDoor(world, access.Command, access.Owner)) valid = false;
            if (!valid)
            {
                ClearWork(c); c.Bashing = null; c.BashOrdered = false;
                _doorWalkers.Remove(actor); _workAccess.Remove(actor);
            }
            else if (c.PickingLock is null && c.Shackles is null && c.ForcingLock is null && c.DoorJob is null && c.Freeing.IsNone && c.EnteringCage is null && !c.BashOrdered && !_doorWalkers.ContainsKey(actor)) _workAccess.Remove(actor);
        }
    }
}