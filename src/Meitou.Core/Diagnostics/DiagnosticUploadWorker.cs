namespace Meitou.Diagnostics;

/// <summary>
/// Operator-configured server outbox delivery, independent of sampling and gameplay.
/// </summary>
public sealed class DiagnosticUploadWorker : IDisposable
{
    /// <summary>
    /// Cancels HTTP and retry waits during host shutdown.
    /// </summary>
    readonly CancellationTokenSource _stop = new();
    /// <summary>
    /// Single serialized uploader; failed packages remain in the outbox.
    /// </summary>
    readonly Task _worker;

    /// <summary>
    /// Starts delivery only when an HTTPS collector was explicitly configured.
    /// </summary>
    public DiagnosticUploadWorker(DiagnosticStore store)
    {
        try
        {
            _worker = DiagnosticStore.CollectorFor(store.Root) is { } collector ? Task.Run(() => Run(store, collector)) : Task.CompletedTask;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Diagnostic collector configuration unavailable: " + error.GetType().Name);
            _worker = Task.CompletedTask;
        }
    }

    /// <summary>
    /// Retries every thirty seconds, including previously requested reports when new collection is disabled.
    /// </summary>
    async Task Run(DiagnosticStore store, Uri collector)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    foreach (string path in store.Pending()) await store.SubmitAsync(path, collector, http, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
                {
                    if (_stop.IsCancellationRequested) break;
                    Console.Error.WriteLine("Diagnostic upload pending: " + error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Stops delivery before disposing the host.
    /// </summary>
    public void Dispose()
    {
        _stop.Cancel();
        _worker.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
