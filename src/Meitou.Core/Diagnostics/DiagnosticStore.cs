using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meitou.Diagnostics;

/// <summary>
/// Durable bounded incident outbox. Uploads are immutable and idempotent; unsuccessful sends remain available for retry/export.
/// </summary>
public sealed class DiagnosticStore(string root)
{
    /// <summary>
    /// Maximum compressed artifact accepted by the private collector.
    /// </summary>
    public const long MaximumBytes = 16 << 20;
    /// <summary>
    /// Shared local-client location; servers explicitly select a directory under their own data root.
    /// </summary>
    public static string ClientRoot => Environment.GetEnvironmentVariable("MEITOU_DIAGNOSTICS_DIRECTORY") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeitouCoop", "diagnostics");
    /// <summary>
    /// Optional operator-selected collector. No automatic diagnostic upload goes to the legacy public feedback service.
    /// </summary>
    public static Uri? Collector => CollectorFor(ClientRoot);

    /// <summary>
    /// Resolves an operator/launcher-configured HTTPS endpoint, with the process environment taking precedence.
    /// </summary>
    public static Uri? CollectorFor(string root)
    {
        string? value = Environment.GetEnvironmentVariable("MEITOU_DIAGNOSTICS_URL");
        string path = Path.Combine(root, "collector.txt");
        if (string.IsNullOrWhiteSpace(value) && File.Exists(path)) value = File.ReadAllText(path).Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : null;
    }

    /// <summary>
    /// Outbox root exposed for local export, never sent in an artifact.
    /// </summary>
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>
    /// Saves a finalized package atomically. Pending reports are never silently removed to make room.
    /// </summary>
    public string Save(DiagnosticSnapshot snapshot, byte[]? screenshot = null, byte[]? runtimeLog = null)
    {
        Directory.CreateDirectory(Root);
        using var lease = new FileStream(Path.Combine(Root, ".write-lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        TrimSent();
        var pending = Pending();
        if (pending.Length >= 32 || pending.Sum(p => new FileInfo(p).Length) + MaximumBytes > 128L << 20)
            throw new IOException("Diagnostic outbox is full; export or submit pending reports first.");

        string path = Path.Combine(Root, $"{snapshot.Report:N}-{snapshot.Artifact:N}.zip");
        WritePackage(snapshot, path, screenshot, runtimeLog);
        return path;
    }

    /// <summary>
    /// Atomically replaces this run's recovery artifact without adding it to the upload queue.
    /// </summary>
    public void Recovery(DiagnosticSnapshot snapshot)
    {
        string directory = Directory.CreateDirectory(Path.Combine(Root, "recovery")).FullName;
        WritePackage(snapshot, Path.Combine(directory, $"{snapshot.Run:N}.zip"));
        foreach (var stale in new DirectoryInfo(directory).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(8))
            stale.Delete();
    }

    /// <summary>
    /// Lists immutable, finalized packages still waiting for explicit submission.
    /// </summary>
    public string[] Pending() => Directory.Exists(Root)
        ? Directory.GetFiles(Root, "*.zip").Where(p => !p.EndsWith(".sent.zip", StringComparison.Ordinal)).Order().ToArray() : [];

    /// <summary>
    /// Promotes a local recovery snapshot without changing its bytes or IDs, so repeat submission remains idempotent.
    /// </summary>
    public string QueueRecovery(string path)
    {
        string directory = Path.GetFullPath(Path.Combine(Root, "recovery"));
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Recovery must belong to this store.", nameof(path));
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > MaximumBytes) throw new InvalidDataException("Recovery package too large.");
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("diagnostics.json") ?? throw new InvalidDataException("Missing recovery manifest.");
        if (entry.Length > 32 << 20) throw new InvalidDataException("Recovery manifest too large.");
        using var manifest = entry.Open();
        var snapshot = JsonSerializer.Deserialize(manifest, DiagnosticJson.Default.DiagnosticSnapshot)
            ?? throw new InvalidDataException("Invalid recovery manifest.");
        string destination = Path.Combine(Root, $"{snapshot.Report:N}-{snapshot.Artifact:N}.zip");
        if (File.Exists(destination) || File.Exists(destination[..^4] + ".sent.zip")) return destination;
        using var lease = new FileStream(Path.Combine(Root, ".write-lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var pending = Pending();
        if (pending.Length >= 32 || pending.Sum(p => new FileInfo(p).Length) + source.Length > 128L << 20)
            throw new IOException("Diagnostic outbox is full.");
        source.Position = 0;
        string temporary = destination + $".{Guid.NewGuid():N}.part";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Uploads one finalized artifact. Retrying identical bytes returns success even after a lost response.
    /// </summary>
    public async Task SubmitAsync(string path, Uri collector, HttpClient http, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Package must belong to this outbox.", nameof(path));
        string[] ids = Path.GetFileNameWithoutExtension(path).Split('-');
        if (ids.Length != 2 || !Guid.TryParseExact(ids[0], "N", out var report) || !Guid.TryParseExact(ids[1], "N", out var artifact))
            throw new InvalidDataException("Invalid diagnostic package name.");

        await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid diagnostic package size.");
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        file.Position = 0;
        var destination = new Uri(collector.AbsoluteUri.TrimEnd('/') + $"/diagnostics/{report:N}/{artifact:N}");
        using var request = new HttpRequestMessage(HttpMethod.Put, destination);
        request.Content = new StreamContent(file);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        request.Headers.Add("X-Content-SHA256", hash);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
            throw new HttpRequestException("Collector did not acknowledge the immutable artifact.");
        file.Close();
        MarkDelivered(fullPath);
    }

    /// <summary>
    /// Marks a package acknowledged by the private collector or the authenticated game server, retaining it for local export.
    /// </summary>
    public void MarkDelivered(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Root, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid outbox path.", nameof(path));
        if (File.Exists(fullPath)) File.Move(fullPath, fullPath[..^4] + ".sent.zip", overwrite: true);
        TrimSent();
    }

    /// <summary>
    /// Writes a self-contained ZIP without reflection-based serialization or intermediate content dumps.
    /// </summary>
    public static void WritePackage(DiagnosticSnapshot snapshot, string path, byte[]? screenshot = null, byte[]? runtimeLog = null)
    {
        string temporary = path + $".{Guid.NewGuid():N}.part";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var entry = zip.CreateEntry("diagnostics.json", CompressionLevel.Fastest).Open())
                        JsonSerializer.Serialize(entry, snapshot, DiagnosticJson.Default.DiagnosticSnapshot);
                    if (screenshot is { Length: > 0 and <= 5_000_000 })
                    {
                        using var picture = zip.CreateEntry("screenshot.png", CompressionLevel.NoCompression).Open();
                        picture.Write(screenshot);
                    }
                    if (runtimeLog is { Length: <= DiagnosticRuntimeLog.MaximumBytes })
                    {
                        using var log = zip.CreateEntry("runtime.log", CompressionLevel.Fastest).Open();
                        log.Write(runtimeLog);
                    }
                }

                if (file.Length > MaximumBytes) throw new IOException("Diagnostic package exceeds the size limit.");
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Keeps the newest eight acknowledged local packages for export.
    /// </summary>
    void TrimSent()
    {
        foreach (var stale in new DirectoryInfo(Root).GetFiles("*.sent.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(8))
            stale.Delete();
    }
}
