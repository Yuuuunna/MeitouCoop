# AI task tables

Task names/IDs: `src/Meitou.Data/Gameplay/Ai/TaskType.cs` (all 288 named rows checked against this research). IDs 289/290 are unnamed in FCS. See [AI](ai.md) for runtime semantics; lock, door, cage and lockpicking task behaviour (76-80, 107, 110, 133, 140, 155, 162, 166/167, 185/186, 195-201, 207, 285/286) is in [locks](locks.md). Tables retain factory mappings and all TaskData/state values; repeated enum names and condition names are represented by IDs.

## taskType to Task class

**Verified** (the Tasker factory `FUN_14032ebd0` switches on a byte table at `140330408` indexed by taskType - 1; the table was read
from the exe bytes with a probe, each case body was matched to the RTTI `Task_*` class its constructor installs, and the resulting
type-to-class list agrees for all 290 rows, including 147 default cases). 290 jump-table entries versus
289 names in `fcs_enums.def`: types 289 and 290 build `Task_Follow` and `Task_BashDoor` and have no enum name (**Unknown** why).
147 types fall through to `Task_Blank` (the factory's default case): no behaviour of their own from this factory.
The per-type record census is omitted; it can be regenerated from merged AI_TASK data.

Factory mappings grouped by identical class; each ID occurs exactly once:

- (goal; Task_Blank): 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23, 24, 30, 32, 33, 34, 37, 38, 39, 41, 42, 43, 45, 46, 47, 48, 49, 50, 54, 55, 56, 57, 58, 59, 63, 66, 70, 84, 85, 86, 88, 89, 90, 91, 94, 101, 104, 105, 106, 109, 111, 112, 113, 114, 115, 117, 118, 120, 122, 125, 126, 131, 132, 135, 137, 138, 139, 142, 143, 144, 145, 148, 149, 151, 152, 153, 154, 155, 157, 158, 160, 167, 168, 169, 172, 173, 177, 178, 179, 183, 184, 187, 188, 189, 190, 191, 192, 193, 194, 195, 197, 202, 203, 204, 205, 206, 211, 212, 214, 217, 218, 222, 223, 230, 232, 233, 234, 237, 238, 239, 242, 243, 245, 248, 250, 251, 252, 254, 255, 259, 260, 265, 268, 270, 271, 273, 275, 276, 277, 279, 281, 282, 283, 288
- Task_AddMaterialsToBuilding: 71
- Task_BashDoor: 81, 123, 226, 272, 287, 290
- Task_BruteForceLock: 286
- Task_BruteForceShackles: 186
- Task_Build: 2, 95
- Task_CloseDoor: 73, 75
- Task_Crouch: 27
- Task_CuffTarget: 166
- Task_CutLock: 285
- Task_CutShackles: 185
- Task_Dismantle: 96
- Task_DogLimbRunningPlay: 278
- Task_EatingCrops: 231
- Task_EatPrisoner: 220
- Task_EmptyMachine: 284
- Task_EmptyMachine / Task_Pickup: 92
- Task_EmptyMachine_BuildMats / Task_Pickup: 247
- Task_EnterBuilding: 156
- Task_EquipBestWeapon: 6, 264
- Task_FeedCorpseIntoMachine: 136
- Task_FillMachine: 93, 124
- Task_FirstAid: 25, 60, 269
- Task_FirstAidRig: 249
- Task_FocusedMeleeAttack: 5, 61
- Task_Follow: 31, 44, 253, 289
- Task_Follow / Task_FollowAndTalk: 164
- Task_FollowLeader: 141, 171
- Task_GetOutOfBed: 116, 208, 257
- Task_GetOutOfCage: 133, 207, 256
- Task_GetSlave: 174
- Task_GetUp: 52, 65
- Task_GoHomeTown: 128
- Task_GoHomeTownFast: 127
- Task_grabOneFood: 244
- Task_HuntingSmell: 176
- Task_InvestigateAlarms: 209, 210
- Task_Kidnapping: 246
- Task_KillPrisoner: 134, 196
- Task_LeaveBuilding: 200
- Task_LiftSomething: 68, 170, 213, 225, 240
- Task_LockDoor: 77, 79, 162, 198
- Task_Loot_AI / Task_Pickup: 3
- Task_Loot_Order: 26
- Task_Loot_ResourcesWeHaveStorageFor: 241
- Task_MakeAnnouncement: 103, 129
- Task_MakeTargetStandStill: 64
- Task_ManTheGate: 180
- Task_MeleeAttack: 4, 227
- Task_Move: 1, 29, 40, 67
- Task_MoveBowRange: 267
- Task_MoveToDoor: 82
- Task_MoveToDoor_CurrentLocation: 83
- Task_MoveToDoor_Gate_ChooseSide: 121
- Task_MoveToDoor_Inside: 163
- Task_MoveToDoor_Outside: 199
- Task_OpenDoor: 72, 74
- Task_OperateMachine: 87, 221
- Task_OperateMachine / Task_UseTurret: 146, 235
- Task_Patrol: 22
- Task_PatrolTown: 36
- Task_PatrolTownViolent: 216
- Task_PickLock: 76
- Task_PickLock_Shackles: 201
- Task_PlayerTalkto: 12
- Task_ProcessNewSlave: 182
- Task_Prospecting: 150
- Task_PutDownObject: 69
- Task_PutInSomething: 99, 108
- Task_RangedAttack: 262, 263, 266
- Task_ReleasePrisoner: 110
- Task_Runaway: 35, 280
- Task_SheatheWeapon: 7
- Task_Shopping: 119
- Task_SleepOnFloor: 175, 261
- Task_StandAtNode: 51
- Task_StandStill: 62
- Task_StealthKO: 228, 229
- Task_StripWeapons: 181
- Task_Stumble: 147
- Task_TakeIntruderOutside: 224
- Task_TakeIntruderOutsideTown: 274
- Task_TakeOffCuffs: 219
- Task_TalktoPlayer: 102, 159, 161
- Task_TownStalker: 165
- Task_TravelToMissionTarget_ArmyFormation: 215
- Task_TravelToTargetTown: 53, 130
- Task_UnCrouch: 28
- Task_UnlockDoor: 78, 80, 140
- Task_UseBed: 98, 100, 258
- Task_UseCage: 107
- Task_UseTrainingDummy: 97
- Task_WorshipTarget: 236

## Build tasks

**Verified** (factory `FUN_14032ebd0`, order labels `FUN_14079b2d0`): player orders 2 "Build", 95 "Repair" (both
`Task_Build`), 96 "Dismantle" (`Task_Dismantle`); 71 `Task_AddMaterialsToBuilding`; 247 `Task_EmptyMachine_BuildMats`.
Conditions: 50 `hasBuildingMaterials` (`FUN_14059ba50`: nothing outstanding, or the character or its backpack holds
one of an outstanding item), 51 `buildingHasBuildingMaterials` (`FUN_1405a39c0`: site not stalled), 150
`HAVE_SOME_BUILD_MATS` (`FUN_140597530`, **Observed**: inventory holds the target's item). `Task_Build`
(`FUN_14034a440`) plays `medic` (sounds `Character_Build_Loop_Start/Stop`), pushes build work each update and ends
when the building is gone, the site stalls (back to the planner) or it completes. Work, delivery, fetching (115 -> 247),
the Engineer job (125), repair and dismantle: [buildings-production](buildings-production.md#construction-tick).

## Prospecting

**Verified** (`Task_Prospecting` vtable `1416bf310`: init `140351c90`, update `140002f04`'s target; the HUD button `FUN_140721800`). Order 150, flags 0x41, condition `!me swim`. It is a `ProgressBarTask` (bar caption "Prospecting"). Each update: plays the `medic`
animation at speed 1, sets the character's state field (`+0x198`) to 4 (meaning **Unknown**), adds the character's update time (`Character+0xc4`, [game-loop](game-loop.md#the-character-update-budget)) to the progress; at 1.0 it ends the task
(`FUN_1400522c0`) and hands the character's position, its science stat and its name to the Prospecting window ([ui-screens](ui-screens.md#prospecting)). There is no walking, no XP and no target: it runs where the character stands.

## TaskData registry

**Verified** (disassembly of the table builder `FUN_140319a60`, each registration call `FUN_14060ea00(type, memberFn, ...)` parsed,
then every row re-parsed from the decompile of the same function: 253 registration calls with 253 distinct type numbers, and type,
text, flag bits and the contents and order of lists A and B agree for all 253 rows; the decompile drops some trailing call arguments,
so the `!` on an effect and the `*` marker rest on the disassembly alone and are **Observed**). 251 of the 288 named types plus the
two unnamed types 289 and 290 are registered; 37 named types are not: 9, 10, 11, 18, 23, 27, 33, 46, 48, 49, 50, 54, 56, 63, 66, 70, 109, 111, 117,
120, 126, 145, 151, 154, 160, 178, 179, 191, 193, 205, 206, 229, 251, 252, 265, 267, 271.
Columns: display text (field +0x138), registration flag bits, condition list A (TaskData +0x4c; names prefixed `!` are required
false; `*` marks an entry whose second flag byte (+5) is set, meaning **Unknown**), condition list B (+0xa8, at most 11) and the
single effect (+0x128). **List A is what a row achieves, list B what it needs, the effect the state a goal row wants another row
to achieve** (**Observed**, [planner](#planner)). The `ALWAYS_IMPOSSIBLE_TASK` row's text is the string "null". Two states (33 and 85)
are registered with an empty name.

A/B/effect use StateType IDs below; `isAtLocation` is two ids, 1 and 46 (67 Running achieves 46, so it is no provider of 1). `!` and `*` retain their original meanings; literal `none`/`-` remain unchanged. Unmapped or ambiguous names remain literal. Row order is registration order.

| ID | Text | Flags | A | B | Effect |
| ---: | --- | --- | --- | --- | --- |
| 1 | Moving | 0x63 | isAtLocation, !69, 40, 41 | 27, !24, !83, 60, 90, 59 | - |
| 29 | Move order | 0x63 | isAtLocation | !24, !83, 60, 90, 59 | - |
| 67 | Running | 0x63 | isAtLocation, 40, 41 | 27, !24, !83, 60, 90, 59 | - |
| 82 | Moving to door | 0x61 | 57 | 27, !24, !83, 90, 60 | - |
| 163 | Moving to door inside | 0x41 | 111 | 27, !24, !83, 90, 60 | - |
| 83 | Moving to current door | 0x41 | 58 | 27, !24, !83 | - |
| 199 | Moving to door outside | 0x41 | 126 | 27, !24, !83, 90, 60 | - |
| 104 | null | 0x77 | - | 20 | - |
| 180 | Gate guard | 0x1 | - | 91, 52 | - |
| 121 | Moving to gate | 0x61 | 91 | 27, !26, 60 | - |
| 122 | Open gate | 0x61 | 90 | !54 | 52 |
| 123 | Bashing gate | 0x21 | !54, 56 | !70, !iAmBeingMeleeAttacked immediate, 165, hasWeaponEquipped, 91, !52 | - |
| 290 | Bashing gate | 0x21 | !54, 56 | !70, !iAmBeingMeleeAttacked immediate, 165, hasWeaponEquipped, 91, !52 | - |
| 272 | Smashing | 0x21 | 163 | !70, 165, !iAmBeingMeleeAttacked immediate, hasWeaponEquipped, isAtLocation | - |
| 270 | Assaulting town | 0x21 | 90 | - | 163 |
| 189 | Worker | 0x1 | - | - | 123 |
| 190 | Worker | 0x1 | - | - | 136 |
| 87 | Operating machine | 0x1 | 123 | 38, 73, 2, !hasWeaponEquipped, !63, !62, !89, !169, 93, isAtLocation | - |
| 221 | Operating machine | 0x1 | 136 | 38, 73, 2, 93, !hasWeaponEquipped, isAtLocation | - |
| 152 | Operating automatic machine | 0x1 | - | 2, !hasWeaponEquipped | !62 |
| 124 | Hauling to | 0x1 | - | 2, 67, isAtLocation | - |
| 114 | Ditching to make space | 0x1 | 82 | - | !62 |
| 242 | Ditching resources | 0x1 | 147 | - | !62 |
| 184 | Ditching loot | 0x1 | !121 | - | !62 |
| 92 | Collecting resource | 0x1 | 66, !63, !169 | 82, isAtLocation | - |
| 247 | Collecting resource | 0x1 | 150 | 82, isAtLocation | - |
| 284 | Looting | 0x1 | 68 | 82, isAtLocation | - |
| 115 | Finding building mats | 0x1 | 50 | - | 150 |
| 93 | Filling machine | 0x1 | !62, 63* | 2, 67, !169, isAtLocation | - |
| 94 | Try filling machine | 0x1 | 67 | - | 66 |
| 113 | Try emptying machine | 0x1 | !63, 105, !66, !89 | - | !62 |
| 91 | Unjamming machine | 0x1 | !64 | !62, !63 | - |
| 90 | Unjam all machines | 0x1 | !65 | - | !64 |
| 89 | Keeping everything running | 0x1 | - | !65 | - |
| 88 | Delivering | 0x1 | - | isAtLocation | - |
| 84 | Open current door | 0x41 | 60 | - | 53 |
| 74 | Opening door here | 0x41 | 53 | !55, 58 | - |
| 75 | Closing door here | 0x41 | !53 | 58 | - |
| 85 | Open destination door | 0x41 | 59 | - | 52 |
| 72 | Opening door | 0x41 | 52 | !54, 57 | - |
| 73 | Closing door | 0x41 | !52 | !56, 57 | - |
| 77 | Locking door | 0x41 | 54 | !52, 70, !56, 57 | - |
| 78 | Unlocking door | 0x45 | !54 | 70, 57 | - |
| 140 | Unlocking door | 0x1 | !54 | 70, 57 | - |
| 79 | Locking door here | 0x41 | 55 | !52, 70, 58 | - |
| 162 | Locking door from inside | 0x41 | 112 | 70, 111, !52 | - |
| 198 | Locking door from outside | 0x41 | 127 | 70, 126, !52 | - |
| 80 | Unlocking door here | 0x41 | !55 | 70, 58 | - |
| 287 | Bashing door | 0x21 | !55, 56, 52 | !70, !128, hasWeaponEquipped, 57 | - |
| 195 | Locking up cages | 0x1 | - | - | 54 |
| 155 | Locking up | 0x1 | 87 | !strangers in house, 107 | 112 |
| 197 | Locking up from outside | 0x1 | - | !strangers in house, !107 | 127 |
| 86 | Opening up | 0x1 | !87 | - | 52 |
| 156 | Entering building | 0x61 | 107 | 90, 59 | - |
| 216 | Ransacking town | 0x1 | - | hasWeaponEquipped | 107 |
| 200 | Leaving building | 0x61 | !107 | 59 | - |
| 76 | Lockpicking | 0x55 | !54 | 38, 166, isAtLocation, !hasWeaponEquipped | - |
| 201 | Lockpicking Shackles | 0x55 | !114 | 38, 167, isAtLocation, !hasWeaponEquipped | - |
| 219 | Removing Shackles | 0x45 | !134 | !114 | - |
| 202 | Escaping | 0x55 | none | !134, !79 | - |
| 185 | Cutting shackles | 0x55 | !114 | 38, 122, isAtLocation, !hasWeaponEquipped | - |
| 285 | Cutting lock | 0x55 | !54 | 38, 122, isAtLocation, !hasWeaponEquipped | - |
| 186 | Breaking shackles | 0x55 | !114 | 38, isAtLocation | - |
| 286 | Breaking lock | 0x55 | !54 | 38, isAtLocation | - |
| 133 | Get out of cage | 0x74 | !79 | !54 | - |
| 256 | Get out of cage | 0x74 | !79 | - | - |
| 207 | Escape cage | 0x74 | !79 | !54 | - |
| 81 | Bashing door | 0x21 | !54, 56, 52 | !70, !128, hasWeaponEquipped, 57 | - |
| 226 | Bashing door | 0x21 | !54, 56, 52 | !70, !128, hasWeaponEquipped, 57 | - |
| 95 | Repairing | 0x1 | !56, !71 | 38, isAtLocation, !26 | - |
| 125 | Engineer | 0x1 | - | - | 2 |
| 213 | Lifting/Snatching | 0x1 | 48 | 80, 27, !26, 7, isAtLocation | - |
| 240 | Capture Target | 0x1 | 48 | 95, 113, 80, 27, !26, 7, isAtLocation | - |
| 225 | Lifting/Snatching Order | 0x1 | 48 | 80, 27, !26, isAtLocation | - |
| 246 | Kidnapping | 0x1 | 48 | 80, 27, !26, 7, isAtLocation | - |
| 68 | Lifting | 0x1 | 48 | 80, 27, !26, isAtLocation, 7 | - |
| 170 | Lifting and healing | 0x1 | 115 | !78, !22, 80, 27, !26, isAtLocation | - |
| 69 | Putting down | 0x1 | !48, !47 | 47 | - |
| 2 | Building | 0x1 | 2, !71 | 38, 51, isAtLocation, !26 | - |
| 96 | Demolishing | 0x1 | none | isAtLocation | - |
| 71 | Adding materials | 0x1 | 51 | 50, isAtLocation, !26 | - |
| 3 | Collecting item | 0x65 | 3 | 15, isAtLocation | - |
| 14 | Idleness | 0x75 | - | !162, !hasWeaponEquipped | - |
| 28 | Standing | 0x25 | !24, !25, !26 | !83 | - |
| 65 | Getting up | 0x25 | !162 | 45 | - |
| 52 | Waking up | 0x65 | - | 45 | - |
| 147 | Stumbling | 0x21 | - | 45 | - |
| 6 | Equipping weapon | 0x65 | hasWeaponEquipped | !me swim, 38 | - |
| 264 | Equipping crossbow | 0x65 | hasWeaponEquipped | !me swim, 38 | - |
| 7 | Sheathing weapon | 0x65 | !hasWeaponEquipped, !hasWeaponEquipped, !hasWeaponEquipped | - | - |
| 269 | Fixing Legs | 0x45 | !103 | 38, 23, !me swim, !39, !hasWeaponEquipped | - |
| 25 | First aid | 0x45 | !22 | 38, 23, !target swimming, 42, isAtLocation, !hasWeaponEquipped | - |
| 249 | Splinting | 0x45 | !152 | 38, 151, !target swimming, 42, isAtLocation, !hasWeaponEquipped | - |
| 60 | Repairing | 0x45 | !133 | 38, 132, !target swimming, 42, isAtLocation, !hasWeaponEquipped | - |
| 58 | Medic | 0x45 | - | - | !22 |
| 250 | Splint Rigging | 0x5 | - | - | !152 |
| 57 | Robotics | 0x45 | - | !39 | !133 |
| 62 | Standing still | 0x75 | - | !me swim, !39 | - |
| 64 | Stand still dammit! | 0x45 | 42 | !target swimming, !16 | - |
| 237 | Worshiping | 0x41 | - | - | target worshiped |
| 236 | Worshiping | 0x41 | target worshiped | !hasWeaponEquipped, !47 | - |
| 131 | Looting food | 0x1 | - | - | 68 |
| 259 | Picking up food | 0x71 | 149 | - | 3 |
| 277 | Fetch | 0x20 | 149 | - | 3 |
| 278 | Playing 'Limbs' | 0x20 | !149 | - | - |
| 279 | Limb Jealousy | 0x20 | - | - | 28 |
| 243 | Getting food | 0x61 | - | - | 149 |
| 244 | Grabbing food | 0x61 | 149 | !12, 82, isAtLocation | - |
| 8 | Finding weapon | 0x45 | 4 | - | 3 |
| 177 | Looting the dead | 0x45 | - | - | 3 |
| 212 | Confiscating goods | 0x45 | - | - | 3 |
| 26 | Looting | 0x45 | !0 | isAtLocation | - |
| 238 | Foraging animals | 0x1 | - | 82 | 148 |
| 241 | Looting resources | 0x5 | 148 | isAtLocation | - |
| 35 | Running away | 0x61 | !39, 6 | 60, !26 | - |
| 280 | Running away | 0x61 | - | 60, !26 | - |
| 128 | Retreating | 0x63 | - | !26 | - |
| 127 | Retreating fast | 0x63 | 95 | !26 | - |
| 19 | Going home | 0x61 | 18 | - | isAtLocation |
| 59 | Get ready | 0x65 | 37 | !26, hasWeaponEquipped | - |
| 4 | Attacking | 0x41 | 6*, 7 | !target swimming, !me swim, 27, 60, 90, 59, 37, !69, 41 | - |
| 262 | Shooting | 0x1 | 6*, 7 | !me swim, !iAmBeingMeleeAttacked immediate, 161, !26, hasWeaponEquipped | - |
| 263 | Shooting | 0x1 | 7 | !me swim, !iAmBeingMeleeAttacked immediate, 161, !26, hasWeaponEquipped | - |
| 266 | Shooting | 0x1 | 7 | !me swim, !iAmBeingMeleeAttacked immediate, 161, !26, hasWeaponEquipped | - |
| 228 | Taking out | 0x1 | - | !target swimming, 27, 60, 90, 59, 38, !69 | - |
| 227 | Attacking | 0x20 | 6*, 7 | 27, !78, 60, 90, 59, 37, !69, hasWeaponEquipped, 41 | - |
| 13 | Attacking attackers of | 0x61 | !16, !39, !12* | - | 7 |
| 34 | Attacking trouble makers | 0x21 | !29 | - | 7 |
| 149 | Turret guard | 0x1 | 6* | 38 | using target turret |
| 222 | Turret home guard | 0x1 | 6* | - | using target turret |
| 146 | Turret gunner | 0x1 | using target turret*, using any turret | 38, !47, !iAmBeingMeleeAttacked immediate, 2, 93, !hasWeaponEquipped, isAtLocation | - |
| 235 | Turret gunner | 0x1 | 7 | using any turret | - |
| 234 | Turret guard | 0x1 | 6*, !39, !12* | 38 | using target turret |
| 101 | Assault | 0x61 | - | !47 | 7 |
| 248 | Attacking Squad | 0x61 | - | !iAmBeingMeleeAttacked general, !47 | 7 |
| 165 | Stalking | 0x1 | - | - | - |
| 103 | Diplomatting | 0x1 | 76 | !101, 131 | - |
| 129 | Approaching fast | 0x1 | 76 | !101, 131 | - |
| 159 | Talking | 0x41 | isDialogDelivered | isAtLocation | - |
| 161 | Talking | 0x55 | isDialogDelivered | - | - |
| 12 | Talking | 0x45 | none | isAtLocation | - |
| 102 | Making announcement | 0x41 | none | isAtLocation | - |
| 164 | Following and talking | 0x41 | none | !26, !iAmBeingMeleeAttacked immediate, 28 | - |
| 188 | Working the slaves | 0x1 | none | - | isDialogDelivered |
| 194 | Selling slaves | 0x1 | !155 | - | isDialogDelivered |
| 218 | Selling slaves | 0x1 | !155 | - | isDialogDelivered |
| 192 | Delivering slaves | 0x3 | - | !26, atDestinationTown | - |
| 158 | Shooing | 0x41 | !strangers in house | 107, !130 | isDialogDelivered |
| 283 | Shooing | 0x41 | !strangers in house | 107, !130 | isDialogDelivered |
| 17 | Protection | 0x61 | - | !16, !22 | - |
| 16 | Attacking enemies | 0x61 | - | - | 6 |
| 41 | Fighting for master | 0x21 | - | - | 6 |
| 21 | Attacking others | 0x61 | - | - | 7 |
| 144 | Home territory aggression | 0x61 | - | - | 6 |
| 32 | Self preservation | 0x65 | !iAmBeingMeleeAttacked general | - | !39 |
| 45 | Bodyguard | 0x61 | - | !16, !39 | - |
| 44 | Following | 0x61 | 28 | !16, 60, 90, 59 | - |
| 253 | Following | 0x61 | 28 | !7, 60, 90, 59 | - |
| 40 | Following | 0x61 | 28 | !7, !16, 60, 90, 59 | - |
| 141 | Following leader | 0x61 | - | 60, 90, 59, !26 | - |
| 171 | Following master | 0x61 | - | - | - |
| 31 | Staying close | 0x61 | - | !7, !16, 60, 90, 59 | - |
| 289 | Staying close | 0x20 | - | !7, !16, 60, 90, 168 | - |
| 166 | Restraining | 0x41 | 134, !124 | 38, isAtLocation, 118, 7 | - |
| 181 | Stripping weapons | 0x41 | 118 | 38, isAtLocation, 7 | - |
| 182 | Processing slave | 0x1 | 120 | isAtLocation, 134, 42 | - |
| 183 | Watching slaves | 0x1 | - | - | 120 |
| 260 | Watching for new slaves | 0x1 | - | - | 120 |
| 167 | Capturing slave | 0x1 | - | - | 134 |
| 174 | Ordering slaves | 0x1 | 116 | 38, 125, 134, isAtLocation | - |
| 173 | Gathering slaves | 0x1 | - | - | 116 |
| 217 | Gathering slaves for shipping | 0x1 | - | - | 116 |
| 245 | Gathering unholy slaves | 0x1 | - | - | 116 |
| 187 | Obedient Slave | 0x75 | none | - | - |
| 168 | Carrying slaves | 0x1 | - | - | 115 |
| 169 | Putting down | 0x1 | - | !47 | - |
| 15 | Protect allies | 0x61 | - | - | !12 |
| 288 | Protect allies | 0x61 | - | - | !12 |
| 143 | Protect close allies | 0x61 | - | - | !16 |
| 30 | Holding position | 0x75 | - | - | isAtLocation |
| 51 | Stand at node | 0x61 | 31 | isAtLocation | - |
| 20 | Shopkeeper | 0x41 | - | - | 31 |
| 38 | Static guard | 0x61 | - | !47 | 31 |
| 268 | Static guard indoors | 0x61 | - | !47 | 31 |
| 157 | Static town guard | 0x61 | - | !47 | 31 |
| 47 | general node | 0x1 | - | - | 31 |
| 42 | Getting a weapon | 0x45 | - | 38, 4 | - |
| 176 | Blood tracking | 0x61 | - | !26, 60 | - |
| 22 | Patrolling | 0x21 | - | !26, 60, !hasWeaponEquipped | - |
| 36 | Patrolling town | 0x21 | - | atDestinationTown, !47, !26, 60, !hasWeaponEquipped | - |
| 37 | Wandering town | 0x1 | - | !26, !hasWeaponEquipped | 31 |
| 118 | Shopping | 0x1 | - | 86 | (unnamed state 85) |
| 119 | Shopping | 0x1 | (unnamed state 85) | isAtLocation | - |
| 214 | Relaxing | 0x1 | - | 86 | 123 |
| 43 | Staying home | 0x61 | - | !26, !hasWeaponEquipped, 18 | 136 |
| 255 | Reigning | 0x41 | - | !26, !hasWeaponEquipped, 18 | 136 |
| 275 | Sitting around | 0x1 | - | !26, !47, !hasWeaponEquipped, 86 | 136 |
| 24 | Wandering | 0x21 | 86 | - | isAtLocation |
| 39 | Wandering trader | 0x1 | - | !hasWeaponEquipped, 32, atDestinationTown | - |
| 55 | Recruiting | 0x1 | - | - | isAtLocation |
| 53 | Travelling | 0x61 | atDestinationTown | !26, 32 | - |
| 130 | Travelling quickly | 0x61 | atDestinationTown | !26, 32 | - |
| 215 | Mission formation | 0x61 | 131 | !26 | - |
| 5 | Attacking target | 0x61 | 7 | !target swimming, !78, 37, !69 | - |
| 61 | Attacking target unprovoked | 0x61 | 7 | !target swimming, !78, 37, !69 | - |
| 150 | Prospecting | 0x41 | - | !me swim | - |
| 97 | Training | 0x1 | - | 38, 73, isAtLocation | - |
| 153 | Going to bed | 0x41 | - | !strangers in house P | 83 |
| 239 | Going to bed | 0x41 | - | !strangers in house, 87 | 83 |
| 98 | Sleeping | 0x41 | 72, 83 | !130, 73, !hasWeaponEquipped, !47, isAtLocation | - |
| 258 | Sleeping | 0x41 | - | !130, 73, !hasWeaponEquipped, !47, isAtLocation | - |
| 175 | Sleeping on ground | 0x61 | 72 | 6, !29, !95, !hasWeaponEquipped, !47 | - |
| 261 | Hiding | 0x61 | 72 | 6, !29, !hasWeaponEquipped, !47 | - |
| 99 | Put in bed | 0x1 | 77 | 73, 47, isAtLocation | - |
| 116 | Getting out of bed | 0x41 | !83 | 45 | - |
| 208 | Getting out of bed! | 0x41 | !83 | 45 | - |
| 257 | Getting out of bed | 0x41 | !83 | 45 | - |
| 105 | Find and rescue | 0x1 | - | - | 48 |
| 148 | Find and rescue | 0x1 | - | - | 48 |
| 106 | Find and put in bed | 0x1 | - | - | 77 |
| 100 | Sleeping | 0x41 | 72 | - | - |
| 142 | Find and rescue leader | 0x1 | - | 101 | 48 |
| 135 | Kill random prisoner | 0x1 | - | - | !98 |
| 134 | Kill prisoner | 0x1 | !98 | hasWeaponEquipped, isAtLocation | - |
| 139 | Eat random prisoner | 0x21 | - | - | 135 |
| 282 | Dinner time | 0x21 | - | - | 135 |
| 230 | Eat corpse | 0x61 | - | - | 135 |
| 233 | Eat body | 0x61 | - | - | 135 |
| 232 | Find crops | 0x61 | - | - | !am hungry |
| 231 | Eating crops | 0x61 | !am hungry | farm has food, isAtLocation | - |
| 220 | Eat prisoner | 0x61 | 135 | 142, !47, !hasWeaponEquipped, isAtLocation | - |
| 196 | Beat prisoner | 0x1 | 7 | 78, hasWeaponEquipped, isAtLocation | - |
| 136 | Disposing of corpse | 0x1 | 100 | 99, isAtLocation | - |
| 138 | Find corpses | 0x41 | 99 | - | 48 |
| 137 | Corpse disposal | 0x1 | - | 99 | 100 |
| 107 | Imprisoned | 0x55 | 79 | 73, isAtLocation | - |
| 108 | Put in prison | 0x1 | 81 | 73, 47, isAtLocation | - |
| 112 | Find and put in cage | 0x1 | 154 | !99 | 81 |
| 281 | Find and put in cage | 0x1 | 154 | - | 81 |
| 172 | Getting in my cage | 0x55 | - | - | 79 |
| 211 | Freeing prisoner | 0x1 | - | - | !78 |
| 276 | Liberating prisoners | 0x1 | - | - | !78 |
| 110 | Release from prison | 0x1 | 0, !78 | 125, !73, isAtLocation | - |
| 203 | Arresting | 0x1 | 129 | 7, 118, 48 | - |
| 204 | Bounty hunting | 0x1 | - | - | 129 |
| 223 | Remove Home Intruder | 0x1 | - | !47 | 48 |
| 224 | Intruder out | 0x41 | 137 | 47, 60 | - |
| 273 | Remove Intruder | 0x1 | - | !47 | 48 |
| 274 | Throwing out | 0x41 | 164 | 47, 60, 90 | - |
| 209 | Investigating | 0x1 | !130 | 86 | - |
| 210 | Investigating | 0x1 | !130 | 86 | - |
| 132 | Find and kidnap | 0x1 | 153 | !47 | 48 |
| 254 | Kidnapper | 0x1 | none | 153, 154 | - |

## StateType ids

**Observed** (registry `FUN_140610250`, one `FUN_140610170` call per id; names are the strings passed; the names of the other ids were re-read
from the decompile and agree with this table, except that the decompile shows no registration call for ids 0, 2, 3, 6, 13 and
20, whose names here come from the disassembly). No registration call was found for ids 8, 9, 10, 11, 14, 19, 21, 30, 34, 35, 44,
49, 61, 75, 96, 119, 138, 170; ids 33 and 85 are registered with an empty name.

| id | name |
|---|---|
| 0 | `none` |
| 1 | `isAtLocation` |
| 2 | `isBuildingComplete` |
| 3 | `isSubjectInInventory` |
| 4 | `hasWeapon` |
| 5 | `hasWeaponEquipped` |
| 6 | `isNoEnemiesInVicinity` |
| 7 | `isTargetDown` |
| 12 | `isAlliesFighting` |
| 13 | `isTargetEnemy` |
| 15 | `isItemLootable` |
| 16 | `isAggressionTowards` |
| 17 | `AM_IDLE` |
| 18 | `isAtHome` |
| 20 | `noneR` |
| 22 | `needsFirstAid` |
| 23 | `haveFirstAidKit` |
| 24 | `isCrouching` |
| 25 | `isLyingProne` |
| 26 | `IS_CROUCHING_OR_LYING` |
| 27 | `isMovementAllowed` ([range](#perma-jobs)) |
| 28 | `isNearTo` |
| 29 | `isGeneralAggressionLevel` |
| 31 | `atNode` |
| 32 | `hasDestinationTown` |
| 33 | (empty name string) |
| 36 | `atDestinationTown` |
| 37 | `isReadyForAction` |
| 38 | `hasOneWorkingArm` |
| 39 | `isAggressionTowardsMe` |
| 40 | `isWithin10Meters` |
| 41 | `isWithin50Meters` |
| 42 | `targetStandingStill` |
| 43 | `iAmDown` |
| 45 | `canGetUp` |
| 46 | `isAtLocation` |
| 47 | `IS_CARRYING_SOMETHING` |
| 48 | `IS_CARRYING_TARGET` |
| 50 | `hasBuildingMaterials` |
| 51 | `buildingHasBuildingMaterials` |
| 52 | `DOOR_IS_OPEN` |
| 53 | `DOOR_IS_OPEN_HERE` |
| 54 | `isDoorLocked` |
| 55 | `DOOR_IS_LOCKED_HERE` |
| 56 | `DOOR_IS_BROKEN` |
| 57 | `AT_LOCATION_BUILDING_DOOR` |
| 58 | `AT_CURRENT_LOCATION_BUILDING_DOOR` |
| 59 | `DESTINATION_IS_ACCESSIBLE` |
| 60 | `LOCATION_IS_ACCESSIBLE` |
| 62 | `machineHasEmptyInputs` |
| 63 | `isMachineOutputFull` |
| 64 | `isMachineJammed` |
| 65 | `isAnyMachineJammed` |
| 66 | `haveSomeResourcesFrom` |
| 67 | `haveSomeResourcesFor` |
| 68 | `HAVE_SOME_LOOT_FROM` |
| 69 | `isRouteTraceBlocked` |
| 70 | `isObjectMine` |
| 71 | `isBuildingDamaged` |
| 72 | `isFullyRested` |
| 73 | `machineHasFreeOperatorSlot` |
| 74 | `isEnemyForceDefeated` |
| 76 | `isMessageDelivered` |
| 77 | `isCarriedDudeInBed` |
| 78 | `TARGET_IS_IMPRISONED` |
| 79 | `I_AM_IMPRISONED` |
| 80 | `isCarryingArmOK` |
| 81 | `CARRIED_DUDE_NOW_IN_CAGE` |
| 82 | `gotInventoryRoomForResources` |
| 83 | `isInBed` |
| 84 | `isAtAShop` |
| 85 | (empty name string) |
| 86 | `atATown` |
| 87 | `allDoorsLocked` |
| 88 | `isInsideTargetBuilding` |
| 89 | `haveSomeResourcesFromThisMachineButWantThemGoneIfPossible` |
| 90 | `DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS` |
| 91 | `AT_LOCATION_FORTIFICATION_GATE` |
| 92 | `isEnemy` |
| 93 | `BUILDING_HAS_POWER` |
| 94 | `atDestinationTown` |
| 95 | `isAtHomeTown` |
| 97 | `CAGES_ARE_ALL_FULL` |
| 98 | `MACHINE_OCCUPANT_IS_ALIVE` |
| 99 | `isCarriedDudeDead` |
| 100 | `CARRIED_DUDE_NOW_IN_DISPOSAL_MACHINE` |
| 101 | `isLeaderDown` |
| 102 | `isSquadmatesFighting` |
| 103 | `myLegsAreMessedUp` |
| 104 | `using target turret` |
| 105 | `isMachineOutputEmpty` |
| 106 | `machineHasFullInputs` |
| 107 | `I_AM_INSIDE_TARGET_BUILDING` |
| 108 | `strangers in house` |
| 109 | `isDialogDelivered` |
| 110 | `isDialogDelivered` |
| 111 | `AT_LOCATION_BUILDING_DOOR_INSIDE` |
| 112 | `DOOR_IS_LOCKED_AND_IM_INSIDE` |
| 113 | `targetAtATown` |
| 114 | `isWearingShackles_locked` |
| 115 | `IS_CARRYING_TARGET_HEALTHY` |
| 116 | `TARGET_IS_MY_SLAVE` |
| 117 | `TARGET_HAS_BEEN_LOOTED` |
| 118 | `TARGET_IS_UNARMED` |
| 120 | `TARGET_LOOKS_LIKE_A_SLAVE` |
| 121 | `CARRYING_EXCESS_LOOT` |
| 122 | `hasTools` |
| 123 | `IS_WORKING` |
| 124 | `isTargetEscapee` |
| 125 | `TARGET_IS_CONSCIOUS` |
| 126 | `AT_LOCATION_BUILDING_DOOR_OUTSIDE` |
| 127 | `DOOR_IS_LOCKED_AND_IM_OUTSIDE` |
| 128 | `isAlly` |
| 129 | `TARGET_IS_ARRESTED` |
| 130 | `ALARMS_IN_THE_VICINITY` |
| 131 | `atPackageTarget` |
| 132 | `haveroboAidKit` |
| 133 | `needsFirstAid_robot` |
| 134 | `isChained` |
| 135 | `TARGET_IS_EATEN` |
| 136 | `IS_WORKING_PRETEND` |
| 137 | `INTRUDER_IS_OUTSIDE` |
| 139 | `me swim` |
| 140 | `target swimming` |
| 141 | `strangers in house P` |
| 142 | `isTargetRestrainedOrKO` |
| 143 | `farm has food` |
| 144 | `am hungry` |
| 145 | `using any turret` |
| 146 | `target worshiped` |
| 147 | `DITCHED_ALL_RESOURCES` |
| 148 | `LOOTED_STORABLE_ITEMS` |
| 149 | `gotAFoodItem` |
| 150 | `HAVE_SOME_BUILD_MATS` |
| 151 | `hasSplintKit` |
| 152 | `needsSplint` |
| 153 | `FINAL_GOT_A_KIDNAP_VICTIM` |
| 154 | `FINAL_FOUND_AND_PUT_CARRIED_IN_A_CAGE` |
| 155 | `iHaveSomeSlaves` |
| 156 | `hasWeaponEquipped` |
| 157 | `hasWeaponEquipped` |
| 158 | `iAmBeingMeleeAttacked immediate` |
| 159 | `iAmBeingMeleeAttacked general` |
| 160 | `isWithinMyBowRange` |
| 161 | `hasTwoWorkingArm` |
| 162 | `iAmProne` |
| 163 | `isBuildingDestroyed` |
| 164 | `INTRUDER_IS_OUTSIDE_GATES` |
| 165 | `crowdLimit8` |
| 166 | `iHaveSufficientLockSkill` |
| 167 | `iHaveSufficientLockSkillForShackles` |
| 168 | `DESTINATION_IS_ACCESSIBLE_ANIMAL` |
| 169 | `isMachineInputInvalid` |

## Arrest and prison rows

Rules: [economy 11.5, 11.7](economy.md#115-bail-and-sentence). **Observed** (evaluators and TaskData hooks resolved from the exe's thunk bytes):

| Id | Semantics |
| --- | --- |
| state 129 `TARGET_IS_ARRESTED` (`FUN_14059a380`) | target `+0x2f8 == 2` (imprisoned), or carried (`+0x3d4`) by a character of another faction; a non-character target: true while I carry it |
| state 78 `TARGET_IS_IMPRISONED` (`FUN_1405a1cc0`) | the target character's `+0x2f8 == 2` |
| goal 203 Arresting | desire = AI slot 32 (`FUN_140598a40`): 0 when status `+0x88` is null and the character's ownership town is set, else 1 |
| goal 204 Bounty hunting ("Arrest bounties") | desire = AI slot 33: the free-cage finder's result; target finder `FUN_14099bfc0` (wanted, uncarried, not imprisoned) |
| goals 112 / 281 Find and put in cage | desire `AI::scorePutCarriedDudeInCage` `FUN_1405a1140` (**Verified**): 0 unless carrying a resolvable character; then outside a town 0.1, another town than the blackboard's 0, an animal 0, law enforcement carrying a bounty-free slave 1.4, an ally 0, dead 0.5, alive 2.0. Finder `AI::findOwnedFreeCage` (a free BF_CAGE). 112 also needs !99 isCarriedDudeDead, 281 does not: a captive who dies on the way is caged only by 281. Data: 281 in the two "addon- law enforcement" and two fish resident packages |
| goal 211 Freeing prisoner | finder `FUN_1408cf3c0` (`BuildingFinder_Cages`): an occupied usable cage whose occupant's sentence is served (`FUN_1405c9f00`) |
| goal 276 Liberating prisoners | finder `FUN_1408cf140` (`BuildingFinder_CagesToLiberate`): a cage of a faction that fails the relation test `FUN_1406b2630` (read as allied) holding an occupant that is not an enemy |
| goal 42 "Getting a weapon" / 8 FIND_WEAPON / 6, 264 equip (**Verified**, decompile) | 42: B 38 hasOneWorkingArm, !4 hasWeapon, desire 2.0 (`AI::scoreDontWantToBeUnarmed`); plans 8 (finder `AI::findWeapon` `FUN_14099b1d0`, effect 3, acted out by row 3 "Collecting item"). Finder, first pass with a match, each within 100: nearest free WEAPON world item it has room for (in a town only one in its location owned by nobody or an enemy); else the first sensed dead body with a weapon (not carried, not an animal, no squad mate on it); else the first KO/crawling non-ally, player-side only with Bandits loot, not loved. `Task_EquipBestWeapon` (finishes on its first update): sections `back` then `hip`, first item whose ranged flag matches (1 for 264) and usable; an NPC not in a player squad also any WEAPON of the inventory. Data: 42 in 19 packages |
| goals 202 Escaping / 256 desire (**Verified**, decompile) | 202: desire 2.0, B !134 isChained, !79 imprisoned, restricted, no finder or acts: its work is the providers, 219 Removing Shackles (A !134, B !114 from 201 / 185 / 186) and the cage rows; never done (list A `none`). 256 desire 1 while imprisoned, not chained, in a building with a lock that is not locked. Data: 202 in the "Escaping slave" packages, 256 in "captured NPC", 133 in none |
| goal 212 Confiscating goods (**Verified**, decompile) | desire 1, finder `AI::findPrisonerWithContraband` `FUN_1408cf6a0`: cages of the current town (`BuildingFinder_Cages` (1, 0x100)) in list order, an occupant no squad mate works on, first with a stolen equipped armour piece it has room for, else any stolen item: the first cage wins. Effect 3, acted out by row 3 (`Task_Loot_AI`), whose law-enforcement branch takes one unit of each stolen item ([confiscation](carry-loot.md#33-npc-looting)) |
| goals 131 Looting food / 243 Getting food (**Verified**, decompile) | finder `AI::findSomethingToLootFoodFrom` `FUN_1408cb650` over the blackboard's target town, else the current one, else the zone grid: buildings with a non-empty inventory, owned by the looter's faction or the looter not the player's, not inside another building for an animal, not path-impossible, not a shop of the looter's faction; without function stuff when it holds food the looter may eat, with function stuff when (another owner or no production building) and it holds function-3 food (an animal also 15). Nearest wins. 131: effect 68 by 284 `Task_EmptyMachine` (moves output items while there is room; nothing for a building without function stuff); 243: effect 149 by 244 `Task_grabOneFood` |
| 133 / 207, 256 `Task_GetOutOfCage` | constructor flag "escape" 0 for 133, 1 for 207 and 256 ([locks](locks.md#cages-and-beds)) |

## Prisoner fate and slave rows

**Observed** (TaskData registrations in `FUN_140319a60`: desire at `+0x108`, finder at `+0x160`; the desire thunks resolve to `AI` vtable slots; task classes from their vtables).
Meitou: [simulation, captives](../simulation.md#captives).

| Goal | Desire | Finder / rule |
| --- | --- | --- |
| 135 Kill random prisoner, 282 Dinner time | AI slot 20 `FUN_14032e9b0`: 1 when the character's town's kill timer has passed, or every cage it knows is held by a live character (`FUN_1405a1ab0`, the CAGES_ARE_ALL_FULL test; false with no cage); else 0. A town not yet in the timer map gets now + uniform[1, 15) h (`FUN_14034a810`, `FUN_1409b1c80(1, 15)`), a global map keyed by the town | `FUN_1408d0d90` (also 139): keeps a remembered occupied cage, else the cage finder `FUN_1408d0770(ai, occupied, 8)`: function-8 buildings, weight clamp(1 - d^2/4608^2, 1e-5, 1), x100 same faction (another faction's only when a town test `FUN_140621230` passes, **Unknown**), x100 in the character's home, x1e-5 for the police case; the best weight wins. No target when the occupant's RACE `robot` (`+0x7c`) is set; else the occupant fires event 10 MARKED_FOR_DEATH at the chooser |
| 139 Eat random prisoner | slot 19 (1) | as 135 |
| 183 Watching slaves | slot 61 `FUN_140597b20`: with a target, slot 59; else 1 while not carrying and character vfunc `0x368` is 0 | `FUN_1408cd630`: none while itself held; sensed characters (masks 0x60 / 0x110) in slave state that need processing; else `BuildingFinder_SlavesToProcess` over its home town: occupied function-8 cages of non-player factions passing the `BuildingFinder_Cages` tests, occupant not of its own squad, within 180 (squared 32400) and needing processing |
| 260 Watching for new slaves | slot 61 | `FUN_1408cc110`: the same building finder; the first occupant that is not down (vfunc 6) and in slave state 0 |
| 167 Capturing slave | slot 61 | `FUN_1408cdd00`: sensed (0x4008 / 0x11) down or dead or legs-down characters, uncarried, free, not animals, not in the give-up list, NPC class (status `+0xdc`) not 1 LAW_ENFORCEMENT, 3 TRADER or 5 DIPLOMAT, slave state not 1, `FUN_140644170` false (already shackled); the nearest. Plan and tasks: [Slave work, shout and capture](#slave-work-shout-and-capture) |
| 168 Carrying slaves | `FUN_140599480` (the rescue desire) | `FUN_1408cdf50`: sensed (0x20 / 0x210) down characters in slave state, owned by its faction (status `+0xe0`), uncarried, free; the nearest |
| 173 Gathering slaves, 245 Gathering unholy slaves | slot 63 `FUN_14059b940`: 1 while the platoon's slave list holds at most 3 characters up and free (vfunc 6 false, `+0x2f8` 0) | 173 `FUN_1408cc4b0`: the nearest occupied processable cage of its faction in the squad's current town; 245 `FUN_1408d1410`: own-faction cages, the first occupant that is a woman (appearance `sex female`, `FUN_140295a50`) or of another race than the gatherer |
| 217 Gathering slaves for shipping | slot 64 `FUN_140597b80`: in a TOWN or OUTPOST (town `+0xd8` 2 or 1); for a player town (or faction byte `+8`) only an allied one | `FUN_1408cc600`: cages of any faction in the town, not player-owned, with a live awake occupant (vfunc `0x360` false) not twice in the give-up list; the nearest |
| 174 Ordering slaves (`Task_GetSlave`) | slot 19 | its start posts message type 5 (target, self) to the global queue `DAT_1421346d8` and ends; what the queue does with type 5 is **Unknown** (effect: 116 TARGET_IS_MY_SLAVE) |
| 182 Processing slave (`Task_ProcessNewSlave`) | slot 19 | 3 s of `medic`; then items of the weapon and armour sections not locked shackles (armour `+0x244` 6 kept) go to the processor's storage or inventory; the faction's `slave clothing` is made and put on where the slot is free (`FUN_1403560e0`); event 35 INTRODUCING_NEW_SLAVE (37 RECAPTURED_A_SLAVE when vfunc `0x3e0`) to it; slave state 1 (`FUN_1405a4940`), owner set when it had none, its jobs cleared and job 187 Obedient Slave added toward the owner; every platoon of the processor's faction and every sensed ally lists it (`FUN_14067aca0`) |
| 188 Working the slaves | slot 65 `FUN_14059a0a0`: 0.25 x the platoon's slave list size | `FUN_1408cd940`: a random live, uncarried, free slave of the platoon's list not in the give-up list; B list isDialogDelivered; the event is the row's own, 38 SHOUT_AT_SLAVE_WORKER (**Verified**: the registration stores 0x26 at TaskData `+0x130`) |
| 192 Delivering slaves | slot 65 | none; B: !26, atDestinationTown |
| 194 / 218 Selling slaves | 65; 218 slot 66 `FUN_140597c70`: only in a prison or slave camp (town type 7, 5) | `FUN_1408ca950`: a random allied building of designation flag 0x80000 in the town whose owner is a character or squad (`+0xd8` 1 / 0x22): its leader; B isDialogDelivered (event 39 SLAVE_DELIVERY) |
| 172 Getting in my cage | slot 19 | `FUN_1408caaf0`: for a slave whose owner handle is a character or squad: the nearest free cage of the owner's faction |
| 189 / 190 Worker | slot 19 | `FUN_1408cac60` / `FUN_1408cadb0`: a random building of the platoon's town whose BuildingFunction is in mask 0x41a (bits MINE, RESEARCH, REFINERY, CRAFTING); a character whose side differs from the town's (squad ownership `+0x250`) only works there as a slave of state 1; none found: 0. Effects IS_WORKING (123, 189) and IS_WORKING_PRETEND (136, 190), acted out by 87 / 221 Operating machine |
| 187 Obedient Slave | - | no desire or finder: a job added when a character is enslaved (the slave window's sale, processing), scored by its own branch in `FUN_14050e5b0` (see below) |
| 230 Eat corpse (**Verified**) | slot 19 (1) | `AI::findCorpse_onGround` `FUN_1408cfde0`: sensed DEAD, lying (ragdoll or carried), race `carriable`; an animal eater: not a robot, not its own race; not carried, no squad mate on it; nearest. Effect 135 TARGET_IS_EATEN is always false (`AI::noneState`), so it always plans 220 on the target and stays chosen while the finder finds it |
| 233 Eat body (**Verified**) | slot 19 (1) | `AI::findDeadOrKOBody_onGround` `FUN_1408ce680`: sensed KO, DEAD or CRAWLING, not ALLY, not a robot, not the eater's race, lying or unconscious or dead, not carried, no squad mate on it; nearest (a KO victim is eaten alive) |
| 232 Find crops (**Verified**) | slot 19 (1) | `FUN_1408cb810`: in the blackboard's target town, else the current one, whose faction does not coexist with the eater's (`FUN_1406b23c0`): a current farm target kept, else a random farm (class 13) with grown > 0.4 and died < 0.9. Effect 144 "am hungry" is always true, so it always plans 231 `Task_EatingCrops`: each update `eat` clip, crime 14 on the eater against the farm's faction, `FarmBuilding::eat(age 1 x dt)`: an accumulator past 1 destroys the next plant (growth -10, the harvest index), one plant per game second. Data: 230 in 5 packages and 7 races' `AI Goals`, 233 in Beak Thing and the spiders, 232 in the river and swamp raptors |
| 237 / 236 Worshiping (**Verified**) | slot 19 (1) | `AI::findFogmanWorshipTarget` `FUN_1408ce880`: a remembered cage with an occupant kept; else in the nearest town the first cage whose occupant is being eaten. Effect 146 is always false, so 236 `Task_WorshipTarget` runs on it: beyond 60 walk to the cage, within stop, face it, `bowing`, every uniform[3, 14) game s event 53 (no target); no end of its own. Data: 237 in the fog packages |
| 277 / 278 / 279 dog limb play (**Verified**) | 277 1; 278 `AI::scoreDogPlaying` (mouthful: 0.3 a player squad's dog over 0.4, else 1.1; a player adult over 0.95: 0); 279 `AI::scoreDogPlayingChase` (1.2 NPC; player: 0 adult, 0.3 over 0.2, 0.6 younger) | 277 `AI::findLimbOnGround`: nearest limb (function 17) within 200 in its location (an adult not while fed or hunger above 2.75; returns 1 even with no target). 278 `Task_DogLimbRunningPlay`: first run 80-180 past the centroid of who it senses, away; then random legs of 1000-1400, a new one under 200 left, speed 999; ends when the mouth empties. 279 `AI::findAllyAnimalWithFoodInMouth`: first sensed awake ALLY animal with a mouthful, followed. State 149: a mouthful or food in the inventory or backpack. Data: Bonedog and Boneyard Wolf race `AI Goals` |

- **Kill** (`Task_KillPrisoner`, 134 and 196): faces the target (a character, or a cage's occupant) and plays `chop down static`; each time the clip passes 0.55 the
  character's weapon attack (`FUN_1408868b0`, the race multipliers included) lands through the target's take-hit virtual and the target says event 11 SCREAMING_TORTURE. When the
  target is dead the character's town gets a new kill time, now + uniform[1, 15) h. 134 needs a drawn weapon.
- **Eat** (`Task_EatPrisoner`, 220): walks to within 20 of the target (XZ); a robot target ends it. Then each update: faces it, plays `eat`, says event 52 EATING_SOMETHING_SOUNDS,
  and calls the target's being-eaten virtual with 1.0 (`FUN_1404356f0`): unless dead, and while vfunc `0x358` is false (read as "conscious", **Unknown**) or its byte `+0x4f` is set, the
  target says event 11 and gains toughness XP (`FUN_1408c6430(stats, dt, 10, 21)`: the continuous entry, [character-stats](character-stats.md#levelling-xp), amount 10, stat 21 Toughness), then the
  medical update `FUN_140650370`: rate = 1 x lerp(2, 0.4, toughness x stat scale x 0.01) per second (x5 instead when medical `+0x164`, **Unknown**); parts from the last down:
  the first with h - 1.1 s > -M loses rate x dt from flesh and from the treated pool and the update ends; a limb with nothing left is severed when the global `DAT_142133504`
  is set; with nothing left, a counter `+0x15c` rises, and past 5 the character dies ("eaten"; "{1} has been eaten alive." for the player's). A robot is never eaten. The end
  of the task resets the town's kill time as Kill's does. A target not imprisoned and with `+0x278` set is bitten once and the eater re-ordered (order 0x23, **Unknown**).
- Whether the kill timer map is saved is **Unknown** (Meitou keeps it in the running game only).

### Slave work, shout and capture

Meitou: [simulation, captives](../simulation.md#captives).
- **Worker** (189 `AUTO_LABOURING_MINES`, 190 `..._PRETEND`; **Verified** registrations in `FUN_140319a60`: effects 0x7b IS_WORKING and 0x88 IS_WORKING_PRETEND, finders `FUN_1408cac60` /
  `FUN_1408cadb0`; only 190 commits, 0.5 + r x 2 h, crowd 1). They are not slave-only: the data lists them in "worker fighter indie resident", "Shop" (slave list), "working mines and farms",
  "Work, Protect, Capture", "South drone sacrifice farmer" and, in the Slave role list, "slave worker day guard" (171 at 0.1, 189, 172 at night, 190). The finder asks the platoon's town
  (`Blackboard +0x88`) for a building through `BuildingFinder_SpecialFunction` (mask 0x41a read as bits, **Observed**: 1 MINE, 3 RESEARCH, 4 REFINERY, 10 CRAFTING) and takes one at random
  (slot 2 `FUN_1409fb020`: index `FUN_1409b3b40(0, n - 1)`); the town test is the one in the table above. Acting out is 87 (real: needs the machine's inputs and output room, power, a free operator slot) or
  221 (pretend); both `Task_OperateMachine` ([sitting and the held tool](ai-tasks.md#town-goals), [production](buildings-production.md#production)). Meitou's NPC towns have no running machines, so both are
  the same standing at the building at its functionality's clip (`stonemine` by default): **engine choice**, the building's `use range` is not read (30 units).
- **Obedient Slave** (187, flags 0x75, no lists; **Observed**, `FUN_14050e5b0`): a job whose type is 0xbb is not scored like the others. For a character in slave state 1 that is not
  sneaking (`Character::isStealthMode` `FUN_1405c9750`) and whose owner handle (`+0x328` of the slave's AI owner object) names a character (type 1) or a squad (0x5b), the job loads the owner platoon's
  Slave role list (role 4, tiers mask 0xe: `FUN_14026b790`) into the slave's own goal list (`+0x178`) and scores that with the normal scorer `FUN_14050df10`. So a slave runs its owner's
  `Slave AI Goals`. Meitou: a member of its owner's squad in the Slave role does ([ai](ai.md#goal-loading)); a slave in another squad takes the Slave list of the squad it obeys
  (`CharacterCold.SlaveMaster`: the owner squad of the squad factory's slaves, the slaver who bought or processed it, the arresting guard's squad, the receiver of a delivery;
  runtime only, so a loaded slave obeys nobody until enslaved again). A faction owner with no squad, and player-faction slaves (the player drives them), are not modelled.
  The status tooltip tells the player: "Your character will have a permanent job assigned called [Obedient slave]. While this is active he will automatically work for his master."
- **Shout** (188, `Task_TalktoPlayer`, slots 1 and 2 `FUN_14034c5a0` / `FUN_14034c7c0`): the event is the row's (TaskData `+0x130`: **Verified** 0x26 for 188, 0x27 for 194 and 218, 9 for 158 and 283).
  Start fires it at the target (`FUN_140684990`); each update walks to the target (vfunc `0x3b0`), repeats event 9, gives up after 25 minutes with event 60, and ends when the dialogue is free.
  The slavers' event-38 lines ("shout at slave worker": chance temporary 10, six lines) are in "slaver guard MAIN" and "Holy Nation Paladin". Meitou paces it: a worker shouts at most every 60 to 180 game seconds.
- **Capture** (167): the planner chain is 167 (effect isChained 134) <- 166 Restraining (`Task_CuffTarget`; needs 38, at location, 118 TARGET_IS_UNARMED, 7 target down) <- 181 Stripping weapons
  (`Task_StripWeapons`; effect 118). **Strip** (**Observed**, `FUN_14034e7b0`): `medic` for 0.8 s, then the items of the target's `hip` and `back` sections (`FUN_140747ff0`) go to the
  stripper's inventory (else its storage), and the stripper's squad sets short-term tag 6 on the target (`FUN_1406744a0`; the NPC looting finder skips tag-6 targets, [carry-loot](carry-loot.md#33-npc-looting)). **Cuff** (`FUN_14034af20`): a "Restraining" bar
  with `medic`, progress += 0.1 x a per-update float of the character (`+0xc4`, read as frame time: about 10 s, **Unknown**), full at 0.99: the cuffer says event 37 RECAPTURED_A_SLAVE to the
  target (its lines "new slave capture" / "slave recapture", no effects) and posts message type 5 to the global list `DAT_1421346d8`. The shackles themselves are not put on anywhere in the task
  (**Unknown** where; Meitou puts the stock `SHACKLES` on). The list's only reader, `FUN_140a0af10` (via `DAT_1421346d0`), sums per-listener contributions around a point: no game logic
  was found for type 5, which `Task_GetSlave` (174) posts too. Carrying the cuffed one to a cage is goal 112 (not decoded); 168 lifts a downed slave (`Task_LiftSomething`, effect 115) before 112.
  The packages that list 167 and 168 are slaver, guard and workmaster packages ("addon- slave catching", "wandering slaver base", "slave worker day guard", the Rebirth and Holy Nation guards).
- **Looks like a slave**: processing (182) makes state 120 TARGET_LOOKS_LIKE_A_SLAVE true. The watchers' "needs processing" test `FUN_1408ca3d0`: a character with no owner needs it; one with an owner of an
  allied faction needs it unless its slave state is not 2 or 3 and vfunc `0x3e0` is true and `FUN_1405a6bb0(platoon, character)` holds. That function (**Observed**, outline): an animal is fine, NPC
  class 1 or 5 is not; otherwise the torso and leg slots (inventory sections 5 and 6) must be empty, shackles or items of the owner faction's `slave clothing` list, and the shackle slot (section 9) must hold a locked item.
  Meitou's reading (**engine choice**): wearing locked shackles or any of the owner's slave clothing; an owner with none always passes.
- **Cage memory**: `Task_PutInSomething` for a cage (`FUN_140358670`) makes the prisoner's squad remember the putter as captor (`FUN_140673a20(prisoner, putter, 5)`) and calls `FUN_14067aca0`, which
  adds the prisoner to the putter's, its allies' and its squad members' platoon lists (the "slave list"). [Squad memory](dialogue.md#squad-memory) has the other writers.

## Desire and commitment

**Observed** (TaskData constructor `FUN_14061d350`, registrations in `FUN_140319a60` parsed for all 253 rows, scorer `FUN_14050df10`, `AITaskSytem`
vfunc 5 `FUN_14050caf0`). TaskData fields: +0x108 desire (a member function, nearly always a virtual of the `AI` object: `mov rax,[rcx]; jmp [rax+slot*8]`);
+0x160 target finder (returns a factor, 0 when it found nothing; the desire is multiplied by it when the row has an effect, `FUN_14060fd80`);
+0x18 / +0x1c commitment minimum / random range in game hours; +0x20 commits; +0x34 crowd factor (default 1); +0x3c is read by the senses
(`FUN_1408529c0`, meaning **Unknown**).

- Score = desire x goal weight x crowd^n (n squad mates on the same type and target); x1.25 for the current task. For a committing current task the
  score is at least 0.4 until the commitment ends and at most 0.05 after it, so the character moves on.
- Commitment: when a committing task becomes current, end = now + min + rand x range. A candidate that is not the current task has its target cleared
  before it is chosen, so a newly chosen goal gets a fresh target from its finder.
- Desires (`AI` vtable slots): 19 = 1, 39 = 0.1 (Idleness), 40 = 0.25 (Patrolling, Patrolling town), 41 = 0.5 (Stand at node, Shopkeeper, Static guard,
  general node), 48 = 0.01 + 0.4 x rand (Wandering town, Staying home, Sitting around, Static town guard, Shopping 119). 125 rows desire 1; 102 call a
  function of their own (sleeping, following, retreating, turrets, Shopping 118: 1 until the shopping list is done).

| Type | Commitment (h) | Crowd |
|---|---|---|
| 37 Wandering town, 118 Shopping | 0.25 + r x 1.5 | 1 |
| 43 Staying home, 255 Reigning, 275 Sitting around | 0.2 + r x 0.8 | 1 |
| 38, 268 Static guard | 0.1 + r x 0.5 | 1 |
| 157 Static town guard | 0.1 + r x 1 | 1 |
| 47 general node | 0.03 + r x 0.1 | 1 |
| 153, 239 Going to bed | 2 + r x 8 | 1 |
| 190 Worker, 221 Operating machine | 0.5 + r x 2 | 1 |
| 240 Capture Target | 0.05 + r x 0.5 | 1 |
| 248 Attacking Squad | 0.3 + r x 2 | 1 |
| 45 Bodyguard | - | 0.9 |
| 57, 58, 125, 237, 250 | - | 0.8 |

## Planner

**Observed** (class `TaskPathfinder`: search `FUN_14050f800`, entry `FUN_14050fe10`, pick `FUN_140510650`, node expansion `FUN_14051bae0`, provider maps
`FUN_14060fe30`, "can run" `FUN_14060f940`). Meitou: [simulation](../simulation.md#planner).

- **Row fields** (builder `FUN_140319a60`, its disassembly read per registration; the two bools **Verified** against a second parse of the same
  calls). *Acts* (+0x134): the registration's first bool, cleared when an effect is set; rows with neither acts nor an effect: 104, 91, 89, 202, 14,
  59, 192, 17, 45, 187, 169, 42, 39, 203, 254. *Needs target* (+0x48), the second bool: 1, 29, 67, 82, 163, 121, 123, 290, 272, 87, 221, 152, 124,
  114, 242, 92, 247, 284, 115, 93, 94, 113, 91, 88, 72, 73, 77, 78, 140, 79, 162, 198, 80, 287, 200, 76, 201, 202, 185, 285, 186, 286, 81, 226, 95,
  213, 240, 225, 246, 68, 170, 2, 96, 71, 3, 25, 249, 60, 236, 244, 26, 241, 4, 262, 263, 266, 228, 227, 13, 146, 235, 101, 248, 165, 103, 129,
  159, 161, 12, 102, 164, 17, 45, 44, 253, 40, 31, 289, 166, 181, 182, 174, 30, 51, 119, 55, 5, 61, 97, 98, 258, 99, 106, 100, 134, 231, 220, 196,
  136, 137, 108, 112, 281, 110, 203. *Target finder* (+0x160): 122, 270, 189, 190, 152, 114, 242, 184, 115, 94, 113, 90, 84, 85, 195, 155, 197, 86,
  216, 125, 58, 250, 57, 237, 131, 259, 277, 279, 243, 8, 177, 212, 238, 19, 13, 34, 149, 222, 234, 101, 248, 188, 194, 218, 158, 283, 16, 41, 21,
  144, 32, 183, 260, 167, 173, 217, 245, 168, 15, 288, 143, 20, 38, 268, 157, 47, 37, 118, 214, 43, 255, 275, 24, 55, 153, 239, 105, 148, 106, 142,
  135, 139, 282, 230, 233, 232, 138, 137, 112, 281, 172, 211, 276, 204, 223, 273, 132. *Not a provider* (+0x40): 29, 186, 286, 226, 213, 240, 225, 246,
  263, 266, 228, 235, 44, 253, 5, 61, 97, 258. *Restricted* (+0x41): 122, 270, 140, 76, 202, 185, 285. *Keeps the queue* (+0x23): 87, 221. +0x22
  (28, 65, 147): **Unknown**.
- **Providers**: six registries, one per category flag bit (1, 2, 4, 0x10, 0x20, 0x40), each map a state and its polarity to the rows of that category,
  not +0x40, whose list A holds it, in ascending task type (filled from the registry's map keyed by type, `FUN_14060fe30`, **Observed**). Which registry a character plans with is **Unknown** (rows only flagged 0x20 are dog play and animal attacks).
- **Can run** (memoised per row, target and effect target): the first B entry that does not hold is reported unmet, false; else true without an effect;
  with one, the effect target (from the finder when asked), false without one; true when the effect holds, else the effect is reported unmet.
- **Search**: a node is (row, target hand, position), with value g = h = the row's desire when made; a node whose desire is not above 0 is a dead end
  (a root one fails the plan). A root without a finder that can run is the plan by itself. Otherwise, while open nodes remain, take the one with the
  **highest** g + h, then accept it when it equals a task already queued or repeats one of its ancestors (up to 50; rows 1 and 67 never count), or
  when it has no finder, acts and can run. Else expand it: children for the effect (target: the effect target its finder found) and for every B
  entry that does not hold (target: its own), one child per provider; a restricted provider only when the search is flagged (which caller sets the
  flag is **Unknown**) or the character has no squad; a needs-target provider only when a target is known; a state without one gets a dead end
  (row 104 "null"). The children followed are those of the first unmet B entry in list order, else the effect's. A child not yet seen gets
  g = min(its g, the node's g) and the node as parent; one already closed is accepted from there. A node repeated 4 times is reused, not made again.
- **Result**: the accepted node's chain up to the root (stopping at a queued task) is the plan. Its value is the least desire on it (from 10); above 0,
  every node that acts or has an effect becomes a task (factory) in a new queue, leaf first. A failed plan clears the queue unless the root keeps it.
- **Pick**: candidates are tried by key, highest first (a taken key is raised by 0.0001 until free, so a tie goes to the later-listed goal; **Verified**, `FUN_14050df10`); the first whose plan has a value above 0 becomes current, a candidate whose plan fails has its
  target cleared and the next is tried.
- **Source order** (`AITaskSytem::chooseGoal` `FUN_140510ef0`, KenshiLib names, **Observed**): player orders (OrdersReceiver +0x38, `checkOrders`), jobs,
  urgent perma-jobs, priority-3 goals, other perma-jobs, priority-2 then priority-1 goals, the hold-position task; the first source that plans wins.
  Goals sit in per-priority lists (`taskPriority`: 1 fluff, 2 non-urgent, 3 urgent, 4 obedience). Orders are queued at priority 3. Consequences for
  fights: [combat](combat.md#fight-lifecycle-and-orders).

## Combat goals

**Verified** (registrations in `FUN_140319a60`, finder/desire stubs resolved by disassembly); readings of the finders' tests **Observed**.

- 16 ATTACK_ENEMIES: effect state 6 `isNoEnemiesInVicinity` (registered without an evaluator). Finder `FUN_14059f3a0` walks the
  looker's sensed characters tagged SENSE_ENEMY and not KO, ally or 0x300 (mask 4, 0x308; [sense tags](perception.md#sense-tags-who-is-an-enemy))
  that are seen or heard (entry +0x38 / +0x39), drops those whose faction the looker's coexists with (`FUN_1406b23c0`) and keeps those passing `FUN_140598630` and `FUN_140599f30` (the post
  and area tests, [perception](perception.md#the-attack-on-sight-tests)); `FUN_14059e300` picks the target. Factor 1 with a target, else 0. This is the attack on sight.
- 144 TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME ("Home territory aggression", finder `FUN_14059fd70`, **Verified** stub): SENSE_ENEMY
  entries (mask 4, 0x208) within 1.3 x the home town's radius (`FUN_140926d50`), skipping a slave whose owner's faction is an ally; with no
  home town it is `FUN_14059fa70` (the same mask). 21 ATTACK_ENEMIES_AND_NEUTRALS (`FUN_14059f720`) takes mask 6 (neutral or enemy, 0x308).
- 143 PROTECT_OWN_SQUAD ("Protect close allies"): effect `!isAggressionTowards` (state 16, evaluator `FUN_14059de10`: the target
  resolves to a character whose AI-side object is set and byte +0x2b0 is set, meaning **Unknown**). Finder `FUN_14099d510` scores
  each squad mate the looker senses by `FUN_14099a190`, nonzero only while that mate's AI holds a character target (+0x200 = 1,
  hand +0x208); else it falls back to a hand list at +0x2f8. Achieved by 13 "Attacking attackers of" (list A `!16, !39, !12*`).
- 15 / 288 PROTECT_ALLIES ("Protect allies", "protect allies - dont leave town"; finder `FUN_14099ca10` at +0x160 for both, **Verified** registration):
  the same shape as 143 over the sensed entries tagged SENSE_ALLY (mask 1, 0x208; same faction or relation >= 50) that are seen or heard, scored by
  `FUN_14099a190` (nonzero while that ally's AI holds a target). An ally of another faction counts for an NPC looker; only a player-faction looker
  needs the global +0x111 and squares the weight ([HelpAllies](../simulation.md#hostility-and-relations)). Achieved by 13 "Attacking attackers of"
  (`FUN_14099c3b0`, **Observed**): the protected one's attackers that it or the looker senses, within 150 of it (outdoors), are taken when either regards
  the other an enemy (slot 125) or the attacker's AI +0x2a4 equals the goal target's id (meaning **Unknown**, read as "it is attacking the protected one"),
  and never one the looker regards an ally (slot 126). On that reading relations do not matter (a neutral attacking an ally is fought), which agrees with
  the owner's report that the original's guards fight skimmers. Gate guards (`Gate guard -no turrets`
  and those inheriting it) list 15, `Gate guard stay in town -no turrets` 288 (**Verified**, package dump).
- 32 SELF_PRESERVATION: every character's urgent goal (`Character::setupAI`); desire AI slot 17, provider 13 (finder `AI::findAttackerOf`) or 35;
  rows 4 / 262 desires are slots 53 / 54. Details: [combat](combat.md#fight-lifecycle-and-orders).
- So a squad with PROTECT goals and no ATTACK goal starts no fight with an idle enemy (**Observed**): e.g. the base `Fog prince squad`
  (`42029-rebirth.mod`, package `fog prince`); its dialogue package `fogman` answers only events 52 and 53 and has no effects.

## Town goals

**Observed** (target finders named; TaskData effects lead the planner to the task that acts them out):

- **37 Wandering town** (`FUN_140999a60`): keeps a target that is still a general node; else one building drawn uniformly from the **public**
  buildings of the character's town in the zone it stands in (`FUN_1409fc6c0` over `FUN_1409fbb00`: the zone's building list, Building vfunc 82, which
  returns +0xc8, the `public day` flag, see [locks](locks.md)), then a random node of type 1 (NODE_GENERAL) in it whose owner is unset or the
  character's faction (`FUN_14038c2f0`); none (no public building, or the drawn one has no such node): 0. Effect `atNode`, acted out by 51 Stand at
  node. Vfunc 82 is false for some building kinds (1, 0x22) under a player-side condition (**Unknown**); the flag's default on buildings without a
  resident template is **Unknown**.
- **47 general node / 20 Shopkeeper** (`FUN_14099f280` / `FUN_14099e920`): a node of that type in the home building (owner unset or own faction),
  reserved; guard posts fall back to general nodes. A node marker faces along its local −X: for 784 of 990 shopkeeper nodes in base-game town interiors,
  yaw − 90° points at the nearest Shop Counter's side the node stands on (**Verified**, layouts placed by `TownBuildings`, compared with each counter's
  yaw; the rest are corner or odd-angled counters). The engine turns a character arriving at a node that way.
- **118 Shopping** (`FUN_1409997f0`): a shop of the town selling an item on the character's shopping list (`FUN_1409fc070`), else a random shop
  (`FUN_1409fc1b0`); factor 1 for a BF_SHOP building, 0.4 otherwise. Then 119 (`Task_Shopping`) at it. Both finders scan the zone's
  BF_SHELL_WITH_INTERIOR buildings; the random one keeps those of the character's town that are public (Building vfunc 82, the `public day`
  flag), the list one those that are shops (vfunc 87, `Building::isAShop`: BF_SHOP furniture inside, residents, and public) and stock the item.
  No faction, membership or hostility test (**Observed**, decompile), so a guild's public home with a counter (the Hub's Thieves Guild
  Watchtower, `public day` true) draws the town's shoppers.
- **214 Relaxing** (`FUN_140998d10`): the current package's own target (package vfunc 1); none: 0. Effect `IS_WORKING`, acted out by 87 Operating
  machine, i.e. using furniture there; with no free one the plan fails and another goal wins.
- **Hang-around-bar package** (`Package_HangAroundBar`, start vfunc 6; **Observed**): its town is the squad's home town, else the nearest friendly one
  (`FUN_1402916b0`); only while that town's zone is loaded. The town's buildings of designation 3 (BD_BAR, the residents' template's
  `building designation`; town vfunc `0x2d0`) are scanned cyclically from a random one; the first with a free **table** wins, that table is
  reserved for the squad in a global table→squad map (`DAT_14212ddb8`) and becomes the package's target. A town with no bar takes a free table
  outdoors in it; all bars full: no target. The table search (`FUN_140292de0`, `BuildingFinder_TableToSitAt` vfunc 5) keeps furniture of
  function mask 0x10810 (BF_REFINERY, BF_CORPSE_DISPOSAL, BF_TABLE) in the town, of a faction not hostile to the squad's, inside the bar (or,
  with no bar, inside no building), in a public building (vfunc 82) and not reserved. The outdoors variant (flag +0x151, BarOutdoors) skips the
  bars and takes a free outdoor table. So a bar holds as many visiting squads as it has free tables.
- **36 Patrolling town** (`Task_PatrolTown` slots 1-3 `FUN_140336370` / `FUN_1403364b0` / `FUN_1403365b0`, **Observed**, decompile): row 36 has no finder or
  effect (registration in `FUN_140319a60`) and no commitment. Start, update and resume pass modes 0/1/2 to the mover `FUN_1405c6b30` with the task target's position
  and, while the task has a target (task vfunc `0x40`), the platoon's town: the blackboard's town hand (`FUN_140269cd0`: the contract's target, else the campaign's
  when it is a town, else the blackboard's town, else the platoon's home town) when it resolves to a town (`FUN_1409f9100`). With a town the move is an area order
  (order mode 2) on the town's position with scatter max(500, 0.5 x the town's radius, Town vfunc 84); without one, on the target's position with scatter 1250.
  `setPatrolInput` draws the destination within ±scatter/2 in X and Z (redraws: [patrol moves](factions-squads-towns.md#off-screen-world)); the update re-sends
  the order only when the mover reports idle, reached or failed. So a patroller strolls from point to point round its town's centre and never stands still for long.
  What the task's own target is for a goal with no finder is **Unknown** (only the townless case reads it).
  Meitou (`AiSystem.Patrol`, `PatrolPoint`): the town's centre ± scatter/2 with the scatter above, the first of 10 draws on walkable ground (the building and water
  redraws are not modelled), a new draw each time a walk is over; wanted only with a town (the townless walk is not built); an occupier walks among the
  complete buildings of the player town it holds (`AiSystem.OccupiedSpot`, engine choice).
- Building layouts' `is bar` / `is shop` are 0 on every exterior layout of the base game and mods checked (2 interior layouts set `is bar`, 9 `is shop`),
  so they do not mark bars and shops (**Verified** over the merged `interiors.level`).
- **43 Staying home** (`FUN_1408cbc00`): furniture in the home whose BuildingFunction is in mask 0x2049a (bits MINE, RESEARCH, REFINERY, TRAINING,
  CRAFTING, CHAIR; reading the mask as bits is **Observed**); **275 Sitting around** (`FUN_1408caf00`): only in the home town, furniture of mask
  0x1020080 (TRAINING, CHAIR, THRONE) not claimed by others. Both lead to 221 (pretend operating).
- Chairs (**Verified**, BUILDING_FUNCTIONALITY records of gamedata.base / rebirth.mod / the install's mods, dumped): BuildingFunction 17 BF_CHAIR
  `chair`, `chair-fixed`, `chair-fixed 180` (animation "sitting chair"; Stool, Old Stool, Bench, Sitting Box) and `sit on floor (no chair)`
  ("sitting idle"; Sitting Pillow, Sitting spot (invisible)); 24 BF_THRONE `throne chair` ("sitting chair"); all `max operators` 1.
- Sitting (**Observed**, decompile and disassembly of `Task_OperateMachine` slots 1 `FUN_14035b430` and 2 `FUN_14035bc90`): for a BF_CHAIR or BF_THRONE
  the sitter's body is put on the seat building's own position (vtable `+0x40`, its node origin, height included) with no offset, then turned to a look
  point; what the seat's float at `+0xa4` (vtable `+0x60`) passed alongside does is **Unknown**. The look point starts as position + orientation × +Z (`UNIT_Z`):
  a seat faces its +Z.
- Seat facing (same source): a BF_CHAIR's functionality `flags` picks the look point. 0 (`chair-fixed`,
  `throne chair`) the seat's own facing; 1 (`chair`: Stool, Old Stool, Sitting Box; `sit on floor`) the nearest building of function 16 BF_TABLE,
  11 BF_CORPSE_DISPOSAL or 4 BF_REFINERY (the seat's parent building's furniture, else ones near the sitter); 2 (`chair-fixed 180`: Bench) the seat's
  facing, turned round when that nearest one is behind it. With none found, the seat's facing. Layout stool rotations are arbitrary (Observed: the Hub's
  Storm House, up to 161° off their table).
- **24 Wandering** (`FUN_140998040`): the nearest town; list A holds `atATown`, so it is dropped while in a town.
- **180 Man the gate** (`Task_ManTheGate` slot 2 `FUN_140342c50`, target `FUN_140338690`; **Observed**, disassembly): the gate is the
  task's target, else the Blackboard's home (`HOME_GATE`), else the town's gate nearest the character (town vfunc `0x308`), each only when it
  is a GatewayBuilding (vfunc `0x3d8`). The guard walks to its first door's point `+0x3bc` and turns along `+0x3bc - +0x3c8`. A door's
  two points (`FUN_14029cc90`) are its leaf's centre ± 15 (constant `0x14168a258`) along the leaf's thin axis, dropped to the ground;
  which side `+0x3bc` is depends on a door body offset (**Unknown**). Gate BUILDING records have no nodes (**Verified**, the merged
  load order's Defensive Gates II-V), so the squad's guard node goals find nothing there.

## Perma-jobs

A character's job list (the Orders menu's "Perma-Jobs"; class `OrdersReceiver`, base of `AITaskSytem`). Meitou: [simulation](../simulation.md#production).

- **Lists** (**Verified**, add function `FUN_1405086d0`, constructor `FUN_140507d20`): `OrdersReceiver` +0x70 holds one-shot orders, +0x88 (count +0x90,
  data +0x98) the perma-jobs in priority order, +0x35 the jobs switch. Each entry is a Tasker: +0x10 target hand, +0x70 its TaskData (+0x44 task type).
- **Adding** (**Verified**, `FUN_1405086d0(orders, type, target, position, permanent)`; Shift with an order, tutorial `FUN_14098d8c0`):
  1. TaskData row +4 set and +0xc set: the type becomes +0xc (orders too).
  2. Not permanent, or the resulting row's +4 is 0: appended to the order list.
  3. Else refused when a job of that type exists and the row's +8 is 0, or when one exists with the same target. Otherwise appended, the jobs
     switch turns on (`FUN_140508210`, which also posts a UI event for a player character), and +0x10, when set, is appended as a second job on the
     same target with no checks (0x1f becomes 0x121 when character vfunc +0x248 returns non-null; that test is **Unknown**). Type 45 also stores
     the target at +0x18.
  4. The planner is re-run (vfunc +0x18).
- **Ordering** (**Observed**, order dispatcher `FUN_1407fa650`): with Shift held (`DAT_142133449`) an order goes to `FUN_1407f5a90`, which adds it
  as a job to each selected character, then posts the Wwise event `Permanent_Job`; without Shift it is a one-shot order. Orders 0x2d, 0x111 and 0xdf
  are always added as jobs; Loot with Shift on a building adds 124 "Hauling to". The Medic and Rescue buttons dispatch 58 and 148 the same way:
  one-shot, or list entries with Shift.
- **Row fields** (**Verified**, `FUN_140319a60`; the 36 rows that set +4, all others leave it 0, **Observed**). +4 kind (2 = turret jobs), +8 several
  targets allowed, +0xc job type, +0x10 companion:

  | Ordered → job (+ companion) | +4 | +8 |
  |---|---|---|
  | 87 → 87, 221 → 221, 124 → 124, 97 → 97 | 1 | 1 |
  | 152 → 152 + 124 | 1 | 1 |
  | 2, 95 → 125; 25 → 58; 249 → 250; 60 → 57; 26 → 238; 4, 5, 61 → 16 | 1 | 0 |
  | 146 → 234 | 2 | 0 (234: 1) |
  | 45 → 45 + 31 (31, 289 rows: +8 1) | 1 | 1 |
  | 148 → 148 + 106; 223 → 223 + 224; 273 → 273 + 274 | 1 | 0 |
  | 136, 137 → 137 + 138 | 1 | 1 |
  | 98, 175, 261 → 153 (153 has no +4: the bed order stays one-shot) | 1 | 0 |
  | 58, 250, 57, 238, 16, 125, 106, 138, 187 (plain jobs) | 1 | 0 |

- **Choosing** (**Verified**, decision pass `FUN_140510ef0`, job scoring `FUN_14050e5b0`, pick `FUN_140510650`): order list → jobs of kind 2 while
  the AI flag at +0xbc is set → urgent tier (3) → all jobs → tiers 2, 1 → idle. Within a job pass each job considered gets the key N+8, N+7, ...
  (N = list length) in list order, kept when its desire is above 0. The pick takes the highest key above 0. **So the first job that can run wins,
  and a higher job takes over as soon as it can run** at the next re-plan. A job is skipped when: it has no TaskData; the switch is off and its
  type is not 16, 31, 45, 57, 58 or 250 (`FUN_14050c1c0`: these run regardless, so the medic job ignores the switch and rescue does not); the
  planner finds an unmet list-B state and the row has no effect (row +0x14 clear; `FUN_14060f940`); its list A holds (done; `FUN_14060f570`,
  `FUN_14050c8e0`); or desire × target finder is 0. Type 187 (Obedient Slave) has a slave-state branch of its own.
- **Range** (**Verified** by decompile of `FUN_1405a3370`, `FUN_140599f30`, `FUN_140597790`, `AI::AI` `FUN_1406236d0`; names KenshiLib 1ce4a16,
  **Observed**): a job's walk is planned through rows 1 Moving / 67 Running, whose list B needs 27 `isMovementAllowed`, so a job whose target fails
  it cannot be planned and is skipped. One-shot orders walk through 29 Move order, which lacks 27, so they reach any distance.
  `AI::isMovementAllowed`: false while the character's +0x278 byte is set (KenshiLib `isCurrentlyGettingUp`); a target object goes to `isTargetInRangeForOrders(RootObject*)`:
  true when the target's `getCurrentTownLocation` is the platoon's `Ownerships::_homeTown` (for the player faction **Unknown**, see 223 below), else
  the position test below, else true for a character of type 1 that is attacking (`CombatClass::isAttacking` in (0, 1)). Position test
  `isTargetInRangeForOrders(Vector3)` by `AI::ordersMovement` (+0x2bc, `AIManuverabilityOrders`; the constructor sets 2): zero vector → true;
  2 ROAM_FAR: the 3D distance² from the character itself < 4.246733e7 (≈ 6517; 12.5 × the medic nearness 3397386.5); 1 STAY_CLOSE (standing order
  NEAR, the centre set to the character's position then): from `centerOfMovement` (+0x2c0) < `meiDistance` (+8) + 200 + `meiDistanceTolerance` (+0xc);
  0 HOLD_GROUND: + 40 instead, with an extra term **Unknown**. Other values: false. Standing order FAR sets ROAM_FAR (EDX = 2 in the disassembly) and clears the centre target.
- **Implicit jobs** (**Verified**, `FUN_14050e5b0` after the list, only for a character with a squad (+0x250), the switch on and not sneaking
  (`FUN_1405c9750`)): lowest keys for the squad AI options at `DAT_142134690` (the overview's AI tab), each kept when its task's desire is above 0:
  +0x117 "Ditch items" → 242 "Ditching resources" (only with an empty task queue +0x60); then, unless three tests hold (`FUN_1402b2b90` on the
  character's +0x450, its byte +0x348, `FUN_1407f4d20`, a set lookup; meanings **Unknown**), +0x116 "Sleep when injured" → 257 "Getting out of
  bed once healed" above 153 "Go home and go to bed", and +0x118 "Sit when idle" → 275 "Sitting around" (its task is built with a float 0.5 where the others pass 1, meaning **Unknown**).
  Tooltips (**Verified**, `FUN_140005e11`): ditch puts excess carried materials into storage when idle; sleep goes to bed when injured and idle;
  sit takes chairs and benches at home. The injury threshold of 153 is **Unknown**; its target finder is the bed search (**Verified**, [locks](locks.md#beds)). Saved as CAMERA bools ([save](../formats/save.md)).
  The other options of the panel: [Squad AI options](#squad-ai-options). Feed and share: [eating](character-stats.md#eating).
- **Save** (**Verified**, `OrdersReceiver` vfunc 1 / 2; 4122 GAMESTATE_AI records of the owner's saves, 199 jobs, types 187 and 57 only): record
  `ai` (GAMESTATE_AI); bool `jobs` = the switch; per job i from 0, int `pjob<i>` (type) and handle `pjobT<i>`. The loader re-adds each job
  through the add function (so companions and the switch are re-run) until `pjob<i>` is missing, then reads `jobs`. `AITaskSytem` vfunc 1 / 2
  also saves the task queue (+0x40 deque): `tjob<i>` type, vec3 `tjobP<i>`, handle `tjobT<i>`. The GAMESTATE_CHARACTER bool `chase mode` is the
  same switch (**Verified**: the writer `FUN_1406280a0` stores the byte +0x35 of the orders receiver at character +0x650 → +0x20; the loader
  `FUN_140626f50` passes it back as toggle 15); the orders panel's `OrdersChaseButton` (captioned JOBS) is this switch.
- **Finders of the jobs** (**Verified** registrations in `FUN_140319a60`, finder thunks resolved; readings **Observed**; "sensed" = the
  character's sensed list `FUN_140853960(include, exclude)`, whose bits are **Unknown**):
  - 58 Medic / 57 Robotics: `FUN_14099f690` with a flag (58: 0, 57: 1; [Medic jobs](#medic-jobs)). Desire `FUN_140998410` (both, and 250).
  - 250 Splint Rigging: `FUN_14099e240` ([Medic jobs](#medic-jobs)).
  - 16 Attacking enemies: `FUN_14059f3a0` ([Combat goals](#combat-goals)); it searches only while the AI's +0xbc (read as the count of sensed
    enemies) is nonzero, which is also the condition of the kind-2 job pass above.
  - 238 Foraging animals: `FUN_1408ce3a0`: the first sensed (0x18, 1) character that is down (`FUN_1407d1440`: carried, or ragdoll flags), an animal
    (vfunc 0x248), not carried, passes a character vfunc 0x3e8 test or has faction flag +0x1d0 (**Unknown**), is in no give-up list, and holds an
    item `FUN_140998950` accepts (inventory, then backpack). Effect 148 → looting.
  - 223 Remove Home Intruder: `FUN_14099c110`: the first sensed (0x400e, 0x201) character, not carried, inside a building whose handle equals the home building hand of the character's
    Ownerships (+0x38; `Hand` vfunc 0x10 is "not equal", so the test `== 0` means equal; for the player faction the Ownerships is one shared object whose home is **Unknown**), down (vfunc 0x30), special state
    (+0x2f8) 0, i.e. not in a bed or cage. 273 Remove Intruder: `FUN_14099c250` (**Verified** by decompile; slot names KenshiLib 1ce4a16, **Observed**): only while the character's current town
    (vfunc 0x78 `getCurrentTownLocation`) passes town vfunc 0x2f0 (`Town`: +0x3b0 set, meaning **Unknown**; `TownBase`: false) and `FUN_1406211e0` (`Ownerships::isMyTown`: the town is
    `_homeTown` +0x30, `occupiedTown` +0x80, or of the Ownerships' faction +0x70); sensed (0x400e, 0x201) in the same town, not carried, unconscious (vfunc 0x30), special state not 2 (not
    caged), `amInsideTownWalls` (vfunc 0x118) > 0 (inside a building: that building's value, a gate by the side it stands; outdoors the gate code the [gate-code pass](pathfinding.md#gate-codes) gives it).
    So an occupier removes intruders from the town it holds. Their companions 224 / 274 carry the body outside: `Task_TakeIntruderOutside` (**Verified** by decompile, slots 2/5/7 `FUN_1403355e0`/`FUN_14033da80`/`FUN_1403356e0`) walks
    to the point beyond the home building's first door (`Building` vfunc 0x3e0 → door +0x3bc, pushed out by a factor 1.1 of a reference vector, **Unknown**); its start (slot 2) ends the task
    without a home building and drops at once when nothing is carried; its update (slot 7) drops the body (`FUN_1405ce1e0`) once the walk is over or 5 game minutes (elapsed hours x 60 >= 5,
    **Observed**) have passed. `Task_TakeIntruderOutsideTown` walks to the town's exit point (`Town` vfunc 0x38 `FUN_140927120`, a cached point refreshed on a timer: 200 beyond a door of one of its
    buildings through vfunc 0x290 `FUN_140929560`, else beyond the door of a building `FUN_1409b2ab0` finds; only partly read, **Observed**) and drops it there.
  - 138 Find corpses: `FUN_1408cfb00`: a building of function 8 (cage) of the character's (`FUN_1408cbea0` mask 0x100) holding a dead,
    non-robot body not in a give-up list, else `FUN_1408ce170` (the nearest dead uncarried body: sensed (0x10, 0), down `FUN_1407d1440`, not carried, a flag at +0x5bc, not in a give-up list; for
    an animal finder only other races). 137 Corpse disposal: `FUN_1408d1090` → `FUN_1408d0770`(1, 0xb): the building of function 11 (BF_CORPSE_DISPOSAL, the Cooking pot / Corpse disposal
    functionalities of `Four Cadavres` and the rebirth shredder, **Verified** data) among the character's buildings whose `UseableStuff` vfunc 0x4f0 holds, weight clamp(1 - d^2 / 2.097e7, 1e-5, 1) (`FUN_1409b2380`),
    x100 for the character's faction (else it must pass the ally test `FUN_140621230`), x100 when it stands in the home building, x1e-5 for a player-type squad member when the parent building's vfunc 0x290 is false;
    the heaviest wins. At the machine `Task_FeedCorpseIntoMachine` (136; slot 3 `FUN_1403567a0`, **Verified**) lets go of the body and kills/removes it ("incinerated or cooked"), plays `Armor_Worn` and
    `Character_Drop_Person` and floats the functionality's `string` over the machine ([corpse machine label](buildings-production.md#classes-and-functions)).
  - 45 Bodyguard, 31 Staying close and 97 Training have no finder: they act on the job's own target; 45 and 31 are goals (Task_Blank, rows above) on the job's character, the context menu gives
    them as "Follow" (31) and "Bodyguard" (45), the dispatcher always adds 45 as a job. 97 is `Task_UseTrainingDummy` (slots 1-3 `FUN_1403513a0`, `FUN_14035c1e0`, `FUN_140345570`; **Verified** by
    decompile): the dummy (function 7, BF_TRAINING; the animation event `Impact Dummy`, a "Training" progress bar for the player's characters) is operated through `UseableStuff` vfunc 0x4b0: with a
    positive `stat used` (+0x3f8) it calls the continuous XP entry with a cap, `FUN_1408c64f0`(stats, dt, 2.0, &stat, cap = the float +0x1a4, stat number): nothing once the stat is at the cap, else the
    same gain as the operating XP (`0.02 x dt x exp gain mult x 2.0 x race[stat]`) through the core curve with that cap, ((cap - stat) / cap)^2, so the stat approaches the cap without reaching it.
    The cap is **Observed** to be the BUILDING's `output rate` (MkI 6, II 11, III 16; lock 11/21/31; assassin 10/20/30; the descriptions promise levels 5/10/15, the displayed level being the whole part);
    the field +0x1a4 itself was not traced to the record. The bar shows the stat's fraction (+0x3a4). Meitou reads it as the stat's fractional part (`TrainBar`; **Observed**). The row's conditions are 38 `hasOneWorkingArm`, 73, `isAtLocation`.
    The XP is the user's own stat, not the injured one. Meitou: [simulation](../simulation.md#job-lists).
- **UI** (strings, `FUN_14072b540` JobsPanel): "No jobs assigned", "To add jobs, hold shift key when giving an order", the Jobs toggle "If this is
  disabled then the character won't do any of the jobs listed."; tutorial: jobs are reordered by dragging and removed with X. Row text: **Unknown**.
- **Unknown:** what list A/B mean per job type in detail (operating 87: A `IS_WORKING`; B free slot, power, inputs not
  empty, output not full, at the machine); how 124 "Hauling to" gets the items it needs (B 67 `haveSomeResourcesFor`; no row's effect is 67).

### Medic jobs

**Verified** (decompile of `FUN_14099f690` (Medic 58 passes 0, Robotics 57 passes 1, and the NPC `Medic` goal), `FUN_140998200` (need), `FUN_140998b90` (nearness), `FUN_140998410` (desire),
`FUN_14099e240` (splint finder), `FUN_140646400`; constants read from the exe). Names from KenshiLib 1ce4a16: `Blackboard::howManyGuysWorkingOnThisSubTarget` (`FUN_1402690e0`), `isMySubTarget` (`FUN_140269190`),
`isTargetMarkedForDeath` (`FUN_14032eb40`), `AITaskSytem.pathFailures` (+0x218, the "give-up list"), `Character::isAlly` / `isEnemy` (vfuncs `0x3f0` / `0x3e8`), `amInsideTownWalls` (`0x118`), `isIndoors` (`0x1d8`, a building
hand), `isGettingEaten` (+0x279), `CharStats._holdPositionMode` (+0x12b).
- **Kits**: any item of function 1 (organic) or 12 (robot) in the medic's inventory or its backpack (**Verified**: `FUN_14099f690` tests the inventory, then a second lookup of the backpack's inventory; `FUN_14004b84e` compares each item's function only, with no charge or quantity test). Squad mates' kits do not count for the job. The task then takes a kit from the medic's own main grid, then its worn backpack, the fewest charges above zero (the rig task's search, **Verified**; the first aid tasks share its vtable slot, **Observed**: [kit search](character-stats.md#first-aid-update)). The splint finder wants function 2 and takes X = min(the medic's `medic` stat, the kit's quality).
- **Desire** (`FUN_140998410`): 0.01 while the AI's `+0xb0` is above 0, else 0.05 while `+0xbc` is above 0 (both read as enemy counts, **Observed**), else 0.1 while the medic carries someone, else 1. Always above 0, and `FUN_14050e5b0` keeps a job whose desire is above 0 (**Verified**). The finder has no squad-fight test (**Verified**, `FUN_14099f690`): a free player medic treats a downed mate while its squad fights (**Observed**, offscreen sandbox run). Meitou: an NPC medic waits while its squad fights or it carries someone (engine stand-ins for the low desire; a kidnapper otherwise walked off after a patient with its captive).
- **Need** (`FUN_140998200`) of a patient for this medic: 0 while the patient's CombatClass `+0x130` is set (fighting). The kit test is on unless the medic is not of the player's faction and the patient is imprisoned or a chained
  slave of the medic's faction (`FUN_14032e2a0`, which then counts without kits); the organic need score ([first aid need](character-stats.md#first-aid-need-score)) counts with an organic kit, the robot one with a robot kit.
  Another faction's patient: x 0.8 when `isAlly` (relation 50 or more, the allied flag, squad memory), else x relation / 100, at least 0.05 (`DAT_141696c74`). The further floor of 0.01 for a player medic with a current
  task (`FUN_140507b70`: its task list is not just GET_UP 65 / 52) is dead code: it needs a factor at or below 0, which the 0.05 minimum rules out.
- **Nearness** (`FUN_140998b90`, flag = the medic's hold-position mode): 0 when the flag is set, the patient is over 40 units (1600 squared) from the medic and over 40 from its hold point `+0x130`; else 1 - d^2 / 3397386.5 (3D),
  at most 1 and at least 0.4 when both are in the same building (vfunc `0x78`), else 0. The pass-1 and pass-2 calls pass no flag (an unset register, **Observed**); Meitou applies it everywhere.
- **Pass 1, allies** (sensed entries with bit 0 ally, none of bit 4 dead): not of a robot race (`+0x7c`) unless job 57; not being eaten (`+0x279` & 7); not on the AI's path-failure list; not carried by an enemy of the medic; not
  fighting (`isInCombatMode`); for a medic not of the player's faction the patient must not be maimed (`FUN_140644170`: a stump in a leg, or in both arms: limb states 1 at slots 2 / 3, or 0 and 1) and, when the medic stands in a
  building, must be seen or heard (the entry's two flags). Score = need x nearness. The same faction always counts; another faction only at 0.1 or more and (the medic is not of the player's faction, or **Heal Allies** `DAT_142134690+0x110`).
  The blackboard's sub-target list: when the score exceeds 0.001 and the pair (medic, patient) is on it, that patient is taken at once with score at least 0.95; a patient others work on (entries with another medic) x 0.05; x relation / 100 once more
  when not an ally (never for an ally entry). The highest wins; then **the medic itself** at its plain need beats it when larger. A found patient ends the search.
- **Pass 2, strangers** (sensed entries with any of neutral, knocked out, prisoner, crawling and none of enemy, dead): not of a robot race (job 58), not an animal (`0x248`), not being eaten, not of a not-real faction, not fighting. A free patient:
  not a bandit-class one when the medic's class differs, not an enemy, passing `FUN_14059a670` (**Verified** to be DC 55 I_SHOULD_HELP_THIS_GUY, Meitou `DialogueSystem.Helps`: same NPC class, not for a traitorous or bandit medic, yes when friendly, honourable both or loved, never when hated),
  seen or heard; it counts only when it is down (`0x30`) and the medic's **faction record says `heals strangers`**. An imprisoned patient (`+0x2f8` = 2): its cage must not be the blackboard's cage marked for death, the patient not a slave
  (status +0 = 1), and the cage owned by the medic's squad home (ownerships `+0x38`); a patient with no cage passes. A chained slave owned by the medic's faction also counts (slave state under 2). Then the medic and the patient must share the town
  (`amInsideTownWalls`) and the building (`isIndoors` hands equal; **the earlier reading "different platoon" was wrong**: the hand's vfunc `0x10` is not-equal and the test needs false). Score = need x nearness; unlike pass 1 a patient
  others work on is skipped, not weighed. No 0.1 floor, no Heal Allies.
- **Pass 3, owned slaves**: characters of the medic squad's ownerships that are chained slaves (state 1), sensed by the squad, not fighting, not a robot (job 58): need x nearness. **Pass 4**, only while no one was found and the medic is outdoors:
  sensed carried (bit 15) patients, not a robot or animal, not eaten, seen or heard, whose carrier is of the medic's faction: need x nearness (flag = hold mode). **Pass 5**, after 3 and 4, when the medic stands in its squad's home building: the
  occupants of its cages (function 8 buildings of that place): need x 0.25 flat (`DAT_141696c80`), taken when above the best so far. So NPC guards patch up their prisoners, without a kit.
- **Splint finder** (`FUN_14099e240`): no splint kit: nothing. Sensed allies, not robot races, not path-failed, not carried by an enemy, of the **medic's own faction only**; weight = `needsSplint`(X) (`FUN_140646400`, **Verified** by disassembly:
  0.1 when a part meets the [needsSplint test](character-stats.md#splints); -0.1 for a dead patient, else 0) x nearness (flag = hold mode) with the same sticky pair and x 0.05 rules; the medic itself at 0.1. No squad-fight test in the finder (**Verified**, `FUN_14099e240`); Meitou's splint job still waits while its squad fights, and the splint job's desire in a fight is **Unknown**.
- **NPCs run it as a goal**: the `Medic` AI_TASK (enum 58) is in the goal lists of most AI packages (Squad, Squad2 and Leader roles; e.g.
  `Guard building, residents`, `Guard town`, `Shop`, `Wandering from town to town`, the slaver packages; **Verified** by a data survey of the
  full load order, 2026-10-24).
- Meitou (`FirstAidSystem.FindPatient` / `SplintSomeone`, one finder for the player's jobs, the Medic button and idle NPCs): all five passes. Original data used: the path-failure list is `CharacterCold.PathFailures` (written by pursuits and treatments; the finder skips such patients). **Observed** (code inspection of `FirstAidSystem.Begin` / `Schedule` and `MovementSystem.ApplyAnswers`): starting treatment clears an earlier path failure; a failed answer or a known navigation endpoint outside treatment reach ends treatment and excludes that patient for 20 game seconds. A provisional route uses its exact prefix endpoint (`PathExact`), or the current medic position when that prefix is empty, rather than its straight extension to the goal. `NavigationWaiting`, `NeedPath` and `Pending` defer this decision: a waiting answer may retain a short exact prefix while navigation loads, without marking the patient unreachable; `isAlly` is `DialogueSystem.RegardsFriendly`, `isEnemy` its squad memory tags (`EnemyMemory`) then the relation, `FUN_14059a670` is `DialogueSystem.Helps` (personality included); the cage marked for death is a Kill or Eat job of the medic's squad on that cage (`CaptiveSystem.Jobs`). Stand-ins left: "sensed" is the medic's sight range, "seen or heard" always holds, "the same town and building" is `SightMap.PlaceOf`, "the squad home" is any place a faction cage stands in, the slave test needs `Shackles`, the disguise faction of `isAlly` is not read, the sub-target list is the set of (medic, patient) pairs of running treatments, the goal's desire stand-in "no fight in the squad" applies to NPC medics only (player medics are not held back by fights, see Desire above). Not yet modelled (**Verified** by decompile of `FUN_14099f690`, not changed): the sticky pair (a running pair is taken at once, `FUN_140269190`), the second relation factor in pass 1 for a patient that is not an ally (`FUN_1406b2460`), and the "not being eaten" test (+0x279 & 7) in the passes.

## Squad AI options

The overview's AI tab, bytes of `DAT_142134690` (**Verified**: CAMERA writer `FUN_1403eb710` / reader `FUN_1403ebf50`, key per byte): +0x110 `heal`,
+0x111 `help`, +0x112 `rescue`, +0x113 `stay`, +0x114 `feed`, +0x115 `share`, +0x116 `sleep`, +0x117 `ditch`, +0x118 `sit`, +0x119 `eject`, +0x11a
`shootfirst`. Captions and tooltips **Verified** in the exe. The AI tab (`FUN_140494ce0`, **Verified**) has nine tick lines (all but `stay` and `eject`, which no
line shows) and a last line "Follow Formation", a `DataPanelLine_DropBox` (`Kenshi_ComboBox`, right edge at 0.97 of the panel; its size **Unknown**) with Random (0), Military (2),
Caravan (1) in that order, tooltip "Formation used when squad members are following a leader", bound to the player faction's FACTION `squad formation`; a change marks every
player squad for a new layout ([squads travelling together](pathfinding.md#squads-travelling-together)). The CAMERA int `formation` holds it, read only when the record has
`ditch`. Meitou: `SquadFormationCommand` sets `World.FactionFormations` (the movement rebuilds the slots when the type changes). New-game values (**Observed**, 511 of the owner's saves): ditch, sleep, sit, share,
feed, eject on; heal, help off (on in 4), rescue, stay, shootfirst off. Readers (**Verified** by decompile):
- **Heal Allies** (+0x110): the medic finder's other-faction test, pass 1 only ([Medic jobs](#medic-jobs)); "Characters with the medic job will also heal characters of allied factions".
- **Help Allies** (+0x111): the protect-allies finders `FUN_14099ca10` (goals 15, 288) and `FUN_14099cf80` (sensed (1, 0x208), seen or heard): for a
  player-faction looker each fighting candidate's threat (`FUN_14099a190`) is × `FUN_1409b2380`(d², 10, 1000, 0.1); one of another faction is
  skipped with the option off and squared with it on. Result capped at 1.2.
- **Rescue Allies** (+0x112): the character finder `FUN_1408cd2d0` ([carry-loot](carry-loot.md#32-goals-and-their-target-finders)) requires the
  rescuer's faction only for a player-faction rescuer with the option off.
- **Feed Animals** (+0x114): off, the player characters' eating `FUN_1405d29c0` returns for an animal (vfunc 0x248), and desire `FUN_140597cb0`
  is 0 for an animal (its rows **Unknown**).
- **Share Food** (+0x115): in `FUN_1405d29c0` (the player characters' eating), with no food in its own inventory or backpack and (`Character::isWithThePlayer` `FUN_140856bc0`,
  floats +0x4bc ≤ 0, +0x4b8 < 2.25, meanings **Unknown**) a character takes food from the backpack of a squad member within 200 units (squared 40000).
- **Shoot first** (+0x11a): turret target choice `FUN_1404374d0`: enemies (sensed 4, 0x210) weigh `FUN_140435d70`(…, 1), unseen × 0.1, the current
  target × 1.5; with none, a player gunner with the option takes sensed (2, 0x210) characters whose faction's type (+0x34) is 0 or 8 or whose
  faction default (+0x60) is below -30, with weight 0.6.

## Unknowns

- Why `taskType` 289 and 290 exist without an enum name (they build `Task_Follow` and `Task_BashDoor`; row 290 of the TaskData table repeats the data of `BASH_GATE`).
- What the 37 named types without a TaskData row do when an AI_TASK record uses them (the list is in the TaskData registry section), and whether any behaviour of the 147 "goal" types (factory default `Task_Blank`) lives outside this factory.
- What the `*` marker on an entry (second flag byte) means, and which category registry a character plans with ([planner](#planner)).
- What the 18 StateType ids with no registration call and the two ids registered with an empty name (33 and 85) evaluate, and the semantics of most evaluators beyond their names.
