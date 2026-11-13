using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Meitou.Launcher;

namespace Meitou.Tests.Launcher;

/// <summary>
/// Fault-injected release discovery with isolated disk caches and no real network or UI initialization.
/// </summary>
public sealed class ReleaseCatalogTests : IDisposable
{
    /// <summary>
    /// An isolated cache directory, never the user's launcher profile.
    /// </summary>
    readonly string _directory = Directory.CreateTempSubdirectory("meitou-catalog-").FullName;

    /// <summary>
    /// A cold start still advertises an installable bundled release when the API is rate-limited, and respects its retry time.
    /// </summary>
    [Fact]
    public async Task FirstStartWith403KeepsTheBundledDownloadAndDoesNotRetryTheApi()
    {
        using var handler = new Responses(RateLimited);
        using var http = new HttpClient(handler);
        var catalog = new ReleaseCatalog(http, _directory, Feed("1.2.3"));
        Assert.Equal("1.2.3", Assert.Single(catalog.Cached()).Version);

        var first = await catalog.FetchAsync(TestContext.Current.CancellationToken);
        var second = await catalog.FetchAsync(TestContext.Current.CancellationToken);
        var release = Assert.Single(first.Releases);
        Assert.Equal($"Meitou-1.2.3-{RuntimeInformation.RuntimeIdentifier}.zip", release.Asset?.Name);
        Assert.NotNull(first.Problem);
        Assert.Single(second.Releases);
        Assert.Equal(1, handler.ApiRequests);
    }

    /// <summary>
    /// Both primary and secondary limit responses honor Retry-After when no reset timestamp is supplied.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RetryAfterDefersTheNextApiRequest(HttpStatusCode status)
    {
        using var handler = new Responses(request =>
        {
            if (request.RequestUri?.Host != "api.github.com") return Response(HttpStatusCode.NotFound);

            var response = Response(status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return response;
        });
        using var http = new HttpClient(handler);
        var catalog = new ReleaseCatalog(http, _directory, Feed("1.2.3"));

        await catalog.FetchAsync(TestContext.Current.CancellationToken);
        var result = await catalog.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Single(result.Releases);
        Assert.Equal(1, handler.ApiRequests);
    }

    /// <summary>
    /// A valid public static catalog refreshes and persists releases without spending an API request.
    /// </summary>
    [Fact]
    public async Task StaticCatalogRefreshesWithoutContactingTheApi()
    {
        using var handler = new Responses(_ => Response(HttpStatusCode.OK, Feed("2.0.0", 2025)));
        using var http = new HttpClient(handler);
        var catalog = new ReleaseCatalog(http, _directory, Feed("1.2.3"));

        var result = await catalog.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Equal("2.0.0", Assert.Single(result.Releases).Version);
        Assert.Null(result.Problem);
        Assert.Equal(0, handler.ApiRequests);
        Assert.Null(ReleaseCache.Read(_directory)?.ETag);
        Assert.Equal("2.0.0", Assert.Single(catalog.Cached()).Version);
    }

    /// <summary>
    /// A missing static endpoint uses the API and scopes its saved conditional request to that API only.
    /// </summary>
    [Fact]
    public async Task MissingStaticCatalogUsesTheApiAndItsConditionalCache()
    {
        var conditions = new List<string?>();
        using var handler = new Responses(request =>
        {
            if (request.RequestUri?.Host != "api.github.com") return Response(HttpStatusCode.NotFound);

            string? condition = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
            conditions.Add(condition);
            if (condition is not null) return Response(HttpStatusCode.NotModified);

            var response = Response(HttpStatusCode.OK, Api("3.0.0"));
            response.Headers.ETag = new EntityTagHeaderValue("\"api-list\"");
            return response;
        });
        using var http = new HttpClient(handler);
        var catalog = new ReleaseCatalog(http, _directory, Feed("1.2.3"));

        var first = await catalog.FetchAsync(TestContext.Current.CancellationToken);
        var second = await catalog.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Equal("3.0.0", Assert.Single(first.Releases).Version);
        Assert.Equal("3.0.0", Assert.Single(second.Releases).Version);
        Assert.Equal(new string?[] { null, "\"api-list\"" }, conditions);
    }

    /// <summary>
    /// Broken disk JSON or a null/invalid body cannot suppress the embedded first-start fallback.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"ETag\":null,\"Body\":null}")]
    [InlineData("{\"ETag\":null,\"Body\":\"{}\"}")]
    public async Task CorruptCacheAndUnavailableNetworkUseTheBundle(string damaged)
    {
        string path = Path.Combine(_directory, "releases-cache.json");
        await File.WriteAllTextAsync(path, damaged, TestContext.Current.CancellationToken);
        using var handler = new Responses(_ => Response(HttpStatusCode.OK, "{\"format\":1,\"releases\":[5]}"));
        using var http = new HttpClient(handler);
        var catalog = new ReleaseCatalog(http, _directory, Feed("1.2.3"));

        var result = await catalog.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1.2.3", Assert.Single(result.Releases).Version);
        Assert.NotNull(result.Problem);
        Assert.Equal(damaged, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Unsupported or foreign static indexes do not overwrite a newer valid saved snapshot.
    /// </summary>
    [Theory]
    [InlineData(2, "Yuuuunna/MeitouCoop")]
    [InlineData(1, "other/repository")]
    public async Task InvalidStaticCatalogPreservesTheSavedList(int format, string repository)
    {
        string body = Api("3.0.0");
        ReleaseCache.Write(new(null, body, DateTimeOffset.UtcNow), _directory);
        using var handler = new Responses(request => request.RequestUri?.Host == "api.github.com"
            ? RateLimited(request) : Response(HttpStatusCode.OK, Feed("2.0.0", 2025, format, repository)));
        using var http = new HttpClient(handler);
        var catalog = new ReleaseCatalog(http, _directory, Feed("1.2.3"));

        var result = await catalog.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Equal("3.0.0", Assert.Single(result.Releases).Version);
        Assert.Equal(body, ReleaseCache.Read(_directory)?.Body);
        Assert.NotNull(result.Problem);
    }

    /// <summary>
    /// The real packaged resource must contain a recognized game release, independently of any disk cache.
    /// </summary>
    [Fact]
    public void PackagedSeedContainsARealPublishedGame()
    {
        using var handler = new Responses(RateLimited);
        using var http = new HttpClient(handler);
        var catalog = new ReleaseCatalog(http, _directory);

        Assert.Contains(catalog.Cached(), release => release.Kind == ReleaseKind.Game && release.Tag.StartsWith("coop-v", StringComparison.Ordinal));
    }

    /// <summary>
    /// Removes only the test's freshly created cache directory.
    /// </summary>
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// An unavailable static index and an explicit API quota reset.
    /// </summary>
    static HttpResponseMessage RateLimited(HttpRequestMessage request)
    {
        if (request.RequestUri?.Host != "api.github.com") return Response(HttpStatusCode.ServiceUnavailable);

        var response = Response(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString());
        return response;
    }

    /// <summary>
    /// A disposable JSON response owned by the catalog's HTTP request.
    /// </summary>
    static HttpResponseMessage Response(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// A minimal API release with a platform-matching asset.
    /// </summary>
    static string Api(string version) => JsonSerializer.Serialize(new[]
    {
        new
        {
            draft = false, tag_name = "coop-v" + version, name = "Coop " + version, prerelease = true,
            published_at = "2020-01-01T00:00:00Z", body = "Release notes", html_url = "https://github.com/Yuuuunna/MeitouCoop/releases",
            assets = new[]
            {
                new { name = $"Meitou-{version}-{RuntimeInformation.RuntimeIdentifier}.zip",
                    browser_download_url = "https://github.com/Yuuuunna/MeitouCoop/releases/download/example/client.zip", size = 1234 },
            },
        },
    });

    /// <summary>
    /// A versioned static index wrapping the same API-shaped release array.
    /// </summary>
    static string Feed(string version, int year = 2020, int format = 1, string repository = "Yuuuunna/MeitouCoop") =>
        $$"""{"format":{{format}},"repository":"{{repository}}","generatedAt":"{{year}}-01-01T00:00:00Z","releases":{{Api(version)}}} """;

    /// <summary>
    /// Deterministic transport that records whether the API was contacted.
    /// </summary>
    sealed class Responses(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        /// <summary>
        /// Number of requests that would consume anonymous GitHub API quota.
        /// </summary>
        public int ApiRequests { get; private set; }

        /// <summary>
        /// Supplies a fixture response without opening a network connection.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "api.github.com") ApiRequests++;
            return Task.FromResult(respond(request));
        }
    }
}
