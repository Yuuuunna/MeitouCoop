using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Diagnostics;
using Meitou.Networking.VNext;

namespace Meitou.Server;

/// <summary>
/// Immutable operator batch manifest; files are stored beneath its report ID, not uploaded to public feedback.
/// </summary>
public sealed record DiagnosticBatch(Guid Report, DateTimeOffset StartedUtc, string ServerStatus, DiagnosticPeerCapture[] Players);

/// <summary>
/// Online authenticated peer registry and bounded operator-triggered collection batches.
/// </summary>
public sealed class ServerDiagnosticCollection
{
    /// <summary>
    /// Protects the online set independently of the simulation owner.
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// Only the newest connection generation for each world/player may participate.
    /// </summary>
    readonly Dictionary<(Guid World, ulong Player), ServerDiagnosticPeer> _peers = new();
    /// <summary>
    /// One operator batch per server at a time.
    /// </summary>
    readonly SemaphoreSlim _batchGate = new(1, 1);
    /// <summary>
    /// Current or most recent command task, retained so the console can report its status.
    /// </summary>
    Task<DiagnosticBatch>? _batch;

    /// <summary>
    /// Human-readable command status without reading mutable game state.
    /// </summary>
    public string Status => _batch is null ? "idle"
        : _batch.IsCompleted ? "finished"
        : "collecting";

    /// <summary>
    /// Registers only after authentication and fences any previous connection generation.
    /// </summary>
    public ServerDiagnosticPeer Register(DiagnosticScope scope, NativeGameConnection transport)
    {
        var peer = new ServerDiagnosticPeer(scope, async (frame, token) =>
        {
            while (!transport.TrySendProtocolControl(frame)) await Task.Delay(10, token).ConfigureAwait(false);
        }, Unregister);
        ServerDiagnosticPeer? previous;
        lock (_gate)
        {
            var key = (scope.World, scope.Connection);
            _peers.TryGetValue(key, out previous);
            if (previous is null || previous.Scope.Generation < scope.Generation) _peers[key] = peer;
            else previous = peer;
        }

        previous?.Dispose();
        return peer;
    }

    /// <summary>
    /// Takes a snapshot of the online set immediately and starts nonblocking console collection.
    /// </summary>
    public bool Start(DiagnosticSession session, CancellationToken cancellationToken)
    {
        if (!_batchGate.Wait(0)) return false;
        ServerDiagnosticPeer[] peers;
        lock (_gate) peers = _peers.Values.ToArray();
        _batch = Task.Run(() => Run(session, peers, cancellationToken), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Joins the current command for shutdown or integration checks.
    /// </summary>
    public Task<DiagnosticBatch>? Completion => _batch;

    /// <summary>
    /// Starts a bounded batch directory, preserves server evidence and records every target's final status.
    /// </summary>
    async Task<DiagnosticBatch> Run(DiagnosticSession session, ServerDiagnosticPeer[] peers, CancellationToken token)
    {
        Guid report = Guid.NewGuid();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            string directory = PrepareDirectory(session.Store.Root, report, peers.Length);
            Console.WriteLine($"Diagnostic batch {report:N}: {peers.Length} authenticated online players; output={directory}");
            string serverStatus = CaptureServer(session, report, directory);
            var initial = new DiagnosticBatch(report, started, serverStatus, peers.Select(p => new DiagnosticPeerCapture(p.Scope, "Pending")).ToArray());
            WriteManifest(directory, initial);
            var results = await CollectPeers(peers, report, directory, token).ConfigureAwait(false);
            var batch = initial with { Players = results };
            WriteManifest(directory, batch);
            Console.WriteLine($"Diagnostic batch {report:N} finished: saved={results.Count(r => r.Status == "Saved")}/{results.Length}; output={directory}");
            return batch;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Diagnostic batch {report:N} failed: {error.Message}");
            return new(report, started, "Failed:" + error.GetType().Name, peers.Select(p => new DiagnosticPeerCapture(p.Scope, "NotCollected")).ToArray());
        }
        finally
        {
            _batchGate.Release();
        }
    }

    /// <summary>
    /// Requests all online snapshots immediately, then limits simultaneous file uploads to four connections.
    /// </summary>
    static async Task<DiagnosticPeerCapture[]> CollectPeers(ServerDiagnosticPeer[] peers, Guid report, string directory, CancellationToken token)
    {
        using var slots = new SemaphoreSlim(4, 4);
        async Task<DiagnosticPeerCapture> Collect(ServerDiagnosticPeer peer)
        {
            var result = await peer.CollectAsync(report, directory, token, slots).ConfigureAwait(false);
            Console.WriteLine($"Diagnostic player={peer.Scope.Connection}/{peer.Scope.Generation}: {result.Status}; runtimeLog={result.RuntimeLog ?? "unavailable"}");
            return result;
        }

        return await Task.WhenAll(peers.Select(Collect)).ConfigureAwait(false);
    }

    /// <summary>
    /// Reserves a bounded disk budget and never silently deletes completed operator evidence.
    /// </summary>
    static string PrepareDirectory(string root, Guid report, int players)
    {
        string batches = Directory.CreateDirectory(Path.Combine(root, "batches")).FullName;
        var directory = new DirectoryInfo(batches);
        long used = directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        if (directory.GetDirectories().Length >= 8 || used + (players + 1) * DiagnosticStore.MaximumBytes > 2L << 30)
            throw new IOException("Diagnostic batch storage is full; archive or remove old batches first.");
        return Directory.CreateDirectory(Path.Combine(batches, report.ToString("N"))).FullName;
    }

    /// <summary>
    /// Uses the same incident format for server evidence without consuming the ordinary manual-report outbox.
    /// </summary>
    static string CaptureServer(DiagnosticSession session, Guid report, string directory)
    {
        try
        {
            DiagnosticStore.WritePackage(session.Snapshot(report, new(), "operator-all"), Path.Combine(directory, "server.zip"));
            return session.Recorder.Enabled ? "Saved" : "SavedCollectionDisabled";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "Failed:" + error.GetType().Name;
        }
    }

    /// <summary>
    /// Writes an atomic index before and after collection so an interrupted batch remains discoverable.
    /// </summary>
    static void WriteManifest(string directory, DiagnosticBatch batch)
    {
        string path = Path.Combine(directory, "batch.json");
        string temporary = path + ".part";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, batch, DiagnosticBatchJson.Default.DiagnosticBatch);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// A stale teardown cannot unregister a replacement connection.
    /// </summary>
    void Unregister(ServerDiagnosticPeer peer)
    {
        lock (_gate)
        {
            var key = (peer.Scope.World, peer.Scope.Connection);
            if (_peers.GetValueOrDefault(key) == peer) _peers.Remove(key);
        }
    }
}

/// <summary>
/// Stable serialization for operator batch indexes.
/// </summary>
[JsonSerializable(typeof(DiagnosticBatch))]
internal sealed partial class DiagnosticBatchJson : JsonSerializerContext;
