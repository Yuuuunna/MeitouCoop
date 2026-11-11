using System.Security.Cryptography;
using System.Text;
using Meitou.Content;
using Meitou.Data;

namespace Meitou.Networking;
public enum ContentKind : byte
{
    Database,
    GameplayAsset
}

public sealed record ContentFile(ContentKind Kind, string Key, string Path);
public sealed record ContentFingerprint(ContentKind Kind, string Key, long Length, string Sha256);
/// <summary>
/// Ordered compatibility evidence. Paths are local-only; only logical keys, sizes and digests enter the manifest.
/// </summary>
public sealed class ContentManifest
{
    public const int MaximumFiles = 16384;
    readonly ContentFingerprint[] _files;
    public ContentManifest(IEnumerable<ContentFingerprint> entries)
    {
        var copy = entries.Take(MaximumFiles + 1).ToArray();
        if (copy.Length > MaximumFiles)
            throw new InvalidDataException("Too many compatibility files.");
        var keys = new HashSet<(ContentKind, string)>();
        _files = copy.Select(f =>
        {
            if (f is null || !Enum.IsDefined(f.Kind) || f.Length < 0 || !ValidHash(f.Sha256))
                throw new InvalidDataException("Invalid content fingerprint.");
            string key = CanonicalKey(f.Key);
            if (!keys.Add((f.Kind, key)))
                throw new InvalidDataException("Duplicate compatibility file key.");
            return f with
            {
                Key = key,
                Sha256 = f.Sha256.ToUpperInvariant()
            };
        }).ToArray();
        Files = Array.AsReadOnly(_files);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("Meitou.ContentManifest.1");
            writer.Write(_files.Length);
            foreach (var file in _files)
            {
                writer.Write((byte)file.Kind);
                writer.Write(file.Key);
                writer.Write(file.Length);
                writer.Write(Convert.FromHexString(file.Sha256));
            }
        }

        Digest = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    public IReadOnlyList<ContentFingerprint> Files { get; }
    public string Digest { get; }

    /// <summary>
    /// Hash an explicitly selected set of gameplay-relevant files. Call on stable content before running the world.
    /// </summary>
    public static ContentManifest ReadFiles(IEnumerable<ContentFile> inputs)
    {
        var result = new List<ContentFingerprint>();
        foreach (var input in inputs)
        {
            if (result.Count >= MaximumFiles)
                throw new InvalidDataException("Too many compatibility files.");
            if (input is null)
                throw new InvalidDataException("Missing compatibility file.");
            string key = CanonicalKey(input.Key);
            string path = GameFiles.Resolve(input.Path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            if (stream.Length != length)
                throw new IOException("Content changed while hashing.");
            result.Add(new ContentFingerprint(input.Kind, key, length, hash));
        }

        return new ContentManifest(result);
    }

    /// <summary>
    /// Database merge order is retained exactly. Additional collision/navigation/animation/config assets must be supplied by the host.
    /// </summary>
    public static ContentManifest ReadLoadOrder(LoadOrder order, IEnumerable<ContentFile>? gameplayAssets = null)
    {
        if (order.Issues.Count != 0)
            throw new InvalidDataException("Cannot fingerprint an unresolved load order.");
        return ReadFiles(order.Entries.Select(e => new ContentFile(ContentKind.Database, e.Name, e.Path)).Concat(gameplayAssets ?? []));
    }

    public static string RulesDigest(IReadOnlyDictionary<string, string> authoritativeRules)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("Meitou.GameplayRules.1");
            writer.Write(authoritativeRules.Count);
            foreach (var(key, value)in authoritativeRules.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(key) || value is null)
                    throw new InvalidDataException("Invalid gameplay rule.");
                writer.Write(key);
                writer.Write(value);
            }
        }

        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    internal static bool ValidHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    static string CanonicalKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1024)
            throw new InvalidDataException("Invalid logical content key.");
        string normalized = key.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Split('/').Any(p => p.Length == 0 || p is "." or "..") || normalized.Any(c => char.IsControl(c) || c == ':'))
            throw new InvalidDataException("Content keys must be portable relative names.");
        return normalized;
    }
}