using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Meitou.Connections;

namespace Meitou.Launcher;

/// <summary>
/// Explicit client selection and server history; credentials and server admission remain owned by the game.
/// </summary>
public sealed partial class CoopPage : UserControl
{
    /// <summary>
    /// The settings supplied by the window after the control has been constructed.
    /// </summary>
    LauncherConfig? _config;

    /// <summary>
    /// The executable currently shown, shared with the window's launch and settings paths.
    /// </summary>
    GameTarget? _target;

    /// <summary>
    /// Suppresses selection callbacks while the view reflects the saved configuration.
    /// </summary>
    bool _updating;

    /// <summary>
    /// Called after an explicit client selection or schema refresh.
    /// </summary>
    internal event Action? TargetChanged;

    /// <summary>
    /// Starts the game with a validated address; true means that the process started, not that admission succeeded.
    /// </summary>
    internal Func<string, Task<bool>>? ConnectRequested { get; set; }

    /// <summary>
    /// Builds the standalone Coop page.
    /// </summary>
    public CoopPage() => InitializeComponent();

    /// <summary>
    /// Connects the page to the window's settings.
    /// </summary>
    internal void Configure(LauncherConfig config)
    {
        _config = config;
        _updating = true;
        SourceBox.SelectedIndex = config.UseLocalClient ? 1 : 0;
        LocalPathBox.Text = config.LocalClientPath ?? "";
        AddressBox.Text = config.ServerAddress;
        ShowHistory();
        _updating = false;
    }

    /// <summary>
    /// Shows the same target that Play, graphics settings and hardware detection use.
    /// </summary>
    internal void ShowTarget(GameTarget? target, string? problem)
    {
        _target = target;
        _updating = true;
        SourceBox.SelectedIndex = _config?.UseLocalClient == true ? 1 : 0;
        _updating = false;
        TargetText.Text = target is null ? LauncherText.Translate(problem ?? "")
            : $"{LauncherText.Translate(target.Version)}\n{target.Exe}";
        BuildText.Text = target?.BuildDescription() ?? "";
    }

    /// <summary>
    /// Changes source without silently selecting another executable when the local path is unavailable.
    /// </summary>
    void Source_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _config is null) return;

        _config.UseLocalClient = SourceBox.SelectedIndex == 1;
        SaveSelection();
    }

    /// <summary>
    /// Chooses a folder containing a published client or a checkout build.
    /// </summary>
    async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Client folder" });
            if (folders is not [var folder] || folder.TryGetLocalPath() is not { } path) return;

            LocalPathBox.Text = path;
            await UseLocalAsync();
        }
        catch (Exception error) when (IsClientError(error))
        {
            ShowProblem(error.Message);
        }
    }

    /// <summary>
    /// Applies the typed local path.
    /// </summary>
    async void ApplyLocal_Click(object? sender, RoutedEventArgs e) => await UseLocalAsync();

    /// <summary>
    /// Finds a checkout build only after the user explicitly requests one.
    /// </summary>
    async void FindCheckout_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (GameTarget.FindCheckout() is not { } path)
            {
                ShowProblem("Build the game first, or browse to an exported client.");
                return;
            }

            LocalPathBox.Text = path;
            await UseLocalAsync();
        }
        catch (Exception error) when (IsClientError(error))
        {
            ShowProblem(error.Message);
        }
    }

    /// <summary>
    /// Validates a local executable before persisting it and obtaining its schema.
    /// </summary>
    async Task UseLocalAsync()
    {
        if (_config is null) return;

        try
        {
            string path = Path.GetFullPath(LocalPathBox.Text?.Trim() ?? "");
            var candidate = new LauncherConfig { UseLocalClient = true, LocalClientPath = path };
            if (GameTarget.Resolve(candidate, null) is null) throw new ArgumentException("No Meitou client was found at this path.");

            _config.LocalClientPath = path;
            _config.UseLocalClient = true;
            LocalPathBox.Text = path;
            if (SaveSelection()) await RefreshClientAsync();
        }
        catch (Exception error) when (IsClientError(error))
        {
            ShowProblem(error.Message);
        }
    }

    /// <summary>
    /// Persists a source change and refreshes the window only after the write succeeds.
    /// </summary>
    bool SaveSelection()
    {
        try
        {
            _config?.Save();
            TargetChanged?.Invoke();
            ShowProblem("");
            return true;
        }
        catch (Exception error) when (IsClientError(error))
        {
            ShowProblem(error.Message);
            return false;
        }
    }

    /// <summary>
    /// Re-reads build information and requests a fresh schema from the selected local client.
    /// </summary>
    async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshClientAsync();

    /// <summary>
    /// Updates the local schema without allowing overlapping refreshes.
    /// </summary>
    async Task RefreshClientAsync()
    {
        if (!RefreshButton.IsEnabled) return;

        RefreshButton.IsEnabled = false;
        try
        {
            TargetChanged?.Invoke();
            if (_target is { Local: true } local) await local.RefreshSchemaAsync();

            TargetChanged?.Invoke();
            ShowProblem("");
        }
        catch (Exception error) when (IsClientError(error))
        {
            ShowProblem(error.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Connects with the entered address.
    /// </summary>
    async void Connect_Click(object? sender, RoutedEventArgs e) => await ConnectAsync();

    /// <summary>
    /// Enter in the server field performs the same validated connection action.
    /// </summary>
    async void Address_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        await ConnectAsync();
    }

    /// <summary>
    /// Remembers at most ten server addresses; never loads or copies game credentials.
    /// </summary>
    async Task ConnectAsync()
    {
        if (_config is null || ConnectRequested is null || !ConnectButton.IsEnabled) return;

        ConnectButton.IsEnabled = false;
        try
        {
            string address = ServerAddress.Normalize(AddressBox.Text ?? "");
            ShowProblem("");
            if (!await ConnectRequested(address)) return;

            _config.ServerAddress = address;
            _config.RecentServers = new[] { address }.Concat(_config.RecentServers ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
            _config.Save();
            AddressBox.Text = address;
            ShowHistory();
            ShowProblem("");
        }
        catch (Exception error) when (IsClientError(error))
        {
            ShowProblem(error.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Refills the address history without initiating a connection.
    /// </summary>
    void ShowHistory() => RecentBox.ItemsSource = _config?.RecentServers ?? [];

    /// <summary>
    /// Selecting history fills the address field; connecting remains an explicit action.
    /// </summary>
    void Recent_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (RecentBox.SelectedItem is string address) AddressBox.Text = address;
    }

    /// <summary>
    /// Opens the actual selected client's directory.
    /// </summary>
    async void OpenFolder_Click(object? sender, RoutedEventArgs e) => await OpenTargetAsync(log: false);

    /// <summary>
    /// Opens the selected client's log, or its directory when no log exists yet.
    /// </summary>
    async void OpenLog_Click(object? sender, RoutedEventArgs e) => await OpenTargetAsync(log: true);

    /// <summary>
    /// Uses the platform's file launcher with errors kept on the page.
    /// </summary>
    async Task OpenTargetAsync(bool log)
    {
        if (_target is null || TopLevel.GetTopLevel(this) is not { } top) return;

        try
        {
            string path = log && File.Exists(_target.LogPath) ? _target.LogPath : _target.Folder + Path.DirectorySeparatorChar;
            if (!await top.Launcher.LaunchUriAsync(new Uri(path))) ShowProblem("Could not open the selected client path.");
        }
        catch (Exception error) when (IsClientError(error))
        {
            ShowProblem(error.Message);
        }
    }

    /// <summary>
    /// Shows recoverable client and configuration failures in the page.
    /// </summary>
    void ShowProblem(string message) => ProblemText.Text = LauncherText.Translate(message);

    /// <summary>
    /// Failures that can be corrected by choosing another client, address or writable directory.
    /// </summary>
    static bool IsClientError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or NotSupportedException
        or System.Text.Json.JsonException;
}
