using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Simulation;

/// <summary>
/// The state every tick touches for one character, in a dense struct array (docs/simulation.md, "World model"). Plain data, no
/// references, so the table copies it by block and the state hash reads it by value. What the fields mean is up to the systems;
/// the movement system uses position, velocity, yaw, goal, task and flags.
/// </summary>
public struct CharacterHot
{
    /// <summary>The slot holds a character. A dead slot's other fields are stale.</summary>
    public bool Alive;
    /// <summary>The slot's generation: raised each time the slot is used again, so an old <see cref="CharacterId"/> never names the new character.</summary>
    public int Generation;
    /// <summary>World position: X/Z on the map, Y the height the character stands at.</summary>
    public Vector3 Position;
    public Vector3 Velocity;
    /// <summary>Heading in radians, 0 along +Z, turning towards +X (the renderer's convention: yaw of <c>atan2(x, z)</c>).</summary>
    public float Yaw;
    /// <summary>Movement mode (walk, run, ...); the meaning is the movement system's.</summary>
    public byte Mode;
    public ushort Flags;
    /// <summary>Where the character is going (X/Z).</summary>
    public Vector2 Goal;
    /// <summary>Cursor into the character's path (the path itself is cold state).</summary>
    public int PathCursor;
    /// <summary>Animation state and its time in seconds.</summary>
    public ushort Animation;
    public float AnimationTime;
    public float Health;
    /// <summary>What the character is doing (a <see cref="CharacterTask"/>) and the time in seconds that task keeps (a wait, a repath cooldown).</summary>
    public byte Task;
    public float TaskTime;
    /// <summary>The speed stat S in world units (decimetres) per second, and the race walk speed (the cap of speed mode 0).</summary>
    public float MaxSpeed;
    public float WalkSpeed;
}

/// <summary>The rest of a character: things few systems touch per tick (identity, squad, path, body, inventory, combat state; the AI blackboard is still to come).</summary>
public sealed class CharacterCold
{
    /// <summary>Persistent world identity when registered; retained when a stored character is unloaded and restored.</summary>
    public EntityId Entity { get; internal set; }
    public string Name { get; set; } = "";
    public int Faction { get; set; }
    /// <summary>What the renderer needs to draw the character; a character without one is simulated but not published in snapshots.</summary>
    public CharacterAppearance? Appearance { get; set; }
    /// <summary>The tick the character appeared at.</summary>
    public long SpawnedTick { get; set; }
    /// <summary>The CHARACTER (or ANIMAL_CHARACTER) record it was made from.</summary>
    public string RecordId { get; set; } = "";
    /// <summary>The squad it belongs to (-1 for none) and its role in it (the editor's SquadMemberType).</summary>
    public int SquadId { get; set; } = -1;
    public int Role { get; set; }
    /// <summary>Seconds left that a follower blocked on its way straight to its slot takes paths (<see cref="MovementSystem.FollowPathSeconds"/>).</summary>
    public float FollowPathTime { get; set; }
    /// <summary>A formation follower's heading (unit X/Z; zero until it starts), eased toward its leader's (<see cref="MovementSystem.HeadingEase"/>). Not saved; hashed while following.</summary>
    public System.Numerics.Vector2 FollowHeading { get; set; }
    /// <summary>The speed a follower wants this tick (<see cref="MovementSystem"/>, the original's desired speed). Not saved; hashed while following.</summary>
    public float FollowSpeed { get; set; }
    /// <summary>Whom a <see cref="FollowOrder"/> told it to follow while its task is <see cref="CharacterTask.Follow"/> (None: its squad's leader). Not saved; hashed only while set.</summary>
    public CharacterId FollowOrder { get; set; } = CharacterId.None;
    /// <summary>Whether the <see cref="FollowOrder"/> is a Bodyguard order (45): the follower also defends its target (<see cref="RetaliationSystem"/>). Not saved; hashed with <see cref="FollowOrder"/>.</summary>
    public bool Guarding { get; set; }
    /// <summary>The target a manned-turret gunner was told to shoot at (Shoot At, 235; <see cref="Combat.ShootAtOrder"/>); None: the gunner chooses. Not saved; hashed only while set.</summary>
    public CharacterId TurretTarget { get; set; } = CharacterId.None;
    /// <summary>The caged prisoner this character walks to free (Set Free, 110; <see cref="Locks.SetFreeOrder"/>); None: none. Not saved; hashed only while set.</summary>
    public CharacterId Freeing { get; set; } = CharacterId.None;
    /// <summary>The free cage this character walks to get into (Get in, 107; <see cref="Locks.GetInCageOrder"/>); null: none. Not saved; hashed only while set.</summary>
    public string? EnteringCage { get; set; }
    /// <summary>The path being followed (world positions, the next one at <c>CharacterHot.PathCursor</c>); replaced whole in a serial phase.</summary>
    public Vector3[] Path { get; set; } = [];
    /// <summary>
    /// The index into <see cref="Path"/> where its provisional long-range part begins (<see cref="PathResult.Exact"/>; int.MaxValue: none). Reaching it asks for
    /// the path again, at most every <see cref="MovementSystem.ProvisionalRepathSeconds"/> (<see cref="ProvisionalRepathTick"/>). Set with the path, not hashed.
    /// </summary>
    public int PathExact { get; set; } = int.MaxValue;
    public long ProvisionalRepathTick { get; set; }
    /// <summary>Times the walker was stopped on the provisional part since a long route last gained a navmesh part (<see cref="MovementSystem.ProvisionalBlockedLimit"/>).</summary>
    public int ProvisionalBlocked { get; set; }
    /// <summary>Times the walker was stopped on the navmesh part of its path since it last reached a waypoint or was given an order (<see cref="MovementSystem.BlockedRepathLimit"/>).</summary>
    public int BlockedRepaths { get; set; }
    /// <summary>The path request in flight, so an answer to an older one is ignored.</summary>
    public long PathRequest { get; set; }
    /// <summary>
    /// The request whose path is being followed when it was not the one in flight (an order given again before its path came, holding the right button);
    /// set only with the asynchronous path service, so not hashed.
    /// </summary>
    public long PathApplied { get; set; }
    /// <summary>Active local replacement of Path through SpliceEnd for SpliceGoal; cleared when the path or order changes. Not saved.</summary>
    public Vector3[]? SplicePath { get; set; }
    public int SpliceEnd { get; set; }
    public Vector2 SpliceGoal { get; set; }
    public byte SpliceTask { get; set; }
    public long SpliceRequest { get; set; }
    /// <summary>Highest submitted splice id: obsolete splice answers must never become whole-goal paths. Retained across cancellation.</summary>
    public long LastSplice { get; set; }
    /// <summary>Belongs to the player's faction: can be selected and ordered.</summary>
    public bool IsPlayer { get; set; }
    /// <summary>Queued move orders (X/Z) after the current one (shift + right click); changed only by the character's own partition or a serial phase.</summary>
    public List<System.Numerics.Vector2> OrderQueue { get; } = [];
    /// <summary>
    /// The height a move order was given at (the clicked ground), for the goal <see cref="GoalHeightAt"/>: it picks the floor where floors are stacked.
    /// Other goals use the character's own height. Derived from orders and not hashed.
    /// </summary>
    public float GoalHeight { get; set; } = float.NaN;
    public System.Numerics.Vector2 GoalHeightAt { get; set; }
    /// <summary>
    /// How near (units) the walk's last waypoint counts as reached; 0: <see cref="MovementSystem.ArriveRadius"/>. The AI sets it for a walk to a node, post, seat
    /// or gun (<see cref="MovementSystem.SpotArrive"/>), which ends on the spot; <see cref="MovementSystem.GoTo"/> clears it. Derived from the AI and not hashed.
    /// </summary>
    public float ArriveWithin { get; set; }
    /// <summary>Animation layers in play (made by <see cref="AnimationSystem"/>; null before the first tick it runs).</summary>
    public CharacterAnimation? Animation { get; set; }
    /// <summary>The kind of weapon drawn in the right hand (None while sheathed): the stance animations are chosen for.</summary>
    public Meitou.Data.Gameplay.WeaponKinds DrawnWeapon { get; set; }
    /// <summary>The bolts loaded in the crossbow (the combat slot's, mirrored for the renderer's loaded string and bolts; display only: not hashed, not saved).</summary>
    public int CrossbowShots { get; set; }
    /// <summary>The numbers of the HUD's extended lines, refreshed by <see cref="BodySystem"/> every tick (display only: not hashed, not saved).</summary>
    public ExtendedStats Extended { get; set; }
    /// <summary>
    /// The skill its job used at <see cref="SkillInUseTick"/> (the original's Character <c>+0x198</c> <c>currentSkillUsing</c>, docs/game/ui-screens.md section 4): what the HUD's
    /// "Current Skill" line shows. Set by the job systems with <see cref="UseSkill"/> (display only: not hashed, not saved).
    /// </summary>
    public Meitou.Data.Gameplay.Bodies.StatsEnumerated SkillInUse { get; private set; }
    public long SkillInUseTick { get; private set; } = long.MinValue;
    /// <summary>Marks <paramref name="stat"/> as the skill its job uses this tick.</summary>
    public void UseSkill(Meitou.Data.Gameplay.Bodies.StatsEnumerated stat, long tick) => (SkillInUse, SkillInUseTick) = (stat, tick);
    /// <summary>The race, stats and medical state (null for anything without a race record): see <see cref="BodySystem"/>.</summary>
    public Meitou.Data.Gameplay.Bodies.RaceData? Race { get; set; }
    /// <summary>
    /// An animal's ANIMAL_CHARACTER fields (null for people), set with its body. Its age (the original's <c>CharacterAnimal+0x700</c>) is <see cref="Bodies.CharacterStats.Scale"/>;
    /// <see cref="BodySystem"/> grows it (docs/game/animals.md "Ageing").
    /// </summary>
    public Meitou.Data.Gameplay.Bodies.AnimalData? Animal { get; set; }
    /// <summary>The age an animal's drawn size was last refreshed at (the original's <c>+0x6fc</c>): the size the renderer shows is lerp(this, scale min, scale max). Follows from the age: not hashed.</summary>
    public float ShownAge { get; set; } = 1;
    /// <summary>
    /// The bolts stuck in the character (docs/game/combat.md "Bolt visuals"): shown on their bones until first aid has nothing left to bandage. A visual: not hashed, not saved
    /// (the original keeps them in its appearance only).
    /// </summary>
    public List<StuckBolt>? StuckBolts { get; set; }
    public Meitou.Simulation.Bodies.CharacterStats? Stats { get; set; }
    public Meitou.Simulation.Bodies.MedicalState? Medical { get; set; }
    /// <summary>Sandbox only (the animation sandbox of the game host): replaces the movement mode of every order, and the speed the character walks at (0: the speed chain's). Not hashed; the normal game never sets them.</summary>
    public byte? ModeOverride { get; set; }
    public float SpeedOverride { get; set; }
    /// <summary>The path cost factor of water for this character (race <c>water avoidance</c>, halved for the player faction; 0: default), the floor of its <see cref="WaterCost"/>.</summary>
    public float WaterFactor { get; set; }
    /// <summary>Its faction's FACTION <c>road preference</c> (0: none), with which a long trip takes the roads (<see cref="RoadNetwork.Route"/>).</summary>
    public float RoadPreference { get; set; }
    /// <summary>The water state 0..3 (<see cref="Meitou.Simulation.Bodies.Speed.WaterState"/>) <see cref="MovementSystem"/> found at the character's position, and its swim speed (<see cref="Meitou.Simulation.Bodies.Speed.Swim"/>) <see cref="BodySystem"/> keeps up to date. Derived each tick: not hashed, not saved.</summary>
    public byte WaterState { get; set; }
    public float SwimSpeed { get; set; }
    /// <summary>
    /// The weather acting on the character (the original's animation holder <c>+0x198</c> / <c>+0x1a0</c>, docs/formats/weather.md "Effects on characters"): its kind, the
    /// region's strength s and the worn protection from it, set by <see cref="WeatherSystem"/> while it is exposed and not immune; None otherwise. Sight and hearing read them.
    /// Derived each tick: not hashed, not saved.
    /// </summary>
    public Meitou.Data.World.WeatherAffecting Weather { get; set; }
    public float WeatherStrength { get; set; }
    public float WeatherProtection { get; set; }
    /// <summary>
    /// The status notice a player character's weather update put up this tick (the original's short status string, Character <c>+0x548</c>, cleared at the start of every weather update and set when the
    /// effect hurt it, docs/formats/weather.md "Effects on characters") and the protection it shows. Derived each tick: not hashed, not saved.
    /// </summary>
    public WeatherNotice WeatherNotice { get; set; }
    public float WeatherNoticeProtection { get; set; }
    /// <summary>Where the last blood decal of its trail was left, and the drip build-up while it bleeds being carried (<see cref="BodySystem"/>, docs/game/blood-decals.md). Display only: not hashed, not saved.</summary>
    public System.Numerics.Vector3 BloodMark { get; set; }
    public float BloodDrip { get; set; }
    /// <summary>Burst by a fatal hit in luckyren Mode (<see cref="World.Luckyren"/>): the blow's direction (X/Z); <see cref="BodySystem"/> removes it at the end of the tick. Not saved.</summary>
    public System.Numerics.Vector2? Burst { get; set; }
    /// <summary>What it carries (null for anything without a race; an animal's grid is its <c>inventory w</c> x <c>inventory h</c>, its luggage on the backpack slot):<see cref="Meitou.Simulation.Items.FeedSystem"/> eats from it, <see cref="BodySystem"/> weighs it.</summary>
    public Meitou.Simulation.Items.Inventory? Inventory { get; set; }
    /// <summary>The tick it died at (-1: alive); the corpse decays 12 game hours later (<see cref="BodySystem.SlowWorld"/>, which holds the clock back while it is carried or held).</summary>
    public long DiedTick { get; set; } = -1;
    /// <summary>A corpse with flies (docs/game/character-stats.md "Corpses": 4 game hours after death while the weather leaves characters alone); it smells of blood (<see cref="SmellSystem"/>).</summary>
    public bool HasFlies { get; set; }
    /// <summary>Where the character came from in a loaded save, with the saved stats and medical state (null for a character the game made itself); see <c>Meitou.Simulation.Saving</c>.</summary>
    public Saving.SavedCharacterLink? Save { get; set; }
    /// <summary>The look was changed in the character editor (<see cref="Editing.EditCharacter"/>): a save writes its CHARACTER_APPEARANCE back.</summary>
    public bool LookEdited { get; set; }
    /// <summary>RACE <c>pathfind footprint radius</c> (0: not known, paths use the default human); what the navmesh keeps clear.</summary>
    public float FootprintRadius { get; set; }
    /// <summary>The character is in a fight (set by the combat system): the animation stance takes the combat clips.</summary>
    public bool InCombat { get; set; }
    /// <summary>
    /// The crossbow controller's <c>combatMode</c> (<c>+0x2f0 → +0x36</c>, docs/game/ui-screens.md section 4 "Combat mode"): set by each shooting tick (the shooting task's update), cleared
    /// when the shooting ends (the fight's end, or the switch to melee). Only the HUD reads it; not saved, not hashed.
    /// </summary>
    public bool RangedCombatMode { get; set; }
    /// <summary>
    /// Character <c>+0x3d8 lastUsedWeaponCategory</c>: the <c>skill category</c> of the weapon in hand, written only by the HUD while it shows the character in a melee fight (0 at
    /// creation; no save key). At 7 the accuracy line takes Turrets. Display state: not saved, not hashed.
    /// </summary>
    public int LastWeaponCategory { get; set; }
    /// <summary>
    /// The key of the turret the character mans this tick (set by the combat system's serial step, docs/simulation.md "Combat", turrets; null: none). Derived every tick; not saved, not hashed.
    /// </summary>
    public string? Gunning { get; set; }
    /// <summary>A fighter running at its target out of the combat stance (set by the <see cref="PursuitSystem"/>, docs/game/combat.md "Combat stance").</summary>
    public bool CombatRunning { get; set; }
    /// <summary>
    /// The slot of the target a fighter in its footwork faces while a path moves it (set by the <see cref="PursuitSystem"/> every tick, -1: none; docs/animation.md "Side steps").
    /// Derived every tick; not saved, not hashed.
    /// </summary>
    public int FaceSlot { get; set; } = -1;
    /// <summary>The fight steering of a fighter in its footwork (<see cref="Combat.SteerState"/>; null until its first fight). Not saved; hashed while active.</summary>
    public Combat.SteerState? Steer { get; set; }
    /// <summary>
    /// The fight steering's speed (the original's steering +0x12c, <c>FUN_1402af1e0</c>): it grows by <see cref="MovementSystem.Acceleration"/> per second up to the top speed while
    /// the character fights and is never reset, so a fighter that has got going keeps its pace through stops, new paths and the footwork (whose 18 caps the speed used, not this).
    /// </summary>
    public float SteerSpeed { get; set; }
    /// <summary>
    /// The nav agent's own speed along its path, moved toward the wanted one at <see cref="MovementSystem.PathAcceleration"/>. A turn slows the step it takes, not this, so
    /// a walker taking a bend does not lose its pace bit by bit; 0 when it stands, lies or is held.
    /// </summary>
    public float AgentSpeed { get; set; }
    /// <summary>Seconds the fighter's target has been moving away out of a fight (the <see cref="PursuitSystem"/>'s fleeing check).</summary>
    public float TargetFleeingTime { get; set; }
    /// <summary>Its last path request came back with no way there (set by the <see cref="MovementSystem"/>, taken by the <see cref="PursuitSystem"/>).</summary>
    public bool PathFailed { get; set; }
    public bool NavigationWaiting { get; set; }
    public long NavigationRetryTick { get; set; }
    /// <summary>
    /// Fight targets it found no path to and the tick until which its target choice leaves them out (<c>AITaskSytem::notifyPathImpossible</c> stores 20 in its
    /// <c>pathFailures</c>, <c>isPathImpossible</c> skips them; docs/game/combat.md "Attack slots and circling"). Not saved.
    /// </summary>
    public List<(CharacterId Target, long Until)>? PathFailures { get; set; }
    /// <summary>
    /// The number that turns its portrait camera (docs/game/ui-screens.md "Portrait cells"): the save's <c>portrait_serial</c>, else its handle's serial; a character the game makes
    /// gets random × 4e9 at <see cref="CharacterTable.Spawn"/>, as the original rolls one per character.
    /// </summary>
    public uint? PortraitSerial { get; set; }
    /// <summary>The character is running away (set and cleared by the <see cref="FleeSystem"/>, serial phases only): it neither looks for fights nor answers attacks.</summary>
    public bool Fleeing { get; set; }
    /// <summary>
    /// How exposed a sneaking character was at <see cref="StealthExposureTick"/> (set by the <see cref="AggroSystem"/> in its serial commit): what its stealth XP
    /// rate reads (<see cref="Bodies.XpService.Sneaking"/>).
    /// </summary>
    public StealthExposure StealthExposure { get; set; }
    public long StealthExposureTick { get; set; } = long.MinValue;
    /// <summary>The hostile NPCs noticing or detecting this sneaker lately (<see cref="AggroSystem"/>, serial commit; display only: not hashed, not saved).</summary>
    public List<StealthWatcher>? Watchers { get; set; }
    /// <summary>Weapon and worn armour (track F, docs/simulation.md "Combat").</summary>
    public Meitou.Simulation.Combat.Fighter? Fighter { get; set; }
    /// <summary>The patient of the <see cref="CharacterTask.Treat"/> task (itself for self-treatment); meaningful only while the task is set (<see cref="FirstAid.FirstAidSystem"/>).</summary>
    public CharacterId TreatTarget { get; set; } = CharacterId.None;
    /// <summary>The treatment splints instead of bandaging (the original's <c>SPLINT_ORDER</c>, task "Splinting").</summary>
    public bool TreatSplint { get; set; }
    /// <summary>The task a self-started treatment goes back to when it ends (<see cref="CharacterTask.Wander"/>, <see cref="CharacterTask.Follow"/> or Idle).</summary>
    public CharacterTask ResumeTask { get; set; }
    /// <summary>The medic job (58 <c>JOB_MEDIC</c>) is in <see cref="Jobs"/>: an idle character with a kit treats hurt squad mates by itself. NPCs with a kit do so without it (an engine rule until the AI).</summary>
    public bool MedicJob => Jobs.Exists(j => j.Type == Meitou.Data.Gameplay.Ai.TaskType.JOB_MEDIC);
    /// <summary>The character this one carries on its left shoulder (none: not carrying); the original's <c>isCarrying</c> with the partner handle. Set and cleared by <see cref="Carrying.CarrySystem"/> in serial phases only, together with <see cref="CarriedBy"/> of the other.</summary>
    public CharacterId Carrying { get; set; } = CharacterId.None;
    /// <summary>The character carrying this one (none: not carried); the original's <c>isBeingCarried</c> with the partner handle.</summary>
    public CharacterId CarriedBy { get; set; } = CharacterId.None;
    /// <summary>While carried: lifted awake by a friendly carrier (the player's Pick Up on a standing squad mate), so it stays on the shoulder until put down rather than being set down as one that came round.</summary>
    public bool CarriedAwake { get; set; }
    /// <summary>While carried: where the body hangs on its carrier's shoulder, from the carrier's pose (<see cref="Ragdolls.RagdollSystem"/>, each tick); null without the carrier's skeleton.</summary>
    public Carrying.CarryAttach? CarryAttach { get; set; }
    /// <summary>Whom the <see cref="CharacterTask.PickUp"/>, <see cref="CharacterTask.Loot"/> or <see cref="CharacterTask.StealthKo"/> task is for; meaningful only while one of those tasks is set.</summary>
    public CharacterId CarryTarget { get; set; } = CharacterId.None;
    /// <summary>Seconds into the <c>shoulder lift left</c> clip of a pick-up or put-down, or the <c>knockout</c> swing of a stealth KO, in progress (-1: not yet; the animation system plays the clip at this time).</summary>
    public float LiftTime { get; set; } = -1;
    /// <summary>The stealth KO target heard the attacker on its last step before the swing (the sense entry's heard flag, <see cref="CharacterTask.StealthKo"/>).</summary>
    public bool KnockoutHeard { get; set; }
    /// <summary>The <see cref="CharacterTask.PickUp"/> is a kidnap (<see cref="Carrying.PickUpOrder.Kidnap"/>): the lift rolls <see cref="Carrying.CarrySystem.KidnapChance"/> first.</summary>
    public bool Kidnapping { get; set; }
    /// <summary>The rescue job (148 <c>FIND_AND_RESCUE_IF_THERES_BEDS</c>, NUM8) is in <see cref="Jobs"/>: an idle character picks up a downed member of its faction by itself.</summary>
    public bool RescueJob => Jobs.Exists(j => j.Type == Meitou.Data.Gameplay.Ai.TaskType.FIND_AND_RESCUE_IF_THERES_BEDS);
    /// <summary>The orders panel's toggles (<see cref="Simulation.Orders"/>); the player's characters only.</summary>
    public CharacterOrders Orders { get; set; } = CharacterOrders.Jobs;
    /// <summary>The point the Hold order holds (CharStats +0x130): where Hold was turned on, then each ground move's destination while on Hold; null: where it stands (not saved).</summary>
    public System.Numerics.Vector2? HoldPoint { get; set; }
    /// <summary>The tick its stumble clip ends (combat's StunUntil, set at each interrupting hit): until then it neither walks nor works (docs/game/combat.md "Stagger outside a fight").</summary>
    public long StaggeredUntil { get; set; }
    /// <summary>The tick its get-up after a knockout ends (<see cref="Combat.CombatSystem.GetUpSeconds"/> from waking; Character +0x278 isCurrentlyGettingUp).</summary>
    public long GettingUpUntil { get; set; }
    /// <summary>The speed mode the character's move orders use (the original's saved <c>speed mode</c>: walk, run or no cap); no cap by default.</summary>
    public byte RunSpeed { get; set; } = SpeedMode.Free;
    /// <summary>
    /// CHARACTER <c>NPC class</c> (the editor's CharacterTypeEnum; <c>fcs.def</c>'s default is MILITARY), set from the record at spawn; <c>None</c> for a character without a record
    /// (hand-made in tests). The crime rules read it (who is police, who may loot, who can be booked); not hashed, it follows from the record.
    /// </summary>
    public Meitou.Data.Gameplay.FundamentalType NpcClass { get; set; }
    /// <summary>
    /// CHARACTER <c>faction importance</c> (the game's <c>Character+0x41c</c>, read by <c>FUN_14062b210</c>): how much a deed against it moves its faction's relations
    /// (<see cref="FactionDeeds.Weight"/>); 0 without a CHARACTER record. Set from the record at spawn, so not hashed.
    /// </summary>
    public float FactionImportance { get; set; }
    /// <summary>The character's bounties by faction (null: none ever); <see cref="Crime.CrimeSystem"/>, serial phases only. Hashed only while set.</summary>
    public Crime.BountyLedger? Bounties { get; set; }
    /// <summary>A crime nobody has booked yet (null: none); <see cref="Crime.CrimeSystem"/>, serial phases only. Hashed only while set.</summary>
    public Crime.PendingCrime? Pending { get; set; }
    /// <summary>What the AI decided for it (null: a character the AI does not run: the player's, or one whose squad has no AI packages); <see cref="Ai.AiSystem"/>.</summary>
    public Ai.AiMind? Ai { get; set; }
    /// <summary>Held still by an interaction (a trader in an open trade, <see cref="Trading.TradeSystem"/>): the AI leaves it alone. Serial phases only.</summary>
    public bool Engaged { get; set; }
    /// <summary>
    /// The personality tag dialogue tests (DC_PERSONALITY_TAG, docs/game/dialogue.md "Line check"): the save's <c>personality</c> when it has one, else rolled from the record on first use
    /// (<see cref="Dialogue.Personality"/>); 0 not yet known. Follows from the seed, the id and the record, so not hashed.
    /// </summary>
    public int Personality { get; set; }
    /// <summary>The dialogues this character has locked or unlocked (DIALOGUE string id → locked; saved as <c>lok*</c>); null: none changed. <see cref="Dialogue.DialogueSystem"/>; hashed only while set.</summary>
    public SortedDictionary<string, bool>? DialogueLocks { get; set; }
    /// <summary>
    /// The original's <c>Character+0x2f8</c> (save key <c>in something</c>): in a bed or imprisoned in a cage or restraint, with <see cref="InWhat"/> the
    /// <see cref="Locks.Fixture.Id"/> it is in (null: not known, e.g. a loaded prisoner whose cage the world has not mapped). Set by <see cref="Locks.FixtureRegistry"/>, serial phases only.
    /// </summary>
    public Locks.HeldState InSomething { get; set; }
    public string? InWhat { get; set; }
    /// <summary>Imprisoned: it does not move, fight, carry or run AI (docs/simulation.md "Cages, beds and locks").</summary>
    public bool IsImprisoned => InSomething == Locks.HeldState.Imprisoned;
    /// <summary>
    /// The prison sentence as the original stores it (docs/game/economy.md 11.5): the start in <see cref="World.GameHours"/> and the length in game hours (save keys <c>sentence</c>,
    /// <c>psts</c>); served when start + length is not after now. Length 0: none (served).
    /// </summary>
    public double SentenceStart { get; set; }
    public float SentenceLength { get; set; }
    /// <summary>
    /// The original's "recently wanted" marker (status <c>+0x110</c>, set to 250 by a booking or a witnessed crime, docs/game/economy.md 11.2/11.8): while above 0 a looker recognises
    /// the bounty at once. Game seconds counting down by dt (<c>AI::updateStateBroadcast</c> <c>FUN_1405a4ae0</c>, <b>Observed</b>); the same field is the disguise's blown cooldown (saved as <c>disguiseblown</c>). <see cref="Crime.CrimeSystem"/>; hashed only while set.
    /// </summary>
    public float RecentlyWanted { get; set; }
    /// <summary>The bounty recognition meter (economy.md 11.8; <b>engine</b>: one per target, not per looker): fires at 1. <see cref="Crime.ArrestSystem"/>; hashed only while set.</summary>
    public float Recognition { get; set; }
    /// <summary>
    /// The clothing disguise (status <c>+0x100</c>, docs/game/perception.md "Disguise"): 0..1, recomputed for the player's characters by <see cref="Crime.ArrestSystem"/>; with
    /// <see cref="DisguiseFaction"/> the uniform's faction (status <c>+0x108</c>, -1 none). Hashed only while set.
    /// </summary>
    public float DisguiseLevel { get; set; }
    public int DisguiseFaction { get; set; } = -1;
    /// <summary>Seconds left on the disguise tooltip's three marks (same faction, slave owner, someone knows me; 14 s from the last looker that applied one). Runtime only, not hashed.</summary>
    public float[]? DisguiseMarks { get; set; }
    /// <summary>The escaped-prisoner flag (status <c>+0xe8</c>, docs/game/locks.md "Cages and beds"): left a cage before the sentence was served. Hashed only while set.</summary>
    public bool Escaped { get; set; }
    /// <summary>Held in a cage by a kidnapper (<see cref="Kidnap.KidnapSystem"/>), not sentenced: no sentence is ever served, no bail is offered, getting out is no crime. Saved as <c>meitou kidnapped</c>. Hashed only while set.</summary>
    public bool Kidnapped { get; set; }
    /// <summary>
    /// The original's slave state (status <c>+0</c>, saved as <c>slavestate</c>; <c>FUN_1405a4940</c>): 0 free, 1 slave, 2 escaped slave, 3 ex-slave. A slave's sentence is never served.
    /// <see cref="Locks.FixtureRegistry.Enslave"/>; an escaped slave lies low to ex-slave, then free (<see cref="Locks.FixtureRegistry.LayLow"/>); hashed only while set.
    /// </summary>
    public byte SlaveState { get; set; }
    /// <summary>The game hours at which <see cref="SlaveState"/> last became 2 or 3 (status <c>+0x8</c>, saved as the float <c>ssct</c>; <see cref="Locks.FixtureRegistry.LayLow"/>). Hashed with a state above 1.</summary>
    public double SlaveSince { get; set; }
    /// <summary>The faction owning a slave or escaped slave (the original keeps a <c>slaver</c> handle; -1 none). Saved as <c>meitou slave owner</c>; hashed with <see cref="SlaveState"/>.</summary>
    public int SlaveOwner { get; set; } = -1;
    /// <summary>The squad a slave obeys (the original's owner handle naming a platoon; -1 none): job 187 Obedient Slave runs that squad's <c>Slave AI Goals</c> (<see cref="Ai.AiSystem"/>).
    /// Runtime only (not saved, not hashed): set where the slave is made or sold.</summary>
    public int SlaveMaster { get; set; } = -1;
    /// <summary>Stood up from playing dead on the player's order since it went down (<c>playerWantsMeToGetUp</c>): the get-up line may be event 25. Runtime only.</summary>
    public bool OrderedUp { get; set; }
    /// <summary>The player's speed group (<c>SpeedGroup</c>, made for the selection by Group speed: the first member's slot; -1 none). Runtime only.</summary>
    public int SpeedGroup { get; set; } = -1;
    /// <summary>The fixture this character is picking the lock of (<see cref="Locks.LockSystem"/>; null: none). Hashed only while set.</summary>
    public string? PickingLock { get; set; }
    /// <summary>The door this character is bashing in (<see cref="Locks.LockSystem"/>; null: none). Hashed only while set.</summary>
    public string? Bashing { get; set; }
    /// <summary>Whether <see cref="Bashing"/> was ordered by the player (Smash, 226) rather than found by the pursuit: the basher then needs no attack target. Hashed with <see cref="Bashing"/>.</summary>
    public bool BashOrdered { get; set; }
    /// <summary>The fixture whose lock this character cuts or breaks by order (Use Tools 285, Use Strength 286; <see cref="Locks.LockSystem"/>; null: none). Hashed only while set.</summary>
    public string? ForcingLock { get; set; }
    /// <summary>How <see cref="ForcingLock"/> is forced.</summary>
    public Locks.LockForce Force { get; set; }
    /// <summary>Game seconds since the last swing at <see cref="Bashing"/>' door (-1: walking there).</summary>
    public float BashTime { get; set; }
    /// <summary>The shackles this character is working on (<see cref="Locks.LockSystem"/>: picking, cutting; null: none). Hashed only while set.</summary>
    public Locks.ShackleJob? Shackles { get; set; }
    /// <summary>The ground item this character walks to and picks up (<see cref="Items.GroundItems"/> id, <see cref="PlayerSystem"/>; null: none). Hashed only while set.</summary>
    public int? PickingUpItem { get; set; }
    /// <summary>The limb an animal holds in its mouth (<see cref="Items.FeedSystem.HoldInMouth"/>; null: none), eaten later by <see cref="Items.FeedSystem"/>. Hashed only while set; not saved.</summary>
    public Items.ItemInstance? Mouth { get; set; }
    /// <summary>The world tick at which <see cref="Mouth"/> was taken.</summary>
    public long MouthSinceTick { get; set; }
    /// <summary>The building site the <see cref="CharacterTask.Build"/>, <see cref="CharacterTask.Dismantle"/> or <see cref="CharacterTask.DeliverMaterials"/> task works (<see cref="Construction.BuildSite.Id"/>; null: none). Serial phases only. Hashed only while set.</summary>
    public string? BuildSite { get; set; }
    /// <summary>The material delivery bar (0 to 1, filling at 0.2 per second, <see cref="CharacterTask.DeliverMaterials"/>). Hashed with <see cref="BuildSite"/>.</summary>
    public float BuildBar { get; set; }
    /// <summary>The player building (<see cref="Construction.BuildSite.Id"/>) a <see cref="CharacterTask.FetchMaterials"/> builder takes <see cref="FetchRecord"/> from (null: none, or a ground item). Hashed with <see cref="BuildSite"/>.</summary>
    public string? FetchSite { get; set; }
    /// <summary>The ground item (<see cref="Items.GroundItems"/> id) a <see cref="CharacterTask.FetchMaterials"/> builder picks up (null: none). Hashed with <see cref="BuildSite"/>.</summary>
    public int? FetchItem { get; set; }
    /// <summary>The material record a <see cref="CharacterTask.FetchMaterials"/> builder fetches (null: not fetching). Hashed with <see cref="BuildSite"/>.</summary>
    public string? FetchRecord { get; set; }
    /// <summary>The building the <see cref="CharacterTask.Operate"/> task works (<see cref="Construction.BuildSite.Id"/>; null: none). Serial phases only. Hashed only while set.</summary>
    public string? OperateSite { get; set; }
    /// <summary>The progress (0 to 1) of the <see cref="CharacterTask.Prospect"/> task (null: not prospecting). Serial phases only; not saved. Hashed only while set.</summary>
    public float? Prospecting { get; set; }
    internal PlayerId ProspectPlayer { get; set; }
    internal CharacterControl? ProspectControl { get; set; }
    /// <summary>The storage an operator walks to, to store its machine's output or fetch an input (<see cref="Production.ProductionSystem"/> "Hauling"; null: at its machine). Not saved. Hashed only while set.</summary>
    public string? HaulSite { get; set; }
    /// <summary>The ITEM a hauling operator fetches from <see cref="HaulSite"/>; null: it stores what it carries.</summary>
    public string? HaulFetch { get; set; }
    /// <summary>The job list (Perma-Jobs, <see cref="Production.PermaJobs"/>), first = highest priority. Serial phases only. Hashed only while not empty.</summary>
    public List<Production.PermaJob> Jobs { get; } = [];
    /// <summary>The <see cref="Jobs"/> entry the current task was started from (-1: none, the task was ordered). Hashed with <see cref="Jobs"/>.</summary>
    public int JobIndex { get; set; } = -1;
    /// <summary>The implicit job (a squad AI option, <see cref="SquadAi"/>) the current task was started from: 242 ditching, 153 going to bed, 275 sitting; null: none. Hashed only while set.</summary>
    public Meitou.Data.Gameplay.Ai.TaskType? Implicit { get; set; }
    /// <summary>The storage a <see cref="CharacterTask.Haul"/> trip ends at (a "Hauling to" job's, or the one an implicit ditch picked). Hashed with <see cref="HaulSite"/>.</summary>
    public string? HaulTo { get; set; }
    /// <summary>The clip the operator plays while it works (the functionality's <c>animation</c>).</summary>
    public string? OperateClip { get; set; }
    /// <summary>The clips of the cage it is held in (<see cref="Locks.Fixture.Clips"/>), set when it is caged; read only while <see cref="InSomething"/> is imprisoned. Derived, not hashed.</summary>
    public Locks.CageClips CageClips { get; set; }
    /// <summary>The ITEM the operator holds while its operate task runs (the functionality's <c>special tool</c>); derived, not hashed.</summary>
    public string? OperateTool { get; set; }
    /// <summary>The machine's <see cref="Meitou.Data.Gameplay.Construction.MachineData.HungerRate"/> while its operate task runs; derived, not hashed.</summary>
    public float OperateHungerRate { get; set; } = 1;
    /// <summary>The operator did work this tick (it plays <see cref="OperateClip"/>); derived, not hashed.</summary>
    public bool Operating { get; set; }
    /// <summary>The whole-body clip (an ANIMATION record name) a captor holds while it works a captive standing (<see cref="Kidnap.CaptiveSystem"/>: "chop down static", "eat", "medic"); null: none. Derived, not hashed.</summary>
    public string? ActionClip { get; set; }
}

/// <summary>
/// The characters of a world: <see cref="CharacterHot"/> structs in two dense arrays, <see cref="Previous"/> (the last tick's
/// result, which every phase reads, other characters' included) and <see cref="Next"/> (the tick being computed, where a phase
/// writes only its own characters), plus a <see cref="CharacterCold"/> object per slot. A tick starts with <see cref="BeginTick"/>
/// (Next becomes a copy of Previous) and ends with <see cref="EndTick"/> (the two swap). Slots are reused, the most recently freed
/// first, with the generation raised; spawning and removing happen only in the serial phases, in the order the commit sorts them,
/// which is what keeps slot assignment independent of the thread count.
/// </summary>
public sealed class CharacterTable
{
    CharacterHot[] previous;
    CharacterHot[] next;
    CharacterCold?[] cold;
    readonly Stack<int> free = [];
    readonly Dictionary<EntityId, CharacterId> entities = [];
    readonly Dictionary<CharacterId, bool> recentRemovals = [];
    readonly Queue<CharacterId> removalOrder = [];
    internal Action<CharacterCold, bool>? EntityRemoved;
    int highWater;
    bool inTick;
    TablePhase phase = TablePhase.Outside;

    /// <summary>Limbs cut off in the tick (the character's slot, the limb slot 0 to 3): the population system rebuilds the look and drops the severed limb between ticks.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(int Character, int Limb)> LimbsChanged { get; } = new();

    public CharacterTable(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        previous = new CharacterHot[capacity];
        next = new CharacterHot[capacity];
        cold = new CharacterCold?[capacity];
    }

    /// <summary>Slots ever used: every character is in 0..HighWater-1.</summary>
    public int HighWater => highWater;
    /// <summary>Characters alive.</summary>
    public int Count => highWater - free.Count;

    /// <summary>The last tick's state of the slots 0..HighWater-1.</summary>
    public ReadOnlySpan<CharacterHot> Previous => previous.AsSpan(0, highWater);
    /// <summary>The state being computed, same slots. A phase writes only the slots of its own partition.</summary>
    public Span<CharacterHot> Next => next.AsSpan(0, highWater);
    /// <summary>Authoritative read state: Next during a tick, committed Previous at a host boundary.</summary>
    public ReadOnlySpan<CharacterHot> Current => (inTick ? next : previous).AsSpan(0, highWater);

    public CharacterCold? Cold(int slot) => cold[slot];

    /// <summary>The id of the character in <paramref name="slot"/> as of the last tick.</summary>
    public CharacterId IdOf(int slot) => new(slot, previous[slot].Generation);

    /// <summary>Whether <paramref name="id"/> names a character alive as of the last tick.</summary>
    public bool IsAlive(CharacterId id) =>
        id.Slot >= 0 && id.Slot < highWater && previous[id.Slot].Alive && previous[id.Slot].Generation == id.Generation;

    /// <summary>Finds the id in Next during a tick, or the committed state at a host boundary, including recent spawns/removals.</summary>
    public bool TryResolveNext(CharacterId id, out int slot)
    {
        slot = id.Slot;
        var current = inTick ? next : previous;
        return slot >= 0 && slot < highWater && current[slot].Alive && current[slot].Generation == id.Generation;
    }

    public bool TryResolveEntity(EntityId entity, out CharacterId character)
    {
        character = CharacterId.None;
        if (entity.IsNone || !entities.TryGetValue(entity, out var live) || !TryResolveNext(live, out _)) return false;
        character = live;
        return true;
    }

    internal void BindEntity(CharacterId character, EntityId entity)
    {
        if (phase == TablePhase.Parallel) throw new InvalidOperationException("Entity identities are assigned in serial phases.");
        if (entity.IsNone || !TryResolveNext(character, out int slot)) throw new InvalidOperationException("A live character and nonempty identity are required.");
        if (!cold[slot]!.Entity.IsNone && cold[slot]!.Entity != entity) throw new InvalidOperationException("A character identity cannot change.");
        if (entities.TryGetValue(entity, out var existing) && existing != character) throw new InvalidOperationException("The identity is already loaded.");
        cold[slot]!.Entity = entity;
        entities[entity] = character;
    }

    internal TablePhase Phase
    {
        get => phase;
        set => phase = value;
    }

    internal void BeginTick()
    {
        Array.Copy(previous, next, highWater);
        inTick = true;
    }

    internal void EndTick()
    {
        (previous, next) = (next, previous);
        inTick = false;
    }

    /// <summary>
    /// Adds a character; call only from the serial phases (inputs, commit, slow world) or before the first tick. The slot is the
    /// most recently freed one, else a new one. The generation and <c>Alive</c> in <paramref name="state"/> are set here.
    /// </summary>
    public CharacterId Spawn(CharacterHot state, CharacterCold cold, long tick)
    {
        ArgumentNullException.ThrowIfNull(cold);
        if (phase == TablePhase.Parallel) throw new InvalidOperationException("characters are spawned in the serial phases only");
        if (!cold.Entity.IsNone && entities.ContainsKey(cold.Entity)) throw new InvalidOperationException("The character identity is already loaded.");
        int slot;
        int generation;
        if (free.Count > 0)
        {
            slot = free.Pop();
            generation = Current[slot].Generation + 1;
        }
        else
        {
            slot = highWater;
            if (slot == previous.Length) Grow();
            highWater++;
            generation = 0;
        }
        state.Alive = true;
        state.Generation = generation;
        cold.SpawnedTick = tick;
        cold.PortraitSerial ??= (uint)(Rng.Float(Rng.Mix(Rng.Key(new CharacterId(slot, generation)) ^ (ulong)tick * 0x9E3779B97F4A7C15UL)) * 4e9f);
        next[slot] = state;
        // Outside a tick there is no "last tick" to keep apart from the new one.
        if (!inTick) previous[slot] = state;
        this.cold[slot] = cold;
        if (!cold.Entity.IsNone) entities.Add(cold.Entity, new CharacterId(slot, generation));
        return new CharacterId(slot, generation);
    }

    /// <summary>The id the next <see cref="Spawn"/> will give (serial phases), so a character's seeded body can be made before it is added.</summary>
    public CharacterId PeekNextId() => free.Count > 0 ? new CharacterId(free.Peek(), Current[free.Peek()].Generation + 1) : new CharacterId(highWater, 0);

    /// <summary>Removes a character in the state being computed; false when <paramref name="id"/> is not alive there. Serial phases only.</summary>
    public bool Remove(CharacterId id, bool unloaded = false)
    {
        if (phase is TablePhase.Parallel or TablePhase.Inputs or TablePhase.Schedule)
            throw new InvalidOperationException("characters are removed in the commit or the slow world only (a phase reads them as alive)");
        if (!TryResolveNext(id, out int slot)) return false;
        if (!inTick) next[slot] = previous[slot];
        next[slot].Alive = false;
        if (!inTick) previous[slot].Alive = false;
        if (cold[slot] is { } removed)
        {
            if (!removed.Entity.IsNone) entities.Remove(removed.Entity);
            EntityRemoved?.Invoke(removed, unloaded);
        }
        cold[slot] = null;
        free.Push(slot);
        recentRemovals.Add(id, !unloaded); removalOrder.Enqueue(id);
        if (removalOrder.Count > 8192) recentRemovals.Remove(removalOrder.Dequeue());
        return true;
    }

    /// <summary>Bounded transient evidence for network departures. Missing/expired evidence means loss of scope, never inferred destruction.</summary>
    public bool ConfirmedDestroyed(CharacterId id) => recentRemovals.GetValueOrDefault(id);

    /// <summary>
    /// Puts a character at <paramref name="position"/> facing <paramref name="yaw"/>, standing still with no path (a teleport: into a cage). Serial phases or setup only; outside a
    /// tick both buffers are written, as <see cref="Spawn"/> does.
    /// </summary>
    public void Place(int slot, Vector3 position, float yaw)
    {
        if (phase == TablePhase.Parallel) throw new InvalidOperationException("characters are placed in the serial phases only");
        if (!inTick) next[slot] = previous[slot];
        ref var n = ref next[slot];
        n.Position = position;
        n.Yaw = yaw;
        n.Velocity = Vector3.Zero;
        n.Goal = new Vector2(position.X, position.Z);
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
        n.Task = (byte)CharacterTask.Idle;
        n.TaskTime = 0;
        if (!inTick) previous[slot] = n;
    }

    void Grow()
    {
        int size = previous.Length * 2;
        Array.Resize(ref previous, size);
        Array.Resize(ref next, size);
        Array.Resize(ref cold, size);
    }

    /// <summary>Adds the table's canonical state, in slot order, to <paramref name="hasher"/>: every slot's state, the free list in reuse order and the cold fields that matter.</summary>
    internal void Hash(ref StateHasher hasher)
    {
        hasher.Add(highWater);
        for (int i = 0; i < highWater; i++)
        {
            ref readonly var c = ref previous[i];
            hasher.Add(c.Alive);
            hasher.Add(c.Generation);
            if (!c.Alive) continue;
            hasher.Add(c.Position);
            hasher.Add(c.Velocity);
            hasher.Add(c.Yaw);
            hasher.Add((int)c.Mode);
            hasher.Add((int)c.Flags);
            hasher.Add(c.Goal);
            hasher.Add(c.PathCursor);
            hasher.Add((int)c.Animation);
            hasher.Add(c.AnimationTime);
            hasher.Add(c.Health);
            hasher.Add((int)c.Task);
            hasher.Add(c.TaskTime);
            hasher.Add(c.MaxSpeed);
            hasher.Add(c.WalkSpeed);
            var k = cold[i]!;
            hasher.Add(k.Faction);
            hasher.Add(k.SpawnedTick);
            hasher.Add(k.SquadId);
            hasher.Add(k.Role);
            hasher.Add(k.FollowPathTime);
            if (c.Task == (byte)CharacterTask.Follow) { hasher.Add(k.FollowHeading); hasher.Add(k.FollowSpeed); }
            hasher.Add(k.Path.Length);
            hasher.Add(k.PathRequest);
            if (k.LastSplice != 0) { hasher.Add(0x53706C63); hasher.Add(k.LastSplice); }
            if (k.SplicePath is not null)
            {
                hasher.Add(0x53706C41); hasher.Add(k.SpliceEnd); hasher.Add(k.SpliceGoal); hasher.Add((int)k.SpliceTask); hasher.Add(k.SpliceRequest);
                hasher.Add(ReferenceEquals(k.SplicePath, k.Path));
            }
            if (k.NavigationWaiting) { hasher.Add(0x4E617657); hasher.Add(k.NavigationRetryTick); }
            hasher.Add(k.IsPlayer);
            hasher.Add(k.OrderQueue.Count);
            foreach (var q in k.OrderQueue) hasher.Add(q);
            hasher.Add((int)k.DrawnWeapon);
            BodyHash.Add(ref hasher, k.Stats, k.Medical);
            if (k.Inventory is null) hasher.Add(-1); else k.Inventory.Hash(ref hasher);
            hasher.Add(k.DiedTick);
            if (k.HasFlies) hasher.Add(0x466C6965);
            hasher.Add(k.InCombat);
            if (k.CombatRunning || k.TargetFleeingTime != 0) { hasher.Add(0x43527573); hasher.Add(k.CombatRunning); hasher.Add(k.TargetFleeingTime); }   // only while set, so worlds without fights hash as before
            if (k.Steer is { Active: true } steer) steer.Hash(ref hasher);
            if (k.PathFailures is { Count: > 0 } failures) { hasher.Add(0x50464C53); foreach (var (target, until) in failures) { hasher.Add(target.Slot); hasher.Add(target.Generation); hasher.Add(until); } }
            if (k.SteerSpeed != 0) { hasher.Add(0x53746572); hasher.Add(k.SteerSpeed); }
            if (k.AgentSpeed != 0) { hasher.Add(0x41676e74); hasher.Add(k.AgentSpeed); }
            if (k.Animation is { } anim) anim.Hash(ref hasher); else hasher.Add(-1);
            hasher.Add(k.Name.Length);
            // First aid state adds to the hash only while it is set, so worlds that never use it hash as before.
            if (c.Task == (byte)CharacterTask.Treat) { hasher.Add(k.TreatTarget); hasher.Add((int)k.ResumeTask); if (k.TreatSplint) hasher.Add(0x53706C74); }
            if (k.Fleeing) hasher.Add(0x466C6565);   // only while set, so worlds without fleeing hash as before
            if (k.StealthExposureTick != long.MinValue) { hasher.Add((int)k.StealthExposure); hasher.Add(k.StealthExposureTick); }   // likewise, once a sneaker was exposed
            // Carrying, looting and the rescue job add to the hash only while set, so worlds without them hash as before.
            if (!k.Carrying.IsNone) { hasher.Add(0x43617272); hasher.Add(k.Carrying); }
            if (!k.CarriedBy.IsNone) { hasher.Add(0x42726E65); hasher.Add(k.CarriedBy); }
            if (k.CarriedAwake) hasher.Add(0x4177616B);
            if (c.Task is (byte)CharacterTask.PickUp or (byte)CharacterTask.PutDown or (byte)CharacterTask.Loot or (byte)CharacterTask.StealthKo) { hasher.Add(k.CarryTarget); hasher.Add(k.LiftTime); hasher.Add((int)k.ResumeTask); if (c.Task == (byte)CharacterTask.StealthKo) hasher.Add(k.KnockoutHeard); if (k.Kidnapping) hasher.Add(0x4B69646E); }
            // Orders add to the hash only while set, so worlds without them hash as before.
            if (k.Orders != CharacterOrders.None) { hasher.Add(0x4F726473); hasher.Add((int)k.Orders); }
            if (k.RunSpeed != SpeedMode.Free) { hasher.Add(0x52756E53); hasher.Add((int)k.RunSpeed); }
            // Bounties and pending crimes add to the hash only while set, so worlds without crimes hash as before.
            k.Bounties?.Hash(ref hasher);
            k.Pending?.Hash(ref hasher);
            // The AI's decision and the trade hold add to the hash only while set, so worlds without AI packages hash as before.
            if (k.Ai is { } mind) { hasher.Add(0x41694D64); mind.Hash(ref hasher); }
            if (k.Engaged) hasher.Add(0x456E6761);
            if (!k.FollowOrder.IsNone) { hasher.Add(0x466F6C77); hasher.Add(k.FollowOrder); if (k.Guarding) hasher.Add(0x47756172); }
            if (!k.TurretTarget.IsNone) { hasher.Add(0x54725467); hasher.Add(k.TurretTarget); }
            if (!k.Freeing.IsNone) { hasher.Add(0x46726565); hasher.Add(k.Freeing); }
            if (k.EnteringCage is { } entering) { hasher.Add(0x456E7472); hasher.Add(Rng.StableHash(entering)); }   // only while set, so worlds without follow orders hash as before
            if (k.DialogueLocks is { Count: > 0 } locks)
            {
                hasher.Add(0x4C6F6B73);
                foreach (var (dialogue, locked) in locks) { hasher.Add(Rng.StableHash(dialogue)); hasher.Add(locked); }
            }
            // Beds, cages and sentences add to the hash only while set, so worlds without them hash as before.
            if (k.InSomething != Locks.HeldState.None) { hasher.Add(0x496E536D); hasher.Add((int)k.InSomething); hasher.Add(k.InWhat is { } w ? Rng.StableHash(w) : 0UL); }
            if (k.SentenceLength > 0) { hasher.Add(k.SentenceStart); hasher.Add(k.SentenceLength); }
            // The arrest state adds to the hash only while set, so worlds without it hash as before.
            if (k.RecentlyWanted > 0) { hasher.Add(0x52576E74); hasher.Add(k.RecentlyWanted); }
            if (k.Recognition > 0) { hasher.Add(0x5265636F); hasher.Add(k.Recognition); }
            if (k.DisguiseLevel > 0) { hasher.Add(0x44697367); hasher.Add(k.DisguiseLevel); hasher.Add(k.DisguiseFaction); }
            if (k.Escaped) hasher.Add(0x45736361);
            if (k.Kidnapped) hasher.Add(0x4B64706E);
            if (k.SlaveState != 0) { hasher.Add(0x536C7653); hasher.Add(k.SlaveState); hasher.Add(k.SlaveOwner); if (k.SlaveState > 1) hasher.Add(k.SlaveSince); }
            if (k.PickingLock is { } pick) { hasher.Add(0x5069636B); hasher.Add(Rng.StableHash(pick)); }
            if (k.Bashing is { } bash) { hasher.Add(0x42617368); hasher.Add(Rng.StableHash(bash)); hasher.Add(k.BashTime); if (k.BashOrdered) hasher.Add(0x4F726473); }
            if (k.ForcingLock is { } force) { hasher.Add(0x466F7263); hasher.Add(Rng.StableHash(force)); hasher.Add((int)k.Force); }
            if (k.Shackles is { } job) { hasher.Add(0x5368636B); hasher.Add(Rng.Key(job.Wearer)); hasher.Add((int)job.Work); }
            if (k.PickingUpItem is { } taking) { hasher.Add(0x54616B65); hasher.Add(taking); }
            if (k.Mouth is { } mouth) { hasher.Add(0x4D6F7574); hasher.Add(Rng.StableHash(mouth.Record)); hasher.Add(k.MouthSinceTick); }
            // A builder's site, bar and the task it goes back to add to the hash only while it works one, so worlds without sites hash as before.
            if (k.BuildSite is { } site)
            {
                hasher.Add(0x4275696C); hasher.Add(Rng.StableHash(site)); hasher.Add(k.BuildBar); hasher.Add((int)k.ResumeTask);
                if (k.FetchRecord is { } fetch) { hasher.Add(0x46657463); hasher.Add(Rng.StableHash(fetch)); hasher.Add(Rng.StableHash(k.FetchSite ?? "")); hasher.Add(k.FetchItem ?? 0); }
            }
            if (k.OperateSite is { } machine) { hasher.Add(0x4F706572); hasher.Add(Rng.StableHash(machine)); }
            if (k.Prospecting is { } prospected)
            {
                hasher.Add(0x50726F73); hasher.Add(prospected);
                if (k.ProspectControl is { } surveyGrant)
                { hasher.Add(0x53757276); hasher.Add(surveyGrant.Player.Value); hasher.Add(surveyGrant.Entity.Value); hasher.Add(surveyGrant.Faction); hasher.Add(surveyGrant.Revision); }
            }
            if (k.HaulSite is { } haul) { hasher.Add(0x4861756C); hasher.Add(Rng.StableHash(haul)); hasher.Add(Rng.StableHash(k.HaulFetch ?? "")); }
            if (k.HaulTo is { } haulTo) hasher.Add(Rng.StableHash(haulTo));
            if (k.Implicit is { } implicitJob) { hasher.Add(0x496D706C); hasher.Add((int)implicitJob); hasher.Add((int)k.ResumeTask); }
            if (k.Jobs.Count > 0)
            {
                hasher.Add(0x4A6F6273);
                hasher.Add(k.JobIndex);
                hasher.Add((int)k.ResumeTask);
                foreach (var perma in k.Jobs) { hasher.Add((int)perma.Type); hasher.Add(Rng.StableHash(perma.Site ?? "")); if (!perma.Character.IsNone) hasher.Add(perma.Character); }
            }
        }
        hasher.Add(free.Count);
        foreach (int slot in free) hasher.Add(slot);
    }
}

/// <summary>Where in a tick the table is, so a spawn or removal in the wrong place fails loudly instead of corrupting a tick.</summary>
public enum TablePhase
{
    /// <summary>Between ticks (setup): anything goes.</summary>
    Outside,
    /// <summary>Spawns allowed; removals not (the phases that follow read the character as alive).</summary>
    Inputs,
    Schedule,
    /// <summary>Think, move, act: no spawns or removals.</summary>
    Parallel,
    Commit,
    SlowWorld,
}
