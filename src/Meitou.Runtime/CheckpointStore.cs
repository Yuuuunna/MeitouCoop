using Meitou.Simulation.Saving;
using System.Security.Cryptography;
using System.Text.Json;
using Meitou.Data.Save;

namespace Meitou.Runtime;
public enum CheckpointStage
{
    DataWritten,
    Sealed
}

public sealed record StoredCheckpoint(Guid Id, SaveGame World, byte[] Metadata);
/// <summary>
/// Immutable checkpoint directories and one atomically replaced current pointer. A single writer owns the filesystem lock;
/// readers can finish using an older checkpoint. This container does not define the multiplayer metadata schema.
/// </summary>
public sealed class CheckpointStore
{
    const int Format = 1;
    const string PointerName = "current.json", ManifestName = "manifest.json", MetadataName = "multiplayer.json";
    readonly string _root;
    public CheckpointStore(string root) => this._root = Path.GetFullPath(root);
    sealed record Pointer(int Version, Guid Id, string ManifestHash);
    sealed record Entry(string Name, long Bytes, string Sha256);
    sealed record Manifest(int Version, Guid Id, Entry[] Files);
    /// <summary>
    /// SaveCapture and metadata capture must come from the same serialized world boundary. The caller owns both detached inputs
    /// until this returns. Observer callbacks run before publication only; throwing leaves the current checkpoint unchanged.
    /// </summary>
    public Guid Commit(SaveGame world, ReadOnlyMemory<byte> metadata, Action<CheckpointStage>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateMetadata(metadata);
        var files = world.AllFiles().Select(f => (Name: "world/" + SafeName(f.Name), f.File)).ToArray();
        if (files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new InvalidDataException("Duplicate checkpoint file name.");
        Directory.CreateDirectory(_root);
        using var writer = new FileStream(Path.Combine(_root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var snapshots = Path.Combine(_root, "checkpoints");
        Directory.CreateDirectory(snapshots);
        Guid id = Guid.NewGuid();
        string stage = Path.Combine(snapshots, ".pending-" + id.ToString("N"));
        string sealedPath = Path.Combine(snapshots, id.ToString("N"));
        Directory.CreateDirectory(stage);
        List<Entry> entries = [];
        foreach (var(name, file)in files)
            Add(name, file.ToBytes());
        if (world.Portraits is not null)
            Add("world/" + SaveFolder.PortraitName, world.Portraits);
        Add(MetadataName, metadata.Span);
        ValidateWorld(SaveGame.Load(Path.Combine(stage, "world")));
        observer?.Invoke(CheckpointStage.DataWritten);
        var manifest = new Manifest(Format, id, entries.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray());
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        WriteNew(Path.Combine(stage, ManifestName), manifestBytes);
        Directory.Move(stage, sealedPath);
        observer?.Invoke(CheckpointStage.Sealed);
        byte[] pointer = JsonSerializer.SerializeToUtf8Bytes(new Pointer(Format, id, Hash(manifestBytes)));
        string next = Path.Combine(_root, "current-" + id.ToString("N") + ".tmp");
        WriteNew(next, pointer);
        // Same-directory rename: at every observable boundary current names either the old or the complete new bundle.
        File.Move(next, Path.Combine(_root, PointerName), overwrite: true);
        return id;
        void Add(string name, ReadOnlySpan<byte> bytes)
        {
            string path = Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteNew(path, bytes);
            entries.Add(new Entry(name, bytes.Length, Hash(bytes)));
        }
    }

    /// <summary>
    /// No pointer means no committed checkpoint. Corruption is an error; never silently roll back a successful creation.
    /// </summary>
    public StoredCheckpoint? LoadCurrent()
    {
        byte[] pointerBytes;
        try
        {
            pointerBytes = ReadBounded(Path.Combine(_root, PointerName), 4096, sharing: FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        var pointer = Decode<Pointer>(pointerBytes);
        if (pointer.Version != Format || pointer.Id == Guid.Empty || !ValidHash(pointer.ManifestHash))
            throw new InvalidDataException("Invalid checkpoint pointer.");
        string snapshot = Path.Combine(_root, "checkpoints", pointer.Id.ToString("N"));
        RejectLink(snapshot);
        byte[] manifestBytes = ReadBounded(Path.Combine(snapshot, ManifestName), 16 * 1024 * 1024);
        if (Hash(manifestBytes) != pointer.ManifestHash)
            throw new InvalidDataException("Checkpoint manifest checksum differs.");
        var manifest = Decode<Manifest>(manifestBytes);
        if (manifest.Version != Format || manifest.Id != pointer.Id || manifest.Files is null)
            throw new InvalidDataException("Checkpoint identity or format differs.");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ManifestName
        };
        foreach (var entry in manifest.Files)
        {
            if (entry is null || SafeName(entry.Name) != entry.Name || !expected.Add(entry.Name) || entry.Bytes < 0 || !ValidHash(entry.Sha256) || entry.Name != MetadataName && !entry.Name.StartsWith("world/", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid checkpoint manifest entry.");
        }

        var actual = EnumerateFiles(snapshot).ToArray();
        if (actual.Length != expected.Count || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected) || !expected.Contains(MetadataName) || !expected.Contains("world/quick.save"))
            throw new InvalidDataException("Checkpoint files are missing or unexpected.");
        foreach (var entry in manifest.Files)
        {
            using var stream = File.OpenRead(Path.Combine(snapshot, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
            if (stream.Length != entry.Bytes || Convert.ToHexString(SHA256.HashData(stream)) != entry.Sha256)
                throw new InvalidDataException($"Checkpoint checksum differs: {entry.Name}.");
        }

        byte[] metadata = ReadBounded(Path.Combine(snapshot, MetadataName), 128 * 1024 * 1024);
        ValidateMetadata(metadata);
        var world = SaveGame.Load(Path.Combine(snapshot, "world"));
        ValidateWorld(world);
        return new StoredCheckpoint(pointer.Id, world, metadata);
    }

    static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static void ValidateWorld(SaveGame world)
    {
        if (world.Problems.Count != 0)
            throw new InvalidDataException("Checkpoint world is incomplete: " + string.Join("; ", world.Problems));
    }

    static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    static T Decode<T>(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty checkpoint document.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed checkpoint document.", e);
        }
    }

    static void ValidateMetadata(ReadOnlyMemory<byte> metadata)
    {
        if (metadata.Length > 128 * 1024 * 1024)
            throw new InvalidDataException("Checkpoint metadata is too large.");
        try
        {
            using var document = JsonDocument.Parse(metadata);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int value) || value < 1)
                throw new InvalidDataException("Checkpoint metadata needs a schema version.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed checkpoint metadata.", e);
        }
    }

    static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name))
            throw new InvalidDataException("Invalid checkpoint path.");
        string normalized = name.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c))))
            throw new InvalidDataException("Invalid checkpoint path.");
        return normalized;
    }

    static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Checkpoint links are not supported.");
    }

    static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLink(path);
                if (Directory.Exists(path))
                    pending.Push(path);
                else
                    yield return Path.GetRelativePath(root, path).Replace('\\', '/');
            }
    }

    static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    static byte[] ReadBounded(string path, int maximum, FileShare sharing = FileShare.Read)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, sharing);
        if (stream.Length > maximum)
            throw new InvalidDataException("Checkpoint document is too large.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}