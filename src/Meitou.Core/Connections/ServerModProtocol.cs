using System.Buffers.Binary;
using System.Text.Json;

namespace Meitou.Connections;

/// <summary>
/// Additive public bootstrap messages using MTN1 framing; archive bytes follow their bounded descriptor on the same TLS stream.
/// </summary>
public static class ServerModProtocol
{
    /// <summary>
    /// Requests the active mod snapshot without authenticating or modifying server state.
    /// </summary>
    public const ushort ManifestQuery = 80;

    /// <summary>
    /// Returns a strict JSON manifest, or an explicit unavailable response.
    /// </summary>
    public const ushort ManifestReply = 81;

    /// <summary>
    /// Requests only the advertised archive by its SHA-256, never by a filesystem path.
    /// </summary>
    public const ushort ArchiveQuery = 82;

    /// <summary>
    /// Repeats the archive's manifest before streaming exactly ArchiveBytes bytes.
    /// </summary>
    public const ushort ArchiveReply = 83;

    /// <summary>
    /// File inventories can exceed the ordinary gameplay frame limit and use this separately bounded bootstrap response.
    /// </summary>
    public const int MaximumManifestBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Writes a bootstrap message without buffering the archive itself.
    /// </summary>
    public static async Task WriteAsync(Stream stream, ushort kind, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (payload.Length > MaximumManifestBytes) throw new InvalidDataException("Mod bootstrap message is too large.");

        byte[] header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x314E544D);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), kind);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects wrong framing, stale archive replies, unknown JSON fields and oversized metadata before allocating its payload.
    /// </summary>
    public static async Task<ServerModManifest> ReadAsync(Stream stream, ushort expectedKind, CancellationToken token)
    {
        byte[] header = new byte[12];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x314E544D || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)) != expectedKind || length < 0 || length > MaximumManifestBytes)
            throw new InvalidDataException("Unsupported server mod response. Update the server and launcher, then reconnect.");

        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(bytes, ModSyncJson.Default.ServerModManifest)
            ?? throw new InvalidDataException("Missing server mod manifest.");
        manifest.Validate();
        return manifest;
    }
}
