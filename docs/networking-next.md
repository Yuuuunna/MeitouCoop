# Network vNext contract and acceptance

**Observed (working implementation; acceptance pending):** protocol 21 is connected to the game session,
authority state and renderer. This is not an accepted release; protocol 20 measurements are the historical
comparison baseline, not a runtime fallback.
Existing [authority](multiplayer.md), [persistence](server.md) and content compatibility contracts remain.
Measured baseline and its limits: [networking](networking.md#local-latency-acceptance).

## Measurement contract

Every sampled intent has `(world, opaque connection subject, connection generation, request)`; each controlled entity adds its
stable entity ID. Publications, path-service incarnation/request and rendered frame have separate IDs.
Trace process/run IDs disambiguate restarts. Emit opaque IDs and numeric outcomes, not credentials,
names, inventories, position dumps or input text. Explicit trace files are opt-in; bounded player-test incident capture uses the
[runtime diagnostic switches](diagnostics.md) and defaults on. Both keep IO off the owner thread and never change command ordering,
RNG, hashes, persistence, scheduling or admission.

| Stage | Required evidence |
|---|---|
| Client intent | monotonic submission time, request, selected entity IDs, initial received tick; discrete/held input |
| Client transport | enqueue and write completion; receipt time, status and server tick |
| Server input | complete frame received, dispatch, authority queue/execute/receipt completion |
| Movement | accepted order/entity, supersession; path queued/start/end/apply and outcome; navigation wait/retry |
| Authority result | movement-system acceptance (not merely Evaluated receipt), order/entity revision, first root position change caused by that stationary-start order; no-path/already-at-target/rejected/superseded are separate outcomes |
| Publication | capture start/end, tick, publication, encode start/end, bytes by component, queue wait/write end; ACK/backpressure |
| Client result | publication received/applied, first authoritative position change, received tick |
| Presentation | first renderer root-position change derived from that authority revision/publication, frame and presentation tick; idle animation/rotation do not count; not physical screen illumination |

Use monotonic durations within each process. Never subtract client and server UTC timestamps to claim
one-way latency. Client intent-to-pose is a client-local duration; server stages use server-local
durations. Join the two traces by IDs and ticks, reporting unmeasured transit/scheduling as a residual,
not fabricated precision. Also report received-to-presented tick lag and server tick progress per real second.
Record trace drops, missing stages and canceled/censored requests; do not exclude them from counts.
Existing `receiptMs` ends before path completion; receive gaps end after replica application.

Baseline procedure: installed-content save with ~124 visible characters; stationary-start movement,
single clicks and held steering separately; 1 then 2 clients, 30 s warm-up and 120 s capture, three runs.
Capture first-entry/cold navigation separately. Preserve publication-size distributions and initial/delta
breakdown; do not extrapolate synthetic component proportions to the VPS. Compare tracing on/off overhead.
The latency chain is closed only when every sampled request has a terminal classification and the
accepted stationary-start cases join through authority movement and renderer submission.
Transport events distinguish application enqueue, native buffer ownership release and peer receipt;
native send completion is not proof of wire transmission. Trace each command's supersession explicitly.

**Observed (measurement implementation; installed-content acceptance pending):** opt in with
`MEITOU_LATENCY_TRACE=<new .jsonl file or directory>`; both hosts may set the same GUID
`MEITOU_LATENCY_CASE_ID` to group an experiment. Protocol 20 uses opaque `Player.Value` as connection
subject because its generation counter is per player. Run IDs fence process restarts; multiple matching
server runs are ambiguous, never resolved by UTC. Numeric endpoint tags separate client/authority
terminal events in same-process captures. `World.ObserveLatency(sink)` attaches the simulation
sidecar. The bounded writer's final summary counts attempts/writes/drops; a missing summary is incomplete.
Path events are limited to sampled movement requests, with a bounded sidecar installed before path enqueue;
`PathBound` maps the returned ID and is not the path-service queue timestamp.
Analyze with `dotnet run --project tools/Meitou.LatencyTrace -- server.jsonl client.jsonl`.
Publication containment is recorded by the server against captured entity root positions, and the renderer
callback names its actual source view. Queued/nonstationary/unattributable movement remains deferred or
censored. Authority movement superseded before a client pose is `MovedBeforePresentationSuperseded`,
not a successful pose join. Writer queue/start/completion are separate; residual subtracts only the
server frame-receipt-to-write-start and client apply-to-pose durations, leaving socket writes, transit
and decode/scheduling unmeasured. GPU draw-list/installed-content runs, held-input labeling, component-byte accounting and
tracing overhead still require acceptance; synthetic callback tests do not prove display illumination.

### Held-movement diagnosis

**Observed** (`InstalledMovementTests`, real owned role from a copied checkpoint, native loopback): sustained held steering can
starve under FIFO append-and-skip path scheduling even when receipts remain fast. One captured click waited 691 ms for a
0.097 ms query; held-fixed steering produced no authority displacement over eight seconds, with no completed sampled paths.
This reproduces without a remote link bottleneck: RTT <1 ms and roughly 340 kbit/s bidirectional UDP application payload.
Current scheduling is specified in [simulation](simulation.md#threading).

**Observed** (same checkpoint scenario, Release reproduction): single, held-fixed and held-changing phases all move the original
role roughly 100 units. Their first path queue waits were 0.013/0.010/0.010 ms; 50 ms sampling first observed authority motion at
62/123/122 ms and replica motion at 122/185/122 ms. These are individual loopback observations, not percentiles, exact
input-to-render measurements, controlled Debug/Release speedups or VPS/wire-throughput acceptance.

The opt-in test uses `MEITOU_MOVEMENT_SERVER_DATA`, optional `MEITOU_MOVEMENT_SERVER_CONFIG` and a fresh
`MEITOU_MOVEMENT_OUTPUT`; it requires the existing native QUIC fixture variables. It copies data, rotates only copied credentials,
uses an ephemeral loopback port, preserves the original role's gameplay state and writes bounded stages plus sampled motion/UDP
payload counters. It never opens a renderer or substitutes a healthy recruited actor. Arbitrary saves may contain blocked or
incapacitated roles; a failed diagnostic needs its state report, not an assumed scheduling regression.

## Protocol and transport boundaries

**Verified** (`BountyCountdownTests`): bounty countdowns preserve positive infinity for permanent bounties through publication,
decoding and clock projection; NaN and negative infinity remain invalid. The binary field layout is unchanged, but both endpoints
need the corrected validation; the [current build identity](networking.md) rejects older peers during admission.
**Observed** (dedicated console stack at `EncodeBounties`, checkpoint `c41eaa9a`, 2026-11-14):
a player's 13000 bounty produced the permanent sentinel that the previous finite-only encoder rejected, closing the peer on publication
and again on reconnect. The rule is [bounty lifetime](game/economy.md); this is not a QUIC transport failure.

Application protocol 21 / ALPN `meitou/21` replaces game TCP; the TCP listener serves public discovery only.
Transport authentication completes before any state or operation. Keep world, connection generation,
grant revision and recipient filtering at the authority boundary; transport ordering is not authorization.
No state-mutating 0-RTT commands. Reconnect fences old epochs and preserves durable operation IDs.

| Lane | Semantics and bound |
|---|---|
| Control | independent reliable stream; authenticated requests, receipts and admission; max application frame 16 KiB; no bulk state; max queued 256 KiB/64 operations |
| Entity lifecycle | independent reliable stream; spawn/despawn, interest generation, catalog/schema references and grant withdrawal; max frame 64 KiB, queue 256 KiB |
| State | authenticated unreliable sequenced datagrams; latest absolute entity/component state wins; no retransmit of obsolete state; application datagram <= min(1150 bytes, negotiated transport limit) |
| Bulk | independent reliable streams for bounded initialization/resync; <=64 KiB chunks, <=32 MiB transfer, one active transfer per peer; <=256 KiB outstanding application send bytes |

Reliable lanes prefix each frame with a little-endian u32 body length, included in the lane's frame
limit. Empty/oversized frames and truncated stream termination reject. Native receive chunks are not
frame boundaries; `ReliableLaneFramer` buffers at most one bounded frame and dispatches on the owner.
Framing does not replace typed body validation, authentication or epoch checks.

Application queues are bounded before native submission. Pending state is keyed by entity/component and
coalesced before encoding; expired state is dropped (100 ms send deadline), never replayed as a backlog.
Reliable operations are rejected as Busy before acceptance when capacity is exhausted, never silently lost.
Bulk work cannot hold a lock needed by control dispatch, ACK processing or simulation. Independent streams
still share congestion capacity: native QUIC congestion control, outstanding-byte bounds and reserved control scheduling are required.
State queues have at most 4096 entity/component keys and 256 KiB; exceeding either drops obsolete unsent
state and reduces subscription/cadence explicitly. Receive caps mirror frame/transfer limits before allocation.
Lifecycle overflow withdraws the view and closes the slow connection; despawn/revocation cannot be dropped.
Application ACKs release bulk outstanding-byte credit: native SEND_COMPLETE releases buffer ownership only,
never this credit. Bound native-submitted datagrams separately and release their buffers only on native
terminal send events. Transport callbacks copy/enqueue bounded data; never run decoding or simulation.

**Observed** ([MsQuic API](https://github.com/microsoft/msquic/blob/main/docs/API.md)):
MsQuic provides secure unreliable datagrams and independent reliable streams. Native MsQuic v2 API is
the candidate adapter, not a handwritten reliability/cryptography stack. Pin the exact native package,
ABI, TLS provider, license and reproducible deployment after the platform probe passes.
Probe candidate: `Microsoft.Native.Quic.MsQuic.OpenSSL` 2.6.3, win-x64, native API v2;
record package/DLL hashes before the probe and deploy the identical artifact if accepted.
**Observed** (2026-11-09, user-returned native probe JSON, Server 2016 build 14393/x64): the pinned
OpenSSL DLL passed two loopback connections with TLS 1.3, validated certificate, bidirectional stream
and 32/1150/negotiated-limit DATAGRAM ACKs, shutdown/reconnect, zero remaining handles/buffers/drops.
Package SHA256 `41505c3861ef386c8788dd6d9e0ffea75b275b7d0651c2b940628ab729cf27bd`;
DLL SHA256 `290b6c8716ac09eea043913f86164e0b2f5096c666c9f1ca81c0c5c1864e1cb0` matched the local probe.
The default Schannel path requires newer Windows.
**Verified** (2026-11-09, same pinned probe, local client `vps-public-client.json`): two real public-UDP
connections to that VPS completed validated TLS 1.3, bidirectional streams and three DATAGRAM sizes
(including 1150-byte application payload), application ACKs and clean reconnect/shutdown. Transport RTT
was 28.8–29.8 ms; no remaining native handles/buffers or receive-queue drops. This small capability probe
does not measure sustained gameplay. **Unknown:** impaired-link behavior on that public path.
Sources: [platforms](https://github.com/microsoft/msquic/blob/main/docs/Platforms.md),
[.NET QUIC](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview).
No silent TCP emulation of the state lane; failed platform/UDP capability blocks cutover explicitly.

**Verified** (local native adapter integration tests): the [pinned adapter](../tools/Meitou.QuicTransport/README.md)
accepts two independent peers, exchanges all lanes after listener disposal, releases pending native
resources, rejects hostname/CA failures with TLS alerts, and fails closed on event-queue overflow.
This is separate from the Server 2016 platform probe: deployment of the actual shim there, IPv6 listening,
sustained public-path/impairment tests remain open. Application authentication and game integration use this adapter.

## Replication model

Network DTOs are independent of `WorldSnapshot`/`CharacterSnapshot`; those remain local presentation
contracts. All wire lengths/counts, floats, IDs, flags and revisions are validated before allocation/apply.
Little-endian fixed-width binary for high-frequency state; no JSON tree/base64/full-character replacement
in the state lane. Unknown versions/kinds/required fields reject; no permissive fallback to full snapshots.

Datagram header, in order: magic `MTN2` (4 bytes), version u16, kind u8, flags u8 (zero), session epoch u64,
view epoch u64, packet sequence u64, server tick u64, authority publication u64, body length u16 (50 bytes total). Sequences never wrap within an epoch.
Initial state kind 1 has u16 record count, then length-prefixed entity motion records:
entity u64, interest generation u32, component revision u32, position XYZ f32, velocity XYZ f32, yaw f32,
movement flags u16, draw yaw f32 (50 bytes, excluding u16 record length). Draw yaw NaN means navigation
yaw fallback; infinity rejects. Equal revisions require identical IEEE bits, including NaN payloads.
Finite world coordinates only; no quantization
until its error budget is verified against navigation, combat and rendering. Records must fit one datagram.
Motion flag bits are Moving=0, Swimming=1, Carried=2, Ragdoll=3, Discontinuity=4; others reject.
Epochs, entity IDs, interest generations, component revisions and packet sequences are nonzero.
Component revision exhaustion requires a higher interest generation; generation exhaustion requires a
new view; view/packet-sequence exhaustion requires a new authenticated session. Counters never wrap.
Use independent entity revisions: loss of one packet must not require a missing global delta baseline.
Unknown entities/interest generations are dropped, not buffered without bounds. Despawn/revocation fences
late datagrams. Publication/sequence numbers are not a substitute for stable entity identity.

Components: motion at up to 20 Hz for the actively controlled/interactive moving entities; other
authorized entities use relevance/change-based scheduling within the shared per-peer budget, not
the same full-state cadence for every visible character. Absolute refresh at least once/second
when idle. Stop/teleport/carry/ragdoll transitions additionally send a reliable absolute motion checkpoint
with the same entity revision, so losing the final datagram cannot strand a state until another movement.
Vitals/equipment/appearance/subscription changes and locomotion/action transitions are reliable lifecycle
records, retained until acknowledged; losing the sole final change must converge without new gameplay input.
Locomotion/action state has explicit state
epoch, start tick and playback parameters; vitals/equipment/faction on change; inventory/details only for
authorized subscriptions; appearance/content references at creation/change. Action events are identified
and deduplicated. Static records/assets are resolved locally against the compatibility manifest.
Visual weather reconstruction may move client-side only after separating it from authoritative hazard,
wind and gameplay queries; no gameplay weather is approximated to save bandwidth.

All lanes carry the session/view epoch. `ViewBegin` on control fences and clears older authorized domain
state, presentation history and staged transfers immediately. A baseline has an ID, capture tick and
lifecycle sequence; bulk validates into staging, never directly into the active replica. `ViewCommit`
names that baseline and a lifecycle watermark. Client applies ordered lifecycle changes through that
watermark to staging before Ready ACK; only then may server grant Active. Drop pre-Active datagrams;
subsequent absolute updates recover motion. A newer view epoch invalidates any incomplete older commit.
Bounded lifecycle history overflow abandons the baseline, never activates an incomplete view.
Despawn/re-entry changes entity interest generation; late old-generation state cannot recreate an entity.
Reliable checkpoints and datagrams use the same component revision comparison; baseline completion
cannot overwrite a newer applied revision. Privacy fences are checked again before sending staged output.

Client keeps latest authorized domain state separately from its timestamped presentation history.
Render consumes a monotonic server-time estimate; animation state epochs/start ticks are independent of
packet arrival. A repeated tick refreshes authorized data without restarting an animation. Define loss,
clock correction, teleport, carry, ragdoll and AOI-entry discontinuities explicitly; no enlarged blend-window
heuristic is accepted as a replacement for this timeline. No client-authoritative movement/combat.

**Verified** (local animation timeline tests against current `AnimationSystem.Publish`, not new original-game
evidence): bounded binary playback segments carry raw weights, explicit blend area/wanted/full-body
metadata, signed playback rate, loop/clamp mode, phase and start/expiry ticks. Sampling reproduces current
upper/lower/overlay normalization. Future transitions wait for their start tick; duplicate epochs do not
restart playback. At most 32 future transitions and 16 layers/segment are retained; overflow requests resync,
expiry holds the prescribed endpoint, and withdrawal clears playback. Segments span at most 60 simulation
seconds. Authority export must supply exact parameters and renew on changes; root motion and action/audio
events remain separate. The actual renderer consumes these samples; comprehensive combat/carry/ragdoll
graphical parity and impaired-link acceptance remain open.

## Acceptance gates (requirements, not measured achievements)

**Observed** (2026-11-11, installed pre-cutover checkpoint copy without application rate limiting,
production async paths/8 threads, 123 authorized entities, native loopback;
`slimmed-measure-bfa26fea/report.json`): after binary encoding for `Build` (21 B), `GameHours`-anchored
countdown projection for `Prison`/`Bounties`, RLE/varint `CollectionManifest` encoding, UI-precision
delta deadbands (`Body`, `Hud`, `HealthParts`, `ItemGrid`) against last-sent state, and bounded network
animation tolerances, 120 s conservative IPv4 upper-bound means met the <=500 kbit/s target at
362.11 kbit/s (raw UDP 335.25; 10 s windows 312.28–432.38) for one peer and 325.77/332.23 kbit/s
(raw UDP 300.78/311.74; 10 s windows 292.60–360.73) for two. Received ticks progressed at 30.00/30.00
per second. All 33 moves were retained: receipt P95 43.32 ms, acceptance-to-motion P95 57.86 ms,
client-authoritative-root P95 113.06 ms; zero sampled trace loss. Live account binding measured
207.67 ms worst concurrent command receipt.

**Observed** (actual Vulkan client, 2026-11-10, `quiet-20261110-210138/gui-acceptance-summary.json`):
100 s at 117 FPS with 123 authorized entities; all 12 movement samples joined through actual renderer
submission with zero trace loss. Receipt P95 was 57.52 ms, authority-motion P95 62.38 ms and
intent-to-render-submit P95 135.97 ms. No PNG capture ran during this timing test. The separate visual
run retains its screenshot-induced outliers; these results do not establish physical display latency,
VPS behavior or full combat/carry/ragdoll graphical parity. Unaffected bandwidth/save results were not
rerun after the loading-clock-only correction.

| Gate | Pass condition |
|---|---|
| Measurement | all sampled requests classified; accepted stationary-start cases correlate to first authority motion and first renderer pose; zero unexplained trace loss; tracing overhead reported |
| Correctness | reorder/duplicate/loss never rolls back entity revision or reapplies a durable operation; test final update lost then world idle, revocation then old baseline completion, newer revision then old baseline, and despawn/re-entry then old datagram |
| Animation | walking, turning, stop, combat, carry and ragdoll remain coherent through burst/duplicate delivery; deterministic pose/event checks plus graphical capture |
| Nominal profile | RTT 30 ms, jitter +/-10 ms, 25 Mbit/s shared uplink (corrected VPS specification), 0% loss; 1/2 clients, ~124 visible characters each, same installed-content scene |
| Responsiveness | warmed stationary-start move: submission-to-accepted receipt P95 <=100 ms, to first authority motion (server-local acceptance duration) P95 <=100 ms, to first renderer pose P95 <=250 ms; report cold path separately |
| Throughput | optimization target: <=500 KiB/s (512,000 bytes/s) per player steady-state mean over the 120 s capture, both directions including transport encryption/ACK/retransmission and UDP/IP overhead; report each 10 s window and peaks, including overruns; no application bytes/s limiter or gameplay delay to enforce this target; cold baseline bytes/time reported separately from the VPS global 25 Mbit/s link capacity |
| Resource budgets | no network IO/encoding on world owner; capture P95 <=5 ms; queues stay within above bounds; 30 min run without growth or missed lifecycle events |
| Impairment | repeat RTT 100/250 ms, jitter +/-30/50 ms, loss 1/3%, reorder/duplication, 5/1 Mbit/s; bounded queues, no reliability violation/resync storm; report latency and explicit reduced update rate, not nominal-budget claims |
| Persistence | load a pre-refactor real checkpoint copy; preserve account/role ownership and recover durable operations; save/restart/reload, not just same-version synthetic roundtrip |
| Deployment | paired client/server version rejection, Server 2016 native probe, clean shutdown/reconnect, no credentials/assets in package |

The current throughput target is 500 KiB/s = 512,000 bytes/s = 4.096 Mbit/s per player. Earlier captures used a stricter
500 kbit/s target (62,500 bytes/s); their recorded measurements are historical, not a current-build acceptance result.
The target is observational, not a hard cap: exceeding it never enables application rate limiting. Remove redundant representation and
coalesce obsolete state; bounded memory/native-send admission remains necessary independently of throughput.
Do not call an application-payload-only measurement the wire budget. Native UDP payload counters plus
packet/header accounting (or a packet trace) must include both directions and retransmissions.
Count traffic at one endpoint, not twice across client and server. The global 25 Mbit/s gate measures
server egress; the per-player gate measures both directions over complete 120 s counter epochs with
10 s window boundaries, actual UDP datagram counts and IP-header byte totals; counter resets or missing
windows cannot pass. Throughput gates never substitute for gameplay quality.
**Observed** (pinned MsQuic 2.6.3 headers): `SendTotalPackets`/`RecvTotalPackets` count QUIC packets,
not UDP datagrams; packet coalescing prevents using these as exact UDP/IP overhead measurements.
The 50-byte motion-record prototype costs 124,000 bytes/s if all 124 characters are sent at 20 Hz, before framing;
that exceeded the earlier 500 kbit/s target. It remains a correctness reference, not a current-build wire measurement. Compact representations with verified error bounds, change detection, relevance tiers and
local animation playback optimize toward the target; overruns are measured and reported. No constant-changing global `view` component
may embed static scene/UI/faction collections; clocks and publication IDs cannot dirty those components.

## Inventory and trade budget

**Verified** (2026-11-16, Linux .NET 11 scratch diagnostics using production `WorldViewCodec`, `GameStatePublisher`/`CaptureCritical`,
`GameStateReplica`, publication packing, bulk/control codecs and `ReliableLaneFramer`; no Kenshi or network): 20 scenarios cover
inventory/trade grids of 100 or 256 items and combined 256-item views. Each scenario applies ten quantity changes over one virtual second
at 10 Hz; only item 0 changes from 5 to 6 or back in each grid, while its inventory revision advances. Distinct item tags have
0, 32 or 4096 characters; the largest case also repeats a 128-character stolen-owner label. Record IDs and names are constant.
Every baseline and changed publication round-trips through the replica.

Inventory-only has two character grids; trade-only has stock and customer goods. Combined includes both character grids, trade stock and
customer goods, plus `GoodsOf` for both controlled characters, with matching item/tag identities and goods revisions. Each listed grid
contains 256 items. Tag identity and command validation follow [item actions and shared stock](networking.md#item-actions-and-shared-stock).

| Workload | Raw tag / owner characters | Downstream bytes/s | Upstream ACK bytes/s | Combined bytes/s | Combined KiB/s |
|---|---:|---:|---:|---:|---:|
| Inventory only | 0 / 0 | 43,780 | 1,700 | 45,480 | 44.41 |
| Inventory only | 4096 / 128 | 152,555 | 3,800 | 156,355 | 152.69 |
| Trade only | 0 / 0 | 38,119 | 1,700 | 39,819 | 38.89 |
| Trade only | 4096 / 128 | 149,151 | 3,800 | 152,951 | 149.37 |
| Combined | 0 / 0 | 77,954 | 2,400 | 80,354 | 78.47 |
| Combined | 32 / 0 | 354,960 | 7,300 | 362,260 | 353.77 |
| Combined | 4096 / 0 | 355,581 | 7,300 | 362,881 | 354.38 |
| Combined | 4096 / 128 | 358,593 | 7,300 | 365,893 | 357.32 |

Bytes include publication and reliable-lane framing, bulk chunks/manifest, `ViewCommit`, `ViewActive`, a representative active admission
for two controlled roles, and critical clock frames. Reverse traffic assumes one cumulative ACK per chunk, a manifest ACK and `ViewReady`;
ACK coalescing can reduce it. Admission size varies with authorized roles and qualifications. The largest combined baseline costs
43,348 downstream + 870 reverse bytes; the steady-state table excludes that one-time baseline. Unchanged inventory/trade fields add
zero component bytes in all 20 scenarios; clock-only traffic is 1,790 bytes/s.

The largest measured combined workload uses 71.46% of the target, leaving 146,107 bytes/s (142.68 KiB/s) for other traffic.
This is offered application traffic, not a sustainable-rate or whole-game acceptance result: it excludes QUIC/TLS/UDP/IP/link overhead,
retransmission, motion, animation, unrelated components/events, authentication and other control traffic. It must not be added to an
unrelated historical benchmark and presented as an installed-scene total. Real two-way traffic still needs the 120-second capture above.
The same synthetic workload with a 64-hex-character tag digest and plain JSON measured 10,448,260 bytes/s; that is a representation
comparison of this adaptation, not a measurement of a prior released binary.

**Verified** (same scratch diagnostics): an inventory command costs 402 framed control bytes without a tag and 434 with a tag, for both
32- and 4096-character source tags. Full tags stay at the authority; inventory tokens match the current authoritative tag, reject a different
tag and reject simultaneous raw-tag/digest input. Trade and player-trade tokens also preserve the authoritative digest without exposing the
raw tag. The three new authority rules add 45 baseline component bytes, zero when unchanged; changing all three costs 1,872 framed downstream
application bytes. `SoundEvent.Armour` adds 11 bytes to one ordinary JSON event;
one `FedToMachine` critical publication costs 519 bytes including framing.

Plain and compressed JSON round-trips pass. Diagnostics reject ten malformed envelopes/streams, including wrong magic/version/size,
truncation, trailing data and concatenated streams. Two approximately 18 MB expanded fields reject the aggregate 32 MiB limit despite only
8,715 wire bytes; a 300 KB deferred field rejects the 256 KiB bound despite 302 wire bytes. Oversized capture also rejects, and replica failure clears
its visible view. These are codec/authority checks, not transport, graphical or real-save acceptance.

## Upstream presentation budget

The following measurements describe the prior `704cc1ec` presentation integration. They do not include the current inventory/trade workload.

**Verified** (synthetic serialization, Linux .NET 11; no Kenshi or network): 100 captures over 10 seconds of virtual time at 10 Hz,
124 visible characters, 2 UI actors, 4 meters and 34 mod skills per active actor, a 32-byte inspected-power ID, and one static attachment per
visible character. Typical value/arrows/tooltip lengths are 4/1/96 UTF-8 bytes; maximum strings use their 256/64/4096-byte limits.
All dynamic values change each capture. The authority retains exact values; display equivalence compares against the last transmitted value.

| Scenario | New components + records, bytes/s | Game frames, bytes/s | Reliable bulk with framing, bytes/s |
|---|---:|---:|---:|
| No mod display data; unchanged new fields | 0 | 1,750 | 2,350 |
| Typical values; static tooltip | 6,560 (6.41 KiB/s) | 8,310 | 8,910 |
| Typical values; tooltip changes each capture | 14,880 (14.53 KiB/s) | 16,630 | 17,230 |
| Maximum strings; static tooltip | 31,760 (31.02 KiB/s) | 33,510 | 34,110 |
| Maximum strings; tooltip changes each capture, paced at 2 Hz | 75,024 (73.27 KiB/s) | 77,990 | 79,550 |

The actual `GameStatePublisher`, `GameViewProtocol.Pack`, `BulkChunkCodec` and `ReliableLaneFramer` produce these lengths.
Bulk accounting includes 4 bytes per packed game frame, 52 bytes per chunk and 4 bytes per reliable frame. It excludes control/ACK messages,
other changing game state, QUIC/UDP/IP and retransmissions. Unchanged new fields send zero bytes after baseline.

**Observed** (source): dynamic bars/value/arrows (2097) and the complete tooltip table (2100) are independently revisioned absolute components;
a SHA256 digest joins them without stale text under reordering. Static text is not retransmitted with changing bars. When a changing complete
tooltip table exceeds 1024 bytes, the whole meter presentation pair coalesces to a 500 ms interval, using monotonic time independent of game
pause or time scale. Until due it retains the last sent pair; then it publishes only the latest capture. Baselines, new entities, scope/slot
withdrawal, slot-count changes and explicit tooltip removal bypass that delay. Short changing tooltips and bars with static tooltips retain
their normal cadence. Before pacing, maximum changing text measured 360,080 component bytes/s and 367,910 game-frame bytes/s.

**Verified** (actual motion producer/scheduler and codecs, synthetic immediately completed sends, 124 moving characters): near 5 Hz costs
33,800 application datagram bytes/s; 2 interactive at 20 Hz plus 122 near cost 36,140; all interactive at 20 Hz cost 135,200.
The heaviest UI bulk above plus all-interactive motion totals **214,750 bytes/s (209.72 KiB/s)**, leaving **297,250 bytes/s (290.28 KiB/s)**
of the 512,000-byte budget for other state/control and transport overhead. This is measured application headroom, not whole-game or VPS
acceptance. Actual combined directions, retransmissions and cold entry still require the 120-second installed-content capture above.
`BandwidthScheduler` bounds/coalesces queues and outstanding sends; it is not a hard bytes/s limiter. Frequent attachment changes and
immediate visibility transitions can add bursts beyond the measured scenarios.

A component record adds 19 bytes. Nonempty bar payload = `33 + sum(1 + present*(16 + valueBytes + arrowBytes))`; an empty list costs 1 byte.
Tooltip-table payload = `1 + sum(1 + present*(2 + tooltipBytes))`; a null tooltip has a two-byte marker and no text. Skills = `2 + 4N`.
Power = 1 byte absent or `11 + instanceBytes` present. Empty new fields add `81 * visibleCharacters + 20` baseline bytes (10,064 for 124),
then zero unchanged. The maximum-string scenario's initial baseline is 55,230 new-component bytes, 354,478 game-frame bytes and
359,698 reliable-bulk bytes. Cold baselines are reported separately from steady state.

A bone-mesh declaration costs `34 + keyBytes + meshPathBytes + boneNameBytes`, plus one count byte per list. Up to 64 declarations per character
send once/on change, never per animation frame; they are not a pose stream. Wire and mod setters share bounds: 4 meters, 34 skills, the text caps
above and attachment key/path/bone caps 128/1024/256 UTF-8 bytes. Malformed lengths, duplicate keys, invalid transforms and nonfinite new values
are rejected, except the intentional mod-skill NaN sentinel. Whole-view JSON wraps hidden meter slots without relaxing generic null-array
rejection. Existing content agreement remains mandatory.

**Verified** (scratch diagnostics, no new repository tests): 47 ordering/lifecycle/pacing checks pass, including both component arrival orders,
late baselines, mandatory field coverage, generation changes, immediate withdrawals, 499/500 ms and 1024/1025-byte boundaries. All five full
baseline round trips and hidden-meter/NaN-skill JSON and binary round trips pass. These are codec checks, not graphical or transport acceptance.

Timers/performance assertions are Bench; game/GPU/long-run cases are Slow. Normal tests cover deterministic
packet schedules, correlation and queue invariants without wall-clock performance thresholds.
Replace old protocol-specific tests together with the production cutover; retain/rebind authority,
ownership, creation, persistence and gameplay behavior tests. Remove protocol-20 writer/decoder,
snapshot transport and superseded smoothing tests once replacement coverage and gates pass.
Do not ship dual production paths or claim completion from codec-only tests; every gate above, including
responsiveness, throughput and animation, blocks production cutover. Remaining gates stay explicitly open.

| Existing test group | Cutover replacement |
|---|---|
| `NetworkingCompatibilityTests` / `NetworkCommandTests` wire-specific cases | v21 framing/schema tests; retain authentication ordering, issuer validation and operation deduplication behavior |
| `WorldViewCodecTests` / `StateReplicationTests` JSON/patch cases | component codecs and revision/fence reducer; retain recipient isolation, withdrawal, appearance changes and destruction-vs-out-of-scope semantics |
| `StateTransportTests` chunk/base-ACK cases | bounded bulk commit, epoch fencing, final-state recovery and slow-peer scheduling tests |
| `SecureServerTests.*` production scenarios | migrate to the actual v21 client/server adapter; keep creation, ownership, inventory, events, reconnect and persistence assertions |

**Verified** (local `NetworkingNext` deterministic tests): motion codec/MTU validation and
`GameStateClientReplica` reject conflicts atomically, fence withdrawn/re-entered views and preserve newer
revisions across baseline transitions. These tests are not production transport, rendering or VPS acceptance.

**Verified** (local `RecipientComponentCaptureTests`): the migration capture boundary exhaustively maps
view, world and character fields. Tick/publication/root-motion changes leave static field deltas
empty; optional-field withdrawal and recipient/generation separation are explicit. It consumes already
filtered views and serializes detached individual fields for comparison; it is not the final wire schema,
an authorization filter, or an acknowledged delivery ledger. **Observed** (synthetic 124-character fixture):
advancing raw animation layer time changes 9,052 field-value bytes per step (~181 KB/s at 20 Hz before
framing). Raw animation snapshots cannot become the new lifecycle payload; authority-exported timelines
remain required.

**Verified** (local scheduler tests): pre-serialization admission, coalescing/expiry, independent lane
ownership, peer fairness, overflow and bounded native send reservations are enforced without artificial
application rate limiting. Control frames fit 16 KiB, lifecycle/bulk chunks 64 KiB, and state 1,150 bytes.
Arbitrary serializer closure storage is outside declared payload bounds; production capture must own bounded
data. Native completion releases buffer ownership only; bulk peer-ACK credit remains separately required.

**Verified** (local bulk tests): one transfer stages at most 32 MiB, in 4,096-byte chunks with a 52-byte
binary identity/offset header, plus reliable framing. Only contiguous peer ACKs release the 256 KiB
application outstanding budget. Baseline IDs increase within a view; duplicate manifests preserve progress,
conflicts reject, and empty payloads reject. Completion verifies SHA256 and returns an owned staging artifact,
not permission to activate a replica. Epoch withdrawal zeroes retained data; consumers must dispose detached
artifacts on withdrawal and still enforce the lifecycle watermark/Ready/Active transaction above.

**Observed** (existing `MotionReplicationProducerTests`, Release/Linux, 120 s/124 synthetic entities): admitted payload totals 3,970,464 bytes
(33,087.20 bytes/s), with active entity delivery gaps at most 50 ms. This fixture pumps up to eight times per 50 ms and uses the isolated
single-record checkpoint codec; it does not exercise `MotionCheckpointSender`, remaining gameplay components or the production transport.
These are application payloads, not wire-throughput or real-game latency acceptance. Motion captures carry explicit ticks, cancel unsent older
batches, and freeze coherent records before deferred encoding. Withdrawal retains bounded generation fences; reconnect requires a new producer.
Stop/carry/ragdoll/discontinuity checkpoints remain retained until peer ACK; overdue state or failed queue admission is reported explicitly.

## Game connection integration

**Observed (current client/server implementation; acceptance pending):** `RemoteGameSession` and
`SecureServerPeer` use `NativeGameConnection` for gameplay. `MTC2` control envelopes reuse typed
business DTOs without MTN1 framing. Three bounded reliable framers and sequenced state datagrams
feed separate native lanes. Control submission remains independent of bulk ownership; native completion
is buffer release, not peer receipt. Statistics expose actual encrypted UDP payload counters and QUIC
packet counts; these exclude IP overhead and cannot certify the wire-throughput gate.

`ViewBegin` clears domain/presentation state. A manifest-receipt ACK precedes bulk submission, avoiding
cross-stream ordering assumptions. SHA256-validated bulk remains staged until `ViewCommit`; client
Ready names the captured authority publication, and authority activation precedes Active. Large component
deltas use the same bounded bulk transaction. Bulk ACKs release application credit; native ownership
release separately frees queued-byte credit. Bulk submission rechecks the recipient's authority stamp.
Reliable motion checkpoints share component revisions with datagrams and receive explicit peer ACKs.

**Observed** (source): after queuing a baseline bulk, `SecureServerPeer` seeds its fresh `MotionReplicationProducer` from that same detached
publication's exact motion roots, interest generations and capture tick (`SeedReliableBaseline`). Seeding creates no redundant checkpoint
or synthetic ACK and retains the `ViewReady` gate. Later captures compare revisions/flags normally; an unseeded producer's first capture
still generates reliable checkpoints.

**Observed** (source): each motion `Pump` queues all due records in independent batches of at most 21, ordered by controlled/interactive/near/far
relevance, then last send and stable capture order. A producer reserves 196 consecutive scheduler keys. New captures, withdrawals and view
changes cancel every unsent older batch; cadence advances only on admission. There is no per-publication packet allowance or bytes/s cap.
`MotionCheckpointSender` batches consecutive reliable transitions only when their capture tick and authority publication match. Each batch's
highest ID carries the existing cumulative ACK; only successfully admitted IDs enter the ACK ledger. A full native/lifecycle queue retains
the next batch for retry and prevents later IDs overtaking it. Existing retained-memory and outstanding-send bounds remain in force.

**Observed** (source): ragdoll component 2038 is exact sparse little-endian binary: null is one zero byte; a present pose has a 9-byte header
(presence, u16 skeleton length, u16 active count, float blend weight), followed by 30 bytes per active bone (u16 index, quaternion and position).
Field 2038 declares `CompressedJson = false`; its binary bytes bypass JSON compression-envelope validation and expanded-size interpretation.
Skeletons are bounded to 1024 bones; malformed lengths, unordered/duplicate indices and non-finite transmitted values reject. Inactive transforms
have no posing effect and decode to zero. With the 19-byte component record, 16 active bones cost 508 bytes and two cost 88 bytes; these are
application component sizes, not wire measurements. The authority's per-recipient cache reuses unchanged owned poses and drops departed/null
entries; the encoder compares active values before allocating bytes and keeps an independent value baseline. No quantization or pose cadence
reduction is used. Both endpoints require the matching [engine build identity](networking.md).

Component encoding runs on a worker after owner-thread detachment and is followed by an
authority-stamp recheck. Motion carries its capture publication directly, so a component/bulk delay cannot
hold movement behind a reliable lifecycle publication. Renderer and client root traces retain the actual
motion packet sequence; reliable checkpoints reserve its high bit and use explicit peer receipt ACKs.
Large creation/invitation/talk replies use an explicit typed business manifest on the same bulk sender
as state. At most one transfer is active; pending results have eight-item/256 KiB bounds and active plus
pending source bytes cannot exceed 32 MiB. Capacity failure disconnects and faults pending callers;
durable results remain recoverable by request identity. Business bytes are strictly decoded only after
checksum completion and cannot satisfy world Ready. A new view cancels old business transfers by query
identity. Authentication/preparation admission updates clear world state without dropping an authorized
private business transfer.

**Verified** (local codec and replica tests): draw facing shares the timestamped motion history with root
position and navigation yaw. Sampling crosses the +/-pi boundary by shortest angle; delayed lifecycle
fields do not move facing ahead of the presentation tick. Reliable draw yaw is spawn-only.
`MTC2` control envelopes include session/view epochs (22-byte header). Only compatibility and
credential exchange use bootstrap `0/0`; authenticated admission establishes nonzero epochs.
Transport ACKs have a separate bounded coalescer (4,098 keys, cumulative bulk offsets), rather than
occupying the 64-operation business queue or its request-rate allowance. Private bulk credit handling
still rechecks authority before submitting additional bytes.

**Observed** (schema): world field 1055, `ContainerOpener`, belongs to the independent Interaction component. Its recipient projection and
display-time withdrawal rules are described in [networking](networking.md).
World fields 1056–1058 add `MapSquads` (Knowledge), `WorkSites` and `FoliageOutposts` (Scene); character fields 2093–2096 add
`FirstSelected` (Details), `KidnapVictim` (Vitals), `Lean` and `Anchor` (Animation). Existing IDs are unchanged. Command IDs 36, 60, 61,
79 and 166 encode item rotation, squad autonomy, squad home, machine arrangement and held rotation respectively. The engine/build
compatibility identity fences peers with older component or command schemas; protocol 21 framing is unchanged.

**Observed** (`JsonComponentCompression`, schema encode/decode): JSON fields other than Events and keyed collection entries of at least 1 KiB use independent
Brotli streams (quality 1, window 22) only when the complete envelope saves at least 128 bytes and 12.5%. The envelope is `00 4D 4A 43`,
version byte `01`, exact uncompressed length (little-endian int32), then the stream; a field's existing presence byte remains outside it.
Plain JSON and one-byte null withdrawals stay valid. No prior value or dictionary is needed, so inventory revisions, click tokens,
out-of-order publication fences and authorization withdrawals retain their existing atomic semantics. Decode rejects an invalid header,
an output beyond 32 MiB, incomplete consumption, trailing data or a mismatched output length, then applies ordinary JSON and typed validation.
Compression uses one bounded pooled scratch buffer; decompression allocates the exact declared output. The complete capture and each decoded
publication retain the 32 MiB expanded-byte limit, including framing within the publication; deferred fields reserve their expanded size against
the existing 256 KiB bound and against the publication that applies them. Schema metadata distinguishes compressed JSON from binary fields.
Raw `WorldViewCodec.EncodeComponent`, Events encoding and event-retention/critical-fragment byte accounting remain unchanged.

The receiver and renderer share a monotonic authority-clock estimate. Receive-side sampling consumes
due animation transitions while content/GPU loading has no renderer callbacks; renderer frame deltas do
not advance the clock a second time. The estimate never presents beyond received server time, keeps a
100 ms reservoir (at least three authority ticks), slews normally by at most 5%, and resets with the view.
Confirmed lag beyond 300 ms (at least nine ticks) explicitly re-anchors forward to that reservoir before
animation apply; this is a shared-clock discontinuity, not a blend window. Corrections expose numeric
counters and trace records.
Ordinary UI/component deltas use the low-priority bulk path even below 64 KiB. Events, playback,
retirement and clock updates use priority lifecycle publications; motion remains independent. One active
UI transfer prevents further full-component encoding. Lifecycle publication admission is atomic across
all framed parts; one bounded pending critical batch resumes at its exact publication index. Further
critical encoding pauses above a 16 KiB lifecycle ownership window, while latest motion capture and
recipient event retention continue. Event IDs survive source-ring expiry until their critical batch is
admitted (4,096 entries/256 KiB maximum); overflow is explicit. Clock-only ordinary UI deltas are omitted.

**Verified** (`PresentationEventValidationTests`, `PendingPresentationEventsTests`; codec and v21 critical-publication round trips):
wire validation and recipient retention share a 4,096 combined-event limit. The simulation journal's 512-entry bound applies to
each source capture. Fixed-position `Building` damage and `Label` floaters may omit a character target; all other kinds require one. Validation errors
identify the family, entry index/ID, failed field or duplicate ID, high-water, family counts and publication context.
Retention errors distinguish retained-key, projected-key and encoded-byte limits with actual usage and maxima.
Diagnostics omit names, text, content identities and positions.

Motion capture runs at 20 Hz independently of the 10 Hz animation/critical/UI schedule. Before the
first actual renderer root submission, receive samples re-anchor the presentation reservoir so initial
content loading cannot leave a late-entry client permanently behind authority. Native/TLS/credential exchange has a 15 s deadline; verified credentials receive an explicit bounded
account-binding stage (up to 3 min). Public TCP discovery (`GameTls` / `QuicDeployment.ClientOptionsAsync`)
and `DeviceConnection` do not pin or verify self-signed server certificates, while keeping TLS 1.2/1.3
discovery encryption and leaf-PEM QUIC TLS 1.3 transport encryption. Registration captures remain on the world owner; detached commit uses the existing
checkpoint coordinator on a worker, with current-world/principal confirmation only after durable success.

Stale-view requests receive a bounded correlated `NotDispatched` outcome in the current epoch; only the
matching pending query fails. This says nothing about an earlier durable attempt with the same ID.
Commands, creation and invitations are never automatically retried. Explicit latest entry/observation
intents may retry once per newer view, only after a not-dispatched response and observed `ViewBegin`;
entry still passes current grant/loading checks. Stale admission queries return fresh status. A new
state-bulk manifest clears the prior commit target, so cross-lane completion cannot acknowledge an older
publication. Source-ring events remain visible for their journal lifetime after transport admission; only
unadmitted IDs extend beyond that lifetime.

**Verified** (`GameStateReplicationTests`): after its newest root sample, a moving remote character
may use authoritative linear velocity for at most 0.250 simulation seconds. The projected endpoint
then holds. Stop, pause, carry, ragdoll and discontinuity disable this prediction; angular rates are not
inferred. Authorized domain roots remain unchanged. Per-entity histories still interpolate at their
source ticks; this presentation policy does not establish impairment or graphical acceptance.
