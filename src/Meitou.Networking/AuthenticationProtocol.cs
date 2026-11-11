using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Networking;
public sealed record AuthenticationRequest(Guid Account, string Token)
{
    public override string ToString() => $"AuthenticationRequest {{ Account = {Account}, Token = [redacted] }}";
}

/// <summary>
/// Transport credential message. The host accepts this only after TLS and compatibility, never as a player/world identity.
/// </summary>
public static class AuthenticationProtocol
{
    public const int MaximumBytes = 512;
    static readonly JsonSerializerOptions Json = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public static bool ValidCredential(AuthenticationRequest? request) => request is not null && request.Account != Guid.Empty && request.Token is { Length: 64 } && request.Token.All(char.IsAsciiHexDigit);
    public static WireFrame Encode(AuthenticationRequest request)
    {
        if (!ValidCredential(request))
            throw new InvalidDataException("Invalid account credential format.");
        return new(WireMessage.Authenticate, JsonSerializer.SerializeToUtf8Bytes(request, Json));
    }

    public static AuthenticationRequest Decode(WireFrame frame)
    {
        if (frame.Kind != WireMessage.Authenticate || frame.Payload.Length > MaximumBytes)
            throw new InvalidDataException("Expected bounded authentication request.");
        try
        {
            var reader = new Utf8JsonReader(frame.Payload.Span, new JsonReaderOptions { MaxDepth = 2 });
            var fields = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                if (reader.TokenType == JsonTokenType.PropertyName && !fields.Add(reader.GetString()!))
                    throw new InvalidDataException("Duplicate authentication field.");
            var request = JsonSerializer.Deserialize<AuthenticationRequest>(frame.Payload.Span, Json);
            return ValidCredential(request) ? request! : throw new InvalidDataException("Invalid account credential format.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Malformed authentication request.");
        } // do not include credential input in diagnostics
    }
}