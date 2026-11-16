using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// Dedicated server selection and console controls, with state stored separately from client launches.
/// </summary>
public sealed partial class ServerPanel : UserControl
{
    /// <summary>
    /// The sole hosted process; client Play and Connect have independent lifetimes.
    /// </summary>
    readonly ServerProcess _server = new();

    /// <summary>
    /// Refreshes bounded output on the UI thread instead of posting one UI operation for every console line.
    /// </summary>
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>
    /// The window's persistent settings.
    /// </summary>
    LauncherConfig? _config;

    /// <summary>
    /// Reads the currently selected content installation when starting, including later changes in Settings.
    /// </summary>
    Func<GameInstall?>? _install;

    /// <summary>
    /// Disables repeated shutdown commands while the server saves and stops.
    /// </summary>
    bool _stopping;

    /// <summary>
    /// Prevents repeated submissions until the redirected command has been flushed.
    /// </summary>
    bool _sending;

    /// <summary>
    /// Keeps the launcher open while it owns the server's console and shutdown controls.
    /// </summary>
    internal bool IsRunning => _server.IsRunning;

    /// <summary>
    /// Builds the controls and refreshes output only while attached to a window.
    /// </summary>
    public ServerPanel()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => RefreshProcess();
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Restores the dedicated paths without launching a process or changing the client selection.
    /// </summary>
    internal void Configure(LauncherConfig config, Func<GameInstall?> install)
    {
        _config = config;
        _install = install;
        ServerPathBox.Text = config.HostedServerPath ?? "";
        ConfigPathBox.Text = config.HostedServerConfig;
    }

    /// <summary>
    /// Chooses an extracted server or build folder.
    /// </summary>
    async void BrowseServer_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Dedicated server folder" });
            if (folders is [var folder] && folder.TryGetLocalPath() is { } path) ServerPathBox.Text = path;
        }
        catch (Exception error) when (IsUserError(error))
        {
            ShowProblem(error.Message);
        }
    }

    /// <summary>
    /// Chooses an existing configuration, preserving its world and account storage locations.
    /// </summary>
    async void BrowseConfig_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Server configuration" });
            if (files is [var file] && file.TryGetLocalPath() is { } path) ConfigPathBox.Text = path;
        }
        catch (Exception error) when (IsUserError(error))
        {
            ShowProblem(error.Message);
        }
    }

    /// <summary>
    /// Searches for an existing Release or Debug server build only on request.
    /// </summary>
    void FindCheckout_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ServerTarget.FindCheckout() is not { } path)
                throw new InvalidOperationException("Build Meitou.Server first, or browse to an extracted server package.");

            ServerPathBox.Text = path;
            ShowProblem("");
        }
        catch (Exception error) when (IsUserError(error))
        {
            ShowProblem(error.Message);
        }
    }

    /// <summary>
    /// Validates and saves the selection before starting the dedicated process with its own configuration and log.
    /// </summary>
    void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (_config is null || IsRunning) return;

        try
        {
            var install = _install?.Invoke() ?? throw new InvalidOperationException("Set the Kenshi folder first (Settings).");
            var target = SelectedTarget();
            _config.HostedServerPath = target.Exe;
            _config.HostedServerConfig = target.ConfigPath;
            _config.Save();
            _stopping = false;
            _server.Start(target, install);
            ShowProblem("");
        }
        catch (Exception error) when (IsUserError(error))
        {
            ShowProblem(error.Message);
        }

        RefreshProcess();
    }

    /// <summary>
    /// Requests a checkpoint through the running server's console.
    /// </summary>
    async void Save_Click(object? sender, RoutedEventArgs e) => await SendCommandAsync("save");

    /// <summary>
    /// Requests normal shutdown; never kills a process that may be writing its checkpoint.
    /// </summary>
    async void Stop_Click(object? sender, RoutedEventArgs e) => await SendCommandAsync("stop");

    /// <summary>
    /// Submits a typed console command through the adjacent button.
    /// </summary>
    async void Command_Click(object? sender, RoutedEventArgs e) => await SubmitCommandAsync();

    /// <summary>
    /// Submits a single command on Enter without inserting a newline.
    /// </summary>
    async void Command_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        await SubmitCommandAsync();
    }

    /// <summary>
    /// Clears the command field only after a successful write, leaving failed input available for retry.
    /// </summary>
    async Task SubmitCommandAsync()
    {
        string command = ServerCommandBox.Text?.Trim() ?? "";
        if (command.Length == 0 || !_server.Ready) return;

        if (await SendCommandAsync(command)) ServerCommandBox.Text = "";
    }

    /// <summary>
    /// Keeps command errors visible and the Stop control available after a failed write.
    /// </summary>
    async Task<bool> SendCommandAsync(string command)
    {
        if (!IsRunning || _stopping || _sending) return false;

        try
        {
            _sending = true;
            _stopping = command.Equals("stop", StringComparison.OrdinalIgnoreCase);
            RefreshProcess();
            await _server.CommandAsync(command);
            ShowProblem("");
            return true;
        }
        catch (Exception error) when (IsUserError(error))
        {
            _stopping = false;
            ShowProblem(error.Message);
            return false;
        }
        finally
        {
            _sending = false;
            RefreshProcess();
        }
    }

    /// <summary>
    /// Updates process state and output without claiming readiness before the server reports a listener.
    /// </summary>
    void RefreshProcess()
    {
        bool running = IsRunning;
        StartServerButton.IsEnabled = !running;
        ServerSettings.IsEnabled = !running;
        OpenConfigButton.IsEnabled = !running;
        SaveServerButton.IsEnabled = running && _server.Ready && !_stopping && !_sending;
        StopServerButton.IsEnabled = running && !_stopping && !_sending;
        ServerCommandControls.IsEnabled = running && _server.Ready && !_stopping && !_sending;
        ServerStatusText.Text = ProcessStatus();
        string output = _server.Output;
        if (ServerOutputBox.Text == output) return;

        ServerOutputBox.Text = output;
        ServerOutputBox.CaretIndex = output.Length;
    }

    /// <summary>
    /// Describes startup, readiness, shutdown and error exit separately.
    /// </summary>
    string ProcessStatus()
    {
        if (!IsRunning)
            return _server.ExitCode is { } code ? LauncherText.Translate("Server exited with code {1}.", code) : "";
        if (_stopping) return LauncherText.Translate("Stopping server; waiting for world save…");

        return LauncherText.Translate(_server.Ready
            ? "Server is listening. See console for endpoints."
            : "Starting headless server; loading content…");
    }

    /// <summary>
    /// Resolves the editable selection for a new launch.
    /// </summary>
    ServerTarget SelectedTarget() => ServerTarget.Resolve(ServerPathBox.Text ?? "", ConfigPathBox.Text ?? "");

    /// <summary>
    /// Opens an existing configuration for editing while the server is stopped; the server owns its schema and defaults.
    /// </summary>
    async void OpenConfig_Click(object? sender, RoutedEventArgs e)
    {
        if (IsRunning) return;

        try
        {
            string path = ConfigPathBox.Text?.Trim().Trim('"') ?? "";
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a server configuration file.");

            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Start the server first to create its configuration.");

            await OpenPathAsync(path);
        }
        catch (Exception error) when (IsUserError(error))
        {
            ShowProblem(error.Message);
        }
    }

    /// <summary>
    /// Opens the actual running or last launched server's log, retaining access after an error exit.
    /// </summary>
    async void OpenLog_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string path = (_server.Target ?? SelectedTarget()).LogPath;
            if (!File.Exists(path)) throw new FileNotFoundException("Start the server first to create its console log.");

            await OpenPathAsync(path);
        }
        catch (Exception error) when (IsUserError(error))
        {
            ShowProblem(error.Message);
        }
    }

    /// <summary>
    /// Uses the desktop's file association without passing paths through a shell command.
    /// </summary>
    async Task OpenPathAsync(string path)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        if (!await top.Launcher.LaunchUriAsync(new Uri(path))) throw new IOException("Could not open the selected server path.");
    }

    /// <summary>
    /// Displays recoverable errors without replacing process status or console output.
    /// </summary>
    void ShowProblem(string message) => ServerProblemText.Text = LauncherText.Translate(message);

    /// <summary>
    /// Recoverable path, runtime, desktop-launch and console failures.
    /// </summary>
    static bool IsUserError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException;
}
