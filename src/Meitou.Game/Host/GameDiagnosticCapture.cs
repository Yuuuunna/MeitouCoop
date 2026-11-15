using Meitou.Diagnostics;
using Meitou.Game.Ui;
using Meitou.Networking;

namespace Meitou.Game.Host;

/// <summary>
/// Connects the feedback form to private durable diagnostics and matching server evidence.
/// </summary>
sealed class GameDiagnosticCapture(GameHost host)
{
    /// <summary>
    /// One immutable report per form; retries reuse the saved artifact rather than recapturing a different incident.
    /// </summary>
    Task<string>? _report;
    /// <summary>
    /// Whether the completed report status was displayed.
    /// </summary>
    bool _shown;

    /// <summary>
    /// Publicly shareable correlation only; the private artifact remains access-controlled.
    /// </summary>
    public Guid Report { get; private set; }

    /// <summary>
    /// Allows a new incident after reopening the form, without losing an in-flight report.
    /// </summary>
    public void Open()
    {
        if (_report is { IsCompleted: false }) return;
        _report = null;
        Report = Guid.Empty;
        _shown = false;
    }

    /// <summary>
    /// Samples the active frame and surfaces the completed private capture independently of public feedback uploads.
    /// </summary>
    public void Tick()
    {
        if (DiagnosticSession.Current is { Recorder.Enabled: true } diagnostics)
        {
            long tick = host.Session.HasWorldView ? host.Session.CurrentSnapshot.Tick : 0;
            double presented = host.Session is RemotePlayerSession remote ? remote.Connection.PresentationTick : tick;
            diagnostics.Frame(tick, presented);
            if (host.Session is RemotePlayerSession online) online.Connection.SampleDiagnostics();
        }

        if (_shown || _report is not { IsCompleted: true }) return;
        _shown = true;
        host.Player.FeedbackPanel.DiagnosticStatus = _report.Result;
        Console.WriteLine("diagnostic " + _report.Result);
    }

    /// <summary>
    /// Freezes client evidence immediately and starts bounded paired collection off the frame thread.
    /// </summary>
    public void Start(FeedbackForm form, Task<byte[]>? screenshot)
    {
        if (_report is not null || DiagnosticSession.Current is not { } diagnostics) return;
        var remote = (host.Session as RemotePlayerSession)?.Connection;
        var identity = remote?.State.Admission.Identity;
        var scope = identity is null ? new DiagnosticScope() : new(identity.World, identity.Player.Value, identity.Generation);
        var snapshot = diagnostics.Snapshot(Guid.NewGuid(), scope, remote is null ? "local-session" : "pending") with
        {
            Description = form.Title + "\n" + form.Body,
            Mods = Data.LoadOrder.FromInstall(host.Install).Entries.Select(e => e.Name).ToArray()
        };
        Report = snapshot.Report;
        host.Player.FeedbackPanel.DiagnosticStatus = $"Collecting report {snapshot.Report:N}...";
        _report = Task.Run(() => Capture(diagnostics, snapshot, remote, form.Screenshot ? screenshot : null));
    }

    /// <summary>
    /// Saves before attempting HTTP; missing server evidence is explicit and never prevents a useful client report.
    /// </summary>
    static async Task<string> Capture(DiagnosticSession diagnostics, DiagnosticSnapshot snapshot, RemoteGameSession? remote, Task<byte[]>? screenshot)
    {
        try
        {
            string server = snapshot.ServerCapture;
            if (remote is not null) server = await CaptureServer(remote, snapshot.Report).ConfigureAwait(false);
            byte[]? png = await OptionalScreenshot(screenshot).ConfigureAwait(false);
            string path = await diagnostics.SaveAsync(snapshot with { ServerCapture = server }, png).ConfigureAwait(false);
            string summary = $"Report {snapshot.Report:N} saved; server: {server}.";
            return summary + await Submit(diagnostics.Store, path).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return "Diagnostic capture failed: " + error.GetType().Name + ". Existing logs remain available.";
        }
    }

    /// <summary>
    /// A failed optional picture never prevents preserving the incident evidence.
    /// </summary>
    static async Task<byte[]?> OptionalScreenshot(Task<byte[]>? screenshot)
    {
        if (screenshot is null) return null;
        try
        {
            return await screenshot.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Upload failure is independent of successful local capture and leaves a retryable artifact.
    /// </summary>
    static async Task<string> Submit(DiagnosticStore store, string path)
    {
        try
        {
            if (DiagnosticStore.Collector is not { } collector) return " Export or submit from the launcher's Diagnostics panel.";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await store.SubmitAsync(path, collector, http).ConfigureAwait(false);
            return " Uploaded privately.";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return " Upload pending; retry from the launcher.";
        }
    }

    /// <summary>
    /// Requests only the bound connection's evidence, with a short timeout and explicit missing/disabled classification.
    /// </summary>
    static async Task<string> CaptureServer(RemoteGameSession remote, Guid report)
    {
        if (remote.State.Disconnection is not null) return "disconnected";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var reply = await remote.CaptureDiagnosticsAsync(report, timeout.Token).ConfigureAwait(false);
            return reply.Status.ToString();
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
        {
            return error is OperationCanceledException ? "timeout" : "unavailable";
        }
    }
}
