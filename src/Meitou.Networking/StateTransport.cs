using System.Buffers.Binary;
using System.Security.Cryptography;
using Meitou.Simulation;

namespace Meitou.Networking;

public sealed record StateResyncRequest(ReplicationIdentity Identity, ulong AfterPublication);

public static class StateTransport
{
    public const int ChunkBytes = 256 * 1024;
    internal const int HeaderBytes = 84;
    public static IReadOnlyList<WireFrame> Split(ReplicationIdentity identity, EncodedPublication publication)
    {
        identity.Validate();
        var bytes = publication.Payload;
        if (publication.Publication == 0 || bytes.Length == 0 || bytes.Length > StatePatchProtocol.MaximumBytes) throw new InvalidDataException("Invalid publication size.");
        byte[] digest = SHA256.HashData(bytes);
        int count = (bytes.Length + ChunkBytes - 1) / ChunkBytes;
        var frames = new WireFrame[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i * ChunkBytes, size = Math.Min(ChunkBytes, bytes.Length - offset);
            var payload = new byte[HeaderBytes + size]; var header = payload.AsSpan();
            WriteIdentity(header, identity); BinaryPrimitives.WriteUInt64LittleEndian(header[32..], publication.Publication);
            BinaryPrimitives.WriteInt32LittleEndian(header[40..], i); BinaryPrimitives.WriteInt32LittleEndian(header[44..], count);
            BinaryPrimitives.WriteInt32LittleEndian(header[48..], bytes.Length); digest.CopyTo(header[52..84]);
            bytes.AsSpan(offset, size).CopyTo(header[HeaderBytes..]);
            frames[i] = new(WireMessage.StateChunk, payload);
        }
        return frames;
    }
    public static WireFrame EncodeAck(StateAcknowledgement ack)
    {
        ack.Identity.Validate();
        if (ack.Publication == 0 || ack.Hash.Length != 64 || ack.Hash.Any(c => !char.IsAsciiHexDigitUpper(c))) throw new InvalidDataException("Invalid state acknowledgement.");
        var payload = new byte[72]; WriteIdentity(payload, ack.Identity);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(32), ack.Publication); Convert.FromHexString(ack.Hash).CopyTo(payload, 40);
        return new(WireMessage.StateAck, payload);
    }
    public static StateAcknowledgement DecodeAck(WireFrame frame)
    {
        if (frame.Kind != WireMessage.StateAck || frame.Payload.Length != 72) throw new InvalidDataException("Expected a bounded state acknowledgement.");
        var bytes = frame.Payload.Span; var identity = ReadIdentity(bytes); ulong publication = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]);
        if (publication == 0) throw new InvalidDataException("Invalid acknowledged publication.");
        return new(identity, publication, Convert.ToHexString(bytes[40..]));
    }
    public static WireFrame EncodeResync(StateResyncRequest request)
    {
        var payload = new byte[40]; request.Identity.Validate(); WriteIdentity(payload, request.Identity);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(32), request.AfterPublication); return new(WireMessage.StateResync, payload);
    }
    public static StateResyncRequest DecodeResync(WireFrame frame)
    {
        if (frame.Kind != WireMessage.StateResync || frame.Payload.Length != 40) throw new InvalidDataException("Expected a bounded resync request.");
        return new(ReadIdentity(frame.Payload.Span), BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.Span[32..]));
    }
    public static WireFrame EncodeWithdrawal(ReplicationIdentity identity, ulong publication)
    {
        if (publication == 0) throw new InvalidDataException("Invalid withdrawal.");
        return EncodeResync(new(identity, publication)) with { Kind = WireMessage.StateWithdrawn };
    }
    public static StateResyncRequest DecodeWithdrawal(WireFrame frame)
    {
        if (frame.Kind != WireMessage.StateWithdrawn) throw new InvalidDataException("Expected state withdrawal.");
        var result = DecodeResync(frame with { Kind = WireMessage.StateResync });
        if (result.AfterPublication == 0) throw new InvalidDataException("Invalid withdrawal.");
        return result;
    }
    internal static void WriteIdentity(Span<byte> bytes, ReplicationIdentity identity)
    {
        identity.World.TryWriteBytes(bytes); BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], identity.Player.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], identity.Generation);
    }
    internal static ReplicationIdentity ReadIdentity(ReadOnlySpan<byte> bytes)
    {
        var identity = new ReplicationIdentity(new Guid(bytes[..16]), new(BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..])), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
        identity.Validate(); return identity;
    }
}

/// <summary>At most one bounded partial publication per connection. The host supplies IO deadlines and resets on disconnect.</summary>
public sealed class StateChunkAssembler(ReplicationIdentity identity)
{
    byte[]?[]? chunks;
    byte[]? digest;
    int total, received;
    ulong publication;
    public int BufferedBytes { get; private set; }
    public byte[]? Receive(WireFrame frame)
    {
        try
        {
            if (frame.Kind != WireMessage.StateChunk || frame.Payload.Length <= StateTransport.HeaderBytes || frame.Payload.Length > StateTransport.HeaderBytes + StateTransport.ChunkBytes)
                throw new InvalidDataException("Invalid state chunk size.");
            var bytes = frame.Payload.Span;
            if (StateTransport.ReadIdentity(bytes) != identity) throw new InvalidDataException("State chunk is addressed to another connection.");
            ulong nextPublication = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]);
            int index = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]), count = BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]), size = BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]);
            if (nextPublication == 0 || size <= 0 || size > StatePatchProtocol.MaximumBytes || count != (size + StateTransport.ChunkBytes - 1) / StateTransport.ChunkBytes ||
                index < 0 || index >= count || bytes.Length - StateTransport.HeaderBytes != Math.Min(StateTransport.ChunkBytes, size - index * StateTransport.ChunkBytes))
                throw new InvalidDataException("Invalid state chunk envelope.");
            if (nextPublication < publication || nextPublication == publication && chunks is null) return null;
            if (nextPublication > publication)
            {
                Clear(); publication = nextPublication; total = size; chunks = new byte[count][]; digest = bytes[52..84].ToArray();
            }
            if (chunks!.Length != count || total != size || !bytes[52..84].SequenceEqual(digest)) throw new InvalidDataException("Inconsistent publication chunks.");
            var body = bytes[StateTransport.HeaderBytes..];
            if (chunks[index] is { } previous)
            {
                if (!body.SequenceEqual(previous)) throw new InvalidDataException("Conflicting duplicate chunk.");
                return null;
            }
            chunks[index] = body.ToArray(); BufferedBytes += body.Length; received++;
            if (received != chunks.Length) return null;
            byte[] result = new byte[total];
            for (int i = 0; i < chunks.Length; i++) chunks[i]!.CopyTo(result, i * StateTransport.ChunkBytes);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(result), digest!)) throw new InvalidDataException("Publication checksum failed.");
            // The packet is independently decoded and identity-checked before replica mutation.
            var patch = StatePatchProtocol.Decode(result);
            if (patch.Identity != identity || patch.Publication != publication) throw new InvalidDataException("Chunk identity disagrees with publication.");
            Clear(); return result;
        }
        catch { Clear(); throw; }
    }
    public void Clear() { chunks = null; digest = null; total = received = BufferedBytes = 0; }
    public void Withdraw(ulong sequence) { publication = Math.Max(publication, sequence); Clear(); }
}

public sealed class ClientStateReceiver
{
    readonly ReplicationIdentity identity;
    readonly StateChunkAssembler chunks;
    public ClientWorldReplica Replica { get; }
    public ClientStateReceiver(ReplicationIdentity identity, WorldViewCodec codec)
        => (this.identity, chunks, Replica) = (identity, new(identity), new(identity, codec));
    public WireFrame? Acknowledgement => Replica.Acknowledgement is { } ack ? StateTransport.EncodeAck(ack) : null;
    public WireFrame ResyncRequest() => StateTransport.EncodeResync(new(identity, Replica.HighestPublication));
    public ReplicaResult? Receive(WireFrame frame)
    {
        try
        {
            if (frame.Kind == WireMessage.StateWithdrawn)
            {
                var withdrawal = StateTransport.DecodeWithdrawal(frame);
                if (withdrawal.Identity != identity) throw new InvalidDataException("Withdrawal addressed to another connection.");
                chunks.Withdraw(withdrawal.AfterPublication); return Replica.Withdraw(withdrawal.AfterPublication);
            }
            return chunks.Receive(frame) is { } complete ? Replica.Receive(complete) : null;
        }
        catch { chunks.Clear(); Replica.Reset(); throw; }
    }
}

/// <summary>World-owner bridge from current authorization to frozen network publications; account authentication remains the host's responsibility.</summary>
public sealed class AuthoritativeStatePublisher
{
    readonly AuthorityGateway authority;
    readonly AuthorityConnection connection;
    readonly ReplicationIdentity identity;
    readonly ServerStateReplicator replicator;
    public AuthoritativeStatePublisher(AuthorityGateway authority, AuthorityConnection connection, WorldViewCodec codec)
    {
        this.authority = authority; this.connection = connection;
        identity = new(connection.World, connection.Player, connection.Generation); replicator = new(identity, codec);
    }
    public IReadOnlyList<WireFrame>? Capture(PlayerViewRequest request, PlayerObservationRules rules, double timeScale = 1)
    {
        if (timeScale is not (0 or 1 or 2 or 5)) throw new ArgumentOutOfRangeException(nameof(timeScale));
        var view = authority.CaptureView(connection, request, rules);
        if (view is null)
        {
            if (authority.WithdrawView(connection) is not { } publication) return null; // replaced/closed handles must close their transport
            replicator.Withdraw(publication); return [StateTransport.EncodeWithdrawal(identity, publication)];
        }
        return StateTransport.Split(identity, replicator.Publish(view with { TimeScale = timeScale }, id => authority.ConfirmedDestroyed(connection, id)));
    }
    public bool Acknowledge(WireFrame frame)
    {
        if (authority.GetPhase(connection) is null) return false;
        var ack = StateTransport.DecodeAck(frame);
        if (!replicator.Acknowledge(ack)) return false;
        if (authority.GetPhase(connection) == AdmissionPhase.Synchronizing && !authority.ActivateAfterView(connection, ack.Publication))
            replicator.RequestBaseline();
        return true;
    }
    public bool RequestBaseline(WireFrame frame)
    {
        if (authority.GetPhase(connection) is null || StateTransport.DecodeResync(frame).Identity != identity) return false;
        replicator.RequestBaseline(); return true;
    }
    public void RequestBaseline() => replicator.RequestBaseline();
}
