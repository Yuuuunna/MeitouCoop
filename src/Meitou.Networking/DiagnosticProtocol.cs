using System.Buffers.Binary;
using Meitou.Diagnostics;

namespace Meitou.Networking;

/// <summary>
/// Server evidence outcome; capture never enables collection and never returns private server data to a player.
/// </summary>
public enum DiagnosticCaptureStatus : byte
{
    /// <summary>
    /// Matching evidence is durable in the server's outbox.
    /// </summary>
    Saved,
    /// <summary>
    /// Server collection is disabled by its operator.
    /// </summary>
    Disabled,
    /// <summary>
    /// The connection or process has reached its capture budget.
    /// </summary>
    Busy,
    /// <summary>
    /// Storage failed; the client report remains useful independently.
    /// </summary>
    Failed
}

/// <summary>
/// An acknowledgement tied to the authenticated connection and client-generated incident ID.
/// </summary>
public sealed record DiagnosticReply(Guid Report, DiagnosticScope Scope, DiagnosticCaptureStatus Status);

/// <summary>
/// Small fixed-size diagnostic control messages; no file paths, credentials or arbitrary selectors cross the game connection.
/// </summary>
public static class DiagnosticProtocol
{
    /// <summary>
    /// Requests a server-side snapshot for the currently authenticated connection only.
    /// </summary>
    public static WireFrame Request(Guid report)
    {
        if (report == Guid.Empty) throw new InvalidDataException("Empty diagnostic report ID.");
        return new(WireMessage.DiagnosticCapture, report.ToByteArray());
    }

    /// <summary>
    /// Validates the complete fixed-size request before any diagnostic work is scheduled.
    /// </summary>
    public static Guid ReadRequest(WireFrame frame)
    {
        if (frame.Kind != WireMessage.DiagnosticCapture || frame.Payload.Length != 16)
            throw new InvalidDataException("Invalid diagnostic request.");
        var report = new Guid(frame.Payload.Span);
        if (report == Guid.Empty) throw new InvalidDataException("Empty diagnostic report ID.");
        return report;
    }

    /// <summary>
    /// Encodes an acknowledgement, not the server artifact itself.
    /// </summary>
    public static WireFrame Reply(DiagnosticReply reply)
    {
        byte[] bytes = new byte[49];
        reply.Report.TryWriteBytes(bytes);
        reply.Scope.World.TryWriteBytes(bytes.AsSpan(16));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), reply.Scope.Connection);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), reply.Scope.Generation);
        bytes[48] = (byte)reply.Status;
        var frame = new WireFrame(WireMessage.DiagnosticReply, bytes);
        _ = ReadReply(frame);
        return frame;
    }

    /// <summary>
    /// Decodes and validates an authenticated reply.
    /// </summary>
    public static DiagnosticReply ReadReply(WireFrame frame)
    {
        if (frame.Kind != WireMessage.DiagnosticReply || frame.Payload.Length != 49)
            throw new InvalidDataException("Invalid diagnostic reply.");
        var bytes = frame.Payload.Span;
        var reply = new DiagnosticReply(new(bytes[..16]), new(new(bytes.Slice(16, 16)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..])),
            (DiagnosticCaptureStatus)bytes[48]);
        if (reply.Report == Guid.Empty || reply.Scope.World == Guid.Empty || reply.Scope.Connection == 0 || reply.Scope.Generation == 0 ||
            !Enum.IsDefined(reply.Status)) throw new InvalidDataException("Invalid diagnostic correlation.");
        return reply;
    }
}
