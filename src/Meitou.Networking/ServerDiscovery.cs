using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Networking;

public sealed record ServerInformation(CompatibilityHello Hello, Dictionary<string, string> Rules, string[] GameplayAssets, bool DeviceRegistration);

/// <summary>Public connection metadata. First contact pins the certificate; subsequent contact must match it.</summary>
public static class ServerDiscovery
{
    const int MaximumBytes = 128 * 1024;
    static readonly JsonSerializerOptions json = new()
    {
        MaxDepth = 8, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static WireFrame Encode(ServerInformation information)
    {
        Validate(information);
        using var buffer = new BoundedBuffer(MaximumBytes);
        JsonSerializer.Serialize(buffer, information, json);
        return new(WireMessage.ServerInformation, buffer.ToArray());
    }

    public static ServerInformation Decode(WireFrame frame)
    {
        if (frame.Kind != WireMessage.ServerInformation || frame.Payload.Length > MaximumBytes) throw new InvalidDataException("Expected server information. Update both client and server.");
        try
        {
            WorldViewCodec.ValidateJson(frame.Payload.Span);
            var result = JsonSerializer.Deserialize<ServerInformation>(frame.Payload.Span, json) ?? throw new InvalidDataException("Missing server information.");
            Validate(result);
            return result;
        }
        catch (JsonException e) { throw new InvalidDataException("Invalid server information.", e); }
    }

    static void Validate(ServerInformation value)
    {
        if (value.Hello is null || value.Rules is null || value.GameplayAssets is null || value.Rules.Count > 128 || value.GameplayAssets.Length > 1024 ||
            value.Rules.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || p.Value is null || p.Value.Length > 2048) ||
            value.GameplayAssets.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 1024 || Path.IsPathRooted(p) || p.Contains(':') || p.Replace('\\', '/').Split('/').Any(s => s is "" or "." or "..")))
            throw new InvalidDataException("Invalid server metadata bounds or asset paths.");
        _ = CompatibilityHandshake.Encode(value.Hello);
        if (!string.Equals(ContentManifest.RulesDigest(value.Rules), value.Hello.RulesDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Server rules do not match its compatibility digest.");
    }

    public static async Task<(ServerInformation Information, string Certificate)> QueryAsync(string host, int port, string? knownCertificate, CancellationToken token)
    {
        try { return await QueryOnceAsync(host, port, knownCertificate, token).ConfigureAwait(false); }
        catch (Exception e) when (!token.IsCancellationRequested &&
            e is IOException or SocketException)
        {
            // Discovery is read-only. Retry one interrupted transport, never malformed
            // metadata, authentication failures or state-changing creation requests.
            Console.WriteLine($"network   {DateTime.UtcNow:O} discovery transport interrupted ({e.GetType().Name}); retrying once");
            await Task.Delay(250, token).ConfigureAwait(false);
            return await QueryOnceAsync(host, port, knownCertificate, token).ConfigureAwait(false);
        }
    }

    static async Task<(ServerInformation Information, string Certificate)> QueryOnceAsync(string host, int port, string? knownCertificate, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
        SslStream tls;
        string? observed = knownCertificate;
        if (knownCertificate is not null && GameTls.VerifyServerCertificates) tls = await GameTls.ConnectAsync(tcp.GetStream(), host, knownCertificate, deadline.Token).ConfigureAwait(false);
        else
        {
            // Public discovery records the presented certificate; the shared pre-release policy also applies here.
            tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                var now = DateTime.UtcNow;
                if (GameTls.VerifyServerCertificates && (now < copy.NotBefore.ToUniversalTime() || now > copy.NotAfter.ToUniversalTime())) return false;
                observed = copy.GetCertHashString(HashAlgorithmName.SHA256);
                return true;
            });
            try
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                { TargetHost = host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowRenegotiation = false }, deadline.Token).ConfigureAwait(false);
            }
            catch { await tls.DisposeAsync().ConfigureAwait(false); throw; }
        }
        await using (tls)
        {
            await WireFrame.WriteAsync(tls, new(WireMessage.ServerInformationQuery, ReadOnlyMemory<byte>.Empty), deadline.Token).ConfigureAwait(false);
            var frame = await WireFrame.ReadAsync(tls, deadline.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("Server does not support address-only connection. Update the server.");
            return (Decode(frame), observed ?? throw new AuthenticationException("No server certificate."));
        }
    }
}
