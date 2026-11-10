using System.Text.Json;
using Meitou.Diagnostics;

if (args.Length == 0) { Console.Error.WriteLine("Usage: dotnet run --project tools/Meitou.LatencyTrace -- server.jsonl client.jsonl [...]"); return 2; }
var records = new List<LatencyTraceRecord>();
var metadata = new Dictionary<Guid, (Guid Case, long Frequency, int Process)>();
try
{
    foreach (string file in args)
    {
        if (new FileInfo(file).Length > 256 * 1024 * 1024) throw new InvalidDataException("Trace file exceeds 256 MiB analysis limit.");
        foreach (string line in File.ReadLines(file))
        {
            if (line.Length > 16384 || records.Count >= 1_000_000) throw new InvalidDataException("Trace analysis bound exceeded.");
            using var document = JsonDocument.Parse(line); var e = document.RootElement;
            if (e.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unsupported trace schema.");
            Guid run = e.GetProperty("run").GetGuid(), caseId = e.GetProperty("case").GetGuid();
            long frequency = e.GetProperty("frequency").GetInt64(), timestamp = e.GetProperty("time").GetInt64();
            int process = e.GetProperty("process").GetInt32();
            var stage = (LatencyStage)e.GetProperty("stage").GetInt32();
            var outcome = (LatencyOutcome)e.GetProperty("outcome").GetInt32();
            var endpoint = (LatencyEndpoint)e.GetProperty("endpoint").GetInt32();
            if (run == Guid.Empty || frequency <= 0 || timestamp <= 0 || process <= 0 || !Enum.IsDefined(stage) || !Enum.IsDefined(outcome) || !Enum.IsDefined(endpoint))
                throw new InvalidDataException("Invalid run, clock or event enum.");
            var runMetadata = (caseId, frequency, process);
            if (metadata.TryGetValue(run, out var previous) && previous != runMetadata)
                throw new InvalidDataException("Run has inconsistent case, frequency or process metadata.");
            metadata[run] = runMetadata;
            records.Add(new(run, caseId, frequency,
                new((LatencyStage)e.GetProperty("stage").GetInt32(), e.GetProperty("world").GetGuid(),
                    e.GetProperty("connection").GetUInt64(), e.GetProperty("generation").GetUInt64(), e.GetProperty("request").GetUInt64(),
                    e.GetProperty("entity").GetUInt64(), e.GetProperty("pathService").GetGuid(), e.GetProperty("path").GetInt64(),
                    e.GetProperty("publication").GetUInt64(), e.GetProperty("tick").GetInt64(), e.GetProperty("revision").GetUInt64(),
                    e.GetProperty("frame").GetInt64(), e.GetProperty("presentationTick").GetDouble(), (LatencyOutcome)e.GetProperty("outcome").GetInt32(),
                    e.GetProperty("value").GetInt64(), e.GetProperty("time").GetInt64(), endpoint)));
        }
    }
    bool loss = false;
    foreach (var run in records.GroupBy(r => r.Run))
    {
        var footer = run.Where(r => r.Event.Stage == LatencyStage.TraceSummary).ToArray();
        bool complete = footer.Length == 1 && footer[0].Event.Value == 0 && footer[0].Event.Revision == (ulong)(run.Count() - 1)
            && footer[0].Event.Request == footer[0].Event.Revision;
        loss |= !complete;
        Console.WriteLine($"run={run.Key:N} evidence={(complete ? "complete" : "INCOMPLETE: missing/footer/drop/count mismatch")} events={run.Count()} drops={(footer.Length == 1 ? footer[0].Event.Value : -1)}");
        var ticks = run.Where(r => r.Event.Stage == LatencyStage.CaptureFinished).OrderBy(r => r.Event.Timestamp).ToArray();
        if (ticks.Length > 1 && ticks[^1].Event.Timestamp > ticks[0].Event.Timestamp)
            Console.WriteLine($"server tick progress={(ticks[^1].Event.Tick - ticks[0].Event.Tick) * (double)ticks[0].Frequency / (ticks[^1].Event.Timestamp - ticks[0].Event.Timestamp):F2}/second");
    }
    var reports = LatencyTraceAnalyzer.Analyze(records);
    foreach (var r in reports)
    {
        Console.WriteLine($"request={r.Request} entity={r.Entity} connection={r.Connection}/{r.Generation} classification={r.Classification} joined={r.Joined} intentToPoseMs={Number(r.IntentToPoseMs)} acceptanceToMotionMs={Number(r.AcceptanceToMotionMs)} unmeasuredTransitSchedulingResidualMs={Number(r.UnmeasuredResidualMs)} sourceTickLag={Number(r.SourceToPresentedTickLag)} latestReceivedTickLag={Number(r.LatestReceivedToPresentedTickLag)} missing=[{string.Join(", ", r.Missing)}]");
        Console.WriteLine("  stageMs " + string.Join(" ", r.DurationsMs.Select(p => p.Key + "=" + Number(p.Value))));
    }
    Console.WriteLine($"sampled={reports.Count} joined={reports.Count(r => r.Joined)} incomplete={reports.Count(r => r.Missing.Count != 0)} captureLoss={loss}");
    Console.WriteLine("Residual includes socket writes, transit and client decode/scheduling after server write-start; it is not one-way network latency. Renderer submission does not measure physical display illumination.");
    return loss || reports.Count == 0 || reports.Any(r => r.Missing.Count != 0) ? 1 : 0;
}
catch (Exception error) { Console.Error.WriteLine("Trace analysis failed: " + error.Message); return 2; }

static string Number(double? value) => value is { } number ? number.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "unmeasured";
