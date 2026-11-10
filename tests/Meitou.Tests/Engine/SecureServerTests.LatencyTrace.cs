using System.Diagnostics;
using System.Numerics;
using Meitou.Diagnostics;
using Meitou.Engine;
using Meitou.Simulation;

namespace Meitou.Tests.Engine;

[CollectionDefinition("LatencyTrace", DisableParallelization = true)]
public sealed class LatencyTraceCollection;

[Collection("LatencyTrace")]
public partial class SecureServerTests
{
    sealed class LatencySink : ILatencyTraceSink
    {
        readonly Lock gate = new();
        readonly List<LatencyTraceRecord> records = [];
        readonly Guid run = Guid.NewGuid();
        public void Emit(in LatencyTraceEvent e) { lock (gate) records.Add(new(run, Guid.Empty, Stopwatch.Frequency, e)); }
        public LatencyTraceRecord[] Records { get { lock (gate) return records.ToArray(); } }
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
