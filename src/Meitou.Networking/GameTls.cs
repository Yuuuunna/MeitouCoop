using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Meitou.Networking;

/// <summary>Production game transport. The host supplies handshake deadlines; certificate pinning is explicit, never an accept-all fallback.</summary>
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
    public static async Task<SslStream> ConnectAsync(Stream stream, string targetHost, string? certificateSha256, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetHost)) throw new ArgumentException("A server hostname is required.", nameof(targetHost));
        byte[]? pin = null;
        if (certificateSha256 is not null)
        {
            if (certificateSha256.Length != 64 || !certificateSha256.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid SHA-256 certificate pin.", nameof(certificateSha256));
            pin = Convert.FromHexString(certificateSha256);
        }
        bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (pin is null) return errors == SslPolicyErrors.None;
            if (certificate is null) return false;
            using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            var now = DateTime.UtcNow;
            return now >= copy.NotBefore.ToUniversalTime() && now <= copy.NotAfter.ToUniversalTime()
                && CryptographicOperations.FixedTimeEquals(pin, copy.GetCertHash(HashAlgorithmName.SHA256));
        }
        var tls = new SslStream(stream, leaveInnerStreamOpen: false, Validate);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowRenegotiation = false,
                CertificateRevocationCheckMode = pin is null ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
            }, cancellationToken).ConfigureAwait(false);
            return tls;
        }
        catch { await tls.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
