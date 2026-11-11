using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Meitou.Diagnostics;

/// <summary>Bounded opt-in JSONL writer. TryWrite never waits on IO. Dispose drains and emits loss totals.</summary>
public sealed class BoundedLatencyTraceWriter : ILatencyTraceSink, IAsyncDisposable
{
    readonly Channel<LatencyTraceEvent> _queue;
    readonly Stream _output;
    readonly Task _worker;
    readonly bool _leaveOpen;
    readonly Guid _run = Guid.NewGuid();
    readonly Guid _caseId;
    long _attempted, _dropped, _written;
    int _closing;
    int _emitting;
    Exception? _failure;

    public BoundedLatencyTraceWriter(Stream output, int capacity = 8192, bool leaveOpen = false, Guid caseId = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this._output = output; this._leaveOpen = leaveOpen; this._caseId = caseId;
        _queue = Channel.CreateBounded<LatencyTraceEvent>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });
        _worker = Task.Run(WriteLoop);
    }

    public long Attempted => Interlocked.Read(ref _attempted);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Written => Interlocked.Read(ref _written);
    public Exception? Failure => _failure;

    public void Emit(in LatencyTraceEvent value)
    {
        Interlocked.Increment(ref _emitting);
        try
        {
            if (Volatile.Read(ref _closing) != 0) return; // outside the capture lifetime
            Interlocked.Increment(ref _attempted);
            if (!_queue.Writer.TryWrite(value.Timestamp == 0 ? value with { Timestamp = Stopwatch.GetTimestamp() } : value))
                Interlocked.Increment(ref _dropped);
        }
        finally { Interlocked.Decrement(ref _emitting); }
    }

    async Task WriteLoop()
    {
        bool writingEvent = false;
        try
        {
            await foreach (var value in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                writingEvent = true;
                await Write(value).ConfigureAwait(false);
                writingEvent = false;
                Interlocked.Increment(ref _written);
            }
            await Write(new(LatencyStage.TraceSummary, Value: Dropped, Revision: (ulong)Written,
                Request: (ulong)Attempted, Timestamp: Stopwatch.GetTimestamp())).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _failure = error;
            if (writingEvent) Interlocked.Increment(ref _dropped);
            Interlocked.Exchange(ref _closing, 1);
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
        }
    }

    async Task Write(LatencyTraceEvent e)
    {
        using var buffer = new MemoryStream(512);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("schema", 1); json.WriteString("run", _run); json.WriteNumber("process", Environment.ProcessId);
            json.WriteString("case", _caseId);
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
        await _output.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closing, 1);
        while (Volatile.Read(ref _emitting) != 0) await Task.Yield();
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        if (!_leaveOpen) await _output.DisposeAsync().ConfigureAwait(false);
    }
}
