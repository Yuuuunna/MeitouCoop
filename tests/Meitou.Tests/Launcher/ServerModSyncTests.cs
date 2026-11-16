using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Meitou.Connections;
using Meitou.Content;
using Meitou.Game.Settings;
using Meitou.Launcher;
using Meitou.Networking;
using Meitou.Server;
using Meitou.Tests.Engine;

namespace Meitou.Tests.Launcher;

/// <summary>
/// Serializes process-wide mod-discovery overrides with other test collections.
/// </summary>
[CollectionDefinition("Server mod sync", DisableParallelization = true)]
public sealed class ServerModSyncCollection;

/// <summary>
/// Synthetic mod files and real loopback TLS verify downloads without original game content, Steam, native QUIC or a GPU.
/// </summary>
[Collection("Server mod sync")]
public sealed class ServerModSyncTests : IDisposable
{
    /// <summary>
    /// All packages and cache generations are isolated from the user's installation and launcher settings.
    /// </summary>
    readonly string _directory = Directory.CreateTempSubdirectory("meitou-mod-sync-").FullName;

    /// <summary>
    /// Restores the caller's explicit mod-directory environment after isolated-discovery checks.
    /// </summary>
    readonly string? _previousDirectory = Environment.GetEnvironmentVariable("MEITOU_MODS_DIRECTORY");

    /// <summary>
    /// Restores any caller-selected load-order file.
    /// </summary>
    readonly string? _previousCfg = ModList.ModsCfgOverride;

    /// <summary>
    /// Every synthetic installation starts with its own default mod-discovery rules.
    /// </summary>
    public ServerModSyncTests()
    {
        Environment.SetEnvironmentVariable("MEITOU_MODS_DIRECTORY", null);
        ModList.ClearModsCfg();
    }

    /// <summary>
    /// The public TLS endpoint transfers the exact enabled order and assets, reuses verified caches and preserves legacy discovery.
    /// </summary>
    [Fact]
    public async Task TlsDownloadPreservesOrderAssetsAndLocalConfigurationAndReusesCache()
    {
        var install = Install("Second", "First");
        AddMod(install, "Unused");
        byte[] originalConfig = File.ReadAllBytes(Path.Combine(install.DataDirectory, "mods.cfg"));
        using var package = Package(install);
        using var certificate = GameTlsTests.Certificate();
        await using var server = new TcpDiscoveryServer(new(IPAddress.Loopback, 0), certificate, Information(), mods: package);
        string address = Address(server);
        var token = TestContext.Current.CancellationToken;
        var manifest = await ServerModClient.QueryAsync(address, token);
        Assert.Equal(["Second", "First"], manifest.Mods.Select(m => m.Name));
        Assert.DoesNotContain(manifest.Files, f =>
        {
            return f.Path.Contains("Unused", StringComparison.Ordinal) || f.Path.Contains("gamedata.base", StringComparison.Ordinal);
        });
        Assert.DoesNotContain(_directory, Encoding.UTF8.GetString(manifest.Encode()));

        int downloads = 0;
        async Task Download(string path, CancellationToken cancellation)
        {
            downloads++;
            await ServerModClient.DownloadAsync(address, manifest, path, null, cancellation);
        }

        var cache = new ServerModCache(Path.Combine(_directory, "cache"));
        string prepared = await cache.PrepareAsync(manifest, Download, token);
        Assert.Equal("Second.mod\r\nFirst.mod\r\n", File.ReadAllText(Path.Combine(prepared, "mods.cfg")));
        Assert.Equal("asset:First", File.ReadAllText(Path.Combine(prepared, "mods", "First", "asset.txt")));
        Assert.Equal(prepared, await cache.PrepareAsync(manifest, Download, token));
        Assert.Equal(1, downloads);
        Assert.Equal(originalConfig, File.ReadAllBytes(Path.Combine(install.DataDirectory, "mods.cfg")));
        Assert.Equal(Information().Hello, (await ServerDiscovery.QueryAsync("127.0.0.1", server.Endpoint.Port, token)).Hello);
    }

    /// <summary>
    /// Changes to source folders cannot change an existing server snapshot; corrupted or injected cached files trigger a clean redownload.
    /// </summary>
    [Fact]
    public async Task SnapshotIsImmutableAndDamagedCacheIsRepaired()
    {
        var install = Install("Sample");
        using var package = Package(install);
        File.WriteAllText(Path.Combine(install.ModsDirectory, "Sample", "asset.txt"), "changed after startup");
        using var certificate = GameTlsTests.Certificate();
        await using var server = new TcpDiscoveryServer(new(IPAddress.Loopback, 0), certificate, Information(), mods: package);
        var cache = new ServerModCache(Path.Combine(_directory, "cache"));
        int downloads = 0;
        async Task Download(string path, CancellationToken cancellation)
        {
            downloads++;
            await ServerModClient.DownloadAsync(Address(server), package.Manifest, path, null, cancellation);
        }

        string prepared = await cache.PrepareAsync(package.Manifest, Download, TestContext.Current.CancellationToken);
        string asset = Path.Combine(prepared, "mods", "Sample", "asset.txt");
        Assert.Equal("asset:Sample", File.ReadAllText(asset));
        File.WriteAllText(asset, "corrupted cached asset");
        File.WriteAllText(Path.Combine(prepared, "mods", "Sample", "injected.dll"), "unadvertised executable");
        Assert.Equal(prepared, await cache.PrepareAsync(package.Manifest, Download, TestContext.Current.CancellationToken));
        Assert.Equal(2, downloads);
        Assert.Equal("asset:Sample", File.ReadAllText(asset));
        Assert.False(File.Exists(Path.Combine(prepared, "mods", "Sample", "injected.dll")));
    }

    /// <summary>
    /// An interrupted new version leaves the last verified generation intact and never publishes partially downloaded files.
    /// </summary>
    [Fact]
    public async Task InterruptedDownloadPreservesPreviousGenerationAndCleansStaging()
    {
        var first = Archive("mods/Sample/Sample.mod", "first");
        var second = Archive("mods/Sample/Sample.mod", "second");
        string root = Path.Combine(_directory, "cache");
        var cache = new ServerModCache(root);
        string prepared = await cache.PrepareAsync(first.Manifest, (path, token) => File.WriteAllBytesAsync(path, first.Bytes, token),
            TestContext.Current.CancellationToken);
        async Task Interrupt(string path, CancellationToken token)
        {
            await File.WriteAllBytesAsync(path, second.Bytes[..4], token);
            throw new IOException("Disconnected halfway through download.");
        }

        await Assert.ThrowsAsync<IOException>(() => cache.PrepareAsync(second.Manifest, Interrupt, TestContext.Current.CancellationToken));
        Assert.Equal("first", File.ReadAllText(Path.Combine(prepared, "mods", "Sample", "Sample.mod")));
        Assert.False(Directory.Exists(Path.Combine(root, second.Manifest.Identity)));
        Assert.Empty(Directory.GetDirectories(root, "*.partial-*"));
    }

    /// <summary>
    /// A ZIP matching its outer checksum still cannot install paths outside a declared mod folder or portable filesystem names.
    /// </summary>
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("mods/Sample/../../outside.txt")]
    [InlineData("mods/Sample/file.txt:stream")]
    [InlineData("mods/Sample/CON.txt")]
    [InlineData("profile.json")]
    public async Task UnsafeArchiveMembersNeverPublishOrEscape(string member)
    {
        var invalid = Archive(member, "payload");
        string root = Path.Combine(_directory, "cache");
        var cache = new ServerModCache(root);
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.PrepareAsync(invalid.Manifest,
            (path, token) => File.WriteAllBytesAsync(path, invalid.Bytes, token), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(root));
        Assert.False(File.Exists(Path.Combine(_directory, "outside.txt")));
    }

    /// <summary>
    /// A symlink entry is rejected before extraction even when its name and checksums look like a regular advertised file.
    /// </summary>
    [Fact]
    public async Task SymbolicLinkArchiveMemberIsRejected()
    {
        var invalid = Archive("mods/Sample/Sample.mod", "../outside", symlink: true);
        var cache = new ServerModCache(Path.Combine(_directory, "cache"));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.PrepareAsync(invalid.Manifest,
            (path, token) => File.WriteAllBytesAsync(path, invalid.Bytes, token), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_directory, "cache")));
    }

    /// <summary>
    /// Corruption is checked independently for the complete archive and each decompressed member.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ArchiveAndMemberChecksumFailuresNeverPublish(bool damageArchive)
    {
        var package = Archive("mods/Sample/Sample.mod", "payload");
        var manifest = damageArchive ? package.Manifest with { ArchiveSha256 = new string('0', 64) }
            : package.Manifest with { Files = [package.Manifest.Files[0] with { Sha256 = new string('0', 64) }] };
        string root = Path.Combine(_directory, "cache");
        var cache = new ServerModCache(root);
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.PrepareAsync(manifest,
            (path, token) => File.WriteAllBytesAsync(path, package.Bytes, token), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(root));
    }

    /// <summary>
    /// Cancellation does not turn an incomplete package into a usable cached installation.
    /// </summary>
    [Fact]
    public async Task CancelledDownloadCleansStagingAndDoesNotPublish()
    {
        var package = Archive("mods/Sample/Sample.mod", "payload");
        using var stop = new CancellationTokenSource();
        async Task Cancel(string path, CancellationToken token)
        {
            await File.WriteAllBytesAsync(path, package.Bytes, token);
            stop.Cancel();
        }

        string root = Path.Combine(_directory, "cache");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ServerModCache(root).PrepareAsync(package.Manifest, Cancel, stop.Token));
        Assert.Empty(Directory.GetDirectories(root));
    }

    /// <summary>
    /// An empty server set replaces local/workshop discovery for only the child process and never rewrites the user's mod list.
    /// </summary>
    [Fact]
    public async Task EmptyServerProfileDisablesLocalExtrasWithoutEditingTheInstall()
    {
        var install = Install("LocalExtra");
        string configPath = Path.Combine(install.DataDirectory, "mods.cfg");
        byte[] original = File.ReadAllBytes(configPath);
        File.WriteAllText(configPath, "");
        using var package = Package(install);
        File.WriteAllBytes(configPath, original);
        using var stream = new MemoryStream();
        await package.ServeAsync(stream, ServerModProtocol.ArchiveQuery, Encoding.ASCII.GetBytes(package.Manifest.ArchiveSha256),
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        _ = await ServerModProtocol.ReadAsync(stream, ServerModProtocol.ArchiveReply, TestContext.Current.CancellationToken);
        byte[] archive = stream.ToArray()[(int)stream.Position..];
        string directory = await new ServerModCache(Path.Combine(_directory, "cache")).PrepareAsync(package.Manifest,
            (path, token) => File.WriteAllBytesAsync(path, archive, token), TestContext.Current.CancellationToken);

        var start = new System.Diagnostics.ProcessStartInfo("unused-client");
        ServerModSync.Configure(start, directory);
        Assert.Null(Environment.GetEnvironmentVariable("MEITOU_MODS_DIRECTORY"));
        Environment.SetEnvironmentVariable("MEITOU_MODS_DIRECTORY", start.Environment["MEITOU_MODS_DIRECTORY"]);
        Assert.Empty(ModList.Discover(install));
        Assert.Empty(File.ReadAllText(start.Environment["MEITOU_MODS_CFG"]!));
        Assert.Equal(original, File.ReadAllBytes(configPath));
    }

    /// <summary>
    /// Downloading installed code does not grant execution when the exact advertised code set was refused by the user.
    /// </summary>
    [Fact]
    public async Task RefusedCodeConsentStopsBeforeDownloadingAndSchemaAdvertisesSupport()
    {
        var install = Install("Code");
        File.WriteAllText(Path.Combine(install.ModsDirectory, "Code", "meitou.json"), "{}");
        using var package = ServerModPackage.Create(install, Path.Combine(_directory, "packages"), true,
            new HashSet<string>(["Code"], StringComparer.Ordinal), []);
        using var certificate = GameTlsTests.Certificate();
        await using var server = new TcpDiscoveryServer(new(IPAddress.Loopback, 0), certificate, Information(), mods: package);
        string clientDirectory = Path.Combine(_directory, "client");
        Directory.CreateDirectory(clientDirectory);
        var schema = LauncherSchema.Create();
        Assert.Equal(1, schema.ServerModSyncFormat);
        File.WriteAllText(Path.Combine(clientDirectory, "settings-schema.json"), schema.ToJson());
        var target = new GameTarget(Path.Combine(clientDirectory, "client.exe"), "test", false);
        var sync = new ServerModSync();
        bool prompted = false;
        Task<bool> Refuse(ServerModManifest manifest)
        {
            prompted = true;
            Assert.True(Assert.Single(manifest.Mods).ExecuteCode);
            return Task.FromResult(false);
        }

        Assert.Null(await sync.PrepareAsync(target, Address(server), Refuse, new Progress<string>()));
        Assert.True(prompted);
        Assert.False(sync.IsBusy);
    }

    /// <summary>
    /// Session code permissions include only server-approved mods and remain separate from local trust/settings.
    /// </summary>
    [Fact]
    public async Task CodeProfileContainsOnlyServerApprovedTrustAndSettings()
    {
        var install = Install("Approved", "DataOnly");
        foreach (string name in new[] { "Approved", "DataOnly" })
            File.WriteAllText(Path.Combine(install.ModsDirectory, name, "meitou.json"), "{}");
        Dictionary<string, Dictionary<string, string>> settings = new() { ["approved.code"] = new() { ["rate"] = "2" } };
        using var package = ServerModPackage.Create(install, Path.Combine(_directory, "packages"), true,
            new HashSet<string>(["Approved"], StringComparer.Ordinal), settings);
        Assert.All(package.Manifest.Mods, m => Assert.True(m.HasCode));
        Assert.False(package.Manifest.Mods[1].ExecuteCode);

        using var stream = new MemoryStream();
        await package.ServeAsync(stream, ServerModProtocol.ArchiveQuery, Encoding.ASCII.GetBytes(package.Manifest.ArchiveSha256),
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        _ = await ServerModProtocol.ReadAsync(stream, ServerModProtocol.ArchiveReply, TestContext.Current.CancellationToken);
        byte[] archive = stream.ToArray()[(int)stream.Position..];
        string directory = await new ServerModCache(Path.Combine(_directory, "cache")).PrepareAsync(package.Manifest,
            (path, token) => File.WriteAllBytesAsync(path, archive, token), TestContext.Current.CancellationToken);
        var profile = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(directory, "profile.json")),
            ModSyncJson.Default.ServerModProfile)!;
        Assert.Equal(["Approved"], profile.TrustedCodeMods);
        Assert.Equal("2", profile.ModSettings["approved.code"]["rate"]);
        Assert.NotEqual(package.Manifest.Identity, (package.Manifest with { Mods = [.. package.Manifest.Mods.Reverse()] }).Identity);
    }

    /// <summary>
    /// Sharing can be disabled without preventing ordinary server discovery or writing a mod archive.
    /// </summary>
    [Fact]
    public async Task DisabledSharingKeepsLegacyDiscoveryAndDoesNotCreateAnArchive()
    {
        var install = Install("Sample");
        string packages = Path.Combine(_directory, "packages");
        using var package = ServerModPackage.Create(install, packages, false, new HashSet<string>(), []);
        using var certificate = GameTlsTests.Certificate();
        await using var server = new TcpDiscoveryServer(new(IPAddress.Loopback, 0), certificate, Information(), mods: package);
        var manifest = await ServerModClient.QueryAsync(Address(server), TestContext.Current.CancellationToken);
        Assert.False(manifest.Available);
        Assert.False(Directory.Exists(packages));
        Assert.Equal(Information().Hello, (await ServerDiscovery.QueryAsync("127.0.0.1", server.Endpoint.Port,
            TestContext.Current.CancellationToken)).Hello);
    }

    /// <summary>
    /// Invalid inventories are rejected before creating a download destination, including cross-platform collisions and oversized outputs.
    /// </summary>
    [Fact]
    public async Task InvalidInventoryNeverInvokesDownloader()
    {
        var package = Archive("mods/Sample/Sample.mod", "payload");
        var invalid = new[]
        {
            package.Manifest with { Format = 2 },
            package.Manifest with { Mods = [new("Sample", false, false), new("sample", false, false)] },
            package.Manifest with { Files = [package.Manifest.Files[0] with { Path = "../outside.mod" }] },
            package.Manifest with { Files = [package.Manifest.Files[0] with { Length = ServerModManifest.MaximumBytes + 1 }] },
            package.Manifest with { Mods = [new("Sample", false, true)] },
        };
        Task Download(string path, CancellationToken token) => throw new InvalidOperationException("Downloader must not run.");

        var cache = new ServerModCache(Path.Combine(_directory, "cache"));
        foreach (var manifest in invalid)
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.PrepareAsync(manifest, Download, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_directory, "cache")));
    }

    /// <summary>
    /// An actual truncated TLS archive is refused even when the server sent a valid descriptor before disconnecting.
    /// </summary>
    [Fact]
    public async Task TruncatedTlsDownloadIsRejected()
    {
        var package = Archive("mods/Sample/Sample.mod", "payload");
        using var certificate = GameTlsTests.Certificate();
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task Serve()
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var tls = await GameTls.AcceptAsync(socket.GetStream(), certificate, timeout.Token);
            _ = await WireFrame.ReadAsync(tls, timeout.Token);
            await ServerModProtocol.WriteAsync(tls, ServerModProtocol.ArchiveReply, package.Manifest.Encode(), timeout.Token);
            await tls.WriteAsync(package.Bytes.AsMemory(0, 4), timeout.Token);
        }

        var serving = Serve();
        string address = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        await Assert.ThrowsAsync<EndOfStreamException>(() => ServerModClient.DownloadAsync(address, package.Manifest,
            Path.Combine(_directory, "partial.zip"), null, timeout.Token));
        await serving;
    }

    /// <summary>
    /// Address-only discovery accepts exactly the same approved code evidence/settings used by later game admission.
    /// </summary>
    [Fact]
    public async Task DeviceDiscoveryUsesApprovedCodeEvidenceAndRejectsChangedSettings()
    {
        var install = Install("Code");
        string folder = Path.Combine(install.ModsDirectory, "Code");
        File.WriteAllText(Path.Combine(folder, "meitou.json"),
            """{"id":"sample.code","apiVersion":2,"assemblies":["Code.dll"],"entry":"Code.Entry"}""");
        File.WriteAllText(Path.Combine(folder, "Code.dll"), "synthetic bytes; this file must never execute");
        var trusted = new HashSet<string>(["Code"], StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, string>> settings = new() { ["sample.code"] = new() { ["rate"] = "2" } };
        var rules = ServerCompatibility.WithCodeSettings(new Dictionary<string, string>(), install, trusted, settings).ToDictionary();
        var hello = ServerCompatibility.CreateHello(install, ServerCompatibility.EngineBuild, rules, [], trusted, settings);
        using var certificate = GameTlsTests.Certificate();
        await using var server = new TcpDiscoveryServer(new(IPAddress.Loopback, 0), certificate, new(hello, rules, [], true));
        string identities = Path.Combine(_directory, "identities");
        var profile = await DeviceConnection.PrepareAsync(install, Address(server), TestContext.Current.CancellationToken,
            identities, trusted, settings);
        Assert.Equal(hello, profile.Hello);
        await Assert.ThrowsAsync<InvalidDataException>(() => DeviceConnection.PrepareAsync(install, Address(server),
            TestContext.Current.CancellationToken, identities, trusted, new Dictionary<string, Dictionary<string, string>>()));
    }

    /// <summary>
    /// Creates a minimal synthetic install; these bytes are generated by the test, never original game content.
    /// </summary>
    GameInstall Install(params string[] enabled)
    {
        string root = Path.Combine(_directory, "install");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "data", "gamedata.base"), "synthetic base file");
        var install = GameInstall.Open(root);
        foreach (string name in enabled) AddMod(install, name);
        File.WriteAllLines(Path.Combine(install.DataDirectory, "mods.cfg"), enabled.Select(n => n + ".mod"));
        return install;
    }

    /// <summary>
    /// Adds a tiny database placeholder and a separate asset to exercise complete-folder downloads.
    /// </summary>
    static void AddMod(GameInstall install, string name)
    {
        string folder = Path.Combine(install.ModsDirectory, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".mod"), "database:" + name);
        File.WriteAllText(Path.Combine(folder, "asset.txt"), "asset:" + name);
    }

    /// <summary>
    /// Builds a normal snapshot without permitting executable code.
    /// </summary>
    ServerModPackage Package(GameInstall install) => ServerModPackage.Create(install, Path.Combine(_directory, "packages"), true,
        new HashSet<string>(StringComparer.Ordinal), []);

    /// <summary>
    /// A valid minimal legacy discovery message independent of gameplay and real content.
    /// </summary>
    static ServerInformation Information() => new(new(CompatibilityHandshake.Protocol, "test",
        ContentManifest.RulesDigest(new Dictionary<string, string>()), new string('0', 64)), [], [], true);

    /// <summary>
    /// The ephemeral public bootstrap endpoint selected by the operating system.
    /// </summary>
    static string Address(TcpDiscoveryServer server) => $"127.0.0.1:{server.Endpoint.Port}";

    /// <summary>
    /// Creates a ZIP whose outer checksum is valid, with independently controlled inventory and entry metadata.
    /// </summary>
    static (ServerModManifest Manifest, byte[] Bytes) Archive(string member, string text, bool symlink = false)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(member);
            if (symlink) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var output = entry.Open();
            output.Write(payload);
        }

        byte[] zip = bytes.ToArray();
        var file = new ServerModFile("mods/Sample/Sample.mod", payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var manifest = new ServerModManifest(1, true, zip.Length, Convert.ToHexString(SHA256.HashData(zip)), [new("Sample", false, false)], [file], []);
        return (manifest, zip);
    }

    /// <summary>
    /// Restores process-wide overrides and removes only this fixture's temporary files.
    /// </summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MEITOU_MODS_DIRECTORY", _previousDirectory);
        ModList.ClearModsCfg();
        if (_previousCfg is not null) ModList.UseModsCfg(_previousCfg);
        Directory.Delete(_directory, recursive: true);
    }
}
