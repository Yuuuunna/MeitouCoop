using System.Buffers.Binary;

namespace Meitou.Networking;
public enum WireMessage : ushort
{
    Hello = 1,
    Compatible = 2,
    Incompatible = 3,
    Authenticate = 4,
    Authenticated = 5,
    AuthenticationFailed = 6,
    Admission = 7,
    EnterWorld = 8,
    AdmissionQuery = 9,
    Observe = 10,
    ServerInformationQuery = 11,
    ServerInformation = 12,
    AuthenticateDevice = 13,
    AuthenticationPending = 14,
    OperationNotDispatched = 15,
    Command = 16,
    CommandReceipt = 17,
    StateChunk = 32,
    StateAck = 33,
    StateResync = 34,
    StateWithdrawn = 35,
    CreationCatalogQuery = 40,
    CreationPreview = 41,
    CreationCommit = 42,
    CreationLookup = 43,
    CreationReply = 44,
    InvitationRequest = 48,
    InvitationReply = 49,
    TalkHintQuery = 56,
    TalkHintReply = 57,
    ViewBegin = 64,
    BulkManifest = 65,
    BulkAck = 66,
    ViewCommit = 67,
    ViewReady = 68,
    ViewActive = 69,
    MotionCheckpointAck = 70,
    BulkManifestAck = 71,
    BusinessBulkManifest = 72,
    BusinessBulkCancelled = 73
}

public sealed record WireFrame(WireMessage Kind, ReadOnlyMemory<byte> Payload)
{
    public ulong SessionEpoch { get; init; }
    public ulong ViewEpoch { get; init; }

    public const ushort Format = 1;
    public const int MaximumPayload = 1024 * 1024;
    const uint Magic = 0x314E544D; // MTN1
    const int HeaderSize = 12;
    /// <summary>
    /// One reader and one serialized writer per stream. Close the connection after any framing or partial-write error.
    /// </summary>
    public static async ValueTask<WireFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[HeaderSize];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0)
            return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var kind = (WireMessage)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != Format || !Enum.IsDefined(kind) || length > MaximumPayload)
            throw new InvalidDataException("Invalid network frame header.");
        byte[] payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new WireFrame(kind, payload);
    }

    public static async ValueTask WriteAsync(Stream stream, WireFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!Enum.IsDefined(frame.Kind) || frame.Payload.Length > MaximumPayload)
            throw new InvalidDataException("Invalid network frame.");
        byte[] payload = frame.Payload.ToArray();
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Format);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)frame.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}