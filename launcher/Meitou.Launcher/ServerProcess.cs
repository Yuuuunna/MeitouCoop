using System.Diagnostics;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// Owns one dedicated server, drains both console streams and requests graceful saving and shutdown through standard input.
/// </summary>
sealed class ServerProcess
{
    /// <summary>
    /// Serializes console capture from the two output readers.
    /// </summary>
    readonly object _gate = new();

    /// <summary>
    /// Keeps each redirected console command and its flush together when controls submit concurrently.
    /// </summary>
    readonly SemaphoreSlim _commands = new(1, 1);

    /// <summary>
    /// The last console lines, bounded independently of the full disk log.
    /// </summary>
    readonly Queue<string> _lines = new();

    /// <summary>
    /// The process retained until its readers finish and a later launch replaces it.
    /// </summary>
    Process? _process;

    /// <summary>
    /// Completion includes the process exit, output drain and log closure.
    /// </summary>
    Task? _completion;

    /// <summary>
    /// Whether this launcher still owns a live server or its remaining output.
    /// </summary>
    public bool IsRunning => _completion is { IsCompleted: false };

    /// <summary>
    /// The last process's exit code, available after completion.
    /// </summary>
    public int? ExitCode { get; private set; }

    /// <summary>
    /// True after the server reports its listening endpoints; process start alone does not imply readiness.
    /// </summary>
    public bool Ready { get; private set; }

    /// <summary>
    /// The actual launch target; editing the page never redirects a running process's commands or log.
    /// </summary>
    public ServerTarget? Target { get; private set; }

    /// <summary>
    /// Takes a consistent console snapshot for the UI's periodic refresh.
    /// </summary>
    public string Output
    {
        get
        {
            lock (_gate) return string.Join(Environment.NewLine, _lines);
        }
    }

    /// <summary>
    /// Starts only a dedicated executable and appends a timestamped session to its persistent log.
    /// </summary>
    public void Start(ServerTarget target, GameInstall install)
    {
        if (IsRunning) throw new InvalidOperationException("The hosted server is already running.");

        var start = target.CreateStartInfo(install);
        var log = new StreamWriter(target.LogPath, append: true) { AutoFlush = true };
        try
        {
            _process?.Dispose();
            _process = Process.Start(start) ?? throw new InvalidOperationException("The dedicated server did not start.");
        }
        catch
        {
            log.Dispose();
            throw;
        }

        Target = target;
        ExitCode = null;
        Ready = false;
        lock (_gate) _lines.Clear();

        Capture($"[{DateTimeOffset.Now:O}] {target.Exe} " + "--config " + target.ConfigPath, log);
        _completion = ObserveAsync(_process, log);
    }

    /// <summary>
    /// Sends a save or graceful shutdown command through the same console as manually entered commands.
    /// </summary>
    public Task CommandAsync(bool stop) => CommandAsync(stop ? "stop" : "save");

    /// <summary>
    /// Sends one console line without shell interpretation; a missing or exited server is reported to the caller.
    /// </summary>
    public async Task CommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Contains('\r') || command.Contains('\n'))
            throw new ArgumentException("Enter one console command.", nameof(command));

        await _commands.WaitAsync();
        try
        {
            if (!IsRunning || _process is null || _process.HasExited)
                throw new InvalidOperationException("The hosted server is not running.");

            await _process.StandardInput.WriteLineAsync(command.Trim());
            await _process.StandardInput.FlushAsync();
        }
        finally
        {
            _commands.Release();
        }
    }

    /// <summary>
    /// Drains both streams concurrently, then records exit without exposing an unobserved background exception.
    /// </summary>
    async Task ObserveAsync(Process process, StreamWriter log)
    {
        using (log)
        {
            try
            {
                await Task.WhenAll(ReadAsync(process.StandardOutput, log), ReadAsync(process.StandardError, log), process.WaitForExitAsync())
                    .ConfigureAwait(false);
                ExitCode = process.ExitCode;
                Capture($"[{DateTimeOffset.Now:O}] " + LauncherText.Translate("Exit code: {1}", ExitCode), log);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                Capture(error.Message, log);
                await process.WaitForExitAsync().ConfigureAwait(false);
                ExitCode = process.ExitCode;
            }
        }
    }

    /// <summary>
    /// Reads continuously so a full redirected pipe cannot block simulation startup or shutdown.
    /// </summary>
    async Task ReadAsync(StreamReader reader, StreamWriter log)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) Capture(line, log);
    }

    /// <summary>
    /// Keeps at most 200 lines of 2,000 characters in memory; disk failures never stop draining the process pipes.
    /// </summary>
    void Capture(string line, StreamWriter log)
    {
        lock (_gate)
        {
            if (line.StartsWith("Listening on ", StringComparison.Ordinal)) Ready = true;
            AddLine(line);
            try
            {
                log.WriteLine(line);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AddLine(LauncherText.Translate("Could not write server log: {1}", error.Message));
            }
        }
    }

    /// <summary>
    /// Adds a bounded line while the console gate is held.
    /// </summary>
    void AddLine(string line)
    {
        _lines.Enqueue(line.Length > 2000 ? line[..2000] : line);
        while (_lines.Count > 200) _lines.Dequeue();
    }
}
