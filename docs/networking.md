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
The current identity is `meitou-upstream-315d26ec-authority-1`; both endpoints require item-tag digests, compressed JSON component envelopes,
experience rules, corpse-machine sound metadata and sparse binary ragdoll poses,
the [diagnostic control messages](diagnostics.md), mod-presentation components and editor-open command, in addition to the presentation-event
and bounty contracts in [networking-next](networking-next.md).
Ground-item material IDs and optional blood-event attacker IDs
travel with their existing state/event components. Attacker IDs are removed when that recipient cannot see the attacker.
The authoritative `AnimalsPickUp` rule is included in gameplay settings and their digest. Older build identities fail admission;
unknown gameplay rules are not silently accepted. Transport and container framing remain protocol 21.

**Observed** (source): `AnimalMovesFix`, `PrisonPoseFix` and `GroundRepair` also belong to the authority's gameplay rules. The headless host
configures heightmap repair before opening terrain; a remote host uses the admitted rule before opening its scene. The corresponding
remote settings are read-only; head-turn release, photo poses/lights and other purely visual choices remain local.

Trusted code-mod data phases run before derived game content. Both endpoints independently fingerprint the actual merged-record edits
under `code-data/*`; copying the server's rule strings cannot substitute for running the locally trusted phase. The client reuses the
same edited database for admission, the view codec and rendering. The content manifest includes enabled `RE_Kenshi.json` files, the
resolved targets of their final file rebinds and redirected explicit gameplay assets. Hidden or missing rebind targets carry a stable
marker distinct from an empty file. Logical keys never carry local absolute paths; these checks load no native RE_Kenshi plugins.

**Observed** (source): `SquadXp`, `OthersXp` and `BagSearchFix` are authoritative gameplay rules, included in compatibility and the
recipient projection. XP classification covers every player faction. Headless and graphical hosts share the built-in XP-mod adoption
rules; remote settings cannot change them. Field 2074 (`WarParty`) is retired and is not reused.

**Observed** (source): `WorkSites` publishes server-resolved IDs, positions, owners and positive site revisions for observable natural
and level work targets before a machine exists. It grants no inventory or machine configuration access. Nearby NPC worksite stock
requires a currently authorized actor within transfer reach; machine controls require asset permission. Item rotations update inventory
revisions, and held rotations retain the issuing player's holder serial and actor grants. Squad autonomy/home commands require the
current faction membership and ManageSquads; a remote home assignment additionally requires ManageAssets for that building.

Map markers disclose observed campaigns, campaigns targeting a faction whose event details the recipient may read, and that player's
contracts or contracts of a faction they may fully manage. Character bone anchors omit hidden carriers. `FoliageOutposts` contains only
geometry affecting the recipient's observed area, plus managed outposts; influence margins follow navigation, without exposing economic
outpost data. Nearby foliage ownership is world-wide player ownership, independent of the client's selected faction.

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
## Mod presentation and new inspection state

**Observed** (source): `Meters` and `ModSkills` use explicit changed-only binary components. A remote view includes at most its selected
lead and inspected character's new UI details; other players' skill arrays stay private. Nullable meter slots retain registration indices,
and mod skills preserve the upstream NaN sentinel. Effective vanilla stats already use the existing skill/HUD components; temporary
modifiers are evaluated by the authority. Registration capacity is shared by the headless mod host and client UI: 4 meters and 34 skills.
Meter tooltips have a separate absolute component from their dynamic bar/value/arrows. A digest binds the two components; reordered
updates display a tooltip only when its table matches the readings. Baselines require both components, and withdrawal clears both.
Changing tooltip tables larger than 1024 bytes coalesce the complete meter presentation to 2 Hz using monotonic time. The last sent pair
remains coherent until the latest capture is due; new entities and visibility/text withdrawals publish immediately. Static tooltips do not
slow dynamic readings, and short changing tooltips retain their normal cadence.

`BoneMeshes` is public only for observable characters and sends bounded asset/bone references and local transforms, not mesh data or
per-frame bone matrices. Baselines, changes and removal use the existing lifecycle/revision ledger. Matching content/code manifests supply
both endpoints' definitions; clients do not run authoritative mod systems to reconstruct state. Cosmetic meters/attachments remain unsaved,
while custom skill levels use upstream STATS persistence and temporary modifiers retain their mod-sidecar restoration contract.

`InspectedLevelPower` is one nullable binary component for a known, observable building. Static level lights with no machine/fixture entry
can be inspected through their known position and the last validated observation filter, rechecked against current controlled roles.
The level index resolves the current town list and effective ruin state, including layout changes and taken-over player buildings.
This read-only fallback does not add a worksite or grant inventory/configuration access. `StealChance` is calculated for the actual loot or
container opener and is absent outside that session; a null value consumes no item JSON property. Its tooltip changes at whole-percent
precision, without changing the server's theft roll.

Command 121 (`InPlayEditorOpened`) carries one character and its server-issued editor session. Current control/membership, session owner,
character and surgeon proximity are checked before hair restoration. The session records that opening already ran, so replay does not undo
later shaving; captivity stays intact. Remote debug stat/editor commands remain unexposed. Source/binary compatibility is enforced at admission.
Payload estimates, bounds and the 500 KiB/s optimization target live in [the bandwidth contract](networking-next.md#upstream-presentation-budget).

### Squads, mod jobs and interaction closure

**Observed** (source): fields 1060/1061/1062 carry `IndependentSquads`, `InventoriesClosed` and `ModJobNames` respectively.
Independent rosters are scoped to member factions and observable members; the active faction is projected by `RemotePlayerSession`.
An independence change advances existing role revisions and removes selection/control availability without deleting persistent ownership.
Orders recheck current controllability, so a command captured before the switch cannot direct an independent member.
Squad-management commands still require current faction permission; remote callers cannot assign a mod's `Owner` identity.
A player-issued independence switch keeps at least one other controllable role for every affected controller, including mixed-owner
squads. Trusted mod commands retain their own independence/recovery semantics and the upstream last-led-squad rule.

Inventory closure routes by the affected actor's controller (or each affected faction member), closes only that player's open trade,
loot/container windows and barter, and advances their closure counter. Pending walk-up open orders remain intact. Other players keep
their own interaction sessions. Fixture town identities and held-item attach slots use their existing typed fixture/held components.
`OrderCommand.Clicked` travels in the typed command body; the authority derives the task point from the resolved building and terrain.
`CagesSpareOwn` is an authoritative rule included in compatibility and remote read-only settings.

Mod job names are registered once, copied into an immutable table and encoded once per recipient baseline; unchanged tables add no delta.
Registration and decoding share the 4096-type/256-UTF-16-unit name bounds, with explicit rejection rather than dropped entries.
Global components share the 32 MiB expanded-publication bound; the 256 KiB deferred-character bound does not apply to this table.
Mod map markers derive from the authorized client snapshot; the callback adds no marker traffic and cannot replicate private mod state.
See [mod registration and multiplayer authority](modding.md).

### Head targets, status effects and faction counts

**Observed** (source): character component 2101 carries the server's nullable `LookAt` target: one byte when absent, or one byte and
three exact little-endian floats when present. It changes through the existing revision ledger. Targets outside the recipient's
observation area project to the same horizontal head bearing 64 units away; their true distance and hidden identity are not disclosed.
`PointedCommand` is bound to the published character set and also applies while paused; it only requests the permitted context facts.

Components 2102 and 2103 split status effects into stack/lifetime readings and immutable definitions. A reading contains the simulation
tick, step duration and definition digest; the replica advances the displayed countdown from that anchor. Only the authority applies
modifiers or removes an effect. Ordinary clock advancement does not resend text or readings; stack changes and lifetime refreshes use
the existing publication cadence. Previously seen definitions remain cached for the currently selected lead or inspected character,
so removing/reapplying a common effect does not repeat its description. Losing that details panel or entity generation discards the
cache. At most two character panels receive these details; other owned or merely nearby actors do not receive the effect list.

Snapshot fields 1063/1064 and the membership-scoped faction view carry total and cap-counted living members, including saved unloaded
roles. Mod-owned `Uncounted` squads are excluded only from the cap. Independent purses and dormant simulation are server state, saved
with the squad; their public consequences use existing money, shop, role and motion components. A client's camera never wakes a
sleeping squad. New work-tool metadata, machine idle/item dimensions and farm harvest progress use their existing typed components.

### Incremental component bandwidth

**Observed** (2026-11-19, .NET 11 Release, synthetic detached snapshots): a repository-external diagnostic used production
`GameStatePublisher`, `GameStateReplica`, `MotionReplicationProducer`, bulk/control encoders and the motion scheduler, and checked
complete field round trips. Identical GUIDs, trajectories and effect inputs were replayed before and after the new lossless compression.
The before column already includes the new synchronized fields and effect-definition cache; it is not a historical measurement of an
older Coop release. World time advances at 30 Hz, motion sampling at the real server's 50 ms interval (20 Hz), and full components and
critical clock publication at 100 ms (10 Hz). Existing interest tiers still select each character's motion cadence.

The following are total application bytes in both directions per simulated second, averaged over ten seconds after the initial
baseline. They include motion, ordinary components, critical frames, reliable motion checkpoints, bulk framing/control and reverse
ACKs. KiB means 1024 bytes. The final column adds an explicit budget assumption of 96 bytes per application message, including reverse
ACK messages; it is not measured QUIC/TLS/UDP/IP overhead.

| Scene for one recipient | Before compression KiB/s | Final KiB/s | Final application messages/s | With 96 B/message allowance KiB/s |
| --- | ---: | ---: | ---: | ---: |
| 100 nearby characters moving and looking | 72.02 | 23.06 | 131.0 | 35.34 |
| 500 nearby characters moving and looking | 313.48 | 59.69 | 229.8 | 81.23 |
| 500 combat characters, regular motion and look targets | 711.10 | 128.67 | 574.8 | 182.56 |
| 500 combat characters, unpredictable look targets | 711.10 | 177.80 | 594.8 | 233.56 |
| 500 combat characters, scattered positions, differing velocities/headings and unpredictable look targets | 711.10 | 405.04 | 594.8 | 460.80 |

The last row avoids assuming that every character shares the same movement. Motion datagram counts are unchanged by compression:
400, 1350, 4800, 4800 and 4800 over the respective ten-second traces. The last row sends 480 motion datagrams, 60 bulk/control messages,
10 critical messages, 2.4 checkpoints and 42.4 reverse ACKs per second. No sampling frequency, coordinate precision, packet grouping,
reliable checkpoint or retransmission semantics were reduced, and no new rate limit or deferred publication was added.

Status-effect samples use two detailed characters, each with 32 effects and 16 KiB of description per effect, including one permanent
effect. The main sample deliberately uses repeated ASCII text and is highly compressible; its baseline ratio is not representative of
arbitrary mod text. Dynamic state results below include the existing idle motion and critical-clock traffic, approximately 1.900 KiB/s.

| Effect changes during ten seconds | Before compression KiB/s | Final KiB/s | Definition traffic after first introduction |
| --- | ---: | ---: | --- |
| Static effects, only countdown advances | 1.900 | 1.900 | Zero; countdown component also sends zero deltas |
| All stacks change once per second | 3.810 | 2.977 | Zero |
| Remove and reapply one effect each second | 3.796 | 2.975 | Zero |
| Empty, add, remove all, reapply cycle | 111.280 | 2.978 | Initial addition only; later additions reuse the cache |

One-time baseline totals, including reverse ACKs, are separate from the steady-state table: the two-character repeated-text sample is
1088.417 KiB before and 3.188 KiB after compression; the 500-character scattered-motion sample is 2353.272 KiB before and 195.975 KiB
after. A second description sample uses fixed-seed random bytes encoded as Base64, still 16 KiB per effect. Its high-entropy baseline
is 1088.417 KiB before and 821.665 KiB after (807.896 KiB down plus 13.770 KiB ACKs), while unchanged subsequent captures remain
1.900 KiB/s. The empty/add/remove/reapply trace including its first definition transfer averages 111.280 to 84.267 KiB/s. Long new
definitions can therefore still create a large one-time transfer; text is neither truncated nor resent for ordinary countdowns.

In the scattered 500-character trace, cumulative motion production/admission/serialization time was 49.61 ms before and 232.93 ms
after, motion decode 19.72/125.57 ms, and bulk packing/framing 11.23/18.20 ms over ten simulated seconds. These wall-clock sections
include scheduling, checks and allocation, not just Brotli. The final compressed run partly overlapped test execution, while the
uncompressed run used an idle build window; these samples do not establish a precise CPU percentage or real-game frame rate.

The 500 KiB/s/player average remains an optimization target, not a hard cap. These synthetic traces omit real Kenshi animation,
equipment changes, combat events, player commands and loss/retransmission. Initial baselines and simultaneous unrelated gameplay
must also be included in live acceptance. Neither the application totals nor the 96 B allowance establish a guaranteed on-wire
maximum. The independent absolute compression format and allocation bounds are specified in
[the transport contract](networking-next.md#absolute-bulk-and-motion-compression).

### Item actions and shared stock

**Observed** (source): mod item-use handlers run only on the authority, with the issuing player's current character grant, inventory
revision and item identity. Each inventory or trade item carries an optional 32-character `ModTagDigest` instead of its raw mod
tag; empty tags send no property. It is the first 128 bits of SHA-256 over exact UTF-16 code units, used alongside inventory revisions
and actor grants. The authority and saves retain the complete tag, and retagging
advances inventory revisions. Tokens created by local windows retain raw tags; remote tokens echo the digest and cannot combine it with
a different raw tag. Nullable stolen-owner labels are omitted when absent.

Building-shop stock stays in physical containers. Trading uses one shared projection, settles each mutation before the next command,
and rebuilds after external container changes. A held stock item remembers its original shop, so rollback uses the current projection
even after a window closes or another player changes stock. Per-player trade/container sessions, revisions and actor grants remain
required; a stale window cannot buy an item already removed from its container.

**Observed** (source): NPC purchase price floaters and new world sounds use the recipient event journal. Prison-release audio is private
to the released character's controller. Corpse-machine audio carries the captured armour material and uses observable position when
the body has already been removed; hidden character references are stripped. Local inspection audio remains a UI action.
The new `animalage` and faction-relation debug actions remain local-only and are absent from the remote command whitelist.

## Regional weather

`WeatherFrame` carries public regional schedules and their authoritative clock, or an explicitly disabled
environment. Each region uses a separate `w:` component, so clock-only updates do not resend every region.
The codec bounds/validates IDs, indices and finite weather values. Presentation additionally validates the
complete region/season/weather catalogue before changing any region; incompatible frames never partially
apply. `AcceptPresentation` performs no random draws or minute-by-minute catch-up and only changes a region's
presentation serial when its weather occurrence changes. Remote weather selection is server-owned.

**Verified** by `WeatherReplicationTests`, `WeatherScheduleTests` and installed-content TLS creation/entry:
baseline/delta/disable, malformed frames, repeated imports, atomic content rejection and renderer ingestion.
Each recipient-scoped weather-effect root carries a stable region/generation/group/unit identity (or fixed-placer identity), effect/system IDs, transform,
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
Text such as consumed item names belongs only to the target's controller at emission. Floaters with a character target
require that target to be visible; fixed-position building damage and labels without a target require authorized observation
of their position. Own/enemy flags are relative to the recipient.
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
