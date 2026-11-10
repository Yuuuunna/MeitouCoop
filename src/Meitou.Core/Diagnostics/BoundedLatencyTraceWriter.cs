using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Meitou.Diagnostics;

/// <summary>Bounded opt-in JSONL writer. TryWrite never waits on IO. Dispose drains and emits loss totals.</summary>
public sealed class BoundedLatencyTraceWriter : ILatencyTraceSink, IAsyncDisposable
{
    readonly Channel<LatencyTraceEvent> queue;
    readonly Stream output;
    readonly Task worker;
    readonly bool leaveOpen;
    readonly Guid run = Guid.NewGuid();
    readonly Guid caseId;
    long attempted, dropped, written;
    int closing;
    int emitting;
    Exception? failure;

    public BoundedLatencyTraceWriter(Stream output, int capacity = 8192, bool leaveOpen = false, Guid caseId = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.output = output; this.leaveOpen = leaveOpen; this.caseId = caseId;
        queue = Channel.CreateBounded<LatencyTraceEvent>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });
        worker = Task.Run(WriteLoop);
    }

    public long Attempted => Interlocked.Read(ref attempted);
    public long Dropped => Interlocked.Read(ref dropped);
    public long Written => Interlocked.Read(ref written);
    public Exception? Failure => failure;

    public void Emit(in LatencyTraceEvent value)
    {
        Interlocked.Increment(ref emitting);
        try
        {
            if (Volatile.Read(ref closing) != 0) return; // outside the capture lifetime
            Interlocked.Increment(ref attempted);
            if (!queue.Writer.TryWrite(value.Timestamp == 0 ? value with { Timestamp = Stopwatch.GetTimestamp() } : value))
                Interlocked.Increment(ref dropped);
        }
        finally { Interlocked.Decrement(ref emitting); }
    }

    async Task WriteLoop()
    {
        bool writingEvent = false;
        try
        {
            await foreach (var value in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                writingEvent = true;
                await Write(value).ConfigureAwait(false);
                writingEvent = false;
                Interlocked.Increment(ref written);
            }
            await Write(new(LatencyStage.TraceSummary, Value: Dropped, Revision: (ulong)Written,
                Request: (ulong)Attempted, Timestamp: Stopwatch.GetTimestamp())).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            if (writingEvent) Interlocked.Increment(ref dropped);
            Interlocked.Exchange(ref closing, 1);
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
        }
    }

    async Task Write(LatencyTraceEvent e)
    {
        using var buffer = new MemoryStream(512);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("schema", 1); json.WriteString("run", run); json.WriteNumber("process", Environment.ProcessId);
            json.WriteString("case", caseId);
            json.WriteNumber("frequency", Stopwatch.Frequency); json.WriteNumber("time", e.Timestamp);
            json.WriteNumber("stage", (int)e.Stage); json.WriteString("world", e.World);
            json.WriteNumber("endpoint", (int)e.Endpoint);
            json.WriteNumber("connection", e.Connection); json.WriteNumber("generation", e.Generation);
            json.WriteNumber("request", e.Request); json.WriteNumber("entity", e.Entity);
            json.WriteString("pathService", e.PathService); json.WriteNumber("path", e.Path);
            json.WriteNumber("publication", e.Publication); json.WriteNumber("tick", e.Tick);
            json.WriteNumber("revision", e.Revision); json.WriteNumber("frame", e.Frame);
            json.WriteNumber("presentationTick", e.PresentationTick); json.WriteNumber("outcome", (int)e.Outcome);
            json.WriteNumber("value", e.Value); json.WriteEndObject(); json.Flush();
        }
        buffer.WriteByte((byte)'\n');
        await output.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref closing, 1);
        while (Volatile.Read(ref emitting) != 0) await Task.Yield();
        queue.Writer.TryComplete();
        await worker.ConfigureAwait(false);
        if (!leaveOpen) await output.DisposeAsync().ConfigureAwait(false);
    }
}
