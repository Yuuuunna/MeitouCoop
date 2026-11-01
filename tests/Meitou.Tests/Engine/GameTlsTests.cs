using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meitou.Networking;

namespace Meitou.Tests.Engine;

public class GameTlsTests
{
    internal static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows Schannel cannot use CreateSelfSigned's ephemeral key. Import a temporary user-key container; disposal removes it.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
    }
    [Fact]
    public async Task PinnedTlsAuthenticatesServerBeforeCredentialFramesTravel()
    {
        using var certificate = Certificate(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token); using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        var accepting = GameTls.AcceptAsync(server.GetStream(), certificate, timeout.Token);
        var connecting = GameTls.ConnectAsync(client.GetStream(), "localhost", certificate.GetCertHashString(HashAlgorithmName.SHA256), timeout.Token);
        await Task.WhenAll(accepting, connecting);
        await using var clientTls = await connecting;
        await using var serverTls = await accepting;
        Assert.True(clientTls.IsAuthenticated && clientTls.IsEncrypted); Assert.True(serverTls.IsAuthenticated && serverTls.IsEncrypted);
        var request = new AuthenticationRequest(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        await WireFrame.WriteAsync(clientTls, AuthenticationProtocol.Encode(request), timeout.Token);
        Assert.Equal(request, AuthenticationProtocol.Decode((await WireFrame.ReadAsync(serverTls, timeout.Token))!));
    }
    [Fact]
    public async Task WrongCertificatePinFailsInsteadOfFallingBackToUnverifiedTransport()
    {
        using var certificate = Certificate(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token); using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        var accepting = GameTls.AcceptAsync(server.GetStream(), certificate, timeout.Token);
        await Assert.ThrowsAsync<AuthenticationException>(() => GameTls.ConnectAsync(client.GetStream(), "localhost", new string('0', 64), timeout.Token));
        try { await using var accepted = await accepting; }
        catch (Exception e) when (e is AuthenticationException or IOException) { }
    }
}
