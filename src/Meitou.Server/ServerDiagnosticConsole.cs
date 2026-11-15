using Meitou.Diagnostics;

namespace Meitou.Server;

/// <summary>
/// Operator-only runtime controls, separate from player capture requests.
/// </summary>
static class ServerDiagnosticConsole
{
    /// <summary>
    /// Handles runtime controls and operator capture commands without requiring a server restart.
    /// </summary>
    public static async Task<bool> ExecuteAsync(string line, DiagnosticSession session, ServerDiagnosticCollection collection, CancellationToken stopping)
    {
        string command = line.Trim().ToLowerInvariant();
        if (!command.StartsWith("diagnostics", StringComparison.Ordinal)) return false;
        try
        {
            if (await Dispatch(command, session, collection, stopping).ConfigureAwait(false))
                Console.WriteLine($"Diagnostics {(session.Recorder.Enabled ? "on" : "off")}; pending={session.Store.Pending().Length}; error={session.Failure ?? "none"}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Diagnostic command failed: " + error.GetType().Name);
        }

        return true;
    }

    /// <summary>
    /// Dispatches a recognized operator action; true asks the entry point to print collection status.
    /// </summary>
    static async Task<bool> Dispatch(string command, DiagnosticSession session, ServerDiagnosticCollection collection, CancellationToken stopping)
    {
        switch (command)
        {
            case "diagnostics on":
            case "diagnostics off":
                session.SetEnabled(command == "diagnostics on");
                return true;
            case "diagnostics capture":
                Guid report = Guid.NewGuid();
                string path = await session.SaveAsync(session.Snapshot(report, new(), "operator")).ConfigureAwait(false);
                Console.WriteLine($"Diagnostic report {report:N}: {path}");
                return false;
            case "diagnostics status":
                Console.WriteLine("Operator collection: " + collection.Status);
                return true;
            case "diagnostics collect-all":
                Console.WriteLine(collection.Start(session, stopping)
                    ? "Online-player diagnostic collection started; progress and output directory will follow."
                    : "An online-player diagnostic batch is already running.");
                return false;
            default:
                Console.WriteLine("diagnostics on | off | status | capture | collect-all");
                return false;
        }
    }
}
