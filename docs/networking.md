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
The current identity is `meitou-upstream-e820fb87-authority-2`; both endpoints require lasting-character IDs, tag heights, resolved mod tints,
named mod sounds, cage-click commands, item-tag digests, compressed JSON component envelopes,
experience rules, corpse-machine sound metadata, sparse binary ragdoll poses, shared authority visuals, counted stack lifts,
shown-machine hints, turret-shot sound selection, shared item-tag definitions, trail/tint/pulse/audio state and portable WAV playback identities,
the [diagnostic control messages](diagnostics.md), mod-presentation components and editor-open command, in addition to the presentation-event
and bounty contracts in [networking-next](networking-next.md).
Ground-item material IDs and optional blood-event attacker IDs
travel with their existing state/event components. Attacker IDs are removed when that recipient cannot see the attacker.
The authoritative `AnimalsPickUp` rule is included in gameplay settings and their digest. Older build identities fail admission;
unknown gameplay rules are not silently accepted. Transport and container framing remain protocol 21.

**Observed** (source): `AnimalMovesFix`, `PrisonPoseFix` and `GroundRepair` also belong to the authority's gameplay rules. The headless host
configures heightmap repair before opening terrain; a remote host uses the admitted rule before opening its scene. The corresponding
remote settings are read-only; head-turn release, photo poses/lights and other purely visual choices remain local.

**Observed** (source): `CagedAnimalFix`, `KeepCampaignClocks`, `ArrestLiftFix` and `OffscreenOverrides` also use authoritative rules,
the compatibility digest and read-only remote settings. Campaign clocks and offscreen AI are evaluated by the server.
`CounterTalk`, `TurretAssault`, `KeepDowned` and per-skill `SquadSkillXp` rates also belong to the authoritative rules and compatibility digest.
Faction home is shared faction state; player membership and asset permissions still gate home assignment.

**Observed** (source): nullable `MaxFactionSize`, `MaxSquadSize` and `MaxSquads` overrides, plus `SeatBedFix`, `PickUpTheftFix` and
`ArrestLostFix`, belong to the same authority rule digest and remote read-only settings. Capacity overrides range from 1 to 256;
fields 1070/1071/1072 publish the effective authority limits. Creation, recruitment and squad changes use those effective limits,
including faction counts and independent squads where the operation requires them. Remote UI mods cannot change the authority clock.

**Observed** (source): `MoveResearch` (87), `SetStoragePriority` (88) and `RenameSite` (102) use typed commands. Research mutations bind
the displayed queue revision and faction; site mutations bind the canonical site/root revisions and current asset permission.
Worksite views include record, shown name and kind without granting inventory access. `Engineering`/`Attacking` use fields 2107/2108;
the private `HasMedkit`/`HasSplintKit` details use 2109/2110 only for controlled characters. Paused execution preserves these checks and
does not advance world ticks; only issuerless authority commands may use the trusted mod paused-handler path.

**Observed** (source): command 156 (`TakeOffShacklesOrder`) binds the wearer's control and membership grants independently of the current
selection. Execution rechecks the live grant, equipped shackles and open lock, including while paused. Field 2111 (`ShacklesOpen`) is
private to controlled characters; `ShacklesOff` retains its item identity and is audible only to that character's controller. `Victory`
uses the existing observable event journal. Replays cannot repeat the inventory mutation or sound.

World field 1073 (`CraftStations`) is an Economy keyed collection indexed by canonical site ID. Names come from the recipient's selected
faction and allies, then pass the existing manageable-machine filter; another player's selected faction never populates this directory.
Renaming one station changes only its entry; authorization withdrawal and older baselines obey the existing collection revision fences.

`DropFromLoot` joins the authority rule digest and remote read-only settings. Inventory pause/focus and the half-second jobs resume delay
belong to the actual player's valid inventory session; closing a forged or stale session cannot affect another controller.

**Observed** (source): world field 1074 (`ModViews`) carries mod views published for that recipient. Command 190 (`ModClientInput`)
validates its envelope and binds the authenticated issuer's control grant for the actor. Both require the current build identity.

Extra Extensions variables and saved character mod tags remain authority-owned script/save state. Dialogue preflight checks identity,
funds, permissions and recruitment capacity before item or variable effects. Visible results use existing inventory, dialogue and world
components; arbitrary variable/tag tables are not broadcast. Checkpoints preserve these values and their multi-player ownership.

**Observed** (source): character text sources receive the existing `LastingId` (field 2104, eight exact bytes) and the display call's
`ShownPlace`. Remote UI resolves names through the admitted frontend database without calling local population services. Cache answers
are scoped to character and place; assigning a smith its first lasting ID also invalidates the server's derived smith-name cache.
Refreshing a UI text source changes no authoritative character name, ID or state hash and adds no component traffic. Server-generated
notices, dialogue and shop/smith strings retain their existing text publication contract; already generated text is not retrospectively
rewritten by a client's source. This interface does not broadcast private character tags or add a per-character table of display strings.

**Verified** (`ShownTextTests`, `PlayerSessionUiTests`, `SmithViewCacheTests`): a lasting ID above 2^53 survives publisher/replica/UI round trip;
two characters and two display places keep separate answers, source refresh sends no Name/RecordId/LastingId component, withdrawal fences
an older baseline, and assigning an idle or working smith its first ID refreshes the name without requiring an explicit text refresh.

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

### Lasting identities, mod appearance and sound

**Observed** (source): observable characters publish `LastingId` in component 2104 as one exact unsigned 64-bit value and `TagHeight`
in component 2105 as one exact finite, nonnegative 32-bit float. These are independent changed-only fields, not motion payloads.
The lasting ID is a mod-facing identity across saves; it never substitutes for incarnation-scoped character IDs or control grants.
Resolved appearance descriptors include the upstream bounded pose weights. Up to four validated `LookTint` values travel separately
in binary component 2106, so tint changes do not resend meshes or poses. Exact default float bit patterns are omitted; all other values
remain float32. The field occupies at most 149 bytes before its component envelope. The replica joins appearance and tint publications
in either order within the same interest generation. Unchanged descriptors and tints add no repeated delta; neither mesh data nor
per-frame matrices travel with these fields.

Authority mod sound posts drain on the world owner. Character posts require a visible subject and a nearby position; point posts require
a nearby position; explicit interface posts reach admitted recipients as global presentation. Their event/switch names are bounded UTF-8
strings and use the existing reliable event journal, high-water acknowledgements and world identity fences. Local UI-mod sound callbacks
use the client's presentation sink. They cannot issue a network sound command or access the authoritative world, and candidate/data
preparation suppresses them. Ordinary sounds omit the absent named-post property.

Command 155 (`CageClickOrder`) carries the selected actors, occupant and displayed fixture revision. Execution rechecks the recipient's
control, faction membership, occupant and cage; forwarded pick/escape orders retain the original grants. Hints are private to the issuer.
Upstream queued dialogue, trade, body-disposal and cage-escape actions preserve command provenance on execution and restoration.
Server-origin mod orders carry internal trusted provenance, which is absent from the remote command schema. Mod-owned faction shops
remain server-only configuration; ordinary players use their attributed trade sessions and existing inventory revisions.

### Shared mod effects, glows and machine windows

**Observed** (source): snapshot collection 1068 carries authority effects, glows, trails and audio modulation. Server combat-mod
callbacks enqueue these requests on the authority. Clients use compatible particle assets and the shared request data; they do not need to
execute that server mod to display them. Local UI-mod callbacks remain local. Character/bone/weapon effects and glows require a visible
character; point effects require a nearby position; building effects include observable player, level and nest buildings. Recipient
withdrawal removes the corresponding visuals. Particle instances, simulation and per-frame bone transforms are not streamed.

Stable IDs, separately revisioned definitions/readings and absolute start/expiry times preserve renewals without sending countdowns or
unchanged anchors and names. The client deduplicates
one-shots, skips already expired initial history and clears handles on world/view replacement. The authority retains expired requests for
three seconds so short effects survive a publication boundary; clients still apply expiry at their presentation clock. A kept effect is
identified by owner, effect and anchor, so choosing a different anchor creates a different request. Shared visuals are cosmetic and unsaved.
Effect size/pulse and glow tint/through-wall mode retain exact values. Trails carry anchors and styles, with client-local sampling and a
bounded five-second retired tail; they never stream per-frame trajectory history. Audio state is keyed by mod owner, including identity
values that replace earlier modulation. Headless projectile callbacks read the live projectile source once per authority tick even when
ordinary snapshots are disabled. Vulkan consumes the effect/glow/trail render paths; upstream Metal has no equivalent visual renderer.
Authority audio state is consumed by both hosts. See [mod presentation](modding.md) for initialization, disposal and sound lifecycles.
Binary values, reliable burst staging and lossless compression are specified in [the wire contract](networking-next.md#shared-visual-collection).

Fields 1065/1066 carry the inspected canonical town-machine panel and manageable machine site. They reuse the authorized machine projection
rather than creating a second stock inventory. `ShowMachinesCommand` (185) names the recipient's zero, one or two current machine windows;
closing sends an empty list. It is a presentation hint, never a stock-access grant. Authority execution rejects unknown/unpublished sites
and projection independently rechecks current stock permission or an authorized nearby interaction. Only shown authorized windows carry
storage items and a positive storage revision; static grid size, permitted emptiness and control metadata remain available. A validated
open-container session is also scoped to that player's shown window. Changing one player's windows cannot crop another player's inventory.
Single-player host snapshots use the same shown-window optimization; multiplayer capture does not read the host's hint as a global filter.
Obsolete character `Inventory` component 2016 is retired, without reusing its wire ID.

**Verified** (2026-11-26, repository-external .NET 11 diagnostics, 40 assertions): production publisher/replica and presentation joins
preserve exact UTF-16 tags, including NUL and isolated surrogates. Same-bucket and cross-bucket retags survive repeated late baselines in
both initialized and uninitialized replicas. Inventory/definition withdrawal and a later digest-only item cannot restore old text.
Actual `World.Authority.CaptureView` projections exclude another nearby player's private inventory, distant actors and unreferenced
known definitions, while retaining the recipient's authorized item text. A full 65,536-definition table also survives an FF-to-00 retag
whose new bucket is delivered before the old bucket's withdrawal, plus a later old baseline; the final count and every item tag stay exact.
This capacity diagnostic uses eight 8192-item inventories and is separate from the ordinary-load bandwidth traces below.
These checks are separate from the repository test count and
do not establish installed-game or renderer acceptance.

### Incremental component bandwidth

**Observed** (e975a97d baseline, 2026-11-26, .NET 11 Release, repository-external deterministic diagnostics): the production `GameStatePublisher`,
`GameStateReplica`, `MotionReplicationProducer`, scheduler, bulk/control framing and reverse acknowledgements encode recipient views.
Each trace covers ten seconds after its initial baseline, world time at 30 Hz, motion at 20 Hz and full/critical captures at 10 Hz.
All 500 characters are interactive combat actors with independently scattered positions, drift and changing head targets. DrawYaw is
Yaw + 0.151527 radians, preserving the existing reversible bit predictor. Each scenario verifies 100,000 motion records against their
captured bits, 500 initial moving-transition reliable checkpoints, 100 full publications and no reduced-cadence result.

Application rates count both directions: components, critical frames, motion, checkpoints, reliable framing, bulk manifests/chunks,
commit/ready/active messages and reverse ACKs. The separate budget column adds **96 bytes per application message**, including reverse
messages. This is a planning allowance per application message, not a transport upper bound or measured QUIC/TLS/UDP/IP overhead. KiB means 1024 bytes. Initial baseline bytes
are separate from the ten-second means. The in-memory trace has no RTT, packet loss, encryption or retransmissions.

The visual workloads use actual `World.ModVisuals` APIs. They renew 64 kept effects and 256 glows at fixed anchors/targets with independently
changing colour/strength/expiry. Their source journal retains three seconds of 100 turret sounds/s (exact selector 0–3), while the sender
only emits events not already admitted to reliable delivery. The client consumes and acknowledges every half-second, checking unique IDs.

The new-feature workloads add size/pulse, alternating Light/Tint glows with 32 through-wall targets, 64 trails, two owner audio settings,
10 portable-WAV starts and 10 stop events/s, 64 distinct 1024-code-unit tags with four retags/s, eight eight-item inventories changing
quantities at 10 Hz, and 500 Attacking/10 Engineering flags changing at 1 Hz. Visual changes use the row's 4 or 10 Hz cadence. All API
requests are accepted; replica visuals, tags, quantities and new flags are checked, and all 1200 sounds are consumed once. Trail points,
bone transforms and particle instances remain local. This constructed load is an encoding diagnostic, not a typical-play traffic claim.

| Scene | Application KiB/s | With 96 B/message KiB/s | Entry baseline KiB |
| --- | ---: | ---: | ---: |
| 500 combat actors | 328.16 | 372.23 | 92.70 |
| 64 effects + 256 glows at 4 Hz + 100 turret sounds/s | 355.14 | 399.58 | 103.81 |
| New-feature workload, visual updates at 4 Hz | 399.24 | 445.94 | 183.94 |
| New-feature workload, visual updates at 10 Hz | 450.18 | 498.00 | 183.94 |

The 10 Hz row leaves only about 2.00 KiB/s (0.40%) of the modeled 500 KiB/s target. It does not establish headroom for real transport
loss/retransmissions, extra gameplay churn or arbitrary mod traffic. Compared with the 4 Hz effects/glows row, adding the listed new
features at the same visual cadence costs 44.10 KiB/s of application data and 46.35 KiB/s including the message allowance. This compares
these complete constructed loads; it is not an attribution of every byte to a single feature.

No application bytes/s limiter, cadence reduction, quantization or event dropping enforces the target. These traces omit installed Kenshi
animation/equipment churn, concurrent gameplay command families, player count scaling and impaired links. The 500 KiB/s/player average
optimization target requires a separate installed-game wire measurement, including cold sync, stable play, reconnect and retransmission.

#### Crafting and interaction increment

**Verified** (2026-11-27 UTC+08, .NET 11 Release, repository-external production codec/publisher/replica diagnostic): a separate ten-second
fixture uses 500 combat actors, 30 Hz source ticks, 20 Hz motion and 10 Hz full/critical capture. It counts the same application lanes,
bulk handshake and reverse ACKs, with the separate 96 B/message allowance described above. Each scene preserves 100,000 bit-exact motion
records, 500 acknowledged checkpoints and 100 full publications, with zero reduced-cadence results. An additional 100 static captures
emit no component changes, including the station directory, shackle flag and authority rules.

| Paired change | Additional application KiB/s | Additional KiB/s with 96 B/message |
| --- | ---: | ---: |
| 128 stations, one rename/s, whole-list representation | 0.7694 | 0.7694 |
| Same activity, keyed station entries | 0.1177 | 0.1177 |
| Ten characters' shackle state changes/s | 0.5647 | 0.5647 |
| Ten new sounds/s over 100 existing turret sounds/s | 0.4395 | 0.4395 |
| Existing sound background plus station rename, ten shackle changes and ten new sounds/s | 1.0973 | 1.0973 |

Keyed station entries reduce this rename increment by 84.7%; 64- and 128-entry directories have the same steady rename cost. The combined
fixture delivers all 1,100 sounds exactly once, retaining the new sound kinds and shackle item. Its complete rate is 280.7000 application
KiB/s, or 320.5250 with the message allowance; entry baseline is 96.302 KiB. It omits the older trace's mod visuals, tags and inventory
churn, so it neither reruns the 498.00 KiB/s row nor establishes a new total by adding its increment to that row. Starting new sounds from
an otherwise silent fixture adds event-component overhead; that combined increment is 2.2195 KiB/s. Neither fixture bounds arbitrary play.

**Verified** (the same diagnostic, command round trips): one `TakeOffShacklesOrder` with a role grant is 253 B JSON / 279 B framed reliable
application data / 375 B with the allowance. A four-piece `PlaceBuildings` is 816/842/938 B; adding one `WallLineIntent` makes it
984/1010/1106 B, an increment of 168 B per confirmation. These samples exclude receipts and vary with field lengths. Wall-line intent is
command-only and adds no steady snapshot traffic. No application limiter, precision loss, cadence reduction or event dropping was added.

### Item actions and shared stock

**Observed** (source): command 160 carries an integer stack `Count`; the decoder rejects nonpositive counts, and execution retains the
current actor grant, source revision, item token and held-session checks. The source-compatible whole-stack constructor uses the token's
quantity. Planned buildings carry `Outside` through the existing placement command. Door repair jobs retain the selected actors and bind the
displayed door revision. Field 1067 publishes bounded immutable `ModJobOffers`; custom task codes travel through the existing `AddJob`
command, whose handler resolves the registered offer and rechecks current site, membership, control and work permissions.


**Observed** (source): mod item-use handlers run only on the authority, with the issuing player's current character grant, inventory
revision and item identity. Each inventory or trade item carries an optional 32-character `ModTagDigest` instead of its raw mod
tag in its changing item fields; empty tags send no property. It is the first 128 bits of SHA-256 over exact UTF-16 code units, used alongside inventory revisions
and actor grants. The authority and saves retain the complete tag, and retagging
advances inventory revisions. Tokens created by local windows retain raw tags; remote tokens echo the digest and cannot combine it with
a different raw tag. Nullable stolen-owner labels are omitted when absent.

Field 1069 carries the recipient's referenced tag definitions separately. Only tags from the already authorized character, ground,
held, trade, barter, craft and open-machine views enter it. Quantity, charge and stock changes therefore reuse unchanged definitions.
The presentation join restores exact UTF-16 text, including isolated surrogate code units; missing or withdrawn definitions produce
empty text, never an older item's tag. Definition-table changes invalidate the join cache, while command tokens retain only the digest.
The independently revisioned 256 digest-prefix buckets prevent late baselines from reviving withdrawn definitions and avoid sending a
whole digest table on each retag. Bounds and encoding are specified in [the wire contract](networking-next.md#shared-item-tag-definitions).

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
