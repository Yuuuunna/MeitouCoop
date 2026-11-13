using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Meitou.Launcher;

/// <summary>
/// Reads the public static catalog, then the API, retaining the newest valid disk or bundled snapshot when neither can be reached.
/// </summary>
sealed class ReleaseCatalog(HttpClient http, string cacheDirectory, string? bundledJson = null)
{
    /// <summary>
    /// Serializes creation of a shared refresh task; cancelling one caller does not cancel another caller's refresh.
    /// </summary>
    readonly object _gate = new();

    /// <summary>
    /// The current network refresh, shared by the version page and launcher update check.
    /// </summary>
    Task<(List<Release> Releases, string? Problem)>? _pending;

    /// <summary>
    /// The API's requested retry time after a rate-limit response; static catalog requests remain available.
    /// </summary>
    DateTimeOffset _retryApiAfter;

    /// <summary>
    /// Maximum catalog size and API pagination bounds.
    /// </summary>
    const int MaximumBytes = 8 * 1024 * 1024, PageSize = 100, MaximumPages = 50;

    /// <summary>
    /// An already validated snapshot, including the origin needed to explain an offline result.
    /// </summary>
    sealed record Snapshot(string Body, List<Release> Releases, string? ETag, DateTimeOffset SavedAt, bool Bundled);

    /// <summary>
    /// Returns the offline snapshot immediately, including on first start before any network request completes.
    /// </summary>
    public List<Release> Cached() => Offline()?.Releases ?? [];

    /// <summary>
    /// Shares overlapping refreshes without making a cancelled caller wait for their completion.
    /// </summary>
    public Task<(List<Release> Releases, string? Problem)> FetchAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_pending is null || _pending.IsCompleted) _pending = RefreshAsync();
            return _pending.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Prefers the static index so normal launches consume no anonymous GitHub API quota.
    /// </summary>
    async Task<(List<Release> Releases, string? Problem)> RefreshAsync()
    {
        var offline = Offline();
        try
        {
            if (await StaticAsync() is { } catalog) return Remember(catalog);
        }
        catch (Exception error) when (Recoverable(error))
        {
        }

        if (DateTimeOffset.UtcNow >= _retryApiAfter)
        {
            try
            {
                if (await ApiAsync(offline) is { } catalog) return Remember(catalog);
            }
            catch (Exception error) when (Recoverable(error))
            {
            }
        }

        string problem = offline is null ? "Could not load release information. Check your connection and try again."
            : offline.Bundled ? "Cannot check for updates; showing the bundled release list."
            : "Cannot check for updates; showing the saved release list.";
        return (offline?.Releases ?? [], LauncherText.Translate(problem));
    }

    /// <summary>
    /// Reads a bounded, repository-scoped static index without contacting api.github.com.
    /// </summary>
    async Task<Snapshot?> StaticAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var request = new HttpRequestMessage(HttpMethod.Get, LauncherIdentity.ReleaseCatalogUrl);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) return null;

        return ParseFeed(await ReadAsync(response, timeout.Token), bundled: false);
    }

    /// <summary>
    /// Falls back to the paginated API, with its ETag scoped only to an API response.
    /// </summary>
    async Task<Snapshot?> ApiAsync(Snapshot? cached)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var pages = new List<JsonElement>();
        string? etag = null;
        int bytes = 0;
        for (int page = 1; page <= MaximumPages; page++)
        {
            string url = $"https://api.github.com/repos/{Releases.Repository}/releases?per_page={PageSize}&page={page}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (page == 1 && cached?.ETag is { } previous) request.Headers.TryAddWithoutValidation("If-None-Match", previous);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (page == 1 && response.StatusCode == HttpStatusCode.NotModified) return cached;
            if (!response.IsSuccessStatusCode)
            {
                BackOff(response);
                return null;
            }
            if (page == 1) etag = response.Headers.ETag?.ToString();

            string body = await ReadAsync(response, timeout.Token);
            bytes += Encoding.UTF8.GetByteCount(body);
            if (bytes > MaximumBytes) throw new InvalidDataException("Release information is too large.");
            using var document = JsonDocument.Parse(body);
            foreach (var release in document.RootElement.EnumerateArray()) pages.Add(release.Clone());
            if (document.RootElement.GetArrayLength() < PageSize) return Join(pages, etag);
        }

        throw new InvalidDataException("The release list exceeded its page limit.");
    }

    /// <summary>
    /// Merges complete API pages without accepting a partial listing after an error.
    /// </summary>
    static Snapshot Join(List<JsonElement> pages, string? etag)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var page in pages) page.WriteTo(writer);
            writer.WriteEndArray();
        }

        string body = Encoding.UTF8.GetString(stream.ToArray());
        return new(body, Releases.Parse(body), etag, DateTimeOffset.UtcNow, false);
    }

    /// <summary>
    /// Honors the API's Retry-After or primary rate-limit reset instead of repeatedly consuming requests on refresh.
    /// </summary>
    void BackOff(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return;

        var now = DateTimeOffset.UtcNow;
        var retry = response.Headers.RetryAfter;
        _retryApiAfter = retry?.Date ?? now.Add(retry?.Delta ?? TimeSpan.FromMinutes(1));
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var values)) return;
        if (!long.TryParse(values.FirstOrDefault(), out long seconds) || seconds is < 0 or > 253402300799) return;

        var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (reset > _retryApiAfter) _retryApiAfter = reset;
    }

    /// <summary>
    /// Selects the newest valid offline snapshot; corrupt disk data never hides the bundled catalog.
    /// </summary>
    Snapshot? Offline()
    {
        var bundled = ParseFeed(bundledJson ?? Bundled(), bundled: true);
        var cache = ReleaseCache.Read(cacheDirectory);
        if (cache is null) return bundled;

        try
        {
            var releases = Releases.Parse(cache.Body);
            var saved = cache.SavedAt ?? DateTimeOffset.MinValue;
            return bundled is not null && bundled.SavedAt > saved ? bundled : new(cache.Body, releases, cache.ETag, saved, false);
        }
        catch (Exception error) when (Recoverable(error))
        {
            return bundled;
        }
    }

    /// <summary>
    /// Loads the catalog embedded in both managed and Native AOT launchers.
    /// </summary>
    static string Bundled()
    {
        using var stream = typeof(ReleaseCatalog).Assembly.GetManifestResourceStream("Meitou.Launcher.Releases.json");
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Accepts only this repository's supported catalog envelope and a completely valid release array.
    /// </summary>
    static Snapshot? ParseFeed(string text, bool bundled)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.GetProperty("format").GetInt32() != 1
                || root.GetProperty("repository").GetString() != Releases.Repository) return null;

            string body = root.GetProperty("releases").GetRawText();
            var saved = root.GetProperty("generatedAt").GetDateTimeOffset();
            return new(body, Releases.Parse(body), null, saved, bundled);
        }
        catch (Exception error) when (Recoverable(error))
        {
            return null;
        }
    }

    /// <summary>
    /// Persists only validated responses; a read-only cache does not prevent installing a release.
    /// </summary>
    (List<Release> Releases, string? Problem) Remember(Snapshot snapshot)
    {
        try
        {
            ReleaseCache.Write(new(snapshot.ETag, snapshot.Body, snapshot.SavedAt), cacheDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return (snapshot.Releases, null);
    }

    /// <summary>
    /// Bounds response size even when the server supplies no Content-Length header.
    /// </summary>
    static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var target = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (target.Length + count > MaximumBytes) throw new InvalidDataException("Release information is too large.");
            target.Write(buffer, 0, count);
        }

        return Encoding.UTF8.GetString(target.ToArray());
    }

    /// <summary>
    /// Data, network and filesystem failures that must fall back to another source instead of closing the launcher.
    /// </summary>
    static bool Recoverable(Exception error) => error is HttpRequestException or OperationCanceledException or IOException
        or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException;
}
