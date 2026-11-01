using System.Text.Json;
using Meitou.Content;
using Meitou.Data;

namespace Meitou.Networking;

public sealed record ServerConnectionProfile(string Host, int Port, Guid Account, string Token, string CertificateSha256, CompatibilityHello Hello)
{
    public IReadOnlyDictionary<string, string> Rules { get; init; } = new Dictionary<string, string>();
    public string[] GameplayAssets { get; init; } = [];
    public override string ToString() => $"ServerConnectionProfile {{ Host = {Host}, Port = {Port}, Account = {Account}, Token = [redacted] }}";
    public static ServerConnectionProfile Read(string path)
    {
        var profile = JsonSerializer.Deserialize<ServerConnectionProfile>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Missing connection profile.");
        if (string.IsNullOrWhiteSpace(profile.Host) || profile.Port is < 1 or > 65535 || profile.Account == Guid.Empty ||
            !AuthenticationProtocol.ValidCredential(new(profile.Account, profile.Token)) || !ContentManifest.ValidHash(profile.CertificateSha256)
            || profile.Hello is null || profile.Rules is null || profile.GameplayAssets is null) throw new InvalidDataException("Invalid connection profile.");
        return profile;
    }
    public void Write(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
        file.Flush(true);
    }
}

/// <summary>Both executables independently hash their own merge order and configured gameplay files.</summary>
public static class ServerCompatibility
{
    public const string EngineBuild = "meitou-experimental-1";
    public static CompatibilityHello CreateHello(GameInstall install, string engineBuild, IReadOnlyDictionary<string, string> rules,
        IEnumerable<string>? gameplayAssets = null)
    {
        var assets = (gameplayAssets ?? []).Select(relative =>
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or ""))
                throw new InvalidDataException("Gameplay assets must use relative installation paths.");
            return new ContentFile(ContentKind.GameplayAsset, relative.Replace('\\', '/'), GameFiles.Resolve(Path.Combine(install.Root, relative)));
        }).ToArray();
        var manifest = ContentManifest.ReadLoadOrder(LoadOrder.FromInstall(install), assets);
        return new(CompatibilityHandshake.Protocol, engineBuild, ContentManifest.RulesDigest(rules), manifest.Digest);
    }
}
