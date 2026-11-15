using System.Diagnostics;
using System.Numerics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Meitou.Diagnostics;
using Meitou.Game.Host;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Movement;

namespace Meitou.Tests.Engine;

[CollectionDefinition("LatencyTrace", DisableParallelization = true)]
public sealed class LatencyTraceCollection;

[Collection("LatencyTrace")]
public partial class SecureServerTests
{
    /// <summary>
    /// Operator collection returns multi-chunk client logs over native QUIC, records disabled peers and keeps admission messages usable.
    /// </summary>
    [Fact]
    public async Task OperatorDiagnosticBatchCollectsOnlineClientPackagesAndRuntimeLogs()
    {
        await using var scene = new Scene();
        using var diagnostics = new DiagnosticSession("server", "test", Path.Combine(scene.Root, "diagnostics"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        byte[] log = Encoding.UTF8.GetBytes("test runtime log\n" + Convert.ToHexString(RandomNumberGenerator.GetBytes(180_000)));
        Task<string?> Capture(Guid report, DiagnosticScope scope, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var recorder = new DiagnosticRecorder { Enabled = true };
            var snapshot = recorder.Capture(report, Guid.NewGuid(), "client", "test", scope, "server-requested") with { RuntimeLogStatus = "sanitized" };
            var store = new DiagnosticStore(Path.Combine(scene.Root, "client-" + scope.Connection));
            return Task.FromResult<string?>(store.Save(snapshot, runtimeLog: log));
        }

        Task<RemoteGameSession> Connect(int index, Func<Guid, DiagnosticScope, CancellationToken, Task<string?>> capture)
        {
            var credential = scene.Actors[index].Credential;
            return RemoteGameSession.ConnectAsync("127.0.0.1", scene.Server.Endpoint.Port, Hello(), new(credential.Account, credential.Token),
                new(scene.Data.Db), timeout.Token, transportOptions: NativeFixture.Options(scene.Certificate), diagnosticCapture: capture);
        }

        await using var first = await Connect(0, Capture);
        await using var second = await Connect(1, Capture);
        await using var disabled = await Connect(2, (_, _, _) => Task.FromResult<string?>(null));
        first.RequestEntry(scene.Actors[0].Grant.Entity);
        await WaitRemote(first, state => state.CanControl, timeout.Token);
        Assert.True(await Meitou.Server.ServerDiagnosticConsole.ExecuteAsync("diagnostics collect-all", diagnostics, scene.Server.Diagnostics, timeout.Token));
        Assert.False(scene.Server.Diagnostics.Start(diagnostics, timeout.Token));
        first.RequestBaseline();
        first.RefreshAdmission();
        second.RefreshAdmission();
        var batch = await scene.Server.Diagnostics.Completion!.WaitAsync(timeout.Token);
        Assert.Equal(3, batch.Players.Length);
        Assert.Equal(2, batch.Players.Count(p => p.Status == "Saved"));
        Assert.Equal("Disabled", Assert.Single(batch.Players, p => p.Scope.Connection == disabled.State.Admission.Identity.Player.Value).Status);
        string folder = Path.Combine(diagnostics.Store.Root, "batches", batch.Report.ToString("N"));
        Assert.True(File.Exists(Path.Combine(folder, "server.zip")));
        Assert.True(File.Exists(Path.Combine(folder, "batch.json")));
        foreach (var peer in batch.Players.Where(p => p.Status == "Saved")) VerifyBatchPackage(folder, peer, batch.Report, log);
        Assert.Null(first.State.Disconnection);
        Assert.Null(second.State.Disconnection);
        await WaitRemote(first, state => state.CanControl, timeout.Token);
    }

    /// <summary>
    /// Verifies the stored manifest's authenticated scope and exact runtime-log bytes after a multi-chunk transfer.
    /// </summary>
    static void VerifyBatchPackage(string folder, Meitou.Server.DiagnosticPeerCapture peer, Guid report, byte[] log)
    {
        string path = Path.Combine(folder, peer.File!);
        Assert.True(new FileInfo(path).Length > DiagnosticTransferProtocol.ChunkBytes * 2);
        var package = DiagnosticPackage.Read(path);
        Assert.Equal(report, package.Report);
        Assert.Equal(peer.Scope, package.Scope);
        using var zip = ZipFile.OpenRead(path);
        using var runtime = zip.GetEntry("runtime.log")!.Open();
        using var bytes = new MemoryStream();
        runtime.CopyTo(bytes);
        Assert.Equal(log, bytes.ToArray());
    }

    /// <summary>
    /// Real authenticated QUIC requests retain separate scopes and cannot enable disabled server collection.
    /// </summary>
    [Fact]
    public async Task DiagnosticCapturePairsAuthenticatedConnectionsAndHonorsServerSwitch()
    {
        await using var scene = new Scene();
        using var diagnostics = new DiagnosticSession("server", "test", Path.Combine(scene.Root, "diagnostics"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var first = await ConnectRemote(scene, scene.Actors[0].Credential, timeout.Token);
        await using var second = await ConnectRemote(scene, scene.Actors[1].Credential, timeout.Token);
        Guid report = Guid.NewGuid();
        var a = await first.CaptureDiagnosticsAsync(report, timeout.Token);
        var b = await second.CaptureDiagnosticsAsync(report, timeout.Token);
        Assert.Equal(Meitou.Networking.DiagnosticCaptureStatus.Saved, a.Status);
        Assert.Equal(Meitou.Networking.DiagnosticCaptureStatus.Saved, b.Status);
        Assert.NotEqual(a.Scope.Connection, b.Scope.Connection);
        Assert.Equal(2, diagnostics.Store.Pending().Length);
        Assert.Equal(a, await first.CaptureDiagnosticsAsync(report, timeout.Token));
        Assert.Equal(2, diagnostics.Store.Pending().Length);

        diagnostics.SetEnabled(false);
        var disabled = await first.CaptureDiagnosticsAsync(Guid.NewGuid(), timeout.Token);
        Assert.Equal(Meitou.Networking.DiagnosticCaptureStatus.Disabled, disabled.Status);
        Assert.False(diagnostics.Recorder.Enabled);
        Assert.Equal(2, diagnostics.Store.Pending().Length);
    }

    sealed class LatencySink : ILatencyTraceSink
    {
        readonly Lock _gate = new();
        readonly List<LatencyTraceRecord> _records = [];
        readonly Guid _run = Guid.NewGuid();
        public void Emit(in LatencyTraceEvent e) { lock (_gate) _records.Add(new(_run, Guid.Empty, Stopwatch.Frequency, e)); }
        public LatencyTraceRecord[] Records { get { lock (_gate) return _records.ToArray(); } }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task LatencyTraceRealQuicAuthorityPathPublicationAndPresentationHooksJoinWithoutClockGuessing(bool synchronousPaths, bool supersedeBeforePose)
    {
        var sink = new LatencySink(); var previousSink = LatencyTrace.Sink; LatencyTrace.Sink = sink;
        try
        {
            await using var scene = new Scene(movement: true, synchronousPaths: synchronousPaths);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = timeout.Token;
            var actor = scene.Actors[0];
            await scene.Loop.InvokeAsync(() =>
            {
                var world = scene.Runtime.World;
                world.ObserveLatency(sink);
                // Place preserves normal fixture identity/ownership; speed fields are synthetic scene setup.
                world.Characters.Place(actor.Role.Slot, new(0, 300, 0), 0);
            }, token);
            await using var remote = new RemotePlayerSession(await ConnectRemote(scene, actor.Credential, token), (_, _) => 300);
            remote.Connection.RequestEntry(actor.Grant.Entity);
            await PumpPlayer(remote, () => remote.CanControl, token);
            remote.Submit(new MoveOrder([actor.Role], new(400, 300, 0)) { Tick = 0 });
            await PumpPlayer(remote, () => remote.PendingCommands == 0, token);
            // Step the real runtime owner deterministically. No wall-clock latency threshold is asserted.
            while (remote.CurrentSnapshot.Characters.Single(c => c.Id == actor.Role).Position.X <= 0)
            {
                await scene.Loop.InvokeAsync(() => scene.Runtime.World.RunTick(), token);
                remote.Advance(.01); await Task.Delay(5, token);
            }
            // This is the same callback GameHost calls after adding the visible root to its draw list.
            // A stationary root (including idle-animation-only turns) must not satisfy it.
            remote.TraceRendererRootSubmission(actor.Role, new(0, 300, 0), 1);
            Assert.DoesNotContain(sink.Records, r => r.Event.Stage == LatencyStage.RendererRootSubmitted);
            if (supersedeBeforePose)
            {
                remote.Submit(new MoveOrder([actor.Role], new(500, 300, 0)) { Tick = 0 });
                await PumpPlayer(remote, () => remote.PendingCommands == 0, token);
                var superseded = Assert.Single(LatencyTraceAnalyzer.Analyze(sink.Records), r => r.Request == 1);
                Assert.Equal("MovedBeforePresentationSuperseded", superseded.Classification);
                Assert.False(superseded.Joined); Assert.Empty(superseded.Missing);
                return;
            }
            var drawn = remote.CurrentSnapshot.Characters.Single(c => c.Id == actor.Role).Position;
            remote.TraceRendererRootSubmission(actor.Role, drawn, 2);
            await PumpPlayer(remote, () => remote.PendingCommands == 0, token);
            var report = Assert.Single(LatencyTraceAnalyzer.Analyze(sink.Records));
            Assert.Equal("Moved", report.Classification);
            Assert.True(report.Joined, string.Join(", ", report.Missing));
            Assert.NotNull(report.IntentToPoseMs); Assert.NotNull(report.AcceptanceToMotionMs);
            var records = sink.Records;
            Assert.Contains(records, r => r.Event.Stage == LatencyStage.PathStarted);
            Assert.Contains(records, r => r.Event.Stage == LatencyStage.PathFinished);
            Assert.Contains(records, r => r.Event.Stage == LatencyStage.PathApplied);
            var bound = Assert.Single(records, r => r.Event.Stage == LatencyStage.PathBound);
            var queued = Assert.Single(records, r => r.Event.Stage == LatencyStage.PathQueued && r.Event.PathService == bound.Event.PathService && r.Event.Path == bound.Event.Path);
            var started = Assert.Single(records, r => r.Event.Stage == LatencyStage.PathStarted && r.Event.PathService == bound.Event.PathService && r.Event.Path == bound.Event.Path);
            Assert.True(queued.Event.Timestamp <= started.Event.Timestamp);
            var pose = Assert.Single(records, r => r.Event.Stage == LatencyStage.RendererRootSubmitted);
            var sourceApplied = Assert.Single(records, r => r.Event.Stage == LatencyStage.PublicationApplied && r.Event.Publication == pose.Event.Publication && r.Event.Entity == pose.Event.Entity && r.Event.Value == pose.Event.Value);
            var newerReceived = sourceApplied with { Event = sourceApplied.Event with
            {
                Publication = pose.Event.Publication + 1, Tick = pose.Event.Tick + 3, Timestamp = pose.Event.Timestamp - 1,
            } };
            var ahead = Assert.Single(LatencyTraceAnalyzer.Analyze(records.Append(newerReceived)));
            Assert.Equal(report.SourceToPresentedTickLag, ahead.SourceToPresentedTickLag);
            Assert.Equal(report.SourceToPresentedTickLag + 3, ahead.LatestReceivedToPresentedTickLag);
            Assert.Contains(records, r => r.Event.Stage == LatencyStage.PublicationContainsMotion && r.Event.Publication == pose.Event.Publication);
            var missingContainment = Assert.Single(LatencyTraceAnalyzer.Analyze(records.Where(r => r.Event.Stage != LatencyStage.PublicationContainsMotion)));
            Assert.False(missingContainment.Joined); Assert.Contains("exact publication containment", missingContainment.Missing);
            var missingPath = Assert.Single(LatencyTraceAnalyzer.Analyze(records.Where(r => r.Event.Stage != LatencyStage.PathFinished)));
            Assert.False(missingPath.Joined); Assert.Contains("path queue/start/end/apply correlation", missingPath.Missing);
            Guid separateServerRun = Guid.NewGuid();
            var separateClocks = records.Select(r => r.Event.Stage is LatencyStage.ServerFrameReceived or LatencyStage.ServerDispatch or LatencyStage.ServerBusinessDequeued
                or LatencyStage.AuthorityQueued or LatencyStage.AuthorityExecute or LatencyStage.AuthorityReceipt or LatencyStage.MovementAccepted
                or LatencyStage.PathQueued or LatencyStage.PathBound or LatencyStage.PathStarted or LatencyStage.PathFinished or LatencyStage.PathApplied
                or LatencyStage.AuthorityRootChanged or LatencyStage.CaptureStarted or LatencyStage.CaptureFinished or LatencyStage.EncodeStarted
                or LatencyStage.EncodeFinished or LatencyStage.PublicationSendQueued or LatencyStage.PublicationWriteStarted
                or LatencyStage.PublicationWriteComplete or LatencyStage.MotionSendQueued or LatencyStage.MotionWriteStarted or LatencyStage.MotionWriteComplete or LatencyStage.PublicationContainsMotion or LatencyStage.Terminal
                ? r with { Run = separateServerRun, Frequency = r.Frequency * 2,
                    Event = r.Event with { Timestamp = r.Event.Timestamp * 2 + 1_000_000_000_000 } } : r);
            var separateReport = Assert.Single(LatencyTraceAnalyzer.Analyze(separateClocks));
            Assert.True(separateReport.Joined, string.Join(", ", separateReport.Missing));
            Assert.Equal(report.IntentToPoseMs, separateReport.IntentToPoseMs);
            Assert.Equal(report.AcceptanceToMotionMs, separateReport.AcceptanceToMotionMs);
            Assert.Equal(report.UnmeasuredResidualMs, separateReport.UnmeasuredResidualMs);
            Assert.NotNull(report.DurationsMs["serverBusinessQueue"]);
            Assert.NotNull(report.DurationsMs["serverAdmissionCheck"]);
            Assert.Equal(report.DurationsMs["serverBusinessQueue"], separateReport.DurationsMs["serverBusinessQueue"]);
            Assert.Equal(report.DurationsMs["serverAdmissionCheck"], separateReport.DurationsMs["serverAdmissionCheck"]);
            var replayedServer = records.Where(r => r.Event.Stage is LatencyStage.AuthorityQueued or LatencyStage.AuthorityExecute or LatencyStage.AuthorityReceipt)
                .Select(r => r with { Run = Guid.NewGuid() });
            Assert.Equal("AmbiguousServerRun", Assert.Single(LatencyTraceAnalyzer.Analyze(records.Concat(replayedServer))).Classification);
            if (Environment.GetEnvironmentVariable("MEITOU_LATENCY_TEST_OUTPUT") is { Length: > 0 } output)
            {
                await using var writer = new BoundedLatencyTraceWriter(new FileStream(output + (synchronousPaths ? ".sync.jsonl" : ".async.jsonl"), FileMode.Create, FileAccess.Write));
                foreach (var record in records) writer.Emit(record.Event);
            }
        }
        finally { LatencyTrace.Sink = previousSink; }
    }
}
