using System.Text.Json;

namespace Meitou.Launcher;

/// <summary>
/// The last valid release list, its API-only ETag and its snapshot time, kept for offline startup.
/// </summary>
static class ReleaseCache
{
    /// <summary>
    /// The cached answer: page 1's ETag and every page's releases as one JSON array.
    /// </summary>
    internal sealed record Cache(string? ETag, string Body, DateTimeOffset? SavedAt = null);

    /// <summary>
    /// Where the cache is kept, in the launcher's data folder.
    /// </summary>
    static string CacheFile(string directory) => Path.Combine(directory, "releases-cache.json");

    /// <summary>
    /// The cached answer, or null when there is none or it cannot be read.
    /// </summary>
    internal static Cache? Read(string directory)
    {
        try
        {
            string path = CacheFile(directory);
            if (!File.Exists(path)) return null;

            var cache = JsonSerializer.Deserialize(File.ReadAllText(path), LauncherJson.Default.Cache);
            return cache is null ? null : cache with { SavedAt = cache.SavedAt ?? File.GetLastWriteTimeUtc(path) };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps <paramref name="cache"/> as the last answer.
    /// </summary>
    internal static void Write(Cache cache, string directory)
    {
        Directory.CreateDirectory(directory);
        string path = CacheFile(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(cache, LauncherJson.Default.Cache));
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
