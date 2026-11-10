using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Networking;

public sealed record ServerInformation(CompatibilityHello Hello, Dictionary<string, string> Rules, string[] GameplayAssets, bool DeviceRegistration);

/// <summary>Public connection metadata over encrypted TLS, without server certificate pinning.</summary>
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

    public static async Task<ServerInformation> QueryAsync(string host, int port, CancellationToken token)
    {
        try { return await QueryOnceAsync(host, port, token).ConfigureAwait(false); }
        catch (Exception e) when (!token.IsCancellationRequested &&
            e is IOException or SocketException)
        {
            // Discovery is read-only. Retry one interrupted transport, never malformed
            // metadata, authentication failures or state-changing creation requests.
            Console.WriteLine($"network   {DateTime.UtcNow:O} discovery transport interrupted ({e.GetType().Name}); retrying once");
            await Task.Delay(250, token).ConfigureAwait(false);
            return await QueryOnceAsync(host, port, token).ConfigureAwait(false);
        }
    }

    static async Task<ServerInformation> QueryOnceAsync(string host, int port, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
        await using var tls = await GameTls.ConnectAsync(tcp.GetStream(), host, deadline.Token).ConfigureAwait(false);
        await WireFrame.WriteAsync(tls, new(WireMessage.ServerInformationQuery, ReadOnlyMemory<byte>.Empty), deadline.Token).ConfigureAwait(false);
        var frame = await WireFrame.ReadAsync(tls, deadline.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("Server does not support address-only connection. Update the server.");
        return Decode(frame);
    }
}
