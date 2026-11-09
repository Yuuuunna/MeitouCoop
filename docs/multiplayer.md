# Multiplayer authority contracts

Engine contracts, not claims about original Kenshi behaviour. Single-player and multiplayer share these
boundaries; full multiplayer acceptance and admission UI remain unfinished.

## Players and faction resources

`World.Factions` owns each indexed faction's funds, name and research. `PlayerState` references its
current faction; changing membership does not transfer resources. `World.Player` remains the local
compatibility entry point; `World.Research` aliases that player's faction research.
`World.Players` registers independent `PlayerId`s and per-player interface state. Membership grants
no control or spending rights. Server setup grants explicit `FactionPermission`s and character control.
Management permission alone does not control a character. Transfer replaces a revisioned grant;
old grants stay invalid even if control later returns to the original owner.
Explicit-control mode stays enabled after membership removal. A faction's `PlayerOwned` classification
and resources survive removal of its last member; leaving never re-enables implicit local control.

`FactionRelations.RegisterFaction` appends a runtime identity without changing existing indices,
relations, wars, ranks or trust. New pairs use content defaults, not a template's live diplomacy.
`PopulationData.RegisterFaction` updates population lookups at the same exclusive boundary.
`FactionTemplate` resolves appearance/equipment content independently of runtime identity.
Registration requires all readers/background population work to be stopped at the boundary.
The single-player `PlayerFaction` relation alias still exists; multiplayer must use ordinary rows.

**Verified** by `WorldPlayersTests`, `RuntimeFactionTests`: resource isolation, explicit permission,
stale grants, faction catalogue growth, independent initial diplomacy and template lookup (synthetic data).

## Attributed commands and faction rules

`CommandQueue.Enqueue(PlayerId, SimCommand)` is a trusted host API, not authentication. It stamps the
issuer and captures character-control or faction-membership grant revisions. Execution rejects grants
revoked and later restored. Attributed character orders must list their characters; an invalid member
rejects the whole group, and an empty list cannot borrow local selection. `PlayerCommandRouting` fails
closed for commands whose handlers have not been migrated. Currently supported: selection, movement,
follow, stop, attack/shoot, stance/speed, queue/cancel research, trade windows/buy/sell/stock arrangement,
own-inventory arrangement/right-click/atomic transfer, map reading, talk/choice/end, character-trade confirm/close,
squad management, faction naming/formation, personal squad-AI options, container open/close, machine/container take/put,
craft queue/configuration, machine power/furnace controls, job remove/reorder, character-order jobs,
machine operation/turret orders, supported targeted production jobs, existing-site construction/upgrade/
demolition/public-state commands, faction-scoped town edits, validated placement and property purchase.
Legacy/internal commands retain their local path.
The explicit wire decoder and authenticated-handle bridge are documented in [networking](networking.md).
Account/TLS connection handling is defined in [server contracts](server.md); the graphical remote client session remains unwired.

`World.Authority` is the trusted-host gateway after authentication. Opaque connections bind a player
and world incarnation; opening a replacement invalidates the old connection, including queued requests.
Admission is Preparing -> Synchronizing -> Active, advanced only by the host. A valid loaded living
control grant is required; missing/unloaded roles return to Preparing without granting creation rights.
`Open`, admission transitions and processing require the serialized world owner; Submit/Close are thread-safe.

Typed requests freeze their payload and displayed control/membership grants. Execution checks current
connection, role, grant revision and target faction again. Client Tick is ignored. Each request passes
all input handlers before the next request, with its own committed character buffer. Runtime ticks,
paused interfaces and explicit Synchronize drain requests; replacement/disposal closes old waiters.
Exceptions fault the authority and runtime; they do not imply rollback of a partially executed handler.

Monotonic request numbers are per player across reconnects within the world. Same-number/same-payload
replays return the original receipt; different payloads conflict. The latest 128 completed receipts per
player are retained; older numbers below the high-water mark expire and never execute again. Queue limits
are 256 pending requests, 8 MiB of command payloads and 1 MiB per command; refusal consumes no number.
Operation order and snapshot `StateRevision` advance independently of Tick. `Evaluated` confirms handler
evaluation, not gameplay success; authoritative state/scoped feedback provide the business outcome.
Ledger/revisions remain in memory: cross-restart deduplication and durable creation receipts are pending.
**Verified** by `AuthorityGatewayTests` and `TradeTests.AuthorityPurchaseReceiptSurvivesReconnectWithoutChargingOrTransferringTwice`:
replacement, paused ordering, frozen grants/payloads, bounded replay, faults and actual purchase conservation.

Research queue/cost/prerequisites, bench level, contribution falloff, unlocks and machine improvements
use the owning faction. Implicit storage/hauling/seat/incinerator searches use the worker/site owner.
Faction research commands explicitly name their faction and require `ManageAssets` at execution.
Research/crafting views key their caches by faction state. Unported interface commands remain gated.

Machine configuration commands require the actual owner's `ManageAssets` grant and a displayed control
revision. Queue identity/order, repeat, power and furnace toggle changes advance it; work progress does not.
Old queue-index cancellations and replayed toggle requests are rejected, including while paused.
Job removal/reorder requires the role's control grant and displayed list revision; equal-looking replacement
rows still change that revision. Native craft/job rows carry their displayed target and revision, so switching
selection or changing a list during dragging cannot redirect the operation. Character-order job additions
accept explicit controlled roles and the supported player order types.

Operation/turret orders and targeted machine/storage/training jobs bind the displayed work-site revision,
the owner's membership revision and explicit character grants. Site replacement, ownership, record,
completion, destruction or upgrade changes invalidate displayed targets; progress alone does not.
Building/machine/resource views publish these revisions. Natural targets still need legitimate region/content
resolution; their eventual remote discovery/admission path is not implemented.

Player work rechecks the current controller's asset rights during scheduling and before production/hauling
mutations, including both ends of a hauling trip. Revocation stops work without deleting cargo or job lists;
an authorized controller transfer preserves work. Offline controllers retain grants. Locked/closed targets
are excluded from production trips. Delegated mining preserves existing ownership; only unowned natural
sites are claimed. NPC and legacy work retain their existing paths. Construction-job addition still needs integration.

**Verified** by `ProductionTests`: foreign/regranted management permissions, replaced machines, independent
craft queues, duplicate queue/toggle requests, stale same-option rows, progress-safe cancellation, furnace
ownership and stale/revoked role-list edits. These tests do not establish all background-job permissions.
Further `ProductionTests` cover foreign/stale work targets, same-id site replacement, permission loss
between scheduling and production, authorized transfer, job resumption, cargo preservation after destination
ownership changes and delegated mining ownership. They do not prove all construction/carrying/turret behaviour.

Existing-site construction commands bind the clicked site and forwarded wall root revisions, owner
membership and explicit worker grants. Builders recheck asset rights in scheduling and before consuming
materials; automatic sources belong to the target site faction, and fetching rechecks ownership/locks.
Wall attachment/forwarding does not cross factions; demolition does not delete another owner's furniture
or refund another owner's attached pieces. Upgrade unlocks use the building owner. Native site controls
retain the displayed revisions across confirmation dialogs.

Placement accepts an explicit faction with its own research and outposts; nearby factions do not share
a newly assigned town. `WorldContent` supplies `IConstructionPlacement` through Runtime's `WorldPlacement`;
attributed placement without a validator is rejected. Preview and authority share `PlacementCheck`.
Authority uses live sites/roles, complete CPU footprints, resource readings and owned-parent floor surfaces;
missing declared collision cannot count as empty space. Parent references are canonical world keys.
Transforms, world bounds, floors 0..3 and the existing preview's 1000-unit height adjustment are bounded.
Requests contain at most 2048 plans, checked sequentially against earlier committed sites. Current policy
requires a controlled role within 5000 units or the faction's outpost radius, and ready navigation at the
centre/footprint corners. Owned indoor parents bypass the foreign-town exclusion, not ownership checks.
Town name/public edits require faction permission and displayed revision. Multiplayer power grids use
town and owner together, so unassigned town number 0 never shares electricity between factions.

**Verified** by `PlacementAuthorityTests`: competing/duplicate placement, live blockers, missing geometry,
unready footprint corners, parent ownership/floors/completion, resources, slope, transforms, observation
range and revoked grants. The install-backed `HeadlessContentTests` loads real content, prepares a second
navigation region and places an exterior building without GPU initialization. Placement still inherits
the engine's unresolved snap/use-node and surface-rule limitations; these checks do not prove all indoor,
ceiling, wall-joint or mod-content placement. Cold collision loading is synchronous and needs integration
with region preparation; request-id deduplication is still separate unfinished work.

**Verified** by `ConstructionTests`: foreign/regranted rights, stale wall roots, cross-faction links,
furniture preservation, owner-specific upgrades, interrupted material consumption, delegated ground
sources, separate trusted-placement outposts and versioned town edits. `ProductionTests` verifies separate
power for different owners at town 0; existing construction/house-repair tests remain applicable.

Core bounty credits, bail payments and automatic fines use the acting/charged character's faction funds.
An overflowing bounty credit leaves the claim available. Different player factions are not exempt from
assault, theft or kidnapping merely because both roles have `IsPlayer`.
**Verified** (`CrimeTests.PlayersInNewFactionsCanAttackEachOther` and
`CrimeFactionQueriesFollowRuntimeRegistrations`): crime queries use the live faction catalogue;
factions registered after system initialization support assault and bounty lookup in both player directions.

## Squads and automatic behaviour

Squad commands name a faction and capture its membership revision. Membership permits viewing;
`ManageSquads` permits creation, rename, ordering, empty-squad removal and formation changes.
Moving/dismissing members requires `AssignCharacters` plus their current captured control grants.
A swap requires both grants and the exact displayed counterpart; a shifted drop past the last member
cannot bypass capacity. Native controls send the faction and swap counterpart.

Dismissal preserves the entity, position and inventory, removes control, and moves the character to an
NPC squad. Its faction must retain another living member. Each player faction has its own death archive;
death retains ownership records for future admission classification. Selection fallback uses only owned
living roles. Faction naming requires `ManageFaction` and checks other factions' effective runtime names.

Automatic healing/help/rescue, food consumption/storage searches and turret shoot-first use the role's
controller settings. Offline controllers retain settings; unassigned roles use defaults. Cross-controller
food sharing requires both players' sharing option and the same faction/squad. Transfers change which
controller supplies options. Personal AI-option commands accept only defined individual flags.

**Verified** by `SquadAuthorityTests` and `WorldPlayersTests`: operation without a local faction,
foreign-faction/stale-grant rejection, swap ownership/target changes, capacity, dismissal/recruitment,
separate death archives, personal food-sharing opt-in/transfer, effective-name conflicts, runtime formation
growth and persistent explicit-control mode (synthetic worlds). Existing quick-save/golden tests pass.
Offline role persistence and client replica views are still pending; these tests do not cover every
automatic behaviour or content-driven squad transition.

## Dialogue ownership

`DialogueSystem` keeps world-wide line-use records, rolls, NPC event counters and speech, with separate
player conversations, approach state, bail lists, editor requests and character-trade windows. An NPC
conversation excludes other players' conversations/trades. Closing one player's interaction preserves
another subsystem's NPC hold. Choices bind to both session id and choice revision, including while paused;
native controls echo the identifiers of the displayed choices. Character-trade confirmations bind to window id.

Dialogue conditions, payments, research checks, faction-name tokens, guild ranks and recruits use the
talker's bound faction, independently of the viewed faction. Funds visibility and spending are separate
permissions. Recruitment requires faction management and available faction/squad capacity; preflight runs
before locks, item grants and payments. Recruits receive control grants for the initiating player.
Character trades recheck target eligibility, range, current authority and payer capacity; offered player
roles also retain their control-grant revisions. The same freed slave/animal cannot be bought twice.

Long-term player memory and temporary alliance flags are keyed by NPC squad and player faction in
multiplayer. Legacy player-memory fallback applies only to the imported local faction. In-memory squad
unload/restore preserves the faction keys. Faction ranks and faction memory are hashed, but their disk
extension and migration from original single-player ranks remain part of unfinished persistence work.

**Verified** by `DialogueTests`: independent funds/windows, shared NPC exclusion, stale choices, paused
choices, permission revocation, hidden balances, guild-rank/memory separation, recruitment ownership and
capacity, and two buyers attempting to free the same slave. Existing single-player/golden scenarios pass.
These tests do not prove all content-driven dialogue actions or character-editor authority integration.

## Inventory and trade interactions

`TradeSystem` owns one stock/restock/market service with separate player interactions. Each window binds
its customer control grant, membership revision and actual customer faction; changing the viewed faction
does not change the payer. `TradeCommand.SessionId` binds actions to the displayed window. Attributed
commands cannot use legacy windows, and stale/revoked windows cannot spend. Merchant holds are shared:
closing one window does not release a merchant still serving another. Buyback receipts belong only to
their originating interaction. `CloseForPlayer` is the teardown hook for future connection handling.

`InventoryCommand.InventoryRevision` binds an operation to its displayed character/source inventory. Revisions
detect replacement with identical visible tokens and advance independently of simulation ticks; they
are process/world-session metadata, not persistent item identities. Native trade UI echoes the window
and inventory revisions. Inventory arrangement, right-click and map reading also require the issuing
player's current captured control grant. Cash/blueprints use the character's actual faction; map knowledge
and operation notices target the issuing player.

`TransferInventoryItem` commits a drag between controlled character inventories without a server-held
cursor item. It requires both control grants, both displayed inventory revisions and range; split/merge
or an explicitly requested swap commits only after both destination layouts pass validation. Refusal
leaves items in their source inventories. The client must refresh after a stale revision; this does not
replace request-id deduplication. Native drag/drop uses the player-scoped custody commands described below.

Purchases check spending permission; private balances require read permission. Recipient balance capacity
limits transfers before items move. The world commits competing requests serially against its one stock.
Trade/blueprint/map notices use per-player queues; other legacy notices still require recipient migration.

**Verified** by `TradeTests`: competing buyers, isolated windows, merchant holds, old-session rejection,
permission revocation, hidden balances, account overflow, range, viewed-faction changes, buyback isolation,
identical-looking restock and transactions while paused. `InventoryActorTests` checks foreign-role/stale
inventory rejection, cash overflow, faction-specific blueprints, private maps and stance validation.
It also checks atomic transfers: both-role authority, revoked grants, range, stale destination, repeated
split, charge conservation, failed/successful swaps, stack limits, container-cycle rejection, locked/disabled
equipment slots and prosthetic-body consistency on successful/refused moves.
The default single-player trade, held-item and golden scenarios remain covered by existing tests.

Container open/approach state is per player. Sessions bind the opener's grant, membership revisions,
fixture instance and owner; lock, consciousness, capture/carry state, range and floor are checked again.
An attributed close names the displayed session, so an old close cannot dismiss a reopened window.
Members need `ManageAssets` for their faction's containers; delegated access to another member faction
does not count as theft. Foreign containers retain gameplay theft rules; unowned containers need no
faction permission. `PublishContainer(PlayerId)` returns only that player's authorized open view.

Take/put require both character inventory and storage revisions. Machine revisions include slot identity,
record/count, storage and crafted-item identity, and closure; ordinary production progress does not
invalidate them. A replacement machine gets a new revision even with identical contents. Positioned puts
respect the requested count. Right-click transfers retain their documented partial-capacity behaviour.
Native container/machine controls echo the displayed revisions/session; character inventory views carry revisions.

**Verified** by `ContainerAuthorityTests`: independent windows, stale closes, competing takes, identical
item/machine replacement, membership and control revocation, locked/missing sessions, removed fixtures,
floor-separated approaches, delegated access without theft, requested put counts, slot-count changes and
charge conservation when right-clicking part of a used stack.
Held-item drag UI remains unmigrated. The network gateway deduplicates migrated commands and connection
teardown closes only the current player's transient windows; held-item custody still needs its own integration.

`WorldStates` publishes immutable unique-state and diplomatic snapshots. Global facts need no online
players. Player-dependent conditions require one faction to satisfy all conditions. Unique deaths and
captures record involved factions in multiplayer; a legacy boolean applies only to the imported faction.
Provenance is cleared on release and is not yet written to the multiplayer save extension.

**Verified** (synthetic worlds): `PlayerCommandTests` covers simultaneous separate selections/movement,
foreign-role rejection and queued-grant revocation. `ProductionTests` covers isolated materials, research
falloff, blueprint/crafting views and building-owner upgrades. `MultiplayerWorldStateTests` rejects mixed
faction witnesses and checks worker snapshot isolation. `JailTests` covers account isolation, bail rates,
claim replay and overflow; `CrimeTests` checks cross-player assault/theft. Existing golden hashes remain
unchanged. These cases do not prove all gameplay command handlers are multiplayer-safe.

## Character identity and unloaded roles

`CharacterId` is a loaded slot/generation. `EntityId` is a persistent identity within one world;
network references must additionally identify the world incarnation. `World.Entities.Track` allocates
an identity when a character is registered. `CharacterCold.Entity` survives storing/restoring the same
cold state; `CharacterTable` binds it to its current slot and rejects duplicate live bindings.
Reserve every saved identity, including unloaded ones, before allocating new identities on load.

Control grants refer to entity identity and faction. `KnownRoles` includes unloaded/dead roles;
`Validate` requires a loaded living character and current membership. Missing loaded state never means
the account has no roles. Revoking membership removes that faction's grants even while unloaded.
`CharacterTable.Current` and identity resolution use Next within a tick and the committed buffer outside
it. Boundary spawning, removal and placement preserve the latest slot generation; recently committed
roles do not disappear or alias an older generation. `WorldPlayersTests` exercises these transitions.

**Verified** by `WorldPlayersTests`: unload/rebind, slot reuse, death, membership revocation, reserved
identity allocation, duplicate live identity rejection and selection isolation (synthetic worlds).
Disk identity/grant persistence is covered under [persistence boundaries](#persistence-boundaries).

## Authorized world views

`AuthorityGateway.CaptureView` runs on the serialized world owner for a current Synchronizing/Active
connection. Preparing, replaced, faulted and closed sessions receive no world view. Losing the last living
role returns the session to Preparing; an uncommitted durable creation also blocks publication.
Each result carries world/player/connection generation and a strictly increasing per-connection Publication,
independent of Tick/StateRevision (permissions/subscriptions can change while paused).
Network publication ACKs open control through ActivateAfterView, which rechecks the captured role/membership
stamp. Changed grants/availability require a new baseline; old connection publications never activate a replacement.

Observation uses the union of living controlled roles' horizontal radius (server default one zone, maximum
two); carried roles use the carrier's position. This is an engine area-observation rule, not NPC stealth/sight
emulation. Dead roles provide no observation anchor. An optional filter of at most 256 valid zones can only
reduce observation and never activates simulation/navigation. Loaded owned roles remain available for roster
details outside the filter, including corpses; unloaded identity/grant records remain in the role list.

Public characters/machines use explicit field allowlists. Private inventory, jobs and path details require
ownership or the relevant authorized interaction; same-faction membership is insufficient. Machine stocks, crafting options,
site materials and remote asset management require ManageAssets. Nearby machine/farm presentation remains
public. Balances require ReadFunds, independently of SpendFunds. Membership permits faction research views;
ManageSquads permits the faction roster, otherwise squad lists contain only owned members.
Trade/dialogue/container views revalidate their interaction grants at capture, including membership revisions;
cached windows cannot survive revocation/regrant merely because the character has the same owner again.
Exploration and interface selection come from the recipient, never World.Player. Parallel character projection
consumes serially captured inventory/job revision stamps. Legacy publication uses the same character builder.

**Observed** (source review): inspecting a visible NPC or another player's character publishes the recipient's
`Inspected` flag and information/medical-panel fields, including stats, status and health parts. It preserves
own selection without granting control, inventory, jobs or paths. `BodySystem` refreshes extended stats for
every player's inspected target. Visibility filters still apply, and clearing inspection removes these fields.

**Verified** by `PlayerWorldViewTests` and the authorized-publication case in `TradeTests`: independent private
views/balances, same-faction separation, permission loss/transfer, bounded observation without activation,
dead/carried roles, preparation/reconnect gates, machine stock redaction, stale trade exclusion and stable
revision stamps across 601 owned roles. [Wire replication](networking.md#state-replication) consumes these views.
These checks do not establish in-game graphical inspection or every loot workflow.

## Shared runtime

`World.Regions` captures distinct role/activity/preparation contributions and their union. Multiplayer
population activation no longer requires the local player or follows a camera. Living player roles
(including carried/captured ones), combat, active work destinations and active player-owned bases retain
their regions. A base keeps its same-owner/town support buildings. Overlapping demands do not subtract
one another; no remaining living role allows the existing simulation linger/unload rules to expire.
Dead/unloaded ownership records are not deleted by region expiry.

Host preparation uses disposable, world-bound leases. Navigation-only leases load geometry without
activating population or revealing map knowledge; old-world leases cannot pin a replacement world.
These are trusted host APIs, not authentication or a client camera request. Stored-role admission uses the
[persistent role locator](server.md#stored-role-recovery) to select a legitimate lease area.

`WorldRegionNavigation` is shared by both hosts. It retains simulation demand, route hints, preparation
and still-loaded living NPCs, then unloads navigation after 30 idle real seconds by default. NPC navigation
retention does not itself reactivate population, avoiding an unload feedback loop. Graphical frames no
longer independently unload navigation. Paused runtime calls process permitted commands/services without
advancing ticks. Legacy simulation keeps its original camera activation and off-screen policy.

Town/camp discovery, discovered notices and factions met use each player's controlled roles and creation
state; membership alone shares no exploration. **Verified** by `RegionRetentionTests` and
`MultiplayerRegionTests`: overlap, paused expiry, carrier/combat/base demand, lease disposal/replacement,
remote-only population, unchanged resident identities, private same-faction discovery, camera exclusion
and death without lost ownership. Tests do not prove network subscriptions, all campaign demand, or scale.

`Meitou.Runtime.WorldRuntime` owns/disposes the world and its systems. `WorldSession` delegates normal
and tick-ahead execution plus world replacement to it. Dedicated `Advance` retains tick debt beyond its
per-call budget; pause adds/drains no debt.
The graphical host retains its existing presentation clock and frame budget. Failed ticks prohibit
further runtime execution until replacement; failed replacement setup preserves the previous world.

`IRuntimeWorldService` binds external state only to the active runtime. Input/tick boundaries and paused
edits synchronize it without requiring snapshots or render frames. Candidates can tick during setup
without publishing overlays. Replacement suspends old services, activates new ones and restores the old
services if activation fails; failed suspensions remain pending until cleanup succeeds. Paused-boundary
failures fault the runtime just like failed ticks. Disposal unsubscribes and clears external state.

`WorldBuildings` owns the navigation overlay for both hosts. Path mode, completion/build threshold,
record changes, removal and destroyed level overrides determine its contents. The graphical host now
updates only visual buildings/lamps/effects. A moved overlay invalidates both old and new regions.
Navigation disposal waits for any running coarse-obstacle update; queued updates skip disposed content.
**Verified** by `WorldBuildingsTests`/`WorldRuntimeTests`: paused edits without snapshots, lifecycle changes,
candidate isolation, partial activation/cleanup failure recovery and faulted-boundary handling. The
install-backed headless test checks additions/removals against the real `NavSystem.Runtime` overlay.
`WorldNavigation` commits prepared meshes, unloads, kept-region expansion and coarse-obstacle updates at
active-world boundaries. Runtime navigation uses deferred publication; standalone navigation tools retain
their immediate mode. `PrepareZone` means generated, `LoadZone` means published. Loading hosts prepare,
synchronize and check readiness; route hints request needed zones without making them walkable.
Each build uses a frozen runtime-overlay snapshot for both cache hashing and geometry. Rebuild/unload/world
incarnation changes reject stale results and stale failures. Current failures remain cached until explicit
rebuild, unload/reload or world replacement, preventing per-frame retry loops. Old-world seed requests and
route-loading hints cannot affect the new incarnation.

Missing authoritative navigation returns a waiting path. Orders retain their goal and follow only the
verified prefix, then stand and retry; unloaded floors preserve the actor's height. Invalidation retires
earlier path-query ids so the legacy older-answer optimization cannot revive stale geometry. Runtime
services request seed expansion for player roles standing in pruned regions after publication.
**Verified** by `NavPublicationTests` and `NavigationWaitingTests`: controlled publication/unload, overlay
snapshots, stale build/error rejection, replacement, queued seed expansion, explicit failure retry,
retained orders, bounded retry frequency, verified-prefix stopping and stale path-answer rejection.
The real-content headless test passes through deferred preparation/commit. These tests do not establish
multi-client region retention, all navigation failure UI, capacity or cross-machine replay timing.

`WorldContent` is the shared CPU assembly path used by GameHost and `HeadlessWorld`: navigation,
ragdoll collision, town homes/lights/rooms, resource nodes, rigs and animation libraries. `GameAssets`
owns file/mesh resolution in Data; Rendering's `AssetLocator` is a compatibility name. Missing required
world geography fails loading instead of silently removing wild squads/resources. `TerrainSource` reads
full-resolution tiles through a bounded synchronized cache, independent of any camera's loaded patch.
`WorldGateRegions` captures navigation/overlay inputs, computes labels off-thread and publishes at runtime
boundaries. Changed geometry invalidates old labels immediately; pending labels do not permit fortification
checks. Obsolete jobs cannot publish into replacements, and current build failures fault the runtime.

`WeatherSchedule` advances regional schedules without a camera. `SimulationWeather` runs before gameplay
systems: regional weather plus affecting non-camera groups and fixed map effect nodes use CPU simulation.
GameHost supplies it through the same `WorldContent.BuildSystems` as the headless host. Rendering receives
copies of regional schedules, so drawing/camera time does not advance authority. Weather nodes use game
tick time (engine choice); [checkpoints](server.md) preserve node RNG/lifetime/particle tails and
[replication](networking.md#regional-weather) supplies authoritative roots to presentation.
Camera-anchored particle groups are presentation only here; modded camera groups with local gas/fire
affects need an explicit multiplayer rule before claiming full effect compatibility.

**Verified** by `WorldRuntimeTests`: retained debt, pause, failure recovery, candidate disposal/navigation
isolation and matching single-player/runtime hashes including tick-ahead. `HeadlessContentTests` checks
parallel terrain tile eviction against the source heightmap. Its install-backed slow case loads actual
content, prepares the start's 3 x 3 navigation region, spawns a start squad and ticks all standard systems
without GPU initialization. `WeatherScheduleTests` checks minute catch-up, fixed gas nodes without a
viewer, and authoritative schedule isolation from presentation. These are not multiplayer acceptance tests.

## Persistence boundaries

`EntitySave` writes character identities into GAMESTATE_CHARACTER and a versioned allocator registry
into CAMERA. It reserves all known identities before loading any role, including unloaded/dead records
and identities whose records were removed. Slot reuse never changes persistent identity; newly tracked
saved NPCs retain their identity across unload/reload. Duplicate, unreserved, zero or malformed identities
and unsupported registry versions fail before loading world state. Saves without extensions remain valid.
**Verified** by `SaveWorldTests` identity cases: disk round trip with different slots, allocator high-water,
offline reservation, original NPC unload/recapture/reload, corrupt registries and legacy loading.

`Runtime.CheckpointStore` writes detached world files and versioned metadata into one immutable bundle.
A manifest checks every file's size/hash; a same-directory atomic pointer rename publishes the bundle
only after data is flushed and the saved world parses without missing-content problems. A filesystem lock
serializes writers; readers retain old bundles. Interrupted pending/sealed bundles are never auto-selected.
Corruption fails explicitly instead of silently reverting a committed creation. No automatic pruning yet.
**Verified** by `CheckpointStoreTests`: paired disk round trips, injected interruptions before publication,
reader/writer overlap, competing-writer exclusion, corruption/extra-file/path rejection. This is a local
filesystem process-interruption contract, not a guarantee against hardware/power-loss filesystem failures.
The metadata container checks JSON/schema-version presence; schema validation belongs to its consumer.

`PlayerRegistrySave` captures/restores the authority and faction-resource portion of checkpoint metadata.
Schema 9/authority schema 4 persist account bindings, retired-role state and `PlayerRoleHistory`: NeverAssigned, Assigned or Unknown. Initial grants
and transfers mark the recipient Assigned; revocation, dismissal, membership removal and death never clear
it. Schema-2 metadata migrates players with surviving grants to Assigned and all others to Unknown, since
absence of a current grant cannot prove that no role ever existed. Current metadata requires the history
field and rejects grants paired with a NeverAssigned/Unknown history.
Account subjects are distinct from player names; older metadata keeps players unbound and rejects injected
bindings. Binding/credential ownership and persistence gates are defined in [server contracts](server.md).
`WorldPlayers.AssessEntry` separates legacy worlds, new players without memberships, membership/history
without usable roles, unresolved stored grants, and loaded living roles. Incapacitation, captivity and
carrying do not erase living-role ownership; pending saved carry links wait for dependency restoration.
Stored/dead-archive records use the persistent role locator; unknown records never imply first-time entry. NewPlayer is a routing result,
not a creation entitlement or permission to award start resources. The gateway exposes this assessment
only for its current authenticated connection, including Preparing sessions; replaced/closed handles
cannot query it. The query runs on the serialized world owner and exposes no world positions or inventories.
**Verified** by `PlayerAdmissionHistoryTests`, metadata migration cases in `SaveWorldTests` and the revoked
role checkpoint test: sticky history, transfer/revocation, membership-only waiting, unchanged incapacitated/
captured/carried entry, dead/unresolved handling, conservative migration and actual restart after revocation.
It records player IDs, explicit-control mode, memberships/permission bits, current control grants (including
unloaded/dead roles), the grant-revision high-water and sticky player-faction classification. Restoration
requires an unused authority registry and no prior connection history. All references, permissions,
revisions and loaded-role factions are validated before authority changes; revoked revisions cannot be
reissued after restore. A retained dead/unloaded role grants neither active control nor creation eligibility.
Faction resources preserve 64-bit funds, names, guild ranks, completed/queued/paid/partial research and
research version independently of the legacy CAMERA money/RESEARCH aliases. Research content and queues
must reconstruct without loss. Metadata must match the world's faction-ID order and entity reservations.
**Verified** by `PlayerRegistryPersistenceTests` and checkpoint cases in `SaveWorldTests`: actual paired disk
restoration, stale grants, member-without-role admission, dead/unloaded ownership, resource isolation above
the legacy money range, detached capture and rejection without partial authority/resource restoration.
Metadata also carries `FactionCatalogueState` and raw `FactionDiplomacyState`. `PrepareCatalogue`
runs on exclusive candidate data before `SaveLoader.Load`: it validates the whole definition list before
appending missing runtime factions in their saved order. Static content IDs/order must match; runtime
identities resolve to static templates, including factions originally created from another runtime faction.
An already-matching catalogue is reusable; conflicts, missing templates and reordered content fail.
After the legacy loader, metadata restoration replaces the directed relation/coexistence/war tables,
global trust, legacy ranks, player-row alias setting and change counter without applying that alias while
copying. Array dimensions, finite values, identity order and self-war flags are checked before mutation.
**Verified** by `FactionCataloguePersistenceTests` and the dynamic-catalogue checkpoint case in `SaveWorldTests`:
fresh-content reconstruction, cached-index invalidation, repeated preparation, rejection without partial
registration, template flattening and asymmetric diplomacy preservation. The real-content headless test
also captures this metadata; this does not yet prove full multi-faction world restoration.
`Squad.PlayerManaged` distinguishes player rosters from same-faction NPC squads in explicit-control mode.
Starts, player squad creation, death archives and player-platoon loading set it; membership and a control
grant alone do not claim an NPC squad. Management, active-squad selection and recruitment capacity use this
classification. Entering explicit mode preserves existing player-managed factions even before assigning
their roles to an account.

Multiplayer world saves mark their format and every managed platoon, retain each squad's actual faction,
per-faction ordering, persistent squad ID and allocator high-water, and write each character's actual owner,
player-role flag and death flag. Empty squads and dead archives survive recapture; loaded controlled roles
without a saved record cause capture to fail. Same-faction NPCs stay ordinary saved population. Loader
options take the complete matching metadata and derive player-faction IDs; a marked world cannot load through the bare
single-player path or the single-player import filter. Legacy unmarked saves retain their existing loader.
**Verified** by `SaveWorldTests.ThreePlayerFactionsRestoreTheirOwnSquadsRolesAndItemsWithoutClaimingSameFactionNpcs`
and related tests: three factions, stable squad/entity IDs, positions, items, orders, empty/dead rosters,
same-faction NPC exclusion, recapture, allocator non-reuse, missing metadata and duplicate squad rejection.
`SquadAuthorityTests` covers management permission not claiming NPC squads; pinned single-player hashes pass.
When recruitment discards a saved NPC's source link, capture removes that old platoon's character record
and off-screen storage retires its member entry. The existing entity is written only in its new roster;
`SaveWorldTests` checks recapture and retirement after the saved-NPC identity/unload scenario.
`BuildingSave` has a multiplayer path that writes each constructed site's/outpost's actual faction.
Explicit constructed-building/outpost markers distinguish player-created assets from generated NPC
interiors, including those owned by a faction with player members. Global outpost numbers survive record
reordering; parent links, materials, damage progress, upgrade/ruin flags and per-zone building allocation
high-water survive restoration. Removing a constructed site removes its saved building/inventory records
without removing another owner's sites or unmarked NPC interiors. The first conversion from an unmarked
single-player save also removes dismantled assets of that imported player faction.
`SaveLoadOptions.MultiplayerMetadata` restores faction resources before asset initialization; machine
loading includes every restored constructed owner, retaining inventories, work queues and power state.
**Verified** by `ProductionTests.MultiplayerCheckpointRestoresOutpostsBuildingsAndMachineContentsForEveryOwner`
and invalid-asset cases: paired disk round trip, separate owners/stocks, empty and reordered outposts,
damaged/upgrading sites, parents, crafting/input/output/battery state, allocation reservations, demolition,
unmarked NPC preservation and unknown owner/content/town or missing allocation rejection.
Property purchase names the buyer, paying faction, displayed property revision and quoted price. At
execution it requires that faction's SpendFunds + ManageAssets, current role control, a conscious/un-carried
buyer of the same faction within 2000 world units (engine rule), and current sale eligibility/price. Payment
and owner transfer run serially; property replacement invalidates old quotes even when price is unchanged.
`meitou property purchased` persists purchases independently of the local-player alias. Purchased remote
houses survive registered/unregistered town replacement and restore their repair/material/pause/ruin state.
NPC-faction membership alone neither purchases its houses nor creates repair sites for untouched NPC ruins.
**Verified** by multiplayer `HousePurchaseTests`: competing buyers, receipt replay, payer/actor isolation,
permission restoration, range and quote revision checks, binary state round trip, remote repair restoration
and town-replacement protection before/after fixture registration.
`MachineSave` binds one saved source to each world. `ProductionSystem.Ensure` restores each zone/instance
once, including level-house machines created when fixtures register later; ordinary bulk load uses the
same path. A level placement's canonical runtime key is mapped back to its raw zone-file instance, and
state records are resolved through the instance's references rather than a guessed record name.
Repeated Ensure/Read calls and destroyed-machine recreation cannot replay checkpoint stock. Town reset
invalidates pending restoration and removes obsolete level construction/machines. Capture clears retired
machine records/queues/inventories and old unregistered container state, then writes current live contents;
newly acquired post-reset contents are retained. World replacement gets a fresh restoration binding.
**Verified** by the late-save `ProductionTests`: actual checkpoint and region-registration restoration of a
remote purchased house's machine, repeated/read/recreation exclusion, save/restart after removal or town
reset, and preservation of newly acquired container contents after reset.
`WorldCheckpointCoordinator` captures world files and metadata at one serialized boundary after draining
authority requests and resolving the local held item. It deep-copies the payload before worker-thread IO;
reentrant tick/revision/time changes reject capture. A producer/session token fences stale drafts across
world replacement, disposal or producer handoff. Commit sequences prevent older captures overwriting newer
ones; retrying the latest committed draft returns its existing ID. Disk failure leaves the live world usable.
Runtime replacement/disposal share the commit gate, while ordinary stepping does not take that gate.

Restore prepares an independent catalogue before constructing systems, loads resources/assets/roles and
then authority/diplomacy into the candidate before activation. Invalid loading keeps the old world. If only
old-world disposal fails after activation, the result returns the usable new coordinator plus CleanupFailure
for the host to report. **Observed** (source review): `SaveLoadOptions.GameHoursOverride` selects exact checkpoint hours before any timer restoration; legacy CAMERA minute encoding remains compatible. Runtime checkpoint timing preserves exact GameHours and Daytime; saved source tick
and revision are diagnostic, while native timers rebase to the replacement world's tick zero. Native minute
clock fields must agree with the precise timestamp. HeadlessWorld supplies the real content/level options.
**Verified** by `WorldCheckpointCoordinatorTests`: detached capture, authority drain, exact time, commit
ordering/retry, producer handoff, failed IO/load, independent-catalogue enforcement, cleanup reporting and
a deterministic restore/queued-old-commit race. The installed headless test captures/commits/reads actual
content without a renderer. These do not establish all environmental/world-event state persistence.
Personal views/preferences and full client admission remain pending; the coordinator is not yet
wired into the graphical save flow or a dedicated-server executable.

`World.CreationQualifications` persists server-issued initial-start and faction-approved qualifications.
It creates no characters, grants no funds/items and performs no debit. Initial approval requires known
NeverAssigned history and no membership; one initial identity per player is retained through revocation.
Login-style retries never reopen it. Explicit server reauthorization can update an unused initial policy
under the expected revision; receiving any role permanently prevents that initial policy from qualifying.
Faction approval separately requires CreateCharacters, plus SpendFunds for nonzero cost, and recipient
membership. Stable approver-scoped approval IDs deduplicate retries and reject changed payloads. Faction
approval never includes independent start rewards. Current approver/member revisions, permissions, funds,
character limit and game-time expiry are rechecked when assessing use. Approval does not reserve money.
Preparing connections can read only their own qualification views; stale handles cannot query them.
Metadata retains all qualification identities/revisions and revocations. Older metadata migrates
to an empty ledger without creating eligibility or expanding old permissions to CreateCharacters; newer
approval records/creation bits in a legacy envelope are rejected. Current ledgers reject missing IDs,
duplicate approvals/initial entitlements and invalid player/faction/revision references before restore.
**Verified** by `CreationQualificationTests` and the coordinator qualification checkpoint case: authority
separation, retry/revocation, initial policy changes, role-history exclusion, live condition rechecks,
private preparation queries, invalid ledger rejection and actual disk persistence. Eligibility alone is
not a success acknowledgement for character creation.

`PopulationSystem` shares start-recipe generation with the legacy single-player path but separates
multiplayer preparation/materialization and new-faction initialization. `StartPlayer` refuses an
explicit-authority world. `InitializePlayerFaction` accepts only an unused runtime faction and directed
diplomacy; it writes that faction's start funds/research and only diplomacy pairs involving that faction.
It never clears town replacements or resets existing factions. A sponsored roster need not call it.
`PreparePlayerSquad` generates an opaque world/producer-bound recipe using content faction templates and
a server generation seed, without creating characters. Member planning is bounded before appearance work;
missing/gated content or an explicit empty recipe does not silently select another start. Runtime-template
diplomacy is retained in the isolated start-location preview. `MaterializePlayerSquad` rechecks quota,
navigation and unique occupancy, uses logical member indices for seeded stats/names, and attempts a recipe
once. Expected assembly failure removes partial roles before any unique binding; allocator IDs may advance.
Materialization does not award start resources, assign control, select a player UI, consume qualifications
or publish a durable success. `PlayerCreationService` orchestrates these primitives and typed creation choices.
**Verified** by `PlayerCreationFactoryTests`: existing-player resource/diplomacy/UI isolation, bounded or
missing/unwalkable preparation, changed unique/navigation/capacity checks, world binding, partial cleanup,
and generation independent of unrelated slot allocation. Pinned single-player hashes remain unchanged.
The installed headless test also prepares/materializes and assigns a real-content second faction roster
before coordinated checkpoint capture, without changing the first faction's money.

`PlayerCreationService.Execute` derives identity from the current connection, resolves an approved start
through server policy and fingerprints the request. Initial creation reserves a distinct runtime faction;
faction-approved creation uses its existing faction and does not apply start cash/research/diplomacy.
After a population-worker fence and preparation, a durable world barrier covers materialization, control
grants, payment, qualification consumption, receipt capture and checkpoint commit. Success returns only
after the checkpoint pointer is installed. Tick/paused inputs/population preparation and admission activation
cannot advance the pending world; critical capture deliberately does not drain unrelated queued commands.
Disk/capture failure retains the same result and detached capture for the same request's retry, including
after connection replacement. Another service cannot expose an in-memory uncommitted receipt as success.

Schema 8/qualification schema 2 persist historical receipts keyed by player/request, including fingerprint,
qualification version, faction/squad, stable role IDs, paid amount and start-resource flag. A consumed grant
must have one matching receipt; receipts validate reserved identities and cannot reuse roles across results.
Replay returns the original result even after role death/transfer/revocation. Changed payloads conflict;
new requests cannot reuse a consumed qualification. Legacy qualifiers migrate without inventing receipts.
Lookup by request ID is private to the current connection's player and distinguishes unknown, pending,
committed and busy; pending results never expose a receipt as durable success.
Expected preparation/assembly failure retains qualification and funds. Failed initial assembly can leave
an unused catalogue row reserved by the request ID; its resource account/diplomacy are rolled back and retry
reuses it. Unexpected failure after granting begins faults the runtime and requires consistent checkpoint
restoration, never continued partial gameplay. Hosts serialize creation calls and prepare arrival navigation
first; requests select approved starts and typed editor choices, never arbitrary client stats/items/positions.
**Verified** by `PlayerCreationServiceTests`: both creation paths, single payment/resource award, actual
save/restart/replay, frozen IO failure and reconnect retry, stale producer/connection rejection, changed
payload/consumed-token rejection, failed initial assembly recovery, historical dead-role receipts and
corrupt-receipt rejection. The installed headless test exercises initial creation through this transaction.
Creation choices address roster member indices and optionally provide names and race/gender/editor controls.
Requests freeze/bound/sort these inputs before fingerprinting; changing a submitted choice conflicts with its
committed request. Omitted/empty choices preserve the preset request fingerprint. The server constructs a
CharacterDraft from its generated look, accepts only controls/options present in that race's editor definition,
rejects out-of-range/duplicate/unknown controls and requires available limits. Force-race restrictions do not
fall back to all races. Equipment comes from the server recipe; race-dependent stats/medical state are built
by CharacterAssembly, while names and edited appearances survive ordinary save capture. Animals accept names
but no humanoid appearance payload. Sources unable to validate appearances fail before mutation.
**Verified** by creation-service choice tests and the installed headless transaction: committed names/replay,
invalid indices/names/duplicate members/unsupported looks, force-race rejection, real race/colour editing and
rejection of unknown stat-like controls, invalid picker records and out-of-range values without consuming a grant.
Creation, invitation transport and stored-role recovery are defined in [server contracts](server.md).
Graphical editor/catalogue/invitation UI remains integration work; these tests are not final multiplayer acceptance.

## Remaining integration boundaries

Compatibility manifests/handshake, bounded frame IO and the command bridge are implemented in `Meitou.Networking`;
see [networking contracts](networking.md) for coverage and remaining transport integration.

Construction-job assignment, durable cross-restart ordinary gameplay deduplication, admission/creation UI,
complete checkpoint coverage and server packaging remain unfinished. Current tests do not establish
end-to-end multiplayer or original-game/mod parity. Campaign-specific regional demand and full subscription
authorization still need integration.

## Additional player-scoped interactions

**Observed** (source review; no new tests): held custody is per player, with actor/control grants, held serial,
source/destination inventory revisions and shop/loot/container/site sessions. Regranting control cannot revive
old custody. Refused payment never transfers goods; cancellation returns custody without refunding an
uncommitted payment. Missing/replaced sources recover items onto the ground. Disconnect/replacement closes
personal interactions; checkpoint capture returns every player's held items before saving.

Loot inventories publish only while the recipient's original actor grant/session remains valid. Medical,
carrying and facility work rechecks authority during execution; sharing medicine requires the same controller.
Beds charge the acting faction with spending permission. Fixture/group revisions reject replaced/reowned
facilities. Disconnect retains role grants and attached carried bodies; it does not revoke ownership.

Player barter keeps items with their owners until both confirm one offer revision. Offer changes invalidate
confirmation; control/membership changes, distance, disconnect or inventory changes prevent settlement.
Both inventories/accounts are preflighted before transfer. The native panel supports item/money offers,
confirmation and cancellation. These contracts have not received multiplayer runtime validation.

Surgery/recruit editing requires a single-use server dialogue session, current control/membership and validated
appearance controls. Client payment assertions grant nothing. Remote creation uses the durable creation service.
Campaign clocks use NPC faction/campaign/persistent target-base identity; schema 9 also persists per-leader
biome-arrival memory. Campaign conditions use one target faction witness.
