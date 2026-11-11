using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meitou.Networking;

namespace Meitou.Server;
static class ServerCertificate
{
    public static X509Certificate2 Open(string directory, string hostname)
    {
        string path = Path.Combine(directory, "server.pfx");
        if (!File.Exists(path))
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=Meitou experimental server", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            var san = new SubjectAlternativeNameBuilder();
            if (System.Net.IPAddress.TryParse(hostname, out var ip))
                san.AddIpAddress(ip);
            else
                san.AddDnsName(hostname);
            request.CertificateExtensions.Add(san.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(certificate.Export(X509ContentType.Pfx));
            file.Flush(true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var result = GameTls.LoadServerCertificate(path);
        if (!result.HasPrivateKey || result.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            result.Dispose();
            throw new InvalidDataException("Server certificate is expired or has no private key.");
        }

        return result;
    }
}