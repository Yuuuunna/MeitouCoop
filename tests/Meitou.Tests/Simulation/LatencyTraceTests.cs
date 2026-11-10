using System.Numerics;
using System.Text.Json;
using Meitou.Diagnostics;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

[Collection("LatencyTrace")]
public class LatencyTraceTests
{
    sealed class Sink : ILatencyTraceSink
    {
        public readonly List<LatencyTraceEvent> Events = [];
        public void Emit(in LatencyTraceEvent value) => Events.Add(value);
    }
    sealed class Ground(bool waiting = false) : IWalkability
    {
        public float GroundHeight(float x, float z) => 300;
        public bool IsWalkable(float x, float z) => true;
        public PathResult FindPath(Vector3 from, Vector3 to) => waiting
            ? new PathResult([]) { WaitingForNavigation = true } : PathResult.NotFound;
    }
    static (SimWorld World, PathService Paths, CharacterId Role, CharacterControl Grant, AuthorityConnection Connection) Scene(IWalkability ground)
    {
        var paths = new PathService(ground, synchronous: true);
        var world = new SimWorld(new WorldSettings { Threads = 1, PublishSnapshots = false }, ground, [new MovementSystem(paths)]);
        var player = new PlayerId(7); world.Players.Register(player);
        world.Players.SetMembership(player, 7, FactionPermission.All);
        var role = world.Characters.Spawn(new CharacterHot { Position = new(0, 300, 0), Health = 100, MaxSpeed = 80, WalkSpeed = 15 },
            new CharacterCold { Faction = 7, IsPlayer = true }, 0);
        var grant = world.Players.AssignInitial(player, role);
        var connection = world.Authority.Open(player);
        Assert.True(world.Authority.BeginSynchronization(connection)); Assert.True(world.Authority.Activate(connection));
        return (world, paths, role, grant, connection);
    }

    [Fact]
    public async Task NoPathAndEvaluatedReceiptAreSeparateAndTracingDoesNotChangeWorldHashes()
    {
        var a = Scene(new Ground()); var b = Scene(new Ground());
        using var wa = a.World; using var wb = b.World; using var pa = a.Paths; using var pb = b.Paths;
        var sink = new Sink(); ulong hash = wa.StateHash(); wa.ObserveLatency(sink); Assert.Equal(hash, wa.StateHash());
        var accepted = wa.Authority.Submit(a.Connection, 1, new MoveOrder([a.Role], new(400, 300, 0)) { Tick = 0 }, new([a.Grant]));
        var untraced = wb.Authority.Submit(b.Connection, 1, new MoveOrder([b.Role], new(400, 300, 0)) { Tick = 0 }, new([b.Grant]));
        wa.RunTicks(3); wb.RunTicks(3);
        Assert.Equal(AuthorityStatus.Evaluated, (await accepted).Status); Assert.Equal(AuthorityStatus.Evaluated, (await untraced).Status);
        Assert.Equal(wb.StateHash(), wa.StateHash());
        Assert.Contains(sink.Events, e => e.Stage == LatencyStage.MovementAccepted);
        Assert.Contains(sink.Events, e => e.Stage == LatencyStage.Terminal && e.Outcome == LatencyOutcome.NoPath);
        Assert.DoesNotContain(sink.Events, e => e.Stage == LatencyStage.AuthorityRootChanged);
    }

    [Fact]
    public async Task NavigationWaitIsObservedAndCloseCensorsItRatherThanClaimingMovement()
    {
        var scene = Scene(new Ground(waiting: true)); using var world = scene.World; using var paths = scene.Paths;
        var sink = new Sink(); world.ObserveLatency(sink);
        var pending = world.Authority.Submit(scene.Connection, 1, new MoveOrder([scene.Role], new(400, 300, 0)) { Tick = 0 },
            new([scene.Grant]));
        world.RunTicks(3); Assert.Equal(AuthorityStatus.Evaluated, (await pending).Status);
        Assert.Contains(sink.Events, e => e.Stage == LatencyStage.NavigationWait);
        Assert.DoesNotContain(sink.Events, e => e.Stage == LatencyStage.AuthorityRootChanged);
        world.ObserveLatency(null);
        Assert.Contains(sink.Events, e => e.Stage == LatencyStage.Terminal && e.Outcome == LatencyOutcome.Censored);
    }

    [Fact]
    public async Task SupersededAndQueuedRequestsRetainExplicitTerminalClassifications()
    {
        var scene = Scene(new OpenGroundWalkability((_, _) => 300)); using var world = scene.World; using var paths = scene.Paths;
        var sink = new Sink(); world.ObserveLatency(sink);
        var context = new AuthorityContext([scene.Grant]);
        for (ulong request = 1; request <= 3; request++)
        {
            var task = world.Authority.Submit(scene.Connection, request, new MoveOrder([scene.Role], new(400 + request, 300, 0)) { Tick = 0, Queued = request == 3 }, context);
            world.ApplyPausedInterfaceCommands(); Assert.Equal(AuthorityStatus.Evaluated, (await task).Status);
        }
        Assert.Contains(sink.Events, e => e.Request == 1 && e.Stage == LatencyStage.Terminal && e.Outcome == LatencyOutcome.Superseded);
        Assert.Contains(sink.Events, e => e.Request == 3 && e.Stage == LatencyStage.Terminal && e.Outcome == LatencyOutcome.Deferred);
    }

    sealed class BlockedStream : MemoryStream
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await Release.Task.WaitAsync(cancellationToken); await base.WriteAsync(buffer, cancellationToken); }
    }

    [Fact]
    public async Task BoundedWriterReportsLossAndDrainsWithoutBlockingEmitters()
    {
        using var output = new BlockedStream();
        var writer = new BoundedLatencyTraceWriter(output, capacity: 1, leaveOpen: true);
        for (int i = 0; i < 100; i++) writer.Emit(new(LatencyStage.Intent, Request: (ulong)i));
        Assert.True(writer.Dropped > 0); output.Release.SetResult(); await writer.DisposeAsync();
        Assert.Null(writer.Failure); Assert.Equal(writer.Attempted, writer.Written + writer.Dropped);
        string[] lines = System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var footer = JsonDocument.Parse(lines[^1]);
        Assert.Equal((int)LatencyStage.TraceSummary, footer.RootElement.GetProperty("stage").GetInt32());
        Assert.Equal(writer.Dropped, footer.RootElement.GetProperty("value").GetInt64());
        Assert.Equal(writer.Attempted, footer.RootElement.GetProperty("request").GetInt64());
    }
}
