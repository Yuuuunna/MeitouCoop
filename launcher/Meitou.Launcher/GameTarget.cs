using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meitou.Settings;

namespace Meitou.Launcher;

/// <summary>
/// One explicitly selected client: the executable, settings schema, build information and log always belong to this target.
/// </summary>
sealed record GameTarget(string Exe, string Version, bool Local)
{
    /// <summary>
    /// The directory containing the selected executable.
    /// </summary>
    public string Folder => Path.GetDirectoryName(Exe)!;

    /// <summary>
    /// The game writes its log beside its executable.
    /// </summary>
    public string LogPath => Path.Combine(Folder, "log.txt");

    /// <summary>
    /// Creates direct process options with the same runtime selection for Play, settings and hardware detection.
    /// </summary>
    public ProcessStartInfo CreateStartInfo(bool hidden = false)
    {
        Directory.CreateDirectory(LauncherConfig.DataDirectory);
        var start = new ProcessStartInfo(Exe)
        {
            WorkingDirectory = LauncherConfig.DataDirectory, UseShellExecute = false, CreateNoWindow = hidden,
        };
        if (Local) ClientRuntime.Configure(start);

        return start;
    }

    /// <summary>
    /// A generated local schema is invalidated when the executable or game assembly changes.
    /// </summary>
    string CachedSchema
    {
        get
        {
            var executable = new FileInfo(Exe);
            var assembly = new FileInfo(Path.ChangeExtension(Exe, ".dll"));
            string stamp = $"{Exe}|{executable.LastWriteTimeUtc.Ticks}|{executable.Length}|{assembly.LastWriteTimeUtc.Ticks}";
            string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)));
            return Path.Combine(LauncherConfig.DataDirectory, "schemas", key + ".json");
        }
    }

    /// <summary>
    /// Resolves only the chosen source; a missing local client never falls back to an installed release.
    /// </summary>
    public static GameTarget? Resolve(LauncherConfig config, string? installedVersion)
    {
        if (config.UseLocalClient)
        {
            if (string.IsNullOrWhiteSpace(config.LocalClientPath)) return null;

            string path = Path.GetFullPath(config.LocalClientPath);
            string? exe = Directory.Exists(path) ? Releases.FindGame(path)
                : File.Exists(path) && Releases.GameExecutables.Contains(Path.GetFileName(path)) ? path : null;
            return exe is null ? null : new(exe, "Local client", true);
        }

        if (installedVersion is null) return null;
        string? installed = Releases.FindGame(Releases.VersionDirectory(installedVersion));
        return installed is null ? null : new(installed, installedVersion, false);
    }

    /// <summary>
    /// Reads the selected client's schema, using its generated cache for local builds.
    /// </summary>
    public SettingsSchema? ReadSchema()
    {
        try
        {
            return (Local ? SettingsSchema.Read(CachedSchema) : null)
                ?? SettingsSchema.Read(Path.Combine(Folder, SettingsSchema.FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks a local executable for its own schema without writing into its installation directory.
    /// </summary>
    public async Task RefreshSchemaAsync()
    {
        if (!Local) return;

        string destination = CachedSchema;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteSchemaAsync(temporary);
            if (SettingsSchema.Read(temporary) is null) throw new InvalidDataException("The client returned an unsupported settings schema.");

            File.Move(temporary, destination, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Runs the schema-only command with a bounded lifetime and checks its exit code.
    /// </summary>
    async Task WriteSchemaAsync(string path)
    {
        var start = CreateStartInfo(hidden: true);
        start.ArgumentList.Add("--settings-schema");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The selected client did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The client did not return its settings schema within 30 seconds.");
        }

        if (process.ExitCode != 0) throw new InvalidOperationException($"The settings command exited with code {process.ExitCode}.");
    }

    /// <summary>
    /// Displays optional package metadata; absent information is never guessed from the release version.
    /// </summary>
    public string BuildDescription()
    {
        string path = Path.Combine(Folder, "build-info.json");
        if (!File.Exists(path)) return LauncherText.Translate("Build information unavailable (build-info.json is missing).");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string version = Property(root, "version");
            string head = Property(root, "sourceHead");
            string protocol = Property(root, "protocol");
            string engine = Property(root, "engine");
            bool dirty = root.TryGetProperty("includesUncommittedChanges", out var changes) && changes.ValueKind == JsonValueKind.True;
            string changed = dirty ? LauncherText.Translate(" (uncommitted changes)") : "";
            return LauncherText.Translate("Version: {1}\nCommit: {2}{3}\nProtocol: {4}\nEngine: {5}", version, head, changed, protocol, engine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return $"Could not read build information: {error.Message}";
        }
    }

    /// <summary>
    /// A scalar metadata value, or an explicit unknown marker.
    /// </summary>
    static string Property(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "unknown";

    /// <summary>
    /// Finds an existing checkout build after an explicit Find action; never changes the launch source itself.
    /// </summary>
    public static string? FindCheckout()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (!File.Exists(Path.Combine(directory.FullName, "MeitouClient.slnx"))) continue;

                foreach (string configuration in new[] { "Release", "Debug" })
                {
                    string bin = Path.Combine(directory.FullName, "src", "Meitou.Game", "bin", configuration);
                    if (!Directory.Exists(bin)) continue;

                    foreach (string folder in Directory.EnumerateDirectories(bin).OrderDescending())
                        if (Releases.FindGame(folder) is not null) return folder;
                }
            }
        }

        return null;
    }
}
