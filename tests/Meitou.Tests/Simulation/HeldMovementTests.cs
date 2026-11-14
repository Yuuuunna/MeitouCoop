using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Held orders against an explicitly gated asynchronous path worker; timeouts guard deadlocks, not performance.
/// </summary>
public sealed class HeldMovementTests
{
    /// <summary>
    /// Holds every path answer until the test permits completion.
    /// </summary>
    sealed class GatedGround : IWalkability, IDisposable
    {
        /// <summary>
        /// Signals that a real path worker has begun the first query.
        /// </summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Releases blocked queries, including during failed-test cleanup.
        /// </summary>
        public ManualResetEventSlim Release { get; } = new();

        /// <summary>
        /// Number of queries actually taken up by workers.
        /// </summary>
        int _calls;

        /// <summary>
        /// Worker starts observed without consuming any completion signal.
        /// </summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>
        /// The flat surface height.
        /// </summary>
        public float GroundHeight(float x, float z) => 300;

        /// <summary>
        /// Every point on this surface is walkable.
        /// </summary>
        public bool IsWalkable(float x, float z) => true;

        /// <summary>
        /// Returns a direct path only after the test releases the worker.
        /// </summary>
        public PathResult FindPath(Vector3 from, Vector3 to)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Path worker was not released.");
            return new([from, to]);
        }

        /// <summary>
        /// Releases the wait-handle resources after workers have stopped.
        /// </summary>
        public void Dispose() => Release.Dispose();
    }

    /// <summary>
    /// A delayed initial answer remains useful while newer steering orders are waiting.
    /// </summary>
    [Fact]
    public async Task HeldChangingTargetsCanStartWalkingFromAnOlderCompletedPath()
    {
        using var ground = new GatedGround();
        using var movement = new MovementSystem(new PathService(ground, false));
        using var world = MakeWorld(ground, movement, out var actor);
        var token = TestContext.Current.CancellationToken;

        try
        {
            Order(world, actor, new(400, 300, 0));
            await ground.Started.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            for (int i = 0; i < 12; i++) Order(world, actor, new(401 + i, 300, 0));
            Assert.Equal(0, world.Characters.Current[actor.Slot].Position.X);

            ground.Release.Set();
            await DrainWorkers(movement, token);
            Order(world, actor, new(500, 300, 0));

            Assert.True(world.Characters.Current[actor.Slot].Position.X > 0);
            Assert.True(world.Characters.Cold(actor.Slot)!.OrderMove);
        }
        finally
        {
            ground.Release.Set();
        }
    }

    /// <summary>
    /// Repeating an unchanged destination must not multiply queued searches or replace the initial request.
    /// </summary>
    [Fact]
    public async Task HeldUnchangedTargetRetainsItsPendingPathRequest()
    {
        using var ground = new GatedGround();
        using var movement = new MovementSystem(new PathService(ground, false));
        using var world = MakeWorld(ground, movement, out var actor);
        var token = TestContext.Current.CancellationToken;

        try
        {
            var target = new Vector3(400, 300, 0);
            Order(world, actor, target);
            await ground.Started.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            long request = world.Characters.Cold(actor.Slot)!.PathRequest;
            for (int i = 0; i < 12; i++) Order(world, actor, target);

            Assert.Equal(request, world.Characters.Cold(actor.Slot)!.PathRequest);
            Assert.Equal(1, movement.Paths.Submitted);

            ground.Release.Set();
            await DrainWorkers(movement, token);
            Order(world, actor, target);
            Assert.True(world.Characters.Current[actor.Slot].Position.X > 0);
        }
        finally
        {
            ground.Release.Set();
        }
    }

    /// <summary>
    /// A player search can start while every shared worker is blocked inside an NPC search.
    /// </summary>
    [Fact]
    public async Task ReservedPlayerWorkerStartsWithoutWaitingForRunningNpcSearches()
    {
        using var ground = new GatedGround();
        using var paths = new PathService(ground, false);
        var token = TestContext.Current.CancellationToken;

        try
        {
            for (int i = 0; i < PathService.Threads - 1; i++)
                paths.Submit(new CharacterId(i, 1), new(0, 300, 0), new(100, 300, 0));
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => ground.Calls == PathService.Threads - 1,
                TimeSpan.FromSeconds(15)), token));

            paths.SubmitObserved(new CharacterId(100, 1), new(0, 300, 0), new(100, 300, 0), 0, default, 0,
                observeLatency: false, urgent: true);
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => ground.Calls == PathService.Threads,
                TimeSpan.FromSeconds(15)), token));
            Assert.False(ground.Release.IsSet);
        }
        finally
        {
            ground.Release.Set();
        }
    }

    /// <summary>
    /// Constructs a data-free player with real order and movement handlers.
    /// </summary>
    static SimWorld MakeWorld(IWalkability ground, MovementSystem movement, out CharacterId actor)
    {
        var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, ground, [new PlayerSystem(), movement]);
        world.Player.Faction = 2;
        actor = world.Characters.Spawn(new CharacterHot
        {
            Position = new(0, 300, 0), MaxSpeed = 60, WalkSpeed = 30, Task = (byte)CharacterTask.Idle
        }, new CharacterCold { Faction = 2, IsPlayer = true }, 0);
        return world;
    }

    /// <summary>
    /// Submits one held-input sample and advances a single simulation tick.
    /// </summary>
    static void Order(SimWorld world, CharacterId actor, Vector3 target)
    {
        world.Commands.Enqueue(new MoveOrder([actor], target) { Tick = world.Tick });
        world.RunTick();
    }

    /// <summary>
    /// Waits for worker completion without asserting a wall-clock latency budget.
    /// </summary>
    static async Task DrainWorkers(MovementSystem movement, CancellationToken token)
    {
        bool drained = await Task.Run(() => SpinWait.SpinUntil(() => movement.Paths.Pending == 0, TimeSpan.FromSeconds(15)), token);
        Assert.True(drained, "Released path workers did not finish.");
    }
}
