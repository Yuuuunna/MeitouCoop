using System.Diagnostics;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// Owns the single game process started by this launcher and the lifetime of its temporary Steam App ID files.
/// </summary>
sealed class GameProcess
{
    /// <summary>
    /// Serializes exit cleanup with the next launch so a late exit cannot delete the new process's App ID files.
    /// </summary>
    readonly object _gate = new();

    /// <summary>
    /// Temporary files belonging to the current process.
    /// </summary>
    List<string> _steamAppIdFiles = [];

    /// <summary>
    /// The latest game process, retained until the next launch so callers can safely inspect HasExited.
    /// </summary>
    public Process? Running { get; private set; }

    /// <summary>
    /// Raised on the process exit thread with the process, exit code and matching log path.
    /// </summary>
    public event Action<Process, int, string>? Exited;

    /// <summary>
    /// Creates the game's process environment; existing multiplayer identity settings are inherited unchanged.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(GameTarget target, GameInstall install)
    {
        var start = target.CreateStartInfo();
#if DEBUG
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
#endif
        start.Environment[GameInstall.EnvironmentVariable] = install.Root;
        start.Environment.Remove("MEITOU_METAL_GPU_CULL");
        start.Environment.Remove("MEITOU_METAL_GPU_BUDGET_MB");
        return start;
    }

    /// <summary>
    /// Starts the selected executable, registers exit tracking and removes temporary files if starting fails.
    /// </summary>
    public void Start(ProcessStartInfo start, GameTarget target, GameInstall install, bool launchAsSteamKenshi)
    {
        lock (_gate)
        {
            if (Running is { HasExited: false }) throw new InvalidOperationException("Meitou is already running.");

            SteamAppIdFiles.RemoveAll(_steamAppIdFiles);
            Running?.Dispose();
            Running = null;
            StartPrepared(start, target, install, launchAsSteamKenshi);
        }
    }

    /// <summary>
    /// Registers temporary files and exit notification while holding the launch gate.
    /// </summary>
    void StartPrepared(ProcessStartInfo start, GameTarget target, GameInstall install, bool launchAsSteamKenshi)
    {
        bool launched = false;
        try
        {
            _steamAppIdFiles = SteamAppIdFiles.SetUp(start, target.Exe, install, launchAsSteamKenshi);
            var game = Process.Start(start) ?? throw new InvalidOperationException("The game did not start.");
            Running = game;
            launched = true;
            game.Exited += (_, _) => GameExited(game, target.LogPath);
            game.EnableRaisingEvents = true;
#if DEBUG
            ForwardOutput(game);
#endif
        }
        finally
        {
            if (!launched) SteamAppIdFiles.RemoveAll(_steamAppIdFiles);
        }
    }

    /// <summary>
    /// Cleans up this launch's files before notifying the window of completion.
    /// </summary>
    void GameExited(Process game, string log)
    {
        int code;
        lock (_gate)
        {
            if (Running != game) return;

            code = game.ExitCode;
            SteamAppIdFiles.RemoveAll(_steamAppIdFiles);
            _steamAppIdFiles = [];
        }

        Exited?.Invoke(game, code, log);
    }

#if DEBUG
    /// <summary>
    /// Forwards the game's redirected output to the development console and debugger.
    /// </summary>
    static void ForwardOutput(Process game)
    {
        game.OutputDataReceived += (_, data) => ForwardLine(data, Console.Out);
        game.ErrorDataReceived += (_, data) => ForwardLine(data, Console.Error);
        game.BeginOutputReadLine();
        game.BeginErrorReadLine();
    }

    /// <summary>
    /// Writes one tagged child-process line.
    /// </summary>
    static void ForwardLine(DataReceivedEventArgs data, TextWriter console)
    {
        if (data.Data is not { } line) return;

        console.WriteLine($"[Meitou] {line}");
        Debug.WriteLine($"[Meitou] {line}");
    }
#endif
}
