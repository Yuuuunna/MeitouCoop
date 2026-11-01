using Meitou.Runtime;
using Meitou.Server;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

public class ServerWorldLoopTests
{
    static WorldRuntime Runtime() => new(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), []) { Speed = 0 };
    [Fact]
    public async Task CanceledQueuedWorkCannotMutateTheWorldAndOneOperationFailureDoesNotKillTheLoop()
    {
        using var runtime = Runtime(); await using var loop = new ServerWorldLoop(runtime);
        using var release = new ManualResetEventSlim(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = loop.InvokeAsync(() => { started.TrySetResult(); release.Wait(timeout.Token); return 1; }, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        using var cancel = new CancellationTokenSource(); bool changed = false;
        var pending = loop.InvokeAsync(() => changed = true, cancel.Token); cancel.Cancel(); release.Set();
        Assert.Equal(1, await first); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.False(changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => loop.InvokeAsync(() => throw new InvalidDataException("request rejected"), timeout.Token));
        Assert.Equal(42, await loop.InvokeAsync(() => 42, timeout.Token)); Assert.False(runtime.Faulted);
    }
    [Fact]
    public async Task ShutdownCompletesPendingCallersAndConcurrentDisposalWaitsForTheSameCleanup()
    {
        using var runtime = Runtime(); var loop = new ServerWorldLoop(runtime);
        using var release = new ManualResetEventSlim(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = loop.InvokeAsync(() => { started.TrySetResult(); release.Wait(timeout.Token); }, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        bool changed = false; var pending = loop.InvokeAsync(() => changed = true, timeout.Token);
        var stopping = loop.DisposeAsync().AsTask(); var same = loop.DisposeAsync().AsTask();
        Assert.Same(stopping, same); release.Set(); await first; await stopping;
        Assert.NotNull(await Record.ExceptionAsync(() => pending)); Assert.False(changed);
        await loop.DisposeAsync();
    }
}
