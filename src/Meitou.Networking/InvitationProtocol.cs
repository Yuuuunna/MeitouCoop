using Meitou.Simulation.Factions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Simulation;

namespace Meitou.Networking;
public enum InvitationAction
{
    List,
    Offer,
    Accept,
    Decline,
    Revoke,
    Lookup,
    Resume,
    Roster
}

public enum InvitationOutcome
{
    Listed,
    Offered,
    Accepted,
    Closed,
    Unknown,
    PendingPersistence,
    Busy,
    Rejected,
    Roster
}

public sealed record InvitationRequest(Guid Query, InvitationAction Action, Guid Invitation = default, FactionInviteRequest? Offer = null, int Faction = -1, Guid After = default, int Limit = 100, bool IncludeClosed = false, ulong AfterEntity = 0);
public sealed record AssignableRole(CharacterControl Control, string Name, int Squad);
public sealed record InvitationReply(ReplicationIdentity Identity, Guid Query, InvitationOutcome Outcome, FactionInvitationView[] Invitations, Guid Pending, AssignableRole[] Roles, Guid Next = default, ulong NextEntity = 0);
public static class InvitationProtocol
{
    static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 16,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public static WireFrame Request(InvitationRequest request)
    {
        Validate(request);
        return Encode(WireMessage.InvitationRequest, request);
    }

    public static InvitationRequest ReadRequest(WireFrame frame)
    {
        if (frame.Kind != WireMessage.InvitationRequest)
            throw new InvalidDataException("Expected invitation request.");
        var result = Decode<InvitationRequest>(frame);
        Validate(result);
        return result;
    }

    public static WireFrame Reply(InvitationReply reply) => Encode(WireMessage.InvitationReply, reply);
    public static InvitationReply ReadReply(WireFrame frame)
    {
        if (frame.Kind != WireMessage.InvitationReply)
            throw new InvalidDataException("Expected invitation reply.");
        var reply = Decode<InvitationReply>(frame);
        reply.Identity.Validate();
        if (reply.Query == Guid.Empty || !Enum.IsDefined(reply.Outcome) || reply.Invitations.Length > 128 || reply.Roles.Length > 128 || reply.Invitations.Any(v => v is null || v.Invitation is null || (v.Invitation.Inviter != reply.Identity.Player && v.Invitation.Offer.Recipient != reply.Identity.Player)) || reply.Roles.Any(r => r is null))
            throw new InvalidDataException("Invalid private invitation reply.");
        return reply;
    }

    static void Validate(InvitationRequest r)
    {
        if (r.Query == Guid.Empty || !Enum.IsDefined(r.Action) || r.Limit is < 1 or > 128 || (r.Action == InvitationAction.Offer) != (r.Offer is not null) || r.Offer?.Roles is { Length: > 1024 } || r.Action is InvitationAction.Accept or InvitationAction.Decline or InvitationAction.Revoke or InvitationAction.Lookup && r.Invitation == Guid.Empty || r.Action == InvitationAction.Roster && r.Faction < 0)
            throw new InvalidDataException("Invalid invitation request.");
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
            throw new InvalidDataException("Invitation message is too large.");
        try
        {
            WorldViewCodec.ValidateJson(frame.Payload.Span);
            return JsonSerializer.Deserialize<T>(frame.Payload.Span, Json) ?? throw new InvalidDataException("Empty invitation message.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed invitation message.", e);
        }
    }
}