# Carrying, rescue and looting

Original Task_Lift/PutDown/PutIn, carrying/medical flags, inventory transfer and witness research.
Fields/source addresses and unresolved cases retained below. Runtime: [simulation](../simulation.md#carrying-rescue-and-looting).
Crime prices/ledger: [economy](economy.md); body/load: [stats](character-stats.md).

## Carry and release

**Verified** carriable race gate (53-race probe), left-arm usable medical+0x166, left shoulder/
Bip01 L UpperArm attachment. A collapsed, missing or crushed left arm prevents carrying ([arms](combat.md#arms)); right arm irrelevant.
**Observed** target must resolve character and differ from carrier; already-carried targets
require permitted snatch/partner check. AI down-state gate and player context-menu eligibility
are different: the menu offers Pick Up on a standing own character ([ui-input](ui-input.md#context-menu-verbs), **Observed**). Race carry-bone affects attachment; secondary character type unknown.

**Observed** placement each frame (`CharacterAnimal::vfunc_77` `FUN_1405cda20`, `FUN_1405b5980`, `FUN_1405b1a30`): the body's
*node* (not its root bone) gets position `carrierPos + carrierRot·boneModelPos + boneWorldRot·O` and rotation
`boneWorldRot · q(w .70710677, x .70710677) · q(w .99254614, z .121869296)` (90° about x, ~14° about z); the `carry me` clip plays inside
it. Bone = `AnimationClass+0x1b0` (`attachRootToMastersBone`, KenshiLib 1ce4a16), set by the attach `FUN_1405ced90`. `O` lerps by
`t = 2·characterHeight_0to1` from (-1.4,0,1) to (-1.9,-1,2.6) (t<1), then to (-3,-0.4,5); `characterHeight_0to1 = (Height − 80)·0.025`,
0.5 by default (Appearance+0x18c, `FUN_1405334d0`). Gated by `Character+0x1a9`, `AnimationClass+0x1c0` and vfunc 0x248 = 0 (meaning
**Unknown**); otherwise the body is just put at the carrier's position.

Lift sequence/clip/approach, reciprocal partner handles, weapon/ragdoll/state changes and unlink
must stay ordered. **Verified** carry load is CONSTANTS30, not target's weight/race/inventory;
mode0 raises to1 without a separate speed-product carry factor. Dropping on arm loss/KO/death,
put-down and bed/cage placement are different state transitions. Bed/cage occupancy and release
must maintain both character and object links. Rescue target finders differ for squad/player/
NPC/leader and available safe beds; do not turn every pickup into kidnapping.

### A carried body's animation

**Verified** (`FUN_1405ced90`, `FUN_140520500`, `FUN_14051fc60`, `FUN_1405203b0`; writers of the animation category `+0xf0` found: 0, 4 swimming, 5 grounded, never 3): the body's side of the attach is made for every carried
body, **awake or not** (no consciousness test): it sets `+0x3d4`, clears the bed/restraint state, stops the layers (`FUN_14051c8d0`), and plays the record named `carry me` by name (`FUN_14051d510`, the character's
special action `AnimationClass +0x228`). The per-frame update `FUN_140520500` with that action set plays it alone (`FUN_1405203b0` by name each frame) and skips the movement, idle and combat picks, so
a conscious carried character holds the same `carry me` pose as a knocked-out one; category 3 (CARRIED) is only the record's own tag (the only record with `being carried`). Meitou: `AnimationSystem.Update` plays
`carry me` for any carried body (it did so only for a downed one before, so an awake carried mate fell through to the standing picks, none of which fits `being carried`, and showed the binding pose), and
a carried body plays no technique, stumble, lift or knock-out clip. Awake carrying exists for the player's standing squad mates (`CarrySystem.AwakeMate`).

**Observed** (other carrier / body pairs; a Shek body on the human leader, the squad's carried mates): the placement formula above takes only the body's `Height` slider
(0.5 where the race has no body file) and the carrier's attach bone, so a body of another race hangs by the same rule; no difference from it was found. The upper-body movement pick enforces the carrying flags on its
candidates (`FUN_14051d660` flag argument 1 from `FUN_14051d8e0` for the upper list), so a carrier never plays the `limp L/R upper` clips (no `carrying left`): only `limp * lower` (lower picks skip the test) and
`walk/jog carrying left` blend. The `hand2*` records of `gamedata.quack` (`hand2shoulder`, `hand2leftshoulder`, `hand2chest`, `hand2ribs`, `hand2stomach`, `hand2head`; layer overlay, not idle, `uses left/right arm`,
`override L arm`, left leg 0..100) hold a hand to a body part; they are the injury holds ([animation](../animation.md#injury-holds)); a carrier plays only its carrying arm overlay, never a hold.

### Lift and put-down timing

**Verified** (decompile; constants read from the binary). The lift task starts (`FUN_14034f990`) by playing the whole-body clip `shoulder lift <side>` on the carrier at
rate +1.0 (`FUN_1405203b0`; a clip plays at its record's `play speed`, which is **2** for `shoulder lift left/right`: the lift takes length / 2). Its update (`FUN_140354ff0`)
reads the clip position as a fraction (`FUN_1405b4460`, state `+0x54`): once past **0.4** and not yet linked it makes the link (`FUN_14034fbe0`: `FUN_1405cff90` links and calls the attach
`FUN_1405ced90`, the body plays `shoulder lift B` and the pick-up sound); the task is done at **0.95**. A link made at 0.4 persists whatever happens to the task.
The put-down starts (`FUN_14034a230`) by playing the same clip at rate **-1.0** and its update (`FUN_1403504f0`) releases the body when the position falls below **0.2**.
The put-down's **end handler** (`FUN_1403509f0`) always releases the body (`FUN_1405ce1e0`, ragdoll argument 1), stops both lift clips, and when the carrier does not regard the body as friendly
applies the put-down KO (`FUN_140644980`, 0.01 x the carrier's assassination clamped 0.1..0.4): so a put-down ended early (a hit outside a fight ends the current task, [combat](combat.md))
is finished at once, never restarted; a lift cut off before 0.4 plans again from its start. Meitou: `CarrySystem.LiftPlaySpeed` (the record's play speed), `CarrySystem.Interrupted`.

**Verified** placement (decompile `FUN_1405cda20`, `FUN_1405b5980`, `FUN_1405b1a30`, constants read): node position = carrier node position + carrier rotation x the attach bone's model-space position
(`FUN_1405b1ec0`) + the bone's world rotation x O; rotation = bone world rotation x 90 degrees about x x 14 degrees about z; O lerps by t = 2 x (Height - 80) x 0.025 from (-1.4, 0, 1) through (-1.9, -1, 2.6)
to (-3, -0.4, 5), not clamped (Meitou `CarryPose` matches). The body's node is its feet origin, so a carried body's head hangs down in front of the carrier and its hips ride on the shoulder.
Meitou draws the body on the carrier's **drawn** facing and the attach interpolated between ticks (`GameHost.CarrierYaw`, `MixCarry`). Measured (carry scene, a standing carrier): shoulder bone y 14.5, node y 17 to 20,
carried pelvis 8 to 16, head 14 to 16 above the carrier's feet; a crawling carrier's shoulder is at 3 to 5 and so is its body.

### A carrier in a fight

**Verified** in `kenshi_x64.exe`: no attack goal's conditions test carrying (goals 4, 5, 16, 61, 227 in the task table; only Assault 101, Attacking Squad 248 and the static guards test `!IS_CARRYING_SOMETHING`),
and the only combat-side read of the carrying flag (`+0x348`) is the steering update `FUN_140660dc0`, which lists the carried body with its carrier. The protect-allies desire
(`FUN_140599b90`, goals 15 / 288) is x0.5 while carrying and 0 in stealth. So a carrier retaliates and fights on its own with the body on its shoulder; weapon choice reads only the
medical arm bytes (not the carrying flag), and the carrying arm stays on the body because the carrying arm overlay plays every frame ([animation](../animation.md#a-carrier)). Other `+0x348` readers found in the
exe: squad-level "someone carries" tests (`FUN_140288630`, `FUN_14028fdf0`, a crowd-control count), the walk-to-run rule (`FUN_140342a30`: a carrier walking runs, Meitou `MovementSystem`), the lift and
put-down tasks, the FIND_BED desire and the Watching-slaves desire. **Verified** records: `carrying left arm only` (overlay, `override L arm`, every other bone deleted), `stand carrying left` (overlay, `override L arm`),
`walk/jog carrying left` (upper, `override L arm`), `sword ready (carry)` (upper idle, carrying left, one weapon in hand).

## Theft and witnesses

**Observed** pending crime stores code, victim faction/handle, timer and booked state. Own/lawless
factions and police/prisoner exemptions gate creation. Loot permission for downed targets checks
slave/prisoner/bounty/bandit/police/diplomat/own-property branches, not just consciousness.
**Verified** transfer callsites disagree: right-click conscious→Theft3, unpermitted down→Looting10;
drag-drop unpermitted down-character→Theft3, other source→Looting10. Ordinary loot UI path unknown.
Ground pickup can raise Theft inside another faction's building, and taking from another faction's container does (Theft by right-click, Looting by drag).

**Observed** steal roll (`FUN_140794810`, chance `FUN_1407944d0`; virtual names KenshiLib 1ce4a16): both inventory transfers above and the give
set their pending crime, then roll; caught refuses the move. Character owner (Loot, 26, on a sleeper, a playing-dead or a caged
character: [ui-input](ui-input.md#context-menu-verbs)): the owner watches, p = 1.1 x thievery/100, an equipped item (`Item::isEquipped` +0x129)
x0.75, x0.75 again when it is armour (`isArmour`); the drag passes no item. Container: the container's parent building (`isFurnitureOf` -> its
building) must have a resident squad (`Building::residentSquad` +0xD0, a CHARACTER or PLATOON hand), else no watcher; the watcher is the nearest
to the thief of that platoon's active members (`Platoon::activePlatoon` +0x1D8) not down and posture < 3, those on the thief's floor (`getFloor`)
first (`FUN_140792ce0`); t = lerp(0.5, 1, clamp(d/200)), d watcher to container; when the
watcher's `isIndoors` building differs from the container's (`hand::operator!=`), t x0.5 and, if that building has no open door
(`Building::hasAnOpenDoor`), the steal always succeeds; p = 0.3 + 1.95 x thievery/100 x t; posture-3 watcher x2, another floor x1.5. No watcher, or one unconscious, dead, carried or imprisoned (or a down thief): p = 1, the
pending crime stays. Caught (`rand > p`): a watcher in bed says event 47 instead half the time (`rand > 0.5`); else it stands up, says event 8, the
thief gets Theft timer 30 against the owner's faction and the watcher's squad a long-term STOLE_FROM_ME tag (4, `FUN_140673a20`); no faction
relation event (none of the slot-5 callers in [factions 3.5](factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile) is a theft
or loot path). Thievery XP 2 x 20, caught 2 x 80, none when p >= 0.98, the thief is imprisoned or its `+0x180` is 10 or more after the roll.
`+0x180` is the pending crime's timer (ledger at `+0xF0`, timer `+0x90`, the setter `FUN_140852e10(this + 0xF0, ...)`), so a caught thief
(Theft, 30) gets none unless the setter refused. A playing-dead owner (posture 3) is down for the codes (`isDown`) but not for the roll
(only `+0x5b9`, dead, carried, imprisoned skip it), and a caught one stands up.

Witnesses must be eligible and care about victim for theft/fencing/looting/kidnapping;
assault/murder have separate branches. Ordinary witness reacts; only police-like witness with
faction+0x40 books bounty. Kind7 is a squad-level suspect tag, not a faction relation event.
Friendly/hostile/slave-owner/lawless/bandit faction gates matter. Carrying past witnesses has
its own victim/captor ownership checks.

## Stolen ownership

**Observed** item+0x140=from-owner, +0x160=current holder. Backpack uses its owning character's
faction. Same faction/lawless holder is not stolen; strict1 can use set/nonmatching original-owner
handle even if unresolved, strict0 also requires resolution. Dropping ownerless items can set
dropper as owner. The save's `ownedby` is item+0x140 (**Observed**, `Item` vfunc 75/76); preserve handles, not a boolean only.

### Character fields used here

Offsets on the `Character` object; all **Observed** unless the row says otherwise. The names are ours.

| Offset | Meaning | Evidence |
| --- | --- | --- |
| `+0x348` byte | carries someone (`isCarrying`) | set by the attach `FUN_1405cff90`, cleared by the unlink `FUN_1405ce1e0`, read by the carried-weight function `FUN_1405c89d0`: **Verified**, three functions agree |
| `+0x3d4` byte | is being carried | set by `FUN_1405ced90` (the body's side of the attach), cleared by `FUN_1405ce1e0` / `FUN_1405ccba0`; makes "ragdolled or carried" (`FUN_1407d1440`) true: **Verified** |
| `+0x380` handle (type `+0x388`, ids `+0x38c..+0x398`) | the carry partner: the body for the carrier, the carrier for the body | written on both sides by `FUN_1405cff90` / `FUN_1405ced90`, reset to the null handle (type 0xb) by the unlinks: **Verified** |
| `+0x378` byte | carry side, 0 right shoulder, 1 left | picks the bone `Bip01 R UpperArm` or `Bip01 L UpperArm` in `FUN_1405ced90` |
| `+0x350` string | side suffix of the lift clip name (`shoulder lift left` / `right`) | `Task_LiftSomething` slots 1 to 3 |
| `+0x2e0` | the RACE object; RACE `carriable` is its byte `+0x7a` | the RACE loader `FUN_14042efa0` stores it there and the lift task reads it: **Verified** |
| `+0x2e8` | the inventory | `FUN_1405c89d0` |
| `+0x2f8` int | special state: 0 normal, 1 in a bed (bed handle at `+0x300`, type `+0x308`), 2 imprisoned (cage or restraint) | `FUN_14032e2f0` sets and clears 1; the attach clears it and releases a restraint (`uses_restrains_movement_unique` `FUN_140330600`) when it was 2 |
| `+0x650` -> `+0x318` | the medical object; its byte `+0x165` = right arm usable, `+0x166` = left arm usable | section 1.2 |
| `+0x5bc` byte | dead | set by the death function `FUN_1407a6200` ("{1} is dead.", "{1} has died from blood loss.") |
| `+0x5b9` byte | unconscious (inferred) | no writer found; every reader pairs it with dead as "cannot act" (`FUN_1407944d0`, `FUN_140997c70`, `FUN_1408cdf50`, `isReadyForAction` `FUN_140998550`) |
| vtable slot 6 (`+0x30`), `CharacterAnimal::vfunc_6` `FUN_1405c9e10` | `isDown`: dead, `+0x5b9`, ragdolled or carried, or leg state (slot 109, `+0xe0`) of 3 or more | |

### 3.2 Goals and their target finders

TaskData (`FUN_140319a60`) holds a **target finder** at `+0x160` and a follow-up goal pair at `+0xc` / `+0x10` (**Observed**: the field writes; the
finders resolved from the thunks in the exe's bytes):

| Goal | Desire | Finder | Follow-up |
| --- | --- | --- | --- |
| `FIND_AND_RESCUE` 105 | `140599480` (exe bytes): 0 if CharStats `+0x12b` is set or the character carries, else 1 | `FUN_1408cd550` | - |
| `FIND_AND_RESCUE_IF_THERES_BEDS` 148 | `FUN_1405994c0`: 0 if CharStats `+0x12b` or carrying, else the bed search `FUN_1408cfe00(..., 1)` | `FUN_1408cd550` | 148, then **106** `FIND_BED_AND_PUT_IN` |
| `FIND_AND_RESCUE_LEADER` 142 | - | `FUN_140598540`: the squad leader (`platoon +0xa0`) when it is not the character itself and not dead | - |
| `FIND_BED_AND_PUT_IN` 106 | `FUN_1405a0d50`: 2 while carrying a body the carrier regards as friendly, whose slot `0x248` is null, that (for a police carrier, `FUN_140599600`) has no bounty with the carrier's faction, whose faction is not hostile to the carrier's (`FUN_1406b26d0`) and not in its town's list `FUN_140599390`; else 0 | none | - |
| `CARRY_WOUNDED_SLAVES` 168 | - | `FUN_1408cdf50` | - |
| `FIND_AND_KIDNAP` 132 | - | `FUN_1408cd5c0` (the character finder below, masks 0x4008 / 0x811, beds allowed) | - |
| `FIND_A_DEAD_GUY` 138 / 230 | - | `FUN_1408cfb00` / `FUN_1408ce170` (the nearest dead, ragdolled, uncarried body; `carriable` when asked) | - |
| `LOOT_THE_DEAD` 177 | AI slot 62: 1, 0 while character vfunc `0x368` (crawling or ragdolled) | `FUN_14099e680` (`FUN_14099ae10(ai, 0)`, [NPC looting](#33-npc-looting)) | - |
| `LOOT_FOOD_AND_STUFF` 131, `AQUIRE_FOOD_AT_HOMEBASE` 243 | - | `FUN_1408cb650`: a building, `BuildingFinder_LootFoodAndStuff` over a town (from the AI's +0xf0, else +0x18 → +0x88, **Unknown**; no characters): buildings with an inventory holding food (filter partly read, **Observed**) | 243's effect `gotAFoodItem` comes from 244 `Task_grabOneFood`, which takes one food item of the building's inventory |
| `LOOT_STOLEN_GOODS` 212 | - | `FUN_1408cf6a0` (calls the stolen predicate) | - |
| `PICKUP_INTRUDERS` 223 / 273 | - | `FUN_14099c110` / `FUN_14099c250` | - |

**Character finder** (`FUN_1408cd2d0(task, out, include, exclude, allowBed)`, **Verified** by decompile; rescue 105/148 call it with masks 1 / 0x4010 and `allowBed` 0,
kidnap 132 with 0x4008 / 0x811 and `allowBed` 1): walks the owner's sensed list, keeping entries whose category word has a bit of `include` and none of `exclude`
(the bits are **Unknown**); the nearest (squared XZ distance) passing all of these wins: the target is down (ragdolled or carried, `FUN_1407d1440`, or the byte
`+0x5b8`) or, only when `allowBed`, in a bed (`+0x2f8 == 1`); not carried (`+0x3d4`), not dead, special state 0 (not caged; bed 1 when allowed), not an animal
(slot `0x248` is `asAnimal`), and not in the squad's give-up list (`FUN_1402690e0`, a handle-pair list every finder tests; its writer was not traced). Rescue also requires
the target to share the owner's slot-11 object (probably its faction; **Observed**: tested when `include` has bit 1 and a global flag at `+0x112` (Rescue Allies, [squad AI options](ai-tasks.md#squad-ai-options)) is clear). So raiders also take sleepers out of beds. 132 is a
planner goal (A: 153 got a victim, B: not carrying, effect 48 carrying); 254 has no finder: its B list 153, 154 chains 132, then 112/281 find a cage (`FUN_1408d0770`) and 108 puts the
carried one in. What sets `Status+0xe9` ("innocent victim") was not found: it is saved with the slave state (`FUN_140626580` / `FUN_140626830`), cleared by the per-update
`FUN_140852a30` (not carried and in its town's bed, or carried by a friend) and read at the lift's release (`FUN_1405ce1e0`) and by `FUN_1406cc470` / `FUN_1407211b0`;
it is not part of target eligibility.

### 3.3 NPC looting

**Observed** (decompile of `FUN_14099ae10`, `FUN_14059a7b0`, `Task_Loot_AI` start `FUN_140355ad0` and update `FUN_14034d930`, the take `FUN_140342de0`; registrations in
`FUN_140319a60`). The goal that loots downed characters is **177** "Looting the dead": the data's goal "Looting corpses and food" (`5196-gamedata.base`, enum 177) in
"Patrol map (short range) LOOTING" and the Starving Bandits' packages that inherit it. 131 "Looting food" and 243 "Getting food" find buildings, not bodies. 177's effect is
`isSubjectInInventory` (3), provided by row 3 `PICKUP` "Collecting item" (`Task_Loot_AI`, B list `isItemLootable`, `isAtLocation`): the plan walks to the target, then loots it.

- **Finder** (`FUN_14099ae10(ai, animalsOnly)`; 177 passes 0). Two passes over the sensed list, each target within 100 units (squared 10000), not carried (`+0x3d4`),
  reachable (`AI::isDestinationAccessible`, `destinationNotBlockedByFortifications`), not in the squad's give-up list, and not short-term tagged 6 by the looter's squad
  (`FUN_140678960(c, target, 6)`, [squad memory](dialogue.md#squad-memory)); the first match wins (sensed-list order, **Unknown**). Pass 1: mask 0x10 / 0 (read as the dead). Pass 2: masks 0x4008 / 0x11 (the kidnap finder's,
  read as other factions' downed) and also: the target's faction is not the player's (`FACTION +0x250`) unless the global **Bandits loot the player** (`142133598`) is set, and
  `shouldIScrewThisGuyOver(looter, target)` (`FUN_14059a7b0`).
- **shouldIScrewThisGuyOver**, in order (later lines override): true when either is of NPC class 8 (Bandit; status `+0xdc`); false when both have the same class; true when the
  looter is class 2 (Military); false when looter vfunc `0x3f0`(target, 1) holds (**Unknown**); false when the looter is class 1 (Law enforcement); true when the looter's status
  `+0xe0` (a slave's owner faction) is the target's faction; false when the looter's squad has a love tag on it (long & 0xc), true with a hate tag (& 0x2b0); true when the target is dead.
- **Start**: flags `animal` (target vfunc `0x248`), `notPlayerSide` (target's faction is not the player's, or the target is dead; cleared when looter vfunc `0x68` `+0x90` > 0,
  **Unknown**), `foodOnly` (looter of NPC class 8, or its hunger floor, slot 0x348 ([free food](character-stats.md#free-food)), below 2.8), `wantsWeapon` (the looter has no weapon,
  `AI::hasWeapon`, and the target has one). A law-enforcement looter (`Character::isLawEnforcement` `FUN_140599600`: not of a bandit faction, and NPC class 1, or class 2 whose
  status `+0x88` is set and equals `Character::getOwnerships` `+0x30`, meaning **Unknown**; Meitou: `CrimeSystem.IsPoliceLike`) sets a confiscate flag (`+0x82`) and clears `notPlayerSide` and `foodOnly` instead, then skips the crime part (**Confiscation** below). An animal target posts
  "{1} is butchering {2} for meat!!" when the target's faction is the player's.
- **Looting crime** (same start, every other looter; `BountyManager::setCrime` `FUN_140852c80`, **Observed**): code 10 Looting against the target's faction (victim the target)
  when the looter has any bounty (`getActualBounty(ledger, 0)` > 0) or is NPC class 8. Then with `notPlayerSide` or `foodOnly` the looter says event 33 LOOTING_EVERYTHING at the
  target and gets the same Looting unless `Character::isItOkForMeToLoot(looter, target, no item)` (vfunc `0x290`, `FUN_140793940`) allows it; else with `wantsWeapon` it says
  event 32 LOOTING_WEAPON_ONLY, and its Looting test sits behind `AI::hasWeapon(looter)`, which `wantsWeapon` rules out, so it never fires. The setter's own refusals apply
  (police-like culprit, own or lawless victim faction). **isItOkForMeToLoot** with no item: yes for a non-character, a slave or escaped slave (`isSlave` 1 / 2), the looter's
  own slave (`isMySlave`), a target with any bounty or of NPC class 8, a looter of NPC class 1, 2 or 5; otherwise no (the item branch, own-faction property, needs an item).
- **Update**: plays `medic` (start: `squat T`, `medic`); a timer adds the frame time. With neither `foodOnly` nor `notPlayerSide`, past 0.8 s: a `wantsWeapon` looter takes one
  weapon of the target's hip and back sections and is done. Otherwise the take comes past 8 s (an animal target: 20 s):
  1. Weapons (item type 2) of the target's inventory, when the looter has no weapon equipped (`Inventory::hasWeaponEquipped`) or the target is imprisoned; a weapon whose
     slots the looter has all filled is left.
  2. Food: items of type 4 with `item function` 3, then 15, repeated until none moves; then the same from the target's worn backpack (function 3).
  3. With `notPlayerSide` and not `foodOnly`: every item (type 4) and armour (type 3), and every item of the backpack.
  An item goes into the looter's inventory, else its worn backpack, else back. Then the looter's squad tags the target short-term 6 (`FUN_1406744a0`) and the task ends.
- **Confiscation** (the update with the confiscate flag, **Observed**): an animal target waits the 20 s and gets the take above (weapons, food); otherwise past 0.8 s
  `Inventory::getAllStolenItems(target's inventory, includeUnknown 0)` (`FUN_140748bc0`: the inventory's whole item list, then its equipped weapons and armour, each passing
  `InventoryItemBase::isStolen(item, 0)`; the target's own inventory, `Character::getInventory` slot 44 = `+0x2e8`, so not its backpack's contents) and each in turn:
  `removeItemDontDestroy_returnsItem(item, 1, true)` (Inventory slot 5: one unit split off a stack, the whole item when it is the last), `addItem` into the looter's
  inventory; a unit that does not fit goes back and ends the loop. Taken items get `Item::notifyTheftFrom(target)` (slot 106, `FUN_140792800`: calls slot `0x2b0` only when the
  item's owner handle `+0x140` is of type 11 NULL_ITEM, read as "no owner"; a stolen item has one, so nothing changes). Nothing found: the short-term tag 6; something found: no tag. It acts out goal 212 Confiscating goods ([ai-tasks](ai-tasks.md#arrest-and-prison-rows)) and goal 177 for a
  law-enforcement looter. Base data (**Verified**, probe): 212 is listed only by "addon- law enforcement" (and its "- no freeing prisoners" variant), inherited by the police,
  guard-town and police-station packages. Illegal (culture-listed) goods are not part of it: no confiscation of them was found.
- **Bandits loot the player** is the new-game option at block `+0x20`, saved as CAMERA bool `bl` (**Verified**: the reader `FUN_1403eef20` reads key `bl` into `+0x20`, the writer
  `FUN_140912f80` copies `+0x20` to `142133598`; the reader's default is 1, and `bl` is true in the owner's three readable original saves).

## Kidnapping

**Observed** (decompile; names KenshiLib 1ce4a16; literals **Verified**). Verb 246 (the pick-up verb on another faction's character that is
not standing: down, asleep, imprisoned, [ui-input](ui-input.md#context-menu-verbs)) runs `Task_Kidnapping`, a `Task_LiftSomething` whose
arrival (vfunc 1, `FUN_14034ffa0`) first rolls:
- The victim's squad tags the kidnapper TEMP ENEMY and the kidnapper gets a pending Kidnapping (code 15) against the victim's faction.
- **Chance** (`Character::getKidnappingChance`, also the verb's column): 100 for a down victim that is not getting up (+0x278); else
  the stealth KO's formula ([combat](combat.md#stealth-knock-out)) with the victim's effective strength in place of its toughness and no
  sense terms. Roll uniform 0..100; taken when roll <= chance.
- **Taken**: a victim neither dead nor unconscious is knocked out for lerp(2, 300, kidnapper assassination x 0.01) s (raw stat); assassination
  XP 100; then the lift.
- **Failed**: the victim perceives the kidnapper (the kidnapper's status `+0x110` = 250, the recently-wanted marker); its town's alarm is raised at the
  kidnapper (code 15, [town alarms](economy.md#town-alarms)); a sleeper (state 1) gets order 116 Getting out of bed; its squad tags the
  kidnapper TEMP ENEMY and long-term 1 (intruder) and says event 8 to it; assassination XP 300; the task ends; a sleeper wakes (state 0) and
  goes for the kidnapper (`Character::attackTarget`).

Meitou (`CarrySystem.Kidnap.cs`, `PickUpOrder.Kidnap`): as above; asleep is lying in a bed or resting with the sleep behaviour; the
perception mark is not modelled; `+0x110` is `CharacterCold.RecentlyWanted`.

### Escaped slaves lie low

The status object's `+0x8` is a double of game hours (day x 24 + hour), saved as the float `ssct`. The slave state setter stamps it with the world clock on any change to a state above 1.

A per-character status update lets a state above 1 run down from that stamp. With `elapsed` = now - `+0x8` in game hours:
- **elapsed below 0:** the state becomes 0 at once, with the owner kept.
- **State 3 and elapsed / 24 above 4.0:** the state becomes 0 and the owner is cleared.
- **State 2 and elapsed above 12.0:** the state becomes 3 and `+0x8` is stamped again.

These writes are direct, not through the setter. State 1 never takes this path. The update's cadence is **Unknown**.

**Verified** by disassembly of a Kenshi 1.0.65 `kenshi_x64.exe`, sha256 `504b362c…cd3e`. In that build, the setter is `FUN_1405a3eb0`, the rule is `FUN_1405a44c0`, and its only caller is the vtable-reached `FUN_1405a4750`. Its constants are 24.0, 4.0 and 12.0. The other addresses in this file come from another build.

Loading can free an escaped slave at once, because the clock is restored to the minute while the stamp is not. **Observed** with a "The Slaves" save written right after New Game:
- the save holds `ssct` 34.00063 and the clock day 1 10:00, which is 34.0 h;
- both escaped player characters were free within 120 calendar seconds of the load, in 5 runs out of 5, read with the KenshiOracle probe.

What state 3 does elsewhere (**Observed**, the 64 callers of the getter `FUN_1405c8aa0` in 1.0.68): most tests are `== 1`, `== 2` or `!= 0`, so an ex-slave counts as unfree wherever the
test is `!= 0` / `> 0` (a police carrier's cage desire 1.4 `FUN_1405a0f10`, the refugee package `FUN_140288ed0`, slave processing `FUN_1408cd630`, prison hunger 1.15 `FUN_1405c9ca0`) and as
neither slave nor escapee elsewhere. Its own branches: the looks-like-slave level is kept at least 0.01 (`FUN_1405a51e0`, [UI](ui-screens.md)); the status panel shows "Ex-slave"
(`FUN_1405d4260`); a looker that recognises one while the status `+0x110` is above 0 fires event 40 ESCAPED_EX_SLAVE_SPOTTED (`FUN_140859680`) and then sees through it as through an escapee
(`FUN_140856f00`, with the line test 40 in place of 36). Meitou does not model event 40 or that recognition.

Meitou: `FixtureRegistry.LayLow` runs from the crime sweep (engine choice) and also clears the owner on the first path. The stamp is `CharacterCold.SlaveSince`, and `SlaveSave` reads and writes `ssct`.

## Unknowns

1. The bed search (`FUN_1408cfe00`) is decoded for player-faction characters and, in outline, for others ([locks](locks.md#beds)); the meaning of the designated-building kinds is not.
   Where 106 (no target finder) gets its bed is **Unknown**.
2. The **sensed-list category bits** (`FUN_140853960` masks 1, 0x4010, 0x20, 0x210, 0x4008, 0x811, 0x10): which bit is ally, enemy, dead, slave;
   and the size of the sensed list, which is the search range of every finder.
3. The **status object at `Character+0x1a0`** (int at +0, saved as `slavestate`: 1 slave, 2 escaped slave, 3 ex-slave; `+0x8` the [lie-low stamp](#escaped-slaves-lie-low); the setter `FUN_1405a4940` posts "{1} has been enslaved" for a free player-faction character and sets `+0x110` to 250 on 2; its callers: caging, the squad factory's `slaves`, slave processing (task 182), escapes, release and bail); its `+0x88`, `+0xe0` (a slave's owner faction); its class name, and what sets `+0xe9` (the "innocent victim" flag; see the character finder).
   Partly known (**Observed**, [economy 11.4-11.8](economy.md#115-bail-and-sentence)): `+0xe8` escaped-prisoner flag, `+0xea` set on release
   or bail and cleared by a new pending crime, `+0x110` the "recently wanted" marker (250.0).
4. The **writer of `+0x5b9`**, read as "unconscious" from its readers only.
5. **Which inventory transfer the player's usual loot takes** (drag and drop gives Theft for a down owner, the right-click transfer Looting): both
   are real, the UI events behind `FUN_140713d20` and `FUN_140715560` were not mapped.
6. **Witness timing**: how often `FUN_1408580d0` runs per sensed character, with what range and line of sight, and how the pending timer `+0x90`
   counts down (`FUN_140851f40` sets it; the decrement was not found).
7. `PUT_DOWN_CARRIED_DUDE_IF_THEY_CAN_WALK` (169): its "can walk" test. (Kind 4 of `FUN_14038ab50` is the bed's nearest type-4 node the body is placed at, [locks](locks.md#beds).)
8. Bed rent: decoded ([locks](locks.md#beds)); allied factions' beds are free. The town-type and `FUN_140621230` tests in it are **Unknown**.
9. A **despawn timer** for ground items.
10. The steal order type 0x1d of the ALT path (`FUN_1407fc440`): the taskType table says 29 is `MOVE_CUS_ORDERED`.
11. The **speed caps** of the `carrying left/right` movement clips (RACE `male/female carry ragdoll` are unused: [ragdolls](ragdolls.md)).
12. `FUN_14079c600`, called when the player picks up a non-player character, and **`CharStats+0x12b`** (set by slot 97 order kind 0xc), which turns
    both rescue goals off.
13. The put-down KO time: `FUN_140644980` has no roll and only the blood term (**Verified**, [character-stats](character-stats.md#cut-off-and-leg-loss)); Meitou's `PutDownKoSeconds` still uses the general terms (gap). The relation test `FUN_1406b27b0` of "friendly".

## Expression reference

- The attach: `+0x2f8 == 2`; `+0x2f8 = 0`
- Weight and speed: `platoon +0x78 -> +0xb0 != 4`
- Dropping: `RagdollPart { WHOLE=1, RIGHT_ARM=2, LEFT_ARM=4, ..., CARRY_MODE=2048 }`
- The unlink `FUN_1405ce1e0`: `+0x240 = -1`; `+0x3d4 = 0`
- `Task_PutInSomething`: `+0x2f8 = 2`; `+0x20 = 1`; `+0x2f8 = 1`; `+0x2f8 == 1`
- Crimes for taking items: `+0x34 == 8`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- 1.3 Sequence and clips (**Observed**, one function each)
- 1.4 The attach (`FUN_14034fbe0` -> `FUN_1405cff90` -> `FUN_1405ced90`, **Observed**)
- 2.1 The unlink `FUN_1405ce1e0(character, ragdollArg, keepInPlace)` (**Observed**)
- 2.2 `Task_PutDownObject` (`PUT_DOWN_OBJECT` 69, also goal 169) (**Observed**: `FUN_14034a230` start, `FUN_1403504f0` update, `FUN_1403509f0` end)
- 2.3 `Task_PutInSomething` (`PUT_SOMEONE_IN_BED` 99, `PUT_IN_CAGE` 108) (**Observed**: `FUN_140358170`, `FUN_140358670`, `FUN_140358cc0`)
- 4.1 What may be looted (`isItemLootable`, state 0xf, `FUN_14059dcc0`, **Observed**)
- 4.4 The stolen flag (`FUN_14079e550(item, strict)`, **Observed**; answers part of the economy.md Unknown)
- 4.6 Carrying people past witnesses (`FUN_1408549f0`, **Observed**)

## Multiplayer runtime contract

**Observed** (implementation inspection; not acceptance-tested): issuer-specific loot windows bind
actor, body, session generation and the opening control grant. Item transfers require both current
inventory revisions and a matching actor/session. Carry, loot and assassination commands retain
their grants through approach and execution; revocation stops the instruction without removing an
already carried body. Awake teammate pickup requires control of that teammate in explicit mode;
downed victims and kidnapping retain normal gameplay/crime checks. Loot money credits the acting
character's faction; NPC looting preferences come from the victim's controller.

**Observed** (implementation inspection): closing loot matches that player's actor, target and session,
independently of container windows. Corpse disposal binds its selected machine/site, original owner,
site revision, actor grant and membership revision until arrival. Losing these rights cancels the
errand while preserving the carried body; incineration checks that the original carried body is dead.
