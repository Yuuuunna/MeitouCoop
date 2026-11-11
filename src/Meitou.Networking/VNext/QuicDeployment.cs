using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Meitou.Networking.VNext;

/// <summary>Trusted local deployment plus public bootstrap. TCP carries public metadata only;
/// credentials, commands and game state use the independently authenticated QUIC connection.</summary>
public static class QuicDeployment
{
    public static (string Directory, string ShimSha256) ResolveNative()
    {
        string directory = Environment.GetEnvironmentVariable("MEITOU_QUIC_NATIVE_DIRECTORY") ?? Path.Combine(AppContext.BaseDirectory, "native", "quic");
        directory = Path.GetFullPath(directory);
        string path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024)
            throw new InvalidDataException("The paired QUIC runtime is missing. Install the complete client/server package.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(path));
        var value = manifest.RootElement;
        string hash = value.GetProperty("shimSha256").GetString() ?? "";
        if (value.GetProperty("api").GetInt32() != 1 || value.GetProperty("architecture").GetString() != "win-x64" || value.GetProperty("msquicVersion").GetString() != "2.6.3" || value.GetProperty("tls").GetString() != "OpenSSL" || !string.Equals(value.GetProperty("msquicSha256").GetString(), Transport.NativeQuicEndpoint.MsQuicSha256, StringComparison.OrdinalIgnoreCase) || !ContentManifest.ValidHash(hash))
            throw new InvalidDataException("Unsupported native QUIC deployment manifest.");
        return (directory, hash);
    }

    public static async Task<QuicGameTransportOptions> ClientOptionsAsync(string host, int port, CancellationToken cancellationToken)
    {
        var deployment = ResolveNative();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient
        {
            NoDelay = true
        };
        await tcp.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
        byte[]? certificateBytes = null;
        await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate is null)
                return false;
            certificateBytes = certificate.GetRawCertData();
            return true;
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowRenegotiation = false, CertificateRevocationCheckMode = X509RevocationMode.NoCheck, }, deadline.Token).ConfigureAwait(false);
        await WireFrame.WriteAsync(tls, new(WireMessage.ServerInformationQuery, ReadOnlyMemory<byte>.Empty), deadline.Token).ConfigureAwait(false);
        var frame = await WireFrame.ReadAsync(tls, deadline.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("Discovery closed before metadata.");
        var info = ServerDiscovery.Decode(frame);
        if (info.Hello.Protocol != 21)
            throw new InvalidDataException("This client requires the paired protocol 21 server.");
        using var remote = X509CertificateLoader.LoadCertificate(certificateBytes ?? throw new AuthenticationException("Server certificate missing."));
        string? name = null;
        foreach (var extension in remote.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17")
                continue;
            var san = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
            name = san.EnumerateDnsNames().FirstOrDefault() ?? san.EnumerateIPAddresses().FirstOrDefault()?.ToString();
        }

        name ??= remote.GetNameInfo(X509NameType.DnsName, false);
        if (string.IsNullOrWhiteSpace(name))
            throw new AuthenticationException("Pinned certificate has no TLS server identity.");
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "server-certificates");
        Directory.CreateDirectory(folder);
        string ca = Path.Combine(folder, remote.GetCertHashString(HashAlgorithmName.SHA256) + ".pem");
        // Public material only; replace atomically so concurrent players never read a partial PEM.
        string temporary = ca + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, remote.ExportCertificatePem(), deadline.Token).ConfigureAwait(false);
            File.Move(temporary, ca, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return new(deployment.Directory, deployment.ShimSha256, ca, name);
    }
}