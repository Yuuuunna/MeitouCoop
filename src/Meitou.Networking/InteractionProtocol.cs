using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Simulation;

namespace Meitou.Networking;
public sealed record TalkHintQuery(Guid Query, CharacterId Speaker, CharacterId Target, ExpectedRoleGrant Grant, ulong MembershipRevision);
public sealed record TalkHintReply(ReplicationIdentity Identity, Guid Query, CharacterId Speaker, CharacterId Target, ulong GrantRevision, ulong MembershipRevision, PlayerTalkHint Hint);
/// <summary>
/// Read-only affordances. An available hint never authorizes a later action.
/// </summary>
public static class InteractionProtocol
{
    static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 8,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public static WireFrame Query(TalkHintQuery query)
    {
        Validate(query);
        return Encode(WireMessage.TalkHintQuery, query);
    }

    public static TalkHintQuery ReadQuery(WireFrame frame)
    {
        if (frame.Kind != WireMessage.TalkHintQuery)
            throw new InvalidDataException("Expected talk hint query.");
        var query = Decode<TalkHintQuery>(frame);
        Validate(query);
        return query;
    }

    public static WireFrame Reply(TalkHintReply reply)
    {
        Validate(reply);
        return Encode(WireMessage.TalkHintReply, reply);
    }

    public static TalkHintReply ReadReply(WireFrame frame)
    {
        if (frame.Kind != WireMessage.TalkHintReply)
            throw new InvalidDataException("Expected talk hint reply.");
        var reply = Decode<TalkHintReply>(frame);
        Validate(reply);
        return reply;
    }

    static void Validate(TalkHintQuery query)
    {
        if (query.Query == Guid.Empty || query.Speaker.IsNone || query.Target.IsNone || query.Grant is not { Faction: >= 0, Revision: > 0 } grant || grant.Entity.IsNone || query.MembershipRevision == 0)
            throw new InvalidDataException("Invalid talk hint query.");
    }

    static void Validate(TalkHintReply reply)
    {
        reply.Identity.Validate();
        if (reply.Query == Guid.Empty || reply.Speaker.IsNone || reply.Target.IsNone || reply.GrantRevision == 0 || reply.MembershipRevision == 0 || reply.Hint is null || !Enum.IsDefined(reply.Hint.Status))
            throw new InvalidDataException("Invalid talk hint reply.");
    }

    static WireFrame Encode<T>(WireMessage kind, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 4096)
            throw new InvalidDataException("Talk hint is too large.");
        return new(kind, bytes);
    }

    static T Decode<T>(WireFrame frame)
    {
        if (frame.Payload.Length > 4096)
            throw new InvalidDataException("Talk hint is too large.");
        try
        {
            WorldViewCodec.ValidateJson(frame.Payload.Span);
            return JsonSerializer.Deserialize<T>(frame.Payload.Span, Json) ?? throw new InvalidDataException("Missing talk hint.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed talk hint.", e);
        }
    }
}