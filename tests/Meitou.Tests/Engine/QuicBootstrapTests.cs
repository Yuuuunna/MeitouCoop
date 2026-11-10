using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Server;

namespace Meitou.Tests.Engine;

public class QuicBootstrapTests
{
    static ServerInformation Information()
    {
        Dictionary<string, string> rules = [];
        return new(new(21, ServerCompatibility.EngineBuild, ContentManifest.RulesDigest(rules), new('A', 64)), rules, [], true);
    }

    [Fact, Trait("Category", "Slow")]
    public async Task PublicBootstrapExportsLeafPemAndSelectsItsActualTlsIdentity()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MEITOU_QUIC_NATIVE_DIRECTORY") is null,
            "Set the trusted built native artifact directory for bootstrap integration.");
        using var certificate = GameTlsTests.Certificate();
        await using var server = new TcpDiscoveryServer(new(IPAddress.Loopback, 0), certificate, Information());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = await QuicDeployment.ClientOptionsAsync("127.0.0.1", server.Endpoint.Port, timeout.Token);
        Assert.Equal("localhost", options.ServerName);
        Assert.Equal(certificate.ExportCertificatePem(), await File.ReadAllTextAsync(options.CaFile, timeout.Token));
        File.Delete(options.CaFile);
    }

    [Fact]
    public async Task TcpBootstrapNeverAcceptsGameplayOrAccountAuthentication()
    {
        using var certificate = GameTlsTests.Certificate();
        await using var server = new TcpDiscoveryServer(new(IPAddress.Loopback, 0), certificate, Information());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Endpoint, timeout.Token);
        await using var tls = await GameTls.ConnectAsync(tcp.GetStream(), "localhost", timeout.Token);
        await WireFrame.WriteAsync(tls, new(WireMessage.Authenticate, ReadOnlyMemory<byte>.Empty), timeout.Token);
        try { Assert.Null(await WireFrame.ReadAsync(tls, timeout.Token)); }
        catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        { /* Both TCP reset and clean EOF reject the unsupported request without an authenticated reply. */ }
    }
}
