# Networking contracts

Engine contracts for compatibility, authority, recipient privacy and the client replica. The current protocol 21
wire, transport lanes, replication format and acceptance gates are specified in
[networking-next](networking-next.md). Current source implements that integration; release acceptance remains pending.

Compatibility checks carry application protocol, engine/build identity, authoritative rules and the ordered
content manifest. The manifest uses normalized logical keys, content kind, byte length and SHA-256; it never
contains local paths. Reordering database entries or changing bytes changes the digest. Hosts include the
gameplay assets needed for simulation parity; matching database hashes alone do not prove asset coverage.
Rules digests retain exact authoritative values. A hash does not prove honest client attestation.
`NetworkingCompatibilityTests` verifies content/order/rule changes, normalization and mismatch handling.

**Observed** (source): `ServerCompatibility.EngineBuild` is the shared client/server identity for the integrated upstream rules.
The current identity is `meitou-upstream-7379edb6-authority-1`; ground-item material IDs and optional blood-event attacker IDs
travel with their existing state/event components. Attacker IDs are removed when that recipient cannot see the attacker.
The authoritative `AnimalsPickUp` rule is included in gameplay settings and their digest. Older build identities fail admission;
unknown gameplay rules are not silently accepted. Transport and container framing remain protocol 21.

Authentication and admission are separate from compatibility. The server binds identity from the authenticated
connection; commands cannot supply player identity, issuer, tick or control grants. Client intents expand selected
actors against the displayed view and carry expected role/faction/membership revisions. `AuthorityGateway`
rechecks ownership, grants, revisions, world incarnation and active admission when executing. Preparing or
synchronizing clients cannot act; reconnect fences old handles. Evaluated receipts mean handlers ran, not that
business validation accepted the operation. `NetworkCommandTests`, `ClientCommandBindingTests`,
`PlayerWorldViewTests` and TLS cases in `SecureServerTests.RemoteClient` cover these contracts.

Recipient views are projections, never commands or authoritative simulation state. Private fields are scoped
to their authorized player/faction; hidden references are removed and withdrawal clears the usable replica.
Membership, role availability and grant changes are rechecked before activation/publication. Character identity
and control grants are incarnation-scoped so a world replacement cannot reuse stale authority. Shared view
contracts, faction presentation, weather, messages, events, creation and invitations are described below and in
[multiplayer](multiplayer.md). Detailed wire encoding and protocol acceptance live only in
[networking-next](networking-next.md).

**Observed** (source): recipient projection publishes `ContainerOpener` only for a currently authorized, visible container session.
`RemotePlayerSession` masks container windows in both displayed snapshots using the current visible, available role and positive session ID;
withdrawal or a mismatched opener/session hides the window. Independently arriving interaction and machine components may temporarily differ.
This presentation filter preserves raw component values and epoch/revision fences; the codec validates ID structure without requiring
same-frame references across components. Wire field ownership is specified in [networking-next](networking-next.md#game-connection-integration).

`RemoteGameSession` consumes the typed replica without a local simulation world. Control requires both Active
admission and a usable view. Creation and invitation replies are private to the connection/player; mutation
recovery uses the original operation identity and lookup/resume, not automatic replay. Graphical gameplay uses
`IPlayerSession`; [graphical admission](#graphical-admission) performs account and creation flow before handing
the session to the world host. Source and test coverage do not yet establish final multi-client gameplay acceptance.
## Regional weather

`WeatherFrame` carries public regional schedules and their authoritative clock, or an explicitly disabled
environment. Each region uses a separate `w:` component, so clock-only updates do not resend every region.
The codec bounds/validates IDs, indices and finite weather values. Presentation additionally validates the
complete region/season/weather catalogue before changing any region; incompatible frames never partially
apply. `AcceptPresentation` performs no random draws or minute-by-minute catch-up and only changes a region's
presentation serial when its weather occurrence changes. Remote weather selection is server-owned.

**Verified** by `WeatherReplicationTests`, `WeatherScheduleTests` and installed-content TLS creation/entry:
baseline/delta/disable, malformed frames, repeated imports, atomic content rejection and renderer ingestion.
Recipient-scoped weather effects carry Each root carries a
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
Blood/explosion marks are position-scoped with hidden character IDs redacted; block sparks require a visible character. Blood carries its emission tick, step duration and corpse lifetime; the view also
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
Role-status notices route through `PostCharacter` to that role's controller; faction/diplomacy notices go only to
explicit members. Attributed conversation logs go only to their participant; unattributed ambient bubbles emit
`SaidPublic` to nearby viewers, and `TutorialFacts` in `CapturePlayerView` are scoped to the recipient's owned
living roles. Pending bubble lines revalidate authority before expansion. These UI histories are not saved or hashed.

Remote UI restores history without old popup replay, then consumes new IDs once; transient public speech
joins its log separately. The popup backlog is capped at 32. The local host keeps its TakeMessages/TakeSaid
flow, including quiet screenshot setup. **Verified** by `PlayerMessageTests`, `DialogueTests.Hints`,
`PlayerSessionUiTests`, component delta tests and real TLS reconnect tests in `SecureServerTests.Messages`.

**Observed** (existing test coverage): `GameStateReplicationTests` and `ComponentViewBehaviorTests` exercise protocol 21 component/lifecycle behavior and recipient isolation; `InstalledQuicAcceptanceTests` covers installed-content QUIC creation, entry and reconnect. Final graphical gameplay and capacity acceptance remain pending.

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

Discovery sends no credentials and runs over encrypted TLS without server certificate pinning, so replacing
a self-signed server certificate does not block clients.
ServerInformation is bounded to 128 KiB, 128 rules and 1024 relative install asset paths. No assets or
credentials are downloaded. Old servers without discovery must be updated. Legacy CLI profiles still work.
**Verified** by `SecureServerTests.Devices`: address parsing, traversal rejection, public metadata discovery,
and installed-content identity persistence without silently replacing corrupt local state.

`OnlineLobby` authenticates asynchronously. Existing living/stored roles resume entry; eligible roleless
accounts browse the server catalogue through the original beginning panel, then edit every human squad
member in the existing body/face/hair editor, with race restrictions, randomisation, import/export and orbit.
Drafts reconstruct only public editor controls. Debounced server previews supply rendered appearances;
confirmation validates and durably commits the final choices before requesting entry. No local player
squad is spawned. Difficulty remains server-owned. Esc/cancel abandons preparation; it cannot undo an
accepted commit. A cancelled/failed connection can be retried from the form.

**Observed** (source: `CharacterEditorHost.DrawEditorStage`, `GameHost.FillDrawListCore`): online drafts use
synthetic character IDs and a stage origin of zero; drawing their server-provided appearances must not
require a matching world snapshot character. In-world editors still require that snapshot.
`GameSaves.PrintSavedLog` reads NPC save logs only from a local `WorldSession`, never a remote replica.
`OnlineLobby` logs elapsed milliseconds separately for discovery/content checking, client compatibility,
authentication, admission preparation and appearance preview, without credentials. **Unknown**: the cause
of the reported connection/start-preview slowdown; stage timings and server preparation logs are needed.

Before commit, a flushed atomic journal under LocalApplicationData/Meitou/connections stores the request
without credentials, keyed by endpoint/account. A per-key lock excludes concurrent launchers.
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

## Protocol 20 local latency baseline (historical)

**Observed (protocol 20 baseline only; not current protocol 21 acceptance)** (paired VPS capture on 2026-11-09, complete trace footers, zero drops; 660 delta
publications with full component-size sampling): mean patch size 637,761 bytes; `view` contributes
81.48% of raw change-value JSON, characters 18.41%, appearance/weather-region together below 0.12%.
`view` averages 517,162 bytes/publication. It retains transport publication/time and the unsplit
snapshot collections (fixtures, interaction/UI state, faction/player details and event batch);
`FreezeAgainst` only splits the explicitly named collections, so a new publication number alone
changes the entire `view` component. Per-field sizes within `view` remain **Unknown**.
At 10 publications/s that mean patch size implies about 51 Mbit/s of application payload; the user's
corrected VPS uplink specification is 25 Mbit/s. Observed reader throughput near 26 Mbit/s is evidence
of bandwidth pressure, not proof of the provider's exact shaping/burst accounting. Removing `view`
alone would still imply about 9.65 Mbit/s at 10 Hz. New replication must split the data model rather
than merely remove the clock field or change transport. The 117 movement samples were classified
as superseded/non-stationary; none completed the stationary intent-to-render acceptance chain.

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
The protocol 20 loopback probe retained TCP/TLS/scheduling costs; do not subtract an invented network delay. Use Release, the copied
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
