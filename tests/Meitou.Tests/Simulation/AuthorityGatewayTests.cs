using System.Numerics;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class AuthorityGatewayTests
{
    static readonly PlayerId Player = new(2);
    static OpenGroundWalkability Ground() => new((_, _) => 0);
    static SimWorld World(params ITickSystem[] systems) => new(new WorldSettings { PublishSnapshots = false }, Ground(), systems);
    static (CharacterId Role, CharacterControl Grant) Register(SimWorld world)
    {
        world.Players.Register(Player);
        world.Players.SetMembership(Player, 7, FactionPermission.All);
        var role = world.Characters.Spawn(new CharacterHot(), new CharacterCold { Faction = 7, IsPlayer = true }, world.Tick);
        return (role, world.Players.AssignInitial(Player, role));
    }
    static AuthorityConnection Connect(SimWorld world)
    {
        var connection = world.Authority.Open(Player);
        Assert.True(world.Authority.BeginSynchronization(connection));
        Assert.True(world.Authority.Activate(connection));
        return connection;
    }
    static SelectCommand Select(CharacterId role) => new([role]) { Tick = long.MaxValue };
    static AuthorityContext Context(CharacterControl grant) => new([grant]);

    sealed class Recorder(string name, List<string> calls) : ITickSystem
    {
        public readonly List<SimCommand> Commands = [];
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands)
        {
            foreach (var command in commands)
            {
                Commands.Add(command);
                calls.Add($"{name}:{command.GetType().Name}");
            }
        }
    }

    [Fact]
    public async Task ReconnectionInvalidatesQueuedOldConnectionAndRetainsItsReceipt()
    {
        var recorder = new Recorder("one", []);
        using var world = World(recorder);
        var (role, grant) = Register(world);
        var old = Connect(world);
        var pending = world.Authority.Submit(old, 1, Select(role), Context(grant));
        var current = Connect(world);
        Assert.True(current.Generation > old.Generation);
        Assert.Null(world.Authority.GetPhase(old));
        Assert.Equal(AuthorityStatus.InvalidConnection, (await world.Authority.Submit(old, 2, Select(role), Context(grant))).Status);
        Assert.Same(pending, world.Authority.Submit(current, 1, Select(role), Context(grant)));
        world.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.InvalidConnection, (await pending).Status);
        Assert.Empty(recorder.Commands);
        var accepted = world.Authority.Submit(current, 2, Select(role), Context(grant));
        world.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.Evaluated, (await accepted).Status);
        Assert.Single(recorder.Commands);
    }

    [Fact]
    public async Task ReceiptReplayNeverExecutesAgainAndDifferentPayloadConflicts()
    {
        var recorder = new Recorder("one", []);
        using var world = World(recorder);
        var (role, grant) = Register(world);
        var connection = Connect(world);
        var first = world.Authority.Submit(connection, 1, Select(role), Context(grant));
        Assert.Same(first, world.Authority.Submit(connection, 1, Select(role) with { Tick = -5 }, Context(grant)));
        Assert.Equal(AuthorityStatus.Conflict, (await world.Authority.Submit(connection, 1, Select(role) with { Additive = true }, Context(grant))).Status);
        world.ApplyPausedInterfaceCommands();
        connection = Connect(world);
        Assert.Same(first, world.Authority.Submit(connection, 1, Select(role), Context(grant)));
        for (ulong number = 2; number <= 130; number++)
        {
            var next = world.Authority.Submit(connection, number, Select(role), Context(grant));
            world.ApplyPausedInterfaceCommands();
            Assert.Equal(AuthorityStatus.Evaluated, (await next).Status);
        }
        Assert.Equal(AuthorityStatus.Expired, (await world.Authority.Submit(connection, 1, Select(role), Context(grant))).Status);
        Assert.Equal(130, recorder.Commands.Count);
        Assert.Equal(130ul, world.Authority.HighWater(connection));
    }

    [Fact]
    public async Task FrozenPayloadAndGrantsCannotBeChangedAfterSubmission()
    {
        var recorder = new Recorder("one", []);
        using var world = World(recorder);
        var (role, grant) = Register(world);
        var connection = Connect(world);
        CharacterId[] roles = [role];
        CharacterControl[] grants = [grant];
        var command = new MoveOrder(roles, new Vector3(1, 2, 3)) { Tick = long.MaxValue, Queued = true };
        var request = world.Authority.Submit(connection, 1, command, new(grants));
        roles[0] = CharacterId.None;
        grants[0] = default;
        world.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.Evaluated, (await request).Status);
        var frozen = Assert.IsType<MoveOrder>(Assert.Single(recorder.Commands));
        Assert.Equal(role, Assert.Single(frozen.Characters));
        Assert.Equal(new Vector3(1, 2, 3), frozen.Target);
        Assert.True(frozen.Queued);
        Assert.Equal(0, frozen.Tick);
        Assert.Equal(Player, frozen.Issuer);
    }

    [Fact]
    public async Task PausedRequestsRunAllSystemsInRequestOrderAndAdvanceRevisionWithoutTicking()
    {
        List<string> calls = [];
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, Ground(), [new Recorder("a", calls), new Recorder("b", calls)]);
        var world = runtime.World;
        var (role, grant) = Register(world);
        var connection = Connect(world);
        var first = world.Authority.Submit(connection, 1, Select(role), Context(grant));
        var second = world.Authority.Submit(connection, 2, new StopCommand([role]) { Tick = 300 }, Context(grant));
        runtime.Speed = 0;
        runtime.Advance(1);
        Assert.Equal(new[] { "a:SelectCommand", "b:SelectCommand", "a:StopCommand", "b:StopCommand" }, calls);
        var a = await first; var b = await second;
        Assert.Equal(0, world.Tick);
        Assert.True(a.Operation < b.Operation);
        Assert.True(a.Revision < b.Revision);
        Assert.Equal(b.Revision, world.StateRevision);
    }

    [Fact]
    public async Task GrantRevocationAndRestorationDoesNotReviveAnOldRequest()
    {
        var recorder = new Recorder("one", []);
        using var world = World(recorder);
        var (role, grant) = Register(world);
        var connection = Connect(world);
        var request = world.Authority.Submit(connection, 1, Select(role), Context(grant));
        world.Players.RemoveMembership(Player, 7);
        world.Players.SetMembership(Player, 7, FactionPermission.All);
        world.Players.AssignInitial(Player, role);
        world.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.StaleAuthority, (await request).Status);
        Assert.Empty(recorder.Commands);
    }

    [Fact]
    public async Task TargetFactionRevisionIsCheckedEvenWhenTheSamePlayerHasBothMemberships()
    {
        var recorder = new Recorder("one", []);
        using var world = World(recorder);
        Register(world);
        world.Players.SetMembership(Player, 8, FactionPermission.All);
        var connection = Connect(world);
        var member = world.Players.Find(Player)!;
        var command = new RenameFactionCommand("changed") { Faction = 7, Tick = 0 };
        var wrong = world.Authority.Submit(connection, 1, command, new([], 8, member.MembershipRevision(8)));
        var stale = world.Authority.Submit(connection, 2, command, new([], 7, member.MembershipRevision(7)));
        world.Players.SetMembership(Player, 7, FactionPermission.ReadFunds);
        world.Players.SetMembership(Player, 7, FactionPermission.All);
        world.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.StaleAuthority, (await wrong).Status);
        Assert.Equal(AuthorityStatus.StaleAuthority, (await stale).Status);
        Assert.Empty(recorder.Commands);
    }

    [Fact]
    public async Task NoRoleAndUnsynchronizedPlayersCannotEnterGameplay()
    {
        using var world = World();
        world.Players.Register(Player);
        world.Players.SetMembership(Player, 7, FactionPermission.All);
        var connection = world.Authority.Open(Player);
        Assert.False(world.Authority.BeginSynchronization(connection));
        var command = new RenameFactionCommand("name") { Faction = 7, Tick = 0 };
        var context = new AuthorityContext([], 7, world.Players.Find(Player)!.MembershipRevision(7));
        Assert.Equal(AuthorityStatus.NotActive, (await world.Authority.Submit(connection, 1, command, context)).Status);
        Assert.Equal(0ul, world.Authority.HighWater(connection));
        var role = world.Characters.Spawn(new CharacterHot(), new CharacterCold { IsPlayer = true, Faction = 7 }, 0);
        world.Players.AssignInitial(Player, role);
        Assert.True(world.Authority.BeginSynchronization(connection));
        Assert.Equal(AuthorityStatus.NotActive, (await world.Authority.Submit(connection, 1, command, context)).Status);
        Assert.True(world.Authority.Activate(connection));
        var request = world.Authority.Submit(connection, 1, command, context);
        world.Characters.Remove(role);
        world.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.NoActiveRole, (await request).Status);
        Assert.Equal(AdmissionPhase.Preparing, world.Authority.GetPhase(connection));
        Assert.Single(world.Players.KnownRoles(Player)); // Unloaded ownership is not eligibility for free creation.
    }

    [Fact]
    public async Task QueueLimitDoesNotConsumeTheRejectedRequestNumberAndClosingCompletesWaiters()
    {
        using var world = World();
        var (role, grant) = Register(world);
        var connection = Connect(world);
        List<Task<AuthorityReceipt>> requests = [];
        for (ulong n = 1; n <= 256; n++) requests.Add(world.Authority.Submit(connection, n, Select(role), Context(grant)));
        Assert.Equal(AuthorityStatus.Busy, (await world.Authority.Submit(connection, 257, Select(role), Context(grant))).Status);
        Assert.Equal(256ul, world.Authority.HighWater(connection));
        world.Dispose();
        foreach (var request in requests) Assert.Equal(AuthorityStatus.WorldClosed, (await request).Status);
    }

    [Fact]
    public async Task UnsupportedAndOversizedPayloadsNeverEnterTheQueue()
    {
        using var world = World();
        var (role, grant) = Register(world);
        var connection = Connect(world);
        Assert.Equal(AuthorityStatus.Unsupported, (await world.Authority.Submit(connection, 1, new FocusCommand(Vector3.Zero) { Tick = 0 }, new([]))).Status);
        var command = new RenameFactionCommand(new string('x', 1024 * 1024 + 1)) { Tick = 0, Faction = 7 };
        Assert.Equal(AuthorityStatus.InvalidRequest, (await world.Authority.Submit(connection, 1, command, new([], 7, world.Players.Find(Player)!.MembershipRevision(7)))).Status);
        var valid = world.Authority.Submit(connection, 1, Select(role), Context(grant));
        world.RunTick();
        Assert.Equal(AuthorityStatus.Evaluated, (await valid).Status);
    }

    sealed class Throwing : ITickSystem
    {
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands)
        {
            if (commands.Any(c => c.Issuer is not null)) throw new InvalidOperationException("Injected handler failure");
        }
    }

    sealed class TickFailure : ITickSystem
    {
        public Action? BeforeFailure;
        public void Schedule(SimWorld world)
        {
            BeforeFailure?.Invoke();
            throw new InvalidOperationException("Injected simulation failure");
        }
    }

    [Fact]
    public async Task SimulationFailureClosesRequestsArrivingAfterTheInputBoundary()
    {
        var failure = new TickFailure();
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, Ground(), [failure]);
        var world = runtime.World;
        var (role, grant) = Register(world);
        var connection = Connect(world);
        Task<AuthorityReceipt>? pending = null;
        failure.BeforeFailure = () => pending = world.Authority.Submit(connection, 1, Select(role), Context(grant));
        Assert.Throws<InvalidOperationException>(() => runtime.Step());
        Assert.True(runtime.Faulted);
        Assert.Equal(AuthorityStatus.WorldFaulted, (await pending!).Status);
        Assert.Equal(AuthorityStatus.WorldFaulted, (await world.Authority.Submit(connection, 2, Select(role), Context(grant))).Status);
    }

    [Fact]
    public async Task NullPayloadMembersAreRejectedBeforeTheyCanFaultTheWorld()
    {
        using var world = World();
        Register(world);
        var connection = Connect(world);
        SimCommand[] malformed =
        [
            new MoveOrder(null!, Vector3.Zero) { Tick = 0 },
            new RenameFactionCommand(null!) { Tick = 0 },
            new Meitou.Simulation.Construction.PlaceBuildings([null!]) { Tick = 0 },
            new Meitou.Simulation.Items.QuickMoveItem(default, null!, false, false) { Tick = 0 },
        ];
        foreach (var command in malformed)
            Assert.Equal(AuthorityStatus.InvalidRequest, (await world.Authority.Submit(connection, 1, command, new([]))).Status);
        Assert.Equal(0ul, world.Authority.HighWater(connection));
        world.RunTick();
    }

    [Fact]
    public async Task PausedSnapshotsCarryTheCommittedRevisionAndActualPlayerSelection()
    {
        using var world = new SimWorld(new WorldSettings(), Ground(), [new PlayerSystem()]);
        var (role, grant) = Register(world);
        var connection = Connect(world);
        var request = world.Authority.Submit(connection, 1, Select(role), Context(grant));
        world.ApplyPausedInterfaceCommands();
        Assert.Equal(role, Assert.Single(world.Players.Find(Player)!.State.Selection));
        Assert.Empty(world.Player.Selection);
        Assert.Equal((await request).Revision, world.Snapshot.StateRevision);
        Assert.NotEqual(0ul, world.Snapshot.StateRevision);
        Assert.Equal(0, world.Snapshot.Tick);
    }

    [Fact]
    public async Task WorldReplacementClosesOldWaitersAndCannotReuseOldConnectionHandles()
    {
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, Ground());
        var old = runtime.World;
        var (role, grant) = Register(old);
        var connection = Connect(old);
        var request = old.Authority.Submit(connection, 1, Select(role), Context(grant));
        runtime.ReplaceWorld([], w => Register(w));
        Assert.Equal(AuthorityStatus.WorldClosed, (await request).Status);
        Assert.Equal(AuthorityStatus.InvalidConnection, (await runtime.World.Authority.Submit(connection, 1, Select(role), Context(grant))).Status);
        var fresh = Connect(runtime.World);
        Assert.NotEqual(connection.World, fresh.World);
    }

    [Fact]
    public async Task HandlerFailureFaultsTheRuntimeAndNeverLeavesPendingReceiptsHanging()
    {
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, Ground(), [new Throwing()]);
        var world = runtime.World;
        var (role, grant) = Register(world);
        var connection = Connect(world);
        var first = world.Authority.Submit(connection, 1, Select(role), Context(grant));
        var second = world.Authority.Submit(connection, 2, Select(role), Context(grant));
        Assert.Throws<InvalidOperationException>(() => runtime.Synchronize());
        Assert.True(runtime.Faulted);
        Assert.Equal(AuthorityStatus.WorldFaulted, (await first).Status);
        Assert.Equal(AuthorityStatus.WorldFaulted, (await second).Status);
        Assert.Equal(AuthorityStatus.WorldFaulted, (await world.Authority.Submit(connection, 3, Select(role), Context(grant))).Status);
    }
}
