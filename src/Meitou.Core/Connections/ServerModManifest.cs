using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Connections;

/// <summary>
/// One enabled mod, in server load order; code executes only when the server explicitly trusts it.
/// </summary>
public sealed record ServerModEntry(string Name, bool HasCode, bool ExecuteCode);

/// <summary>
/// A portable archive member and the exact uncompressed bytes expected from it.
/// </summary>
public sealed record ServerModFile(string Path, long Length, string Sha256);

/// <summary>
/// A startup snapshot of enabled mod folders. Original base-game files and local filesystem paths are never advertised.
/// </summary>
public sealed record ServerModManifest(int Format, bool Available, long ArchiveBytes, string ArchiveSha256,
    ServerModEntry[] Mods, ServerModFile[] Files, Dictionary<string, Dictionary<string, string>> ModSettings)
{
    /// <summary>
    /// Bounds downloads and decompression independently of compression ratio.
    /// </summary>
    public const long MaximumBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>
    /// The response for servers that have not enabled file sharing.
    /// </summary>
    public static ServerModManifest Unavailable { get; } = new(1, false, 0, "", [], [], []);

    /// <summary>
    /// Binds the order, file inventory, archive and executable-code permissions into a cache and consent identity.
    /// </summary>
    [JsonIgnore]
    public string Identity => Convert.ToHexStringLower(SHA256.HashData(Encode()));

    /// <summary>
    /// Serializes with generated metadata for the Native AOT launcher.
    /// </summary>
    public byte[] Encode()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, ModSyncJson.Default.ServerModManifest);
        if (bytes.Length > ServerModProtocol.MaximumManifestBytes) throw new InvalidDataException("Server mod manifest is too large.");

        return bytes;
    }

    /// <summary>
    /// Rejects unknown schemas, unsafe names, duplicate paths, excessive sizes and inconsistent code declarations.
    /// </summary>
    public void Validate()
    {
        if (Format != 1 || Mods is null || Files is null || ModSettings is null || Mods.Length > 1024 || Files.Length > 100000
            || ArchiveBytes < 0 || ArchiveBytes > MaximumBytes || Available && (ArchiveBytes == 0 || !ValidHash(ArchiveSha256)))
            throw new InvalidDataException("Invalid server mod manifest.");
        if (!Available && (ArchiveBytes != 0 || ArchiveSha256 != "" || Mods.Length != 0 || Files.Length != 0 || ModSettings.Count != 0))
            throw new InvalidDataException("Invalid disabled mod sharing response.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in Mods)
        {
            if (mod is null || !PortablePart(mod.Name) || !names.Add(mod.Name) || Content.ModList.CoreModNames.Contains(mod.Name)
                || mod.ExecuteCode && !mod.HasCode)
                throw new InvalidDataException("Invalid or duplicate server mod name.");
        }

        ValidateFiles(Mods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal));
        if (ModSettings.Count > 1024 || ModSettings.Any(p => !Content.CodeModManifest.IsValidId(p.Key) || p.Value is null
            || p.Value.Count > 256 || p.Value.Any(v => string.IsNullOrWhiteSpace(v.Key) || v.Key.Length > 256 || v.Value is null || v.Value.Length > 2048)))
            throw new InvalidDataException("Invalid server code-mod settings.");
    }

    /// <summary>
    /// Requires every member to live in a declared mod folder and every declared mod to contain its database and optional code manifest.
    /// </summary>
    void ValidateFiles(HashSet<string> names)
    {
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null || !PortablePath(file.Path) || file.Length < 0 || file.Length > MaximumBytes || !ValidHash(file.Sha256)
                || !paths.Add(file.Path) || file.Path.Split('/') is not ["mods", var name, ..] || !names.Contains(name))
                throw new InvalidDataException("Invalid server mod file path or fingerprint.");
            total = checked(total + file.Length);
            if (total > MaximumBytes) throw new InvalidDataException("Server mods exceed the unpacked size limit.");
        }

        foreach (var mod in Mods)
        {
            if (!paths.Contains($"mods/{mod.Name}/{mod.Name}.mod")
                || mod.HasCode != paths.Contains($"mods/{mod.Name}/meitou.json"))
                throw new InvalidDataException("Server mod inventory is missing its database or code manifest.");
        }
    }

    /// <summary>
    /// Portable relative files only: no drive, traversal, alternate stream, empty segment or Windows device alias.
    /// </summary>
    public static bool PortablePath(string? path) => path is { Length: > 0 and <= 1024 } && path.Split('/').All(PortablePart);

    /// <summary>
    /// A filename component with the same meaning on supported filesystems.
    /// </summary>
    static bool PortablePart(string? part)
    {
        if (string.IsNullOrWhiteSpace(part) || part.Length > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*')) return false;

        string stem = part.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] is >= '1' and <= '9');
    }

    /// <summary>
    /// A complete hexadecimal SHA-256 fingerprint.
    /// </summary>
    public static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
}

/// <summary>
/// Explicit per-process code permissions; stored outside mod folders and never merged into the player's saved settings.
/// </summary>
public sealed record ServerModProfile(string[] TrustedCodeMods, Dictionary<string, Dictionary<string, string>> ModSettings)
{
    /// <summary>
    /// The launcher passes only the verified, consented profile to its child process.
    /// </summary>
    public const string EnvironmentVariable = "MEITOU_SERVER_MOD_PROFILE";

    /// <summary>
    /// Reads session permissions when present; an invalid explicit profile never falls back to local trust.
    /// </summary>
    public static ServerModProfile? FromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (new FileInfo(path).Length > ServerModProtocol.MaximumManifestBytes) throw new InvalidDataException("Server mod profile is too large.");

        var profile = JsonSerializer.Deserialize(File.ReadAllBytes(path), ModSyncJson.Default.ServerModProfile)
            ?? throw new InvalidDataException("Missing server mod profile.");
        if (profile.TrustedCodeMods is null || profile.ModSettings is null)
            throw new InvalidDataException("Invalid server mod profile.");

        return profile;
    }
}

/// <summary>
/// Strict generated JSON shared by the server and Native AOT launcher.
/// </summary>
[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8)]
[JsonSerializable(typeof(ServerModManifest))]
[JsonSerializable(typeof(ServerModProfile))]
public sealed partial class ModSyncJson : JsonSerializerContext;
