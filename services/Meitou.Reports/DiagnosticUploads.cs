using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;

namespace Meitou.Reports;

/// <summary>
/// Private immutable diagnostic artifacts. Public callers may deposit bounded bytes; only authenticated administrators can read them.
/// </summary>
public sealed class DiagnosticUploads(string root)
{
    /// <summary>
    /// Upload and disk bounds are independent of the public feedback attachment store.
    /// </summary>
    const long MaximumBytes = 16L << 20, DiskBudget = 1L << 30;
    /// <summary>
    /// Serializes admission and atomic publication; excess concurrent uploads retry instead of consuming an unbounded queue.
    /// </summary>
    readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>
    /// Private directory outside the publicly served feedback tree.
    /// </summary>
    readonly string _root = Path.GetFullPath(root);

    /// <summary>
    /// Accepts an immutable artifact or acknowledges an identical retry. Hash mismatches never replace existing evidence.
    /// </summary>
    public async Task<int> Put(Guid report, Guid artifact, HttpRequest request)
    {
        string hash = request.Headers["X-Content-SHA256"].ToString();
        if (report == Guid.Empty || artifact == Guid.Empty || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return 400;
        if (request.ContentLength is <= 0 or > MaximumBytes) return 413;
        if (!await _gate.WaitAsync(0, request.HttpContext.RequestAborted)) return 429;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_root);
            string path = ArtifactPath(report, artifact);
            if (File.Exists(path)) return await Matches(path, hash, request.HttpContext.RequestAborted) ? 204 : 409;
            TrimExpired();
            var files = new DirectoryInfo(_root).GetFiles("*.zip");
            if (files.Length >= 2048 || files.Sum(f => f.Length) + MaximumBytes > DiskBudget) return 507;
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaximumBytes + 1;
            temporary = path + $".{Guid.NewGuid():N}.part";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            int status = await Receive(request.Body, temporary, hash, timeout.Token);
            if (status != 204) return status;
            File.Move(temporary, path);
            return 204;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    /// <summary>
    /// Lists artifact IDs under one incident, only from the admin route.
    /// </summary>
    public string[] List(Guid report) => report == Guid.Empty || !Directory.Exists(_root) ? [] :
        Directory.GetFiles(_root, $"{report:N}-*.zip").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray();

    /// <summary>
    /// Resolves an exact admin download; never accepts a filesystem path from a request.
    /// </summary>
    public string? Find(Guid report, Guid artifact)
    {
        string path = ArtifactPath(report, artifact);
        return report != Guid.Empty && artifact != Guid.Empty && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Deletes an incident's artifacts under the same admission gate used by uploads.
    /// </summary>
    public async Task Delete(Guid report)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (string name in List(report)) File.Delete(Path.Combine(_root, name + ".zip"));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Generates a flat name from validated GUIDs only.
    /// </summary>
    string ArtifactPath(Guid report, Guid artifact) => Path.Combine(_root, $"{report:N}-{artifact:N}.zip");

    /// <summary>
    /// Keeps private evidence for seven days; expired uploads are removed during the next admission.
    /// </summary>
    void TrimExpired()
    {
        foreach (var file in new DirectoryInfo(_root).GetFiles("*.zip"))
            if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)) file.Delete();
    }

    /// <summary>
    /// Compares the existing immutable artifact with the retry's content identity.
    /// </summary>
    static async Task<bool> Matches(string path, string hash, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies a bounded request without extracting untrusted ZIP contents.
    /// </summary>
    static async Task<int> Receive(Stream input, string path, string expected, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > MaximumBytes) return 413;
            hash.AppendData(buffer, 0, count);
            await file.WriteAsync(buffer.AsMemory(0, count), token);
        }

        if (total == 0 || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase)) return 400;
        await file.FlushAsync(token);
        file.Flush(flushToDisk: true);
        return 204;
    }
}
