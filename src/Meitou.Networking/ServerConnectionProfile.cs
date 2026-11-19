using System.Text.Json;
using Meitou.Content;
using Meitou.Data;

namespace Meitou.Networking;
public sealed record ServerConnectionProfile(string Host, int Port, Guid Account, string Token, CompatibilityHello Hello)
{
    public IReadOnlyDictionary<string, string> Rules { get; init; } = new Dictionary<string, string>();
    public string[] GameplayAssets { get; init; } = [];

    public override string ToString() => $"ServerConnectionProfile {{ Host = {Host}, Port = {Port}, Account = {Account}, Token = [redacted] }}";
    public static ServerConnectionProfile Read(string path)
    {
        var profile = JsonSerializer.Deserialize<ServerConnectionProfile>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Missing connection profile.");
        if (string.IsNullOrWhiteSpace(profile.Host) || profile.Port is < 1 or > 65535 || profile.Account == Guid.Empty || !AuthenticationProtocol.ValidCredential(new(profile.Account, profile.Token)) || profile.Hello is null || profile.Rules is null || profile.GameplayAssets is null)
            throw new InvalidDataException("Invalid connection profile.");
        return profile;
    }

    public void Write(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
        file.Flush(true);
    }
}

/// <summary>
/// Both executables independently hash their own merge order and configured gameplay files.
/// </summary>
public static class ServerCompatibility
{
    /// <summary>
    /// Both endpoints must accept effect definitions/countdowns, head targets, faction counts and the current authoritative gameplay rules.
    /// Discovery metadata is opaque to older clients, so their own build identity must reject admission.
    /// </summary>
    public const string EngineBuild = "meitou-upstream-315d26ec-authority-1";
    // Hash without loading DLLs: connecting never grants permission to execute installed code.
    public static IEnumerable<ContentFile> CodeEvidence(GameInstall install, IReadOnlySet<string>? trusted)
    {
        if (trusted is null || trusted.Count == 0)
            yield break;
        var discovered = ModList.Discover(install).ToDictionary(m => m.Name, StringComparer.Ordinal);
        foreach (string name in ModList.ReadEnabled(install))
        {
            if (!trusted.Contains(name) || !discovered.TryGetValue(name, out var mod) || !mod.HasCode)
                continue;
            var manifest = CodeModManifest.Read(GameFiles.Resolve(mod.CodeManifestPath));
            string prefix = "code/" + name + "/";
            // Include private managed/native dependencies and configuration as well as declared entry DLLs.
            foreach (string file in Directory.EnumerateFiles(mod.Folder, "*", SearchOption.AllDirectories).OrderBy(f => Path.GetRelativePath(mod.Folder, f).Replace('\\', '/'), StringComparer.Ordinal))
                yield return new ContentFile(ContentKind.GameplayAsset, prefix + Path.GetRelativePath(mod.Folder, file).Replace('\\', '/'), file);
            foreach (string assembly in manifest.Assemblies)
                if (!File.Exists(GameFiles.Resolve(Path.Combine(mod.Folder, assembly))))
                    throw new InvalidDataException($"Code mod {name} is missing {assembly}.");
        }
    }

    /// <summary>
    /// Explicit gameplay files resolve exactly as map/resource readers do. The enabled RE_Kenshi configuration and its final override targets
    /// are evidence even for data-only mods; no other resource directory is scanned and target paths stay local.
    /// </summary>
    public static IEnumerable<ContentFile> GameplayEvidence(GameInstall install, IEnumerable<ContentFile> assets, IReadOnlySet<string>? trusted)
    {
        var rebinds = ReKenshiRebinds.For(install);
        foreach (var asset in assets)
        {
            string path = GameFiles.Resolve(asset.Path);
            yield return rebinds.TryRedirect(path, out var target)
                ? asset with { Path = target, AllowMissing = true } : asset with { Path = path };
        }
        foreach (var file in CodeEvidence(install, trusted)) yield return file;
        foreach (var mod in ReKenshiFile.EnabledMods(install))
        {
            string path = GameFiles.Resolve(Path.Combine(mod.Folder, ModList.NativePluginFile));
            if (File.Exists(path)) yield return new(ContentKind.GameplayAsset, "rebind-config/" + mod.Name + "/RE_Kenshi.json", path);
        }
        foreach (var (source, target) in rebinds.Files)
        {
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
            yield return new ContentFile(ContentKind.GameplayAsset, "rebind/" + key, target) { AllowMissing = true };
        }
    }

    public static IReadOnlyDictionary<string, string> WithCodeSettings(IReadOnlyDictionary<string, string> rules, GameInstall install, IReadOnlySet<string>? trusted, IReadOnlyDictionary<string, Dictionary<string, string>>? settings, IEnumerable<string>? codeEdits = null)
    {
        var result = rules.Where(p => !p.Key.StartsWith("code-setting/", StringComparison.Ordinal)
            && !p.Key.StartsWith("code-data/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        int edit = 0;
        foreach (string fingerprint in codeEdits ?? [])
            result.Add("code-data/" + (edit++).ToString(System.Globalization.CultureInfo.InvariantCulture), fingerprint);
        var mods = ModList.Discover(install).ToDictionary(m => m.Name, StringComparer.Ordinal);
        foreach (string name in ModList.ReadEnabled(install))
        {
            if (trusted?.Contains(name) != true || !mods.TryGetValue(name, out var mod) || !mod.HasCode)
                continue;
            string id = CodeModManifest.Read(GameFiles.Resolve(mod.CodeManifestPath)).Id;
            if (settings is null || !settings.TryGetValue(id, out var values))
                continue;
            foreach (var(key, value)in values)
                result.Add($"code-setting/{id}/{key}", value);
        }

        return result;
    }

    public static CompatibilityHello CreateHello(GameInstall install, string engineBuild, IReadOnlyDictionary<string, string> rules, IEnumerable<string>? gameplayAssets = null, IReadOnlySet<string>? trustedCodeMods = null, IReadOnlyDictionary<string, Dictionary<string, string>>? modSettings = null, IEnumerable<string>? codeEdits = null)
    {
        var assets = (gameplayAssets ?? []).Select(relative =>
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or ""))
                throw new InvalidDataException("Gameplay assets must use relative installation paths.");
            return new ContentFile(ContentKind.GameplayAsset, relative.Replace('\\', '/'), GameFiles.Resolve(Path.Combine(install.Root, relative)));
        }).ToArray();
        var effective = WithCodeSettings(rules, install, trustedCodeMods, modSettings, codeEdits);
        var manifest = ContentManifest.ReadLoadOrder(LoadOrder.FromInstall(install), GameplayEvidence(install, assets, trustedCodeMods));
        return new(CompatibilityHandshake.Protocol, engineBuild, ContentManifest.RulesDigest(effective), manifest.Digest);
    }
}
