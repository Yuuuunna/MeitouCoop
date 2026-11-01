using System.Diagnostics;
using System.Threading.Channels;
using Meitou.Runtime;

namespace Meitou.Server;

/// <summary>The single owner of a shared runtime while serving. Network workers enqueue synchronous world-boundary work, never run simulation themselves.</summary>
public sealed class ServerWorldLoop : IAsyncDisposable
{
    readonly WorldRuntime runtime;
    readonly CancellationTokenSource stop = new();
    readonly Lock lifecycle = new();
    Task? shutdown;
    readonly Channel<IWork> work = Channel.CreateBounded<IWork>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    public Task Completion { get; }
    public CancellationToken Stopping => stop.Token;
    public WorldRuntime Runtime => runtime;
    public ServerWorldLoop(WorldRuntime runtime) { this.runtime = runtime; Completion = Task.Run(Run); }
    interface IWork { void Execute(); void Fail(Exception error); }
    sealed class Work<T>(Func<T> action, CancellationToken cancellation) : IWork
    {
        public TaskCompletionSource<T> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Execute()
        {
            if (cancellation.IsCancellationRequested) { Result.TrySetCanceled(cancellation); return; }
            try { Result.TrySetResult(action()); } catch (Exception e) { Result.TrySetException(e); }
        }
        public void Fail(Exception error) => Result.TrySetException(error);
    }
    /// <summary>Cancellation prevents queued work from starting. Once a synchronous mutation starts, its result is observed so callers can clean it up.</summary>
    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var item = new Work<T>(action, cancellation);
        await work.Writer.WriteAsync(item, cancellation).ConfigureAwait(false);
        return await item.Result.Task.ConfigureAwait(false);
    }
    public Task InvokeAsync(Action action, CancellationToken cancellation = default) => InvokeAsync(() => { action(); return true; }, cancellation);
    async Task Run()
    {
        Exception? failure = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            long previous = Stopwatch.GetTimestamp();
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                long now = Stopwatch.GetTimestamp(); double seconds = Stopwatch.GetElapsedTime(previous, now).TotalSeconds; previous = now;
                for (int i = 0; i < 64 && !stop.IsCancellationRequested && work.Reader.TryRead(out var item); i++) item.Execute();
                if (runtime.Faulted) throw new InvalidOperationException("The authoritative runtime has faulted.");
                if (!stop.IsCancellationRequested && !runtime.World.HasPendingDurableMutation) { runtime.Advance(seconds); runtime.Synchronize(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { failure = e; throw; }
        finally
        {
            work.Writer.TryComplete(failure); stop.Cancel();
            while (work.Reader.TryRead(out var item)) item.Fail(failure ?? new ObjectDisposedException(nameof(ServerWorldLoop)));
        }
    }
    public ValueTask DisposeAsync() { lock (lifecycle) return new(shutdown ??= StopAsync()); }
    async Task StopAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        try { await Completion.ConfigureAwait(false); } finally { stop.Dispose(); }
    }
}
