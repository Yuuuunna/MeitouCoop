namespace Meitou.Diagnostics;

/// <summary>Explicit host lifetime for opt-in tracing. No IO is performed by emission callers.</summary>
public sealed class LatencyTraceSession : IDisposable, IAsyncDisposable
{
    readonly BoundedLatencyTraceWriter? writer;
    readonly ILatencyTraceSink? previous;
    LatencyTraceSession(BoundedLatencyTraceWriter? writer)
    {
        this.writer = writer; previous = LatencyTrace.Sink;
        if (writer is not null) LatencyTrace.Sink = writer;
    }

    public static LatencyTraceSession FromEnvironment()
    {
        string? destination = Environment.GetEnvironmentVariable("MEITOU_LATENCY_TRACE");
        if (string.IsNullOrWhiteSpace(destination)) return new(null);
        try
        {
            string? grouping = Environment.GetEnvironmentVariable("MEITOU_LATENCY_CASE_ID");
            Guid caseId = string.IsNullOrWhiteSpace(grouping) ? Guid.Empty : Guid.Parse(grouping);
            string path = Path.GetFullPath(destination);
            if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(path);
                path = Path.Combine(path, $"latency-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
            }
            else Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new(new BoundedLatencyTraceWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                65536, FileOptions.Asynchronous), caseId: caseId));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Latency trace could not start: " + error.GetType().Name);
            return new(null);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        if (writer is null) return;
        if (ReferenceEquals(LatencyTrace.Sink, writer)) LatencyTrace.Sink = previous;
        try { await writer.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { Console.Error.WriteLine("Latency trace could not close: " + error.GetType().Name); }
        if (writer.Failure is not null) Console.Error.WriteLine("Latency trace writer failed; capture is incomplete: " + writer.Failure.GetType().Name);
    }
}
