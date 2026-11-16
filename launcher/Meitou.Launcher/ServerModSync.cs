using Meitou.Connections;

namespace Meitou.Launcher;

/// <summary>
/// One cancellable pre-launch synchronization, keeping network downloads and profile publication outside the window's process-start code.
/// </summary>
sealed class ServerModSync
{
    /// <summary>
    /// The current operation, including its download and file verification.
    /// </summary>
    CancellationTokenSource? _stop;

    /// <summary>
    /// Prevents another Play or Connect from racing a pending mod selection.
    /// </summary>
    public bool IsBusy => _stop is not null;

    /// <summary>
    /// Requests cancellation without touching existing verified caches.
    /// </summary>
    public void Cancel() => _stop?.Cancel();

    /// <summary>
    /// Queries, obtains explicit executable-code consent and publishes the verified server profile before any client is started.
    /// </summary>
    public async Task<string?> PrepareAsync(GameTarget target, string address, Func<ServerModManifest, Task<bool>> consent, IProgress<string> status)
    {
        if (IsBusy) throw new InvalidOperationException("Server mods are already synchronizing.");

        using var stop = new CancellationTokenSource();
        _stop = stop;
        try
        {
            if (target.Local) await target.RefreshSchemaAsync();
            stop.Token.ThrowIfCancellationRequested();
            if (target.ReadSchema()?.ServerModSyncFormat != 1)
                throw new InvalidOperationException("Update the selected client to support isolated server mod synchronization.");

            status.Report(LauncherText.Translate("Reading the server mod list…"));
            var manifest = await ServerModClient.QueryAsync(address, stop.Token);
            if (!manifest.Available) throw new InvalidOperationException("This server has disabled mod downloads (ShareMods).");
            if (manifest.Mods.Any(m => m.ExecuteCode) && !await consent(manifest)) return null;

            stop.Token.ThrowIfCancellationRequested();
            status.Report(LauncherText.Translate("Checking {1} server mods…", manifest.Mods.Length));
            var progress = new Progress<long>(done =>
            {
                status.Report(LauncherText.Translate("Downloading server mods: {1} / {2} MiB",
                    done / (1024 * 1024), manifest.ArchiveBytes / (1024 * 1024)));
            });
            var cache = new ServerModCache(Path.Combine(LauncherConfig.DataDirectory, "server-mods"));
            return await cache.PrepareAsync(manifest,
                (path, token) => ServerModClient.DownloadAsync(address, manifest, path, progress, token), stop.Token);
        }
        finally
        {
            _stop = null;
        }
    }

    /// <summary>
    /// Supplies only the synchronized client process with its mod roots, exact order and consented code permissions.
    /// </summary>
    public static void Configure(System.Diagnostics.ProcessStartInfo start, string directory)
    {
        start.Environment["MEITOU_MODS_DIRECTORY"] = Path.Combine(directory, "mods");
        start.Environment[ServerModProfile.EnvironmentVariable] = Path.Combine(directory, "profile.json");
        start.Environment["MEITOU_MODS_CFG"] = Path.Combine(directory, "mods.cfg");
    }
}
