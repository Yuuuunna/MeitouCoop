using System.Diagnostics;

namespace Meitou.Diagnostics;

/// <summary>
/// Host-owned runtime diagnostics. The stable sink lets existing observers follow live switches without rebinding the world.
/// </summary>
public sealed class DiagnosticSession : ILatencyTraceSink, IDisposable
{
    /// <summary>
    /// Installed process session, absent in tools that do not opt into the host lifecycle.
    /// </summary>
    public static DiagnosticSession? Current { get; private set; }
    /// <summary>
    /// Bounded structured event, health and console histories.
    /// </summary>
    public DiagnosticRecorder Recorder { get; } = new();
    /// <summary>
    /// Durable incident outbox and recovery packages.
    /// </summary>
    public DiagnosticStore Store { get; }
    /// <summary>
    /// Restart-specific identity used independently of a report's cross-host ID.
    /// </summary>
    public Guid Run { get; } = Guid.NewGuid();
    /// <summary>
    /// Latest collection/storage error, with no paths or secrets.
    /// </summary>
    public string? Failure { get; private set; }
    /// <summary>
    /// Sidecars may skip their work when neither runtime capture nor an explicit trace file is enabled.
    /// </summary>
    public bool Enabled => Recorder.Enabled || _previous is { Enabled: true };
    /// <summary>
    /// Invalidates correlations spanning a runtime switch.
    /// </summary>
    public long Epoch => Recorder.Epoch;
    /// <summary>
    /// Process role; a server process cannot accidentally answer a client upload request in an embedded host.
    /// </summary>
    public string Role => _role;
    /// <summary>
    /// Log path selected by the local host, never supplied by a server request.
    /// </summary>
    public string? RuntimeLogPath { get; init; }
    /// <summary>
    /// Client or server artifact role and build identity.
    /// </summary>
    readonly string _role, _build;
    /// <summary>
    /// Preexisting explicit latency trace remains independently functional.
    /// </summary>
    readonly ILatencyTraceSink? _previous;
    /// <summary>
    /// Console writers restored on orderly disposal.
    /// </summary>
    readonly TextWriter _output, _error;
    /// <summary>
    /// Stops and joins background sampling before restoring global hooks.
    /// </summary>
    readonly CancellationTokenSource _stop = new();
    /// <summary>
    /// Sampling/control polling worker; no filesystem IO occurs on the owner loop.
    /// </summary>
    readonly Task _worker;
    /// <summary>
    /// Serializes incident and recovery writes without blocking gameplay emitters.
    /// </summary>
    readonly SemaphoreSlim _capture = new(1, 1);
    /// <summary>
    /// Serializes runtime switches with background preference reads.
    /// </summary>
    readonly object _controlGate = new();
    /// <summary>
    /// Owns interval aggregation independently of host lifecycle and storage.
    /// </summary>
    readonly DiagnosticHealthSampler _health;

    /// <summary>
    /// Installs capture after existing console and explicit latency sinks have been initialized.
    /// </summary>
    public DiagnosticSession(string role, string build, string root)
    {
        if (role is not ("client" or "server")) throw new ArgumentException("Invalid diagnostic role.", nameof(role));
        _role = role;
        _build = build;
        Store = new(root);
        _health = new(Recorder);
        _previous = LatencyTrace.Sink;
        _output = Console.Out;
        _error = Console.Error;
        PollControl();
        LatencyTrace.Sink = this;
        Console.SetOut(new DiagnosticConsoleWriter(_output, Recorder));
        Console.SetError(new DiagnosticConsoleWriter(_error, Recorder));
        Current = this;
        _worker = Task.Run(SampleLoop);
    }

    /// <summary>
    /// Enables or disables collection immediately and persists the preference for the launcher and next run.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        lock (_controlGate)
        {
            DiagnosticControl.Write(Store.Root, enabled);
            Recorder.Enabled = enabled;
        }
    }

    /// <summary>
    /// Fans out existing trace events; diagnostics never change their IDs, timing or gameplay ordering.
    /// </summary>
    public void Emit(in LatencyTraceEvent value)
    {
        Recorder.Emit(value);
        LatencyTrace.Emit(_previous, value);
    }

    /// <summary>
    /// Called once per presented game iteration, including menus; no work is retained while disabled.
    /// </summary>
    public void Frame(long tick = 0, double presentationTick = 0) => _health.Frame(tick, presentationTick);

    /// <summary>
    /// Samples server owner-loop costs and backlog without reading mutable world state on another thread.
    /// </summary>
    public void ServerPass(long tick, double workMilliseconds, double tickMilliseconds, int queue, double debt) =>
        _health.ServerPass(tick, workMilliseconds, tickMilliseconds, queue, debt);

    /// <summary>
    /// Freezes evidence synchronously before compression is scheduled; callers can attach it to a durable report later.
    /// </summary>
    public DiagnosticSnapshot Snapshot(Guid report, DiagnosticScope scope, string serverCapture) =>
        Recorder.Capture(report, Run, _role, _build, scope, serverCapture);

    /// <summary>
    /// Writes a frozen report off the caller's thread, serialized with the periodic recovery writer.
    /// </summary>
    public async Task<string> SaveAsync(DiagnosticSnapshot snapshot, byte[]? screenshot = null, byte[]? runtimeLog = null)
    {
        await _capture.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Store.Save(snapshot, screenshot, runtimeLog)).ConfigureAwait(false);
        }
        finally
        {
            _capture.Release();
        }
    }

    /// <summary>
    /// Polls only the switch while disabled; sampling and recovery resume without recreating observers.
    /// </summary>
    async Task SampleLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        using var process = Process.GetCurrentProcess();
        int seconds = 14;
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                PollControl();
                if (!Recorder.Enabled) continue;
                try
                {
                    process.Refresh();
                    _health.Sample(process);
                    if (++seconds % 15 == 0) await SaveRecovery().ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Failure = error.GetType().Name;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Control read failures are visible but cannot terminate the game or server.
    /// </summary>
    void PollControl()
    {
        try
        {
            lock (_controlGate)
            {
                bool enabled = DiagnosticControl.Read(Store.Root);
                if (enabled != Recorder.Enabled) Recorder.Enabled = enabled;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Failure = error.GetType().Name;
        }
    }

    /// <summary>
    /// Preserves a bounded previous-run snapshot; recovery is local and is not uploaded automatically.
    /// </summary>
    async Task SaveRecovery()
    {
        if (!await _capture.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (Recorder.Enabled) Store.Recovery(Snapshot(Run, new(), "recovery-unpaired"));
        }
        finally
        {
            _capture.Release();
        }
    }

    /// <summary>
    /// Joins the worker and restores preexisting console and trace hooks.
    /// </summary>
    public void Dispose()
    {
        _stop.Cancel();
        _worker.GetAwaiter().GetResult();
        if (ReferenceEquals(LatencyTrace.Sink, this)) LatencyTrace.Sink = _previous;
        Console.SetOut(_output);
        Console.SetError(_error);
        if (ReferenceEquals(Current, this)) Current = null;
        _stop.Dispose();
    }
}
