using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

public sealed partial class AiSystem
{
    /// <summary>Kinds of <see cref="PlanTarget"/> the finders give.</summary>
    static class Targets
    {
        public const byte Spot = 1, Bed = 2, Seat = 3, Turret = 4, Town = 5, Work = 6;
    }

    /// <summary>How near a home's position counts as at home (units; engine stand-in for the original's inside-the-building test).</summary>
    const float HomeRadius = 20;

    /// <summary>
    /// The steps the engine carries out besides a goal's own (docs/simulation.md "Planner"): moving and running (the goal's walk), travelling to the mission town (the
    /// travel goal's walk), standing at a node, shopping, sleeping, manning a turret, operating or pretending to (sitting at the seat), sheathing and getting out of bed,
    /// drawing a weapon, going home and getting near a target (walks of their own before the goal's act-out).
    /// A plan ending in another step fails.
    /// </summary>
    static bool Carried(TaskType step) => step is TaskType.MOVE_ON_FREE_WILL or TaskType.MOVE_ON_FREE_WILL_FAST or TaskType.TRAVEL_TO_TARGET_TOWN
        or TaskType.TRAVEL_TO_TARGET_TOWN_FAST or TaskType.STAND_AT_NODE or TaskType.BUY_SHIT
        or TaskType.USE_BED or TaskType.USE_BED_ORDER or TaskType.USE_TURRET or TaskType.OPERATE_MACHINERY or TaskType.COLLECT_OUTPUT_RESOURCE or TaskType.PRETEND_TO_OPERATE_MACHINERY or TaskType.UNEQUIP_WEAPON
        or TaskType.GET_OUT_OF_BED or TaskType.GET_OUT_OF_BED_IF_ITS_EMERGENCY or TaskType.GET_OUT_OF_BED_ONCE_HEALED
        or TaskType.EQUIP_WEAPON or TaskType.EQUIP_CROSSBOW or TaskType.GO_HOMEBUILDING or TaskType.GET_NEAR_TO
        or TaskType.ENTER_BUILDING or TaskType.LEAVE_BUILDING;

    /// <summary>
    /// One character's view for the <see cref="TaskPlanner"/> (one per worker thread, bound per think): its states (docs/simulation.md "Planner" lists the
    /// stand-ins), the targets of the goals Meitou acts out, and the desires of the rows on the way.
    /// </summary>
    sealed class PlanContext : IPlanWorld
    {
        [ThreadStatic] static PlanContext? local;
        public static PlanContext Local => local ??= new PlanContext();

        AiSystem system = null!;
        World world = null!;
        AiMind mind = null!;
        Blackboard board = null!;
        int slot;
        readonly List<PlanStep> scratch = [];

        /// <summary>Everyone plans with the first category, the one nearly every row is in (engine choice: which one a character uses is Unknown, docs/game/ai-tasks.md "Planner").</summary>
        public int Category => 0x1;
        public bool Squadless => false;

        public PlanContext Bind(AiSystem s, World w, int at, AiMind m, Blackboard b)
        {
            (system, world, slot, mind, board) = (s, w, at, m, b);
            return this;
        }

        /// <summary>
        /// Plans a goal; true (and the plan in <see cref="AiMind.Plan"/>) when it has one whose first step the engine carries out. A goal without a TaskData
        /// row is its own plan; one whose list A already holds is left out (the scorer's done test).
        /// </summary>
        public bool TryPlan(AiGoal goal, float desire)
        {
            var type = goal.Task.Type;
            if (TaskDataTable.Of(type) is not { } row)
            {
                mind.Plan.Clear();
                mind.Plan.Add(new PlanStep(type, default));
                return true;
            }
            var target = row.Is(TaskDataFlags.NeedsTarget) && !row.Is(TaskDataFlags.Finder) ? Find(row, default) : default;
            if (row.Is(TaskDataFlags.NeedsTarget) && !row.Is(TaskDataFlags.Finder) && target.IsNone) return false;
            if (row.Done.Length > 0 && Array.TrueForAll(row.Done, c => Holds(c, target))) return false;
            if (TaskPlanner.Local.Plan(row, desire, target, this, mind.Plan, scratch) <= 0) return false;
            if (scratch[0].Type != type && !Carried(scratch[0].Type)) return false;
            mind.Plan.Clear();
            mind.Plan.AddRange(scratch);
            return true;
        }

        public float Desire(TaskDataRow row, in PlanTarget target)
        {
            float d = TaskProfiles.Of(row.Type).Desire;
            return d >= 0 ? d : 1;   // a random or own desire function counts as 1 on the way
        }

        ref CharacterHot Me => ref world.Characters.Next[slot];

        bool At(in PlanTarget t)
        {
            if (t.IsNone) return true;
            var p = Me.Position;
            float slack = t.Kind == Targets.Turret ? TurretSlack : t.Kind == Targets.Work ? WorkReach : system.Settings.PostSlack;
            if (Vector2.DistanceSquared(Flat(p), Flat(t.Position)) > slack * slack) return false;
            // A spot on a floor counts on that floor only (NaN: no floor).
            return float.IsNaN(t.Position.Y) || MathF.Abs(p.Y - t.Position.Y) <= FloorSlack;
        }

        float Distance(in PlanTarget t) => t.IsNone ? 0 : Vector2.Distance(Flat(Me.Position), Flat(t.Position));

        bool Settled(in PlanTarget t) => At(t) && (CharacterTask)Me.Task is CharacterTask.Rest or CharacterTask.Idle;

        /// <summary>
        /// A state's value for a target. States of rooms, doors, machines and fights the AI does not run into (it runs only out of a fight, carrying nothing)
        /// have a fixed stand-in value; an unlisted state is false.
        /// </summary>
        Production.Machine? MachineAt(in PlanTarget target) => target.Kind == Targets.Work && target.Building is { } key ? world.Machines.Find(key) : null;

        public bool Holds(TaskCondition condition, in PlanTarget target)
        {
            var cold = world.Characters.Cold(slot)!;
            bool value = condition.State switch
            {
                StateType.isAtLocation or StateType.isAtLocation_46 or StateType.atNode => At(target),
                StateType.isWithin10Meters => Distance(target) <= 10,
                StateType.isWithin50Meters => Distance(target) <= 50,
                StateType.isNearTo => Distance(target) <= 10,
                StateType.machineHasEmptyInputs => MachineAt(target)?.HasEmptyInputs == true,
                StateType.isMachineOutputFull => MachineAt(target)?.OutputBlocked == true,
                StateType.isMachineJammed => MachineAt(target)?.Jammed == true,
                StateType.gotInventoryRoomForResources => MachineAt(target) is { Output: { } output } && cold.Inventory is { } inventory && system.Data.Items.Create(output.Recipe.Item, 1) is { } unit && Production.ProductionSystem.RoomForOutput(inventory, unit),
                StateType.hasWeaponEquipped or StateType.hasWeaponEquipped_156 or StateType.hasWeaponEquipped_157 => cold.DrawnWeapon != WeaponKinds.None,
                StateType.hasWeapon => cold.Fighter is { Weapon: not null, Unarmed: false },
                StateType.IS_CARRYING_SOMETHING => !cold.Carrying.IsNone,
                StateType.isInBed => cold.InSomething == Locks.HeldState.Bed || (CharacterTask)Me.Task == CharacterTask.Rest && mind.Behaviour == AiBehaviour.Sleep,
                StateType.IS_WORKING or StateType.IS_WORKING_PRETEND or StateType.using_target_turret or StateType.using_any_turret => Settled(target),
                // AI::iAmInsideTargetBuilding (FUN_14059a4b0) for a target that is not a building: inside its home building (no home: held true).
                StateType.I_AM_INSIDE_TARGET_BUILDING => board.Home is not { } inHome || world.Sight.BuildingOf(slot) == inHome.Key,
                StateType.isAtHome => board.Home is not { } home || mind.HomeUnreachable || Vector2.DistanceSquared(Flat(Me.Position), Flat(home.Position)) <= HomeRadius * HomeRadius,
                // Without a town it is never at its home town; its destination town (patrolling in town) is wherever it is.
                StateType.isAtHomeTown => board.Town >= 0 && system.AtHomeTown(world, mind.Squad, board, world.Characters.IdOf(slot)),
                // The running package's mission town when it has one (the trader's next town), else the home town.
                StateType.atDestinationTown or StateType.atDestinationTown_94 => board.Destination.IsNone
                    ? system.AtHomeTown(world, mind.Squad, board, world.Characters.IdOf(slot)) : system.CurrentTown(world, Me.Position) == board.Destination,
                StateType.hasDestinationTown => board.Town >= 0 || !board.Destination.IsNone,
                StateType.atATown => system.TownAt(Me.Position) >= 0,
                // Any alarm of its town in reach (goal 210's own-faction filter is its desire's).
                StateType.ALARMS_IN_THE_VICINITY => system.AlarmOf(world, slot, TaskType.INVESTIGATE_ALARMS, board) is not null,
                StateType.isLeaderDown => mind.Squad.Leader.IsNone || !world.Characters.IsAlive(mind.Squad.Leader),
                StateType.isFullyRested => cold.Medical is not { Incapacitated: true },
                StateType.hasOneWorkingArm => cold.Medical is not { } arms || arms.ArmWorks(left: true) || arms.ArmWorks(left: false),
                StateType.hasTwoWorkingArm => cold.Medical is not { } arms2 || arms2.BothArmsWork,
                // The turret goals' done test (149, 222, 234: list A "6*"): no enemy near, so guards man the turrets only while enemies come (docs/simulation.md "AI", turrets).
                StateType.isNoEnemiesInVicinity => !EnemyNear(cold),
                // Held true: moving, standing up, power and buildings; doors are the movement's and the lock-up round's (docs/simulation.md "Planner").
                StateType.isMovementAllowed or StateType.canGetUp or StateType.isBuildingComplete
                    or StateType.BUILDING_HAS_POWER or StateType.machineHasFreeOperatorSlot or StateType.isReadyForAction
                    or StateType.DOOR_IS_OPEN or StateType.DOOR_IS_OPEN_HERE or StateType.AT_LOCATION_BUILDING_DOOR or StateType.AT_CURRENT_LOCATION_BUILDING_DOOR
                    or StateType.AT_LOCATION_BUILDING_DOOR_INSIDE or StateType.AT_LOCATION_BUILDING_DOOR_OUTSIDE or StateType.DESTINATION_IS_ACCESSIBLE
                    or StateType.LOCATION_IS_ACCESSIBLE or StateType.DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS or StateType.AT_LOCATION_FORTIFICATION_GATE
                    or StateType.DOOR_IS_LOCKED_AND_IM_INSIDE or StateType.DOOR_IS_LOCKED_AND_IM_OUTSIDE or StateType.allDoorsLocked or StateType.isObjectMine
                    or StateType.isAlly or StateType.crowdLimit8 or StateType.targetStandingStill
                    or StateType.isMachineOutputEmpty or StateType.hasBuildingMaterials or StateType.buildingHasBuildingMaterials => true,
                _ => false,
            };
            return value == condition.Holds;
        }

        struct EnemyScan
        {
            public CharacterTable Table;
            public FactionRelations Relations;
            public int Self, Faction;
            public bool Found;
        }

        /// <summary>
        /// An enemy is near (the negation of <c>isNoEnemiesInVicinity</c>): a living character up within <see cref="EnemyVicinity"/> whose faction is an enemy of this one's (the relations
        /// table). <b>Engine choice</b>: the original's test and its radius are <b>Unknown</b>.
        /// </summary>
        bool EnemyNear(CharacterCold cold)
        {
            if (!system.Relations.Contains(cold.Faction)) return false;
            var p = Me.Position;
            var scan = new EnemyScan { Table = world.Characters, Relations = system.Relations, Self = slot, Faction = cold.Faction };
            world.Grid.Query(p.X, p.Z, EnemyVicinity, ref scan, static (int other, ref EnemyScan st) =>
            {
                if (st.Found || other == st.Self || !st.Table.Previous[other].Alive || st.Table.Cold(other) is not { Medical.Incapacitated: false } oc) return;
                if (AggroSystem.IsEnemy(st.Relations, st.Faction, oc.Faction)) st.Found = true;
            });
            return scan.Found;
        }

        /// <summary>The target finders of the goals Meitou acts out (the spots, beds, seats, turrets and towns it already picks); none for the rest.</summary>
        public PlanTarget Find(TaskDataRow row, in PlanTarget from)
        {
            var type = row.Type;
            switch (type)
            {
                case TaskType.STAND_AT_SHOPKEEPER_NODE or TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING or TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY
                    or TaskType.STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE or TaskType.STAND_AT_GENERAL_NODE or TaskType.STAY_IN_HOME or TaskType.SIT_ON_THRONE
                    or TaskType.HOLD_POSITION or TaskType.MAN_THE_GATE:
                    // Staying home on its claimed chair, else at home.
                    if (type == TaskType.STAY_IN_HOME && board.SeatClaims.TryGetValue(slot, out var chair) && !mind.SeatUnreachable) return new PlanTarget(Targets.Seat, chair.Position);
                    return Spot(Targets.Spot, system.SpotFor(mind, board, AiBehaviour.Post, type));
                case TaskType.GO_HOME_AND_GO_TO_BED or TaskType.GO_HOME_AND_GO_TO_BED_SECURE:
                    return Spot(Targets.Bed, system.SpotFor(mind, board, AiBehaviour.Sleep, type));
                case TaskType.GO_HOMEBUILDING:
                    return Spot(Targets.Spot, system.SpotFor(mind, board, AiBehaviour.GoHome, type));
                case TaskType.WANDER_TOWN or TaskType.SHOPPING or TaskType.WANDERER:
                {
                    var behaviour = TaskBehaviours.Of(type);
                    var at = mind.Goal?.Task.Type == type && mind.Target is { } kept ? kept : system.TargetFor(world, world.Characters.IdOf(slot), behaviour, board.Town);
                    if (at is not { } p) return default;
                    return new PlanTarget(type == TaskType.WANDERER ? Targets.Town : Targets.Spot, type == TaskType.WANDERER ? p with { Y = float.NaN } : p);
                }
                case TaskType.RELAX_IN_TOWN_PACKAGE or TaskType.SIT_AROUND:
                    if (type == TaskType.SIT_AROUND && board.Home is null) return new PlanTarget(Targets.Spot, Me.Position);
                    return board.SeatClaims.TryGetValue(slot, out var seat) && !mind.SeatUnreachable ? new PlanTarget(Targets.Seat, seat.Position) : default;
                case TaskType.AUTO_LABOURING_MINES or TaskType.AUTO_LABOURING_MINES_PRETEND:
                    // The building it works at: the one it walks to now, else the pick it will make (the same roll, AiMind.WorkEpoch).
                    if (mind.Behaviour == AiBehaviour.Work && mind.Goal?.Task.Type == type && mind.Target is { } current) return new PlanTarget(Targets.Work, current) { Building = mind.WorkBuilding?.Key };
                    return system.PickWork(world, world.Characters.IdOf(slot), board.Town, mind.WorkEpoch + (mind.WorkActive ? 1 : 0)) is { } work
                        ? new PlanTarget(Targets.Work, new Vector3(work.Position.X, float.NaN, work.Position.Z)) { Building = work.Key } : default;
                case TaskType.MAN_A_TURRET or TaskType.MAN_A_TURRET_ON_BUILDING or TaskType.MAN_A_TURRET_PLAYER_JOB or TaskType.USE_TURRET:
                    return board.TurretClaims.TryGetValue(slot, out int t) && (uint)t < (uint)board.Turrets.Count && board.Turrets[t].Turret is { } turret
                        ? new PlanTarget(Targets.Turret, TurretSpot(world, turret)) : default;
            }
            return default;
        }

        /// <summary>A spot as the act-out walks to it: one on a floor over a hole furniture cuts is at the nearest floor point (<see cref="GoToSpot"/>).</summary>
        PlanTarget Spot(byte kind, (Vector2 Target, bool Home, float? Floor, float? Yaw) s)
        {
            if (s.Floor is { } y && world.Walkability.FloorNear(s.Target.X, s.Target.Y, y, FloorSlack) is { } floor) return new(kind, floor);
            return new(kind, new Vector3(s.Target.X, s.Floor ?? float.NaN, s.Target.Y));
        }
    }
}
