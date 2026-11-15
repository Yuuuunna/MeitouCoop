using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Meitou.Diagnostics;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Reports;
using Microsoft.AspNetCore.Http;

namespace Meitou.Tests.Engine;

/// <summary>
/// Incident lifecycle tests: bounds, runtime switches, scoped evidence and durable idempotent delivery.
/// </summary>
[Collection("LatencyTrace")]
public sealed class DiagnosticCaptureTests
{
    /// <summary>
    /// Disabled capture preserves old evidence and resumes with a new correlation generation.
    /// </summary>
    [Fact]
    public void RuntimeSwitchStopsAllRecorderInputsAndRetainsEvidence()
    {
        var recorder = new DiagnosticRecorder { Enabled = true };
        var scope = new DiagnosticScope(Guid.NewGuid(), 3, 1);
        recorder.Emit(new(LatencyStage.Intent, scope.World, scope.Connection, scope.Generation, Request: 1));
        recorder.Log("before");
        long epoch = recorder.Epoch;
        recorder.Enabled = false;
        recorder.Emit(new(LatencyStage.Intent, Request: 2));
        recorder.Log("after");
        recorder.Sample(new(1, 1, 1, 1, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1));
        recorder.Network(new(Stopwatch.GetTimestamp(), scope, 1, 1, 1, 1, 1, 1, 1));
        var frozen = Capture(recorder, scope);
        Assert.False(frozen.Enabled);
        Assert.Single(frozen.Events);
        Assert.Equal(["before"], frozen.Log);
        Assert.Empty(frozen.Samples);
        Assert.Empty(frozen.Network);
        Assert.True(recorder.Epoch > epoch);

        recorder.Enabled = true;
        recorder.Emit(new(LatencyStage.Intent, scope.World, scope.Connection, scope.Generation, Request: 3));
        Assert.Equal([1UL, 3UL], Capture(recorder, scope).Events.Select(e => e.Request));
    }

    /// <summary>
    /// Capacity eviction is counted and another player's or reconnect generation's data cannot enter a scoped trace.
    /// </summary>
    [Fact]
    public void RingIsBoundedAndConnectionGenerationIsolated()
    {
        var recorder = new DiagnosticRecorder(3) { Enabled = true };
        var scope = new DiagnosticScope(Guid.NewGuid(), 3, 2);
        recorder.Emit(new(LatencyStage.Intent, scope.World, 3, 2, Request: 1));
        recorder.Emit(new(LatencyStage.Intent, scope.World, 4, 2, Request: 2));
        recorder.Emit(new(LatencyStage.Intent, scope.World, 3, 1, Request: 3));
        recorder.Emit(new(LatencyStage.Intent, scope.World, 3, 2, Request: 4));
        recorder.Network(new(Stopwatch.GetTimestamp(), scope, 1, 2, 3, 4, 5, 6, 7));
        recorder.Network(new(Stopwatch.GetTimestamp(), scope with { Connection = 4 }, 2, 3, 4, 5, 6, 7, 8));
        var snapshot = Capture(recorder, scope);
        Assert.Equal(1, snapshot.EvictedEvents);
        Assert.Equal(4UL, Assert.Single(snapshot.Events).Request);
        Assert.Equal(scope, Assert.Single(snapshot.Network).Scope);
        Assert.Equal(3, Capture(recorder, new()).Events.Length);
    }

    /// <summary>
    /// Private console tails still omit secrets and absolute machine paths.
    /// </summary>
    [Fact]
    public void ConsoleTailRedactsSecretsAndBoundsLines()
    {
        Assert.DoesNotContain("abc123", DiagnosticRedaction.Clean("Authorization: Bearer abc123"));
        Assert.DoesNotContain("Alice", DiagnosticRedaction.Clean(@"failed C:\Users\Alice\private.json"));
        Assert.DoesNotContain("alice", DiagnosticRedaction.Clean("failed /home/alice/private.json"));
        Assert.Equal(1024, DiagnosticRedaction.Clean(new string('x', 4096)).Length);
    }

    /// <summary>
    /// Export preserves the full structured event, and failed HTTP keeps identical bytes available for retry.
    /// </summary>
    [Fact]
    public async Task OutboxRetainsFailedUploadAndRetriesIdenticalArtifact()
    {
        using var directory = new Scratch();
        var store = new DiagnosticStore(directory.Path);
        var recorder = new DiagnosticRecorder { Enabled = true };
        recorder.Emit(new(LatencyStage.Intent, Request: 42));
        var snapshot = Capture(recorder, new());
        string path = store.Save(snapshot);
        byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var package = DiagnosticPackage.Read(path);
        Assert.Equal(snapshot.Report, package.Report);
        Assert.Equal(snapshot.Report, Assert.Single(DiagnosticPackage.Trace(package)).Case);
        using (var archive = ZipFile.OpenRead(path))
        using (var entry = archive.GetEntry("diagnostics.json")!.Open())
        {
            var decoded = JsonSerializer.Deserialize(entry, DiagnosticJson.Default.DiagnosticSnapshot)!;
            Assert.Equal(42UL, Assert.Single(decoded.Events).Request);
            Assert.Equal(snapshot.Report, decoded.Report);
        }

        using var handler = new UploadHandler();
        using var http = new HttpClient(handler);
        var collector = new Uri("https://diagnostics.example.test");
        await Assert.ThrowsAsync<HttpRequestException>(() => store.SubmitAsync(path, collector, http, TestContext.Current.CancellationToken));
        Assert.Single(store.Pending());
        handler.Succeed = true;
        await store.SubmitAsync(path, collector, http, TestContext.Current.CancellationToken);
        Assert.Empty(store.Pending());
        Assert.Equal(handler.Requests[0].Url, handler.Requests[1].Url);
        Assert.Equal(original, handler.Requests[0].Bytes);
        Assert.Equal(original, handler.Requests[1].Bytes);
        Assert.True(File.Exists(path[..^4] + ".sent.zip"));
    }

    /// <summary>
    /// Recovery promotion is repeatable and preserves the exact upload bytes instead of recompressing them.
    /// </summary>
    [Fact]
    public void RecoveryPromotionIsIdempotent()
    {
        using var directory = new Scratch();
        var store = new DiagnosticStore(directory.Path);
        var snapshot = Capture(new DiagnosticRecorder(), new());
        store.Recovery(snapshot);
        string recovery = System.IO.Path.Combine(directory.Path, "recovery", $"{snapshot.Run:N}.zip");
        string path = store.QueueRecovery(recovery);
        Assert.Equal(path, store.QueueRecovery(recovery));
        Assert.Single(store.Pending());
        Assert.Equal(File.ReadAllBytes(recovery), File.ReadAllBytes(path));
    }

    /// <summary>
    /// Capture messages reject malformed IDs and stale-view rejection retains the report correlation.
    /// </summary>
    [Fact]
    public void DiagnosticProtocolRejectsMalformedAndCorrelatesStaleRequests()
    {
        Guid report = Guid.NewGuid();
        var frame = DiagnosticProtocol.Request(report);
        Assert.Equal(report, DiagnosticProtocol.ReadRequest(frame));
        Assert.Throws<InvalidDataException>(() => DiagnosticProtocol.ReadRequest(frame with { Payload = new byte[17] }));
        Assert.Throws<InvalidDataException>(() => DiagnosticProtocol.ReadRequest(frame with { Payload = new byte[16] }));
        var rejected = NotDispatchedProtocol.Read(NotDispatchedProtocol.Reply(frame));
        Assert.Equal(report, rejected.Query);
        Assert.Equal(WireMessage.DiagnosticReply, rejected.ReplyKind);
        var reply = new DiagnosticReply(report, new(Guid.NewGuid(), 1, 2), DiagnosticCaptureStatus.Disabled);
        Assert.Equal(reply, DiagnosticProtocol.ReadReply(DiagnosticProtocol.Reply(reply)));
    }

    /// <summary>
    /// Collector acknowledgements survive lost responses, and changed bytes cannot overwrite an existing artifact.
    /// </summary>
    [Fact]
    public async Task PrivateCollectorAcknowledgesDuplicateAndRejectsConflictingBytes()
    {
        using var directory = new Scratch();
        var uploads = new DiagnosticUploads(directory.Path);
        Guid report = Guid.NewGuid(), artifact = Guid.NewGuid();
        byte[] bytes = [1, 2, 3];
        Assert.Equal(204, await uploads.Put(report, artifact, Request(bytes)));
        Assert.Equal(204, await uploads.Put(report, artifact, Request(bytes)));
        Assert.Equal(409, await uploads.Put(report, artifact, Request([9, 8, 7])));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(uploads.Find(report, artifact)!, TestContext.Current.CancellationToken));
        Assert.Single(uploads.List(report));
        var corrupt = Request(bytes);
        corrupt.Headers["X-Content-SHA256"] = new string('0', 64);
        Assert.Equal(400, await uploads.Put(report, Guid.NewGuid(), corrupt));
        var oversized = Request(bytes);
        oversized.ContentLength = DiagnosticStore.MaximumBytes + 1;
        Assert.Equal(413, await uploads.Put(report, Guid.NewGuid(), oversized));
        await uploads.Delete(report);
        Assert.Empty(uploads.List(report));
    }

    /// <summary>
    /// Runtime control persists independently of artifacts, and explicit latency tracing remains chained to its existing sink.
    /// </summary>
    [Fact]
    public void HostSwitchKeepsStableSinkAndStopsNewEvidence()
    {
        using var directory = new Scratch();
        var previous = LatencyTrace.Sink;
        try
        {
            LatencyTrace.Sink = null;
            using var session = new DiagnosticSession("client", "test", directory.Path);
            var sink = LatencyTrace.Sink;
            session.SetEnabled(false);
            Assert.Same(sink, LatencyTrace.Sink);
            Assert.False(DiagnosticControl.Read(directory.Path));
            LatencyTrace.Emit(sink, new(LatencyStage.Intent));
            Assert.Empty(session.Snapshot(Guid.NewGuid(), new(), "test").Events);
            session.SetEnabled(true);
            LatencyTrace.Emit(sink, new(LatencyStage.Intent));
            Assert.Single(session.Snapshot(Guid.NewGuid(), new(), "test").Events);
        }
        finally
        {
            LatencyTrace.Sink = previous;
        }
    }

    /// <summary>
    /// Creates a bounded snapshot with unique identities.
    /// </summary>
    static DiagnosticSnapshot Capture(DiagnosticRecorder recorder, DiagnosticScope scope) =>
        recorder.Capture(Guid.NewGuid(), Guid.NewGuid(), "client", "test", scope, "test");

    /// <summary>
    /// Builds an in-memory upload with the exact content hash used by real clients.
    /// </summary>
    static HttpRequest Request(byte[] bytes)
    {
        var request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream(bytes);
        request.ContentLength = bytes.Length;
        request.Headers["X-Content-SHA256"] = Convert.ToHexString(SHA256.HashData(bytes));
        return request;
    }

    /// <summary>
    /// HTTP transport substitute that records actual request bytes across a failure and retry.
    /// </summary>
    sealed class UploadHandler : HttpMessageHandler
    {
        /// <summary>
        /// Whether the next attempt is acknowledged.
        /// </summary>
        public bool Succeed;
        /// <summary>
        /// Actual request identities and payloads.
        /// </summary>
        public readonly List<(Uri? Url, byte[] Bytes)> Requests = [];
        /// <summary>
        /// Captures the request without contacting a live collector.
        /// </summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            return new(Succeed ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable);
        }
    }

    /// <summary>
    /// Test-owned isolated directory, never a real game install or player outbox.
    /// </summary>
    sealed class Scratch : IDisposable
    {
        /// <summary>
        /// Unique directory created by this test.
        /// </summary>
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meitou-diag-" + Guid.NewGuid().ToString("N"))).FullName;
        /// <summary>
        /// Removes only the uniquely created test directory.
        /// </summary>
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
