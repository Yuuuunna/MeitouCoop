# Simulation

Engine contracts for `Meitou.Simulation`; game facts live in [game/](README.md#gameplay-research).
Source authority: `World*.cs`, `StandardSystems.cs` and each subsystem. Implementation choices
below do not claim original-game parity. Retain Verified/Observed/Unknown scopes in linked research.

## Time model

Control: 30 Hz real time, input/UI/camera continue while paused. The interface's own commands (selection, order toggles, run speed, job lists,
crafting/research queues, squads, inventory, shops, building settings, movement/combat/first-aid/door/cage orders) apply between ticks while paused
(`World.ApplyPausedInterfaceCommands`); orders only set tasks, nobody moves or fights until the next tick. Simulation: 1/30 s game time;
accumulate `realDt*speed`, speeds 0/1/2/5. Budget `6*speed` ticks per frame; excess is dropped and
`AchievedSpeed` reports slowdown. Control ticks precede simulation ticks; a speed change applies
to the entire frame. `WorldSession` rejects unequal world/clock tick lengths, owns snapshots,
clocks, input and camera, and disposes systems/services. Of a frame's ticks only the last two publish
a snapshot (`RunTick(publish)`), the pair the host interpolates between.

### Tick ahead

The game can run the next tick on its own thread (`WorldSession.BeginTickAhead`, thread `Meitou.Tick`) during the world draw (`WorldFrame.Draw`, after
`DrawWorld`'s host prefix), and joins it right after (`EndTickAhead`), so the frame costs about the slower of the two instead of their sum. The tick is
ahead of the clock: it counts as the next one owed (`AdvanceSimulation` takes it, then runs the rest), and the game clock moves when it falls due.
Until then `CurrentSnapshot`/`PreviousSnapshot` hold the pair from before it, so the picture is where it was; `ShownTick` is the world tick on
screen. Between `BeginTickAhead` and `EndTickAhead` nothing may touch the `World`: the renderer's host callbacks (`Source`, `PoseClock`) read only
the session's snapshots. Normal game weather is a CPU environment system; the renderer receives regional copies at frame boundaries
([runtime contracts](multiplayer.md#shared-runtime)). Consequences
(**engine choice**): a command lands one tick later (33 ms at 1x); blood marks and sounds come out with the tick, up to a tick before it shows; a
pause with a tick ahead takes it at once (the picture jumps a tick, then the paused interface's republished snapshots show); a save takes it first
(`SettleAhead`); `ReplaceWorld` drops it. Display → World tick alongside drawing (`TickAhead`): Faithful never (the world, then the draw); Meitou, the default, once
a tick takes 4 ms (`WorldSession.TickMs`, smoothed over about ten ticks) until it is under 2 ms (a tick ahead is slower, sharing the cores); Always
every frame. Off whatever the setting in stills, the duel, recorded flights and the play benchmark (their ticks stay in step with the frame).
F12's `tick ahead` row is its wall time; `outside ticks` leaves it out.

**Verified** original clock: 1200/11 seconds per game hour ([game-loop.md](game/game-loop.md)).
`BodySystem.HoursPerTick=11/36000`; the medical tick takes `TickSeconds` ([character-stats](game/character-stats.md#time)). Navmesh unload uses
real time (30 s); population unload grace uses game time (towns 60 s, roaming 10 s).
`RequestedPause` is independent of remembered nonzero speed. Render previous/current states
with simulation alpha; camera uses control alpha. Positions are float world coordinates;
rendering is camera-relative.

## Threading

Tick order: Inputs → Schedule → Think → Move → Act → ordered Apply → SlowWorld → Publish.
Inputs/Schedule/Apply/SlowWorld are serial; each implemented parallel phase has a system barrier. Publish makes the character snapshots on the
workers in 256-slot chunks (each reads only the tick's state), listed in slot order.
`CharacterHot.Previous` is read-only; a partition writes only its own `Next` slots. Spawn/remove
only in serial phases after consumers finish; generation IDs prevent stale slot references.
Combat has separate double buffers swapped at the end of its SlowWorld, not at global publish. Its checks round a target (who swings at
it, who yields, attack slots, joining, fighters at it) walk `AttackerIndex`, the per-target attacker lists rebuilt in Pursuit's and Combat's
Schedule, filtered to the grid cells a search would cover, in the grid's order. Ragdoll physics steps on `Threads` workers (Bepu deterministic mode).

Effects sort by `(target,source,system,sequence)`; spatial-neighbour iteration order is fixed.
Commands apply by tick and queue order. In-tick orders use serial `World.Defer`; deferred orders
precede host orders for their due tick. No concurrent host enqueue may alter that ordering.
Randomness: stateless `Rng.Hash(seed,entityKey,purpose,counter)`; changing purpose/salt changes rolls.
Worker partitions are `threads*2`, minimum 16 slots (game 32: 128 left a 500-character battle 3 partitions); one worker means caller execution.
Async paths, navmesh and population results enter at tick boundaries; arrival timing is not
repeatable in play. Determinism tests use synchronous services. `PathService` runs 2-4 threads (a quarter of the cores) and drops a
request its character replaced before a thread took it up; F12's sim rows show the requests waiting.

`StateHash` is ordered, includes slot/generation/free-list and hashed subsystem state, normalizes
negative zero, and is a diagnostic rather than gameplay work. Golden scenarios must agree at
1/4/16 threads before pins change; pins are platform-specific. Snapshots expose living-character
identity, appearance, transform, animation, faction/squad/player/selection and body status;
selected characters also expose skills, inventory and path. Host reads double-buffered active
zones within the frame after ticks. Null appearances draw markers.

## World model

Dependencies: Core ← Data ← Simulation ← Engine ← Game; Navigation/Rendering are sibling
libraries. Characters use hot double buffers plus cold objects; squads/platoons use sorted
registries. `(slot,generation)` IDs and the free list participate in hashes. Static typed data
views are built over `GameRecord` once; missing fields use documented defaults. Population shares
factories/database/relations and lazy body/combat/item assembly. Research owns original formulas.

## Systems and their order

`StandardSystems.Build` is the single wiring authority. Ordered systems:
Population, Player, AI, Pursuit, Movement, FirstAid, Body, Carry, Construction, Combat, Animation, Ragdoll
(only with `StandardSystemOptions.Ragdolls`), Feed, Retaliation, Aggro, Flee, Crime, Arrest, Locks, Trade, Dialogue (Arrest and
Locks come with the Crime part), with Production right after Construction. Tests selecting subsets retain this relative order.
Population spawns before later SlowWorld work; player selection precedes movement orders;
AI writes tasks before movement; pursuit schedules paths before movement answers them.
First aid reads patients in its own Act barrier and applies treatment during serial commit.
Body handles medical ticks before combat (`TickMedical=false`); carry sees knockouts before
combat and drops carried targets before attacking. Combat swaps before animation/retaliation;
crime sees deeds and final positions/carry links. Game combat uses `SelfApproach=false`.

## Population

`PopulationSystem`, `SquadFactory`, `CharacterAssembly`, `GeneratedAppearances`: activate the loaded zones
(legacy: the camera's 3 x 3 block and player-character boxes, [corpses](game/character-stats.md#corpses);
explicit multiplayer: `World.Regions` role/activity/preparation demand, [contracts](multiplayer.md#shared-runtime)),
gate spawning on navmesh readiness, create residents/bar/roaming/wild/nest squads,
and assemble race appearance, gear, stats and bodies. Counts, roles, offsets, caps, spawn weights
and world-state gating follow [population research](game/factions-squads-towns.md).
Missing references become `SquadPlan.Problems`; world states are answered live (`WorldStates`, see Campaigns under the off-screen tier). Residents are housed by the original's building rules with housemates and
default residents ([town residents](game/factions-squads-towns.md#town-residents)); prisoners/cages remain
incomplete. A town that unloads turns each squad it made into a pinned platoon holding a `StoredSquad`
(`PopulationStored.cs`): each living member's own cold state (name, look, body, kit, bounties, crime marks) with its place,
cleared of path, AI, carry, treatment, combat and stealth state, plus the squad's home building, shop, purse and AI package.
The town then holds squads and makes none of its own; the platoon is made of the same characters when the town's zone is
active again and leaves with the town's grace. A town or wild site with a living member of its squads in an active zone does not unload (nor counts its grace) wherever its own zone is, so a chase away from home does not end in a vanish (engine choice for the original's per-platoon unload by its own place); a nest held so makes no new squads. This is the original's model: residents are persistent platoons kept as their
characters ([persistent platoons](game/factions-squads-towns.md#1-object-model)). Saves write stored squads as the town's platoons (`sqt` 1). Any platoon's
activation waits for the navmesh gate of its town's zone (a traveller: its own). A town made once (its pools' `started`) makes no residents again; its
badly hurt squads are made afresh within its pools (see "Off-screen tier"). `ActiveZones` is double-buffered. Zone loading/spawn planning stays off the
simulation thread; publish results at boundaries.
`World.PreparePopulation` queues nearby residents/wild squads, activates existing nearby roaming
platoons and drains completed builds during loading. It publishes matching snapshots without advancing
ticks, game hours, AI, movement or roaming timers; readiness includes navmesh-gated sites.

### Unique characters

The original's rules: [unique characters](game/factions-squads-towns.md#unique-characters). `World.Uniques` (`UniqueCharacters.cs`) is the
original's table (CHARACTER → claimed, live character, state 1/0/2, player flag), changed in serial phases only and hashed once it has an entry.
- **Create gate** in `SpawnSquad` (serial, so two squads built at once cannot both make one): a free unique human member is claimed and made; a
  claimed one gets its first `unique replacement spawn` (look rolled then) or nobody, and the first member left leads. Animals are not gated, as
  the original (unique pets such as Settler's Pup can repeat). The player's and sandbox squads skip the gate.
- **Handles**: bound whenever a unique enters the world (made, stored or saved squads re-made), which also claims it (engine choice: the original
  records only the handle there); dropped when a squad is stored or a corpse removed.
- **State**: deaths in the combat commit (player flag when the killer is the player's); a refresh at every population look marks the dead, the
  caged (player: the cage owner is the player's), the alive carried off by a non-ally, and frees one held or carried no more (engine summary of
  the original's ally/town tests). Off-screen losses (Meitou fights) die.
- **Release**: a squad leaving for good without being kept (Faithful despawn, a wild site unloading, a town squad remade by regeneration) frees its
  living uniques so its template makes them again, only when the entry is that character's (Meitou: knocked-out but alive counts as living); the
  dead stay claimed. Town squads stored on unload keep their claims.
- **Seed**: at the first population look, the uniques of each template's first `prisoners` template are imprisoned and unclaimed (kept when a
  save already has them).
- **Save**: CAMERA `usedUniques*` as the original (claimed entries; prisoners derived again). Handles are the written character's for player
  squads, else the loaded save's, else null (NPC handles are not tracked back from the writer).
- **Not built**, dead in the original: FACTION `special squads` (never spawned) and the `UniquePlatoon` missions that only they would run
  ([special squads](game/factions-squads-towns.md#unique-characters), **Verified**). Import is built ([save.md](formats/save.md#importing-a-game-savemanagerimportgame-fun_140378a30)).

### Off-screen tier

The original's rules: [off-screen world](game/factions-squads-towns.md#off-screen-world). It keeps only persistent platoons (residents) and
campaign squads off screen, moves them in straight lines (roads for long trips), never fights or heals there, and drives the rest by clock-based
budgets. The tier follows it, with Meitou choices marked.

**Town overrides** ([rule](game/factions-squads-towns.md#town-overrides)): `PopulationSystem.Replace` runs at each town load but those behind the loading
screen (`PrepareLoading`); the winner goes into `TownSite.Replacement`, and `TownSite.Town` (what every reader uses) becomes it with the placement's id and names
kept (engine choice) and the old faction when it names none; `SiteFactions` follows. The town then makes the replacement's residents even if it was made before;
its old squads stay. Until those residents are made (`SiteState.Repopulate`, the original's `updatedTownOverrides`, not saved) each load makes them afresh without a
new choice. Each such load resets the zone (`ResetZone`): the town's buildings there that are not the player's are registered again from the level (state, locks,
doors, contents; `FixtureRegistry.TownReplaced`; a save writes their owner and level `destroyed` into the old record of each, fixtures or not, `WriteResets`), its other buildings change owner only, the zone's item slots are due at once and its loose items are removed.
Saves write `replacementTown` (and the town's `faction` while replaced) and read it back; a new game or a load clears them first.
Tests: `PopulationTests.A_town_takes_its_heaviest_qualifying_override_at_each_load_and_makes_its_residents`, `..._whose_residents_were_never_made_...`,
`TownOverrideSaveTests`.

Six Faithful/Meitou switches (the fifth, campaign inheritance, under Campaigns below; `PopulationSettings`, live on `PopulationSystem`; Advanced settings (the fifth on the Fixes page), Meitou on by default; `PopulationOffscreen.cs`):
- **Squads keep their people** (`KeepRoamers`). Meitou: a roaming squad the population made keeps its characters as a `StoredSquad` from the
  first time it leaves, travels on as them and comes back as them (moved along with its stand-in); saves write it as a new platoon (`sqt` 2,
  `persistent` false). Faithful (the original): a platoon that is not a town's own is despawned as it leaves, and a stand-in out of range for
  60 s is despawned (the orphan timer); a save's despawned platoon leaves the save; loaded towns refill their pools. **Observed** (headless):
  Meitou brings back the same characters; Faithful replaces them from the pool, and save 2 loses 15 of its 15 travelling platoons in six minutes.
- **Fights far away** (`OffscreenFights`). Meitou: every 2 s, an unloaded travelling or wild stand-in (not a town's own, not the player's) met
  by an enemy (`AggroSystem.IsEnemy` either way) within 300 fights the first such in id order; strength per member able to fight is 10 + the
  mean of melee attack, melee defence and toughness (25 for a squad never made); A wins with S_A² / (S_A² + S_B²); the loser is despawned and the
  winner loses its weakest members in the share min(1, (S_loser / S_winner)²) / 2. Faithful: none.
- **Healing far away** (`OffscreenHealing`). Meitou: when a kept squad comes back, `MedicalState.Recover` steps the hours it was away in one go:
  wounds treated and closed, flesh healing at the part rate, stun gone, blood back at the resting rate, a KO ended if vital parts allow; no
  hunger, degeneration or death. Faithful: it comes back as it left.
- **Wild squads stay** (`KeepWild`). Meitou: a wild site (sector or nest) that unloads keeps each squad with someone alive as a `StoredSquad`
  (nothing leaves a nest's pool) and makes them first, where they stood, when it loads; they count against the site like a save's wild
  platoons, heal as above, and a save writes them as wild platoons. Faithful (the original): dropped, a nest paying for its hurt squads, and a
  save's wild platoon despawned on leaving (the orphan timer); an occupier is kept either way ([occupation](#package-classes)). **Observed** (headless, synthetic sectors and nests): Meitou keeps 12 squads and
  brings back a renamed character; Faithful remakes the squads from the seed.
- **Towns keep their schedule** (`ResidentSchedule`). Meitou: a town squad that comes back after at least an hour unloaded (`ScheduleAfterHours`) is
  marked `Squad.Resume`; each member's first think picks where its plan's walk ends (its bed at night, the counter, its node, seat or gun; a ring of 4 by member index, walkable only), the serial step moves it there (Movement would overwrite a move from the parallel think) and
  the act-out goes on from there, so the town looks as if it had lived through the time; nothing runs while it is unloaded. Faithful (the
  original, which runs nothing for residents off screen): where they stood when it unloaded.

Implemented (`PopulationRoaming.cs`):

- **Step**: at every population look (15 ticks), every unloaded platoon that is not a town's own steps by the look's game time in a parallel pass
  (`World.ParallelFor`, 64 stand-ins a job): the Faithful orphan timer and its travel. A step changes only its own platoon, so the result does not
  depend on the order (the roaming hashes agree at 1, 4 and 16 threads). Despawns, activations, loaded squads, pools and fights follow serially in
  id order. Town squads do no off-screen work (the original's GOHOME at home).
- **Speeds**: per job as the original (GOHOME 55, TRAVEL_TARGET 25, TRAVEL_TARGET_FAST 60 or 90 beyond 25000), the engine's travel stand-in (job
  NONE) 25 or 90, all capped at the original mover's 70 (`StandInSpeedCap`); straight lines. A short-range patrol keeps walking at 25 (the
  original's stands still). Destinations stay the engine's (towns, patrol points near home): the original keeps no roaming squad off screen,
  and saves give no usable target (**Observed**, 4 owner saves: `mission target` always type 0, `target town` on 1-3 platoons, `mission town`
  mostly a TOWN).
- **Roads** (`RoadNetwork`, `PopulationData.Roads` from the level's ROAD records; the same instance serves on-screen long routes,
  [pathfinding](game/pathfinding.md)): a stand-in, or a campaign's leader, takes the roads by the
  original's rule ([roads](game/factions-squads-towns.md#off-screen-world)), worked out when its goal changes and walked point by point; a
  campaign's followers go straight for their leader (engine choice: their goal moves every look). Hidden roads count. Engine reading: the
  join pull is clamp(w) × the point's forward distance. **Observed** (base data): 2109 roads, built in 33 ms; a route takes 0.5-16 ms; 193 of
  200 random town pairs go by road at preference 0.6; about 10 % of roads have a `length` well under their drawn length (0.17-0.44 ×, median
  1.00), which the original's cost uses as is, so a faction with preference 0.3 still takes the Hub-Stack road (walked 34000 vs 16900 straight).
- **Hand-off**: a stand-in that travelled comes back on the nearest walkable point (`WalkableSpots.Nearest`: rings of 4 to 128 units, eight
  directions each); its kept characters keep their offsets round it, each moved to a walkable point within 32 if needed, else onto it. A town's
  own or wild platoon, or one that did not move, keeps its characters' exact places.
- **Cost** (**Observed**, headless, save 2 with 2000 extra travelling stand-ins, warm): the step 0.08-0.1 ms per look at 1 or 8 threads (too little
  work for the threads to pay), pools and fights 0.23-0.26 ms per look (fights run every 4th look), about 0.03 ms a tick in all; save 2 alone
  0.08-0.1 ms per look. Not measured in the viewer's bench harness, which does not run the population.
- **Pools** (`PopulationPools.cs`): every town and nest has the original's two pools (residents `pop`/`popd`, roaming `pop2`/`popd2`),
  read from the save's town and nest states and written back, with `started`, `ded`, `tod`; the hour they last grew is not saved (set at load,
  as the original). Every 2 s (60 ticks) they grow 0.21818 per game hour up to the desired max (roaming only once residents are within 1 of full), for every
  town with pools (the save's from the first look, a new one once made) and the save's nests from load (a rolled nest from its first load), loaded or not. Growth is not state-hashed (as the population's other site state).
  - **Towns**: one made from its data counts its resident squads (those with a home; bar squads not, engine choice after the census) into `pop`
    and `popd` and is `started`; a started town never makes residents again, even with every squad dead (it holds what is left). Town roaming
    pools stay as loaded (0 in every base save) and `RoamingShare` still caps roaming squads (see the census conflict in
    [off-screen world](game/factions-squads-towns.md#off-screen-world)).
  - **Regeneration**: on a town's load, each of its resident squads (platoon id order; of the town's faction, engine choice after the census) that lost ≥ 60 % of its spawn size,
    while the losses fit `pop` less the living resident members so far, comes back made afresh from its template at its place and home, as a
    new kept platoon (the old one's characters are dropped; a save's leaves the save). A template whose members are all by chance gets up to
    8 rolls. **Observed** (headless): new game, Hub squatters 1 of 3 left → 2 fresh; save 2, Squin's Town Guard 2 of 5 → 4 fresh; both the
    same after save and load.
  - **Nests**: the pool starts at 0.7 × `nest resident population` (or the save's `pop2`); a load fills it to `floor(pop2)` members; every 2 s
    a loaded nest with room makes one squad (rolls keyed after the load's), unless a player character is within 1250, then it only weighs.
    An entry must fit the free budget, except while the pool is empty with more than 4 free (the Fog Deathyard's `Fog hive nest`, expected
    size 1 + (12 + 24) / 2 = 19, against a budget of floor(0.7 × 25) = 17). A roll that finds no entry (all weights 0 or none fits the free budget) with no squad of the nest left alive sets `ded` for good, `tod`,
    and posts "<nest> has been wiped out" (engine choice: "intact" = someone alive); so does a pool spent below one member with no one alive. A squad that lost someone takes its living out of `pop2`
    when the nest unloads. Dead squads lying there still count against the template weights. **Observed** (headless): a swamp raptor nest
    (N 20, pool 14) refills an 8-duck squad after its two squads die; drained to 1 with all dead it is wiped out and saved with `ded`, `tod`,
    `pop2` 1.008, `popd2` 20.
  - **Homeless squads** (`PopulationHomeless.cs`; [original rules](game/factions-squads-towns.md#off-screen-world)): every 2 s a squad whose
    leader patrols (a PATROL goal, outside a patrol-class package's town route; a homeless squad has no town, so its patrol package walks here too: the
    Border Zone's starving bandits) walks to its patrol point (its home centre, homeless radius) and picks the
    next within 500; a town drawn with `patrol approaches towns` is aimed 200 outside its first gate that is not a ruin with gate code 0 or unknown (unknown = outside, engine choice) or wall with `interior` references, milled round (50) and left
    within 100, so bandits stop outside the Hub ([patrol task](game/factions-squads-towns.md#off-screen-world)).
    A squad belongs to the sector it stands in: one that walks into an unloaded sector leaves the world (after the roaming
    10 s grace outside the active zones, ≥ 1382.4 from every player character), kept there with
    `KeepWild` up to that sector's count, else dropped. The area tick refills the loaded sectors, all taken as one island (engine choice). Sectors a town stands in (re-read each refill) are never refilled.
    **Observed** (headless, Fog Islands, one zone loaded): a fogman squad walks a sector a few minutes; leaving, a fresh one is made.

Implemented (`PopulationCampaigns.cs`; [original rules](game/factions-squads-towns.md#campaigns),
[save](formats/save.md#war_savestate-type-9-faction-campaigns)). Almost all campaigns aim at player towns (outposts), so nothing rolls until
the player has one and stands in it; requests (victory/loss chains, ally help) can follow.

- **Data** (`Meitou.Data`, `CampaignData` from FACTION_CAMPAIGN, cached per inheritance mode): ranges × 10, `repeat limit` × 24, weighted
  trigger lists, phase packages. Fourth switch, **Raids as written** (`CampaignAncestorsOnce`): Meitou applies each ancestor's list entries
  once; Faithful twice, as the original (doubled weights and trigger chances).
- **War manager** (serial, in faction order at the population look; every rule reads the game clock, so the cadence does not matter): the
  6 h roll, the chance with the linear falloff and `raidFrequencyMult`, targets among `World.Buildings.Towns` (biome groups, no-go zones; only a player town
  with a player character within 1250, slot 70; none with occupiers for a non-hostile campaign; the town level from its finished buildings), requests with due times, redirects (a running campaign too: `Redirect`) and retries, the
  `trigger player ally` help, and DIALOGUE_LINE `trigger campaign` (`TriggerFromDialogue`, keyed off the speaker and tick). Rolls
  are keyed off the world seed, faction and roll hour, so a run repeats. The state hash includes the managers only once one has started or
  queued something, so worlds without a campaign hash as before.
- **Squads**: required men = int(`num forces` × `raidSizeMult`); the `special leader`'s platoon (or the template it leads, made near the
  faction's town), then for the 4 own towns nearest the target their free roaming platoons, then new unloaded stand-ins from `squads to use`
  within 700. Engine choice: homeless and a town's own platoons are not drafted. After the campaign a platoon goes home as a roaming squad:
  Meitou (`KeepRoamers`) with its people, Faithful under the normal despawn.
- **Phases** off screen as the original (0.8 × radius arrival, 4 / 12 / 1 h, the 20 % rule, 3 h removal); the leader travels to the target
  and the others to it at the `travel speed unloaded` speeds (serial, outside the parallel movers). With `OffscreenFights` (Meitou) campaign
  stand-ins fight like other travellers and a squad lost there counts as not intact for the 20 % rule; Faithful: an unloaded assault wins at 4 h. Engine choice: a
  campaign with no platoon left is defeated. End effects: the messages and the trigger roll or the 40-96 h retry; nothing else changes.
- **Loaded**: campaign squads are platoons the AI does not run, so the campaign drives them; the phase's package is what the next population
  look does with `Phase` (the original installs it at the next AI update). Column (`GatherAndMove` as the original): the leader's trail is
  laid each look and follower i heads for its point i × slots × 150 / N back (engine choice for the Unknown: the trail point, also off screen);
  loaded followers jog, the leader marching out keeps its `travel speed loaded` (WALK walk, JOG run mode, RUN and GROUPED free, NO_SPEED_CHANGE
  as it was) while every intact platoon is within 6000, else it waits within 8294.4 of the target or walks. They fight through the normal aggro;
  arrival, timers and the 20 % rule run. **Observed** (headless, Dust Bandits, 4 squads of 49 at `raidSizeMult` 4, camera on the leader): the
  followers keep 190, 380 and 470 behind a walking leader (the rule's 187, 375, 562).
- **Assault**: in phase 1 a loaded campaign squad also fights (`Squad.War`, read by the aggro) everyone not of its faction in the target town when its
  `pt2` package is a Package_WarAssault (20, 32), else the target faction when the campaign is hostile. Looting is [goal 177](#carrying-rescue-and-looting) where the package lists it. A cannibal assault (class 32) goes to phase 2
  when three of its members carry someone or 15 game minutes after the first did (`CampaignInstance.CarryStart`, not saved); kidnapping: [below](#kidnapping).
- **Announcement**: a loaded leader in phase 0 says `dialog announcement` once (camera and a target-faction member within 2500): its lines are said as speech bubbles 2 + 0.12 × length seconds apart, or DA 12's wait, and their `interrupt` makes the next talk
  with it the negotiation, also when its faction is hostile. After arriving it waits for the announcement and then while the negotiation is
  pending, at most 1 game hour (engine choice for the Unknown event-2 state); off screen it goes on. Dialogue DA 37 sets phase 1 (2 without a
  pt2 leader package), DA 38 phase 2; DA 39 (phase 3, unhandled in the original, unused in the data) is not built. **Observed** (headless, real
  data): Dust Bandits "Invasion with demands" shout their four lines 20 s apart; talking to the boss opens the negotiation; paying takes 2000
  and sends them back (phase 2), refusing starts the assault (phase 1, the squad's targets set), stabbing him sends them back.
- **World states** (`WorldStates`, `PopulationSystem.States`): WORLD_EVENT_STATEs answered live by the original's rule
  ([world states](game/factions-squads-towns.md#relations-and-world-states)) from the unique table and the relations; the squad factory,
  wild sites, towns and campaigns all ask it. A population given its own `IWorldStates` (the pinned tests) uses that instead.
- **Save**: WAR_SAVESTATE read and written with the original's keys (unknown keys kept; numbered keys past the new counts removed); a
  characterless campaign stand-in is written as a PLATOON with content file `platoon/` so `p<i>` resolves; a loaded campaign platoon travels.
  The phase, fight, arrival, announcement and carry clocks are Meitou keys ([WAR_SAVESTATE](formats/save.md#war_savestate-type-9-faction-campaigns)), so
  a loaded campaign goes on where it was (the original restarts its phase); the trail is laid again, the announcer is not kept.
- **Observed** (headless, real data, `raidFrequencyMult` 1000): Dust Bandits "Invasion with demands" against an outpost 1500 from Dust King
  Tower gathers 1 squad of 12 for 10 men, arrives after 0.28 h, survives a save and load in phase 1 (campaign, id, phase, squads), wins at 4 h,
  is removed 3 h later and queues its follow-up. The 9 owner saves' 103 war states each come out with the same keys and values.

## Player

`PlayerSystem` / Orders: selection, stop, move/queued move, attack, treat/rest/medic, carry/rescue,
loot, stances and squad toggles are commands. Resolve empty-ID orders against current selection.
New-game start data seeds squad/appearance/gear/money; the start's characters are then remade in the
character editor while `PlayerState.InCreation` ([characters](characters.md#character-editor)); original start
relation/research semantics remain partly unknown. Picking/HUD are host responsibilities, never simulation input.
Squads: `SquadManagement` orders the player's squads (`Squad.Index`), keeps `PlayerState.Squad` the active one and applies the squads screen's
commands with the original's limits ([Squads tab](game/ui-screens.md#squads-tab)); the snapshot's `PlayerSquads` lists them for the UI.

### Order toggles

Passive/Hold/Block/Taunt/Jobs state belongs to the character (medic and rescue are job-list entries, [job lists](#production)) and relevant
systems; the rules follow [combat](game/combat.md#fight-lifecycle-and-orders). A player's character defends itself only without an order
(Idle, a bodyguard's Follow, or Loot with the attacker within 40) and against an attacker within √33000 ≈ 182; Passive does not stop that but keeps it
from joining others' defence and from attack on sight. Hold adds attack on sight while Idle (not Passive, sneaking or crawling), and every target of a
holder (sight, self-defence, re-pick) passes the post test (`Orders.HoldPost`); it walks to a target only within 80 (140 when the target fights it) of
its hold point (`CharacterCold.HoldPoint`: where Hold was turned on, then each ground move's destination; not saved: a loaded holder holds where it stands, as setStandingOrder 12 does; **Observed**: the loader replays the jobs switch through it, Verified; Hold assumed alike).
A toggle (button or hotkey) sets every selected character to the opposite of the first one's state, so a mixed selection ends uniform (engine choice; the original's rule is **Unknown**). Toggles and the run speed arrows also apply while paused ([time model](#time-model)).
New player characters start with Jobs and Ranged enabled. Jobs gates the job list and automatic rescue (the medic, robotics, splint and attacking-enemies jobs run regardless, as the original); direct orders still run. The saved `chase mode` is this switch. Saved toggles override
defaults, including explicit false values. Jobs is GAMESTATE_AI `jobs` ([perma-jobs](game/ai-tasks.md#perma-jobs)); Meitou's older `meitou jobs enabled` is read, not written.
Sneak: sneaking speed cap and clips, quieter, smaller view factor; a hostile looker sees the sneaker only once its
spot meter fills ([perception](game/perception.md#spotting-a-sneaker)); the exposure feeds stealth XP.
Ranged (on for every NPC): a crossbow carrier takes the crossbow at any distance and melees only when dry, in Block, or with a threat
(anyone targeting it) within 40; off, it never shoots. Taunt and Passive act through the target score (`Combat.TargetScore`: Taunt +2 and its crowd term ×0.35,
Passive −1), which both the aggro look and the fighters' re-pick use.
New games select the squad's first member. The game host also selects the first member after
loading, before publishing the initial snapshot; the save reader itself retains saved selection.

## Movement and paths

`MovementSystem`, `PursuitSystem`, `IAgentWalkability`: schedule/query asynchronously, publish at
boundaries, follow ordered path points, steer/separate using previous state and project to ground.
New move orders preserve reusable paths; attack pursuit reschedules target paths. Turning can
hold translation while facing changes. Original speeds/encumbrance and research are in
[pathfinding.md](game/pathfinding.md); footprint clearance/water costs come from nav queries.
Heights use the character's (or clicked) Y to pick stacked floors; long paths have a provisional tail.
Squad followers follow the original ([squads travelling together](game/pathfinding.md#squads-travelling-together)): `Formation.Layout` gives each
member a slot by its faction's `squad formation` (`World.FactionFormations`, RANDOM without data), kept in `Squad.FormationSlots` and rebuilt in
`MovementSystem.Inputs` (serial) when the formation, head or members change; the draws are keyed by squad and that key. A squad mate aims at its slot
turned onto `FollowHeading`, one second of the leader's travel ahead; anyone else (a follow order across squads, a contract's mission target) keeps
20..40 from its target. `FollowSpeedFor` is the original's desired speed. Engine choices: it is capped by the top speed only (not the speed mode);
a squad's slaves go with the head (Meitou's slave owner is a faction); CARAVAN side members are placed even without a SQUAD_2 member; the building-door
aim (`ShutOutAim`) counts a building as shut when all its doors are locked and whole, and takes the outer of the door's points (15 either side of the leaf) as the side away from the leader; within `DirectDistance` 50 it steers straight, farther, or for `FollowPathSeconds` 3 after a blocked step, it takes paths.
A `FollowOrder` (the context menu's Follow, order 31) makes a character follow another the same way (`CharacterCold.FollowOrder` replaces the leader; a leader following a squad mate makes that mate the formation's head) until a move order replaces it or the target is gone (then Idle); not saved.
Body contact (engine choice): standing bodies are solid over `BodyShare` 0.6 of the footprint (2.4 for a human). After its step, each
character takes half the overlap with each separation neighbour's last position (not one predicted from its velocity, which in a jam is wanted, not achieved), averaged over those it overlaps (a sum shakes a packed crowd); its own velocity into a body it already touches is dropped (pressing ranks would squash the front). A walker
shoves a stander aside; one in a bed or cage does not give; a sitter (resting or on a used seat, no path) is
never moved, by contact or separation (**Observed**: the original pushes no sitter), and is not solid (walkers steer past), lying and carried bodies are not in the way, a character closing on its spot
is not stopped, and an unwalkable result falls back to its own step.

## Navmesh

Own DotRecast builder/cache (`Meitou.Navigation`), not Havok runtime. Default cells 2, height 1,
48-cell tiles, agent height 18/max climb 5; ground/water/door areas. A* checks portal clearance
(radius per wall end, default radius 4), water factor and doors. `NavAdapter` exposes simulation interfaces;
`ZoneGate` holds population until ready. Collision and world placement rules:
[collision.md](formats/collision.md), [pathfinding.md](game/pathfinding.md).
Cache key: the zone's building hash, the build settings and a content hash (the data files in load order, `seeds.def` and the
height, blend, biome and overlay maps by size and write time, the DotRecast version). Unloading a zone skips its queued build;
a running one is still cached but not published, and its task is cancelled.

## Animation

`AnimationSystem` produces named layers/weights/times; `AnimationLibrary` supplies race clips,
lengths, masks and selection data. Movement, held care/lying clips, melee/ranged actions and hit
reactions use researched animation rules. Root motion advances with clip time; relocation keeps
separation by footprint radii and drops blocked components. Rendering consumes snapshot layers;
shape/skinning belongs to the character renderer. [animation.md](animation.md).
`PursuitSystem` sets `CharacterCold.CombatRunning` by the original's rule (body gap over `CombatTuning.RunGap` 90, off under
`FootworkGap` 70, on while the target flees; footprints stand in for the radii, the navmesh-blocked chase is not modelled). Running, a
fighter keeps the plain walk/run clips, full speed and the drawn weapon; otherwise it takes the combat clips and the signed speed, capped
at `FootworkSpeed` 18 ([combat](game/combat.md#combat-stance)). A move or follow order ends the character's fight (`CombatSystem.Inputs`:
target, stance and drawn weapon dropped), as the original's new task and movement goal do; it fights again only when attacked once idle.

## Bodies

`BodySystem` / `FirstAidSystem`, `Meitou.Data.Gameplay.Bodies`: race/stat/part factories,
medical tick, blood/hunger/parts, knockout/death, healing, limb state, move speed/encumbrance and
moving XP. Combat hits use the same body state; avoid double medical ticks. Treat/rest/medic
orders find eligible still patients, walk to reach and apply aid serially. Medical time scale
is explicit and uncertain; [character-stats.md](game/character-stats.md) owns original rules.

Limbs: `HealthPart.Limb` / `Replacement` / `ReplacementItem` per slot; saves round-trip them ([save.md](formats/save.md#limbs-medical_state-limbs)).
A severed limb queues (character, limb) on `CharacterTable.LimbsChanged`; between ticks the population system rebuilds the look
(`Loadout.Limbs`: stump or prosthetic mesh, natural limb hidden) and drops the race's severed-limb item (Ground items). Prosthetics are
fitted and removed by holding them onto or off the limbs window's slots (a `*_replace` section takes a LIMB_REPLACEMENT of its side on a stump; lifting a fitted one leaves a
stump; [held item](#items-and-eating), rules in [character-stats.md](game/character-stats.md)) or by a right click (`QuickMoveItem`).

Ground items: `World.GroundItems`, loose items: the save's (zone `1-itemlist`, read and written by `Saving/GroundItemSave`,
[save.md](formats/save.md#zone-files)) and the severed limbs combat drops; at most `GroundItems.Limit` dropped in play (default 1024, up to 4096 by the Advanced setting "Dropped items kept"; the oldest of those goes; read ones
are not capped); published with a revision. A dropped limb lies 5 units to its side of the character, an item dropped from the inventory 3 units ahead, both at a random
yaw and on the ground under that spot (`World.Walkability.GroundHeight`, not the character's own height). The original (`FUN_14064edc0`, **Verified** by decompile) makes the RACE `severed limbs`
item of the cut slot (`SeveredLimbItem`, an `Item` subclass) at the character's `bone name 2` bone of that part: position and orientation from the bone, in-world mode set
through the item's vfunc +0x228 (`FUN_14075d9b0`; its vfunc +0x2d0 `FUN_14075d660` stores them at `+0x48` / `+0xb0`; then `FUN_14075db20` and `FUN_14075e5d0`, which creates the physics object and toggles it by the item's `persistent` key, 1 to 3 only (limbs: 0),
**Observed**), and its PhysX object (a dynamic capsule from the item's `physics file`, [collision](formats/collision.md#item-physics)) falls from there. Engine choice: the host draws that fall (`GameHost.UpdateFalls`, display only; the simulation keeps the rest point): from the stump's bone (`bone name 2`, sampled once its character is drawn), free fall at 98.1 from rest, a drift to the rest point, then a 0.25 s tip; the placement comes on landing (none in the animation sandbox, where the piece stays drawn). Where the original's limb comes to rest is **Unknown**. Otherwise the host lays a dropped item as it would rest
(`GameHost.LaidFlat`): its mesh bounds' thinnest axis up (the severed limb meshes are plain static meshes lying along +X, bounds read equal to their vertices, **Verified** on every `severed limbs` mesh of the install's
merged data), its yaw, tilted to the terrain slope under it (the heights round the drop point over the item's own size; not on a building's floor, where the simulation's ground is used flat), and set down so that none of a
5x5x5 lattice of points of its bounds is below the rendered terrain under it. A weapon, modelled along +Y, lies on the flat of its blade. Read items are drawn by their saved position and whole rotation. The host adds each as a runtime world-object
placement of its record's `ground mesh` / `mesh` (`GameHost.GroundItems`, `WorldObjectLayout.Item`). A right click on one (within 24 px of its
projected middle, pointer `pick_item`) gives `PickUpItemOrder`: the nearest able selected character walks to it and, within 15 units
(`PlayerSystem.PickUpReach`), puts it in its inventory (`Inventory.Add`), or when it does not fit, wears it in its free equipment slot (`InventoryCommands.TryEquip`); what does not fit
stays on the ground ("Not enough room in my inventory"). Another order or a stop ends the walk. A picked-up read item keeps its ITEM record's keys in its inventory record. Engine choices.

Town interiors' loose items (`Items/ItemSlots`, the original's rules in [loose items](game/factions-squads-towns.md#loose-items-in-buildings)): each ITEM_PLACEMENT_GROUP of a town
building's interior layout (`TownBuilding.ItemSlots`, id `<building>/item<n>`) is a slot registered with the town's fixtures. A due empty slot in an active zone gets one item
(weighted pick, a weapon with a manufacturer and model, armour with the grade quality of the owner faction's armour chances: engine reading) laid flat at the slot, turned at
random for `random yaw`, owned by the building's faction (`GroundItem.OwnedBy`) and not counted against the limit; when it leaves the ground the next is due in an int of
[min, max] game hours. Builders fetching materials skip items another faction owns; taking one is stealing, as any owned loose item. Engine choices: slot items are saved as plain
loose items (`ownedby` null) and a reload binds a saved item lying within 2 of a slot (one of its group's records) to it; respawn deadlines are not saved (a taken slot is due at
once after a reload); upper-floor items are not hidden when a lower floor is viewed. The original, loading such a save, generates the interiors again beside those items.

## Items and eating

`Inventory`, `ItemFactory`, `FeedSystem`: grids, stack limits, backpacks, quantities/charges,
load and eating. ITEM `stackable` is an integer, not boolean; gear stacks cap at one.
Transfer/split/top-up through inventory APIs. Eating follows the original's auto-eat (own items, backpack,
squad mates' backpacks; bites of 50) and NPCs are fed by their hunger floor, not rations:
[character-stats.md](game/character-stats.md#eating). Container bonuses: [economy.md](game/economy.md).

Inventory moves validate snapshot identity, bounds, occupancy, stack limits and equipment
type/armour slot. Weapon sections are 10×2 (back) and 7×1 (hip); other equipment slots hold
one compatible item (the belt: an ITEM with `slot` 14, the lanterns, [lights](formats/lights.md#carried-lanterns)). These are engine constraints, not a complete original-game rule set.
Backpacks move intact between pockets and their attachment slot; a bag goes inside a bag only empty. Equipment moves refresh worn appearance and combat gear; pocket armour
does not grant worn bonuses. Race limits apply to every equipment and prosthetic slot of a character (drag, right click, trade, loot, captive clothing; `ItemInstance.WearableBy`, rule in [holding an item](game/ui-screens.md#6-inventory-trade-dialogue-build)); worn items are never taken off for it. `InventoryCommands.Arrange` and `Inventory.AddTo` follow the original's ARRANGE and auto-add; a limited storage's
stacks start from 1 (`Inventory.Limited`). Rules: [holding an item](game/ui-screens.md#6-inventory-trade-dialogue-build).

Held item (`Items/HeldItems.cs`, part of `PlayerSystem`): `PlayerState.Held` is the item on the mouse with its origin (window `ItemOwner`: a character, the open trade's stock
or a site; its root inventory, section and cell) and the character holding it. `LiftItem` takes it out (the whole stack or one unit; a fitted prosthetic comes off; a machine slot's
units are made into an item), `PlaceHeld` places, merges or swaps it (the swapped item becomes the held one), `GiveHeld` adds it to a character in range, `DropHeld` puts it on the
ground (own windows only), `ReturnHeld` puts it back (old cell, else `Inventory.Add`, else after arranging, else the ground). Stock goes only to the customer and is bought when placed
(`TradeSystem.AffordHeld` / `PayHeld`, a stolen sale's `CaughtSelling`); out of the open loot it is marked stolen as a loot is and may butcher the body (`CarrySystem.TookHeld`); out of
another faction's container it is marked stolen. It goes back each tick when its window closed or its holder is down or gone (`KeepHeld`) and before a save (`World.ReturnHeldItem`,
`SaveCapture.Capture`). Hashed (record and quantity), not saved; the snapshot publishes `Held` and the serial of the last held command applied (`HeldSerial`).

## Combat

`CombatSystem`, `CombatDatabase`, `Fighter`: attack scheduling, equipment/techniques, stance,
block/dodge, armour and per-part damage, stun/KO/death, combat XP. Read previous combat state;
ordered effects apply hits. `TickMedical=false` with BodySystem, `SelfApproach=false` with pursuit.
A fighter faces its target only while it has no pursuit path; on one, movement steers (facing the target each tick
undid the turn to a waypoint over 90° off the line to it, and the walker froze).
Original formulas, constants and unresolved technique terms: [combat.md](game/combat.md).
Hits, pauses and initiative follow combat.md "Hit reactions" / "Pacing"; the combat system picks the stumble
clip (through the animation system's chooser) and holds the fighter for its length. No wait before the first swing, a swing with nobody in its attack zone waits 0.5 s and the loser of the
initiative roll 0.5 s; each blow (its packet times the hit's `power`) resolves against everyone in its attack zone, slaves in the original's case and walls and
doors included (`CombatSystem.Strike`, `ApplyZoneBuildings`, combat.md "Attack zone"). Fighters in their footwork face their target and side-step
([animation](animation.md#side-steps)). Engine choices: the initiative roll is drawn per pair and tick so both
sides agree; a target that would not swing back (stumbling, defending, not fighting, or in
Block) is simply attacked; one that lost the roll to a fighter becomes its target; a defence that starts late
joins its clip partway so the passed roll counts when "Late dodges and blocks" is on (Advanced setting, a balance
change: off by default and in both presets);
the attack technique, attack slots, the 5-fighter engaged list and circling follow combat.md "Attack technique choice" /
"Attack slots and circling" with the engine readings listed there (Advanced setting "Attackers per target", 1-5: Faithful 1, the
CONSTANTS value and the default; Meitou 3); the block break-out ignores the 10% lower bound; only a heavy bolt staggers.
Close in, fighters move by the original's force-based fight steering (combat.md "Fight steering"), not paths; from afar or without a clear line they walk paths to their technique reach (`CombatTuning.StandGap`). A landed or
blocked blow takes a defender fighting the attacker off the attacker's engaged list (`FUN_14060a2f0`), so it asks again.
Target choice (`CombatSystem.ChooseTarget`, combat.md "Target selection"): a fighter whose target goes down chooses again at once and ends its fight
only when no candidate is left; while deciding it chooses again every 1 s (0.3 s on Hold), except for a player's pick, and ends the fight when even
its current target fails (a holder's that left the post test, a player's beyond 400). Candidates are its current target (any faction), its attackers and, unless Passive, the attackers of squad mates within `JoinRange` (+10 for one whose mate does not fight it back and that has
under 2 attackers: engine reading of the protectee); not of its faction or squad. The current target must be sensed: within sight range (a sneaker: in view) now, or within the sense entry's lifetime
since (30 / 60 / 120 units of 2 s, combat.md "Leaving a fight"; `CombatSlot.SensedTick`); an NPC's chase may give it up sooner (`AggroSystem`, 24 s unsensed). "Intended aggression ≤ 0" is a fight-off candidate not counting the chooser's faction an enemy (engine
reading); getting up is `CharacterCold.GettingUpUntil`, from waking for `CombatSystem.GetUpSeconds` (the get-up blend and `standing up 3` to `GetUpEnd`),
during which it neither walks, works nor fights (Task_GetUp; it counts as staggered for the work systems).
Only a fighter blocks or dodges. A landed blow on a shackled defender knocks it out 25% of the time (`PutDownKoSeconds` with 0.002: 3 s). A hit on a
character outside a fight that is getting up does nothing more; on any other (not a gunner) it restarts the task (timer, lift or knock-out swing, and a
new path from where it stands; a lock's, site's or machine's progress stays with the object), and a picked stumble clip stops its walking and its work
(first aid, lockpicking, building, operating) until the clip ends (`CharacterCold.StaggeredUntil`). A fight's end puts no weapon away: a player's character sheathes once it is neither walking nor following on an order,
an NPC by its planner's UNEQUIP_WEAPON step (one without an AI at once).

### Ranged

Crossbow reload/steady aim, skill spread, bolt flight, ammo, obstruction and friendly hits are
simulation state; `Schedule` advances bolts. Targets without medical bodies do not stop bolts. Bolts and the line of fire stop at the
terrain heightmap and the buildings' collision triangles (the ragdolls' environment, `SolidHits`), not the walkable surfaces (a roof over
the shooter counted as ground). A building is there once its zone's collision has been read on a worker; until then bolts pass it, which
breaks determinism for those first shots.
Ranged state is hashed when used. Weapon/turret research: [combat.md](game/combat.md).

Turrets (`CombatSystem.Turrets.cs`, facts: [combat.md](game/combat.md#turrets)): one serial step after the bolts fly finds the gunners and runs each manned turret in key
order: target choice, reload, aim point while loaded, turning the parts (`TurretRig`), and the fire decision (in range from the gunner, aimed within 1°, steady 0.4 s, line
along the barrel clear); harpoons are the gunner's bolts (hits, XP, crimes). A gunner is a player character operating its turret (`CharacterTask.Operate`, below) or an NPC
standing at the turret its squad claimed (AI turret goals); one per turret (lowest slot). `CharacterCold.Gunning` names it (derived); the aggro system starts no fight with a
turret guard and retaliation skips a gunner shot at. Turret state (angles, aim, loaded harpoons, reload, target) is hashed once a turret was manned; only the loaded harpoons are saved
(`current shots`, as the exe; [save](formats/save.md#faction-trust-war-flags-bed-rent-and-turrets)), read when the turret is first manned after loading. Engine choices: NPC turrets have efficiency 1 (no NPC power grid); a player turret's efficiency is its machine's power grant
(0 unpowered) and has no mount bonus; the reload belongs to the gunner (a new gunner restarts it); every candidate within 1.5 × range counts as sensed and seen; "fights the
gunner" is the × 1.5 (`FUN_1405ca6f0`); a down target is "slot 0x368"; the turret's facing is its yaw; no Shoot first pass (no such option); the muzzle is the last turning
part's; the turret's own collision is not left out of the line or the flight; the shot sound is a pooled object at the muzzle. The snapshot carries the part angles
(`MannedTurret.Angles`) and the renderer turns the meshes by them ([render-objects](render-objects.md#turrets)). Meitou fix (Fixes page `Turret gunners turn`, on by
default): the turret step sets the gunner's yaw along the barrel every tick; off, it faces the turret as `Task_UseTurret` does (player: the building's place; NPC: its yaw).

### Animals

`ANIMAL_CHARACTER`, animal body/stats/size/fists and creature techniques come from typed data;
animals use faction hostility and carrying/damage rules where eligible. They age and grow, pack beasts spawn with their
bag on, DA 48 sells them, and the context menu's Follow keeps any player character beside another
([animals](game/animals.md#ageing)). Player animals feed themselves: food on the ground (259, `FeedSystem.FetchFood`), food from their outpost's stores
(243, also for people, `TownDutySystem.LookPlayerFood`, [rules](game/character-stats.md#eating)) and limbs (277); `CaptiveSystem.RunPlayerAnimalGoals` also runs their limb play and chase
(278, 279) and eating downed non-allies (233) as [captive jobs](#captives) when idle, ended by any player order. Corpses (230) are dropped from the player's animals by the original;
crops (232) find nothing for them (only the player's farms are machines, and the eater must not get along with the town). Unarmed humans use
martial-arts category 5. [animals.md](game/animals.md), [characters.md](characters.md).

### Hostility and relations

`AggroSystem`, `RetaliationSystem`, `FleeSystem`: previous-position sight/hearing, faction
relations, shared targets, idle-player self-defence ([order toggles](#order-toggles)), chase give-up and return to routine. The look picks
the best `Combat.TargetScore` (the original's attack-on-sight finder), not the nearest.
Being taken as a target is answered the same tick when it alerts ([being targeted](game/combat.md#fight-lifecycle-and-orders)), the rest every 15 ticks;
free squad mates within `JoinRange` (250, engine choice; the original's joiner must sense the mate) join an attacked member's defence each check even while it is
already fighting or down (engine rule), against an attacker within 150 of it (`AI::findAttackerOf`); an NPC with an AI joins this way only with a protect goal
(143 PROTECT_OWN_SQUAD, PROTECT_ALLIES and the like) and only for a mate that fights, else through its stance or not at all (`RetaliationSystem.ProtectsMates`).
An NPC with PROTECT_ALLIES (15, 288) in its lists and hours (`AiMind.ProtectsAllies`, apart from the stance, which keeps the highest goal) also joins any squad's defender of its own or an
allied faction that fights, when it senses it (sight test within R, or within 600 in its own place, where a fight is heard for sure), against an attacker within 150 of it that is not its ally, whatever the relation
(`RetaliationSystem.ProtectAllies`, [combat goals](game/ai-tasks.md#combat-goals)); a gunner stays at its turret, 288's town limit is not modelled. So gate guards fight
the Skimmers (relation 0 to every faction, `ATTACK_ENEMIES_AND_NEUTRALS`) that attack townsfolk.
Orders defer to the next tick. Relations are live state and deed/witness effects have distinct
paths; preserve current ordering.
Relation events (`FactionDeeds`, built with the aggro part; [factions 3.5](game/factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile)):
every landed melee blow and bolt, the cagings of the player, the police and kidnappers, and first aid on an unconscious member, for
any two factions, weighted by the victim's `faction importance` and the town it stands in (`PopulationData.TownAt`, its squad's
`Town`). Engine choices: every striker rates above the 0.1 / 0.15 thresholds; a death is credited only at the killing blow (a later
bleed-out has no killer); first-aid gratitude on every bandaging update. The attack order and looting change no relation. A
witness that regards the culprit as friendly takes 20 off on every sweep (`CrimeSystem.BetrayalDelta`). Wars:
`PlayerRelations.DeclareWar` / `EndWar` (one direction, the speaker's table; the war flag is not saved). Original threshold/sense/retreat facts:
[perception.md](game/perception.md), [AI](game/ai.md), [factions](game/factions-squads-towns.md).

## Carrying, rescue and looting

`CarrySystem`: serial links, pickup/put-down, dropping on KO/death/attack, rescue jobs and inventory
looting. Carried bodies follow the carrier and affect load. The player's Pick Up also lifts an awake own character that carries no one (`CarrySystem.CanBeLifted(World, …)`);
lifted by a friendly carrier it leaves its fight and task and rides (`CharacterCold.CarriedAwake`) until put down, neither fighting nor treating (engine choices; a body
lifted down is still set down when it comes round). Loot can raise witnessed theft and
stolen ownership marks. [carry-loot.md](game/carry-loot.md). Loot window (engine choice):
`OpenLootOrder` walks the looter over (one ordered mid-fight leaves the fight, as with `LootOrder`), then `PlayerState.OpenLoot` publishes the body's whole inventory
(`WorldSnapshot.OpenLoot`, the body's `ItemGrid`: grids, bag and worn gear); items are held across with the mouse ([held item](#items-and-eating)),
or right-clicked over (`TakeLootItem` / `PutLootItem`, `InventoryCommands.MoveInto`; taking counts as a loot for theft, locked-on items stay, moved gear re-dresses both); it closes as a container does, or when the
body comes round (another faction's) or is lifted. The same window opens on the player's own standing character (Trade, `CarrySystem.CanBeOpened`); both characters' bags open with it, the windows in one
row (**Observed**, owner's screenshots of the original, 2026-10-24; Meitou: limbs, own bag, own character, theirs, their bag, centred, and a bag the window opened closes with it unless OPEN BAG was pressed); a death item taken butchers only a down body. An animal's inventory and loot windows use `Kenshi_InventoryAnimalWindow` (backpack slot,
main grid, weight and burden only, grid size from its `inventory w` / `inventory h`; **Observed** in the original). It also runs the stealth knock-out
(`StealthKo` task, reusing `CarryTarget`/`LiftTime`): [combat](game/combat.md#stealth-knock-out).
- **NPC looting** (`CarrySystem.Scavenge.cs`, facts: [carry-loot 3.3](game/carry-loot.md#33-npc-looting)): every `AutoEveryTicks`, an NPC the AI runs whose package lists goal 177 for its role
  in the hour (`PopulationSystem.GoalDuty`) loots the nearest dead body (not of its faction), else downed living one of a non-allied faction, within 100: the player's only with
  `PlayerState.BanditsLoot` (CAMERA `bl`, default on), only whom `shouldIScrewThisGuyOver` allows (classes, love/hate tags), not one its squad tagged short-term 6
  (`MemoryTag.Looted`). At the body (`Loot` task, `LiftTime` from arrival, `medic` clip) it takes after 8 s (animal 20): weapons when it has none equipped, food (functions 3, 15, the
  backpack's 3), and for a non-player-side or dead body, unless it is a Bandit or malnourished (floor < 2.8), every item and armour; a weaponless non-bandit grabs one weapon of a
  living player-side body at 0.8 s instead. Then the tag, and back to its task. On arrival it says event 33 or 32 and may get a pending Looting (`CrimeSystem.NpcLooting`,
  [looting crime](game/carry-loot.md#33-npc-looting)), which its witnesses answer as any NPC culprit's ([crimes](#crimes-and-bounties)). A police-like looter (`CrimeSystem.IsPoliceLike`) confiscates instead: past 0.8 s one unit of each stolen item of the body's inventory (`CarrySystem.Confiscate`), tagging it only when there was none. Engine choices: nearest per pass, the tag never expires.
- **Unarmed NPCs** (`CarrySystem.Arm.cs`, goal 42 "Don't be unarmed" with step 8 FIND_WEAPON, `AI::findWeapon`): on the same cadence an NPC without a weapon, with a working arm,
  whose package lists 42, goes for the nearest free ground weapon within 100 (in a town only an unowned or enemy-owned one) and picks it up, else a dead body with a weapon,
  else a downed living non-ally with one (player side only with `BanditsLoot`, not one its squad loves), which it loots as above. Engine choices: the body loot takes
  more than the weapon; the nearest body, not the sensed-list order; accessibility left to the walk.

## Ragdolls

`Ragdolls/RagdollSystem` (game behaviour: [ragdolls](game/ragdolls.md)) runs serially after animation
when the host passes `CharacterRigs` (the game does; tests and goldens do not). Engine choices:
- **Physics**: BepuPhysics2, one scene stepped on the calling thread below 256 awake bodies, otherwise on the world's thread count (deterministic, 2 solver
  iterations, 4 substeps per tick, fallback batch threshold 1024: Bepu 2.4.0's fallback batch throws
  when its last contact on a dynamic body is removed with one on a kinematic body, which big battles hit at the default 64). Bodies and joints come from the race's ragdoll `.phs`
  (`RagdollDefinition`, scaled by the animal size); revolute → hinge + twist limit, spherical →
  ball socket + swing + twist limits, fixed → weld. Joined pairs do not collide.
- **Parts**: `Whole` when incapacitated, `CarryMode` when carried, a human's downed limbs by their
  LOCATIONAL_DAMAGE `collapse part` (not robotic ones). Bodies outside the mask are kinematic, driven
  by velocity to the animated pose. Animation holds its layers while the whole body is a ragdoll.
- **Carried bodies**: `CarryMode` simulates the forearms and the head only, as the original does ([ragdolls](game/ragdolls.md#bodies-kinematic-or-simulated)); everything else follows the `carry me` clip. Ragdoll bodies turn at most 7 rad/s and the head's neck joint is sprung ([ragdolls](game/ragdolls.md#limits-of-motion-and-joint-springs)).
- **Published pose**: `CharacterSnapshot.Ragdoll` (model-space `BonePose` overrides, weight for the
  get-up blend); the renderer poses with it. While `Whole`, the character's position follows the root.
- **Scene**: 32-unit tiles round each moving ragdoll, terrain sampled every 4 units (128 triangles per tile), plus building
  collision from `Meitou.Navigation.RagdollGeometry` (per zone on a worker, doors and passable parts
  skipped, ≤16 zones). A tile has ground only until its zones are gathered. Tiles unused for 1,800 ticks (one minute at 30 Hz) go.
  **Budgets** (`RagdollSystem.TileBudget`, `StartBudget`, `RagdollPhysics.BuildingUnits`): each tick has 8 tile units
  (ground mesh 1, building mesh 4, charged only when built; a final building mesh can exhaust the remaining units)
  and starts at most 4 whole bodies falling. Starts wait for ground coverage within 12 units scaled by animal size;
  deferred starts retain their saved pose. Frozen save restores, get-ups, limb dangles and lifts never wait for the start budget.
  **Observed** (upstream headless flight probe): 2-unit tile meshes cost 0.45 ms, whole-body builds 0.7 ms;
  single-threaded stepping cost about the same below 300 awake bodies (0.4-0.8 ms), avoiding dispatcher waits of 16-85 ms.
  Remaining heavy frames are partly attributed to GC and dispatcher waits; other causes are **Unknown**.
  The terrain is the full-resolution heightmap (blocks of 64 samples read on demand), not the simulation's `GroundHeights`:
  outside the area loaded at the start that is a coarse grid (a sample per 144 units) off the real ground by 2.4 units
  at the median and 15 at the 90th percentile over the land, so bodies met a ground the character was not standing on.
- **Falling through**: the ground is a one-sided mesh. A new whole-body ragdoll with a body centre under the terrain (a
  foot on a slope; 4% of starts) is lifted as one to 0.25 above it (up to 8 units); speed is capped at 200 units/s and
  spin at 30 rad/s and the contact margin is 8 units (Bepu, 30 Hz ticks: a thin limb moving more than its radius per tick
  tunnels); a dynamic body that crosses from above the terrain to more than 1 unit under it is put back on it.
- **Hash**: mask, frozen, age, blend and body poses in system order.

- **Save** (Meitou key, [ragdolls](game/ragdolls.md#saves)): a whole-body ragdoll's body poses, frozen flag and age (`Saving/RagdollSave`); the loaded
  character's first ragdoll starts from them when its root lies within 20 of where it stands, else (and for carried or limb ragdolls) from the animation.

Gaps: ragdolls collide with each other but not with characters that have none (as the original if its groups 0/1 are the characters' shapes:
ragdoll group 3/4 skips them, [ragdolls](game/ragdolls.md#collision-groups)); the building gather's
timing can change a live game's hash (goldens have no environment).

## Crimes and bounties

`CrimeSystem`: pending deeds, witnesses, booking, police eligibility, faction ledgers and expiry.
Carry/combat report deeds; serial sweep observes current positions. Bounty expiry uses
`0.04 hours/cat`, permanent at >=10000. Original categories, witness rules, imprisonment and
remaining uncertainties: [economy.md](game/economy.md). Expiry is paused while imprisoned. The police's response:
[Arrest and jail](#arrest-and-jail).
Witness sight: the original's sight test with places and light ([perception](game/perception.md#sight), `SightMap`); a
sneaking culprit is seen only once the witness's spot meter is full ([perception](game/perception.md#spotting-a-sneaker);
meters kept by `CrimeSystem`, not saved).
NPC culprits (engine choices): an NPC's non-defensive attack order, and each of its landed blows, sets a pending Assault (not an
animal's; the victim may be a player character), and an NPC looter its Looting ([NPC looting](#carrying-rescue-and-looting)); neither is booked
(other codes are refused for NPCs). A police-like witness not fighting re-fires the witness event (41 for an Assault, 42 for a Looting) at it every
sweep, since the guards' lines (e.g. "Shek guard witness crime": DA 29, 33, 6) need it within 260 and not running. So a town's
guards turn on hungry bandits (Starving Bandits, -10 to all: not hostile, so no aggro target) attacking someone there.
Trespassing (`Trespasses`, [economy 11.2](game/economy.md#112-how-a-bounty-is-gained)): a player character inside
(`SightMap.BuildingOf`, not on the roof) a town building that a squad lives in is a suspect each sweep; a witness that sees it and
regards its faction as neither ally nor enemy sets the pending crime when it lives there, its squad has locked up
(`LockSystem.IsHouseLocked`, the lock-up round's flag; the original also sets it when a resident closes a lockable door) and the
building is not open (template `public day`, kept as `Blackboard.HomePublic`; open 7:00-22:59); or when it is police-like, the
building is not public, its residents' faction is not an ally, and the culprit is a slave or the residents have locked up. The
witness rules above then fire event 17 and a police-like witness books 100. The MY_INTRUDER memory tag is not kept (nothing reads
it). DA 33 CRIME_ALARM (`CrimeSystem.Alarm`): the speaker attacks the target, the target's crime timer is 30 and it is recently
wanted; a speaker of a town squad sets Trespassing on a target with nothing pending inside its private home, and raises the town alarm for
the target's pending crime, which the town's investigators answer ([town alarms](game/economy.md#town-alarms)).
Stealing (`CrimeSystem.Steal`, [carry-loot](game/carry-loot.md#theft-and-witnesses)): taking from another faction's container (right click: Theft;
drag: Looting; give to a character: Theft) sets the pending crime, then the steal roll; caught, the item stays. Pickpocketing (`CrimeSystem.Pickpocket`):
Loot on another faction's sleeper or prisoner opens its inventory (`CarrySystem.CanBePickpocketed`). Every take out of another faction's
character sets the call site's code (`CrimeSystem.TakeCode`: right click, drag, give) and rolls with the owner watching (a playing-dead owner
too); the take-all (`LootOrder`) keeps the Looting of `CrimeSystem.Loot`. Floors are `SightMap.FloorOf` / `FloorAt`. Engine choices: every squad
living in the building watches (Meitou may house more than one), the window closes when the sleeper wakes. A stolen ground pick-up inside
a building is a pending Theft with thievery XP 20, no roll.

## Cages, beds and locks

`Locks/`: `World.Fixtures` (`FixtureRegistry`) holds every door, `has lock` container, cage (function 8, pole/peeler
`Restrains`) and bed of a town (a destroyed building: no doors or own lock, `TownBuilding.Destroyed`), registered once when the population first spawns the town (`RegisterSite`), kept on
unload, hashed only once non-empty. Facts: [locks](game/locks.md), [economy 11.5/11.7](game/economy.md#115-bail-and-sentence).
- **Player orders on locks and cages** (`LockSystem.Force.cs`, `LockSystem.Cages.cs`; [locks](game/locks.md#player-orders-on-locks)): `ForceLockOrder` (Use Tools / Use Strength / Smash), `SetFreeOrder`, `GetInCageOrder`, `PutInCageOrder.Cage`,
  `PickLockOrder.Cage`; cages are published as `WorldSnapshot.Cages` and a prisoner's `PrisonView` names its cage and owner. Per-character state `ForcingLock`, `BashOrdered`, `Freeing`, `EnteringCage` is hashed only while set.
- **Ids** (engine): a building's own lock = its instance id; door i `<inst>#i`; interior item `<inst>/<layout index>`
  (`TownBuildings` collects them as `TownFixture`s); bed k `<inst>@bed<k>`. Owner: the placement's `owner faction ID`, else
  the town's faction. Lock levels roll from `Rng.Hash(seed, StableHash(id), Lock, 0)`: load order never changes them.
- **Doors**: a building no save has starts from its level-data GAMESTATE_BUILDING (`TownBuilding.LevelState`: lock levels and door
  state; only doors with a `state` key count as restored). `AiSystem` applies the template's `initial door state` when it assigns a home (first squad, not to restored
  doors). `IDoorGate` (the game's `NavAdapter` -> `NavDoors`, reset on `ReplaceWorld`) closes a building for every path while
  all its doors are effectively locked and none broken (engine: one nav door state per building; closed-unlocked doors stay passable).
  Motion (`LockSystem.Doors.cs`, serial `SlowWorld`): `StepDoors` moves opening/closing leaves by `door move speed` per second and
  locks a closed one that `wants lock`; locking a home's doors closes them (animated, the lock holds at once). Engine stand-in for
  Task_OpenDoor: every 3 ticks a walker within 40 of a closed door whose current or next path leg crosses the leaf's plane within
  its half width + 6 (`IDoorGate.DoorSpotOf`: the door collision's box via `NavDoorGeometry`; headless: none, so no walk-through opening) opens it;
  at an effectively locked one a walker within 15 whose current leg crosses it stops ("It's locked." for the player). Both measure the walker's height from the
  leaf's foot (`DoorSpot.Foot`, the box's bottom; within 40): measured from its centre, tall gates (Stack's leaf centre is 45 up) were never opened and walked through shut.
  Engine stand-in for the original's door faces, closed until `doa` 0.6 ([locks](game/locks.md#doors) "Navigation"): every tick a door less than 0.6 open is solid
  (`HoldAtLeaves`), so a character that moved into or through its leaf from one side within its width is put back at its footprint radius and waits while the leaf opens
  (**Observed**, real-data probe 2026-10-21: a walker at a Stack gate waits about 3 s and passes at 0.62). **Meitou difference**
  (the original closes no door behind a walker, [locks](game/locks.md#doors)): with `DoorsCloseBehind` (Advanced setting "Doors close behind",
  on; off = Faithful) a door a walker opened closes 5 game seconds after the last character crossed it or stood in its leaf box (depth 12);
  a door that `wants lock` (gates) is left open, since closing would lock it and a locked gate shuts its building for every path.
  `AutoOpened`/`LastPass` are engine state, not saved (a loaded open door stays open), hashed only while set.
  The player's finished buildings register their doors (`RegisterPlayerBuilding`, site -1, unregistered when no longer finished;
  keys written by `BuildingSave`), and a cage or `has lock` building its own lock ([player-built locks](game/locks.md#lock-object)); the site menu has Open/Close Doors (`ToggleDoorsOrder`) and Lock/Unlock Doors
  (`ToggleDoorLocksOrder`), the door panel's buttons for all doors at once. Any door: `DoorOrder` (a quick right click,
  [ui-input](game/ui-input.md)) walks the nearest selected to the leaf's near side (12 out, reach 20) and opens or closes it, or picks another
  faction's locked lock (`PickingLock` on the door; a door is worked from that spot). `WorldSnapshot.DoorPicks` lists every door's leaf for picking
  (rebuilt when buildings register or go); `WorldSnapshot.Doors` publishes the player's doors and every door that is not closed and unlocked; the object renderer slides door parts by `DoorLeaf.LocalOffset` ([render-objects](render-objects.md)).
- **Containers** (`OpenContainerOrder`, [locks](game/locks.md) "Containers"): the opener opens it only within 20 of its height (engine).
- **Object walks** (`MovementSystem.GoToFloor`): move orders and the player's walks to a container, ground item, door, cage, forced lock or operated machine go onto the
  object's own floor (`FloorNear` its height within 20, the goal height set); without it a goal over a furniture hole resolved to the storey above.
- **Cages/beds**: `FindCage` (original weights; engine: skips a cage the carrier can't reach, `Reachable`: its building
  shut by another faction's doors, or a walk of that faction there failed in the last 24 game hours, `MarkUnreachable`;
  no path query runs in a tick), `Imprison` (state 2, `CharacterTable.Place` at the item's origin, cage
  locked, sentence; optional carrier; every caging is in `CagingsOf(tick)` and the `Caged` event, the bounty-claim hook),
  `LiftOut` (a `PickUpOrder` on the occupant of an unlocked cage, awake or down, engine; out of another faction's cage
  the lifter gets a pending code 7 and the freed one Escaping Prison + `Escaped`), `Release` (unlock, clear, erase the releaser's and jail's bounty entries for a booking releaser),
  `GetOut` (refused while locked; Escaping Prison unless served), `EnterBed`/`LeaveBed`.
- **Bashing gates** (`LockSystem.Gates`, `GatePlan`; rules in [locks](game/locks.md#cutting-and-breaking-285--185-cut-286--186-break), codes in
  [gate codes](game/pathfinding.md#gate-codes)): `LockSystem.Regions` holds the latest `GateRegionsCommand` (null: nothing is blocked). At each bash check an NPC
  attacker (as for doors) whose target's code differs from its own, behind a route that is not open (`RouteState`: the widest path over the gates, each gate's
  state from its door fixtures: broken/open 1, closed 2, locked 3, none registered (a town not spawned) 1; codes no gate joins count as 3, engine choices), plans row 4 with `GatePlan`: 90 from the
  codes, 122's finder the first gate on the route (nearest of the equally good), 123/290/81 at it (91: within 45 of the leaf, x2 standing; crowdLimit8: under
  8 bashers on it), the flagged search (restricted rows allowed). A plan that bashes sets `Bashing` to the gate's main door, and the door basher takes over,
  standing on the attacker's side of the leaf. Gate state 2 needs no plan (Meitou walks through closed unlocked doors). Checked before the house doors.
  Coverage: `GateRegionTests` (labelling), `DoorTests.A_bandit_outside_a_locked_gate_plans_to_bash_it_and_none_once_it_is_broken` (the plan).
- **Beds** (`Locks/BedSystem`, serial; facts in [locks](game/locks.md#beds)): bed fixtures are the town interiors' beds and, registered with the player's
  buildings (`<site>@bed0`, site -1), the beds the player built; each keeps its `BedData` (function, clip, construction materials). `UseBedOrder` (258: a quick
  right click on a bed, or its held menu's "Sleep") sends the nearest awake, non-carrying selected character; `PutInBedOrder` (99: "Put in Bed" on the bed's menu,
  or, engine stand-in, on a carried body's menu with the bed search; a quick click while carrying, engine) sends carriers, the cheaper rent paid, the body let go
  and laid in. In reach (`use range` + 15, on the bed's floor within 20, or a walk ended within 60) it pays the rent (`Cost`/`Pay`, player only; the 24 h memo
  is engine state, not saved) and enters; 3 short walks give the bed up (`MarkUnreachable`). In a bed the task is `CharacterTask.Sleep` (held still, the
  bed's clip, `Sleeping` on the HUD); any other task gets it up (`GetUp`; a knock-out or death in bed keeps it there). The player's bed search `FindBed`
  follows the original's weights over beds within `BedSearchRange` 3953 (engine, for the town). Job 106 (in the list with the rescue job): an idle carrier of
  a non-hostile body lays it in the searched bed. Lifting a sleeper takes it out. Engine differences: no power test; the rescue job (148) still runs without a free bed.
  Saves: `HeldSave` (a player-built bed also writes the original's `in what` = its building's handle; read back through `Bind`); a loaded sleeper's task is Sleep.
- **AI sleepers** (going to bed, 153/239, which plan into 98 Sleeping, `Task_UseBed`): arrived at their home's bed spot, the serial step (`AiSystem.LieDown`) lies
  them in that bed, else in the nearest free bed of the building within 30 (engine choice); with none free they rest on the floor there. A goal of any other
  behaviour gets them out (`LeaveBed`, in the think).
  `CharacterCold.InSomething`/`InWhat` name the fixture; an imprisoned character is skipped by AI, movement (held still),
  aggro, answering, `CombatSystem.Engage` as attacker (it can still be attacked) and carrying. Sentence: `SentenceStart`
  (`World.GameHours`) + `SentenceLength`; served when start + length <= now; no bounty with the cage owner: none (served).
- **Lockpicking** (`Lockpicking`, serial; the player's order is in [Arrest and jail](#arrest-and-jail)): `Step` per task update (pending crime,
  timer on the lock, seeded roll by the lock's roll counter, XP), `Allowed` (min chance; engine: conscious, an arm, not
  carried, not on a pole), `Break` (strength > 3 x hardness); `CutLock` / `Break` also serve doors, containers and cages (Use Tools / Use Strength, `LockSystem.Force.cs`, [locks](game/locks.md#player-orders-on-locks)).
  A player character picking a lock or shackles at the lock gets the floating bar (`CharacterSnapshot.Bar`, drawn in the
  `Kenshi_ProgressBarPanel` layout just above the name tag; anchor engine choice). No bar for cutting.
- **Bashing doors** (`LockSystem.Bash`, `DoorPlan`; rules in [locks](game/locks.md)): every 30 ticks an NPC attacking whose target is inside a building
  whose doors are all locked (`IsShut`; inside: nearer the door fixture's origin than its farthest door spot) while it is outside plans row 4 with the
  original's rows (85 → 72 / 81 / 287 → 82); a plan with a bash takes the building's main door (lowest door index with a spot) (`CharacterCold.Bashing`).
  Only door states are evaluated (the rest of row 4 is held); the desire is `scoreAggressiveAction` (no war-campaign case); an animal's natural weapon
  counts. The pursuit leaves it alone; it walks to 12 out from the leaf until within the door's reach (25, x2 standing, the leaf for the marker, no
  gateway 45) and swings every 1.5 s (engine; the clip length is not read); a character ahead within 10 ends it until the next check. A closed unlocked
  door is passed by the walk, so the bandit branch (bashing unlocked doors) and the break-out (287) are not acted out.
  Broken: lock unlocked, door broken and opening, the pursuit resumes. Engine: no crime for NPCs, the `Unlocked` sound for
  `Break`; headless (no door gate, no spots) nothing is bashed. Player characters never bash.

## Arrest and jail

`Crime/ArrestSystem` (serial `SlowWorld`, after `CrimeSystem`) and `Locks/LockSystem`. Facts: [economy 11.5, 11.7, 11.8](game/economy.md#117-arrest).
Arrests, the recognition meter and the marker are hashed only while set. What a save keeps of them: [Saves](#saves).
- **Triggers**: a police-like witness that books a crime acts at once (`CrimeSystem.BookingsOf`, the witnessed-crime
  events); every 15 ticks an awake NPC within 400 that sees a player character (`Visibility`/`SneakVisibility`) whose
  bounty with its faction reaches its class threshold spots it, at once while `RecentlyWanted` > 0 (250 game seconds after
  a booking or a witnessed crime; engine: the countdown), else after 45 s of being seen (engine: one meter per target,
  rate not decoded); only the nearest free police-like looker acts. MILITARY threshold: engine 2000 (original 4000 or 2000);
  the police-like NPCs of the five towns `CrimeInstallTests` loads are MILITARY (**Observed**).
  A police-like NPC in its own town acts on an escaped prisoner (`CharacterCold.Escaped`) it sees. `AggroSystem` no longer
  attacks wanted characters.
- **Spotting talks** (`ArrestSystem.Talk`, the dialogue system, while its event lines are on): the looker fires event 44 (escapee: 45) at the target
  (`DialogueSystem.FireEvent`, a bubble now) and the data decides ([economy 11.7](game/economy.md#117-arrest)): DA 29 arrests (`ArrestFromDialogue`,
  below), "Small bounty" sends the leader to talk (DA 2) and its interrupt opens the fine window (DA 8 + 49 pay and clear; the rest DA 29), others
  say nothing more. The target is then left alone for 60 game s (`SpotRetrySeconds`, engine), longer while the guard still talks or walks up; paying ends it.
- **Fine or arrest** (engine fallback, only without dialogue events, e.g. headless tests; with them a booking witness is left to its witness
  event unless it is already fighting, then it arrests at once): outside his town (engine: farther than 2 x his resident squad's
  home radius from its centre) the guard and his free squad mates attack. In it, 1..1049 is fined (`FineFor`; 300 up to 250
  is engine, the data's line being unreachable): he walks up
  (`Engaged`, so the AI leaves him alone), and within 40 takes the fine when the player's money is above it, erasing his
  faction's entry; else, when the target runs past 800 or after 60 s, and for any bigger bounty: arrest.
- **Arrest**: attack until the target is down (combat ends the fight; nobody beats a downed target), lift
  (`CarrySystem.BeginPickUp`), `FindCage` from the guard (his squad's home building; other arrests' cages reserved), walk,
  and within `use range` + 15 (or a walk that ended within 200) `LetGo` + `Imprison` (`prison time` CONSTANTS). The fight
  stopping with the target up re-orders it within 800, else he gives up; no free cage: he drops the captive (engine).
- **Release**: player and NPC prisoners alike are freed by the release rounds below.
- **Rounds** (`LockSystem.Rounds.cs`, serial; a sweep every 30 ticks starts at most 4, at most 8 run; the worker is
  `Engaged`; each lock takes the owner's 3.3 s): **release** (211): an awake prisoner whose sentence is served, in a cage of
  a faction that books bounties, is freed by an NPC of that faction (one with goal 211 in its role list and hours first,
  else police-like, nearest within 1500) who walks there, unlocks and `Release`s in its faction's name; engine: none such,
  3 failed walks or 120 s, and the system frees it; cages of non-booking factions free nobody. **Lock-up** (155, 197): while
  a member has the goal in its hours a free member with it locks every door of the home (155 only with every member
  inside, engine: within the home's farthest node + 10; neither with a stranger inside); when no member's goal is in its
  hours, what a round locked is unlocked (engine: the morning unlock is not traced; a loaded night's locked homes come back flagged, `meitou house locked`). **Wardens**
  (195): occupied unlocked cages of the faction in its town are locked. **Keys** (engine): a worker whose target is behind
  its own faction's or home's shut doors unlocks them standing before it walks (the sim doesn't know door positions); an
  arrest's guard walking to such a cage has them opened at once. Rounds and locked homes are hashed only while any exist.
- **Lock orders** (`LockSystem`, context-menu verbs on a caged character): `PickLockOrder` (the prisoner its own cage, a mate
  walks to it; `Lockpicking.Step` each tick in reach, messages "Pick success!"/"Pick failed"), `GetOutOrder` (refused while
  locked; an unserved sentence books a pending Escaping Prison and sets `Escaped`, as does any cage of a lawless owner).
  `Imprison`/`Release` clear `Escaped`.
- **HUD**: `CharacterSnapshot.Prison`; the State line shows the status panel's prison text.
- **Bail** (DA 43, [economy 11.5](game/economy.md#115-bail-and-sentence); only the Shek bounty desk and the Inquisitor offer it): the conversation
  closes and the window reopens as the bail list (engine stand-in for the trading window): "Pay Bounty", one choice "Name (c.N)" per prisoner of
  `ArrestSystem.BailOffers` (cages within the speaker's town extent, as `InOwnTown`; player-faction or not hostile to the speaker; `BailFor` not
  null: the cage's owner neither the player's faction nor lawless), `[Leave]`. A click pays (`PayBail(prisoner, payer)`): erases the jail's entry,
  `Release` (no escape). NPC prisoners are only freed, not recruited. `PayBailOrder` (no UI) is the same bail paid by a player character within 500 of the cage.
- **Put in Cage / claim** (`PutInCageOrder`, verb on a body a selected character carries; engine): the carrier walks to the
  best free cage within 1500 (`FindCage`; others' cages reserved), `LetGo` + `Imprison` as an arrest does, then `Claim`
  (11.4 caging tests; the slaver exception never applies): the cage owner's unclaimed entry, halved for a dead captive, is
  paid and marked claimed ("Received c.N reward."). A new order or 3 failed walks end it.
- **Spawn bounties** (`SpawnBounties`, 11.2): rolled for new NPCs (not the player's, not sandbox spawns) under `Spawn` keyed
  by the character id, one fuzz roll for all its factions; no `bounty factions` -> "the empire" (fcs.def), engine: the
  faction named United Cities. Inspecting an NPC shows its bounty in the HUD's bounty panel.
- **Bounty hunters** (engine identification: NPCs of an ADVENTURER faction with a `legal system`, in the base data exactly
  Bounty Hunters): in the spotting sweep they see the player's entry for their faction's booking faction (`legal system`,
  `CrimeFactions.BookingOf`); above 4000 and once recognised (the same meter) the nearest free one and his squad attack
  (`Engaged`), re-ordered while the target is up and within 3000. When it is down, carried, caged or out of reach the hunt
  ends: no cage (11.8), and that hunter leaves that target alone for 600 game seconds (engine). Hunts and cagings are
  hashed only while any exist; restarted after a load ([Saves](#saves)).
- **Bounty desk** (dialogue, [economy 11.4](game/economy.md#114-claiming)): `DA_PAY_BOUNTY` (`ArrestSystem.PayBounty`) pays the carried body's
  unclaimed entry for the speaker's booking faction (halved dead) and marks it claimed; `DA_ARREST_TARGETS_CARRIED_PERSON` (`TakeOver`) runs
  after the line's other effects (data lists them in either order): the carrier lets go and the speaker starts an arrest in the lift
  phase, so it carries the body to a cage as above (none: dropped).
- **Shackles** (`Locks/LockSystem.Shackles.cs`, [locks](game/locks.md)): ARMOUR `is locked` items get a `LockState` (`ItemInstance.Lock`);
  `LockedOn` (effectively locked, worn) refuses moving, unequipping and selling. `ShackleOrder` (verbs Unlock Shackles / Use Tools /
  Use Strength) sends the first eligible worker (the wearer or a mate within 25): pick (`Lockpicking.Step` rules), cut (needs a tool,
  `item function` 9; the best is consumed when hardness reaches 0) or break (strength > 3 x hardness). Another faction's wearer: a pending
  code 7. The lock is saved on the item (`lock lvl`, `locked`, `broken`, `hardness`). Shackled characters show the portrait Slave overlay.
- **Spawn prisoners** (`PopulationSystem.MakePrisoners`; facts: [squad factory](game/factions-squads-towns.md#squad-factory)): a housed squad's `prisoners` templates are made
  as squads of the template's faction (else the owner's) and each member is imprisoned in a random free cage of the home building. Engine choices: members beyond the free cages
  are not made; they are held without a sentence (`Kidnapped`: no release round, no bail, getting out is no escape). Observed: Cat (`Dust King Prisoner`) caged in the Dust King
  Tower's watchtower; Tengu's Vault and the Tower of Abuse have no cages in their homes, so none are made. A caged prisoner is no aggro target (engine choice).
- **Putting shackles on** (`PutShacklesOrder`, verb "Put on Shackles"; engine stand-in for dragging them onto the target's armour slot, which the held item does not do yet): a
  selected character carrying unworn, unbroken shackles walks to a live NPC that is down or caged and wears none, and within `ShackleReach` puts them on, locked again
  (re-locking is an engine choice); what the target wears in that slot goes into its inventory. No crime is booked (**Unknown**). Prisoner Shackles take the boots slot.
- **Slave state** (`CharacterCold.SlaveState` 0/1/2, `SlaveOwner`; facts: [carry-loot](game/carry-loot.md#unknowns) item 3): `DA_ENSLAVE_TARGETS_CARRIED_PERSON` (40) is the bounty
  desk's hand-over (`ArrestSystem.TakeOver(enslave: true)`), and the captive becomes the guard faction's slave once caged ("{1} has been enslaved" for the player's). A slave's
  sentence is never served; getting out of the cage makes it an escaped slave (2, recently wanted 250); a release or bail frees it (0); an owned slave caged in another owner's cage
  changes owner. Dialogue conditions 59, 61, 62 and 64 read it. Saved as `slavestate` and `meitou slave owner` (`SlaveSave`). An arrest with `Enslave` makes the slave at the
  caging; the slavers' own processing (182) of caged captives is [Captives](#captives). CHARACTER `slave` ("sets initial slave state"; **Verified**: fcs.def and the editor's `SlaveStateEnum` NOT_SLAVE 0, IS_SLAVE 1, ESCAPING_SLAVE 2, EX_SLAVE 3) is
  applied to a new game's characters (`StartPlayer`; The Slaves' "start slave" is 1, in locked Prisoner Shackles), owned by the start town's faction (engine; the original's owner is
  **Unknown**); 3 is free.
- **Slave squads** (`PopulationSystem.MakeSlaves`): a squad's planned `slaves` squads (the factory's role 4) are made at its spot as squads of their template's faction (else the
  owner's), every member a slave of the owner squad's faction, put into random free cages of the home after its prisoners (engine: their "Slave only squad" package's one goal is
  Slave get in Cage, 172; the rest stand by the owner). A slave is never police-like (engine: many slave records keep the default MILITARY class); an owned slave starts no fights and is no aggro target but for its owner's
  enemies (engine: Heng's Outlaw slaves, uncaged without building data, drew its Samurai into fights). **Observed** (real-data
  probe 2026-10-21): Rebirth's 10 Rebirth Cage Guards squads make 34 caged Rebirth Slaves.
- **Slave catching** (`ArrestSystem.CatchSlave`, the spotting sweep; the packages' Capture Slaves, 167, in `addon- slave catching`, which Rebirth's gate guards and garrison inherit;
  **engine** rules, the original's escape test and finder are **Unknown**): an awake NPC of the owner faction in its own town that sees a player slave (daylight sight, full range)
  tags it escaping (2) when it wears no locked shackles or stands outside the town; the nearest police-like or slaver one then captures an escaping slave as an arrest with
  `Enslave` (fight down, lift, cage; event 36 ESCAPING_SLAVE_SPOTTED as a bubble), and the caging makes it a slave (1) again. A town here is `InOwnTown`'s extent, else no farther
  from its centre than its gate nearest the point plus 100 (Rebirth's gates stand about 1500 out, its radius is 184); without gates, the extent only. **Observed** (probe): a start
  slave walking out of Rebirth is tagged just past the south gate, and once down is carried back and caged as a slave.
- **Slave trading** (DA 41 / 42, `DialogueSystem.CharacterTrade`; facts: [economy 12](game/economy.md#12-slaves-prisoners-animals)): the line ends the conversation and the window
  opens as it closes (a speech-bubble line opens it at once; only for a player-character target). Rows, prices and the confirm's effects as in the original, with these **engine**
  choices: the buying list is every character in slave state 1 whose `SlaveOwner` is the slaver's faction, not dead (the original's per-owner set is not modelled); the selling
  buyer's money is the slaver's squad purse (`Squad.Money`, a trader's shop money); the confirm re-checks the window's gate (a row ticked, total within the buyer's money) and
  does nothing otherwise; a slave bought free out of a squad of others leads a new "Wandering Squad" (`SquadManagement.DismissTemplate`, as a dismissed character) of its faction, or a new player squad for
  one of the player's; event 51 fires after it is out of its cage; the window closes when the slaver or the customer is no longer awake. It is neither saved nor hashed.
- Not built: task 133 as such (Meitou walks a bought caged
  slave out of its open cage to within 15 units of the buyer; the original task's destination is **Unknown**); DA 48 (animals). A sold character obeys the slaver's squad (187) and is chained: `Character::setChainedMode` (`FUN_14032e590`,
**Verified** by decompile) puts the ARMOUR `SHACKLES` (boots) on one wearing no locked shackles, `SHACKLES-BODY` when its boots section is disabled (Meitou: the race's `no shoes`),
what it wore there going into its inventory (engine choice; `DialogueSystem.Chain`). Its other callers (a dialogue action, `FUN_1405dbd80`, `FUN_1405dc310`, `FUN_140583a10`) are not traced.


## Kidnapping

`Kidnap/KidnapSystem` (serial `SlowWorld`, right after Carry; needs the population). Facts: [carry-loot 3.2](game/carry-loot.md#32-goals-and-their-target-finders)
(the finder), [campaigns](game/factions-squads-towns.md#campaigns) (which packages). The goal-254 rule, per member, every 15 ticks, while any downed character exists:
- **Duty** (`PopulationSystem.KidnapDuty`): the member's role list in its campaign's package for the current phase (a loaded campaign squad is driven by the campaign, so its phase,
  not the AI, names the package: the retreat of the cannibal-type raids, the cannibal assault), or in its AI squad's running package (cannibals on patrol and at home), in the hour's window.
- **Victim** (`IsVictim`): the nearest within sight (`RangeOf`, the rescue range; the sensed list's size is **Unknown**) that is alive, down, of a carriable race, not carried, caged or in a bed,
  not an animal, not already someone's pick, and not of the kidnapper's faction or an ally (engine reading of the **Unknown** class bits). Player characters included; sleepers in beds are not taken.
- **Lift**: the free member (awake, not fighting, fleeing or held, able to carry, not a player character) lifts it with `CarrySystem.BeginPickUp`, then goes back to following its leader or wandering; a marching carrier (a campaign's or a cannibal retreat's) that was idle (a fight just ended) wanders, so the retreat's milling round its own town walks it home (engine choice).
  An AI squad's member starts only when a free cage it may use is in range (the original's "all cages full" stops the goal); a campaign member always does.
- **Carry**: a campaign carrier marches home with its squad (a retreating squad mills round its own town again, not the raid target, engine choice). Within 1.5 x the radius of a town of its faction it registers the town's fixtures (`RegisterSite`, no spawn needed) and
  takes the best free cage of its faction or an ally in range (`FindCage`, the original's weights, own x100; other carriers' cages reserved; engine reading: the original takes another faction's cage when "usable", [economy 11.7](game/economy.md), and an ally's is, so Stoat's slavers use its slave shop's United Cities cages), walks there and within `use range` + 15 lets go and imprisons
  the victim (`Imprison`, `CharacterCold.Kidnapped`). Home with no free cage, three failed walks, or an AI carrier waiting 60 s: it drops the victim, who is then left alone for 2 game hours
  (engine choices; the original's give-up rule is **Unknown**). A carrier that goes down drops its load (Carry). Zones follow a carried player character, so its platoon stays loaded all the way.
- **Prisoner**: `Kidnapped` = held without a sentence: it is never served (no release round), no bail is offered, and getting out is no escape crime. The cage is locked; rescue is the usual
  lock work, or a lift out of an unlocked cage ([Cages](#cages-beds-and-locks)). Messages for player victims ("is carrying X away", "has been kidnapped by F").
- **Save**: the cage state is the existing `in something` / `in what` (Meitou keys for interior cages), and `meitou kidnapped` ([save](formats/save.md)). Carry links are not saved: a save mid-journey
  leaves the victim down where it was and the raiders (retreating campaign squads) lift it again. The kidnap records themselves are not saved. Hashed only while one runs.
- **Marching home**: a campaign carrier cages in the first own town on the way with a free cage and drops the victim only in its campaign's home town without one (`Abduction.HomeSite`; engine reading of the original's cage finder, which weighs the home x100 but does not require it, [ai-tasks](game/ai-tasks.md)); a member of an AI squad that lists a cannibal retreat package (signals 1, 25) lifts
  without a cage in range and cages the same way, its home its squad's home town, which the retreat package walks the squad to (130 TRAVEL_TO_TARGET_TOWN_FAST as going home at a run, [Captives](#captives)).
- What happens to the captive next: [Captives](#captives).
- **Not built**: lifting sleepers from beds, kidnapping as a crime for witnesses, a carrier's platoon unloading with an NPC victim (the carry is dropped when the platoon is stored).
  The original's unload test (`ActivePlatoon::unloadCheck` `FUN_1404febd0`, **Verified** by decompile) holds a platoon loaded while a member carries (Character `+0x348`) a player-faction
  character (`+0x380` hand, `HandleManager::isPlayerFaction`), its 4.5 s countdown reset to 4, which Meitou's zones following a carried player character match; with an NPC load it
  unloads as usual. What `Platoon::deactivate` then does with the carry and the victim's own platoon is **Unknown** (a save writes the link as `carrying`, `FUN_140627b30`).
- **Observed** (headless, real data, `raidFrequencyMult` 1000): the Skin Bandits' "skin bandit standard raid small" against an outpost with four weak player characters: all four are lifted once down (the
  cannibal rule sends phase 2 at the third carrier), three are caged at the faction's Skinhouse HQ (it has three cages), the fourth, on the leader, is carried on towards the campaign's far home.
  A save and load keeps the three caged and `Kidnapped`; a second capture writes the same keys. Cannibal AI squads at home lift and cage a downed player character in about two seconds.

### Blood smell

`SmellSystem` (serial `SlowWorld`, every 30 ticks; facts: [perception](game/perception.md#blood-smell)): each zone's blood target is its corpses with flies plus 0.25 per bleeder, its
position their centroid (the bleeders' when corpses are none or at most 0.25 of them); the value rises 1/60 and falls 1/240 per game second. Corpses get flies 4 game hours after
death while the weather there acts on no one (`BodySystem.Flies`, `CharacterCold.HasFlies`; none in a world without weather). The host draws the effect and plays the sound at them (`GameHost.CorpseFlies`). An animal squad whose package lists 176 hunts
the zone with the best value (own x1, neighbour x0.8, other x0.5) above its ANIMAL_CHARACTER `smell blood`, keeps it while the value holds, and its members run to the
position. Engine: every loaded zone is a candidate; a hunter stops within 50.

### Town duties

`Kidnap/TownDutySystem` (serial `SlowWorld`, after the kidnapping; with the population and the AI): goals of an AI squad's running package that no act-out covers, per member,
every 15 ticks, by `PopulationSystem.GoalDuty` (facts: [ai-tasks](game/ai-tasks.md) "Finders of the jobs", [carry-loot 3.2](game/carry-loot.md#32-goals-and-their-target-finders)).
- **273 Remove Intruder**: a free member standing in its home town, the town its occupation holds or a town of its faction lifts the nearest knocked-out character in that town's
  walls (`amInsideTownWalls` > 0: its [gate code](game/pathfinding.md#gate-codes) is a walled region, so an unwalled town removes none; where the codes are unknown, the built-up area: a
  placed town's radius, a player town's farthest building from its centre), not carried or caged, not of its faction or an ally.
  A body that dies on the way is caged only with **281** listed (else let go).
  Carrying it: with **112** listed, the best free cage in that town (the occupied town's cages are usable), where it is locked up as `Kidnapped`; else with **274** listed, 200 past the
  built-up area on its side (farther out until walkable), put down; else put down. A body put down short of its goal is left alone for 2 game hours.
- **243 Getting food**: a member holding no food walks to the nearest store with food in the town (a player town's complete storage, else an unlocked container) and takes one item.
  Meitou's NPCs are fed by their hunger floor, so the food is not eaten.
- **158 / 283 Shooing** (`AI::findStrangersInMyBuilding`; 283 only while the home is private): the first character in the squad's home building that is neither allied nor an
  enemy, not down, caged, its faction's slave or law enforcement, or (an NPC squad) a player character there not sneaking or spotted; the member walks at it and sends event 9
  (17 to an enemy) every 5 s (engine pacing; the dialogue data escalates); after 25 game minutes, once the stranger is getting away, event 60 and done; over once it is out.
- **216 Ransacking town** (`AI::findRandomBuildingInTown`): a member draws a random standing building with an interior in its mission town (blackboard, else the campaign's
  destination, else where it stands), draws its weapon and runs into it; inside, the errand ends and the next look draws another.
- **131 Looting food** (`AI::findSomethingToLootFoodFrom`, shared with 243): a member holding no food in any town (not only its own) walks to the nearest such store, not a
  container of its own faction (engine reading of the own-shop skip), and takes every food item that fits (`Task_EmptyMachine` moves items while there is room).
- **212 Confiscating goods** (`AI::findPrisonerWithContraband`): in its town, the first occupied cage (list order) whose prisoner's inventory (not its bag) holds an item stolen for it; at the cage it waits 0.8 s and
  confiscates as a police looter does ([NPC looting](#carrying-rescue-and-looting)); engine choice: whatever the runner's class (base data: only law-enforcement packages list 212).
- Occupiers' **36 / 37** town goals walk to a random complete building of the player town (`AiSystem.OccupiedSpot`; the original's target is **Unknown**).
- Engine choices: sight `AggroSystem.OutdoorSight`, arrival 40 (a cage: its `use range` + 15), 4 short walks before giving up; nothing is hashed until one runs.
- **Observed** (headless, an owner save, Starving Bandits occupying an outpost whose buildings reach 253 from its centre): the knocked-out player character lying 566 out is left alone; moved
  into the base, one bandit lifts it and puts it down 413 out, nobody lifts it again in 6000 ticks; with two food items in a storage, two bandits take one each.

## Captives

`Kidnap/CaptiveSystem` (serial `SlowWorld`, right after Kidnap; with the population and the AI). Facts: [ai-tasks, prisoner fate and slave rows](game/ai-tasks.md#prisoner-fate-and-slave-rows),
[ai, prisoner and slaver packages](game/ai.md#prisoner-and-slaver-packages). Each goal runs as a job of one worker: every 30 ticks a sweep gives a job to a member of an AI squad
whose current role list holds the goal in its hours and that is free (the AI's, awake, not fighting, in no job; at most 8 new and 32 running jobs, engine); the worker is `Engaged`
while it works. Cages are those of the squad's town (`Fixture.Site`) unless said otherwise. A walk to a cage ends within `use range` + 15, or a walk that ended within 200; three
failed walks mark the cage unreachable for the faction; its own faction's shut doors are unlocked first. A job gives up after 600 game s (engine).
- **Kill** (135; needs a weapon): when the town's kill timer has run out or all the faction's cages there are held; the best occupied cage of the worker's faction by the cage weights
  (x100 home building), not a robot. At the cage it holds `chop down static` (`CharacterCold.ActionClip`, which the animation system plays for an idle character) and strikes once per
  clip length at 0.55 of it (`CombatSystem.Execute`: the weapon packet landed through armour, no defence, no crime) until the captive dies. Events 10 (on picking) and 11 (each blow).
- **Eat** (139 always; 282 "Dinner time" at the kill rate): same target, weapon sheathed, `eat` held, `MedicalState.BeingEaten` each tick at lerp(2, 0.4, toughness/100) a second
  (limbs severed unless dismemberment is Never; `DeathCause.Eaten` and "{1} has been eaten alive." when nothing is left). Events 52 and 11 every 10 s (engine throttle).
- A captive's death by either resets the town's timer to now + uniform[1, 15) h; the first ask sets it. Timers and jobs are not saved (engine); hashed once any exists.
  **Observed** (headless probe, real data): a cannibal village kills a caged player character within 16 s of work once its timer is due; a fishman lab eats one in about 160 s.
- **Process** (260: an awake non-slave occupant anywhere in the town; 183: an occupant within 180 that is no slave of the worker's side): cages of the worker's faction or an ally,
  not the player's. 3 s of `medic`, then the captive's worn weapons and armour (not locked shackles) go to the worker's inventory (else the ground; engine for the original's
  storage), its faction's `slave clothing` is put on where free, event 35 (37 for a former slave), and it becomes the worker's faction's slave. Kidnapped player characters are
  processed too. **Observed** (probe): a player character caged next to the Slave Farm's Cage Wardens is enslaved about 7 s later.
- **Gather** (173, 245: while the squad has at most 3 of its own slaves up and uncaged; 217: in a town or outpost): the nearest occupied cage of the town the gatherer stands in (173,
  245: its faction's; 217: any but the player's; 245: a woman or another race). The cage is opened and the captive joins the gatherer's squad as a slave member (state 1, owner its
  faction; its AI mind is made anew, so its Slave AI Goals, mostly 171 Follow slavemaster, run). Engine reading of `Task_GetSlave`'s queued message (**Unknown**); NPCs only.
  **Observed** (probe): the Slave Farm's workmasters take the caged slaves out by day; at night their slaves' 172 puts them back.
- **Deliver** (192, the squad leader under a shipping package): walks to `Blackboard.MissionTown`; there, with 194 (or 218 in a prison or slave camp) in its hours, it says event 39
  to the nearest NPC of the town's faction or an ally, and each of its slaves leaves for a "Wandering Squad" of its own, owned by the town's faction, caged in the nearest free cage of
  that faction or an ally (engine: the original's handover is a dialogue for event 39, **Unknown**).
- **Get in the cage** (172, a Slave AI Goal): a slave not held walks to the nearest free cage of its owner's faction in its town and gets in.
- **Packages** (`PackageClass` 8 to 11, rules in `AiSystem.Captives.cs` as the facts): cannibal retreat (1, 25), cannibals at home (27), prisoner gathering (10, 24), slave shipping (11,
  28; Meitou always takes the nearest slave camp not hostile to the squad, the other town rule's flag being **Unknown**); the cannibal patrol (4) does not start while the town's cages
  are full. A squad's slaves are its live slave members in slave state 1 (engine reading of the platoon's slave list).
- **Capture** (167, `CaptiveSystem.Capture.cs`; facts: [slave work, shout and capture](game/ai-tasks.md#slave-work-shout-and-capture)): the nearest downed, uncarried, uncaged character in sight that is
  not of the capturer's side (engine reading of the sensed-class masks), no slave (state 1), not law enforcement, trader or diplomat and not shackled. The capturer walks to it, strips the `hip` and
  `back` items into its inventory (0.8 s of `medic`), cuffs it (10 s, engine choice; locked stock `SHACKLES` put on, what it wore in that slot goes to its inventory; event 37) and has `KidnapSystem.Carry` lift
  it to a cage of its faction or an ally in range (goal 112 in the original; a capture starts only with one free, so no one is cuffed and left), where it is held as kidnapped and then enslaved by the processing jobs. **Carry** (168): the same for a downed slave of the
  carrier's faction, without strip or cuff. A caged target that got up is beaten down first (196, an armed capturer: a `chop down static` blow per clip, `CombatSystem.Execute`), any other is given up. A target a job gave up on is left alone for an hour (engine give-up list). **Observed** (probe, Rebirth, real data): a downed character of the player's faction
  beside the garrison leader is stripped, shackled, carried to a cage and enslaved by the Cage Wardens in about 1250 ticks; its squad then remembers the capturer as captor. **Observed** (probe, Stoat, real data): the Slave Traders cuff a downed player character, cage it in a United Cities cage of the slave shop and enslave it in about 500 ticks.
- **Liberate** (276): the nearest cage, in the town the worker stands in (else its squad's), of a faction neither its own nor allied, holding a live occupant that is not its
  enemy. 3 s of `medic` at the cage (engine), then `FixtureRegistry.Release` (no crime is booked for an NPC liberator).
- **Eating on the ground** (230 / 233, `CaptiveJobKind.EatBody`; also from the race's `AI Goals`, which the sweep reads after the role list): the nearest body within sight that
  the finder takes; the eater walks to it and eats it as a caged meal is eaten (`eat`, event 52, `MedicalState.BeingEaten` for a living one, which can kill it). A corpse is eaten
  until `GiveUpSeconds` (engine: the original goes on while the finder finds it).
- **Fogman worship** (237 / 236, `CaptiveJobKind.Worship`): in the town nearest it, the first cage whose occupant is being eaten (a running Eat job); within 60 it faces the cage,
  plays `bowing` and barks event 53 every 3-14 game s; ends once the cage is empty or the job grows old (the original runs on while the cage stands).
- **Dogs' limb play** (race goals 277-279): 277 sends an animal with an empty mouth (an adult only when its stomach is empty and hunger at most 2.75) to the nearest limb within
  200, which the pick-up takes into its mouth; 278 (a mouthful, not a player squad's adult over 0.95) sprints in random 1000-1400 legs while it holds it; 279 (any NPC animal, a player
  squad's only under 0.95) runs after the first allied animal in sight with something in its mouth. Engine: the play's first leg is random too.
- **Crop raiding** (232, `CaptiveJobKind.EatCrops`): in a player town whose faction is its enemy (engine reading of the coexist test), a random farm with food; at it, `eat`, crime
  14 each tick, one plant per game second (`ProductionSystem.EatCrop`). NPC towns' farms are not machines in Meitou, so only the player's are eaten.
- **Escapes** (`LockSystem.OpportunisticEscapes`, `ShackleEscapes`): an AI prisoner whose role list holds 256 or 202 in its hours walks out of a cage that is not locked
  (`FixtureRegistry.GetOut`); every 30 ticks an AI character outside a cage wearing locked shackles with 202 listed works them off, cutting with a tool, else picking (the
  original planner's choice among pick, cut and break is **Unknown**).
- **Shout** (188): a worker with slaves of its squad up and uncaged says event 38 at a random one and walks towards it for up to 8 s (or within 40); the same worker again after 0.5 to 1.5 x 120 game
  seconds (engine pacing).
- **Work** (189, 190; `AiBehaviour.Work` in the AI, not a job: it competes by score like any goal): a worker of a town with a mine, research bench, refinery or crafting bench (BuildingFunction 1, 3, 4,
  10) walks to a random one (a pick of the character and `AiMind.WorkEpoch`, kept while it works there) to within 30 units, faces it and plays its functionality's clip (`stonemine` by default) as
  `ActionClip`. NPC towns run no machines here, so nothing is produced (their buildings are level data, not `Machine`s, and Meitou has no NPC-town production); the worker does take the building's `hunger rate` (`AiMind.WorkHungerRate`). **Observed** (probe, Rebirth, real data): 35 workers, slaves among them, stand at the mines of the Holy Nation's towns by day. The
  Obedient Slave job (187) is the Slave role list of the owner's squad, which slave members already run; a slave in another squad runs the list of `CharacterCold.SlaveMaster`.
- **Eaten**: a meal still awake gains toughness XP (`XpService.Continuous`, amount 10). **Process** (183) re-processes a slave of its side that does not look like one (no locked shackles and none of
  its owner's slave clothing; an owner without slave clothing never does).
- **Status lines**: a slave or escaped slave selected shows the bounty panel with its status rows ("Slave" / "Escaped slave" and the owner's text, the player-character hints) in its tooltip, ahead of the
  bounty rows (`CharacterSnapshot.Slave`, `PlayerInterface.SlaveRows`; [ui-screens, information panel](game/ui-screens.md#4-hud-mainbargui)). Ex-slave (state 3), "Looks like a slave", "Situation" and "Time remaining" rows are not built.
- **Squad memory**: a caged captive's squad remembers the captor (5, through `FactionDeeds.CagedHook`), a freed prisoner's squad the liberator (3 and TEMP ALLY, event 13), a sold slave's squad the customer
  (5), a bought one's the buyer (3).
- **Shipping off screen**: the shipping squads are town roaming squads and homeless spawns (base data), so the original despawns them as they leave (Faithful). A kept stand-in
  (`KeepRoamers`) whose package is a shipping one (TRAVEL_TARGET, [ai](game/ai.md#unknowns)) with a living slave goes on at 25 to the camp its package chose (none chosen: the
  nearest slave camp not hostile to it) and stays there, the original's TRAVEL_TARGET rule ([off-screen world](game/factions-squads-towns.md#off-screen-world)); the handover
  waits for it to load (`PopulationSystem.ShippingCamp`); a save's travelling platoon not made yet ships too, its slaves read from the save (slave role, `slavestate` 1), to the nearest
  such camp (the save's mission town is not read). Not
  shipped off screen: wild ones (a save's homeless platoons and the squads `KeepWild` keeps), which their sector makes again where they stood, so they stay there as other wild squads.
- Not built: goal 112 as such (captives are carried by the kidnapping system), short-term tag 6 of the strip, and the real operation of NPC town machines by
  workers: it would need a `Machine` (recipe, input and output inventories, progress) for every level building with a functionality, made when its town registers, and their
  states written into the zone files' GAMESTATE_BUILDING as the original's are; production would also feed the shops' stock, whose origin from machines is **Unknown**. Left out
  for now: workers act out the work (clip, hunger rate) with no output.

## Buildings

Player construction contract (Faithful UI). Facts: [construction](game/buildings-production.md#construction),
[placement](game/buildings-production.md#placement), [build menu](game/buildings-production.md#build-menu),
[build tasks](game/ai-tasks.md#build-tasks), [save keys](formats/zones.md#placement-keys). Outposts have no
residents in the original ([the outpost](game/buildings-production.md#placement)) and are on the [map](game/ui-screens.md#map); raids on them: [campaigns](#off-screen-tier). Not built: the door x3 rule ("Wall termites" is disabled in the original).
Damage: melee blows on walls and doors (`CombatSystem.ApplyZoneBuildings`, the original's only damage,
[repair](game/buildings-production.md#work-distance)). Production, power and research: [Production](#production).

**Shared types** (stubs f4cb78c, filled by the simulation track):
- `Meitou.Data/Gameplay/Construction/BuildingBuildData.cs`: `BuildingBuildData.From/LoadAll` (build fields, material
  entries), `MaterialCost`, `Target` (sum of required), `Rate` (`build speed mult` / (1200/11)), `EstimatedHours(buildSpeed)`
  (the build window's estimate, **Verified** algebra: materials / (`build speed mult` x CONSTANTS `build speed` x option)).
- `.../ResearchGate.cs`: `StartResearch(db, start)` (the start-off's `research`, or the default start research when it lists none),
  `Unlocked(db, completed)` (union of `enable buildings`, BUILDING targets only), `DefaultStartId`.
- `.../PlacementRules.cs`: `PlacementFailure` (one value per original message), `PlacementTown`,
  `TownBlock(BuildingBuildData, Vector2 at, IReadOnlyList<PlacementTown>, string playerFactionId)`, `Message(PlacementFailure)`
  (the original's texts; `InvalidFloor` is "Invalid placing floor number." because the rest of that text is not in the
  docs, `NotAvailable` is the engine's "That building is not available yet.").
- `Meitou.Simulation/Construction/ConstructionCommands.cs`: `PlannedBuilding(Record, Position, Rotation, Floor, Parent)`;
  `PlaceBuildings(Plans)` (Confirm), `BuildOrder(Characters, Site, Repair)` (orders 2/95), `DismantleOrder(Characters, Site)`
  (96), `ConfirmDismantle(Site)` (the selection panel's Dismantle: the player's site at once, its furniture destroyed, refused while a non-furniture building stands in it;
  [selected building](game/ui-screens.md#selected-building)), `CancelSite(Site)` (engine: only a site with nothing delivered), `PauseSite(Site, Paused)`, `UpgradeSite(Site)` / `DowngradeSite(Site)`
  ([upgrades](game/buildings-production.md#upgrades)), `SetBuildingPublic(Building, Public)` (a site's `Public`, else an owned town building's
  flag in `World.Fixtures`). Sites are named by instance id.
- `.../ConstructionSnapshot.cs` + `WorldSnapshot`: `Buildings` (`BuildingSnapshot`: `Id`, `Record`, `Position` absolute,
  `Rotation`, `Floor`, `Parent`, `Owner` faction index, `Progress`/`Target` in material units, `Materials` (item,
  required, delivered), `Complete`, `Paused`, `Dismantling`, `Public`, `TownName`), `BuildingsRevision`, `UnlockedBuildings`.

**Simulation state.** `World.Buildings` (`Construction.BuildingRegistry`, sites as `BuildSite`): player-built sites in
creation order, by id, holding the snapshot's fields plus rate, builder counter and the destroyed flags. Owned town houses needing repair also register, with `LevelPlacement` and a zone-qualified id; completed restored houses stay registered for their scene overlay. Ids `<n>-zone<X>.<Y>-INGAME`, n from a per-zone counter (hashed) that starts above every such id
in the loaded save (generated interiors share the pattern; `Reserve(id)`). The 106 `-zoneX.Y-INGAME` ids of the level data
are not reserved by the simulation (**engine gap**, the host may call `World.Buildings.Reserve`); a collision with a base
placement is the host's to avoid. `World.Research` (`Construction.ResearchState`): completed RESEARCH ids and the
`Unlocked` set (replaced, never changed in place). A new game completes the start-off's `research`, or the default start research
(`ResearchGate.DefaultStartId`, [build menu](game/buildings-production.md#build-menu)) when it lists none
(`PopulationSystem.StartPlayer`); a load takes the save's. Research benches change it ([Production](#production), which says when it is hashed).

**Town-house repairs.** `FixtureRegistry` registers an owned damaged/incomplete house on purchase or town activation.
Buying a ruin leaves it destroyed; an explicit Repair order from an able selected player character starts rebuilding,
applying the [ruin transition](game/buildings-production.md#construction-tick) once and restoring doors and indoor sight.
Build, DeliverMaterials and FetchMaterials use the same work and resource rules as new construction. Shift also adds
Engineer (125), which resumes available work after supplies arrive; a direct order stops when no source exists.
Jobs retain their queue priority and switch. Transient orders are not saved; persistent jobs and the house's progress,
delivered materials and ownership are ([save](formats/save.md#zone-files)). Normal damage to a complete house needs no materials.
Town houses cannot be dismantled, cancelled or upgraded through this seam. The original town and instance remain unchanged.
Building inside one names its zone-qualified runtime key as parent; save handles accept that key or a legacy raw instance id. Placement is refused while it is being repaired.
**Verified** implementation coverage: `HouseRepairTests` and `HouseRepairUiTests` (both UI modes, original Storm House data).

**Outposts.** `World.Buildings.Towns` (`PlayerTown`: `Number` 1.., `Name`, `Position`, `Owner`, `Public`; never removed; renamed and ticked public by the [town panel](game/ui-screens.md#town-panel)) and
`BuildSite.Town` (a number, 0 none). `PlaceBuildings` gives each new site its town by the
[outpost rule](game/buildings-production.md#placement): the parent's; else the nearest player town within 5000 (X/Z);
else the town of the nearest player site within 2500; else a `creates player town` non-wall makes "Your Outpost" at its
position, and every player site whose nearest town (level towns and outposts) within 8000 is it moves in. After the
batch, `links together` sites still without a town take the batch's last player town, else the nearest town within 16000
of their mean, else a new outpost there; a level town of another faction leaves them without one (engine:
`BuildSite.Town` names player towns only). After each placement, removal and load the town's `Position` and `Override`
radius are [reassessed](game/buildings-production.md#placement) (`PlayerTown.Radius`, what campaigns, the AI's current town and
dialogue use). Hashed (towns, and `Town` when set) only once there is one.

**ConstructionSystem** (`StandardParts.Construction`, in `All`; `StandardSystemSet.Construction`) runs after Carry, before
Combat; all of it is serial. `ConstructionSettings`: `BuildingSpeed` (the option, 1), `Footprint` (record -> radius,
optional), `EdgeGap` (site, point -> gap to its footprint hull; the host's).
- `Inputs`: `PlaceBuildings` re-checks only the data half (a BUILDING, unlocked, `TownBlock`); a failing plan is dropped
  with `World.Post(Message)`. A total of 0 completes at once, so a free building is complete from the first snapshot.
  `BuildOrder` (player characters, awake, not carried or caged; Build and Repair alike work a site that `NeedsWork`:
  unfinished, or complete with progress below its target, the original's damage) and `DismantleOrder` set the
  `CharacterTask`s `Build` / `Dismantle` and `CharacterCold.BuildSite`
  (the selection when the list is empty). Dismantling is refused with the original's texts while a site stands in the
  site ("I have to dismantle the furniture first." for interior furniture and nodes, else "I have to unmount the
  buildings first."). `CancelSite` removes an incomplete site with nothing delivered, else posts "Dismantle it to get the
  materials back."; `PauseSite` sets `Paused` (only the auto-job skips it). `PlaceBuildings`, `CancelSite`, `ConfirmDismantle` and `PauseSite`
  also apply while paused (`ApplyPausedInterfaceCommands`).
- `Schedule`: a worker walks to the site like a medic to a patient (goal = site XZ, free speed, asked again after a wait
  when the path failed). It has arrived within the [work distance](game/buildings-production.md#work-distance) (`SiteReach`:
  40, walls 60, twice that once stopped, from the footprint hull). A fetcher walks to its source instead (a storage within its
  `use range`; a ground item within `PlayerSystem.PickUpReach`, twice that once stopped; a source that is gone sends it back
  to building). Work ends when the site is gone,
  no longer `NeedsWork` (building), the character is down, fighting, carried, carrying or caged, or another order replaced
  the task.
- `SlowWorld`: arrived workers in slot order. *Build*: a stalled site (`sum(min(delivered, required)) <= progress`) turns
  into *DeliverMaterials* if the builder carries a missing item, else into *FetchMaterials* when `Source` finds one, else
  the work ends;
  otherwise progress += rate x `lerp(0.3, 2, e/100)` x dt x `BuildingSpeed` x CONSTANTS `build speed` x 0.75^k, e the
  engineering stat times its injury multiplier (engine choice), dt = `TickSeconds`, k the builders that already worked
  the site this tick (the 0.75 counter resets each tick: the engine reading of the **Unknown** reset; capped at 15, above
  100 it restarts), engineering XP (`XpService.Engineering`); a rate of 0 or less completes at once; at
  `progress >= target` the site completes (`Complete`, message "Building '<name>' construction complete." for the player's
  faction); a repair (complete, never stalled, no materials) stops at the target. *DeliverMaterials*: the bar fills at
  0.2 per second (`CharacterCold.BuildBar`); full, it hands over one unit of each missing type (inventory first, then
  backpack) and the task returns to *Build* (whether the original repeats deliveries before building is **Unknown**).
  *FetchMaterials* (`CharacterCold.FetchSite` / `FetchItem` / `FetchRecord`, hashed with the site): `Source` follows the
  [original's finder](game/buildings-production.md#material-delivery) for each owed material in list order: a loose item
  of it within 1200 of the builder (engine: the nearest), else the nearest player storage holding it, else the nearest
  player machine whose output holds it, both within squared distance 4.246733e7 (the AI's default range mode; engine:
  no town filter, no remembered source, exact record). At a building it takes up to 2 units while it has room, a loose
  item whole (the rest stays); with something taken it walks back as *Build*, else the work ends. *Dismantle*: a complete site first
  becomes incomplete with progress at most target - 1; then progress -= dt x `lerp(0.05, 4, e/100)` x 4 x rate (no option
  or constant), XP as building; at 0 the delivered materials come back and the site is removed. The `medic` clip plays
  for building and delivering, `stonemine` for dismantling (`AnimationSystem.HeldClip`).
- Refund: entry i with something delivered gives `max(1, trunc(U x delivered))` units, U = m + (1 - m) x
  `Rng.Float(Rng.Hash(seed, StableHash(id), RngPurpose.Build, i))`, m = CONSTANTS `min dismantle materials percentage` /
  100; each unit is its own ground item (`GroundItems.Drop`, yaw 0) at x, z = position + (2u - 1) r, u from streams
  `2000 + (i << 16) + 2k` and `+1`, r = the footprint radius, at least `Reach` (engine: the original's bounding box is
  not in the sim). A destroyed flag (a save's, or a wall line hit to 0) turns a player-built building into a ruin on the next tick; a town house waits for Repair
  (**Verified** rule: progress 0.4 x target, delivered floor(delivered x U[0.3, 0.5) + 0.5)).
- Engineer job (125, run by the [job lists](#production)): `EngineerSite` scores the faction's sites that `NeedsWork`,
  not paused, destroyed, attached or being dismantled, `max(2 (1 - d^2/9000^2), 0.0001) / (n + 1)` (d 3D, n others
  working it), skipping a stalled site the character carries nothing for and `Source` cannot feed; `StartBuilding` sends
  it there. Without the job nobody builds unordered (the original has no other auto-build).
- `BuildingsRevision` bumps on add, complete, a complete building becoming a site (dismantling started), destroyed and
  remove. `Version` (progress, delivery, pause) only rebuilds the published list.

**Hash and determinism.** `World.StateHash` adds the registry (per-zone counters, then each site's fields in creation
order, then the outposts) only once it holds a site or an outpost, like `Fixtures`; `CharacterCold.BuildSite` with its bar and resume task hashes only
when set. Existing golden hashes are unchanged while there are no sites; the `build` scenario pins one with sites.
Decisions read only world state and command order, never collision or navmesh readiness (paths stay the usual
asynchronous input).

**Host seam: authority and presentation.** Runtime's `WorldBuildings` diffs the live registry at serialized
input/tick boundaries and paused edits; navigation needs no published snapshot or renderer frame. Its overlay
includes non-IGNORE sites that are complete, have zero build threshold, or carry destroyed/level-placement
overrides. Runtime activation/rollback contracts: [multiplayer](multiplayer.md#shared-runtime).
The graphical host diffs snapshot `BuildingsRevision` only for objects, lamps and effects; the scaffold reads
`Progress/Target` each frame ([construction look](game/buildings-production.md#construction-look)).
Runtime navigation builds asynchronously but commits meshes, unloads, coarse obstacles and seed expansion
at world boundaries. Stale build generations/incarnations are discarded; waiting movement retains its goal
and stops at the verified prefix. Loading hosts distinguish prepared from published zones. Contracts and
verification: [multiplayer](multiplayer.md#shared-runtime), [long routes](game/pathfinding.md).
- Overlay type: `BuildingPlacement` (`StateId` null for new buildings, `WorldY` = absolute Y). Town repairs retain the
  original state and instance ids, overriding only `Destroyed`; render and nav replace by global `Key`, never append a second house; removing the override lays its zone out again from the level data.
  A repaired town house replaces only its objects and interior in the resident zone; floors are invalidated and its nav overlay applies from repair start even below build threshold.
  House lamps and layout lamps replace the old lights, with none during reconstruction.
- Rendering: `WorldObjects.AddRuntime/RemoveRuntime(BuildingPlacement)` merged into `BuildZone`. Engine choice: a loaded
  zone takes the placement incrementally (no reload); the scaffold is an approximation ([render-objects](render-objects.md#player-buildings-and-the-placement-ghost)).
- Navigation: one thread-safe overlay store shared by every `ZoneGeometryGatherer` (`NearBuildings` and `BuildingHash`
  see it, so the cache key changes), `NavSystem.AddRuntimeBuilding/RemoveRuntimeBuilding` and `RebuildZone(zone)` for
  each zone within `BuildingReach`.

**Placement validation.** Shared CPU rule: `Meitou.Runtime/Placement/PlacementCheck.Evaluate(PlacementQuery) -> PlacementFailure`,
pure over the query (build data, transform, floor, queued ghosts, snapshot characters and sites, towns) and services
(heightmap, collision cache, water level). It calls `PlacementRules.TownBlock` first and returns the first failure in
the original's order. The graphical preview may load collision asynchronously; `WorldContent` installs an
authoritative `IConstructionPlacement` that reads complete CPU geometry and live world state at execution.
Attributed placement requires this validator. Authority contracts and remaining limits: [multiplayer](multiplayer.md).
Ghost controls, tilt and the unconfirmed ghost queue (Undo) are host UI state.
Implemented host-side, in the original's order: town block, resource row (`PlacementRules.Readout` /
`ResourceBlock`: a farm's fertility factor or crop yield (x crop match), or a `world resource mining` building's
`ResourceField.Yield`, below 0.01; no readout without an areas map, or for iron/copper/carbon without known rocks; the
readout is also the ghost's popup, `PlayerInterface.BuildReadout`, a floating tooltip centred over the collision box's top), site under the ghost, static building under it, interior
rule, footprint overlap with static buildings, sites and queued ghosts (convex hulls of the collision shape, SAT),
characters within 2.5 of the hull, LAND parts below the water level 98, slope (four rotated bounding-box corners, `max slope`,
`match slope` tilts fully) and normal.y < 0.2. **Indoors:** the ghost (not walls) stands on the first building floor the
cursor ray meets before the terrain, using the movement pick up to the viewed floor (the build window's floor arrows), untilted;
it queues with that building as parent (a site's id, a level building's zone-qualified key) and its floor. The building must be
a finished player site or a level building the player's faction owns (its state's `owner faction ID`, else its town's
faction; else the original's "don't own" / "incomplete" texts); the parent is left out of the overlap tests, the slope test is skipped (floor taken as
level), and interior-only furniture is refused only off a floor. Engine stand-in for the original's node boxes against the
building's walls: the footprint must lie within the building's indoor hull. **Not decided** (never fail, so the ghost may
show valid where the original refuses): snap nodes, usage nodes, blocked use nodes, vertical margins and the
5 deg object-surface rule. Authority resolves the requested floor against CPU floor collision. A `ceiling placement` ghost hangs its origin 1 below the
lowest collision over the floor point (a slab's or roof's underside, from 5 up; its meshes hang below their origin, Ceiling Lamp
y -19..+4) and fails with the floor-number text when there is none. A site inside another (its parent) is picked through
the parent's roof and walls while the interior view shows the parent. Owned-parent interiors bypass the foreign-town exclusion. Messages: `PlacementRules.Message`.

Build mode (B, `Kenshi_BuildWindow`, Faithful UI only): categories and groups list `WorldSnapshot.UnlockedBuildings` in
database order (the original's order is **Unknown**); the window estimates hours as materials / (`build speed mult` x 6),
rounded up (rounding **Unknown**). Left click or Space queues a valid ghost, Backspace or Undo drops the last, Confirm sends
`PlaceBuildings`; leaving build mode drops the queue. The HUD panels the window covers are hidden meanwhile. Right click
on a site with characters selected gives `BuildOrder` (held 0.2 s: menu with Build, Dismantle -> `DismantleOrder`, Cancel
Construction -> `CancelSite` while nothing is delivered); the pointer is `build` over a site.

Walls (`Building/WallLine.cs`, facts in [walls](game/buildings-production.md#walls)): a `links together` normal wall, or
a snapped tower/gate, is laid as a line: first click (or Space) starts it at the ghost, which snaps to the nearest link
point of a queued wall or player wall site; each frame `WallLine.Layout` lays bend pieces at snapped ends and the
straight run to the cursor (end snapped to a start, or an end for a backwards line), and every piece runs `Evaluate`
as a `WallPiece` (own pitch for slope; `Ignore` skips connectors, wedges and walls at a snapped joint). The next click
queues every piece as its own ghost and chains a line from the far end; right click or Backspace drops the line. Each
piece becomes its own site; `ConstructionSystem` allows a subsection when a BUILDING listing it in `wall subsections`
is unlocked. `ConstructionSystem.AttachWalls` (after placing, and at the end of `BuildingSave.Read`) gives each short,
connector or wedge without parent or children a `WallParent`: the root of the first wall site, with the same complete
flag, that has a link point within sqrt 10 of one of the piece's link points or its middle (engine choice: the middle
catches crest shorts; a normal/single root wins over a subsection root; gates never parent). Orders, builders and the
auto-job act on the root; its progress sets the children's (`min(p/target, 1) x child target`) and completes them; its
removal removes them. So an attached piece costs nothing; the link is not saved or hashed. A destroyed attached piece
just follows its wall again (original **Unknown**). **Engine gaps**: level (NPC town) walls are not snap candidates;
indoor lines (radius 25), the steep-climb straight extension, the offset clamp and the arrows are not built; the stretch
is recomputed each frame from the signed overshoot. The snapshot carries `WallParent`; picking an attached piece in the
UI picks its wall (name, verbs and Cancel's nothing-delivered check are the wall's).

**Saves.** `Saving/BuildingSave.cs` writes each site as a GAMESTATE_BUILDING (`<id>-S23`) plus its entry in the zone
file's `0-buildinglist` (keys per [zones](formats/zones.md#placement-keys)); a zone without a file gets one, the slot
list and the handle (`C` = zone container, `CS` 11111, next free `I`) follow, and player-built entries the world no
longer has (a dismantled building) are removed. The instance Y is the absolute one, as in all game-made
`-zoneX.Y-INGAME` buildings of the samples (**Observed**); a load prefers `world Y pos`. `is complete` is written
explicitly. The parent is `isInsideBuilding` plus Meitou's `meitou parent` (instance id, wins on load). On load it
reserves every `-zoneX.Y-INGAME` id of the save and reads the player faction's such buildings back, in zone and list
order (the player's start has none, base placements use other container names). `town` is the outpost's handle,
`residents` the null handle. Outposts go to `quick.save` as [town-list entries](formats/save.md#player-outposts)
(made when the save has no list); an outpost read back keeps its entry id, state and handle, a new one gets the next
town container `C` (engine choice, the original's handle allocation is **Unknown**) and a seeded `CS`; a load reads the
player faction's non-nest entries in list order and matches buildings by the handle's `C`/`CS`. The state's name
is "Town state <name>" and gives the name back; a new state is written `discovered` and `explored` (Meitou choice). `Saving/ResearchSave.cs` reads and writes the RESEARCH record's `finished<k>`
and `num finished`. Not compared with a real file that has a player-built building (the sample saves have none).
The first snapshot after a load carries every site, so the host seam needs nothing extra.

**Files.** Simulation track: `Meitou.Data/Gameplay/Construction/*`, `Meitou.Simulation/Construction/*`,
`StandardSystems.cs`, `World.cs`, `World.Interface.cs`, `WorldPublish.cs`, `Movement.cs`, `CharacterTable.cs` (cold fields),
`Rng.cs`, `PopulationSystem.Player.cs`, `AnimationSystem.cs` (held clips), `Saving/*`, `Meitou.Data/Save/SaveBuilder.cs`
(a comment). Host track:
`Meitou.Game/GameHost.Building.cs` (diff, overlays, rebuilds, repath), `PlayerInterface.Build.cs` (Kenshi_BuildWindow,
ghost, commands), `Meitou.Runtime/Placement/PlacementCheck.cs`, `Building/BuildMenu.cs` (categories and groups from
`BuildingBuildData` and `UnlockedBuildings`), `KeyMap.cs` (build map), `PlayerInterface.ContextMenu.cs` and
`PlayerInterface.Picking.cs` (site verbs), `GameHost.Interactive.cs` (hook), `Meitou.Engine` if needed,
`Meitou.Ui/*`; `Meitou.Rendering/WorldObjects.cs`, `ObjectStreamer.cs` and the object shader plumbing;
`Meitou.Navigation/NavSystem.cs`, `NavMeshService.cs`, `ZoneGeometryGatherer*.cs`, new `NavRuntimeBuildings.cs`.

## Production

`Production/ProductionSystem` runs the player's complete buildings: mines, wells, refiners (function 4), farms, research and
crafting benches, generators and batteries. Facts: [production](game/buildings-production.md#production), [crafting](game/buildings-production.md#crafting),
[farming](game/buildings-production.md#farming), [research](game/buildings-production.md#research), [power](game/buildings-production.md#power).
- **Machines** (`Production/Machines.cs`, data `Construction/MachineData.cs`). A machine is made when a player site completes
  or loads complete, and dropped with the site; its items fall on the ground. It holds:
  - the recipe inputs and the output, as item counts plus a level or progress (engine choice: crops and water have no quality);
  - the storage grid of a `has inventory` building (a bench's books), as a real `Inventory`;
  - the farm's plants.
- **Yield.** Set at creation from the ground under the building: a resource's value (`ResourceField.Yield`; 0 stays 0 for the player), ore from the
  nearest foliage rock (`FoliageResourceNodes`, given by the host; none headless), and for a farm its
  fertility factor, crop match and water rate.
  - Fertility and the crop match come from the BIOME_GROUP's ENVIRONMENT_RESOURCES at the weather-area cell.
  - The altitude test uses the building's y.
  - **Observed** with the vanilla map: only the central biomes give farming ground.
- **Operating.** `OperateOrder` sets `CharacterTask.Operate`. The character walks within the building's `use range` ([work
  distance](game/buildings-production.md#work-distance)) and works it every tick; work is scaled by its skill factor and trains the stat.
  - Producers make output from their inputs. An item furnace burns its grid into Raw Iron while its Active button is on ([item furnace](game/buildings-production.md#item-furnace)).
  - Farms are harvested and cleared.
  - Research benches add to the research queue; crafting benches work their queue.
  - More characters than `max operators` are refused with "X is already in use.".
  - Farms tick whether anyone is there; rain tops up an outdoor farm's water (`FarmTick`, [farming](game/buildings-production.md#farming)). Unloaded zones: the *Production far away* switch (Meitou on: farms, automatic machines and generator fuel
    run everywhere; Faithful off: only in `World.ActiveZones`, and a farm back after over an hour runs `TimeSkip` from its `save time` stamp,
    `FarmState.LiveHours`); facts in [unloaded zones](game/buildings-production.md#unloaded-zones).
- **Rocks** (`ProductionSystem.Natural.cs`, UI `PlayerInterface.Rocks.cs`; facts in [resources](game/buildings-production.md#resources)). A foliage
  rock is a natural site, operated like a mine: a right click on it with characters selected is an `OperateOrder`, with Shift an Operating machine
  job. Its site id `natural:<BUILDING>@x,y,z` (the rock's position) is all the sim needs: the site is made from it (an `is foliage` BUILDING
  with an operable mine; with a rock source, one of it must stand there), owned by no faction, so player-only lookups skip it. Its machine
  yields `ResourceField.NodeYield` (at least 0.1) and is kept in memory, not saved; a saved job keeps the id (`meitou pjob<i>`). The ore stays in
  the rock's output (5) until taken: a left click selects the rock and opens its machine window once it has been mined (**Observed** by the
  owner: the base game's ore is picked up by hand), or a storage within `HaulRange` takes it as for any machine. Engine choices: the rock is picked by its mesh's triangles where the instance places them (the original picks its building's collision); its radius, the longest half-extent of the mesh's bounds box scaled by the instance and at least `NodeRadius` 25, sizes its resource richness (the original takes the mesh's bounds the same way); without the mesh file a rock is picked as a sphere of 25 round its origin; the pointer over a rock is `mine` (with characters selected, after sites and doors, as the click order is); level-placed `is foliage`
  buildings (not drawn, not in `FoliageResourceNodes`) cannot be mined. NPC town mines are not run (the original's make output while NPCs
  operate them).
- **Items.** `PutInMachine` and `TakeFromMachine` move items between a character within 40 of the building's footprint and the machine:
  inputs, output, or storage cells.
- **Research** (`Construction/ResearchState`). A queue paid at queue time from the benches' storage; a paid id is never
  charged again.
  - Bench level is the best complete bench's `tech level`.
  - Several operators add with the 0.6^k falloff.
  - Partial progress is kept off the queue (in memory only).
  - Blueprints ([rules](game/buildings-production.md#blueprints)): `GameDatabase.AddGearBlueprints` makes the `.TECH.1` research after loading;
    `ItemFactory.Blueprint` makes the item (type `BLUEPRINT`); the own-window right click (`HeldItems.QuickMove`) calls `ProductionSystem.Learn`.
- **Hash.** Machines and research join the world hash only once the player has one; golden hashes are unchanged. Yields read the
  biome map and rock source, so a replay without them (headless) hashes other yields than the host.
  RNG: `RngPurpose.Farm` (replanting, harvest rolls), `RngPurpose.Craft` (critical rolls).
- **Crafting** (`ProductionSystem.Crafting.cs`, data `Construction/CraftingData.cs`). `QueueCraft` (at most 20),
  `CancelCraft`, `RepeatCrafts`. The first craft binds the input sections to its ingredients (other items in a section
  fall on the ground); finished items wait in the output (section `out`) up to the option's store, then the bench reports
  output full. Benches listing fewer `consumes` than a recipe's ingredients get extra sections. A repeating queue puts
  each finished craft back at its end. Crafts no research lists are always available.
- **Research repeats.** A `repeats` research can be done repeats+1 times, each paid at `cost x repeat mult^n`.
- **Power** (`ProductionSystem.Power.cs`). Generators (fuelled or wind), batteries and consumers are machines; each
  town (sites grouped by `Town`, 0 included) shares power as the [original](game/buildings-production.md#power) after the
  operators, so grants follow last tick's operators. The factor scales production, crafting, research and farm growth;
  research `improve buildings` (`ResearchState.Upgrades`) scales rates and adds output and capacity. Engine choices: health
  is 1; wind generators give their blades' share of the region's wind ([rotating parts](game/buildings-production.md#rotating-parts)), loaded or not; battery mode is not saved. HUD: output, fuel use, charge,
  "No power" / "Low power".
- **Hauling** (`ProductionSystem.Hauling.cs`, `ProductionSettings.AutoHaul`, default on; facts in
  [buildings-production](game/buildings-production.md#hauling)). An operator at its machine first puts carried items
  into empty input room. It takes a full output only when a storage will accept it (else the machine stays full) and
  carries it to the nearest player storage (function 2 or 13) that accepts it and has room; or it fetches an empty input's item from the nearest storage holding it or machine whose output holds it (a well's water for a farm). A
  storage accepts an item when `itemtype limit` matches and `limit inventory` is empty or lists it; `stackable bonus
  mult` scales its stacks. The player's drops into a storage follow the same rule ("does not take").
  Excess fetched inputs stay with the operator. Engine choices: storage within `HaulRange` 3000 of the machine, ties
  by site id, a fetch takes what the inputs have room for; the trip is not saved. Crafted items are not hauled.
- **Automatic machines** (`MachineData.IsAutomatic`, raw `max operators` 0; [facts](game/buildings-production.md#production)) produce
  every tick with nobody at them at s = 1 (producers only: refineries, mines, auto-looms, campfires), and their power demand is full. A character
  operating one (job 152, or the quick order) does not work it: it only hauls as above, every round whatever `AutoHaul` says.
- **Job lists** (`PermaJobs.cs`, `ProductionSystem.Jobs.cs`, `Saving/PermaJobSave`; facts in [perma-jobs](game/ai-tasks.md#perma-jobs)).
  `CharacterCold.Jobs` is the ordered list; `AddJob` (Shift + right click on a finished machine or storage) adds through the original's rules
  (remap, duplicate refusal, companion, switch on); `RemoveJob` is the list's X. Every `JobCheckTicks` 10 (staggered by slot) a player character
  that is idle (no task, following or wandering, as for the medic job) or on a job takes the first job that is not blocked, so a higher job takes over as soon as it can run and a blocked one passes
  to the next; a job whose site (or the engineer finder's pick) is `JobRangeSq` 4.246733e7 (≈ 6517, 3D) or more from the character is skipped
  ([range](game/ai-tasks.md#perma-jobs); players are treated as ROAM_FAR and the home-town exception is left out); an order replaces the job's task, and the list resumes once the character is idle. A job that ends returns the
  character to the following or wandering it took it from. The lists also run with no machine while a site `NeedsWork` or a job follows a character. The production pick runs the
  jobs below; first aid, carry and aggro passes run others (further below):
  - 97 Training (Shift + right click on a training dummy, function 7, or the "Training" verb): the Operate task at the dummy; `Train` raises the building's `stat used` by the capped continuous
    XP entry (`XpService.Capped`, rate 2, cap = `output rate`; [facts](game/ai-tasks.md#perma-jobs)). Blocked while the dummy has no free place or the character has no working arm.
  - 45 Bodyguard / 31 Staying close (`PermaJob.Character`; the "Follow" and "Bodyguard" verbs with Shift, `AddOrderJob` with the target): the character follows the job's character with a follow order
    (`MovementSystem.Follow`); blocked when it is gone, for 31 also while it is down; saved with the target's handle and found again from it after a load. Bodyguard's defending is the AI's own (engine
    choice: no extra rule). The jobs run with the switch off (`IgnoresSwitch`).
  - 137 / 138 corpse disposal and 223 / 273 intruders (`ProductionSystem.Errands.cs`, every `ErrandEveryTicks` 15): a free character with 138 and 137 lifts the nearest dead body (a caged one first)
    when a free corpse-disposal machine (function 11) exists and walks it there; in the machine's reach it lets go and the body is removed (items with it). With 223 and 224 (or 273 and 274) it lifts
    a knocked-out, alive character of another faction in the player's building (with doors) it stands in and puts it down just outside the door (273: past the town's farthest building). Engine
    choices: the building test (a footprint round the origin), the friendly exclusion, the reach, 4 walks that end short before the body is given up, and 223's "home" (the building the carrier is in).
  - 234 Turret guard (Shift + quick right click on a turret: 146 becomes 234): mans the turret as an operator; blocked while someone else mans it.
  - 87 Operating machine: blocked while others hold every operator place or the machine has no work (no power; no queued research or craft; a
    farm growing with no water it holds or can get, or ripe with no plant left or no room; an empty input the operator cannot fill from its pack, a storage or a machine's output;
    a full output no storage takes). A farmer at a growing farm with water tends it: it plays the farm's clip (engine reading of task 87, [farming](game/buildings-production.md#farming)).
  - 124 Hauling to (`CharacterTask.Haul`): the nearest player machine within `HaulRange` of the storage whose output the storage takes is
    emptied into the hauler's pack and carried there; items the hauler already carries that some machine makes go first. Blocked with neither.
  - 152 Operating automatic machine (Shift + Auto-Haul on an automatic machine; its companion 124 lands on the same machine): the operator above at the machine, hauling only.
    Blocked unless it is mid-trip for it, carries an item an input has room for, an empty input's item is in a storage, or a full or carried output has a storage that takes it
    (engine reading of the row's goal "no empty inputs", `AutoWork`). 124 on an automatic machine runs the same way, blocked unless it carries an item an input has room for.
  - 125 Engineer (ordered as 2 Build or 95 Repair; Shift + quick right click on an unfinished site): builds `ConstructionSystem.EngineerSite`'s
    pick, whatever the job's target ([buildings](#buildings)); its tasks are *Build*, *DeliverMaterials* and *FetchMaterials*. Blocked with no site.
  - Implicit jobs (`ImplicitPick`): with no list job to run, the switch on and not a slave, the squad AI options (`PlayerState.SquadAi`, the
    overview's AI tab, on by default) start, in this order: Ditch items (242: carried items some player machine makes go to the nearest
    storage within `HaulRange` that takes them, as a Haul trip; only with no queued orders), Sleep when injured (153: a body part below full
    health lies in the bed the bed search finds, a `BedSystem` trip (task Sleep, the bed id in `OperateSite`), until healed; job lists are then looked at
    even with no machine), Sit when idle (275: the nearest free player chair or
    throne within `SeatRange` 600). The one running is kept while it holds; any list job preempts it. Distances are from the character, or its
    squad leader while it follows, so it gets up when the leader walks off. Sitters are put on the seat's origin facing its way;
    the medic job counts them as idle.
  - The list is reordered by dragging a row (`MoveJob`).
  - Jobs on characters: the Medic and Rescue buttons (H, J) start one look for a patient or body, with Shift they add 58 / 148 (+ 106); Shift with
    the First Aid, Splint Injuries, attack or Loot verb (context menu or quick order) adds the job the order becomes (`AddOrderJob`: 58, or 57 on a
    robot, 250, 16, 238). Each runs for a character free for it (`PermaJobs.FreeFor`: idle, wandering, following, on an implicit job or on a list
    job below it; switch on unless the type ignores it), the first in list order that finds a target:
    - 58 Medic / 57 Robotics (`FirstAidSystem.HealSomeone`) and 250 Splint Rigging (`SplintSomeone`): the original's finders
      ([medic jobs](game/ai-tasks.md#medic-jobs)) over characters within sight (stand-in for the sensed list): score = need (untreated HP for
      the kits it has) × relation factor × nearness (1 - d²/1843²); the medic itself scores its need. 58 skips robot races. Splinting takes
      only its own faction, at skill min(medic, kit quality), and needs a splint kit in the medic's own inventory or backpack (as the original, [kit search](game/character-stats.md#first-aid-update)); its weight is 0.1 ([splints](game/character-stats.md#splints)). Engine: no same-building floor, no give-up list.
    - 148 rescue: the carry pass (its own faction; with Rescue Allies also allied factions, `CarrySystem.Rescues`).
    - 238 Foraging animals (`CarrySystem.Forage`): the nearest downed, uncarried animal within sight holding items, not of its own faction
      (engine choice), is looted (take-all and butchering).
    - 16 Attacking enemies: `AggroSystem` looks for the character as for an NPC (`HuntsEnemies`, not in non-combat mode); never gives up a chase; after the
      fight it goes back to following or stands idle.
  - Engine choices: the check period; kind-2 (turret)
    jobs do not run before urgent tasks; the injury test, the ranges, a bed's clip (`sleeponfloor`) and a seat's facing (its own, not the table it faces).
  - Saves: GAMESTATE_AI `pjob<i>` / `pjobT<i>` / `jobs` as the original, plus `meitou pjob<i>` (the site id) for a building the handle cannot
    name; the older `meitou jobs enabled` is read, not written; `chase mode` is written and read as the switch. The squad AI options are the
    CAMERA bools ([squad AI options](game/ai-tasks.md#squad-ai-options)). A job's trip is not saved.
- **Squad AI options** (`PlayerState.SquadAi`, the overview's AI tab, defaults as the owner's saves): Ditch, Sleep, Sit above; Heal Allies
  (the medic jobs take allied factions' patients scoring ≥ 0.1), Help Allies (`RetaliationSystem.HelpAllies`: free idle player characters
  within `JoinRange` 250 of an attacked character of an allied faction defend it, unless the attacker is the player's; engine: no distance
  weight), Rescue Allies (above), Share Food (`FeedSystem`: a player character with no food takes it from a squad mate's backpack within 200,
  only with the option), Feed Animals (off: the player's animals do not eat), Shoot first (`SquadAiRules.ShootsFirstAt`: with no enemy in its
  sights a player turret gunner also takes dangerous neutrals at weight 0.6).
- **Saves** (`Saving/MachineSave`, `ResearchSave`). Each building's production and farm keys go into its
  GAMESTATE_BUILDING. Items go into an INVENTORY_STATE `<site>-inventory`, listed first by the building, with sections
  `in<k>`, `out` and `main` (a furnace's grid `in1`).
  - A loaded farm's plants all take its growth.
  - The original's reading of these inventories is unverified: no sample holds a working player building.
- **UI** (Faithful, `PlayerInterface.Production.cs`).
  - Selecting a machine opens its window at the screen's left edge, vertically centred, an open inventory moved right of it (**Observed**, original screenshot 2026-10-26, mined iron rock): `Kenshi_GenericBuildingWindow`, or for a bench
    `Kenshi_InventoryResearchWindow`. Items are held in and out with the mouse (an input takes its record up to its capacity; `out` takes nothing).
  - The site menu gains Operate (also the quick right click); Shift + quick right click adds a job instead ([job lists](#production)).
  - The HUD shows the farm's growth, ripeness and fertility, the output, workers and tech level.
  - A crafting bench's window shows the current craft and QUEUE... (opens the Craft tab); finished items are lifted out of `out`.
  - The overview's Craft tab lists stations, their crafts (click to queue; red: locked), the queue (click to cancel),
    Repeat Queue and the hovered craft's time and inputs.
  - The overview's research tab lists categories, blueprints (click to queue), the queue (click to cancel) and a
    description with the cost.
  - The original's verb names, row widgets and colours are **Unknown**.
- **Engine gaps:**
  - A quick right click on the player's furnace walks the selection there and opens its window
    (the original's 26 LOOT_TARGET); on the player's turret the nearest able selected character operates it (146 USE_TURRET, `UseTurretOrder`): it
    walks to its gunner spot (as for town turrets below), stands there facing it holding `combatstance`, counts as its operator (so it draws power) and the
    combat system fires it ([Ranged](#ranged)); one gunner, engine choice; someone else at it: "is already in use".
  - Plants are not drawn.

### Prospecting

Facts: [Prospecting](game/ui-screens.md#prospecting), [Task_Prospecting](game/ai-tasks.md#prospecting). `ProductionSystem` (`ProductionSystem.Prospecting.cs`, `Production/Prospecting.cs`) owns it because it holds the `ResourceField`.
- `ProspectOrder` names one player character (the host picks the selected one nearest the camera pivot). If it is awake, not fighting, carried, carrying, caged or swimming (water state 3) it stops and takes
  `CharacterTask.Prospect` with `CharacterCold.Prospecting` = 0 (hashed only while set, not saved). The same controller's repeated order preserves progress; when done it is `Idle`.
- Each tick (`SlowWorld`, before the early returns) the progress grows by the tick's game seconds. At `ProspectSeconds` = 1 (**Verified**) the task ends (`Idle`) and a `ProspectReport` is published to the recipient's `PlayerState.Prospected` (`World.Prospected` aliases the local player's report): the
  prospector's name and science (raw stat), the spot (its position), the zone name and the crop coverages, and the five resource values from `ResourceField.Yield` at the spot. Another task (a move, an attack, a stop) or the character going down
  ends it with no report. The report is display only: nothing reads it, the state hash does not see it, a save does not hold it.
- Multiplayer surveys capture and continuously validate the actor's grant. Transfer/revocation cancels the old task; a new controller starts fresh. Each recipient has an independent result serial. **Verified** by `MultiplayerProspectingTests`; network delivery and bounds are specified in [networking](networking.md#private-results-and-transient-events).
- Map: the report carries `ProspectMap`, sampled once at the end (terrain height `IWalkability.TerrainHeight`, the field resources by `ResourceField.FieldYield` per cell with the spot's record, the rocks in range by `NodeYield`); the window colours it per checked row. The report also carries the wind of the spot's region at that moment (`World.Weather.At`, 0 without weather). The window's camera marker is drawn each frame at the camera's
  target (the strategy camera's pivot) and turned by the level direction from the eye to it.
- Animation: the `medic` clip (`AnimationSystem.HeldClip`). HUD goal text "Prospecting". **Not modelled:** the progress bar over the character, the state field `+0x198`.
- Engine choice: the nearest-to-pivot rule uses the selection (the original's iterates the player interface's list, **Observed**).

## Trade

`TradeSystem`: trader-squad stock/purse/restock, open trade, buying/selling/fencing. Charge,
quality, culture, market, forbidden/stolen factors follow [economy.md](game/economy.md).
A squad shop holds one 18 x 18 stock grid (the building storage size and the vanilla trader window width); `ArrangeStock` arranges it. A housed trader whose template makes no
prisoners or slaves and whose home has stock containers gets a building shop instead ([building shops](game/economy.md#building-shops-observed-decompile)): one stock with a rectangle
per stock container, each container's window showing only its own (`Shop.ForBuilding`, `Shop.Building`), its own purse rule, the containers' locks opened at each fill. Stock cells stack 50 x an item's `stackable`. A housed squad with vendors that is no
trader has its home's containers filled instead (`FillHome`). Only members that trade (`DialogueSystem.Trades`, [who trades](game/economy.md#who-trades)) publish `IsTrader`, the
Trade verb and pointer. Shop state is hashed; the open trade is not, except its buyback receipts (below). A shop is saved on its platoon (`Saving/ShopSave`: stock, fill count,
`inventory refresh time`, [economy](game/economy.md#building-shops-observed-decompile)) and kept by a saved platoon that leaves the world; a save without it gets a fresh fill.
Transfers and money changes must succeed together. Stealing from containers: [crimes](#crimes-and-bounties). Smuggling: the refusal and event 8 (`TradeSystem.Smuggling`) and the held item's prompt ([economy](game/economy.md#fencing-stealing-and-smuggling)); not built: a smuggler's own answer (read by no caller seen).

Bags (engine choice; how the original trades bags and their contents is **Unknown**): a bag trades whole, the same instance with its contents grid, and only empty ("That bag
isn't empty."), since its contents would otherwise go unpriced and unchecked for theft; a bag's contents are not shown as stock. A right click puts a bought bag into a free
backpack slot, else the main grid; a drag puts it where it is dropped, the backpack slot included, never into another bag. Other items: a right click tries `TryEquip`, then the grids.

Buyback (the original's: [economy](game/economy.md#buyback-during-an-open-trade)): a purchase or sale leaves a receipt on the moved units (`TradeReceipt`, the coins paid per
unit, kept through stack splits); moving them back in the same open trade costs or refunds exactly those coins and clears it. Units of the same trade and direction share a stack
(their coins join); they top up plain stacks only when no free cell is left, losing the receipt for those units. Closing or replacing the trade voids every receipt, moved items'
too; stacks it kept apart stay apart. Receipts are in the state hash but not saved, so a save made mid-trade reloads without them, with a different hash.

## Dialogue

`DialogueSystem` (game facts: [dialogue](game/dialogue.md)). The context menu's Talk To (only when a dry run of the line pick, dice and memo put back, finds something to say) sends `TalkOrder`: the
talker walks up (60 units, engine choice; 60 s to arrive), the NPC is held, then event 1 fires and the NPC's dialogue set (record, squad
template, leader role; `DialogueDatabase`) is chosen from by score, line checks and a seeded pick. The NPC line is said (text, tokens and
word swaps, effects at once); passing replies become choices, an NPC-side child a single `[...]`. The host opens `Kenshi_ConversationPanel`
modal, which pauses the game; `DialogueChoice` and `EndConversation` apply between ticks at speed 0; effects that order fights are deferred
to the next tick. A hostile target is attacked instead.

- **Effects built**: locks, interrupts, `change relations`, trade, relations, the three attacks, take and give money, faction rank (DA 35, read by DC 10), the surgeon's editor
  (paid by the line, `EditCharacter.Paid`), assign and clear bounty, lock this dialogue, DA 37 / 38 (a campaign's phase), `trigger campaign` (`PopulationSystem.TriggerFromDialogue`: the rules in
  [campaigns](game/factions-squads-towns.md#campaigns); `world state` conditions read the live states), recruiting (DA 3 / 18, `DialogueSystem.Recruit`), crowd
  triggers, `give item`, `change AI` and `AI contract` (`Ai.ChangeAi`, `Ai.StartContract` with the target: `Blackboard.MissionTarget`, followed by goals aimed at the
  squad mission through `AiMind.FollowTarget`; on its end the leader's `contract end talk passive`), DA 25 / 53 (`FixtureRegistry.TagEscaped` / `FreeSlave`) and DA 29
  (`ArrestSystem.ArrestFromDialogue`), DA 43 (the bail list, [Arrest and jail](#arrest-and-jail)), DA 7, 13, 14, 16, 19-21, 23, 28, 33, 34, 52, 54, 56 (`DialogueSystem.Acts`); the rest is listed in [dialogue](game/dialogue.md#meitou-design).
- **Events from other systems**: `DialogueEvent` (deferred to the next tick) fires an event at an NPC as bubbles, or a given DIALOGUE (the contract end talk);
  first aid, shackles and the release rounds send them. The police's spotting fires 44 / 45 at once (`FireEvent`, which says whether a line was said).
- **Recruiting** ([facts](game/dialogue.md#recruiting)): at the `max faction size` cap the line "It looks like your squad is a little full." is
  posted and the conversation ends; otherwise the speaker leaves its squad (its leader passes to the first member left) and joins the player's
  active squad (the first selected player character's, else `Player.Squad`, else the talker's), or a new "blank squad" squad when that one has
  `max squad size` members: player faction, role Squad1, NPC class Adventurer (as every player-faction character, `CharacterAssembly`), player
  default orders, speed mode Free (2), no AI mind, stopped, its save link dropped (a save writes it as a new member of the player's platoon).
  Its dialogue becomes the player set (`DialogueDatabase.SetFor`). DA 3 ends the conversation and opens the editor in recruit mode
  (`EditKind.Recruit`: free, race and gender kept). Not modelled: the original's extra empty platoon at the `max squads` cap, and the
  uniform clear (Meitou has no uniforms).
- **Conditions**: most DCs are computed (slavery: 59, 61, 62, 64; faction trust 3; tech level 12; town levels 73, 74); those needing state Meitou lacks (AI flags, strength, perception counters, walls,
  training equipment) compute "no"; DC 69 HAS_ILLEGAL_ITEM (the gate guards' inspections) checks the culture's `illegal goods` ([dialogue](game/dialogue.md#conditions)), posting each find (not on the Talk menu's dry run, once per carrier and item per game hour: engine choice). IN_MY_BUILDING uses the squad's home building and the lock rounds' extent; IS_INDOORS the sight map's building.
- **Squad memory** (`SquadMemory`, `DialogueSystem.Memory`; [facts](game/dialogue.md#squad-memory)): per squad and character a time and long / short tags, the player faction
  sharing one long-term entry; DC 18, 20, 23 and 52-55 and the aggro scan (TEMP ENEMY / ALLY) read it, the effects and event-1 ends write it; it stays with a squad's platoon while the squad is out of the world and is saved on the platoon (`MemorySave`) ([engine choices](game/dialogue.md#meitou-design)).
- **Speech bubbles**: `WorldSnapshot.Speech` lists the lines on show (`SpeechBubble`: speaker, text, large font); `PlayerInterface.AddSpeech` draws them over the head. Sources and engine choices: [dialogue](game/dialogue.md#meitou-design).
- **Personality**: the save's `personality` when above 0, else an engine roll from the record's PERSONALITY tags (`Personality`).
- **Engine choices**: a childless NPC line shows `[Leave]`; a reply leading to another reply offers it alone; number keys pick choices.
- **Multiplayer follow-ups**: interrupts produced by attributed dialogue belong to the initiating player and faction. Other accounts, including same-faction members, cannot consume them; another faction of the same player cannot consume them either. Unattributed world-script and legacy interrupts retain their shared NPC scope. The recipient's pending icon is separate from actor-specific eligibility. **Verified** by `DialogueTests.Hints`; [network queries](networking.md#talk-hints) recheck current grants and visibility.
- **State**: last use per line, permanent chance rolls, event counters, squad memory, interrupts and the open conversation are hashed, not
  saved (as the original, except the squad memory, which it saves on the platoon; the faction global trust and war flags it saves under `meitou` keys, [save](formats/save.md#faction-trust-war-flags-bed-rent-and-turrets)). Locks are on `CharacterCold.DialogueLocks`, hashed while set, saved as `lok*` (`DialogueSave`).

## AI

`AiSystem`, blackboards/minds, package/task data: slot/class/time/weight selection, homes/beds/nodes,
patrol/bar/shop/sleep/retreat/contracts, combat stances and off-screen roaming jobs. Data and
TaskData semantics: [ai.md](game/ai.md), [ai-tasks.md](game/ai-tasks.md). Wars and prisoners are incomplete; residents
off screen only come back where their schedule puts them ([off-screen tier](#off-screen-tier)). Hash blackboards/minds/jobs when present.

### Package classes

`PackageClasses.Of` maps the `signal func` ([ai.md](game/ai.md#package-classes)); `Blackboard` runs the original's slot walk with the class rules of `AiSystem`
(`IPackageRules`: start, finished, started, ended, may lead). Built: plain, retreat, patrol (route an engine choice), bar, contracts, and
(`AiSystem.Packages.cs`) the wandering trader (16), passing-by assault (29), occupation (30) and refugee (22: a slave-led squad of fewer than 3 flees to the nearest other town of its faction, a broken one joins a healthy squad there, `SquadManagement.Merge`) as decoded. Not built (never start but as a lone slot-0
package): the war classes (19, 20, 32) and 31, which run only in campaigns; the campaign drives its platoons itself.
- **Current town** (`Blackboard.InTown`, on the package cadence before the choice): nearest town by X/Z, placed towns by `TownData.Radius`, player towns by their
  own (`PlayerTown.Radius`); entering one fires the leader's ENTER_TOWN; the town a squad starts in counts as entered (engine choice: no line for every squad a town makes).
- **Trader**: as the original (stay, chooser, states, leave rule); the leader's WANDERING_TRADER goal walks to the town (`AiBehaviour.Travel`, also
  TRAVEL_TO_TARGET_TOWN and its fast form; `atDestinationTown` is the package's town while it has one), members follow. Engine choices: a town's own squad never
  starts it (Meitou unloads a town's squads with it); a bar package of a squad that is not a town's own takes the bar of the placed town it is in, none in a player
  town. **Observed** (headless, Hub area, 150 000 ticks): a slaver squad reaches the Hub, waits 7.5 h until 06:00 and leaves for Skinne.
- **Assault and occupation**: the squad fights the town's faction (`Squad.War`) and roams the town (its wander centre; RUN_AROUND_TOWN_LOOKING_FOR_PEOPLE acts as
  patrolling); engine choice: the members' posts move into the town, so idle, node and bed goals stay there. The faction clocks (`AiSystem`, hashed once set) are
  not saved, as the original's. An occupier is persistent, as the original's: kept out of the world by every switch (site unload, sector move past its count, a save's
  platoon), saved `persistent`, and when it comes back in the player town it goes on occupying (`Blackboard.Resume`: mission town and enemy again, no line; the town is the one it is made in;
  its members' minds come after, so each takes its post round the town as its mind is made, not where it was saved);
  **Observed** (headless, an owner save with starving bandits occupying an outpost, Faithful switches): they occupy again on load, unload when the players leave
  and come back occupying;
  a save's wild platoon is AI-run like the wild sites' own squads. The occupation's intruder, cage and food goals: [Town duties](#town-duties); its bed goal (153, 1 to 9 h; not the leader's) takes a bed of the occupied town ([beds](game/locks.md#beds)); a member whose bed is taken rests beside it, one in a town without beds at its post. Campaigns read only the loaded occupiers (`AiSystem.Occupied`; they need a player within 1250 of the
  town, so it is loaded). **Observed** (headless, Stack area, a starving-bandit mob put into an outpost with 3 player characters): the assault starts, the
  occupation follows 0.1 h after the players are knocked out. No fight started in that run (the aggro's sight of sandbox characters there was not looked into).

### Planner

`TaskPlanner` runs the original's search ([ai-tasks](game/ai-tasks.md#planner)) over `TaskDataTable` (all 253 rows) for the
goals the engine acts out. The think orders each tier's goals by score, leaves out one whose list A holds, and takes the first
whose plan succeeds (`AiSystem.Choose`); `AiMind.Plan` holds it leaf first and `AiMind.Step` (hashed) its first step. The goal's
act-out does the walk and the goal's own action; steps of their own are getting out of bed, sheathing and drawing a weapon (one think each),
going home (19, the GoHome walk), entering its home building (156, the same walk), leaving it (200, to A + (A − B) × 1.1 of the home's first door,
the outer point taken as the side away from the building's origin, until outside) and getting near a target (40, a walk to within 10). A plan
whose first step is neither the goal nor a step the engine carries out (`AiSystem.Carried`) fails. Engine choices:
- Everyone plans with category 0x1; no character is squadless, so restricted rows (lockpicking, cutting, opening gates) never
  enter a plan. A row without a finder wants its effect for its own target. At most 64 nodes per search.
- A node is made once per row and target (the original allows four copies), so the ancestor-repeat test never fires; a child already
  closed is skipped rather than accepted from the node.
- Only the step being carried out counts as queued, and only while it can run (Meitou's steps do not end on their own).
- Finders: the spots, beds, seats, turrets and towns the act-outs already pick, at the floor point the walk ends on (`PlanContext.Find`); other finders find nothing,
  so such goals do not plan. Rows on the way have their `TaskProfiles` desire, else 1.
- States (`PlanContext.Holds`): location, distance, bed, sitting, home (within 20 of the home), home town, town, weapon drawn, weapon carried
  (`hasWeapon`), carrying and inside the home building (107, `AI::iAmInsideTargetBuilding` for a non-building target) are evaluated; moving, getting up, arms, power, buildings, doors, gates, accessibility, machine slots and readiness are
  held true (movement and the lock-up round deal with doors; the bashers' `DoorPlan` evaluates them); every other state is false.

Combat stances (`AiStance`, the most aggressive combat goal in its hours; none at all attacks on sight): PROTECT_OWN_SQUAD joins only
fights of its own squad, against a mate's target or a mate's attacker (as the original's finder, [ai-tasks](game/ai-tasks.md#combat-goals));
the other PROTECT goals still join any fighting enemy (engine).

Scoring follows the original (`TaskProfiles`: desire, commitment, crowding per goal type); a desire
the original computes in a function of its own counts as 1. Town goals ([ai-tasks.md](game/ai-tasks.md#town-goals)):
wandering and patrolling in town walk to a general node of a public town building in the walker's zone
(public: the residents' template's `public day`, else the level state's `is public day`, else public; drawn
among those with a general node, engine choice), shopping to a node of a
public shop (as the original's finders, [ai-tasks](game/ai-tasks.md#town-goals); no faction test), relaxing under a bar package, sitting around (275) and staying home (43; the home is the bar under a bar package) sit on a seat claimed per
member on the package cadence, and the wanderer walks to the nearest town. A bar package (both kinds) reserves
one table per squad as the original (`AiSystem.PickTable`: public bars tried from a random one, first table no
other bar squad holds; no bar, or the outdoors kind: a free table placed outdoors in the town; none free: the
squad keeps its home and does not relax); its members take the free seats nearest that table (engine choice),
at an outdoor table they stand at it. A home node goal takes a
node of its kind, a guard post a general node next (as the original), then any node but a shopkeeper's
(engine; only the shopkeeper goal falls back onto the counter). Gates have no nodes: the gate guard
(180, the home gate, else the town's gate nearest the squad) stands 15 out from the gate's first door
leaf, on the side away from the town centre (the side is an engine choice), facing out; a member whose
guard or general node goal finds nothing in its home gate flanks the opening on that side, beyond the
leaf's edges (half width + 6, steps of 7), alternately left and right in two rows 8 apart (engine).
A gate's residents are made at the gate guard's spot (engine choice), not the gate building's origin, which on
Defensive Gates is inside the wall about 110 along it from the opening; headless (no door gate, no leaf) they
are made at the origin and keep that post. Seats are the home's
interior furniture with a CHAIR functionality (`HomeSpotKind.Chair`; never a standing node), free
in that building this pass; relaxing falls back to a bar node, then a general node, sitting around
has none and is not wanted without a chair (without a home it sits where it is), staying home without one stands at home (engine choice: the original's
43 operates home furniture, chairs among it, [ai-tasks](game/ai-tasks.md#town-goals); a given-up seat is tried again when the goal is renewed). A seated character
is put on the seat's origin with no offset, facing the seat's way ([ai-tasks](game/ai-tasks.md) "Sitting"), and plays the functionality's clip ("sitting chair", pillows
"sitting idle"). A town's bars and shops are
the buildings whose residents (the squads living there, else the level data's `residents template`)
have designation BAR or a shop (`AiSystem.RefreshPlaces`). Walks to nodes, beds and seats pass the
spot's height and count as arrived only on that floor, and end within `MovementSystem.SpotArrive` (2) of
it, not `ArriveRadius` (10), without separation over the last 10 (engine choice: the barman stands at his node). Furniture cuts holes in a floor's navmesh and
the ground below shows through them (Observed: the Storm House at the Hub, counter, tables, stools, 22..28
below), so a spot whose floor is not under it is walked to (and residents spawned) at the nearest floor
point within 12 (`IWalkability.FloorNear`), and no step goes down into such a hole (`StandDrop`,
[pathfinding](game/pathfinding.md)). A home with no node makes its squads at its origin; for a building with an
interior (not a gate, whose guards stay on the ground under its walkway) they go on its lowest floor more than 12 above the terrain within 40 of the
origin, else the ground there (raised Swamp Houses and Cannibal Shacks: floor 25..55 above the origin, the ground under it within 10). A node with no
floor near it puts them on the nearest meshed storey below (`PopulationSystem.HomeFloor`, engine choices). **Observed** (headless probe of every
town, 600 ticks): characters on the ground under a building floor went from 278 to 128 of 9060; ~80 of those are gate guards, the rest mostly
homeless squads placed under unmeshed platforms. The Storm House's upper storey (+66) is on the navmesh except under its beds, whose
spots therefore stand on the floor below (Observed, 2026-10-18 probe). Engine choices: patrolling town acts as
wandering (its task class is undecoded); a committed goal chosen again after its commitment ran out
starts over with a new target; training and throne furniture of 275's mask are not used; shops stand customers at a general
node, else a guard post, else any node; a home or seat given up
after three failed walks is tried again when a new goal is taken up, and a member that gave its seat up
leaves it to the next; a member keeps its seat while it still relaxes or sits (every squad's kept seats are reserved before any claims), so a
patron coming or going does not move every later sitter to another chair. Headless Hub, Squin and Stack bars (2026-10-21): each patron sits once
and stays for 12000 ticks, with or without that rule (the reported sit/stand churn is not reproduced). **Observed** (headless Hub, 2026-10-26): the
Storm House's 5 tables go to `hub squatters` squads (package signal 17, STAY_IN_HOME only), so the visiting bar squads get none and shop at the counter.

Turret goals (149, 222, 234, 146) man a turret of the squad's town: a placed BUILDING with a
`BUILDING_FUNCTIONALITY` function 12 (`TownBuilding.Turret`: its `use range` and `gun turret`
GUN_DATA). In member order each slot claims the nearest free turret and walks to a spot behind its
gun, facing its way; the home-building goal (222) takes only a turret mounted on its home (the
state's `mounted` handle). Claim order and gunner spot are engine choices. The goals are done while
no enemy is near (list A `isNoEnemiesInVicinity`, [combat.md](game/combat.md#turrets)): the planner holds it false while an enemy
(the relations) is within `AiSystem.EnemyVicinity` 1000 (engine choice: the original's radius is Unknown), so guards man the turrets only
while enemies come. A guard at its turret fires it ([Ranged](#ranged)).

## New-game difficulty

`NewGameSettings` in the game host binds seven numeric options to each new world: body hunger/death/damage,
production/research, construction and nest density. Defaults (1), ranges and save keys follow
[the original](game/character-stats.md#new-game-advanced-options-that-change-these-mechanics).
The two new-game panels share one modal screen; expanding raises the start panel, and its Begin/Cancel remain usable.
Opening a fresh new-game dialog resets the draft; collapsing keeps it. Failed world replacement retains the running
world's options and body factory. Saves carry the original's CAMERA floats `ht cod gdm ps rs bs nnm`, so Meitou and
original saves are interchangeable; loading follows the original's rules exactly (the
[load rules](game/character-stats.md#new-game-advanced-options-that-change-these-mechanics), no clamping). `meitou difficulty <key>` floats of earlier Meitou saves are ignored and dropped on the next save.
Under the sliders are the original's two ticks, saved under its own CAMERA bools: Bandits loot the player (`bl`, default on,
`PlayerState.BanditsLoot`, set on a new world) and Easy prospecting (`ep`, default off, kept and saved only: the original never reads it).

## Saves

`Meitou.Data.Save` parses/writes folders; `Meitou.Simulation.Saving` maps world state. Capture
serially at a boundary, write/read on workers; `ReplaceWorld` publishes a successfully loaded
world and preserves the running world on failure. [save.md](formats/save.md) owns file keys,
handles and original saved/derived/regenerated state.

Code map:
- **Capture.** `SaveCapture.PrepareBase` clones the loaded `SaveGame` (else builds a fresh one with `SaveBuilder`). `Capture` writes what
  the world models over the clone, so every key Meitou does not model passes through. Characters from the loaded save keep their own records
  (`WriteCharacter` gets their `SaveCharacter`; `SaveBuilder.AddCharacter` only makes new ones). Per-system key helpers live in `Saving/*Save.cs`.
- **Host hooks.** `SaveCaptureOptions` carries what only the host knows: `Bodies`, `View` (the strategy camera). `GameHost.CaptureOptions`
  fills them in. `GameHost.SwapInSave` applies host-side state after `SaveLoader.Load`: the camera and the weather.
- **Load.** `SaveLoader.Load` makes characters before the first tick. The character table copies its previous state over the next one at
  `BeginTick`, so state that must reach a tick is not written into `Next` there. It goes through `World.Commands` like player input.

### Quicksave and quickload

F5/F9 and named save menus share mapping. Restore clock, player money/faction, relation/prosperity,
player squads, character transforms/names/roles, stats/medical parts, appearance, inventories,
bounties, bed/cage state and sentence, and registered buildings' locks and doors (into the zone files: every visited zone gets one, made from the level data when the
loaded save has none, [save.md](formats/save.md#zone-files)) and selection; the host restores and writes every region's season, weather, strength and wind ([BIOMES](formats/save.md#biomes-type-28-quicksave-weather-per-region)). NPC platoons whose `basetown` is a placed town load as their saved characters at their saved positions: town-bound ones (not travellers) are pinned to the town (no trips, AI-run, homed in the building their `homebuilding` handle names, else at the site) and a town holding any makes no squads of its own; travellers become roaming stand-ins made from the saved characters on activation. A platoon that unloads keeps its members' state. Meitou saves write them back and the population's own squads as new platoons with their town and home building by handle ([save.md](formats/save.md#npc-platoons-meitou-mapping)). A save's nests are the wild layout's first, and its wild platoons load as themselves bound to their nest (`hometown`) or the sector they stand in, which make only the rest; Meitou saves write the nests made so far and the wild squads the original's way, the others rolled again from the saved seed ([save.md](formats/save.md#nests-town-list)); nest buildings and debris are not placed at all (`TownData.Debris` / `DebrisBuilding` are read, unused), so there are none to save. Shops, campaign phase clocks and whole-body ragdolls are saved (Trade, Campaigns, Ragdolls above). Orders/paths, animation,
combat engagement, wound-list/pain, base-game building state beyond locks and doors (interiors, inventories: a new zone file leaves them to the original's
generation), ground items and game speed are not fully restored. The camera view is ([saved camera](formats/camera.md#saved-camera)). Loaded hashes need not match captured hashes: compare carried fields and agreement
between repeated loads. QuickSaveTests checks carried fields and repeatable loading.

**Crime and jail** (keys in [save.md](formats/save.md#locks-cages-and-sentences)). Saved: bounty ledger (`bountyexp` written with a minute and a tick of slack over the minute-floored
save clock, so an entry does not lose an hour per save and load; `BountySave`), held state and sentence, the escaped flag, the recently-wanted
and recognition timers (player platoon characters, written only while set), the locks of interior cages and containers (an imprisoned character stays locked in), and the homes a
lock-up round locked (without the flag a loaded night's locks would never be undone). **Restarted, not saved** (engine choice; the guard, hunter, releaser and warden of each job is a
town NPC the population makes again with a new id, so a saved link would dangle): arrests (a downed target lies where it fell, is spotted again once awake, the saved markers make
that quick), cagings (the carry link is not saved: order Put in Cage again), hunts and their rest timers, release, lock-up and warden rounds (the 30-tick sweep finds
them again), the unreachable-cage memo (relearned by a failed walk), lock picking (an order), pending crimes
(seconds long, victim id unsaveable). Spawn bounties are not saved: they are rolled when the population makes an NPC, never for a loaded character.

## Sandbox

Headless simulation/debug harnesses use selected StandardParts and synchronous services for
repeatability. Sandbox omits Aggro when its scripted foe must remain idle; benchmarks must
separate simulation work, startup and rendering.
`--sandbox-races` adds one character of every person race (the first CHARACTER record whose `race` list holds only that race; base game and
mods alike) in rings round the pair, for looking at races side by side.
The panel's **animation** drop-down (top right) forces a clip on the hero: the list holds every ANIMATION and COMBAT_TECHNIQUE record
name, typed letters filter it, the wheel, Up / Down and PageUp / PageDown scroll it, a click or Enter picks, Esc closes. The pick loops by the wall clock
from the moment it was picked (a technique keeps only its root height, as in the game), drawn by the host in place of the simulation's layers;
"simulation" gives the hero back. Nothing is sent to the simulation.

## Duel

The deterministic duel harness locates a scripted knockout, runs the pre-lap fight headless,
then replays the last 40 seconds and 4 after KO with cinematic keys. Replacing the world/clock
and resetting camera evaluation is required for repeatable recording.

## Battle

`--battle n unit m unit [rows] [--battle-map plateau|mesa|sandbox|x,z] [--battle-width a[,b]] [--battle-no-fear]` (`GameHost.Battle.cs`): two blocks of NPCs, a squad each (each one CHARACTER or
ANIMAL_CHARACTER record, its own faction's looks, the first fighter leading) 40 units apart in ranks of 25 (or `rows` ranks of n / rows, the back rank taking the rest; `--battle-width` sets
each side's rank length instead, one value for both), 6 units apart
(times the race's `pathfind footprint radius` / 4 when larger, the front rank at least that far from the middle), on a plateau (default, -74028, 13428), the duel's mesa, the sandbox floor or a map spot, with no towns
and no Aggro. The host orders every standing fighter without a combat target at the nearest standing enemy, once per world tick, so
same-faction matchups fight too. Once a second a fighter left out of a target with more than 5 after it (circling) turns on the nearest standing
enemy with room, as the original's target score counts a candidate's engaged fighters against it ([combat](game/combat.md#attack-slots-and-circling)). `--battle-no-fear` leaves out the flee system, so no squad breaks and runs. The battle ends the first tick a side has nobody standing. Backspace rebuilds the world the same way.
A counter at the top middle (with the interface, stills too) gives each side's fighters standing (not incapacitated) and alive (not dead) of how many it started with.

## The game host

`Meitou.Game/GameHost*.cs` adapts input to commands, drives WorldSession, navmesh upkeep,
interpolated CharacterDrawList, HUD, loading/transitions and saves. It does not duplicate system
order. Renderer state lives in Rendering; snapshots are the simulation-to-host boundary.

## Town house purchases

`BuyBuilding` (the panel's `For sale:` price, then the original's confirmation; [original rules](game/ui-screens.md#selected-building))
rechecks the sale flag, town kind, owner and alliance, the quoted price and the money, then takes the money once.
Sellable: function 19, or no function and interior meshes; no walls, gates or `is inside building` (the original's class and
parent tests are not fully decoded). Ruined houses register without fixtures. The bought house's fixtures, loose-item slots and
indoor build permission become the player's, NPC lock rounds on it stop, and it turns private (**Meitou** choice: the original
callback's other effects are **Unknown**). Saved as `owner faction ID` with `sell home` false; a loaded player owner that is not the
level's counts as bought (saved again) and is applied before the fixtures are made.

## Open questions and known gaps

Original-game unknowns are maintained in each linked subsystem reference; do not infer parity from our determinism tests.

**Engine gaps** are listed where they apply, not here: [unique characters](#unique-characters) and [campaigns](#off-screen-tier),
[ranged](#ranged) (bolts before a building's collision loads), [locks](#cages-beds-and-locks),
[slavery](#arrest-and-jail), [kidnapping](#kidnapping), [buildings](#buildings) and walls, [production](#production) (turrets,
plants), [prospecting](#prospecting), [dialogue](#dialogue) (conditions on state Meitou lacks), [AI](#ai) (wars, prisoners, planner finders).
Smuggling and stealing: [trade](#trade). UI: [faithful-ui-parity](faithful-ui-parity.md).

**Behaviour and hash constraints.**
- No end-of-tick hook: the combat system swaps its arrays at the end of its `SlowWorld`, so the result depends on the system order
  ([Systems and their order](#systems-and-their-order)). Moving the swap would change what retaliation reads.
- The two `MedicalContext` builders (`BodySystem` and `CombatSystem.MakeContext`) agree on toughness, strength, encumbrance (the body system writes it to
  `Fighter.EncumbranceFactor`) and seed, and differ in `BodyTimeScale` (combat's is 1) and the bed fields (`Bed`, `BedFactor`, `Resting`, `HealMult`; combat's are unset). So they
  are equivalent only at time scale 1 and outside beds and cages; with the game's `TickMedical = false` the combat one only serves hits, where neither matters.
- Combat ticks are `int` (`CombatSlot.ReadyTick`, `StunUntil`) while the world's tick is `long`.
- `ItemInstance.StolenFrom` is not included in the inventory hash. Adding it requires updating the affected carry/crime pins; pins are platform-specific.
- `BodyHash` does not cover the private `starveGrace` and `starvationKo` of `MedicalState` (nor every flag such as `BloodKo`), so a divergence
  there shows in the hash only later.
- `CharacterHot.Animation`/`AnimationTime` (the old movement layers) and the name length are still hashed; removing them moves every hash.
- `HoursPerTick` is a constant of its own, not derived from `WorldSettings.TickSeconds`.
- Navmesh: the cache key does not see a changed collision file; a running build is not stopped on unload (it finishes into the cache).
