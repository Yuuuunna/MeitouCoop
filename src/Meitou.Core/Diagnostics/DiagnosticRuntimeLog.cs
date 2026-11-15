using System.Text;

namespace Meitou.Diagnostics;

/// <summary>
/// Bounded copy of the host-selected running log. Remote requests never supply a filesystem path.
/// </summary>
public static class DiagnosticRuntimeLog
{
    /// <summary>
    /// Maximum source and sanitized output size; matches the existing client's log cap.
    /// </summary>
    public const int MaximumBytes = 10 << 20;

    /// <summary>
    /// Reads the available log tail and filters credentials/paths using the same rules as diagnostic console summaries.
    /// </summary>
    public static (byte[]? Content, string Status) Read(string? path)
    {
        if (path is null) return (null, "unavailable");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = file.Length;
            bool truncated = length > MaximumBytes;
            file.Position = Math.Max(0, length - MaximumBytes);
            byte[] bytes = new byte[(int)Math.Min(length, MaximumBytes)];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = file.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }

            int start = truncated ? Array.IndexOf(bytes, (byte)'\n', 0, count) + 1 : 0;
            return Sanitize(bytes.AsSpan(start, count - start), truncated || count != bytes.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, "unavailable-" + error.GetType().Name);
        }
    }

    /// <summary>
    /// Filters complete lines while keeping the output below its fixed byte budget.
    /// </summary>
    static (byte[] Content, string Status) Sanitize(ReadOnlySpan<byte> source, bool truncated)
    {
        using var reader = new StringReader(Encoding.UTF8.GetString(source));
        using var output = new MemoryStream();
        while (reader.ReadLine() is { } line)
        {
            byte[] clean = Encoding.UTF8.GetBytes(DiagnosticRedaction.Clean(line) + "\n");
            if (output.Length + clean.Length > MaximumBytes)
            {
                truncated = true;
                break;
            }

            output.Write(clean);
        }

        return (output.ToArray(), truncated ? "sanitized-tail" : "sanitized");
    }
}
