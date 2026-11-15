namespace Meitou.Diagnostics;

/// <summary>
/// Shared runtime switch watched by local clients, or by one server data directory. Package retention is independent of collection.
/// </summary>
public static class DiagnosticControl
{
    /// <summary>
    /// Reads an explicit local preference, defaulting to lightweight collection for player testing.
    /// </summary>
    public static bool Read(string root)
    {
        string path = Path.Combine(root, "enabled.txt");
        return !File.Exists(path) || File.ReadAllText(path).Trim() != "off";
    }

    /// <summary>
    /// Atomically writes the runtime preference. Active processes observe it within their next one-second poll.
    /// </summary>
    public static void Write(string root, bool enabled)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "enabled.txt");
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, enabled ? "on" : "off");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
