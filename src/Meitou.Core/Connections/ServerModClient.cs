using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Meitou.Connections;

/// <summary>
/// Read-only mod bootstrap over the server's existing encrypted TCP port, using the game's self-signed certificate policy.
/// </summary>
public static class ServerModClient
{
    /// <summary>
    /// Queries the exact enabled list before the launcher creates any game process.
    /// </summary>
    public static async Task<ServerModManifest> QueryAsync(string address, CancellationToken token)
    {
        var (host, port) = ServerAddress.Parse(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var tcp = new TcpClient { NoDelay = true };
        await using var tls = await ConnectAsync(tcp, host, port, timeout.Token).ConfigureAwait(false);
        try
        {
            await ServerModProtocol.WriteAsync(tls, ServerModProtocol.ManifestQuery, ReadOnlyMemory<byte>.Empty, timeout.Token).ConfigureAwait(false);
            return await ServerModProtocol.ReadAsync(tls, ServerModProtocol.ManifestReply, timeout.Token).ConfigureAwait(false);
        }
        catch (EndOfStreamException error)
        {
            throw new IOException("This server does not offer mod synchronization. Update the server and enable ShareMods.", error);
        }
    }

    /// <summary>
    /// Streams only the advertised immutable archive, rejecting stale descriptors, truncated data and mismatched SHA-256 before extraction.
    /// </summary>
    public static async Task DownloadAsync(string address, ServerModManifest manifest, string destination, IProgress<long>? progress,
        CancellationToken token)
    {
        manifest.Validate();
        var (host, port) = ServerAddress.Parse(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var tcp = new TcpClient { NoDelay = true };
        await using var tls = await ConnectAsync(tcp, host, port, timeout.Token).ConfigureAwait(false);
        await ServerModProtocol.WriteAsync(tls, ServerModProtocol.ArchiveQuery, Encoding.ASCII.GetBytes(manifest.ArchiveSha256), timeout.Token)
            .ConfigureAwait(false);
        var current = await ServerModProtocol.ReadAsync(tls, ServerModProtocol.ArchiveReply, timeout.Token).ConfigureAwait(false);
        if (current.Identity != manifest.Identity) throw new InvalidDataException("Server mods changed during synchronization. Reconnect to refresh them.");

        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long done = 0, reported = 0;
        while (done < manifest.ArchiveBytes)
        {
            int wanted = (int)Math.Min(buffer.Length, manifest.ArchiveBytes - done);
            int count = await tls.ReadAsync(buffer.AsMemory(0, wanted), timeout.Token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Server mod download was interrupted. Reconnect to retry.");

            await file.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            done += count;
            if (Environment.TickCount64 - reported < 100) continue;

            progress?.Report(done);
            reported = Environment.TickCount64;
        }

        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Server mod archive failed SHA-256 verification.");
        progress?.Report(done);
    }

    /// <summary>
    /// Keeps TLS policy consistent with public game discovery; archive hashes verify content, not the server operator's identity.
    /// </summary>
    static async Task<SslStream> ConnectAsync(TcpClient tcp, string host, int port, CancellationToken token)
    {
        await tcp.ConnectAsync(host, port, token).ConfigureAwait(false);
        var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, static (_, certificate, _, _) => certificate is not null);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowRenegotiation = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, token).ConfigureAwait(false);
            return tls;
        }
        catch
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
