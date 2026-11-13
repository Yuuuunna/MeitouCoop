# Server identity and transport contracts

Engine extension. `Meitou.Server` provides account services, a shared world loop and TLS listener/session
handling, creation and invitation endpoints. The executable and configuration/account CLI support experimental
real-machine sessions; this does not establish complete multiplayer acceptance.

## Dedicated executable

**Verified** (2026-10-31, published win-x64 executables: TLS account creation/entry, GPU screenshot and graceful save): `Meitou.Server --config server.json`
creates default configuration and a persistent self-signed `server-data/server.pfx`. Installation discovery uses
`KENSHI_PATH`/`meitou.local.json`. Relative `DataDirectory` resolves beside the config; accounts, certificate,
private connection profiles and checkpoints stay there. Defaults: loopback port 27800, 32 connections,
Wanderer as the first offered start, persistent random world seed, pause without an Active player, five-minute saves.
**Observed** (executable source): the initial ruleset offers every loaded `NEW_GAME_STARTOFF`, including mods;
`DefaultStart` controls ordering, while `CharacterLimit` and server validation still constrain creation.

**Observed** (source review): `ServerConfiguration.Gameplay` owns global `GameplayRules`, applied to every fresh or restored world
before mod save-data restoration. Clients receive the effective rules as read-only projection; only administrator configuration
changes them. Compatibility hashes `gameplay/*` values captured from the active world after checkpoint restoration.
`Gameplay.AutoSaveSeconds` is 0 (periodic saving disabled) or 10..86400; manual and shutdown saves still run. Legacy
`SaveIntervalSeconds`, when present, wins during loading, is normalized into `Gameplay.AutoSaveSeconds`, and is omitted from new
configuration output. Gameplay values use the same bounded finite ranges as their game settings; invalid values reject configuration.

`--create-account NAME [--output FILE]` issues a random credential and writes a private connection JSON,
then exits without loading a world; tokens never enter logs. Do not include these files in release archives.
Set `BindAddress` and `AdvertisedHost` for LAN/remote access before issuing profiles. `--help` needs no assets.
The process rejects a second world owner using the same data directory. Enter `save`/`stop` or use Ctrl+C;
`--run-for-seconds N` provides a bounded graceful manual run after loading.

Startup restores the latest complete checkpoint; corruption fails startup rather than resetting the world.
New accounts use private initial-start qualifications and durable creation, never `StartPlayer`. Periodic
and final saves run on the world owner after peer teardown. Failed critical persistence prevents a final
snapshot and reports failure; the last complete checkpoint remains authoritative. Checkpoint retention is
currently manual; immutable checkpoint directories accumulate.

Profiles contain endpoint, account/token, expected Hello, rules and explicit relative
`GameplayAssets`. Both executables call `ServerCompatibility.CreateHello` against their own installation.
The default hashes database load order and all database bytes; optional paths add selected gameplay assets.
This intentionally avoids a whole-install scan and does not prove mesh/navigation/animation asset parity.
Build identity `meitou-experimental-1` is experimental, not a reproducible release fingerprint.

**Observed** (implementation): `AllowDeviceRegistration` defaults to true. Compatible TLS clients may
register a random device credential; an existing GUID must authenticate its unchanged digest, including
when registration is disabled. Disabled accounts cannot re-register. New registrations are capped at 32
per minute per process and the account store retains its 10000-account limit. Only token digests are
stored; account-to-player binding still crosses the durable checkpoint boundary. Retrying after a lost
registration reply reuses the client's saved credential. Discovery publishes rules/assets/Hello only.
**Verified** by `SecureServerTests.Devices`: TLS discovery, registration/reconnect identity, wrong-token
and disabled-account rejection, and existing login with registration closed.

`TokenAccountStore` issues a random 256-bit bearer token and GUID account ID. The label is not an identity.
Only SHA-256 token digests are stored; comparisons use fixed-time byte comparison. Credential/request
ToString values redact tokens. Rotation and disable/re-enable advance a credential revision, invalidating
previous authenticated handles. IsCurrent checks the issuer and an account index cached for at most one
second; local writes invalidate it immediately and login always reads fresh data. The peer polls it and
closes revoked sessions. Authentication never grants faction rights.

The bounded/versioned store rejects malformed/duplicate records and JSON fields. Changes hold a writer
file lock, flush a temporary file and rename it over the prior file; a failed pre-publication write leaves
the prior credential valid. Corrupt existing data fails closed. Atomic replacement is a process-interruption
contract, not a hardware/power-loss durability guarantee. Store files and certificate private keys belong
in server state outside source control. There is no password login or public registration endpoint.

`AuthenticationProtocol` accepts only account GUID/token, at most 512 bytes, with no PlayerId. Production
hosts must accept it after TLS and compatibility. `GameTls` allows TLS 1.2/1.3 and accepts self-signed server
certificates without SHA-256 pinning while keeping TLS encryption and account/device authentication enabled.
Hosts supply handshake/IO deadlines and connection/rate limits.
PFX loading uses a temporary user-key container on Windows (Schannel rejects ephemeral keys), ephemeral
keys elsewhere. Callers own/dispose certificates and streams.

`ServerAccountSessions.Open` runs on the serialized world owner. The verified local-token account subject
resolves through World.Players, not client identity or nickname. Unknown subjects receive a new unprivileged
player; an imported/unbound player needs explicit administrative BindAccount. A binding never changes owner
and one subject cannot bind two players. New bindings require a successful world checkpoint before Open
returns an authority connection. Pending status lives on the world, so recreating the resolver after a save
failure cannot bypass it. The public constructor requires a checkpoint coordinator and verifies that it owns
the same active runtime before committing; it does not accept an optional/no-op persistence callback.
Repeated login resets transient trade/dialogue/container/loot windows and opens a replacement generation;
old handles become invalid. Old disconnect cleanup cannot close the replacement's windows. Role ownership,
position and background orders remain in the world. Authentication awards no faction, role or start entitlement.

Bindings are checkpoint metadata schema 9/authority schema 4. Older metadata restores unbound identities;
downgrading the schema cannot introduce bindings. Credentials remain outside world checkpoints, so restoring
an old world does not restore a revoked token. Ordinary world/checkpoint rollback semantics still apply.

**Verified** by `ServerAccountTests`, `GameTlsTests` and the three-faction disk-save test: digest-only storage,
rotation/revocation, failed replacement, corruption rejection, save-failure retry across resolver replacement,
persistent account-to-player mapping (including an empty world's first account), authenticated login after a
complete checkpoint restore, conservative migration, duplicate binding rejection, and encrypted discovery/credential
frames with self-signed server certificates.

## Connection host

`ServerWorldLoop` serializes synchronous runtime operations through a bounded 256-job channel, draining at
most 64 per scheduler pass. It calls shared Runtime.Advance with a one-tick budget and retains tick debt,
servicing command/ACK work between owed ticks; a durable mutation
suspends stepping while queued recovery work remains possible. Request failure does not stop the loop;
simulation failure stops it and cancels peers. Cancellation prevents queued work starting; running synchronous
mutations finish before returning. Network IO and arrival geometry preparation run outside the owner.
Stop resolves pending callers; concurrent disposal awaits the same task. Caller owns final save/runtime disposal.

**Observed** (installed-content CPU profiling and weather-only comparison): global weather ground queries
thrash a 256-tile terrain cache, repeatedly reading full TIFF rows. `TerrainSource` defaults to 4096 tiles
(~33 MiB of samples), retaining full-resolution interpolation and bounded LRU eviction. Parallel edge/eviction
correctness is covered by `HeadlessContentTests.TerrainCachePreservesHeightsAtTileBoundariesAndEvictionUnderParallelReads`.
Local latency results and remaining graphical measurement limits are in
[networking acceptance](networking.md#local-latency-results-protocol-20-baseline-historical). Slow owner passes and peer exceptions are logged.
**Observed** (source): owner jobs also log call-site, queue and execution time when execution reaches
100 ms or waiting reaches 500 ms; labels contain code locations, not account or command values.


**Observed** (source review): `Program` disables local whole-world display snapshots; network capture
independently projects each recipient. Queued work wakes the owner immediately. A tick's runtime-service
boundary is not repeated by the loop. `SecureServerPeer` captures/detaches on the owner and runs transport
work off-loop; publication checks recipient grants before sending. One bounded encoding job per peer limits
worker backlog. This describes current source, not measured throughput or end-to-end acceptance.
Ordinary saves serialize capture/commit requests: capture remains on the owner, while weather JSON/Brotli
encoding and checkpoint disk IO run on a worker. The weather pool copy retains the capture tick; world
replacement/disposal and durable creation retain their coordinator fences. Shutdown awaits saves. Save files
clone their ordered records, references, instances, slot lists and header metadata directly rather than
serializing and parsing them twice during capture; mutable collections remain detached. Capture itself still
costs world-thread time; see [local acceptance](networking.md#local-latency-results-protocol-20-baseline-historical).

`QuicGameServer` accepts protocol 21 connections through the pinned native QUIC transport; TCP is used
only by `TcpDiscoveryServer` to obtain compatibility metadata and native transport configuration. The client
validates discovery metadata and certificate policy before opening QUIC, then `SecureServerPeer` requires
compatibility, authentication and private admission in order. Wrong credentials close without a world view.
The listener defaults to 32 concurrent connections. Control, lifecycle, bulk and state use separate reliable
streams/datagrams with bounded application queues; current limits and framing are in
[networking-next](networking-next.md#protocol-and-transport-boundaries). Source review does not establish
VPS capacity, accepted-throughput targets or graphical gameplay acceptance; see
[networking acceptance](networking-next.md#acceptance-gates-requirements-not-measured-achievements).

Admission messages have a monotonic connection-local sequence, private roles/qualifications and command
high-water. Busy durable transactions expose no speculative roles/qualifications. Entry names an owned
persistent role; the host loader runs outside the loop, then ownership/liveness are checked again before
Synchronizing. ACKs use the protocol 21 view/epoch grant fence in [networking-next](networking-next.md#replication-model); stale admission state requires a fresh view. Clients ignore older admission sequences and require both Active and a usable replica to enable controls. Role loss withdraws the replica. Reconnect invalidates old commands/connections.

`HeadlessEntryPreparation` retains an owned role's stored/current region (carrier position when carried),
prepares navigation off-loop, and publishes/checks readiness on-loop. Grant changes, replacement, death or
leaving the prepared region fail without relocation/new roles; leases always release. Stored player records
are restored only after their regions are ready, and population/fixture/carry dependencies precede admission.

**Verified** by `SecureServerTests`, `ServerWorldLoopTests` and the installed headless test: three simultaneous
TLS clients with separate factions/funds/details, pre-baseline command denial, deduplication, reconnect,
roleless restrictions, preparation concurrent with gameplay, revocation/disconnect during preparation, connection-limit
rejection/recovery, queued cancellation/shutdown and real-content navigation preparation. Graphical-client
and final server-release acceptance remain outstanding.

## Creation sessions

`ServerCreationEndpoint` is shared by connections to one active world. Its immutable rulesets allow specific
starts/templates and roster limits. Catalogue qualification zero requests an initial grant only when the
configured policy permits it and history/membership prove eligibility. Existing revoked/consumed grants
are not renewed. Issuance follows ordinary checkpoint persistence; consumption and created output commit
together. Faction-approved grants use their approved budget and never award the start's independent resources.

The preparation session supports catalogue, preview, commit, lookup and persistence resumption. Query IDs
and draft revisions correlate UI replies; the creation request GUID/fingerprint identifies the durable action.
Requests contain start/name/typed appearance choices, never issuer, seed, funds, attributes or equipment.
Preview and spawn share member assembly and deterministic seeds. Preview allocates no world characters or
factions and spends nothing; it returns generated stats/kit/appearance and server-derived editor controls,
picker choices and race restrictions. Static records in appearance use the existing ID-only display codec.

`HeadlessCreationPreparation` loads the approved deterministic birth region without a pre-existing role.
Qualification, capacity and connection are checked again after preparation. Only successful checkpoint
commit returns Committed/role IDs. A pending write returns PendingPersistence without speculative roles;
already-durable accounts may reconnect during this barrier, while new bindings and gameplay remain blocked.
Empty-ID lookup finds only that player's pending request; Resume persists the frozen result without needing
the lost draft. Exact replays return historical results, changed payloads conflict, and another player cannot
retrieve or resume them. Creation failures use typed result codes and preserve the same qualification unless
it was committed. Creation replies are bounded to one 1 MiB frame; oversized previews fail explicitly.

**Verified** by creation service/protocol tests and TLS creation cases: preview/committed member parity,
server-field spoof rejection, private result lookup, independent/sponsored resources, save-failure reconnect
and resumption, and deduplication. `InstalledCreationAndReconnectKeepAuthorizedViewsAcrossTwentyPublications`
verifies real installed content over TLS from an empty server through creation, checkpoint, baseline and Active
without creating a legacy local player. Graphical editor/menu integration remains pending.

## Invitations and assignments

`FactionInvitations` stores immutable offers and historical outcomes in metadata schema 9 (invitation schema
1); older checkpoints get an empty ledger and cannot inject offers. An offer explicitly grants membership
permissions, transfers selected existing roles, or approves faction-funded creation. InviteMembers is required;
new-member permissions cannot exceed the inviter's own rights. Transfers additionally require AssignCharacters;
creation approvals require CreateCharacters and SpendFunds when charged, plus an allowed server ruleset.
Membership-only acceptance grants no roles, creation qualification or independent start resources.

Existing members can receive later assignment/creation offers. These use Permissions.None to preserve their
membership, capture its revision and reject intervening changes. Offers also capture inviter membership and
role-control revisions. Acceptance rechecks all roles/rights/expiry before any mutation. Batch transfer changes
only control grants and prunes old selection; position, squad, injury, inventory and jobs remain in place.

`PlayerInvitationService` commits membership, transfers, optional creation qualification and accepted outcome
in one checkpoint. Disk failure retains a frozen operation; only its recipient can resume it after reconnect.
Another service cannot acknowledge in-memory success. Accepted replay is historical and cannot rejoin a player
who later left or reclaim transferred roles. Offer/decline/revoke use ordinary checkpoint persistence.

`ServerInvitationEndpoint` handles offers, acceptance, close, private lookup/resumption and paged lists.
Assignable-role metadata requires AssignCharacters for that faction. Invitation details are visible only to
inviter/recipient; no account credentials or arbitrary world view are disclosed. Pages contain at most 128
entries. Ledger safety limits: 128 pending offers per inviter, 16384 records, 65536 original role references;
automatic history pruning remains pending. Graphical invitation/assignment UI is not wired.

**Verified** by invitation service tests and TLS scenarios: membership-only waiting then later assignment,
existing-member permission preservation, private lookup, delegation restrictions, stale batch rejection,
independent faction-funded creation, old-order rejection, paired checkpoint restoration, historical replay,
write-failure recovery and malformed/downgraded metadata rejection.

## Stored-role recovery

`SaveLoadOptions.DeferPlayerRoles` keeps multiplayer player records in `StoredPlayerRoles`; the default
eager/single-player path is retained. Saved squads/IDs are registered immediately. Pending live roles count
toward faction limits and region demand, and their records survive recapture; unloaded squads cannot be
deleted or have their roster changed while recovery is pending. Restoration reads a complete squad/carry
dependency group before spawning, uses saved stats/medical/inventory/jobs and exact positions, and consumes
source records once. Missing readiness/content/partners preserves those records instead of inventing new roles.

WorldRegionNavigation restores ready groups at serialized boundaries independently of client admission.
The server loop also synchronizes outside ticks while paused. Hosts must prepare startup regions before
advancing their intended gameplay clock; this is not an offline medical catch-up implementation.
HeadlessWorld.RestoreCheckpoint uses the existing coordinator and fresh mutable PopulationData with shared CPU
content, returning the new coordinator; services must be rebound to that world. HeadlessWorld.Population
names the active world's data after replacement.
Weather is also world-owned: replacements create an independent SimulationWeather instance. Runtime
boundaries initialize/synchronize it even while paused, before players receive a view. Checkpoints store
an exact versioned regional WeatherFrame in CAMERA alongside legacy BIOMES fields. Restore validates it in
the candidate and preserves the old weather on failure; older checkpoints use BIOMES when available.
Regional save restoration retains the existing fresh-seeded future-random behavior. Affecting effect groups
preserve their generations, timers, nodes, particle tails and portable random states; forced gameplay weather
is also saved. CAMERA's versioned `meitou weather effects` payload is Brotli/base64, bounded to 48 Mi characters
and 512 MiB expanded JSON (regional JSON remains 8 Mi characters). Permanently resting particle emitters encode
their infinite cycle as null. Regional document v2 requires the effect payload; v1/legacy checkpoints recreate effects.
Restore validates catalogue/pool shapes before candidate activation and does not advance effect schedulers.
**Observed** (particle checkpoint codec): particle state v2 appends each live particle's mass as channel 22;
v1's 21 channels remain readable with unit mass. New captures retain gravity-affector state across restart.
**Verified** by ParticleCheckpointTests, WeatherHazardCheckpointTests, WeatherScheduleTests and
WorldCheckpointCoordinatorTests.Weather: subsequent movement, wrapping, tail expiry and respawning agree;
malformed payloads preserve the live world. Installed-content restore/re-entry also exercises checkpoints.

Role availability distinguishes Available, Stored, Dead, Unavailable and Unknown. Metadata schema 9/authority
4 preserves death/permanent-removal tombstones for retained grants; legacy data has none and uses saved archive
evidence conservatively. Removing a dead body no longer turns it into an unresolved living role. Unload is
explicit and does not retire a living grant. Retired roles never regain control through slot/entity rebinding.
Unresolved saved carry links block control. Preparation follows known carrier locations and restores links
after dependencies load; missing partners do not grant freedom. Dead player archives remain unmaterialized.

**Verified** by stored-role save tests, admission/identity tests and installed-content restore/re-entry:
quota and record retention before readiness, unchanged position/blood/stats/kit/jobs, slot reuse isolation,
once-only restoration, dead archive/removed-corpse classification, carry dependencies, missing-link denial
and automatic restoration without any connection. Graphical status/recovery presentation remains pending.

**Observed** (source review; no new tests): connection replacement/teardown releases personal loot, custody,
editor and barter sessions. Thread-side Close queues owner-boundary cleanup; stale connections cannot close
replacement interactions. Checkpoint capture returns all held custody before world/metadata capture.
Metadata schema 9 adds persistent-base campaign clocks and per-leader arrival memory; older schemas migrate
with empty collections and reject injected newer fields.
