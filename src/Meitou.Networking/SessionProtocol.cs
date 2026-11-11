using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Simulation;

namespace Meitou.Networking;
public sealed record AdmissionMessage(ReplicationIdentity Identity, PlayerAdmissionState State);
public sealed record EnterWorldRequest(EntityId FocusRole);
public static class SessionProtocol
{
    static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 16,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public static WireFrame Admission(AuthorityConnection connection, PlayerAdmissionState state, bool authenticated = false) => Encode(authenticated ? WireMessage.Authenticated : WireMessage.Admission, new AdmissionMessage(new(connection.World, connection.Player, connection.Generation), state));
    public static AdmissionMessage ReadAdmission(WireFrame frame)
    {
        if (frame.Kind is not (WireMessage.Authenticated or WireMessage.Admission))
            throw new InvalidDataException("Expected admission status.");
        var value = Decode<AdmissionMessage>(frame);
        value.Identity.Validate();
        if (value.State.Sequence == 0 || !Enum.IsDefined(value.State.Phase) || value.State.Roles.Any(r => r is null || r.Entity.IsNone || r.Revision == 0 || r.Faction < 0 || !Enum.IsDefined(r.Availability) || r.Available && (r.Dead || r.Character.IsNone)) || value.State.Roles.Select(r => r.Entity).Distinct().Count() != value.State.Roles.Length || value.State.Qualifications.Any(q => q is null || q.Grant.Player != value.Identity.Player || !Enum.IsDefined(q.Eligibility) || !Enum.IsDefined(q.Grant.Kind) || !Enum.IsDefined(q.Grant.Status)) || value.State.Busy && (value.State.Entry is not null || value.State.Roles.Length != 0 || value.State.Qualifications.Length != 0) || !value.State.Busy && (value.State.Entry is not { } entry || !Enum.IsDefined(entry.Route) || !Enum.IsDefined(entry.History) || entry.AvailableRoles != value.State.Roles.Count(r => r.Available)))
            throw new InvalidDataException("Invalid admission status.");
        return value;
    }

    public static WireFrame Enter(EntityId role) => !role.IsNone ? Encode(WireMessage.EnterWorld, new EnterWorldRequest(role)) : throw new ArgumentException("An initial role is required.", nameof(role));
    public static EnterWorldRequest ReadEnter(WireFrame frame)
    {
        if (frame.Kind != WireMessage.EnterWorld || frame.Payload.Length > 256)
            throw new InvalidDataException("Expected entry request.");
        var request = Decode<EnterWorldRequest>(frame);
        return !request.FocusRole.IsNone ? request : throw new InvalidDataException("Invalid initial role.");
    }

    public static WireFrame Observe(PlayerViewRequest request) => Encode(WireMessage.Observe, request);
    public static PlayerViewRequest ReadObservation(WireFrame frame)
    {
        if (frame.Kind != WireMessage.Observe || frame.Payload.Length > 16384)
            throw new InvalidDataException("Expected bounded observation filter.");
        var request = Decode<PlayerViewRequest>(frame);
        if (request.Zones is { Count: > 256 } || request.Zones?.Any(z => !z.IsInsideGrid) == true)
            throw new InvalidDataException("Invalid observation filter.");
        return request with
        {
            Zones = request.Zones?.ToArray()
        };
    }

    static WireFrame Encode<T>(WireMessage kind, T value)
    {
        using var buffer = new BoundedBuffer(WireFrame.MaximumPayload);
        JsonSerializer.Serialize(buffer, value, Json);
        return new(kind, buffer.ToArray());
    }

    static T Decode<T>(WireFrame frame)
    {
        if (frame.Payload.Length > WireFrame.MaximumPayload)
            throw new InvalidDataException("Session message is too large.");
        try
        {
            WorldViewCodec.ValidateJson(frame.Payload.Span);
            return JsonSerializer.Deserialize<T>(frame.Payload.Span, Json) ?? throw new InvalidDataException("Missing session message.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed session message.", e);
        }
    }
}