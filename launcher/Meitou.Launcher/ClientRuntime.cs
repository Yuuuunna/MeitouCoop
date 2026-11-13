using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Meitou.Launcher;

/// <summary>
/// Finds an already installed runtime for framework-dependent local builds without modifying global environment or downloading an SDK.
/// </summary>
static class ClientRuntime
{
    /// <summary>
    /// Points only the child process at a compatible runtime, preventing the apphost's missing-runtime installation dialog.
    /// </summary>
    public static void Configure(ProcessStartInfo start)
    {
        string config = Path.ChangeExtension(start.FileName, ".runtimeconfig.json");
        if (!File.Exists(config)) return;

        using var document = JsonDocument.Parse(File.ReadAllText(config));
        if (!document.RootElement.TryGetProperty("runtimeOptions", out var options))
            throw new InvalidDataException("The selected client's runtime configuration has no runtimeOptions.");
        var frameworks = Frameworks(options).ToArray();
        if (frameworks.Length == 0) return; // Self-contained clients list includedFrameworks instead.

        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        string? root = Candidates(start, architecture).Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path => frameworks.All(framework => HasFramework(path, framework.Name, framework.Version)));
        if (root is null)
        {
            string required = string.Join(", ", frameworks.Select(f => $"{f.Name} {f.Version}"));
            throw new InvalidOperationException($"This local client requires {required}. "
                + "Select a self-contained client or an installed compatible runtime.");
        }

        start.Environment[$"DOTNET_ROOT_{architecture}"] = root;
        start.Environment["DOTNET_ROOT"] = root;
    }

    /// <summary>
    /// Frameworks declared by runtimeconfig.json, without treating an unknown version as a compatible runtime.
    /// </summary>
    static IEnumerable<(string Name, Version Version)> Frameworks(JsonElement options)
    {
        if (options.TryGetProperty("framework", out var single)) yield return ReadFramework(single);
        if (!options.TryGetProperty("frameworks", out var multiple)) yield break;

        foreach (var framework in multiple.EnumerateArray()) yield return ReadFramework(framework);
    }

    /// <summary>
    /// One required runtime framework.
    /// </summary>
    static (string Name, Version Version) ReadFramework(JsonElement framework)
    {
        if (!framework.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(name.GetString()) || !framework.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.String || !Version.TryParse(version.GetString(), out var parsed))
            throw new InvalidDataException("The selected client declares an invalid runtime framework.");

        return (name.GetString()!, parsed);
    }

    /// <summary>
    /// The standard patch-compatible runtime search; broader roll-forward policies are left to explicitly installed clients.
    /// </summary>
    static bool HasFramework(string root, string name, Version required)
    {
        string shared = Path.Combine(root, "shared", name);
        if (!Directory.Exists(shared)) return false;

        return Directory.EnumerateDirectories(shared).Any(directory => Version.TryParse(Path.GetFileName(directory), out var version)
            && version.Major == required.Major && version.Minor == required.Minor && version >= required);
    }

    /// <summary>
    /// Runtime locations supplied by the environment, this repository's SDK setup and normal platform installations.
    /// </summary>
    static IEnumerable<string> Candidates(ProcessStartInfo start, string architecture)
    {
        if (start.Environment.TryGetValue($"DOTNET_ROOT_{architecture}", out string? archRoot) && !string.IsNullOrWhiteSpace(archRoot))
            yield return archRoot;
        if (start.Environment.TryGetValue("DOTNET_ROOT", out string? root) && !string.IsNullOrWhiteSpace(root)) yield return root;
        if (start.Environment.TryGetValue("DOTNET_HOST_PATH", out string? host) && !string.IsNullOrWhiteSpace(host))
            yield return Path.GetDirectoryName(host)!;

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "MeitouClient-dotnet11");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        }
        else
        {
            yield return "/usr/share/dotnet";
            yield return "/usr/local/share/dotnet";
            yield return "/usr/lib/dotnet";
        }

        foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")))
                yield return path;
    }
}
