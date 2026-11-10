namespace Meitou.Diagnostics;

public readonly record struct LatencyTraceRecord(Guid Run, Guid Case, long Frequency, LatencyTraceEvent Event);
public sealed record LatencyRequestReport(Guid ClientRun, Guid? ServerRun, Guid World, ulong Connection,
    ulong Generation, ulong Request, ulong Entity, string Classification, bool Joined,
    double? IntentToPoseMs, double? AcceptanceToMotionMs, double? UnmeasuredResidualMs,
    double? SourceToPresentedTickLag, IReadOnlyDictionary<string, double?> DurationsMs, IReadOnlyList<string> Missing)
{
    public double? LatestReceivedToPresentedTickLag { get; init; }
}

/// <summary>Joins opaque evidence. All clock subtraction is constrained to one run.</summary>
public static class LatencyTraceAnalyzer
{
    public static IReadOnlyList<LatencyRequestReport> Analyze(IEnumerable<LatencyTraceRecord> source)
    {
        var records = source.ToArray();
        var serverRequests = records.Where(r => r.Event.Stage is LatencyStage.AuthorityExecute or LatencyStage.AuthorityQueued or LatencyStage.AuthorityReceipt)
            .GroupBy(r => RequestKey(r.Event)).ToDictionary(g => g.Key, g => g.ToArray());
        var requests = records.Where(r => r.Event.Request != 0).GroupBy(r => (r.Run, Key: RequestKey(r.Event)))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var publications = records.Where(r => r.Event.Publication != 0).GroupBy(r => (r.Run, r.Event.World, r.Event.Connection, r.Event.Generation, r.Event.Publication))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var paths = records.Where(r => r.Event.Path != 0).GroupBy(r => (r.Run, r.Event.PathService, r.Event.Path))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var appliedViews = records.Where(r => r.Event.Stage == LatencyStage.PublicationApplied && r.Event.Endpoint == LatencyEndpoint.Client)
            .GroupBy(r => (r.Run, r.Event.World, r.Event.Connection, r.Event.Generation))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Event.Timestamp).ToArray());
        var reports = new List<LatencyRequestReport>();
        foreach (var intent in records.Where(r => r.Event.Stage == LatencyStage.Intent))
        {
            var key = intent.Event;
            var client = requests[(intent.Run, RequestKey(key))];
            var candidates = serverRequests.GetValueOrDefault(RequestKey(key), [])
                .Where(r => r.Case == intent.Case).Select(r => r.Run).Distinct().ToArray();
            if (candidates.Length != 1)
            {
                reports.Add(new(intent.Run, null, key.World, key.Connection, key.Generation, key.Request, key.Entity,
                    candidates.Length == 0 ? "MissingServer" : "AmbiguousServerRun", false, null, null, null, null,
                    new Dictionary<string, double?>(), ["unique server process/run"]));
                continue;
            }
            Guid serverRun = candidates[0];
            var server = requests[(serverRun, RequestKey(key))];
            var terminal = server.Where(r => r.Event.Stage == LatencyStage.Terminal && r.Event.Endpoint != LatencyEndpoint.Client
                && (r.Event.Entity == key.Entity || r.Event.Entity == 0)).OrderBy(r => r.Event.Timestamp).LastOrDefault();
            var accepted = First(server, LatencyStage.MovementAccepted, key.Entity);
            var motion = First(server, LatencyStage.AuthorityRootChanged, key.Entity);
            var pose = First(client, LatencyStage.RendererRootSubmitted, key.Entity);
            var latestApplied = pose == default ? default : appliedViews.GetValueOrDefault(
                (intent.Run, key.World, key.Connection, key.Generation), [])
                .LastOrDefault(r => r.Event.Timestamp <= pose.Event.Timestamp);
            var clientTerminal = client.Where(r => r.Event.Stage == LatencyStage.Terminal && r.Event.Endpoint == LatencyEndpoint.Client && r.Event.Entity == key.Entity)
                .OrderBy(r => r.Event.Timestamp).LastOrDefault();
            bool presentationEnded = pose == default && clientTerminal != default;
            var missing = new List<string>();
            var durations = new Dictionary<string, double?>();
            if (terminal == default) missing.Add("authority terminal classification");
            bool moved = terminal != default && terminal.Event.Outcome == LatencyOutcome.Moved;
            LatencyTraceRecord containment = default, applied = default, writeStarted = default;
            if (moved)
            {
                if (accepted == default) missing.Add("movement acceptance");
                if (motion == default) missing.Add("first authority root change");
                var bound = server.FirstOrDefault(r => r.Event.Stage == LatencyStage.PathBound && r.Event.Entity == key.Entity
                    && (motion == default || r.Event.PathService == motion.Event.PathService && r.Event.Path == motion.Event.Path));
                var pathEvents = bound == default ? [] : paths.GetValueOrDefault((serverRun, bound.Event.PathService, bound.Event.Path), []);
                var pathQueued = First(pathEvents, LatencyStage.PathQueued);
                var pathStarted = First(pathEvents, LatencyStage.PathStarted);
                var pathFinished = First(pathEvents, LatencyStage.PathFinished);
                var pathApplied = First(pathEvents, LatencyStage.PathApplied, key.Entity);
                if (bound == default || pathQueued == default || pathStarted == default || pathFinished == default || pathApplied == default)
                    missing.Add("path queue/start/end/apply correlation");
                durations["pathQueue"] = Milliseconds(pathQueued, pathStarted);
                durations["pathExecution"] = Milliseconds(pathStarted, pathFinished);
                durations["pathFinishToApply"] = Milliseconds(pathFinished, pathApplied);
                if (pose == default)
                {
                    if (!presentationEnded) missing.Add("renderer root submission");
                }
                else
                {
                    containment = server.FirstOrDefault(r => r.Event.Stage == LatencyStage.PublicationContainsMotion
                        && r.Event.Entity == key.Entity && r.Event.Publication == pose.Event.Publication);
                    if (containment == default) missing.Add("exact publication containment");
                    var pubKey = (pose.Event.World, pose.Event.Connection, pose.Event.Generation, pose.Event.Publication);
                    var clientPublication = publications.GetValueOrDefault((intent.Run, pubKey.World, pubKey.Connection, pubKey.Generation, pubKey.Publication), []);
                    bool packetMotion = pose.Event.Value != 0;
                    applied = First(packetMotion ? clientPublication.Where(r => r.Event.Entity == key.Entity && r.Event.Value == pose.Event.Value) : clientPublication,
                        LatencyStage.PublicationApplied);
                    if (applied == default) missing.Add("source publication application");
                    var serverPublication = publications.GetValueOrDefault((serverRun, pubKey.World, pubKey.Connection, pubKey.Generation, pubKey.Publication), []);
                    var actualSends = packetMotion ? serverPublication.Where(r => r.Event.Entity == key.Entity && r.Event.Value == pose.Event.Value) : serverPublication;
                    var written = First(actualSends, packetMotion ? LatencyStage.MotionWriteComplete : LatencyStage.PublicationWriteComplete);
                    var sendQueued = First(actualSends, packetMotion ? LatencyStage.MotionSendQueued : LatencyStage.PublicationSendQueued);
                    writeStarted = First(actualSends, packetMotion ? LatencyStage.MotionWriteStarted : LatencyStage.PublicationWriteStarted);
                    var captureStart = First(serverPublication, LatencyStage.CaptureStarted);
                    var captureEnd = First(serverPublication, LatencyStage.CaptureFinished);
                    var encodeStart = First(serverPublication, LatencyStage.EncodeStarted);
                    var encodeEnd = First(serverPublication, LatencyStage.EncodeFinished);
                    durations["capture"] = Milliseconds(captureStart, captureEnd);
                    durations["encode"] = Milliseconds(encodeStart, encodeEnd);
                    durations["encodeToSendQueue"] = Milliseconds(encodeEnd, sendQueued);
                    durations["writerQueue"] = Milliseconds(sendQueued, writeStarted);
                    durations["writeDuration"] = Milliseconds(writeStarted, written);
                    if (sendQueued == default || writeStarted == default || written == default)
                        missing.Add("publication writer queue/start/completion boundaries");
                    if (captureStart == default || captureEnd == default || encodeStart == default || encodeEnd == default)
                        missing.Add("publication capture/encode boundaries");
                }
            }
            var received = First(server, LatencyStage.ServerFrameReceived);
            var dispatched = First(server, LatencyStage.ServerDispatch);
            var businessDequeued = First(server, LatencyStage.ServerBusinessDequeued);
            var enqueued = First(client, LatencyStage.ClientEnqueue);
            var writeComplete = First(client, LatencyStage.ClientWriteComplete);
            var receipt = First(client, LatencyStage.ReceiptReceived);
            var authorityQueued = First(server, LatencyStage.AuthorityQueued);
            var authorityExecute = First(server, LatencyStage.AuthorityExecute);
            var authorityReceipt = First(server, LatencyStage.AuthorityReceipt);
            durations["intentToReceipt"] = Milliseconds(intent, receipt);
            durations["clientQueueToWrite"] = Milliseconds(enqueued, writeComplete);
            durations["serverFrameToDispatch"] = Milliseconds(received, dispatched);
            durations["serverBusinessQueue"] = Milliseconds(received, businessDequeued);
            durations["serverAdmissionCheck"] = Milliseconds(businessDequeued, dispatched);
            durations["authorityQueue"] = Milliseconds(authorityQueued, authorityExecute);
            durations["authorityExecution"] = Milliseconds(authorityExecute, authorityReceipt);
            durations["clientApplyToPose"] = Milliseconds(applied, pose);
            if (received == default) missing.Add("complete server frame receipt");
            if (dispatched == default) missing.Add("server dispatch");
            if (enqueued == default) missing.Add("client enqueue");
            if (writeComplete == default) missing.Add("client write completion");
            if (receipt == default && !presentationEnded) missing.Add("client receipt");
            double? total = moved ? Milliseconds(intent, pose) : null;
            double? serverSpan = Milliseconds(received, writeStarted);
            double? presentationSpan = Milliseconds(applied, pose);
            double? residual = total is { } t && serverSpan is { } s && presentationSpan is { } p && t >= s + p ? t - s - p : null;
            if (moved && residual is null && !presentationEnded) missing.Add("transit/scheduling residual boundaries");
            string classification = terminal == default ? "CensoredMissingTerminal" : terminal.Event.Outcome.ToString();
            if (moved && presentationEnded) classification = clientTerminal.Event.Outcome == LatencyOutcome.Superseded
                ? "MovedBeforePresentationSuperseded" : "ClientCensored";
            reports.Add(new(intent.Run, serverRun, key.World, key.Connection, key.Generation, key.Request, key.Entity,
                classification, moved && pose != default && missing.Count == 0,
                total, Milliseconds(accepted, motion), residual,
                pose == default ? null : pose.Event.Tick - pose.Event.PresentationTick, durations, missing)
            {
                LatestReceivedToPresentedTickLag = latestApplied == default ? null : latestApplied.Event.Tick - pose.Event.PresentationTick,
            });
        }
        return reports;
    }

    static (Guid World, ulong Connection, ulong Generation, ulong Request) RequestKey(LatencyTraceEvent e) => (e.World, e.Connection, e.Generation, e.Request);
    static LatencyTraceRecord First(IEnumerable<LatencyTraceRecord> records, LatencyStage stage, ulong? entity = null) =>
        records.Where(r => r.Event.Stage == stage && (entity is null || r.Event.Entity == entity)).OrderBy(r => r.Event.Timestamp).FirstOrDefault();
    static double? Milliseconds(LatencyTraceRecord start, LatencyTraceRecord end) =>
        start == default || end == default || start.Run != end.Run || start.Frequency <= 0 || start.Frequency != end.Frequency || end.Event.Timestamp < start.Event.Timestamp
            ? null : (end.Event.Timestamp - start.Event.Timestamp) * 1000d / start.Frequency;
}
