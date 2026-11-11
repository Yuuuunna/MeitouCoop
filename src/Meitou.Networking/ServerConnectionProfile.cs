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
    // Rule semantics and the public GameplayRules shape changed with the upstream layout.
    // Discovery metadata is opaque to older clients, so their own build identity must reject admission.
    public const string EngineBuild = "meitou-upstream-9a79fb665-authority-1";
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

    public static IReadOnlyDictionary<string, string> WithCodeSettings(IReadOnlyDictionary<string, string> rules, GameInstall install, IReadOnlySet<string>? trusted, IReadOnlyDictionary<string, Dictionary<string, string>>? settings)
    {
        var result = rules.Where(p => !p.Key.StartsWith("code-setting/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
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

    public static CompatibilityHello CreateHello(GameInstall install, string engineBuild, IReadOnlyDictionary<string, string> rules, IEnumerable<string>? gameplayAssets = null, IReadOnlySet<string>? trustedCodeMods = null, IReadOnlyDictionary<string, Dictionary<string, string>>? modSettings = null)
    {
        var assets = (gameplayAssets ?? []).Select(relative =>
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or ""))
                throw new InvalidDataException("Gameplay assets must use relative installation paths.");
            return new ContentFile(ContentKind.GameplayAsset, relative.Replace('\\', '/'), GameFiles.Resolve(Path.Combine(install.Root, relative)));
        }).ToArray();
        var codeFiles = CodeEvidence(install, trustedCodeMods);
        var manifest = ContentManifest.ReadLoadOrder(LoadOrder.FromInstall(install), assets.Concat(codeFiles));
        return new(CompatibilityHandshake.Protocol, engineBuild, ContentManifest.RulesDigest(WithCodeSettings(rules, install, trustedCodeMods, modSettings)), manifest.Digest);
    }
}