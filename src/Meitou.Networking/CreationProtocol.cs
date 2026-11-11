using Meitou.Simulation.Factions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Simulation;

namespace Meitou.Networking;
public sealed record CreationCatalogQuery(Guid Query, ulong Qualification = 0, ulong QualificationRevision = 0);
public sealed record CreationDraftQuery(Guid Query, ulong DraftRevision, PlayerCreationRequest Request);
/// <summary>
/// An empty Request queries/resumes only the caller's pending persistence operation.
/// </summary>
public sealed record CreationLookupQuery(Guid Query, Guid Request, bool Resume = false);
public sealed record CreationStartOption(string Id, string Name, string Description, int StartMoney, string[] ForcedRaces);
public sealed record CreationCatalog(CreationQualificationView Qualification, CreationStartOption[] Starts);
public enum CreationOutcome
{
    Catalog,
    Preview,
    Unknown,
    Busy,
    PendingPersistence,
    Committed,
    Rejected
}

public enum CreationFailure
{
    None,
    Unavailable,
    InvalidRequest,
    Qualification,
    Policy,
    Preparation,
    Persistence,
    Conflict
}

public sealed record CreationReply(ReplicationIdentity Identity, Guid Query, Guid Request, ulong DraftRevision, CreationOutcome Outcome, CreationFailure Failure = CreationFailure.None, CreationCatalog? Catalog = null, PlayerCreationPreview? Preview = null, CreationReceipt? Receipt = null);
public static class CreationProtocol
{
    static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 16,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public static bool IsRequest(WireMessage kind) => kind is WireMessage.CreationCatalogQuery or WireMessage.CreationPreview or WireMessage.CreationCommit or WireMessage.CreationLookup;
    public static WireFrame Catalog(CreationCatalogQuery query)
    {
        Validate(query);
        return Encode(WireMessage.CreationCatalogQuery, query);
    }

    public static WireFrame Preview(CreationDraftQuery query)
    {
        Validate(query);
        return Encode(WireMessage.CreationPreview, query);
    }

    public static WireFrame Commit(CreationDraftQuery query)
    {
        Validate(query);
        return Encode(WireMessage.CreationCommit, query);
    }

    public static WireFrame Lookup(CreationLookupQuery query)
    {
        Validate(query);
        return Encode(WireMessage.CreationLookup, query);
    }

    public static CreationCatalogQuery ReadCatalog(WireFrame frame)
    {
        if (frame.Kind != WireMessage.CreationCatalogQuery)
            throw new InvalidDataException("Expected creation catalogue request.");
        var value = Decode<CreationCatalogQuery>(frame);
        Validate(value);
        return value;
    }

    public static CreationDraftQuery ReadDraft(WireFrame frame)
    {
        if (frame.Kind is not (WireMessage.CreationPreview or WireMessage.CreationCommit))
            throw new InvalidDataException("Expected creation draft.");
        var value = Decode<CreationDraftQuery>(frame);
        Validate(value);
        return value;
    }

    public static CreationLookupQuery ReadLookup(WireFrame frame)
    {
        if (frame.Kind != WireMessage.CreationLookup)
            throw new InvalidDataException("Expected creation lookup.");
        var value = Decode<CreationLookupQuery>(frame);
        Validate(value);
        return value;
    }

    public static WireFrame Reply(CreationReply reply, WorldViewCodec codec)
    {
        Validate(reply);
        return new(WireMessage.CreationReply, codec.EncodePresentation(reply));
    }

    public static CreationReply ReadReply(WireFrame frame, WorldViewCodec codec)
    {
        if (frame.Kind != WireMessage.CreationReply)
            throw new InvalidDataException("Expected creation reply.");
        var reply = codec.DecodePresentation<CreationReply>(frame.Payload.Span);
        Validate(reply);
        return reply;
    }

    public static CreationReply Unavailable(ReplicationIdentity identity, WireFrame frame)
    {
        if (frame.Kind == WireMessage.CreationCatalogQuery)
            return new(identity, ReadCatalog(frame).Query, Guid.Empty, 0, CreationOutcome.Rejected, CreationFailure.Unavailable);
        if (frame.Kind == WireMessage.CreationLookup)
        {
            var q = ReadLookup(frame);
            return new(identity, q.Query, q.Request, 0, CreationOutcome.Rejected, CreationFailure.Unavailable);
        }

        var draft = ReadDraft(frame);
        return new(identity, draft.Query, draft.Request.Id, draft.DraftRevision, CreationOutcome.Rejected, CreationFailure.Unavailable);
    }

    static void Validate(CreationCatalogQuery q)
    {
        if (q.Query == Guid.Empty || (q.Qualification == 0) != (q.QualificationRevision == 0))
            throw new InvalidDataException("Invalid creation catalogue scope.");
    }

    static void Validate(CreationDraftQuery q)
    {
        if (q.Query == Guid.Empty || q.DraftRevision == 0 || q.Request is not { Id: var id } r || id == Guid.Empty || r.Qualification == 0 || r.QualificationRevision == 0 || string.IsNullOrWhiteSpace(r.Start) || r.Start.Length > 512 || r.FactionName?.Length > 128 || r.Choices?.Count > 1024)
            throw new InvalidDataException("Invalid creation draft envelope.");
    }

    static void Validate(CreationLookupQuery q)
    {
        if (q.Query == Guid.Empty)
            throw new InvalidDataException("Invalid creation query identity.");
    }

    static void Validate(CreationReply reply)
    {
        reply.Identity.Validate();
        if (reply.Query == Guid.Empty || !Enum.IsDefined(reply.Outcome) || !Enum.IsDefined(reply.Failure))
            throw new InvalidDataException("Invalid creation reply.");
        if ((reply.Catalog is not null) != (reply.Outcome == CreationOutcome.Catalog) || (reply.Preview is not null) != (reply.Outcome == CreationOutcome.Preview) || (reply.Receipt is not null) != (reply.Outcome == CreationOutcome.Committed))
            throw new InvalidDataException("Unexpected creation reply payload.");
        if (reply.Receipt is { } receipt && (reply.Request == Guid.Empty || receipt.Player != reply.Identity.Player || receipt.RequestId != reply.Request || receipt.Qualification == 0 || receipt.QualificationRevision == 0 || receipt.Faction < 0 || receipt.Squad < 0 || receipt.Paid < 0 || receipt.Fingerprint is not { Length: 64 } || receipt.Fingerprint.Any(c => !char.IsAsciiHexDigitUpper(c)) || receipt.Roles is not { Length: > 0 and <= 1024 } || receipt.Roles.Any(e => e.IsNone) || receipt.Roles.Distinct().Count() != receipt.Roles.Length))
            throw new InvalidDataException("Creation receipt belongs to another request/player.");
        if (reply.Preview is { } preview && (reply.Request == Guid.Empty || preview.Request != reply.Request || reply.DraftRevision == 0 || preview.Cost < 0 || preview.Members is not { Length: > 0 and <= 1024 } || preview.Members.Any(m => m is null) || !preview.Members.Select(m => m.Member).SequenceEqual(Enumerable.Range(0, preview.Members.Length))))
            throw new InvalidDataException("Invalid creation preview.");
        if (reply.Catalog is { } catalog && (catalog.Qualification.Grant.Player != reply.Identity.Player || catalog.Starts is null || catalog.Starts.Any(s => s is null) || catalog.Starts.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Starts.Length))
            throw new InvalidDataException("Invalid creation catalogue.");
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
            throw new InvalidDataException("Creation message is too large.");
        try
        {
            WorldViewCodec.ValidateJson(frame.Payload.Span);
            return JsonSerializer.Deserialize<T>(frame.Payload.Span, Json) ?? throw new InvalidDataException("Missing creation message.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed creation request.", e);
        }
    }
}