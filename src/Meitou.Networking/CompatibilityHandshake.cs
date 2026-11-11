using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Networking;
public sealed record CompatibilityHello(ushort Protocol, string EngineBuild, string RulesDigest, string ContentDigest);
public enum CompatibilityFailure
{
    None,
    Protocol,
    EngineBuild,
    GameplayRules,
    Content
}

public enum CompatibilityPhase
{
    AwaitingHello,
    Compatible,
    Rejected
}

/// <summary>
/// Compatibility only. Matching content never authenticates an account or activates a player.
/// </summary>
public sealed class CompatibilityHandshake
{
    public const ushort Protocol = 21;
    public const int MaximumHelloBytes = 4096;
    readonly CompatibilityHello _expected;
    static readonly JsonSerializerOptions Json = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public CompatibilityHandshake(CompatibilityHello expected)
    {
        Validate(expected);
        if (expected.Protocol != Protocol)
            throw new ArgumentException("Unsupported local protocol.", nameof(expected));
        this._expected = expected;
    }

    public CompatibilityPhase Phase { get; private set; }
    public CompatibilityFailure Failure { get; private set; }

    public CompatibilityFailure Receive(CompatibilityHello actual)
    {
        if (Phase != CompatibilityPhase.AwaitingHello)
        {
            Phase = CompatibilityPhase.Rejected;
            throw new InvalidDataException("Duplicate compatibility hello.");
        }

        try
        {
            Validate(actual);
        }
        catch
        {
            Phase = CompatibilityPhase.Rejected;
            throw;
        }

        Failure = actual.Protocol != _expected.Protocol ? CompatibilityFailure.Protocol : actual.EngineBuild != _expected.EngineBuild ? CompatibilityFailure.EngineBuild : !string.Equals(actual.RulesDigest, _expected.RulesDigest, StringComparison.OrdinalIgnoreCase) ? CompatibilityFailure.GameplayRules : !string.Equals(actual.ContentDigest, _expected.ContentDigest, StringComparison.OrdinalIgnoreCase) ? CompatibilityFailure.Content : CompatibilityFailure.None;
        Phase = Failure == CompatibilityFailure.None ? CompatibilityPhase.Compatible : CompatibilityPhase.Rejected;
        return Failure;
    }

    public CompatibilityFailure Receive(WireFrame frame)
    {
        try
        {
            if (frame is null || frame.Kind != WireMessage.Hello)
                throw new InvalidDataException("Expected a compatibility hello.");
            return Receive(Decode(frame.Payload.Span));
        }
        catch
        {
            Phase = CompatibilityPhase.Rejected;
            throw;
        }
    }

    public static byte[] Encode(CompatibilityHello hello)
    {
        Validate(hello);
        return JsonSerializer.SerializeToUtf8Bytes(hello, Json);
    }

    public static CompatibilityHello Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumHelloBytes)
            throw new InvalidDataException("Compatibility hello is too large.");
        try
        {
            // Duplicate keys are rejected, rather than letting parser order select a different claim.
            var reader = new Utf8JsonReader(payload);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                if (reader.TokenType == JsonTokenType.PropertyName && !keys.Add(reader.GetString()!))
                    throw new InvalidDataException("Duplicate compatibility field.");
            var hello = JsonSerializer.Deserialize<CompatibilityHello>(payload, Json) ?? throw new InvalidDataException("Empty compatibility hello.");
            Validate(hello);
            return hello;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed compatibility hello.", e);
        }
    }

    static void Validate(CompatibilityHello hello)
    {
        if (hello is null || hello.Protocol == 0 || string.IsNullOrWhiteSpace(hello.EngineBuild) || hello.EngineBuild.Length > 128 || hello.EngineBuild.Any(char.IsControl) || !ContentManifest.ValidHash(hello.RulesDigest) || !ContentManifest.ValidHash(hello.ContentDigest))
            throw new InvalidDataException("Invalid compatibility hello.");
    }
}