using System.Text.RegularExpressions;

namespace Meitou.Diagnostics;

/// <summary>
/// Conservative filtering for the private console tail; structured events never carry credentials or payloads.
/// </summary>
public static class DiagnosticRedaction
{
    /// <summary>
    /// Credential-like lines are omitted entirely rather than attempting to parse every secret format.
    /// </summary>
    static readonly Regex Secrets = new(@"token|password|secret|credential|authorization|bearer|private.?key|BEGIN .*KEY",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    /// <summary>
    /// Absolute local paths and URLs are not needed for a console summary.
    /// </summary>
    static readonly Regex Paths = new(@"(?:[A-Za-z]:[\\/]|/|https?://)\S+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Removes sensitive lines and machine paths, bounding work and output size.
    /// </summary>
    public static string Clean(string line)
    {
        string bounded = line.Length > 1024 ? line[..1024] : line;
        try
        {
            return Secrets.IsMatch(bounded) ? "[credential-related line omitted]" : Paths.Replace(bounded, "[path]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[line omitted]";
        }
    }
}
