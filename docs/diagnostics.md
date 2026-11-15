# Player-test diagnostics

**Observed** (source; graphical player validation and collection-overhead measurements pending): client and server install a stable
`DiagnosticSession` over existing console output and `LatencyTrace`. Collection defaults on. Manual HTTP uploads require a configured
private HTTPS collector; operator collection below transfers directly over the authenticated game connection. Existing `log.txt` and
opt-in `MEITOU_LATENCY_TRACE` files retain their independent behavior.

## Runtime controls

- Game feedback form: **DIAGNOSTICS: ON/OFF** changes collection immediately; **COLLECT REPORT** saves a private incident without
  creating a public issue. **Send** also starts diagnostic collection and adds its report ID to the existing public feedback text.
  One incident is retained per open form; reopening starts another. Optional screenshot follows the existing screenshot checkbox.
- Launcher Coop page: **Diagnostics** switches all clients sharing its diagnostic directory within one second, exports a selected ZIP,
  or submits/retries it. Pending, acknowledged and previous-run recovery packages are listed independently of the selected executable.
- Server console: `diagnostics on`, `diagnostics off`, `diagnostics status`, `diagnostics capture`, `diagnostics collect-all`.
  `capture` saves the server's own report; `collect-all` requests every currently authenticated player's package and runtime log.
  Players cannot run operator collection or change server collection settings.

The preference is `enabled.txt` (`on`/`off`) under the diagnostic root. Client default:
`LocalApplicationData/MeitouCoop/diagnostics`; `MEITOU_DIAGNOSTICS_DIRECTORY` overrides it for client and launcher.
Server root: `<DataDirectory>/diagnostics`. Disabling stops new recorder events, console tails, health samples and recovery writes;
existing evidence/export and already-requested upload retries remain available. The one-second control poll remains active.
Tracing sidecars discard unfinished correlations across switch generations; a separately enabled explicit latency trace still runs.

## Operator collection

Run `diagnostics collect-all` in the game server console. It returns immediately; per-player progress and the batch's output directory
are printed as work completes. `diagnostics status` reports whether a batch is still collecting. No Reports service, public upload URL,
additional port, player click or restart is needed to run the command once matching clients/server are deployed.

The online roster is frozen when the command starts (unauthenticated sockets and later joins are excluded). All selected clients are
asked to freeze evidence immediately; only transmission is queued, with four uploads at a time. Replaced connections are fenced by
world/player/generation. Disabled clients return `Disabled`, without reading their log or enabling recording. A local server collector
that is off can still request clients; its own package is marked `SavedCollectionDisabled` and contains only retained evidence.

Output: `<DataDirectory>/diagnostics/batches/<report-id>/`:

- `batch.json`: original roster and final status/file name for each player (`Saved`, `Disabled`, `Busy`, `Disconnected`, `Timeout`,
  `Cancelled`, or `Failed:<type>`). An interrupted process can leave the initial `Pending` index and already received files.
- `server.zip`: server evidence with the same report ID.
- `client-<player-id>-<connection-generation>.zip`: `diagnostics.json` plus `runtime.log` when available. The host supplies the actual
  current `GameLog` path (including multi-instance fallback); requests contain no path. The available file can include earlier launches.
  Runtime logs retain up to 10 MiB of source text, with the existing credential/path filtering and 1024-character line limit; unavailable
  logs are explicit in both manifests. No screenshot or save is taken remotely.

Transfers use fixed control kinds 76–79 and bounded 60 KiB chunks on the existing low-priority QUIC bulk lane. They remain tied to the
authenticated session across gameplay view changes, with one outstanding pull/client and a 256 KiB/s client pacing limit. Each package
is at most 16 MiB; SHA-256, length, batch ID and authenticated scope must agree before `.part` is renamed to `.zip`. The three-minute
per-player deadline includes preparation and waiting for an upload slot; failures do not disconnect healthy gameplay. Client buffers
expire after four idle minutes. Client capture is limited to once per thirty seconds per connection; completion marks the local backup
`.sent.zip`, while failed/unacknowledged packages remain available from the launcher.

One batch runs at a time. Server batch storage allows eight directories and reserves against a 2 GiB budget; it refuses further work
when full instead of silently deleting evidence. Archive/remove old batch folders before collecting more. Batch files stay local to the
game server and are not automatically sent to the separate HTTPS collector.

**Verified** (`OperatorDiagnosticBatchCollectsOnlineClientPackagesAndRuntimeLogs`, local native QUIC): multi-chunk logs from two clients,
a third disabled client, shared report IDs, private per-player scopes, duplicate-batch rejection and gameplay view resynchronization.
**Verified** (`DiagnosticTransferTests`): log filtering beyond the short console tail, invalid size/offset/digest/scope rejection,
partial-file cleanup, timeout and disconnect classification. Real player-network overhead remains unmeasured.

## Evidence and bounds

Each ZIP contains `diagnostics.json` (schema 1) and an optional PNG. The manifest includes report/artifact/run IDs, role/build/platform,
UTC capture time, monotonic clock frequency, opaque world/connection/generation scope, trace events, private console tail, process health,
and a server-capture outcome. Manually entered client incidents also include the description and load-order names. No game assets are copied.
Public screenshots/saves/raw-log attachments still follow the existing [feedback](feedback.md) options; they are not the private package.

- Trace: newest 8192 events, pruning events older than three minutes on emission; eviction and failed-emission counts are explicit.
- Console: last 512 lines, 1024 characters each; credential-like lines are omitted, absolute paths/URLs scrubbed. This is a best-effort
  private log summary, not a guarantee arbitrary mod text contains no personal information. Server console tail/process health are
  process-wide; server trace/network samples are scoped to the requesting connection and never returned to players.
- Health: 180 one-second samples: cumulative CPU/GC counts, working set, managed heap, interval frame maximum/count, server owner/tick
  maxima, queue depth/debt and received/presented tick. Network history: 2048 samples, at most once/second/connection, with native RTT,
  cumulative UDP payload bytes (excluding IP headers) and queue pressure. GPU timings and GC pause durations are not collected yet.
- Explicit outbox: 32 pending packages and a 128 MiB budget (new captures reserve 16 MiB); pending evidence is never silently evicted.
  At most eight acknowledged ZIPs and eight recovery ZIPs remain. Each ZIP is capped at 16 MiB.
- Recovery starts on the first enabled one-second sample, then every 15 enabled seconds. It is atomically replaced per run; a hard crash
  may lose the last interval. Recovery is local until explicitly selected for submission and is not automatically paired with a server.

`DiagnosticCapture`/`DiagnosticReply` use wire kinds 74/75 with fixed 16/49-byte payloads. The existing authenticated control stream and
epoch checks apply; scope comes from the server's connection, never the request. Server capture is limited to once per 30 seconds and
32 successful report IDs per connection, with two process-wide storage slots. Repeated successful IDs return the existing acknowledgement.
Client waits at most five seconds for matching evidence; disabled/busy/failed/disconnected/timeout are recorded distinctly. `Saved`
means durable on the server, not necessarily uploaded. Build identity changed; deploy matching client/server packages together.

## Private collector and delivery

Deploy the updated `services/Meitou.Reports`; no live service is changed by building the code. Configure `REPORTS_ADMIN_TOKEN` and
persist `DIAGNOSTICS_DIR` (default `/data/diagnostics`, separate from public `/data/feedback`). Set `MEITOU_DIAGNOSTICS_URL=https://<collector>`
on the game/launcher and server, or put the same URL in each diagnostic root's `collector.txt`. Launcher submission saves its entered URL
for subsequent client reports. Environment takes precedence; clients never select the server's upload destination.

Explicit game reports upload after local save. Server retries its requested reports every 30 seconds; client failures remain for launcher
retry. Recovery submission preserves the original bytes and artifact ID. Acknowledged packages are renamed `.sent.zip` locally.

| Route | Access / contract |
|---|---|
| `PUT /diagnostics/{report}/{artifact}` | Rate-limited deposit; 16 MiB cap, required `X-Content-SHA256`. Identical retry: 204; changed content: 409. No admin configured: 503. |
| `GET /diagnostics/{report}` | Admin bearer only; lists artifact basenames. |
| `GET /diagnostics/{report}/{artifact}` | Admin bearer only; downloads ZIP with `Cache-Control: no-store`. |
| `DELETE /diagnostics/{report}` | Admin bearer only; removes this incident's artifacts. |

The collector does not extract uploads. Artifacts are untrusted submitted evidence, not authenticated server attestations. Upload admission
is serialized, disk use capped at 1 GiB/2048 artifacts, and uploads older than seven days are removed on the next new upload.
These private routes are separate from public feedback links and GitHub attachments.

Analyze downloaded client/server ZIPs together with the existing tool:
`dotnet run --project tools/Meitou.LatencyTrace -- client.zip server.zip`.
It joins by report/world/connection/generation/request and uses only same-process durations. Bounded snapshots remain incomplete windows;
the tool reports missing stages and exits 1 for partial evidence, rather than fabricating an end-to-end measurement.

**Verified** (`DiagnosticCaptureTests`, local .NET 11): runtime recorder gate, bounded/scoped histories, redaction, ZIP round-trip,
failed upload retention, identical retry bytes, recovery promotion, malformed protocol rejection and immutable collector conflicts.
**Verified** (`DiagnosticCapturePairsAuthenticatedConnectionsAndHonorsServerSwitch`, local native QUIC): two authenticated players share
an incident ID but keep different connection scopes, duplicate capture is idempotent, and remote requests cannot enable server collection.
