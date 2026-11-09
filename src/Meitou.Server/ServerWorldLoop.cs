using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    sealed class Work<T>(Func<T> action, CancellationToken cancellation, string origin) : IWork
    {
        readonly long queued = Stopwatch.GetTimestamp();
        public TaskCompletionSource<T> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Execute()
        {
            if (cancellation.IsCancellationRequested) { Result.TrySetCanceled(cancellation); return; }
            long started = Stopwatch.GetTimestamp();
            try { Result.TrySetResult(action()); } catch (Exception e) { Result.TrySetException(e); }
            finally
            {
                double queueMs = Stopwatch.GetElapsedTime(queued, started).TotalMilliseconds;
                double executeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (executeMs >= 100 || queueMs >= 500)
                    Console.WriteLine($"server    {DateTime.UtcNow:O} slow owner job: origin={origin} queueMs={queueMs:F0} executeMs={executeMs:F0}");
            }
        }
        public void Fail(Exception error) => Result.TrySetException(error);
    }
    /// <summary>Cancellation prevents queued work from starting. Once a synchronous mutation starts, its result is observed so callers can clean it up.</summary>
    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellation = default,
        [CallerFilePath] string source = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        ArgumentNullException.ThrowIfNull(action);
        var item = new Work<T>(action, cancellation, $"{Path.GetFileName(source)}:{line}/{member}");
        await work.Writer.WriteAsync(item, cancellation).ConfigureAwait(false);
        return await item.Result.Task.ConfigureAwait(false);
    }
    public Task InvokeAsync(Action action, CancellationToken cancellation = default,
        [CallerFilePath] string source = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0) =>
        InvokeAsync(() => { action(); return true; }, cancellation, source, member, line);
    async Task Run()
    {
        Exception? failure = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            Task<bool> timerReady = timer.WaitForNextTickAsync(stop.Token).AsTask();
            Task<bool> workReady = work.Reader.WaitToReadAsync(stop.Token).AsTask();
            long previous = Stopwatch.GetTimestamp();
            while (!stop.IsCancellationRequested)
            {
                // Queued owner work wakes immediately; it need not wait another scheduler interval
                // for each phase of capture/encode/commit or command/ACK dispatch.
                await Task.WhenAny(timerReady, workReady).ConfigureAwait(false);
                if (timerReady.IsCompleted)
                {
                    if (!await timerReady.ConfigureAwait(false)) break;
                    timerReady = timer.WaitForNextTickAsync(stop.Token).AsTask();
                }
                if (workReady.IsCompleted && !await workReady.ConfigureAwait(false)) break;
                long now = Stopwatch.GetTimestamp(); double seconds = Stopwatch.GetElapsedTime(previous, now).TotalSeconds; previous = now;
                for (int i = 0; i < 64 && !stop.IsCancellationRequested && work.Reader.TryRead(out var item); i++) item.Execute();
                if (workReady.IsCompleted) workReady = work.Reader.WaitToReadAsync(stop.Token).AsTask();
                long tickStarted = Stopwatch.GetTimestamp();
                if (runtime.Faulted) throw new InvalidOperationException("The authoritative runtime has faulted.");
                // Service commands and ACKs between owed ticks instead of monopolizing the owner
                // for eight expensive simulation steps. Advance retains all remaining time debt.
                if (!stop.IsCancellationRequested && !runtime.World.HasPendingDurableMutation)
                {
                    // RunTick already synchronizes runtime services at its boundary.
                    if (runtime.Advance(seconds, maxTicks: 1) == 0) runtime.Synchronize();
                }
                double workMs = Stopwatch.GetElapsedTime(now, tickStarted).TotalMilliseconds;
                double tickMs = Stopwatch.GetElapsedTime(tickStarted).TotalMilliseconds;
                if (workMs >= 500 || tickMs >= 500)
                    Console.WriteLine($"server    {DateTime.UtcNow:O} slow owner pass: workMs={workMs:F0} tickMs={tickMs:F0} tick={runtime.World.Tick} debtSeconds={runtime.PendingGameSeconds:F2}");
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
