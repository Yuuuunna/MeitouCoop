using System.Security.Authentication;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using Meitou.Content;
using Meitou.Networking;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Fact]
    public async Task LobbyRecoversRejectedSavedQualificationWithoutASecondConnection()
    {
        var information = DeviceInformation();
        await using var scene = new Scene(information: information, creations: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var credential = DeviceCredential();
        var session = await RemoteGameSession.ConnectAsync("localhost", scene.Server.Endpoint.Port, information.Hello,
            credential, new(scene.Data.Db), cancellationToken: timeout.Token, deviceIdentity: true);
        Directory.CreateDirectory(Path.Combine(scene.Root, "data"));
        File.WriteAllBytes(Path.Combine(scene.Root, "data", "gamedata.base"), []);
        await using var lobby = new Meitou.Game.OnlineLobby(GameInstall.Open(scene.Root), scene.Data.Db);
        string journal = Path.Combine(scene.Root, "stale.creation.json");
        var request = new Meitou.Simulation.PlayerCreationRequest(Guid.NewGuid(), 9999, 1, "40-t");
        File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(request));
        await lobby.PrepareAdmissionAsync(session, journal).WaitAsync(timeout.Token);
        Assert.NotNull(lobby.Catalog);
        Assert.NotEmpty(lobby.Catalog.Starts);
        Assert.Null(lobby.Request);
        Assert.False(File.Exists(journal));
        Assert.Null(session.State.Disconnection);
        Assert.Empty(session.State.Admission.State.Roles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryRetriesOneInterruptedReadAndStopsAfterTheSecond(bool alwaysClose)
    {
        var information = DeviceInformation();
        using var certificate = GameTlsTests.Certificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int requests = 0;
        var serving = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var tls = await GameTls.AcceptAsync(tcp.GetStream(), certificate, timeout.Token);
                var query = await WireFrame.ReadAsync(tls, timeout.Token);
                Assert.Equal(WireMessage.ServerInformationQuery, query!.Kind);
                requests++;
                if (i == 1 && !alwaysClose) await WireFrame.WriteAsync(tls, ServerDiscovery.Encode(information), timeout.Token);
                await tls.ShutdownAsync().WaitAsync(timeout.Token);
            }
        }, timeout.Token);
        var pending = ServerDiscovery.QueryAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, null, timeout.Token);
        if (alwaysClose) await Assert.ThrowsAnyAsync<IOException>(() => pending);
        else
        {
            var reply = await pending;
            Assert.Equal(information.Hello, reply.Information.Hello);
        }
        await serving;
        Assert.Equal(2, requests);
    }

    static ServerInformation DeviceInformation(bool enabled = true) => new(
        Hello() with { RulesDigest = ContentManifest.RulesDigest(new Dictionary<string, string>()) }, [], [], enabled);
    static AuthenticationRequest DeviceCredential() => new(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public async Task DeviceDiscoveryRegistersOnceAndReconnectsTheSamePlayer()
    {
        var information = DeviceInformation();
        await using var scene = new Scene(information: information);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int count = scene.Accounts.List().Count;
        var discovered = await ServerDiscovery.QueryAsync("localhost", scene.Server.Endpoint.Port, null, timeout.Token);
        Assert.Equal(information.Hello, discovered.Information.Hello);
        Assert.Equal(scene.Certificate.GetCertHashString(HashAlgorithmName.SHA256), discovered.Certificate);
        Assert.Equal(count, scene.Accounts.List().Count); // Public discovery must not register an account.
        var credential = DeviceCredential();
        Meitou.Simulation.PlayerId player;
        await using (var first = await RemoteGameSession.ConnectAsync("localhost", scene.Server.Endpoint.Port, information.Hello,
            credential, new(scene.Data.Db), discovered.Certificate, timeout.Token, deviceIdentity: true))
            player = first.State.Admission.Identity.Player;
        await using var again = await RemoteGameSession.ConnectAsync("localhost", scene.Server.Endpoint.Port, information.Hello,
            credential, new(scene.Data.Db), discovered.Certificate, timeout.Token, deviceIdentity: true);
        Assert.Equal(player, again.State.Admission.Identity.Player);
        Assert.Equal(count + 1, scene.Accounts.List().Count);
        Assert.DoesNotContain(credential.Token, File.ReadAllText(Path.Combine(scene.Root, "accounts.json")));
    }

    [Fact]
    public async Task WrongDeviceTokenAndDisabledAccountsCannotReplaceAnIdentity()
    {
        var information = DeviceInformation();
        await using var scene = new Scene(information: information);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string pin = scene.Certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var credential = DeviceCredential();
        await using (var first = await RemoteGameSession.ConnectAsync("localhost", scene.Server.Endpoint.Port, information.Hello,
            credential, new(scene.Data.Db), pin, timeout.Token, deviceIdentity: true)) { }
        int count = scene.Accounts.List().Count;
        await Assert.ThrowsAsync<AuthenticationException>(() => RemoteGameSession.ConnectAsync("localhost", scene.Server.Endpoint.Port, information.Hello,
            credential with { Token = DeviceCredential().Token }, new(scene.Data.Db), pin, timeout.Token, deviceIdentity: true));
        Assert.NotNull(scene.Accounts.Authenticate(credential));
        scene.Accounts.SetEnabled(credential.Account, false);
        await Assert.ThrowsAsync<AuthenticationException>(() => RemoteGameSession.ConnectAsync("localhost", scene.Server.Endpoint.Port, information.Hello,
            credential, new(scene.Data.Db), pin, timeout.Token, deviceIdentity: true));
        Assert.Equal(count, scene.Accounts.List().Count);
        Assert.False(scene.Accounts.List().Single(a => a.Id == credential.Account).Enabled);
    }

    [Fact]
    public async Task PreReleaseDiscoveryAcceptsAChangedCertificateWithoutRegisteringAnAccount()
    {
        await using var scene = new Scene(information: DeviceInformation());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var previous = GameTlsTests.Certificate();
        int count = scene.Accounts.List().Count;
        var discovered = await ServerDiscovery.QueryAsync("localhost", scene.Server.Endpoint.Port,
            previous.GetCertHashString(HashAlgorithmName.SHA256), timeout.Token);
        Assert.Equal(scene.Certificate.GetCertHashString(HashAlgorithmName.SHA256), discovered.Certificate);
        Assert.Equal(count, scene.Accounts.List().Count);
    }

    [Fact]
    public async Task DisabledRegistrationStillAuthenticatesExistingIdentities()
    {
        var information = DeviceInformation(enabled: false);
        await using var scene = new Scene(information: information);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string pin = scene.Certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var existing = scene.Actors[0];
        await using var client = await RemoteGameSession.ConnectAsync("localhost", scene.Server.Endpoint.Port, information.Hello,
            new(existing.Credential.Account, existing.Credential.Token), new(scene.Data.Db), pin, timeout.Token, deviceIdentity: true);
        Assert.Equal(existing.Player, client.State.Admission.Identity.Player);
        await Assert.ThrowsAsync<AuthenticationException>(() => RemoteGameSession.ConnectAsync("localhost", scene.Server.Endpoint.Port, information.Hello,
            DeviceCredential(), new(scene.Data.Db), pin, timeout.Token, deviceIdentity: true));
        Assert.Equal(3, scene.Accounts.List().Count);
    }

    [Fact, Slow]
    public async Task SavedDeviceIdentitySurvivesPreparationAndRefusesCorruptLocalState()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "No Kenshi install configured.");
        var hello = ServerCompatibility.CreateHello(install!, ServerCompatibility.EngineBuild, new Dictionary<string, string>());
        await using var scene = new Scene(information: new(hello, [], [], true));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string directory = Path.Combine(scene.Root, "device-state");
        string address = "localhost:" + scene.Server.Endpoint.Port;
        var first = await DeviceConnection.PrepareAsync(install!, address, timeout.Token, directory);
        var second = await DeviceConnection.PrepareAsync(install!, address, timeout.Token, directory);
        Assert.Equal(first.Account, second.Account);
        Assert.Equal(first.Token, second.Token);
        Assert.Equal(first.CertificateSha256, second.CertificateSha256);
        string state = Directory.GetFiles(directory, "*.json").Single();
        await File.WriteAllTextAsync(state, "null", timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => DeviceConnection.PrepareAsync(install!, address, timeout.Token, directory));
        Assert.Equal("null", await File.ReadAllTextAsync(state, timeout.Token));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("/outside")]
    [InlineData("data//file")]
    public void DiscoveryCannotRequestFilesOutsideRelativeInstallPaths(string path) =>
        Assert.Throws<InvalidDataException>(() => ServerDiscovery.Encode(DeviceInformation() with { GameplayAssets = [path] }));

    [Theory]
    [InlineData("localhost", "localhost", 27800)]
    [InlineData("localhost:443", "localhost", 443)]
    [InlineData("[::1]:27801", "::1", 27801)]
    public void AddressParsingPreservesExplicitDefaultTlsPortAndIpv6(string address, string host, int port) =>
        Assert.Equal((host, port), DeviceConnection.ParseAddress(address));
}
