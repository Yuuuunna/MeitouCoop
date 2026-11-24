using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Meitou.Diagnostics;

namespace Meitou.Launcher;

/// <summary>
/// Live collection control, local export and explicit private outbox retry, available even when the game cannot start.
/// </summary>
public sealed class DiagnosticsPanel : StackPanel
{
    /// <summary>
    /// The same directory watched by local clients; server data is managed separately.
    /// </summary>
    readonly DiagnosticStore _store = new(DiagnosticStore.ClientRoot);
    /// <summary>
    /// Changes the shared runtime preference within the next poll.
    /// </summary>
    readonly CheckBox _enabled = new() { Content = "Collect diagnostics (running clients and future launches)" };
    /// <summary>
    /// Operator-provided private collector, never the legacy public attachment endpoint by default.
    /// </summary>
    readonly TextBox _collector = new() { PlaceholderText = "Private collector URL (https://...)" };
    /// <summary>
    /// Pending, acknowledged and recovery packages available for export.
    /// </summary>
    readonly ComboBox _packages = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    /// <summary>
    /// Collection/export/submission outcome and missing configuration.
    /// </summary>
    readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    /// <summary>
    /// Explicit submission action; disabled during an active retry.
    /// </summary>
    readonly Button _submit = new() { Content = "Submit / retry selected" };
    /// <summary>
    /// Prevents reflecting the saved preference from writing it again.
    /// </summary>
    bool _refreshing;

    /// <summary>
    /// Builds a small standalone launcher feature rather than expanding the main window's responsibilities.
    /// </summary>
    public DiagnosticsPanel()
    {
        Spacing = 10;
        Children.Add(new TextBlock { Text = "Diagnostics", Classes = { "name" } });
        Children.Add(_enabled);
        Children.Add(new TextBlock
        {
            Text = "When enabled, your connected game server can request a diagnostic package and a filtered runtime log.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        Children.Add(_collector);
        Children.Add(_packages);
        var actions = new WrapPanel();
        AddButton(actions, "Refresh", RefreshClick);
        AddButton(actions, "Export selected", ExportClick);
        _submit.Click += SubmitClick;
        actions.Children.Add(_submit);
        Children.Add(actions);
        Children.Add(_status);
        _enabled.IsCheckedChanged += Toggle;
        AttachedToVisualTree += (_, _) => Refresh();
    }

    /// <summary>
    /// Creates one action button with the existing launcher styling.
    /// </summary>
    static void AddButton(Panel panel, string label, EventHandler<RoutedEventArgs> action)
    {
        var button = new Button { Content = label, Margin = new Avalonia.Thickness(0, 0, 8, 6) };
        button.Click += action;
        panel.Children.Add(button);
    }

    /// <summary>
    /// Reloads local packages without network requests.
    /// </summary>
    void RefreshClick(object? sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// Shows both explicit reports and bounded recovery snapshots from previous runs.
    /// </summary>
    void Refresh()
    {
        _refreshing = true;
        try
        {
            _enabled.IsChecked = DiagnosticControl.Read(_store.Root);
            _collector.Text = DiagnosticStore.Collector?.AbsoluteUri ?? "";
            string recovery = Path.Combine(_store.Root, "recovery");
            var packages = Directory.Exists(_store.Root) ? Directory.GetFiles(_store.Root, "*.zip") : [];
            if (Directory.Exists(recovery)) packages = packages.Concat(Directory.GetFiles(recovery, "*.zip")).ToArray();
            _packages.ItemsSource = packages.Select(p => new Package(p)).OrderByDescending(p => File.GetLastWriteTimeUtc(p.Path)).ToArray();
            _packages.SelectedIndex = packages.Length == 0 ? -1 : 0;
            _status.Text = LauncherText.Translate("{1} pending. Packages stay local until submitted. Recovery snapshots may be up to 15 seconds old.", _store.Pending().Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = error.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Applies the switch to active clients without restarting them.
    /// </summary>
    void Toggle(object? sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try
        {
            DiagnosticControl.Write(_store.Root, _enabled.IsChecked == true);
            _status.Text = LauncherText.Translate("Collection preference saved; running clients update within one second. Existing packages are retained.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = error.Message;
        }
    }

    /// <summary>
    /// Copies one package to a user-selected destination without exposing private files through public feedback.
    /// </summary>
    async void ExportClick(object? sender, RoutedEventArgs e)
    {
        if (_packages.SelectedItem is not Package package || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var destination = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LauncherText.Translate("Export diagnostic report"), SuggestedFileName = System.IO.Path.GetFileName(package.Path), DefaultExtension = "zip"
            });
            if (destination is null) return;
            await using var input = File.OpenRead(package.Path);
            await using var output = await destination.OpenWriteAsync();
            await input.CopyToAsync(output);
            _status.Text = LauncherText.Translate("Diagnostic package exported.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = error.Message;
        }
    }

    /// <summary>
    /// Explicitly sends the selected immutable package; failures leave it available for retry.
    /// </summary>
    async void SubmitClick(object? sender, RoutedEventArgs e)
    {
        if (!_submit.IsEnabled || _packages.SelectedItem is not Package package) return;
        if (!Uri.TryCreate(_collector.Text?.Trim(), UriKind.Absolute, out var collector) || collector.Scheme != "https" ||
            collector.UserInfo.Length != 0 || collector.Query.Length != 0 || collector.Fragment.Length != 0)
        {
            _status.Text = LauncherText.Translate("Enter the private collector HTTPS URL supplied by the server operator.");
            return;
        }

        _submit.IsEnabled = false;
        try
        {
            Directory.CreateDirectory(_store.Root);
            File.WriteAllText(Path.Combine(_store.Root, "collector.txt"), collector.AbsoluteUri);
            string path = package.Path;
            if (Path.GetFileName(Path.GetDirectoryName(path)) == "recovery") path = await Task.Run(() => _store.QueueRecovery(path));
            if (path.EndsWith(".sent.zip", StringComparison.Ordinal) || File.Exists(path[..^4] + ".sent.zip"))
            {
                _status.Text = LauncherText.Translate("This package was already submitted.");
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            await _store.SubmitAsync(path, collector, http);
            Refresh();
            _status.Text = LauncherText.Translate("Diagnostic package submitted privately.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _status.Text = LauncherText.Translate("Submission failed; the local package is retained. {1}", error.Message);
        }
        finally
        {
            _submit.IsEnabled = true;
        }
    }

    /// <summary>
    /// File-backed display row without reading or rendering diagnostic contents.
    /// </summary>
    sealed record Package(string Path)
    {
        /// <summary>
        /// Distinguishes recovery, acknowledged and pending artifacts at a glance.
        /// </summary>
        public override string ToString() => (System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) == "recovery" ? LauncherText.Translate("Recovery: ") : "") +
            System.IO.Path.GetFileName(Path);
    }
}
