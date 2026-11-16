using System.Diagnostics;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// A dedicated server executable and its persistent configuration, independent of the selected game client.
/// </summary>
sealed record ServerTarget(string Exe, string ConfigPath)
{
    /// <summary>
    /// The platform's dedicated server apphost name.
    /// </summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "Meitou.Server.exe" : "Meitou.Server";

    /// <summary>
    /// A persistent console log beside this configuration, retained across executable upgrades.
    /// </summary>
    public string LogPath => ConfigPath + ".launcher.log";

    /// <summary>
    /// Resolves an explicitly chosen folder or apphost without falling back to another server build.
    /// </summary>
    public static ServerTarget Resolve(string path, string configPath)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a dedicated server folder or executable.");
        if (string.IsNullOrWhiteSpace(configPath)) throw new ArgumentException("Choose a server configuration file.");

        string exe = Path.GetFullPath(path.Trim().Trim('"'));
        if (Directory.Exists(exe)) exe = Path.Combine(exe, ExecutableName);
        if (!File.Exists(exe) || !Path.GetFileName(exe).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("No Meitou.Server executable was found at this path.");

        return new(exe, Path.GetFullPath(configPath.Trim().Trim('"')));
    }

    /// <summary>
    /// Finds a built server only after an explicit checkout search.
    /// </summary>
    public static string? FindCheckout() => GameTarget.FindCheckout("Meitou.Server", FindExecutable);

    /// <summary>
    /// Looks for the dedicated apphost directly in a build directory.
    /// </summary>
    static string? FindExecutable(string folder)
    {
        string exe = Path.Combine(folder, ExecutableName);
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Creates a hidden console child with redirected commands and logs, without renderer, Steam or client launch options.
    /// </summary>
    public ProcessStartInfo CreateStartInfo(GameInstall install)
    {
        string workingDirectory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(workingDirectory);
        var start = new ProcessStartInfo(Exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        ClientRuntime.Configure(start);
        start.Environment[GameInstall.EnvironmentVariable] = install.Root;
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(ConfigPath);
        return start;
    }
}
