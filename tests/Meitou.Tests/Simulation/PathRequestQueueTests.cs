using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Movement;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Deterministic queue schedules for held steering, player priority, ordinary fairness and shutdown.
/// </summary>
public sealed class PathRequestQueueTests
{
    /// <summary>
    /// Changing a queued goal replaces its payload without moving the character behind later arrivals.
    /// </summary>
    [Fact]
    public void RepeatedReplacementsRetainTheOriginalQueuePositionAndOnlyTheLatestPayload()
    {
        var queue = new PathRequestQueue();
        var first = Request(1, 1);
        var second = Request(2, 2);
        Assert.Null(queue.Enqueue(first, urgent: false));
        Assert.Null(queue.Enqueue(second, urgent: false));

        for (int i = 3; i <= 20; i++)
        {
            Assert.Equal(first, queue.Enqueue(Request(i, 1), urgent: false));
            first = Request(i, 1);
        }

        queue.Complete();
        Assert.Equal(first, queue.Take());
        Assert.Equal(second, queue.Take());
        Assert.Null(queue.Take());
    }

    /// <summary>
    /// Players pass queued NPC work, but a sustained player stream cannot starve ordinary requests.
    /// </summary>
    [Fact]
    public void PlayerPriorityIsBoundedAndKeepsFifoOrderWithinEachClass()
    {
        var queue = new PathRequestQueue();
        queue.Enqueue(Request(1, 1), urgent: false);
        for (int i = 2; i <= 8; i++) queue.Enqueue(Request(i, i), urgent: true);
        queue.Complete();

        Assert.Equal(new long[] { 2, 3, 4, 5, 1, 6, 7, 8 }, Enumerable.Range(0, 8).Select(_ => queue.Take()!.Value.Id));
        Assert.Null(queue.Take());
    }

    /// <summary>
    /// A character newly controlled by a player promotes its existing queue node, without duplicating it.
    /// </summary>
    [Fact]
    public void PromotingAnExistingRequestDoesNotCreateASecondQueueEntry()
    {
        var queue = new PathRequestQueue();
        queue.Enqueue(Request(1, 1), urgent: false);
        queue.Enqueue(Request(2, 2), urgent: false);
        queue.Enqueue(Request(3, 3), urgent: true);
        Assert.Equal(Request(2, 2), queue.Enqueue(Request(4, 2), urgent: true));
        queue.Complete();

        Assert.Equal(new long[] { 3, 4, 1 }, Enumerable.Range(0, 3).Select(_ => queue.Take()!.Value.Id));
        Assert.Null(queue.Take());
        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(Request(5, 5), urgent: false));
    }

    /// <summary>
    /// The reserved player worker never becomes occupied by a slow NPC search.
    /// </summary>
    [Fact]
    public void ReservedPlayerWorkerOnlyTakesPlayerRequestsAndLeavesNpcWorkToSharedWorkers()
    {
        var queue = new PathRequestQueue();
        queue.Enqueue(Request(1, 1), urgent: false);
        queue.Enqueue(Request(2, 2), urgent: true);
        queue.Complete();

        Assert.Equal(Request(2, 2), queue.Take(playerOnly: true));
        Assert.Null(queue.Take(playerOnly: true));
        Assert.Equal(Request(1, 1), queue.Take());
        Assert.Null(queue.Take());
    }

    /// <summary>
    /// Idle consumers are released when the service shuts down.
    /// </summary>
    [Fact]
    public async Task CompletionWakesEveryIdleWorker()
    {
        var queue = new PathRequestQueue();
        var token = TestContext.Current.CancellationToken;
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => queue.Take(), token)).ToArray();
        queue.Complete();

        var results = await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15), token);
        Assert.All(results, result => Assert.Null(result));
    }

    /// <summary>
    /// Distinct identities and goals make accidental payload reuse visible.
    /// </summary>
    static PathRequest Request(long id, int character) => new(id, new CharacterId(character, 1), Vector3.Zero, new Vector3(id, 300, 0));
}
