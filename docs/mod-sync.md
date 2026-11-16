# Server mod synchronization

**Observed** (source: `ServerModPackage`, `ServerModProtocol`, `ServerModClient`, `ServerModCache`, launcher `ServerModSync`):
Connect defaults to querying and synchronizing the server's exact enabled mod set before starting the selected client.
The Coop checkbox can disable synchronization for manually configured/older servers. Play and Continue retain their local mod list.
Clients advertise `serverModSyncFormat: 1` in `settings-schema.json`; unsupported clients are refused before downloads.

## Server snapshot

`server.json` has `ShareMods` (default `true`). Startup snapshots every file in enabled mod folders, in the effective `ModsCfg` order,
including Workshop folders and code dependencies. Base-game files, disabled mods, accounts, certificates and world saves are not packaged.
Mod folders must contain only files intended for players: sharing exposes the complete enabled folders to unauthenticated downloaders.
Links/junctions, case-colliding names and nonportable paths are refused. `ShareMods: false` disables file sharing.
Restart after changing mods; an active snapshot never changes beneath a downloader. Temporary ZIPs live under `server-data/mod-downloads/`
and are removed on normal shutdown. Interrupted processes can leave orphan ZIPs there; they are not reused or served by later instances.

The existing TCP/TLS discovery port serves these additive MTN1 bootstrap messages (no gameplay protocol/version change):

| ID | Message | Payload |
|---|---|---|
| 80 | Mod manifest query | Empty |
| 81 | Mod manifest | Format 1 JSON inventory, order, code permissions/settings, archive size/SHA-256 |
| 82 | Mod archive query | The advertised SHA-256 as 64 ASCII bytes; never a path |
| 83 | Mod archive | The same manifest, followed by exactly `ArchiveBytes` raw ZIP bytes |

Manifest JSON is capped at 16 MiB, 1,024 mods and 100,000 files. Both compressed and expanded content are capped at 64 GiB.
Queries have a 20-second client deadline; downloads have 30 minutes and at most two concurrent server uploads. The usual discovery connection
limit still applies. TLS follows existing [self-signed discovery policy](server.md); content hashes do not authenticate an operator.
These descriptors use a separate bounded reader/writer because inventories can exceed the normal 1 MiB gameplay frame limit.

## Client isolation and verification

Profiles live under the launcher data folder's `server-mods/<manifest SHA-256>/`. The identity includes order, settings, executable-code
permissions, file lengths/hashes and the archive hash. Downloads go to a unique staging folder; archive and every member pass SHA-256
before publication. Members must match the inventory exactly and stay under declared `mods/<name>/` folders. Directory/symlink entries,
duplicate or unexpected files and traversal/device/alternate-stream names are refused. Cancellation/failure removes staging and retains
previous verified profiles. Existing caches are rehashed and checked for extra files before reuse; damaged caches are replaced after verification.
Updated sets download a complete archive; partial-download resume and per-file delta transfer are not implemented. Cache generations are retained.

**Observed** (source: `ServerModCache`, `ModList`): the profile's `mods.cfg` uses the client's [game name encoding](formats/overview.md#name-encoding).
Names unavailable in the client's code page are refused before downloading, rather than replaced with `?`.

Only the child process receives `MEITOU_MODS_DIRECTORY` (replaces all local/Workshop discovery), `MEITOU_MODS_CFG` (exact server order)
and `MEITOU_SERVER_MOD_PROFILE` (session code trust/settings). No original `mods.cfg`, Steam subscription, installed mod folder or saved code trust
is changed. Empty server sets also isolate out local extras. A conflicting explicit `--mods-cfg` is refused for Connect.

Executable code requires launcher consent for the normalized address plus exact manifest identity, remembered in `TrustedServerModSets` (last 100).
Updated manifests prompt again. Data-only mods synchronize automatically. Only server-approved `TrustedCodeMods` execute; unapproved code
mods contribute data without inheriting local single-player trust. Session settings and trust apply to admission and `GameMods` without being saved
into `meitou.user.json`. RE_Kenshi native plugins remain unsupported. Normal engine/content/rules admission checks still run after synchronization;
matching original content and any separately configured base-game `GameplayAssets` remain each machine's responsibility.

**Verified** (`ServerModSyncTests`, Windows/.NET 11, 20 synthetic cases): real TLS query/download, legacy discovery coexistence, immutable snapshots,
order/assets, cache reuse and corruption/injection repair, cancellation/interruption, ZIP traversal/symlink refusal, independent archive/member hashes,
invalid inventories, disabled sharing, empty-set isolation, code-consent refusal, session trust/settings and address-discovery code compatibility.
