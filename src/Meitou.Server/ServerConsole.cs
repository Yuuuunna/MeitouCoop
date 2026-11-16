using Meitou.Diagnostics;

namespace Meitou.Server;

/// <summary>
/// Reads operator commands from standard input, including redirected launcher input, for one server lifetime.
/// </summary>
sealed class ServerConsole(ServerGameSpeed speed, DiagnosticSession diagnostics, ServerDiagnosticCollection collection,
    Func<Task> save, CancellationTokenSource stop)
{
    /// <summary>
    /// Available operator commands, also printed for unknown input.
    /// </summary>
    public const string Help = "Commands: help; save; stop; " + ServerGameSpeed.Usage
        + "; diagnostics on | off | status | capture | collect-all.";

    /// <summary>
    /// Reads until EOF or shutdown; a command failure is reported without disabling subsequent commands.
    /// </summary>
    public async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // Native console input can ignore cancellation; cancel its wait so shutdown can join active commands.
                string? line = await Task.Run(Console.ReadLine).WaitAsync(stop.Token).ConfigureAwait(false);
                if (line is null) return;

                await ExecuteSafelyAsync(line).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            Console.Error.WriteLine("Console input failed: " + error);
        }
    }

    /// <summary>
    /// Keeps recoverable command failures, including checkpoint IO failures, from ending console input.
    /// </summary>
    async Task ExecuteSafelyAsync(string line)
    {
        try
        {
            await ExecuteAsync(line).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Console command failed: " + error);
        }
    }

    /// <summary>
    /// Dispatches one complete command; blank lines do nothing and invalid arguments print usage.
    /// </summary>
    async Task ExecuteAsync(string line)
    {
        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        switch (parts[0].ToLowerInvariant())
        {
            case "stop" when parts.Length == 1:
                stop.Cancel();
                return;
            case "save" when parts.Length == 1:
                await save().ConfigureAwait(false);
                return;
            case "diagnostics":
                await ServerDiagnosticConsole.ExecuteAsync(line, diagnostics, collection, stop.Token).ConfigureAwait(false);
                return;
            case "speed":
            case "倍速":
            case "游戏倍速":
                string result = parts.Length > 2 ? ServerGameSpeed.Usage
                    : await speed.ExecuteAsync(parts.Length == 2 ? parts[1] : null, stop.Token).ConfigureAwait(false);
                Console.WriteLine(result);
                return;
            default:
                Console.WriteLine(Help);
                return;
        }
    }
}
