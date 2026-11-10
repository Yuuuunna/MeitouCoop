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
            X509KeyStorageFlags.Exportable | (OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet));
    }
    [Fact]
    public async Task PreReleasePublicDiscoveryTlsPolicyKeepsEncryption()
    {
        using var certificate = Certificate(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token); using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        var accepting = GameTls.AcceptAsync(server.GetStream(), certificate, timeout.Token);
        Assert.False(GameTls.VerifyServerCertificates); // Restore the strict-pin assertion before the official release.
        await using var connected = await GameTls.ConnectAsync(client.GetStream(), "localhost", new string('0', 64), timeout.Token);
        await using var accepted = await accepting;
        Assert.True(connected.IsEncrypted); Assert.True(accepted.IsEncrypted);
    }
}
