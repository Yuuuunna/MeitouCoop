using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Meitou.Content;

namespace Meitou.Connections;

/// <summary>
/// Verified immutable mod installations, isolated from the Kenshi installation and published only after every member passes SHA-256.
/// </summary>
public sealed class ServerModCache(string root)
{
    /// <summary>
    /// Reuses only an exact inventory; damaged caches are replaced after a complete replacement has been verified.
    /// </summary>
    public async Task<string> PrepareAsync(ServerModManifest manifest, Func<string, CancellationToken, Task> download, CancellationToken token)
    {
        manifest.Validate();
        token.ThrowIfCancellationRequested();
        if (!manifest.Available) throw new InvalidOperationException("This server has disabled mod downloads (ShareMods).");
        if (manifest.Mods.Any(mod => !GameNameEncoding.CanEncode(mod.Name)))
            throw new InvalidDataException("A server mod name cannot be represented in the system code page.");

        string cache = Path.GetFullPath(root);
        Directory.CreateDirectory(cache);
        string target = Path.Combine(cache, manifest.Identity);
        using var gate = new FileStream(target + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(target) && await VerifyCacheAsync(target, manifest, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            WriteProfile(target, manifest);
            return target;
        }

        string staging = target + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            string archive = Path.Combine(staging, "download.zip");
            await download(archive, token).ConfigureAwait(false);
            await ExtractAsync(archive, staging, manifest, token).ConfigureAwait(false);
            File.Delete(archive);
            token.ThrowIfCancellationRequested();
            WriteProfile(staging, manifest);
            Publish(staging, target);
            return target;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Verifies the archive even when the downloader is supplied by another caller, then checks all headers before extracting anything.
    /// </summary>
    static async Task ExtractAsync(string path, string staging, ServerModManifest manifest, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        if (input.Length != manifest.ArchiveBytes || !Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false))
            .Equals(manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Server mod archive failed SHA-256 verification.");

        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var entries = CheckEntries(archive, manifest);
        Directory.CreateDirectory(Path.Combine(staging, "mods"));
        foreach (var file in manifest.Files)
        {
            string destination = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entries[file.Path].Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyVerifiedAsync(source, output, file, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects traversal, links, directories, duplicate/case-colliding members and files absent from the advertised inventory.
    /// </summary>
    static Dictionary<string, ZipArchiveEntry> CheckEntries(ZipArchive archive, ServerModManifest manifest)
    {
        if (archive.Entries.Count != manifest.Files.Length) throw new InvalidDataException("Server mod archive inventory does not match.");

        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            int type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!ServerModManifest.PortablePath(entry.FullName) || !expected.TryGetValue(entry.FullName, out var file)
                || entry.Length != file.Length || type is not (0 or 0x8000) || !entries.TryAdd(entry.FullName, entry)
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe or unexpected server mod archive member.");
        }

        return entries;
    }

    /// <summary>
    /// Enforces declared uncompressed length while hashing, so forged ZIP metadata cannot expand beyond its allowed output.
    /// </summary>
    static async Task CopyVerifiedAsync(Stream source, Stream output, ServerModFile file, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long done = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            done += count;
            if (done > file.Length) throw new InvalidDataException("Server mod member exceeds its declared size.");

            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }

        if (done != file.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Server mod file failed SHA-256 verification: " + file.Path);
    }

    /// <summary>
    /// Rechecks every cached byte and rejects extra files or links, including injected executable dependencies.
    /// </summary>
    static async Task<bool> VerifyCacheAsync(string directory, ServerModManifest manifest, CancellationToken token)
    {
        string mods = Path.Combine(directory, "mods");
        if (!Directory.Exists(mods) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;

        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (!CollectFiles(mods, directory, paths) || paths.Count != manifest.Files.Length) return false;
        foreach (var file in manifest.Files)
        {
            if (!paths.Contains(file.Path)) return false;

            await using var input = File.OpenRead(Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (input.Length != file.Length || !Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false))
                .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    /// <summary>
    /// Inventories cached files without following a symbolic link or junction.
    /// </summary>
    static bool CollectFiles(string directory, string rootDirectory, HashSet<string> files)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!CollectFiles(path, rootDirectory, files)) return false;
            }
            else files.Add(Path.GetRelativePath(rootDirectory, path).Replace('\\', '/'));
        }

        return true;
    }

    /// <summary>
    /// Keeps per-server order, settings and explicit code trust outside shared game configuration.
    /// </summary>
    static void WriteProfile(string directory, ServerModManifest manifest)
    {
        File.WriteAllText(Path.Combine(directory, "mods.cfg"), string.Concat(manifest.Mods.Select(m => m.Name + ".mod\r\n")),
            GameNameEncoding.Encoding);
        var profile = new ServerModProfile([.. manifest.Mods.Where(m => m.ExecuteCode).Select(m => m.Name)], manifest.ModSettings);
        File.WriteAllBytes(Path.Combine(directory, "profile.json"), JsonSerializer.SerializeToUtf8Bytes(profile, ModSyncJson.Default.ServerModProfile));
    }

    /// <summary>
    /// Replaces a damaged generation only after the staged generation is complete, rolling back a failed rename.
    /// </summary>
    static void Publish(string staging, string target)
    {
        string old = target + ".old-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(target)) Directory.Move(target, old);
        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(old)) Directory.Move(old, target);
            throw;
        }

        if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
    }
}
