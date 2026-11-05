# Ragdolls

Runtime behaviour of character ragdolls in `kenshi_x64.exe`. The `.phs` file layout is in
[phs](../formats/phs.md); carrying/looting links in [carry-loot](carry-loot.md). Addresses are
functions of `kenshi_x64.exe`. Units are world units (0.1 m). Everything is **Observed** (static
reading of the cited function) unless marked otherwise. Our implementation and its engine choices:
[simulation](../simulation.md#ragdolls). **Verified** by `RagdollJointTests`: the human revolute
joints' bases and limits hold ≥99% of the male skeleton's clip poses (knees and elbows bend the way
the file allows).

## Middleware and scene

- **PhysX 2.8** (`NxCreatePhysicsSDK` version `0x2080400`, NxuStream loaders; the install ships
  PhysX system software 9.10.0513). **Verified**: import and constant in `FUN_1407dbee0`.
- Havok `hkb*`/`hka*`/`hkp*` ragdoll class names exist only as reflection data (`?@14210c…`, no code
  references); no ragdoll code uses them.
- Scene (`FUN_1407dbee0`): gravity (0, −98.1, 0); max timestep 1/60, max iterations 16, time-step
  method 1 (variable); flags 0x44; hardware simulation tried first, software on failure.
  SDK parameters: skin width 0.25, default sleep linear velocity² 2.25, angular velocity² 1.96,
  dynamic and static friction scaling 4.0. Four materials share dynamic friction 1.0, static 20.0,
  restitution 0.05 (the default material too); ragdoll shapes use the one at physics `+0x360`.
- PhysX call meanings (flags, damping, sleep, forces) come from NxActor/NxScene vtable slots matched
  to the published 2.8 SDK headers.
- Stepping (`PhysicsActual::vfunc_2` `FUN_1407dd060`, background thread): `simulate(min(dt, 0.04))`
  (0 while paused), flush, blocking fetch.

## Collision groups

A ragdoll's shapes get group **3**, or **4** while character slot 103 (`0x338`, `FUN_1405c8740`:
true roughly when inside a building) is true; `FUN_1407d3c00` swaps it as that changes (not in carry
mode). Groups 3/4 are also items ([collision](../formats/collision.md#collision-groups-observed-fun_14055f6e0-and-the-loaders)).
Pairs disabled in `FUN_1407dbee0` (third argument hidden on a few calls, assumed 0 like the rest):

| Group | Does not collide with |
|---|---|
| 0, 1 | 6, 9–12, 14, 15–18, 23–26 |
| 2 | 1, 4, 6, 14, 28–31 |
| 3 | 0, 1, 5, 6, 7, 23–26, 28–31 |
| 4 | as 3, plus 2 and 14 |
| 5 (doors) | 0, 1, 3, 4, 6, 14, 23–26 |
| 7 | every group 0–31 |
| 28–31 | 0, 1, 3, 4, 14, 23–26, 6 (last pair's group assumed) |

So ragdolls hit each other, items, building floors/stairs/furniture/roofs (9–22, 27), but not doors,
foliage group 6 or passable parts.

Meitou runtime collision (**Observed**, source review): graphical and dedicated hosts share
`WorldContent`'s navigation overlay with `RagdollGeometry`. Its `IRagdollEnvironment.Generation`
is the overlay's `long Revision`; changes discard cached zones, and workers gather frozen overlay
snapshots. Covered physics tiles retry older revisions, retaining old building meshes until the
replacement is ready; an empty result removes the old mesh. Disposal waits for obsolete gathers too.
Frozen ragdolls do not request coverage until unfrozen. **Unknown**: live added/removed/ruined-house
collision regression on this implementation; existing tests do not exercise overlay-driven refresh.

## RagdollPart and requests

`enum RagdollPart { NONE=0, WHOLE=1, RIGHT_ARM=2, LEFT_ARM=4, HEAD=8, RIGHT_LEG=16, LEFT_LEG=32,
CARRY_MODE=2048 }` (**Verified**: `fcs_enums.def`; LOCATIONAL_DAMAGE `collapse part` uses it). The exe
also uses 0x8000 internally as "release all".

- The active mask is AnimationClass `+0x2f8` (`FUN_1407d1390` all-bits test, `FUN_1407d13e0` WHOLE,
  `FUN_1407d13c0` CARRY_MODE). "Ragdolled or carried" `FUN_1407d1440` = Character `+0x3d4` or the
  RagdollAnimation WHOLE/CARRY flags.
- `FUN_1405cbd60(character, on, parts)` queues `{on, parts}` in Character `+0x3e0`; a queued
  opposite request for the same parts cancels both, a duplicate is dropped. Only when the character
  has a visual or is dead (slot 31) and, for "on", is not in furniture (Character `+0x2f8`, the
  bed/cage state of [carry-loot](carry-loot.md)).
- The queue is drained at the sync point `FUN_1407d2380` → `FUN_1405d2c30` → setter `FUN_1405d0db0`,
  which keeps a request queued while the skeleton is not ready or a get-up blend runs.
- The setter, for WHOLE or CARRY_MODE: unlinks a carried body (also when the carrying arm goes limp),
  stops movement, puts the weapon away (slot 90), loads RACE `male ragdoll` / `female ragdoll`, then
  AnimationClass slot 10 (`FUN_1405b9a10`; Human `FUN_1405ba050` also records the arm bits).
  Notify slot 132: humans holding a weapon in a limp arm put it away (`FUN_1405cc230`).
- RACE `male carry ragdoll` / `female carry ragdoll` are **Verified** unused: no string reference in
  the exe. RACE `carry bone` is read by `FUN_1407d3850` (falls back to the skeleton root).
- Animals ragdoll through the same path and fields (`female ragdoll` only for the appearance's
  female flag), but get no partial limb ragdolls.
- Off requests clear bits; at mask 0 the physics ragdoll is destroyed (`FUN_1407d3000`) and the
  animation tracks are re-enabled.

## Triggers

All callers of `FUN_1405cbd60` / `FUN_1405ce9c0` / `FUN_1405d2d20` (part arguments **Verified** in
the disassembly where the decompile hides them):

| Trigger | Request | Where |
|---|---|---|
| Death | on WHOLE unless carried | `FUN_1407a6200`; dead-body tick `CharacterHuman::vfunc_29` re-requests while not ragdolled |
| Death while down | on WHOLE, or CARRY_MODE if carried | `CharacterAnimal::vfunc_64` |
| Knockout, bleed-out (blood ≤ 0 / −25) | on WHOLE | damage `FUN_14064f300` |
| Medical tick while unconscious (`+0x161`) | on WHOLE, or CARRY_MODE if carried | `FUN_140649320` |
| Limb below its collapse level | on with the part's `collapse part` bits, humans only, not for a robotic limb on that side | `FUN_140649870` |
| Limb recovered | off for arm/leg bits no longer collapsed | `FUN_140649320` |
| Picked up | on CARRY_MODE (replaces a queued WHOLE) | `FUN_1405ce9c0(1)` |
| Put down / dropped | off CARRY_MODE; the medical tick then re-requests WHOLE | `FUN_1405ce9c0(0)` |
| Get up | off WHOLE unless carried | `Task_GetUp::vfunc_1` → `FUN_1405c69f0` |
| Furniture use, entity rebuild, destruction | release all | `FUN_140330600`, `FUN_1405b93a0`, `FUN_140799af0` |

No path from hits, knockback, stagger or explosions: a conscious character is never thrown
([combat](combat.md): knockback is root motion). Animals play `VO_Creature_Die` on a WHOLE request.

### Forced crawl

**Observed** (decompile). Posture (slot 109/110, field `+0xe0`, setter `FUN_1405c7e20`; KenshiLib 1ce4a16 `ProneState`):
0 `PS_NORMAL`, 1 `PS_STAYING_LOW` (voluntary crawl, [below](#staying-low)), 2 `PS_CRIPPLED`, 3 `PS_PLAYING_DEAD`, 4 `PS_KO`.
The character tick `FUN_1405d00b0` resets 2 to 0; the medical tick `FUN_140649320` (while conscious) sets
it back to 2 when **any one** part with `collapse part` 0x10/0x20 tests down, and keeps those leg ragdoll
bits on. So it lasts exactly while a leg is down; it does not knock out (the cut-off that takes a leg does: [character-stats](character-stats.md#cut-off-and-leg-loss)).
- Leg test `FUN_140644450`: down when a stump or crushed (limb state 1/3), when h − s < −(base × mult ×
  scale) − wear (severed), or when h − s + b (bandage, `+0x48`; not the rig `+0x4c`) is under the KO point
  `FUN_140643ae0` (−10..−85 by toughness), not under 0. Animals (slot `0x248`, `asAnimal`) never collapse.
  Between 0 and the KO point a leg only limps (leg factor, [speed](character-stats.md)).
- Posture ≠ 0 picks category 5 GROUNDED lower clips (`AnimHuman_LowerCombat` `FUN_14051fc60`); base data
  (rebirth.mod): `crawling move`, `crawl idle up` (any combat mode), `crawl idle down` (out of combat), `crawl use`.
- Defence techniques must have `is prone` equal to posture ≠ 0 (`FUN_140887970`): a crawler has no block or
  dodge (no prone one exists). The attack pick (`FUN_1408876d0`) applies the same test (**Observed**): a crawler
  attacks only with `ground attack L/R`. Nothing in the fight code ends a crawler's fight.
- Speed (`FUN_140886460`, **Verified**): a final override after the water and sneak caps, 5.0 crippled, 7.0 at posture 1. The crippled flag
  (medical +0x160) is set only by the medical tick, so nobody is crippled before their first one (Meitou: `Speed.Crippled`, `MedicalState.Crawling`).
- The get-up task (65, `FUN_14033ca10` / `FUN_140334a20`, **Verified**) of a character with posture > 0 ends at
  once and sets posture 1 (voluntary crawl), which the medical tick does not turn back into 2. The planner's
  `isTargetDown` (`FUN_14099a5a0`) counts posture ≥ 2 as down, `iAmProne` is posture ≠ 0.
- Meitou: `HealthPart.LegDown`, `MedicalState.Crawling` (KO point cached each medical tick), `AnimationSystem.Crawl`
  (engine choices: `crawl use` replaces the medic clip; crawl legs weighed at no less than −100). The ×0.5
  sight factor for a crawler ([perception](perception.md)) is not applied to crawlers who are not sneaking.

### Staying low

**Observed** (character tick `FUN_1405d00b0`, `Character::setStandingOrder` `FUN_1405caa50`,
`Character::ImStealingDoYouNotice` `FUN_140794810`, run speed `FUN_140886460`; field names KenshiLib 1ce4a16):
- Set only on getting up: while totally ragdolled (`AnimationClass::isTotallyRagdoll`, WHOLE) and not carried, a posture
  3/4 character whose AI can get up becomes 3 when `Character::isItSafeToGetUp` fails, else 1 when sneaking (`+0xd4`),
  else 0. `playerWantsMeToGetUp` (`+0xec`, set for each selected character by every player order, `FUN_1407f9e20`,
  cleared each tick) turns that into 0 (1 only from 3 while sneaking). The exe tooltip says the same: a sneaker who wakes
  stays down and crawls. No other setter of 1 was found.
- Standing (not WHOLE): crippled (vfunc 0x360) gives 2; otherwise 1 is kept, anything else becomes 0, and 1 becomes 0
  too while `_isLiterallyUnderMeleeAttackRightNowForSure` (`+0x2b0`). So the forced crawl ends a voluntary one.
- Standing orders: run, jog, walk, group speed (0, 1, 2, 16) on a posture-1 uncrippled character set 0 instead; stealth
  off (4) sets 0 and ends sneaking. A theft witness (`ImStealingDoYouNotice`) in posture 1 or 3 gets 0.
- Effects are those of posture ≠ 0: grounded clips, prone techniques only, stealth ×2, sight ×0.5, target-score terms; run
  speed S = 7 (5 with a leg down, [pathfinding](pathfinding.md#speed-and-orders)).
- Stand Up (order 28, also shown for 65): `Character::addOrder` queues AI task 28 "Standing", built as `Task_UnCrouch`
  ([ai-tasks](ai-tasks.md)): it plays `squat T` backwards and sets posture 0 once the clip is under 0.1. While getting up the
  tick queues task 52 `GET_UP_STAY_DOWN_THOUGH` (posture 1/2) or 65 `GET_UP_STAND_UP` (posture 0).
- The posture is not saved: neither the character writer (`FUN_1406280a0`) nor the medical writer (`FUN_14064ea50`) has a
  key for it; the health panel (`MedicalSystem::getMedicalGUIData`) shows "Staying low" for 1 and "Playing dead..." for 3.
- Meitou: `MedicalState.StayingLow` (not saved), `MedicalState.Prone` (posture ≠ 0) read wherever posture ≠ 0 is; set on
  waking in `CombatSystem.Act`, cleared by a leg down, a speed order, sneak off, Stand Up (`StandUpCommand`, at once: no
  `squat T`), the melee alert (`Fighting`, `AI::underMeleeAttack`), a caught thief's watcher (`CrimeSystem.Steal`) and a
  player order to a selected character still getting up (`PlayerSystem.WantsToGetUp`, the orders that name characters).

### Playing dead

**Observed** (`FUN_1405d00b0`, `Character::isItSafeToGetUp` `FUN_1405ca450`, perception update `FUN_1408580d0`;
KenshiLib 1ce4a16 names): posture 3 is chosen on getting up when `isItSafeToGetUp` fails, and kept while it does (the test
runs each tick while WHOLE). Safe: the character's `SensoryData::numEnemies` (`+0x94`) is 0, or its state broadcast's slave
state is 1 (a slave), or `numConsciousAllies` (`+0x4c`) is above 0 (sensed allies in posture 0, not unconscious or dead). A
player order gets it up (1 when sneaking), as does a thief's watcher noticing.
- Counts (`FUN_1408593d0`, per sensed character): `numEnemies` (`+0x94`) and `numNeutrals` (`+0x98`) skip the unconscious
  (vfunc 0x30), the dead (`+0x5bc`) and the carried (`+0x3d4`), so downed enemies do not count; allies count in
  `FUN_1408580d0` (above).
- Toughness: the Toughness tooltip lists "Get up and fight instead of playing dead" under **Ways To Train**, not as an
  effect. `Task_GetUp`'s start (`FUN_14033ca10`) with posture 0 and `playerWantsMeToGetUp` calls `FUN_1408c6780`: no
  enemy or neutral sensed: dialogue event 0x17; else with no conscious ally sensed: toughness gains
  core(f x (`numEnemies` + 10) x 0.075 x race[toughness]) ([character-stats](character-stats.md)) and event 0x19; else
  event 0x18. Without the order the get-up only says 0x17 / 0x18.
- Meitou: `MedicalState.PlayingDead` (not saved): a knockout that ends while `MedicalContext.PlayDead` is set keeps the
  character `Unconscious` (so everyone treats it as down) with no timer. The get-up line (`DialogueSystem.SeeGettingUp`) is 23 / 25 / 24 by these counts. `BodySystem.Schedule` sets it serially from the last
  tick by `BodySystem.Senses` (enemies `CombatSystem.IsEnemyOf`, by distance within the vision range, 500 inside); a player
  order standing it up grants the toughness (`BodySystem.GotUpByOrder`); animals never play dead. Rechecked every 15 ticks
  (engine choice). `MedicalState.StandUp` ends it. Health header: `HudText.HealthWord`.

## Bodies: kinematic or simulated

The ragdoll (`FUN_1407e4ed0`, `ScytheRagdollPhysicsT`) is created at the scene node position
(character position if zero/invalid; for WHOLE outdoors snapped to terrain height when below it or
over 500 above), scaled by character slot 114 (1.0 for humans, age-lerped size for animals).
Each new body (`ScytheRagdollPhysicsT::vfunc_8` `FUN_1407e2ae0`): kinematic, FILTER_SLEEP_VEL,
linear and angular damping 0.5, collision on. `AnimationClass::vfunc_11` (`FUN_1405b8720`) then
marks each bone **pinned** (kinematic, follows animation) or **simulated** and calls
`FUN_1407d1d20(ragdoll, 1, pinned, bone)` (**Verified** argument order in the disassembly):

| Mask | Simulated bones | Pinned |
|---|---|---|
| WHOLE | all | none |
| LEFT_ARM / RIGHT_ARM | that UpperArm, Forearm | Clavicle and the rest |
| LEFT_LEG / RIGHT_LEG | that Thigh, Calf | Foot and the rest |
| HEAD | Head, Neck | the rest |
| CARRY_MODE, human | both Forearms, Head | the rest (animated carried pose) |
| CARRY_MODE, animal | all but the carry bone | carry bone |

**Verified** (decompile of `FUN_1405b8720`, the `+0x5a` carry flag branch): a carried human keeps its animated "carry me" pose and only the two forearms and the head are simulated; every other bone,
legs included, is pinned to the animation. The carried body's scene node is **not** moved to a ragdoll body (`FUN_1407d3c00` takes the root-body position only for WHOLE or a carried animal): the
carry placement of [carry-loot](carry-loot.md) sets it each frame. The pick-up attach (`FUN_1405ced90`) clears the active mask (`+0x2f8` = 0) and the CARRY_MODE request then rebuilds the ragdoll from the
animated pose; put-down / drop (`FUN_1405ce9c0(0)`) requests CARRY_MODE off, the medical tick requests WHOLE again for a still-unconscious body. Meitou: `RagdollSystem` (`Simulates`, `Rebuild`) does the
same; the limbs that swing are the forearms and the head, so a body's upper arms and legs follow only the `carry me` clip (`play speed` 0.006: almost still).

Activating a simulated body: clear kinematic, ENERGY_SLEEP_TEST, linear damping 0.05, angular 0.01,
sleep energy threshold 5.0, wake 0.4 if asleep. No joint motors or springs are set: a "powered"
pose exists only as pinned kinematic bones.

### Limits of motion and joint springs

- **Verified** (`FUN_1401a8be0` body desc fill, constant read): a body's `maxAngularVelocity` is -1 (the SDK global), and `FUN_1407dbee0` sets only the SDK parameters 1, 2, 3, 5, 6 (skin width, the two
  sleep thresholds, the two friction scalings), never `NX_MAX_ANGULAR_VELOCITY`; so every ragdoll body turns at PhysX 2.8's default global cap of 7 rad/s (**Observed**: the default's value is from the SDK
  header, not from the binary). Meitou: `RagdollPhysics.MaxSpin` 7 (was 30, an engine choice): a swinging forearm or head cannot whip.
- **Verified** (`FUN_1401a81b0`, the revolute branch): spring 1 enabled sets the revolute spring flag with the file's spring, damper and target (degrees); `hu_m_ragdoll_5` has one, on Head -> Neck
  (10000, damper 0, target 0, [phs](../formats/phs.md)). The head is therefore held at the neck's pose by a stiff undamped spring (about 20 Hz on a 1-unit sphere of mass 1) and sags only a hundredth of a radian under its
  weight; it does not flop to its limits. Meitou: `RagdollSystem.HingeSpring` (a Bepu `TwistServo` of the same frequency and damping ratio on the moving inertia).
- **Verified** pinned bodies (`FUN_1407d1550` -> `FUN_1401a9ea0`, `FUN_1401a9f00`): a pinned body gets position and orientation **set** (separate setters, not a kinematic move) and its linear and angular velocity zeroed,
  every frame; a joint to a simulated limb therefore sees only the position error of the jump, never the pinned body's speed. Meitou: `RagdollSystem.Pin` does the same (**Observed** in the sandbox carry scene: the joints stay stable, the forearms swing slightly more after the lift than with the velocity drive it replaced).

## Initial state

`FUN_1407d1800` places every body at its bone's current animated world pose and gives simulated
bodies zero velocity. The setter passes the character's movement velocity (Y zeroed, dropped if
|v|² > 100), but slot 10 never uses it (**Verified**: its register is reused after the clamp). No
hit impulse exists. `FUN_1407d2d10` (per-limb speed caps) and `FUN_1407d2290` have no callers.

## Per frame

Driver `FUN_1405b8180` (AnimationClass), update `FUN_1407d3c00`:

- Root: for WHOLE (or a carried animal) the scene node is set to the root body's position
  (`FUN_1407d1490`: the body named at RagdollAnimation `+0x48`, else the first simulated one);
  otherwise the node keeps the movement position.
- Bones (`FUN_1407d3a30`): a simulated body writes its orientation into its manually controlled Ogre
  bone (plus position for a bone without parent); a pinned body is moved to the animated pose.
- Carried animal: the carry-bone body is moved kinematically to the carrier's attach bone.
- Partial (non-WHOLE) ragdolls are woken every frame (wake 20) and never freeze.
- Water (only when byte `+0x79` of the character's race data is set): when the root is at or below
  y 100 (water level), each body below it gets an upward
  acceleration 98.1 + ln(100 − y + 1), and the freeze timer runs at half speed. Meitou: `RagdollSystem.Buoy` (each body under the level; the original's "below it" read as below the water level).
- Character simulation position `+0x48` = root body position while ragdolled or carried
  (`CharacterAnimal::vfunc_28` via `FUN_1407d16f0`): looting, carrying and AI distances use it.

## Sleep and limits

Freeze = every body put to sleep and updates skipped (`FUN_1407d1f70`), WHOLE only:
- after 2 s of ragdoll, within the next 5 s when ≥ 60% of bodies sleep; after 5 s a downward ray
  from 20 above the node against groups 2, 9–12, 15–22 (mask 0x7f9e04) decides (`FUN_1407d2440`);
- at once when the terrain tile under it is not loaded (`FUN_1405b8180`).

The frozen flag clears when a body wakes (`FUN_1407d2050`). `FUN_1407d4270` (reset timers, wake all
with 0.4) runs when bone flags are applied and when a frozen ragdoll is carried. A root
more than 200 below the terrain is lifted to it and frozen (`FUN_1407d4470`). Global flag
`DAT_1421349c9` (the paused flag, [game-loop](game-loop.md#speed-and-pause)) disables freezing and buoyancy. No distance LOD or ragdoll count cap was found
(`DAT_142133080` is a live-actor counter only).

## Getting up

`Task_GetUp` (`FUN_14033ca10` start, `FUN_140334a20` update):
- Clip: humans `standing up 3`; `crawl idle down` with leg state 1–2; `swim idle` in deep water
  (`FUN_1405c7fd0` level 3); animals their state-matching idle (`AnimationClass::vfunc_25`). No
  face-up/face-down variant.
- `FUN_1407d36e0` → `FUN_1407d3130`: heading = root→`Bip01 Head` direction flattened to the ground
  (rotation from +Z); each bone blends from its ragdoll pose to the clip's first key over 0.75 s
  (`FUN_1407d28e0`: nlerp rotation, lerp translation), then the bones return to animation.
- The task ends when the clip passes 0.86, or at once when crawling.

## Saves

No ragdoll key or pose is saved. After loading, the medical tick and the dead-body tick re-request
WHOLE from the loaded animated pose.

Meitou adds GAMESTATE_CHARACTER string `meitou ragdoll` for a whole-body (not carried) ragdoll: `frozen age n`, then per body (definition order)
position x y z and rotation x y z w, invariant floats; absent otherwise (a world without ragdolls keeps the loaded key); the original ignores it. **Observed** (headless, real data, a knocked-out
human frozen after 61 ticks): save → load gives the same 13 body poses (offset 0), frozen flag and age; without the key it falls again from the clip.

## Unknowns

- Meaning of groups 0, 1, 2, 7, 28–31 and the terrain's group.
- Exact condition of character slot 103 (group 4) and the downward-ray branch of `FUN_1407d2440`.
- Which body is the root (RagdollAnimation `+0x48` key); likely the pelvis.
- `FUN_1405ce9c0`'s branch for Character `+0x388` 1 / 0x5b (calls `FUN_1405b4fa0` on put-down).
- Purpose of the linked Havok libraries.
- Which LOCATIONAL_DAMAGE parts set HEAD in `collapse part` in the shipped data.
