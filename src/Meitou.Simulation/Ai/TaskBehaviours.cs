using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// How the engine acts out a goal it chose (docs/simulation.md "AI"). The original runs a <c>Task_*</c> class and its GOAP plan per goal
/// (docs/game/ai-tasks.md); here a goal maps onto the movement tasks that exist, at the town's buildings, nodes and beds.
/// A goal whose type has no behaviour is never chosen.
/// </summary>
public enum AiBehaviour : byte
{
    /// <summary>Not built: the goal scores 0.</summary>
    None = 0,
    /// <summary>Stands about near its post, stepping a few metres now and then (<see cref="CharacterTask.Wander"/> in <see cref="AiSettings.MillRadius"/>).</summary>
    Mill = 1,
    /// <summary>Walks to its node in the home building (shopkeeper, guard, throne or general, by goal), else to its post, and stands there.</summary>
    Post = 3,
    /// <summary>Keeps its place beside the squad leader (<see cref="CharacterTask.Follow"/>); the leader itself cannot.</summary>
    Follow = 4,
    /// <summary>Walks to its bed in the home building (else the home, else its post) and lies down there (<see cref="CharacterTask.Rest"/>).</summary>
    Sleep = 5,
    /// <summary>Sits down (<see cref="CharacterTask.Rest"/>): at a bar or general node of its home, else inside it; without a home where it is.</summary>
    Sit = 6,
    /// <summary>While a patrol package runs, the leader walks the patrol's route and the others follow it; otherwise wanders about the town (<see cref="CharacterTask.Wander"/>).</summary>
    Patrol = 7,
    /// <summary>Goes back to its home town (its home building, else the town's centre); wanted at 0.5 while away from it and not at all at home (docs/game/perception.md 7.2, Observed).</summary>
    GoHome = 8,
    /// <summary>Walks to the turret of its town it claimed and stands at its gun (docs/simulation.md "AI"), where the combat system fires it.</summary>
    Turret = 9,
    /// <summary>Walks to a general node of a random building of its town and stands there (WANDER_TOWN, docs/game/ai-tasks.md "Town goals", Observed).</summary>
    TownNode = 10,
    /// <summary>Walks into a shop of its town and stands at a node in it (SHOPPING; buying is not modelled).</summary>
    Shop = 11,
    /// <summary>Sits at the seat of the package's bar it claimed (RELAX_IN_TOWN_PACKAGE under a bar package); wanted only while it has one.</summary>
    Relax = 12,
    /// <summary>Walks to the nearest town; not wanted while in one (WANDERER, docs/game/ai-tasks.md "Town goals", Observed).</summary>
    ToTown = 13,
    /// <summary>
    /// The leader walks (runs for the fast one) to the running package's mission town (<see cref="Blackboard.Destination"/>); wanted only while it has one and is not in it
    /// (WANDERING_TRADER and the travelling rows, whose plan is <c>Task_TravelToTargetTown</c> to <c>atDestinationTown</c>, docs/game/ai-tasks.md).
    /// </summary>
    Travel = 14,
    /// <summary>
    /// Works a building of its town: walks to a random one of the town's mines, research benches, refineries and crafting benches and stands at it at its work clip (the worker goals 189 and
    /// 190, finders <c>FUN_1408cac60</c> / <c>FUN_1408cadb0</c>, docs/game/ai-tasks.md "Slave work, shout and capture"; the machines of NPC towns produce nothing here).
    /// </summary>
    Work = 15,
    /// <summary>
    /// Answers a raised alarm of its town (INVESTIGATE_ALARMS 209, and 210 for its own faction's only; <c>Task_InvestigateAlarms</c>, docs/game/economy.md "Town alarms"): runs to it, looks
    /// about there and clears it, attacking criminals it sees on the way (<see cref="Crime.CrimeSystem"/>'s serial step). Wanted only while an alarm is in reach.
    /// </summary>
    Investigate = 16,
    /// <summary>
    /// PATROL_TOWN (36, <c>Task_PatrolTown</c>, docs/game/ai-tasks.md "Town goals"): walks to a random point round its town's centre, and draws the next one each time a walk is over,
    /// so it keeps strolling about the town (an occupier: round the complete buildings of the player town it holds).
    /// </summary>
    PatrolTown = 22,
}

/// <summary>The engine's table from <see cref="TaskType"/> to <see cref="AiBehaviour"/>, and the TaskData display text (docs/game/ai-tasks.md "TaskData registry", Verified) of the types it acts out.</summary>
public static class TaskBehaviours
{
    static readonly Dictionary<TaskType, (AiBehaviour Behaviour, string Text)> Table = new()
    {
        [TaskType.IDLE] = (AiBehaviour.Mill, "Idleness"),
        [TaskType.WANDERER] = (AiBehaviour.ToTown, "Wandering"),
        [TaskType.WANDER_TOWN] = (AiBehaviour.TownNode, "Wandering town"),
        [TaskType.PATROL_TOWN] = (AiBehaviour.PatrolTown, "Patrolling town"),
        [TaskType.PATROL] = (AiBehaviour.Patrol, "Patrolling"),
        [TaskType.RELAX_IN_TOWN_PACKAGE] = (AiBehaviour.Relax, "Relaxing"),
        [TaskType.SHOPPING] = (AiBehaviour.Shop, "Shopping"),
        [TaskType.STAND_AT_SHOPKEEPER_NODE] = (AiBehaviour.Post, "Shopkeeper"),
        [TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING] = (AiBehaviour.Post, "Static guard"),
        [TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY] = (AiBehaviour.Post, "Static guard indoors"),
        [TaskType.STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE] = (AiBehaviour.Post, "Static town guard"),
        [TaskType.STAND_AT_GENERAL_NODE] = (AiBehaviour.Post, "general node"),
        [TaskType.HOLD_POSITION] = (AiBehaviour.Post, "Holding position"),
        [TaskType.STAY_IN_HOME] = (AiBehaviour.Post, "Staying home"),
        [TaskType.MAN_THE_GATE] = (AiBehaviour.Post, "Gate guard"),
        [TaskType.MAN_A_TURRET] = (AiBehaviour.Turret, "Turret guard"),
        [TaskType.MAN_A_TURRET_ON_BUILDING] = (AiBehaviour.Turret, "Turret home guard"),
        [TaskType.MAN_A_TURRET_PLAYER_JOB] = (AiBehaviour.Turret, "Turret guard"),
        [TaskType.USE_TURRET] = (AiBehaviour.Turret, "Turret gunner"),
        [TaskType.SIT_ON_THRONE] = (AiBehaviour.Post, "Reigning"),
        [TaskType.FOLLOW_SQUADLEADER] = (AiBehaviour.Follow, "Following leader"),
        [TaskType.FOLLOW_SLAVEMASTER] = (AiBehaviour.Follow, "Following master"),
        [TaskType.BODYGUARD] = (AiBehaviour.Follow, "Bodyguard"),
        [TaskType.FOLLOW_PLAYER_ORDER] = (AiBehaviour.Follow, "Following"),
        [TaskType.GO_HOME_AND_GO_TO_BED] = (AiBehaviour.Sleep, "Going to bed"),
        [TaskType.GO_HOME_AND_GO_TO_BED_SECURE] = (AiBehaviour.Sleep, "Going to bed"),
        [TaskType.SLEEP_ON_FLOOR] = (AiBehaviour.Sleep, "Sleeping on ground"),
        [TaskType.SIT_AROUND] = (AiBehaviour.Sit, "Sitting around"),
        [TaskType.AUTO_LABOURING_MINES] = (AiBehaviour.Work, "Worker"),
        [TaskType.AUTO_LABOURING_MINES_PRETEND] = (AiBehaviour.Work, "Worker"),
        [TaskType.RETREAT_HOMETOWN] = (AiBehaviour.GoHome, "Retreating"),
        [TaskType.RUN_AWAY_HOMETOWN] = (AiBehaviour.GoHome, "Retreating fast"),
        [TaskType.WANDERING_TRADER] = (AiBehaviour.Travel, "Wandering trader"),
        [TaskType.TRAVEL_TO_TARGET_TOWN] = (AiBehaviour.Travel, "Travelling"),
        // To the running package's destination (Blackboard.Destination): the trader's next town; under the retreat packages that list it (signals 1 and 25) the squad's home town
        // (AiSystem.Captives.cs, engine reading).
        [TaskType.TRAVEL_TO_TARGET_TOWN_FAST] = (AiBehaviour.Travel, "Travelling quickly"),
        // Row 215 of the refugee package (and of the campaigns' first phase, whose squads the campaign drives): to its mission, the town or the squad it is to join.
        [TaskType.TRAVEL_TO_TARGET_PACKAGE] = (AiBehaviour.Travel, "Travelling"),
        // Its Task_PatrolTownViolent is not decoded (engine choice): it roams the town it assaults, as patrolling does without a patrol package.
        [TaskType.RUN_AROUND_TOWN_LOOKING_FOR_PEOPLE] = (AiBehaviour.Patrol, "Ransacking town"),
        [TaskType.INVESTIGATE_ALARMS] = (AiBehaviour.Investigate, "Investigating"),
        [TaskType.INVESTIGATE_ALARMS_ALLIES_ONLY] = (AiBehaviour.Investigate, "Investigating"),
    };

    /// <summary>
    /// The combat goals and the stance each gives (<b>engine reading</b>: how the original turns a sighting into an attack is <b>Unknown</b>, docs/game/perception.md;
    /// these goals are the urgent ones its packages list for it). SELF_PRESERVATION (in the <c>AI Goals</c> of 30 races) is not one: fighting back when attacked is the retaliation rule's.
    /// </summary>
    static readonly Dictionary<TaskType, AiStance> Stances = new()
    {
        [TaskType.ATTACK_ENEMIES] = AiStance.Aggressive,
        [TaskType.ATTACK_ENEMIES_AND_NEUTRALS] = AiStance.AggressiveNeutrals,
        [TaskType.TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME] = AiStance.Territorial,
        [TaskType.PROTECT_ALLIES] = AiStance.Defensive,
        [TaskType.PROTECT_OWN_SQUAD] = AiStance.ProtectSquad,
        [TaskType.PROTECT_ALLIES_STAY_IN_TOWN] = AiStance.Defensive,
        [TaskType.ATTACK_TROUBLE_MAKERS] = AiStance.Defensive,
        [TaskType.ATTACK_ENEMIES_OF_MY_SLAVEMASTER] = AiStance.Defensive,
        [TaskType.BODYGUARD] = AiStance.Defensive,
    };

    // Flat tables by type number for the think's hot loop (a dictionary lookup per goal was a third of the think).
    internal const int TypeCount = 291;
    static readonly AiBehaviour[] Behaviours = BuildBehaviours();
    static readonly sbyte[] StanceTable = BuildStances();

    static AiBehaviour[] BuildBehaviours()
    {
        var a = new AiBehaviour[TypeCount];
        foreach (var (type, e) in Table) a[(int)type] = e.Behaviour;
        return a;
    }

    static sbyte[] BuildStances()
    {
        var a = new sbyte[TypeCount];
        Array.Fill(a, (sbyte)-1);
        foreach (var (type, s) in Stances) a[(int)type] = (sbyte)s;
        return a;
    }

    /// <summary>The stance a combat goal gives; null for a goal that is not one.</summary>
    public static AiStance? StanceOf(TaskType type) => (uint)type < TypeCount && StanceTable[(int)type] >= 0 ? (AiStance)StanceTable[(int)type] : null;

    /// <summary>The goal types that are combat goals.</summary>
    public static IEnumerable<TaskType> CombatGoals => Stances.Keys;

    public static AiBehaviour Of(TaskType type) => (uint)type < TypeCount ? Behaviours[(int)type] : AiBehaviour.None;

    /// <summary>The game's display text of a type the engine acts out; the enum name for the rest.</summary>
    public static string Text(TaskType type) => Table.TryGetValue(type, out var e) ? e.Text : type.ToString();

    /// <summary>Every type the engine acts out.</summary>
    public static IEnumerable<TaskType> Built => Table.Keys;
}
