using System.Numerics;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;

namespace Meitou.Tests.Engine;

public class ClientCommandBindingTests
{
    static readonly CharacterId A = new(1, 2), B = new(2, 3), Unowned = new(3, 4);
    static PlayerWorldView View() => new(Guid.NewGuid(), new(1), 1, 5, 34,
        new(10, [new(A, null, Vector3.Zero, 0, []) { Faction = 7 }, new(B, null, Vector3.One, 0, []) { Faction = 9 },
            new(Unowned, null, Vector3.Zero, 0, []) { Faction = 7 }])
        {
            Buildings = [new("wall-piece", "wall", Vector3.Zero, Quaternion.Identity, 0, null, 9, 0, 1, [], false, false)
                { AuthorityFaction = 9, SiteRevision = 11, RootRevision = 12 }],
            Containers = [new("foreign-container", "box", Vector3.Zero, 13)],
            Machines = [new("machine", "bench", [], null, null, null, 0, 0, 1) { AuthorityFaction = 9 }],
        }, [new(new(101), A, 7, 2, true, false), new(new(102), B, 9, 3, true, false)],
        [new(7, "First", FactionPermission.All, 4, 100, ResearchSnapshot.Empty, []),
            new(9, "Second", FactionPermission.All, 8, 200, ResearchSnapshot.Empty, [])]);

    [Fact]
    public void SelectedActorsAreFrozenExplicitlyWithTheirOwnGrantsAndNoImplicitFaction()
    {
        var view = View(); var selection = new[] { B, A, B };
        var intent = new MoveOrder([], new(10, 20, 30)) { Tick = 999, Queued = true };
        var bound = ClientCommandBinding.Bind(view, intent, selection, 7);
        selection[0] = Unowned;
        var move = Assert.IsType<MoveOrder>(bound.Command);
        Assert.Equal(new[] { B, A }, move.Characters); Assert.Empty(intent.Characters);
        Assert.True(move.Queued); Assert.Equal(intent.Target, move.Target);
        Assert.Equal(new ExpectedRoleGrant(new(102), 9, 3), bound.Expected.Roles[0]);
        Assert.Equal(new ExpectedRoleGrant(new(101), 7, 2), bound.Expected.Roles[1]);
        Assert.Equal(-1, bound.Expected.Faction); Assert.Equal(0ul, bound.Expected.MembershipRevision);
        var sent = CommandProtocol.Decode(CommandProtocol.Encode(view.World, 1, bound.Command, bound.Expected));
        Assert.Equal(new[] { B, A }, Assert.IsType<MoveOrder>(sent.Command).Characters); Assert.Equal(0, sent.Command.Tick);
    }

    [Fact]
    public void OwnershipComesFromCurrentGrantsInsteadOfTheSelectedFactionOrCharacterFlags()
    {
        var view = View();
        Assert.Throws<InvalidOperationException>(() => ClientCommandBinding.Bind(view, new MoveOrder([Unowned], Vector3.Zero) { Tick = 0 }, [A], 7));
        var revoked = view with { Roles = [view.Roles[0] with { Available = false }, view.Roles[1]] };
        Assert.Throws<InvalidOperationException>(() => ClientCommandBinding.Bind(revoked, new MoveOrder([], Vector3.Zero) { Tick = 0 }, [A], 7));
        var unloaded = view with { Snapshot = view.Snapshot with { Characters = [view.Snapshot.Characters[1]] } };
        Assert.Throws<InvalidOperationException>(() => ClientCommandBinding.Bind(unloaded, new SelectCommand([A]) { Tick = 0 }, [], 7));
        Assert.Empty(ClientCommandBinding.Bind(view, new SelectCommand([]) { Tick = 0 }, [A], 7).Expected.Roles);
        Assert.Throws<InvalidOperationException>(() => ClientCommandBinding.Bind(view, new SandboxMovement(A, 1, 100) { Tick = 0 }, [A], 7));
    }

    [Fact]
    public void FactionAndPropertyCommandsCarryTheCorrectMembershipEvenAcrossDifferentOwners()
    {
        var view = View();
        var research = ClientCommandBinding.Bind(view, new QueueResearch("tech") { Tick = 0 }, [A], 9);
        Assert.Equal(9, Assert.IsType<QueueResearch>(research.Command).Faction);
        Assert.Equal(9, research.Expected.Faction); Assert.Equal(8ul, research.Expected.MembershipRevision);
        var wall = ClientCommandBinding.Bind(view, new BuildOrder([], "wall-piece") { Tick = 0, SiteRevision = 11, RootRevision = 12 }, [A], 7);
        Assert.Equal(9, wall.Expected.Faction); Assert.Equal(8ul, wall.Expected.MembershipRevision);
        Assert.Equal(7, Assert.Single(wall.Expected.Roles).Faction);
        Assert.Equal(12ul, Assert.IsType<BuildOrder>(wall.Command).RootRevision);
        var machine = ClientCommandBinding.Bind(view, new OperateOrder([], "machine") { Tick = 0 }, [A], 7);
        Assert.Equal(9, machine.Expected.Faction); Assert.Equal(8ul, machine.Expected.MembershipRevision);
        var container = ClientCommandBinding.Bind(view, new OpenContainerOrder([], "foreign-container") { Tick = 0 }, [A], 7);
        Assert.Equal(13, container.Expected.Faction); Assert.Equal(0ul, container.Expected.MembershipRevision);
        Assert.Throws<InvalidOperationException>(() => ClientCommandBinding.Bind(view, new QueueResearch("tech") { Tick = 0 }, [A], -1));
        Assert.Throws<InvalidOperationException>(() => ClientCommandBinding.Bind(view, new BuildOrder([A], "hidden") { Tick = 0 }, [A], 7));
    }

    [Fact]
    public void PurchaseBindsTheExplicitBuyerAndFactionWithoutBorrowingAnotherPlayersState()
    {
        var view = View();
        var purchase = ClientCommandBinding.Bind(view, new BuyBuilding("house", 100) { Tick = 0 }, [B], 9);
        Assert.Equal(B, Assert.IsType<BuyBuilding>(purchase.Command).Buyer);
        Assert.Equal(9, purchase.Expected.Faction); Assert.Equal(8ul, purchase.Expected.MembershipRevision);
        Assert.Equal(new EntityId(102), Assert.Single(purchase.Expected.Roles).Entity);
        Assert.Throws<InvalidOperationException>(() => ClientCommandBinding.Bind(view, new BuyBuilding("house", 100) { Tick = 0 }, [], 9));
    }
}
