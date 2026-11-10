using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Meitou.Networking;

/// <summary>TLS transport for encrypted public discovery. Self-signed server certificates are accepted without pinning.</summary>
public static class GameTls
{
    public static X509Certificate2 LoadServerCertificate(string path, string? password = null)
        => X509CertificateLoader.LoadPkcs12FromFile(path, password,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);

    public static async Task<SslStream> AcceptAsync(Stream stream, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        if (!certificate.HasPrivateKey) throw new ArgumentException("Server certificate needs a private key.", nameof(certificate));
        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate, ClientCertificateRequired = false, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowRenegotiation = false,
            }, cancellationToken).ConfigureAwait(false);
            return tls;
        }
        catch { await tls.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public static async Task<SslStream> ConnectAsync(Stream stream, string targetHost, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetHost)) throw new ArgumentException("A server hostname is required.", nameof(targetHost));
        var tls = new SslStream(stream, leaveInnerStreamOpen: false, static (_, certificate, _, _) => certificate is not null);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowRenegotiation = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, cancellationToken).ConfigureAwait(false);
            return tls;
        }
        catch { await tls.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
