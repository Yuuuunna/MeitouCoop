using System.IO.Compression;
using System.Text.Json;

namespace Meitou.Diagnostics;

/// <summary>
/// Bounded reader for private incident packages; no archive entry is extracted to disk.
/// </summary>
public static class DiagnosticPackage
{
    /// <summary>
    /// Reads the versioned manifest and validates the collection limits before exposing evidence to tooling.
    /// </summary>
    public static DiagnosticSnapshot Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > DiagnosticStore.MaximumBytes) throw new InvalidDataException("Diagnostic package exceeds 16 MiB.");
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = archive.GetEntry("diagnostics.json") ?? throw new InvalidDataException("Missing diagnostic manifest.");
        if (entry.Length > 32 << 20) throw new InvalidDataException("Diagnostic manifest exceeds 32 MiB.");
        using var stream = entry.Open();
        var snapshot = JsonSerializer.Deserialize(stream, DiagnosticJson.Default.DiagnosticSnapshot)
            ?? throw new InvalidDataException("Invalid diagnostic manifest.");
        if (snapshot.Schema != 1 || snapshot.Report == Guid.Empty || snapshot.Run == Guid.Empty || snapshot.Artifact == Guid.Empty ||
            snapshot.Frequency <= 0 || snapshot.Role is not ("client" or "server") || snapshot.Events is null or { Length: > 8192 } ||
            snapshot.Samples is null or { Length: > 180 } || snapshot.Network is null or { Length: > 2048 } || snapshot.Log is null or { Length: > 512 })
            throw new InvalidDataException("Invalid diagnostic schema, identity or collection bounds.");
        if (snapshot.Events.Any(e => e.Timestamp <= 0 || !Enum.IsDefined(e.Stage) || !Enum.IsDefined(e.Outcome) || !Enum.IsDefined(e.Endpoint)))
            throw new InvalidDataException("Invalid diagnostic event.");
        return snapshot;
    }

    /// <summary>
    /// Reuses the existing latency analyzer's correlation model; the report ID supplies the shared case ID.
    /// </summary>
    public static IEnumerable<LatencyTraceRecord> Trace(DiagnosticSnapshot snapshot) =>
        snapshot.Events.Select(e => new LatencyTraceRecord(snapshot.Run, snapshot.Report, snapshot.Frequency, e));
}
