using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Meitou.Connections;
using Meitou.Content;

namespace Meitou.Server;

/// <summary>
/// An immutable startup archive of enabled mod folders, served by hash without exposing arbitrary filesystem paths.
/// </summary>
public sealed class ServerModPackage : IDisposable
{
    /// <summary>
    /// The temporary snapshot retained until the discovery listener has stopped.
    /// </summary>
    readonly string? _archive;

    /// <summary>
    /// The immutable serialized descriptor, shared by all bootstrap connections.
    /// </summary>
    readonly byte[] _descriptor;

    /// <summary>
    /// Limits simultaneous archive uploads independently of the discovery connection limit.
    /// </summary>
    readonly SemaphoreSlim _uploads = new(2, 2);

    /// <summary>
    /// The validated inventory advertised to launchers.
    /// </summary>
    public ServerModManifest Manifest { get; }

    /// <summary>
    /// Retains only an already generated and validated package.
    /// </summary>
    ServerModPackage(string? archive, ServerModManifest manifest)
    {
        _archive = archive;
        Manifest = manifest;
        _descriptor = manifest.Encode();
    }

    /// <summary>
    /// Captures mods only, never the installation's base files; links and nonportable paths are refused before serving any bytes.
    /// </summary>
    public static ServerModPackage Create(GameInstall install, string directory, bool enabled, IReadOnlySet<string> trustedCodeMods,
        Dictionary<string, Dictionary<string, string>> settings)
    {
        if (!enabled) return new(null, ServerModManifest.Unavailable);

        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var available = ModList.Discover(install).ToDictionary(m => m.Name, StringComparer.Ordinal);
            var mods = ModList.ReadEnabled(install).Select(name => available.TryGetValue(name, out var mod) ? mod
                : throw new InvalidDataException($"Enabled server mod was not found: {name}.")).ToArray();
            var files = WriteArchive(archive, mods);
            using var input = File.OpenRead(archive);
            var manifest = new ServerModManifest(1, true, input.Length, Convert.ToHexString(SHA256.HashData(input)),
                [.. mods.Select(m => new ServerModEntry(m.Name, m.HasCode, m.HasCode && trustedCodeMods.Contains(m.Name)))], files, settings);
            return new(archive, manifest);
        }
        catch
        {
            File.Delete(archive);
            throw;
        }
    }

    /// <summary>
    /// Writes stable member ordering and timestamps so unchanged content reuses the same client cache after a restart.
    /// </summary>
    static ServerModFile[] WriteArchive(string path, ModInfo[] mods)
    {
        var files = new List<ServerModFile>();
        long total = 0;
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var mod in mods)
            {
                foreach (string file in EnumerateFiles(mod.Folder).Order(StringComparer.Ordinal))
                {
                    string relative = $"mods/{mod.Name}/" + Path.GetRelativePath(mod.Folder, file).Replace('\\', '/');
                    if (!ServerModManifest.PortablePath(relative)) throw new InvalidDataException($"Nonportable mod path: {relative}.");
                    if (files.Count >= 100000) throw new InvalidDataException("Too many server mod files.");

                    var fingerprint = WriteFile(archive, relative, file);
                    total = checked(total + fingerprint.Length);
                    if (total > ServerModManifest.MaximumBytes || output.Length > ServerModManifest.MaximumBytes)
                        throw new InvalidDataException("Server mod archive exceeds the size limit.");

                    files.Add(fingerprint);
                }
            }
        }

        return [.. files];
    }

    /// <summary>
    /// Rejects reparse points at every level instead of following links outside an enabled mod's root.
    /// </summary>
    static IEnumerable<string> EnumerateFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked mod folders cannot be shared.");

        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked mod files cannot be shared.");
            if ((attributes & FileAttributes.Directory) == 0) yield return path;
            else
                foreach (string file in EnumerateFiles(path)) yield return file;
        }
    }

    /// <summary>
    /// Hashes the exact bytes copied into the snapshot while preventing concurrent writes on Windows.
    /// </summary>
    static ServerModFile WriteFile(ZipArchive archive, string relative, string path)
    {
        using var input = new FileStream(GameFiles.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = input.Length;
        if (length > ServerModManifest.MaximumBytes) throw new InvalidDataException("A server mod file exceeds the size limit.");

        var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var output = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long copied = 0;
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            copied += count;
            if (copied > length) throw new IOException("Mod files changed while preparing the download.");

            hash.AppendData(buffer, 0, count);
            output.Write(buffer, 0, count);
        }

        if (copied != length) throw new IOException("Mod files changed while preparing the download.");
        return new(relative, length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    /// <summary>
    /// Answers only additive mod bootstrap requests; an unknown or stale hash never selects another filesystem file.
    /// </summary>
    public async Task ServeAsync(Stream stream, ushort kind, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (kind == ServerModProtocol.ManifestQuery && payload.IsEmpty)
        {
            await ServerModProtocol.WriteAsync(stream, ServerModProtocol.ManifestReply, _descriptor, token).ConfigureAwait(false);
            return;
        }

        if (kind != ServerModProtocol.ArchiveQuery || _archive is null || payload.Length != 64
            || Encoding.ASCII.GetString(payload.Span) != Manifest.ArchiveSha256) return;

        await _uploads.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var input = File.OpenRead(_archive);
            await ServerModProtocol.WriteAsync(stream, ServerModProtocol.ArchiveReply, _descriptor, token).ConfigureAwait(false);
            await input.CopyToAsync(stream, token).ConfigureAwait(false);
        }
        finally
        {
            _uploads.Release();
        }
    }

    /// <summary>
    /// Removes this instance's snapshot after all uploads finish; no original mod files are modified.
    /// </summary>
    public void Dispose()
    {
        if (_archive is not null) File.Delete(_archive);
        _uploads.Dispose();
    }
}
