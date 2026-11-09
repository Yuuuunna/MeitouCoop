# Networking contracts

Engine contracts. Compatibility, command dispatch, state replication and the remote connection are implemented;
[account/TLS connection host](server.md#connection-host) uses these components. Graphical gameplay uses `IPlayerSession`; [graphical admission](#graphical-admission) uses the creation protocol.
A compatible peer is not an authenticated or active player.

`ContentManifest` hashes ordered logical keys, content kind, byte length and SHA-256. Database entries
retain load order; reordering mods or changing same-name/same-size bytes changes the digest. Logical keys
normalize separators/case and reject duplicates, absolute/traversal forms. Local paths are never encoded.
File access uses `GameFiles.Resolve`. Hosts must hash stable content before serving and supply the complete
gameplay-asset dependency set; database hashes alone do not establish collision/navigation/animation parity.
Rules digest sorts authoritative rule keys while retaining exact values. The host supplies a release/build
identity and authoritative settings; neither coverage nor honest client attestation is inferred from a hash.
`HeadlessWorld.CreateCompatibilityHello` uses its loaded database order plus explicitly supplied assets.

`CompatibilityHello` carries protocol, engine build, rules digest and content digest. Each mismatch has a
separate rejection reason. JSON is bounded to 4096 bytes and rejects missing/unknown/duplicate fields.
The handshake accepts exactly one Hello frame; malformed or unexpected frames leave it rejected.

`WireFrame` has a 12-byte little-endian header: magic MTN1, uint16 framing version (1), uint16 message kind,
uint32 payload length. Payloads are limited to 1 MiB before allocation. Reads handle fragmentation and
multiple consecutive frames; only EOF before a new header is a clean close. Partial header/payload,
unknown kind/version or invalid size is an error. Writes freeze payload bytes. The host serializes writes,
uses one reader, supplies cancellation/deadlines and closes a connection after framing/partial-write errors.
Message kinds cover compatibility/authentication/admission, commands/receipts, observation filters,
state chunks, ACK/resync, withdrawal, creation/invitation messages and talk hints. Application protocol: 20.

`ClientCommandBinding` expands selection-based intents into explicit actors and binds grants/membership
revisions from the displayed view. `PlayerCommandRouting` supplies the shared actor/faction scope; building
roots and machines publish their authority faction so the client never guesses from its active faction.
The gateway rechecks those revisions when executing. **Verified** by `ClientCommandBindingTests`,
`PlayerWorldViewTests` and real TLS commands in `SecureServerTests.RemoteClient`.

Views include the active member faction, per-faction formation/unlocks and host clock speed. The server
publisher receives speed from the serialized runtime loop; clients cannot set it through this metadata.
`WorldViewCodecTests` verifies roundtrip and rejects invalid speed or an active faction outside memberships.

`CommandProtocol` uses an explicit numeric allowlist of migrated gameplay commands. Runtime type names,
debug/internal commands and unknown fields are rejected. Server-owned Tick/Issuer/control grants never
appear in command bodies. Requests carry world incarnation, increasing request number, expected role
entity/faction/revision tuples and expected faction-membership revision. Role player identity comes from
the bound connection. Bodies retain session, inventory, job, machine, site and town revisions.
JSON rejects duplicate keys at every depth, non-finite floats, unknown enum values/bits, null array elements,
arrays over 4096 elements, strings over 4096 encoded bytes, depth over 16 and over 100000 tokens.
Optional nullable scalar fields retain their command semantics; domain validation still runs at execution.
**Observed** (source review, no new tests): protocol 20 also carries custody, pickup, loot/carry/medical/facility
orders, editor sessions, player barter and scoped inspection. Fixture/group, loot-window and held serial
revisions survive binding. Editor intent uses a bounded race/gender/control/colour/style DTO rather than
mutable FCS definitions. Barter views reach only participants; visible characters expose controller identity.
**Observed** (source review): protocol 20 adds typed `Manoeuvre` (10), `DoorLock` (154), `Pointed` (183) and
`AreaArrival` (184) intents. They retain actor grants, command provenance and target/site revision checks.
Read-only `VerbLead` / `Verbs` are computed for the recipient's faction using its selected owned lead and visible target;
`ArrivalSerial` / `ArrivalForget` keep arrival state per recipient. Projection never authorizes execution.
Network move/squad/placement positions are bounded to the map on X/Z and one world-size magnitude on Y;
placement batches have at most 2048 entries and rotations require squared length within .01 of one.
These engine input limits also prevent finite-but-enormous values from overflowing geometric arithmetic.
Character targets currently use incarnation-scoped slot/generation references; expected control grants
also identify persistent entities. These references cannot be reused across a world replacement.

`AuthoritativeCommandChannel` accepts one compatibility hello before the host binds a current
`AuthorityConnection` obtained after authentication. The channel itself cannot authenticate identities
or advance admission. Preparing/synchronizing requests remain blocked by `AuthorityGateway`.
Dispatch stamps identity through that gateway and preserves expected revisions; reconnect invalidates
queued old-handle work. Closing an old channel cannot close its replacement. Receipts carry the request,
world, status, operation, revision and tick; Evaluated means handlers ran, not that every business rule
accepted the action. Receipt replay/deduplication has the gateway's bounded in-memory lifetime.

**Verified** by `NetworkingCompatibilityTests`: content/order/rule changes, case normalization, malformed
hello fields, mismatch gates, fragmented/coalesced/truncated/bounded frames and a real loopback TCP exchange.
The installed headless test hashes the actual database load order. This is not multi-client gameplay acceptance.

**Verified** by `NetworkCommandTests`: typed movement/placement, strict parsing, staged access gates,
old-connection fencing, world mismatch, TCP exchanges for three player identities, command receipt roundtrip,
duplicate suppression and cross-player control rejection. Authentication is supplied by the test host;
these checks do not establish account security, private state replication or a playable network client.

## State replication

`AuthoritativeStatePublisher` captures [authorized views](multiplayer.md#authorized-world-views) on the world
owner. `WorldViewCodec` preserves view/role/grant metadata, private details, rendering and collections;
GameRecord values are IDs resolved in the compatible local database. CharacterDisplay contains appearance,
resolved body-shape input and visible attachments, excluding Loadout, BodyFile and diagnostics. No assets or
raw FCS records travel. Ragdoll arrays and tuple/set/door/job representations have explicit converters.
**Observed** (source review): protocol 20 character-keyed maps (`TradeView.GoodsOf` / `GoodsRevisions`, `DialogueRequest.EditSessions`) encode as sorted arrays of `{ Character: { Slot, Generation }, Value }` entries; duplicate/negative identities and null entries/values are rejected.
View JSON is limited to 32 MiB/depth 48/two million tokens; duplicate fields, null array entries and non-finite
numbers are rejected, except CharacterSnapshot.DrawYaw's explicit NaN fallback. Payloads are presentation,
never a source of server commands or simulation state.

**Observed** (source review): protocol 20 adds authoritative projectile speed for tracers (finite, nonnegative)
and `PathExact`, the exact navmesh point count from the first published waypoint (0 through `Path.Count`, or `int.MaxValue` when there is no provisional path).
Paths retain selected-owned authorization; public character projection clears both path fields. Projectile
speed retains the existing nearby-projectile filter. Compatibility rejects earlier application protocols.

**Observed** (source review): recipient captures use independent snapshot buffers; publication detaches mutable
animation arrays, bone poses, appearances and turret angles on the world owner before worker serialization.
`Dummies` / `Seats` carry `SiteRevision`; smith details require managed sites, and projectile shooter references require
visible characters. Nest/ruin/emptied-house views follow the existing observation filters. `FoliageOwnedTowns` includes
only ownership visible through Nearby or Manage observation and updates the client's scene ownership snapshot.
Talk hints and hostile-fight details use the actual recipient; disguise details remain limited to controlled characters.

`ServerStateReplicator` freezes keyed appearance/character/building/machine/projectile/public-faction/survey/message entries plus a remaining-view
entry. Deltas compare against an acknowledged publication, replacing whole changed entries so removed
private fields cannot persist. Ordered key/byte framing produces a SHA-256 document hash. ACKs must name a
retained publication/hash and the same world/player/connection generation. Server/client each retain at most
eight history documents and 64 MiB of encoded state (server includes its acknowledged base); encoding/
decoding adds bounded temporary allocations. These are safety limits, not measured capacity guarantees.

Projection streams UTF-8 through envelope fields without mutable JSON trees. Trusted serialized replication
output is not parsed merely to validate the serializer; inbound patches/views retain structural/domain checks.
Validation reuses depth-local field sets and a bounded per-document name cache; escaped duplicate names still
fail. Appearance reuse compares component bytes before decoding unchanged descriptors. **Verified** by
codec/replication/transport tests.

**Observed** (source review): protocol 20 separates `a:slot:generation` appearance entries from `c:`
character state. Components serialize directly; unchanged appearance descriptors reuse acknowledged encoded
bytes after content comparison. Clients decode only changed components and rebuild the small view envelope;
Whole-component replacement preserves private-field removal; optional character fields equal to their declared
defaults are omitted and reset on replacement. Patch structure is checked once, before typed component decoding. Typed caches contain only the current authorized
view and clear on withdrawal/resync. Orphan appearances and embedded character appearances are rejected.
**Verified** (`SecureServerTests.RemoteMovementToleratesLateAndSameTickPublications`): a 150 ms
arrival gap after 100 ms publications keeps movement continuous, and a repeated simulation tick does
not snap an unfinished blend to its endpoint. `RemotePlayerSession` blends from the displayed pose
using remaining simulation time and a 200 ms minimum window (500 ms maximum), rather than copying
the previous packet's arrival interval. Same-tick updates retain blend progress while refreshing
authorized details. This adds visual headroom; no positions extrapolate beyond the latest snapshot,
so sufficiently long delivery gaps still stop movement. Graphical latency remains unmeasured.

`ClientWorldReplica` has no Simulation.World. It commits a typed view only after the entire patch and hash
validate. Missing bases clear usable state and require a new baseline. Reordered/duplicate older publications
never roll it back, including after clear/resync. Historical documents are internal and not exposed as UI views.
**Observed** (source review): `network` diagnostics use UTC timestamps and record missing-base history IDs/bytes,
replica restoration size/count, server withdrawal, invalid-state exception type and session availability/phase/busy/speed
transitions. The graphical client writes these to `log.txt`; no credentials or state payloads are logged.
Repeated missing-base/withdrawal messages are suppressed until a replica recovers. Missing views display speed zero
and empty character snapshots in `RemotePlayerSession`; this is not evidence that the server clock paused.
**Verified** by `StateTransportTests.SlowReceiverKeepsItsViewAcrossDelayedAcksAndRevocationStillBypassesBackpressure`:
the publisher permits at most two unacknowledged ordinary publications, overlapping decoding with transport while
staying below the eight-document history/grant limits. It waits for ACKs before capturing more. Every attempt
still checks role/membership authority and persistence barriers; revocation bypasses the wait. Resync releases the
wait and requests a full baseline. This bounds slow-client backlog without expiring its unacknowledged grant stamp.
**Observed** (creation-fix release log, 2026-11-02): without this backpressure, publication 14 referenced evicted
base 5; repeated resyncs kept referencing base 5 through publication 159, followed by disconnect and endless loading.
**Verified** by `StateReplicationTests.MovingSnapshotsReuseUnchangedAppearancesButOutfitChangesAndWithdrawalInvalidateThem`:
equal encoded appearance data for the same character retains its object reference across snapshots, so renderer
preparation can finish. The cache contains only current authorized characters and clears on withdrawal/reset.
Successive baselines retain bounded history because an ACK for an older baseline can cross a newer full
publication; subsequent deltas may legitimately use that older base. Full publication still replaces the view.
Absence means OutOfScope unless the authority supplies confirmed destruction. CharacterTable keeps at most
8192 removal facts; PopulationStored marks unload separately. Expired facts default to OutOfScope. Other
component disappearance currently has no destruction journal and remains OutOfScope.

State packets (at most 36 MiB) split into 256 KiB chunks. The 84-byte chunk header contains world/player/
generation/publication, index/count/total bytes and packet SHA-256. The assembler buffers one publication,
accepts identical duplicates/out-of-order chunks, rejects conflicts, and replaces incomplete older transfers.
The receiver decodes each completed patch once, checking its identity/publication against the chunk envelope
before replica mutation; `StateTransportTests.ChunkPublicationMustMatchTheDecodedPacketBeforeCommittingAView` covers this fence.
An ACK frame is 72 bytes; resync/withdrawal frames are 40. All retain the outer WireFrame limits. Hosts still
provide transport deadlines, authentication, serialized scheduling and connection-rate policy.
Loss of current admission or a durable-write barrier emits a monotonic withdrawal, clearing usable client
state and old server baselines. A replaced/closed handle returns no publication and its transport must close.
`ClientStateReceiver` handles chunks/withdrawal and generates ACK/resync frames. After a valid publication/hash
ACK, the publisher calls `ActivateAfterView`: the gateway compares captured role availability/identity/grants
and membership revisions with current state. A changed/expired stamp keeps Synchronizing and forces a new
baseline; no living role returns to Preparing. The gateway retains at most eight grant stamps and clears them
on withdrawal. [Session handling](server.md#connection-host) and [stored-role recovery](server.md#stored-role-recovery)
use this fence. The graphical lobby waits for this activation before handing its connection to the world host.

`RemoteGameSession` owns a TCP/TLS connection and `ClientStateReceiver`, without a Simulation.World. It
checks the server's returned compatibility claims before sending credentials, with a 15-second combined
connection/handshake deadline. One reader and one writer multiplex admission, state, command receipts,
creation and invitation replies. The send queue holds at most 64 frames/4 MiB and each write has a 10-second
deadline; at most 128 requests remain outstanding. Replies match command IDs or typed query GUIDs and must
belong to this world/connection. Callers supply stable mutation IDs and fresh query GUIDs.

`State` supplies coherent admission/view references; consumers treat their data as read-only.
Decoding uses a separate replica lock, released before publishing under the state/command lock; UI reads and
input submissions do not wait for JSON decoding. Stop/reset serialize against the replica. **Verified** by
`SecureServerTests.ReadingDisplayedStateDoesNotWaitForLargeSnapshotDecoding` (Bench, 400 NPCs).
**Observed** (local TLS probe, 2026-11-02): state-read p99 fell from 98.5 ms to 0.022 ms; this is not a frame-rate measurement.
`WaitForChangeAsync` avoids missed notifications without executing UI callbacks on the receive thread.
Preparing/busy admission hides the view. Control requires active admission and a validated usable replica.
ACKs/resyncs are automatic. Withdrawal, protocol failure or disconnect removes usable state; disconnect
also fails all pending requests. Cancellation stops a caller's wait, not an accepted server operation:
late replies are still consumed. There is no automatic mutation retry or reconnect; recover creation and
invitation outcomes through their original identity and lookup/resume. Disposal joins both workers.

**Verified** by `SecureServerTests.RemoteClient`: three production remote connections receive isolated
views and deduplicated receipts; a roleless account creates, enters and recovers the same roles on reconnect;
replacement and membership revocation remove control; cancelled waits consume late replies; disposal cancels
entry preparation; a false server compatibility claim cannot obtain credentials.
`HeadlessContentTests.InstalledCreationAndReconnectKeepAuthorizedViewsAcrossTwentyPublications` uses this
production client against installed content, including appearance/editor previews and deferred-role recovery
after checkpoint restore. This is transport/session coverage, not graphical gameplay acceptance.

`CreationProtocol` carries the private [creation session](server.md#creation-sessions) requests/results,
including display-codec previews. Runtime request models live in Simulation so the wire layer has no
dependency on the runtime host. `Committed` is a durable receipt, distinct from preview/pending/busy/rejection.
`InvitationProtocol` carries [offers and assignments](server.md#invitations-and-assignments), with GUID/role-ID
pagination cursors and connection-bound private replies. Recipient acceptance is separate from sending an offer.

## Regional weather

`WeatherFrame` carries public regional schedules and their authoritative clock, or an explicitly disabled
environment. Each region uses a separate `w:` component, so clock-only updates do not resend every region.
The codec bounds/validates IDs, indices and finite weather values. Presentation additionally validates the
complete region/season/weather catalogue before changing any region; incompatible frames never partially
apply. `AcceptPresentation` performs no random draws or minute-by-minute catch-up and only changes a region's
presentation serial when its weather occurrence changes. Remote weather selection is server-owned.

**Verified** by `WeatherReplicationTests`, `WeatherScheduleTests` and installed-content TLS creation/entry:
baseline/delta/disable, malformed frames, repeated imports, atomic content rejection and renderer ingestion.
Protocol 15 adds recipient-scoped `WeatherEffects` with independent `e:` components. Each root carries a
stable region/generation/group/unit identity (or fixed-placer identity), effect/system IDs, transform,
age/lifetime, stop state, wind and wrapping inputs. Particle pools and generator states stay on the server.
Only roots whose effect bounds intersect authorized observation are published; requested zones can only reduce that scope.
Withdrawal removes the roots; a new baseline replaces the set. Duplicate IDs/nonfinite values are rejected.
`PresentedEffectSet` resolves compatible content before applying a frame, retains cosmetic pools across
updates and never schedules, moves or expires roots locally. The particle renderer suppresses independent
affecting weather/map groups and uses these same roots for particles, fog and effect audio. Camera rain and
non-affecting decorative effects retain local presentation. Forced gameplay harness overrides still need a
presentation contract. Graphical remote boot and actual GPU/audio end-to-end acceptance remain pending.
**Verified** by WeatherReplicationTests, PlayerWorldViewTests, PresentedEffectTests and SecureServerTests.Weather:
per-node deltas, observation isolation, removal, baseline pool retention, TLS delivery and role withdrawal.

## Faction presentation

`FactionCatalogue` contains public identities for memberships, known factions, discovered-town owners and
owners referenced by the current authorized snapshot. It has no funds, research or roster fields; identities
use separate `f:` delta entries. Runtime names override static definitions. Each member faction carries only
its directed standings towards that disclosed catalogue, without assuming reciprocal diplomacy.
`RemotePlayerSession` resolves names, map faction IDs and standings from this view and switches them with
the selected faction. Unknown remote factions do not become implicit enemies.

Contract rows disclose controlled protectees; inspecting all faction contracts additionally requires both
ManageFaction and ManageSquads. Upcoming campaigns require ManageFaction or ManageAssets and match that
faction's Enemy field, including queued requests. Other factions' headings/requests are omitted, and stale
targets with another owner show an unknown target rather than its name. Optional countdowns use nullable
wire values. Membership changes replace these views, including removal of readable funds and panel rows.
**Verified** by `FactionPresentationTests`, real TLS faction switching/revocation in `SecureServerTests.Factions`
and `WorldViewCodecTests`. These projection tests do not establish complete multiplayer campaign scheduling.

## Talk hints

`InteractionProtocol` carries bounded 4 KiB queries naming a speaker, visible NPC, exact role grant and
membership revision. The gateway requires active admission, a target in the latest published character set,
and current observation range/filter; it never loads a queried zone. Unknown/missing targets share `Unknown`.
Busy NPCs and unready sessions also return `Unknown`; changed grants return `StaleAuthority`.
Only a validated query evaluates dialogue conditions. Dry-run chance state is restored even on exceptions,
and queries neither allocate gameplay request numbers nor authorize subsequent actions.

`RemotePlayerSession` caps new queries at ten per second, retains 64 entries/16 pending replies, and caches
completed answers for half a second. Role/membership changes invalidate them. `WantsToTalk` is a
recipient-specific snapshot flag; actual follow-up consumption also checks the speaker's faction.
UI clicks waiting on eligibility expire after two seconds and are cancelled by newer commands, pointer
presses or Escape. Unknown eligibility never selects an attack fallback. Attributed TalkOrder rejection
never becomes an implicit AttackOrder. **Verified** by `DialogueTests.Hints`, `SecureServerTests.Hints` and
`PlayerSessionUiTests`, including cross-player/cross-faction follow-ups, stale visibility, unchanged world
hashes, delayed hostile-NPC clicks and cancellation.

## Private results and transient events

`ProspectOrder` names the actor and requires its current grant. The completed survey belongs to its issuing
player, including after disconnect; it is transient UI state, not a checkpoint record. The `prospect` state
component separates its map from the frequently changing view shell, so ordinary deltas do not resend it.
The map codec wraps optional resource grids (rocks have no grid) while keeping generic null array elements
forbidden. Grid dimensions/resource counts are validated before UI use.

`World.EmitFloater`, `EmitSound`, `SaidPublic`, `EmitBlood` and `EmitBlockSpark` record multiplayer display events with one monotonic ID
space. The journal retains at most 512 combined events for three wall-clock seconds, with text capped at
4096 characters and speaker names at 256. Legacy local queues remain available.
Text such as consumed item names belongs only to the target's controller at emission; damage needs a visible
target, and labels need authorized observation of their position. Own/enemy flags are relative to the recipient.
Projectile source ownership is captured at firing, so later controller changes do not reattribute that hit.
Events never affect simulation hashes or saves. Overflow/expiry may drop cosmetic events.

Public character sounds require a visible emitter and authorized observation of the event position;
bolt-ground, turret and wall sounds need the position only. Hidden subject/other IDs are removed. Shackles,
bounty and dismantling UI cues belong to the subject's controller at emission; item IDs travel only with
private shackles cues. Projectile sound references retain the firing character's generation. Public speech
logs require a currently visible speaker; player colouring is relative to the viewer.
Protocol 16 adds blood/explosion marks (position-scoped, hidden character IDs redacted) and block sparks
(visible character required). Blood carries its emission tick, step duration and corpse lifetime; the view also
carries the simulation step duration. Headless producers emit without renderer queues, including an explosion
whose victim was removed that tick. No damage or physical loot is inferred from these cosmetic messages.

The remote adapter skips historical events on its first usable view and after withdrawal, deduplicates later
IDs across deltas/baselines, and retains at most 128 entries per floater/sound/public-speech/blood/spark queue.
Sound/blood/spark delivery is opt-in. Disabling a channel clears its queue; reenabling seeds a floor
from the transport's newest view, including when presentation frames were suspended. `RequestBaseline`
requests a fresh complete view while
retaining the current valid replica. **Verified** by `PresentationEventTests`, `PrivatePresentationReplicationTests`
and real TLS delivery in `SecureServerTests.Presentation`, including private cues, hidden references, baseline
replay, disabled effects and private survey maps.
**Verified** by `CombatVisualTests`, `LuckyrenTests` and `SecureServerTests.CombatVisuals`: headless death,
positional redaction, event timing, malformed/duplicate rejection, mute floors and immediate queue clearing
after transport withdrawal without another presentation frame. GPU/decal/audio end-to-end acceptance is pending;
remote clients do not receive historical decal stores on entry.

`PlayerState.Messages` retains the last 200 private notices/dialogue lines with per-player sequence numbers.
The independent `messages` component avoids resending unchanged history in ordinary deltas. Explicit-control
worlds keep unattributed legacy Post/Said diagnostic-only; they do not become the local account's inbox.
Faction notices go only to explicit members. Attributed conversation logs go only to their participant;
pending bubble lines revalidate authority before expansion. These UI histories are not saved or hashed.

Remote UI restores history without old popup replay, then consumes new IDs once; transient public speech
joins its log separately. The popup backlog is capped at 32. The local host keeps its TakeMessages/TakeSaid
flow, including quiet screenshot setup. **Verified** by `PlayerMessageTests`, `DialogueTests.Hints`,
`PlayerSessionUiTests`, component delta tests and real TLS reconnect tests in `SecureServerTests.Messages`.

**Verified** by `WorldViewCodecTests`, `StateReplicationTests`, `StateTransportTests`: complete typed views,
content ID resolution, private-field replacement, ACK loss/reordering, missing-base recovery, recipient
isolation, bounded/conflicting/reordered chunks, 400-character baseline assembly, withdrawals and real TCP
world publication with unload/destruction distinctions and stale-baseline admission fencing. The installed headless test roundtrips generated
appearances and private inventory. These checks do not constitute final multi-client gameplay/capacity acceptance.

## Graphical admission

**Observed** (implementation): both title styles expose Multiplayer. The form accepts only host[:port]
(bracketed IPv6, default port 27800). No account/profile file is required. `DeviceConnection` discovers
public compatibility metadata, validates local content and atomically persists a random per-endpoint
GUID/256-bit credential before registration. LocalApplicationData/Meitou/identities is private client state;
missing state creates a new identity, but malformed state or rejected credentials never silently replace it.
**Observed** (source): clicking Connect stores the trimmed address in `meitou.user.json`'s
`lastServerAddress`; reopening Multiplayer restores it even if the previous connection failed.
Discovery retries a transport IO/socket failure once on a fresh connection; cancellation, malformed
metadata and TLS authentication failures are not retried. Authentication and creation are not replayed
by this retry. The cause of the reported first-query connection resets remains **Unknown**.
**Verified** (`DiscoveryRetriesOneInterruptedReadAndStopsAfterTheSecond`): closing a real TLS
discovery connection before its reply triggers one retry; a second EOF is reported rather than looped.
**Verified** (`LobbyRecoversRejectedSavedQualificationWithoutASecondConnection`): an unknown saved
creation request is replayed unchanged; a definitive qualification rejection clears it and requests
the current catalogue within the same connection. Pending/unknown outcomes retain the request.
Successful entry clears the completed journal. Server eligibility still decides whether creation is offered.
**Observed** (source review): `MEITOU_IDENTITY_DIRECTORY` overrides the default credential directory;
an explicit `PrepareAsync` directory takes precedence. Packaged player 1/2 scripts select the default
and `LocalApplicationData/Meitou/identities-player2` respectively; legacy profile credentials are unchanged.
The menu omits the initial faction name; the server derives a unique name from its player-faction template
and player ID, adding a suffix on collision. Explicit names still reject duplicates. Vulkan only; Metal online entry remains unsupported.

Discovery sends no credentials. First contact trusts the presented, unexpired TLS certificate and pins it
only after metadata/content validation (TOFU, not independently verified server identity). Subsequent
contacts and the separate authentication connection require that pin; certificate changes fail closed.
ServerInformation is bounded to 128 KiB, 128 rules and 1024 relative install asset paths. No assets or
credentials are downloaded. Old servers without discovery must be updated. Legacy CLI profiles still work.
**Verified** by `SecureServerTests.Devices`: address parsing, traversal rejection, certificate changes,
and installed-content identity persistence without silently replacing corrupt local state.

`OnlineLobby` authenticates asynchronously. Existing living/stored roles resume entry; eligible roleless
accounts browse the server catalogue through the original beginning panel, then edit every human squad
member in the existing body/face/hair editor, with race restrictions, randomisation, import/export and orbit.
Drafts reconstruct only public editor controls. Debounced server previews supply rendered appearances;
confirmation validates and durably commits the final choices before requesting entry. No local player
squad is spawned. Difficulty remains server-owned. Esc/cancel abandons preparation; it cannot undo an
accepted commit. A cancelled/failed connection can be retried from the form.

Before commit, a flushed atomic journal under LocalApplicationData/Meitou/connections stores the request
without credentials, keyed by endpoint/account/certificate. A per-key lock excludes concurrent launchers.
Reconnect queries/resumes that exact request; unknown outcomes retain it, definite non-persistence
rejections release it. Once admitted, the lobby transfers connection ownership to a fresh graphical host;
the online exit menu disposes that host and returns to the title.

**Verified** by the existing installed-content `HeadlessContentTests.InstalledCreationAndReconnectKeepAuthorizedViewsAcrossTwentyPublications`:
production TLS creation, appearance preview and entry. **Unknown**: complete graphical mouse/keyboard
acceptance of the new lobby/editor/host transition.

**Verified** by `SecureServerTests.UnnamedNewPlayersReceiveDistinctFactionsAndCreationReplayKeepsTheName`
and installed-content creation/entry: omitted names commit, consecutive players get distinct factions,
explicit duplicate names reject, and receipt replay preserves the faction. Creation validation failures
log the phase/request ID and reason on the server; graphical connection failures log their exception locally.

## Local latency acceptance

**Observed** (2026-11-09 dual-client report): server passes included 1.55–1.78 s of owner work,
504–737 ms simulation passes and up to 5.05 s of tick debt. Their individual causes and graphical
latency remain **Unknown**. A later 30 s live dual-client sampled-thread-time trace attributed about
22 s of owner-thread execution to `ZoneKeptClosure.Resolve`: navigation recomputed unchanged loaded
zone connectivity at every runtime boundary. Closure invalidation is described in
[zone borders](game/pathfinding.md). A 30 s dual-client trace after invalidation gating measured
0.013 s in closure resolution and 9.45 s in the owner loop, versus 21.95 s and 28.28 s before.
These are sampled execution estimates, not command-to-display measurements or controlled capacity tests;
weather simulation, pathfinding and worker-side publication encoding remain significant costs.
Concurrent clients in one directory use `log.txt` for the first writer
and `log-<process-id>.txt` for another; feedback attaches the submitting process's log.

Project budgets, not an industry standard: 30 Hz simulation and 10 Hz publications imply mean scheduling
waits of about 16.7 + 50 ms before processing/presentation. Sources:
[Riot server budgets](https://www.riotgames.com/en/news/valorants-128-tick-servers) and
[Fiedler snapshot interpolation](https://gafferongames.com/post/snapshot_interpolation/).
Loopback retains TCP/TLS/scheduling costs; do not subtract an invented network delay. Use Release, the copied
~230-character installed-content save, 110 s of existing move-command probing, discard the first 15 s,
include autosaves (60 s during acceptance), and avoid concurrent builds/tests/profilers in the final run.

| Metric | Mean | P95 | P99 |
|---|---:|---:|---:|
| Command submission to authority receipt | <=50 ms | <=100 ms | <=150 ms |
| Publication arrival interval | <=110 ms | <=150 ms | <=250 ms |
| Command to first changed authoritative snapshot | <=100 ms | <=150 ms | unmeasured |
| Command to visible response including interpolation | <=200 ms | <=250 ms | unmeasured |

The existing probe measures only the first two rows. The latter two remain **Unknown** without corresponding
instrumentation; publication intervals/receipts cannot establish input-to-display latency. Require no unexpected
disconnect, missing baseline or withdrawal during active play. Report startup and maximum stalls separately.

**Observed** (2026-11-04, above probe; 227–259 visible characters): 95 warmed command receipts averaged
12.38 ms (P95 20.2, P99 24.2, max 24.4); 949 publication gaps averaged 100.18 ms (P95 131, P99 222,
max 294). Both measured budgets pass, including two autosaves; no unexpected disconnect/baseline loss.
The probe closes its connection normally at 110 s. Initial simulation warm-up included a 777 ms tick;
these results do not establish graphical input-to-display latency or multi-client capacity. A separate existing
25 s state-read probe recorded a 0.142 ms maximum (1,757 reads, none over 10 ms).
