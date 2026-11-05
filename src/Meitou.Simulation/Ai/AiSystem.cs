using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;

namespace Meitou.Simulation.Ai;

/// <summary>Settings of the <see cref="AiSystem"/>; the defaults are the game's (engine choices unless a doc label says otherwise).</summary>
public sealed class AiSettings
{
    /// <summary>A character thinks every this many ticks (staggered by slot), and at once when its squad starts another package or its patrol moves on.</summary>
    public int ThinkEveryTicks { get; init; } = 15;
    /// <summary>Ticks between two package choices of a squad.</summary>
    public int PackageEveryTicks { get; init; } = 30;
    /// <summary>A character farther than this from its spot (node, bed, post) walks back to it.</summary>
    public float PostSlack { get; init; } = 12;
    /// <summary>The radius a character mills about its post in (<see cref="AiBehaviour.Mill"/>).</summary>
    public float MillRadius { get; init; } = 25;
    /// <summary>The score of the goal being acted out is multiplied by this (<b>Observed</b>, docs/game/ai.md "Scoring"), so a near tie does not flip.</summary>
    public float Continuity { get; init; } = 1.25f;
    /// <summary>A patrol package may start this many game hours after the last one started (uniform), and the first one within <see cref="FirstPatrolHours"/> of the squad appearing.</summary>
    public float PatrolEveryHoursMin { get; init; } = 6;
    public float PatrolEveryHoursMax { get; init; } = 12;
    public float FirstPatrolHours { get; init; } = 4;
    /// <summary>A patrol gives up after this many game hours.</summary>
    public float PatrolGiveUpHours { get; init; } = 6;
    /// <summary>A patrol's route: this many points round the home town, between these multiples of the town's radius from its centre, then home.</summary>
    public int PatrolPoints { get; init; } = 4;
    public float PatrolRadiusMin { get; init; } = 1.5f;
    public float PatrolRadiusMax { get; init; } = 3f;
    /// <summary>The patrol's leader is at a route point within this distance.</summary>
    public float RouteArrive { get; init; } = 40;
    /// <summary>
    /// The original's <c>attacks</c> (settings.cfg "Town attacks frequency": 0 Bombardment, 1 High, 2 Normal, 3 Fewer, 4 Rare, 5 Never; default 2): a faction's wait between
    /// passing-by assaults on the player's towns (docs/game/ai.md "Package classes"); Never stops them.
    /// </summary>
    public int TownAttacks { get; set; } = 2;
}

/// <summary>
/// Starts a contract package on a squad for some game hours, aimed at <see cref="Target"/> (docs/game/ai.md "Dialogue and AI": dialogue's <c>AI contract</c>, the target the
/// conversation's partner).
/// </summary>
public sealed record StartContract(int SquadId, string PackageId, float Hours, CharacterId? Target = null) : SimCommand;

/// <summary>Replaces a squad's packages with one (dialogue's <c>change AI</c>).</summary>
public sealed record ChangeAi(int SquadId, string PackageId) : SimCommand;

/// <summary>
/// The AI (docs/simulation.md "AI"; the original's in docs/game/ai.md). Squads whose template lists <c>AI packages</c> get a <see cref="Blackboard"/> that picks
/// the package by slot and package class; each member thinks on its own cadence: it scores the goals of its role list, its race's <c>AI Goals</c> and the
/// default idle goal by tier (urgent, then non-urgent, then fluff; the first tier with a positive score wins), weight, hour window, crowding and continuity,
/// acts out the winner through <see cref="TaskBehaviours"/> (in its home building's nodes and beds where it has one), and takes its combat stance from its
/// combat goals (<see cref="AggroSystem"/> reads it). The AI runs only characters that nothing else holds: not the player's, not fighting, fleeing, down,
/// carrying, carried, treating, looting or trading. Roaming platoons keep their travel stand-in (<see cref="PopulationSystem"/>; their off-screen mode follows
/// their package, <see cref="PlatoonJobs"/>).
/// <list type="bullet">
/// <item><c>Think</c> (parallel): the stance, the choice and the act-out write only the character's own state (its task, goal, path flags and <see cref="AiMind"/>);
/// they read the squad's blackboard, which only the serial steps change.</item>
/// <item><c>Inputs</c> (serial): contracts. <c>SlowWorld</c> (serial): blackboards, homes and minds for new squads; on the package cadence the intact test, the package
/// choice, the patrol's progress and the crowding counts.</item>
/// </list>
/// </summary>
public sealed partial class AiSystem(PopulationData data, AiSettings? settings = null) : ITickSystem, IStateHashed
{
    /// <summary>Every character's default goal (docs/game/ai.md "Default tasks per character", Observed): IDLE at the fluff tier, weight 0.1.</summary>
    public static readonly AiGoal Idle = new(new AiTask("", "IDLE", TaskType.IDLE, TaskPriority.Fluff, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 100, 0.1f);

    /// <summary>Walks to a spot in a row that may end elsewhere before it gives the spot up.</summary>
    public const int MaxFailedWalks = 3;

    /// <summary>How near a turret's gunner spot counts as arrived (units): well inside the turret's <c>use range</c>, which is 10 for the base game's turrets.</summary>
    public const float TurretSlack = 3;
    /// <summary>How near an enemy must be for <c>isNoEnemiesInVicinity</c> to fail (units; <b>engine choice</b>, about a turret's range: the original's radius is <b>Unknown</b>).</summary>
    public const float EnemyVicinity = 1000;
    FactionRelations Relations => data.Relations;

    /// <summary>The step that separates two candidates of the same score (the original's 0.0001).</summary>
    const float TieStep = 1e-4f;

    static readonly TaskPriority[] Tiers = [TaskPriority.Urgent, TaskPriority.NonUrgent, TaskPriority.Fluff];

    public AiSettings Settings { get; } = settings ?? new AiSettings();
    PopulationData Data => data;
    /// <summary>The towns' alarms the investigators answer (<see cref="AiBehaviour.Investigate"/>); null: none are raised.</summary>
    public Crime.CrimeSystem? Crimes { get; set; }

    /// <summary>The alarm an investigator answers: its squad's town's nearest in reach, for goal 210 only its own faction's (<see cref="Crime.CrimeSystem.AlarmFor"/>).</summary>
    internal Crime.TownAlarm? AlarmOf(World world, int slot, TaskType type, Blackboard board) =>
        Crimes is { Alarms.Count: > 0 } crimes && world.Characters.Cold(slot) is { } cold
            ? crimes.AlarmFor(board.Town, world.Characters.Previous[slot].Position, type == TaskType.INVESTIGATE_ALARMS_ALLIES_ONLY ? cold.Faction : -1) : null;

    readonly Dictionary<string, (IReadOnlyList<SlottedPackage> Packages, SquadTemplate? Template)> templates = [];
    // Squads already looked at (ids are never reused); derived from the squads, so not part of the hash.
    readonly HashSet<int> looked = [];
    // The turret indices of each town claimed in the current cadence pass (derived, not hashed).
    readonly Dictionary<int, HashSet<int>> turretTaken = [];
    // The seats of each bar claimed in the current cadence pass, by building (derived, not hashed).
    readonly Dictionary<TownBuilding, HashSet<int>> seatTaken = new(ReferenceEqualityComparer.Instance);
    readonly Rules rules = new();

    // ------------------------------------------------------------------ think (parallel)

    public void Think(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int every = Math.Max(1, Settings.ThinkEveryTicks);
        int hour = world.Hour;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || table.Cold(i) is not { Ai: { Squad: { Ai: { } board } squad } mind } cold) continue;
            // Cheapest test first: most characters are not due this tick.
            if ((world.Tick + i) % every != 0 && mind.Version == board.Version) continue;
            var id = table.IdOf(i);
            var roles = ObeyedRoles(world, squad, cold) ?? board.Roles[(int)RoleOf(squad, id, cold)];
            if (!ReferenceEquals(mind.StanceFrom, roles) || mind.StanceHour != hour)
            {
                // The stance changes only with the role list (a package switch) and the hour.
                mind.Stance = StanceOf(mind, roles, hour);
                (mind.StanceFrom, mind.StanceHour) = (roles, hour);
            }
            ref var n = ref table.Next[i];
            if (!Holds(n, cold)) continue;
            var goal = Choose(world, i, id, mind, squad, board, roles, hour);
            Act(world, i, mind, goal, board);
        }
    }

    /// <summary>Whether the AI runs the character now: it has a mind and nothing else holds it (a fight, a flight, an order, a carry, a trade, a knock-out).</summary>
    public static bool Holds(in CharacterHot n, CharacterCold cold)
    {
        if (cold.Ai is not { } mind || cold.IsPlayer || cold.InCombat || cold.Fleeing || cold.Engaged || cold.IsImprisoned || !cold.CarriedBy.IsNone || !cold.Carrying.IsNone || cold.Medical is { Incapacitated: true }) return false;
        return (CharacterTask)n.Task switch
        {
            CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow => true,
            // The walks to its spots, and lying or sitting there, are the AI's own.
            CharacterTask.GoTo => mind.Behaviour is AiBehaviour.Post or AiBehaviour.Sleep or AiBehaviour.Sit or AiBehaviour.GoHome or AiBehaviour.Patrol or AiBehaviour.Turret
                or AiBehaviour.TownNode or AiBehaviour.Shop or AiBehaviour.Relax or AiBehaviour.ToTown or AiBehaviour.Travel or AiBehaviour.Work or AiBehaviour.Investigate
                or AiBehaviour.PatrolTown,
            CharacterTask.Rest => mind.Behaviour is AiBehaviour.Sleep or AiBehaviour.Sit or AiBehaviour.Relax,
            // In the bed it went to (or lying in one when loaded, before its first think).
            CharacterTask.Sleep => cold.InSomething == Locks.HeldState.Bed && (mind.Behaviour == AiBehaviour.Sleep || mind.Goal is null),
            _ => false,
        };
    }

    /// <summary>
    /// Job 187 Obedient Slave (<c>FUN_14050e5b0</c>, docs/game/ai-tasks.md "Obedient Slave"): a slave (state 1) not sneaking, owned by another squad, takes that squad's
    /// <c>Slave AI Goals</c> instead of its own lists. Null: it plans with its own squad's (a slave in its owner's squad already takes the Slave role there).
    /// </summary>
    static IReadOnlyList<AiGoal>? ObeyedRoles(World world, Squad squad, CharacterCold cold) =>
        cold.SlaveState == 1 && cold.SlaveMaster >= 0 && cold.SlaveMaster != squad.Id && (cold.Orders & CharacterOrders.Sneak) == 0
            && world.Squads.Find(cold.SlaveMaster) is { Ai: { } master } && master.Roles[(int)AiRole.Slave] is { Count: > 0 } list ? list : null;

    /// <summary>The role list a member takes (docs/game/ai.md "Goal loading"): the squad leader the leader's, else by its squad role.</summary>
    internal static AiRole RoleOf(Squad squad, CharacterId id, CharacterCold cold) =>
        squad.Leader == id || cold.Role == (int)SquadRole.Leader ? AiRole.Leader
        : cold.Role == (int)SquadRole.Squad2 ? AiRole.Squad2
        : cold.Role == (int)SquadRole.Slave ? AiRole.Slave
        : AiRole.Squad;

    /// <summary>
    /// The stance from the combat goals of its role list and race in their hours (the most aggressive wins); a character whose lists hold no combat goal at all keeps
    /// <see cref="AiStance.Aggressive"/>, the rule every NPC had before the AI (how the original turns a sighting into an attack is <b>Unknown</b>).
    /// </summary>
    internal static AiStance StanceOf(AiMind mind, IReadOnlyList<AiGoal> roles, int hour)
    {
        bool any = false, allies = false;
        var stance = AiStance.Passive;
        void Take(AiGoal g)
        {
            if (TaskBehaviours.StanceOf(g.Task.Type) is not { } s) return;
            any = true;
            if (!g.InWindow(hour)) return;
            if (s > stance) stance = s;
            allies |= g.Task.Type is TaskType.PROTECT_ALLIES or TaskType.PROTECT_ALLIES_STAY_IN_TOWN;
        }
        for (int k = 0; k < roles.Count; k++) Take(roles[k]);
        for (int k = 0; k < mind.RaceGoals.Count; k++) Take(mind.RaceGoals[k]);
        mind.HasCombatGoals = any;
        mind.ProtectsAllies = allies;
        return any ? stance : AiStance.Aggressive;
    }

    /// <summary>
    /// The goal to act out, with its plan in <see cref="AiMind.Plan"/>: by tier (urgent, non-urgent, fluff), the goals with a positive score highest first,
    /// leaving out one whose list A already holds; the first whose plan succeeds wins (docs/game/ai-tasks.md "Planner": the pick). Idle when none does.
    /// </summary>
    AiGoal Choose(World world, int slot, CharacterId id, AiMind mind, Squad squad, Blackboard board, IReadOnlyList<AiGoal> roles, int hour)
    {
        var race = mind.RaceGoals;
        var candidates = Candidates;
        var plans = PlanContext.Local.Bind(this, world, slot, mind, board);
        foreach (var tier in Tiers)
        {
            candidates.Clear();
            void Consider(AiGoal g)
            {
                if (g.Task.Priority != tier || !g.InWindow(hour)) return;
                var profile = TaskProfiles.Of(g.Task.Type);
                float desire = Desire(world, TaskBehaviours.Of(g.Task.Type), g.Task.Type, profile, squad, board, mind, id, g.Task.Target);
                if (desire <= 0) return;
                int crowd = board.Crowd.GetValueOrDefault(g.Task.Type) - (mind.Goal?.Task.Type == g.Task.Type ? 1 : 0);
                float score = desire * g.Weight * (crowd > 0 ? MathF.Pow(profile.Crowd, crowd) : 1);
                if (mind.Goal is { } current && current.Task.Type == g.Task.Type && current.Task.Target == g.Task.Target)
                {
                    score *= Settings.Continuity;
                    // A goal it committed to holds at least 0.4 until the commitment runs out, then at most 0.05 (docs/game/ai.md "Packages and goals", Observed).
                    if (profile.Commits) score = world.GameHours < mind.CommitUntil ? MathF.Max(score, 0.4f) : MathF.Min(score, 0.05f);
                }
                if (score <= 0) return;
                // Highest first. The original keys its candidates by score and, while the key is taken, adds 0.0001 (FUN_14050df10, Verified): of equal
                // scores the goal listed later ranks first.
                for (int k = 0; k < candidates.Count; k++)
                    if (candidates[k].Score == score) { score += TieStep; k = -1; }
                int at = candidates.Count;
                while (at > 0 && candidates[at - 1].Score < score) at--;
                candidates.Insert(at, (g, score, desire));
            }
            // Index loops: a foreach over the interface would box an enumerator per list and pass.
            for (int k = 0; k < roles.Count; k++) Consider(roles[k]);
            for (int k = 0; k < race.Count; k++) Consider(race[k]);
            for (int k = 0; k < candidates.Count; k++)
                if (plans.TryPlan(candidates[k].Goal, candidates[k].Desire)) return candidates[k].Goal;
        }
        // The default task (IDLE, weight 0.1) is no candidate of a tier: the chooser tries it only when every tier failed (FUN_140510ef0, Observed).
        if (plans.TryPlan(Idle, Desire(world, TaskBehaviours.Of(Idle.Task.Type), Idle.Task.Type, TaskProfiles.Of(Idle.Task.Type), squad, board, mind, id, Idle.Task.Target))) return Idle;
        mind.Plan.Clear();
        return Idle;
    }

    [ThreadStatic] static List<(AiGoal Goal, float Score, float Desire)>? candidates;
    static List<(AiGoal Goal, float Score, float Desire)> Candidates => candidates ??= [];

    /// <summary>
    /// How much the character wants a goal: its type's desire (<see cref="TaskProfiles"/>; a type with a desire function of its own, 1), times what the goal's target
    /// finder found (docs/game/ai-tasks.md "Town goals", Observed: 0 when it found nothing). Going home is 0.5 while away from the home town and 0 in it
    /// (docs/game/perception.md 7.2, Observed); a goal the engine cannot act out is 0.
    /// </summary>
    float Desire(World world, AiBehaviour behaviour, TaskType type, TaskProfile profile, Squad squad, Blackboard board, AiMind mind, CharacterId self, TaskTargetType targeting)
    {
        if (behaviour == AiBehaviour.None) return 0;
        float desire = profile.Desire switch
        {
            TaskProfiles.Random => 0.01f + 0.4f * Rng.Float(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiDesire ^ (ulong)world.Tick << 9 ^ (ulong)type)),
            TaskProfiles.Special => 1,
            var d => d,
        };
        return desire * behaviour switch
        {
            // A goal aimed at the squad's mission follows the contract's target (a bodyguard its employer), wanted only while there is one alive.
            AiBehaviour.Follow when targeting == TaskTargetType.SquadMission => board.MissionTarget.IsNone || board.MissionTarget == self || !world.Characters.IsAlive(board.MissionTarget) ? 0 : 1,
            AiBehaviour.Follow => squad.Leader.IsNone || squad.Leader == self || !world.Characters.IsAlive(squad.Leader) ? 0 : 1,
            AiBehaviour.GoHome => AtHomeTown(world, squad, board, self) ? 0 : 0.5f,
            // A turret is only wanted by a member its squad gave one (the nearest free one in <see cref="ClaimTurrets"/>).
            AiBehaviour.Turret => board.TurretClaims.ContainsKey(self.Slot) ? 1 : 0,
            AiBehaviour.TownNode => WanderIn(board.Town, world.Characters.Previous[self.Slot].Position).Count > 0 || OccupiedBuildings(world, board) > 0 ? 1 : 0,
            // Row 36 has no finder; its walk needs a town to stroll round (engine choice: the original's walk without one, round the task's target, is not decoded).
            AiBehaviour.PatrolTown => board.Town >= 0 || OccupiedBuildings(world, board) > 0 ? 1 : 0,
            AiBehaviour.Shop => Places(board.Town).Shops.Count > 0 ? 1 : 0,
            // A worker needs a mine, bench or refinery in its town (the finders return 0 without one).
            AiBehaviour.Work => Places(board.Town).Workplaces.Count > 0 ? 1 : 0,
            // Relaxing needs a free seat of the package's bar, sitting around a free chair of the home (claimed in <see cref="ClaimSeats"/>); without a home it sits where it is.
            AiBehaviour.Relax => board.BarTable >= 0 && board.SeatClaims.ContainsKey(self.Slot) && !mind.SeatUnreachable ? 1 : 0,
            AiBehaviour.Sit => board.Home is null || board.SeatClaims.ContainsKey(self.Slot) && !mind.SeatUnreachable ? 1 : 0,
            AiBehaviour.ToTown => TownAt(world.Characters.Previous[self.Slot].Position) >= 0 ? 0 : 1,
            // To the package's mission town while not in it.
            AiBehaviour.Travel => board.RefugeeTarget >= 0 ? 1 : board.Destination.IsNone ||CurrentTown(world, world.Characters.Previous[self.Slot].Position) == board.Destination ? 0 : 1,
            // Only while an alarm of its town is in reach (the row's done state !ALARMS_IN_THE_VICINITY).
            AiBehaviour.Investigate => AlarmOf(world, self.Slot, type, board) is null ? 0 : 1,
            _ => 1,
        };
    }

    /// <summary>The nearest town whose <see cref="TownData.Radius"/> holds a point (as the game's position lookup, <c>FUN_140928890</c>), -1 when none.</summary>
    internal int TownAt(Vector3 p)
    {
        var at = Flat(p);
        int best = -1;
        float bestD = float.MaxValue;
        for (int s = 0; s < data.Sites.Count; s++)
        {
            var site = data.Sites[s];
            float r = site.Town.Radius, d = Vector2.DistanceSquared(at, Flat(site.Position));
            if (d <= r * r && d < bestD) (best, bestD) = (s, d);
        }
        return best;
    }

    /// <summary>A town's public buildings with a general node by zone, its shops, its public bars and its outdoor tables, as <see cref="RefreshPlaces"/> last found them.</summary>
    sealed record TownPlaces(IReadOnlyDictionary<ZoneCoordinate, TownBuilding[]> Wander, IReadOnlyList<TownBuilding> Shops, IReadOnlyList<TownBuilding> Bars,
        IReadOnlyList<TownBuilding> OutdoorTables, IReadOnlyList<TownBuilding> Workplaces);

    static readonly TownPlaces NoPlaces = new(new Dictionary<ZoneCoordinate, TownBuilding[]>(), [], [], [], []);
    // Rebuilt in the serial step, read by the think on every worker (derived, not hashed).
    readonly Dictionary<int, TownPlaces> places = [];

    TownPlaces Places(int site) => site >= 0 && places.TryGetValue(site, out var p) ? p : NoPlaces;

    /// <summary>The buildings wandering in <paramref name="site"/> can go to from <paramref name="at"/>: its public ones with a general node in the zone of that point.</summary>
    IReadOnlyList<TownBuilding> WanderIn(int site, Vector3 at) => Places(site).Wander.TryGetValue(WorldLayout.ZoneOf(at.X, at.Z), out var list) ? list : [];

    /// <summary>
    /// The towns' places for the AI's squads (serial, on the package cadence). A bar is a building whose residents' template has <c>building designation</c> BAR
    /// (the original's hang-around-bar package picks one of those in the home town, docs/game/ai-tasks.md "Town goals", Observed); a shop one whose residents
    /// trade (<see cref="SquadTemplate.HasShop"/>; the original's shopping looks for BF_SHOP furniture inside, which Meitou does not read: engine choice),
    /// and public, as the original's shop finders require.
    /// Residents are the squads living there, else the level data's <c>residents template</c>; the layout flags <c>is bar</c> / <c>is shop</c> count too.
    /// A building is public as its residents' template's <c>public day</c> (the original writes it over the home's flag), else as its level state's
    /// <c>is public day</c>, else public (engine choice: the original's default is Unknown); a town building the player owns as its Open to public toggle once set
    /// (the stats panel, <see cref="Locks.FixtureRegistry.PublicOf"/>).
    /// </summary>
    void RefreshPlaces(World world)
    {
        places.Clear();
        var shops = new HashSet<TownBuilding>(ReferenceEqualityComparer.Instance);
        var bars = new HashSet<TownBuilding>(ReferenceEqualityComparer.Instance);
        var publicFlag = new Dictionary<TownBuilding, bool>(ReferenceEqualityComparer.Instance);
        void Note(TownBuilding b, SquadTemplate? t)
        {
            if (t is null) return;
            if (t.HasShop) shops.Add(b);
            if (t.BuildingDesignation == DesignationBar) bars.Add(b);
            publicFlag.TryAdd(b, t.PublicDay);
        }
        foreach (var squad in world.Squads.All)
            if ((squad.Ai?.OwnHome ?? squad.HomeBuilding) is { } home) Note(home, TemplateOf(squad.TemplateId).Template);
        foreach (var squad in world.Squads.All)
            for (int pass = 0; pass < 2; pass++)
            {
                // The squad's town, and the one it is in (a bar package away from home takes that town's bar).
                int site = pass == 0 ? squad.Ai?.Town ?? -1 : squad.Ai?.InTown.Site ?? -1;
                if (site >= 0 && !places.ContainsKey(site)) AddPlaces(site);
            }

        void AddPlaces(int site)
        {
            var buildings = data.Sites[site].Buildings;
            foreach (var b in buildings)
                if (b.ResidentsTemplate.Length > 0) Note(b, TemplateOf(b.ResidentsTemplate).Template);
            bool IsPublic(TownBuilding b) => world.Fixtures.PublicOf(b.Key) ?? (publicFlag.TryGetValue(b, out bool open) ? open : b.LevelState?.GetBool("is public day", true) ?? true);
            var wander = buildings.Where(b => IsPublic(b) && b.Spots.Any(IsGeneralNode)).GroupBy(b => b.Zone).ToDictionary(g => g.Key, g => g.ToArray());
            places[site] = new(wander, [.. buildings.Where(b => (b.IsShop || shops.Contains(b)) && IsPublic(b))], [.. buildings.Where(b => (b.IsBar || bars.Contains(b)) && IsPublic(b))],
                [.. buildings.Where(b => b.IsOutdoorTable)], [.. buildings.Where(b => !b.Destroyed && b.Function is >= 0 and var fn && (WorkFunctions >> fn & 1) != 0)]);
        }
    }

    /// <summary>BuildingDesignation BD_BAR.</summary>
    const int DesignationBar = 3;

    /// <summary>The BuildingFunction bits a worker looks for (mask <c>0x41a</c> of <c>FUN_1408cac60</c> / <c>FUN_1408cadb0</c>, read as bits, <b>Observed</b>): MINE, RESEARCH, REFINERY, CRAFTING.</summary>
    const int WorkFunctions = 0x41a;

    /// <summary>How near a worker's building it stands (units; engine choice: the functionality's <c>use range</c> is not read for the town's buildings).</summary>
    internal const float WorkReach = 30;

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> workClips = new(StringComparer.Ordinal);

    /// <summary>The clip a worker plays at a building: its functionality's <c>animation</c>, else <c>stonemine</c> (<c>Task_OperateMachine</c>, docs/game/buildings-production.md).</summary>
    string WorkClipOf(TownBuilding b) => workClips.GetOrAdd(b.BuildingId, id =>
        data.Db.Find(id)?.GetReferences("functionality") is [var link, ..] && data.Db.Find(link.TargetStringId)?.GetReferences("animation") is [var anim, ..]
            && data.Db.Find(anim.TargetStringId) is { } clip ? clip.Name : "stonemine");

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> workHungerRates = new(StringComparer.Ordinal);

    /// <summary>
    /// The hunger rate of a worker at a building: its functionality's <c>hunger rate</c> (1 without one), which <c>Task_OperateMachine</c>'s set-up (<c>FUN_14035b430</c>, <b>Verified</b>) writes
    /// to the operator's CharStats <c>+0x178</c> for the real (87) and the pretend (221) act-out alike.
    /// </summary>
    float WorkHungerRateOf(TownBuilding b) => workHungerRates.GetOrAdd(b.BuildingId, id =>
        data.Db.Find(id)?.GetReferences("functionality") is [var link, ..] && data.Db.Find(link.TargetStringId) is { } function ? function.GetFloat("hunger rate", 1) : 1);

    /// <summary>
    /// The building a worker works at (the finders of goals 189 / 190: a random building of the town with a function in <see cref="WorkFunctions"/>, slot 2 of the building finder
    /// <c>FUN_1409fb020</c>); the roll is the character's and its <paramref name="epoch"/> of picks (<see cref="AiMind.WorkEpoch"/>), so asking twice names the same one. Null: the town has none.
    /// </summary>
    TownBuilding? PickWork(World world, CharacterId self, int town, int epoch)
    {
        var list = Places(town).Workplaces;
        if (list.Count == 0 || world.Characters.Cold(self.Slot) is not { } worker) return null;
        bool Allowed(TownBuilding b)
        {
            int owner = world.Buildings.Find(b.Key)?.Owner ?? world.Fixtures.TownMachineOwner(b.Key) ?? -1;
            return owner >= 0 && (owner == worker.Faction || worker.SlaveState == 1 && worker.SlaveOwner == owner);
        }
        int count = 0;
        foreach (var b in list) if (Allowed(b)) count++;
        if (count == 0) return null;
        int pick = Rng.Int(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)epoch << 20 ^ 0x57), count);
        foreach (var b in list) if (Allowed(b) && pick-- == 0) return b;
        return null;
    }

    static bool IsGeneralNode(HomeSpot s) => s.NodeType == NodeGeneral;

    /// <summary>BUILDING <c>node type</c> NODE_GENERAL.</summary>
    const int NodeGeneral = 1;

    /// <summary>
    /// Where a town goal goes (docs/game/ai-tasks.md "Town goals"): wandering in town, a random general node of a random public building of the town in the walker's
    /// zone (Observed; the original draws from all of those and finds nothing in one without such a node, Meitou draws from those with one); shopping, a random shop of the town,
    /// at a general node in it, else at it (the original first looks for a shop selling what the character wants to buy: shopping lists are not modelled); the
    /// wanderer, the nearest town. Null when there is none.
    /// </summary>
    Vector3? TargetFor(World world, CharacterId self, AiBehaviour behaviour, int town)
    {
        ulong roll = Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)world.Tick << 8 ^ (ulong)behaviour);
        var places = Places(town);
        switch (behaviour)
        {
            case AiBehaviour.TownNode:
            case AiBehaviour.Shop:
            {
                var from = behaviour == AiBehaviour.TownNode ? WanderIn(town, world.Characters.Previous[self.Slot].Position) : places.Shops;
                if (from.Count == 0) return null;
                var b = from[Rng.Int(roll, from.Count)];
                // A general node; in a shop without one a guard post, else any other node (engine choice: where the original's Task_Shopping stands is not decoded).
                foreach (var accept in (ReadOnlySpan<Func<HomeSpot, bool>>)[IsGeneralNode, s => s.Kind == HomeSpotKind.Guard, s => s.IsNode])
                {
                    int nodes = 0;
                    foreach (var s in b.Spots) if (accept(s)) nodes++;
                    if (nodes == 0) continue;
                    int pick = Rng.Int(Rng.Mix(roll), nodes);
                    foreach (var s in b.Spots)
                        if (accept(s) && pick-- == 0) return s.Position;
                }
                return b.Position;
            }
            case AiBehaviour.ToTown:
            {
                var at = Flat(world.Characters.Previous[self.Slot].Position);
                int best = -1;
                float bestD = float.MaxValue;
                for (int s = 0; s < data.Sites.Count; s++)
                {
                    float d = Vector2.DistanceSquared(at, Flat(data.Sites[s].Position));
                    if (d < bestD) (best, bestD) = (s, d);
                }
                return best < 0 ? null : data.Sites[best].Position;
            }
        }
        return null;
    }

    /// <summary>
    /// Acts out 36 Patrolling town (<c>Task_PatrolTown</c>, docs/game/ai-tasks.md "Town goals"): walks to the point drawn round its town (<see cref="PatrolPoint"/>; an occupier, whose town is the
    /// one it holds as the original's blackboard town is the campaign's target first, to a building of that player town) and, once the walk is over, forgets it, so the next think draws
    /// another, as the original's update sends a new move each time the last one ended.
    /// </summary>
    void Patrol(World world, int slot, AiMind mind, Blackboard board)
    {
        var self = world.Characters.IdOf(slot);
        mind.Target ??= OccupiedSpot(world, board, self) ?? PatrolPoint(world, self, board.Town);
        if (mind.Target is not { } point)
        {
            Become(world.Characters, slot, CharacterTask.Idle);
            return;
        }
        if (!GoToSpot(world.Walkability, world.Characters, slot, mind, Flat(point), GiveUp.Target, SpeedMode.Walk, height: float.IsNaN(point.Y) ? null : point.Y)) return;
        Become(world.Characters, slot, CharacterTask.Idle);
        mind.Target = null;
    }

    /// <summary>The least scatter of a patrol round its town (<c>FUN_1405c6b30</c>, <c>DAT_14168dcf4</c>).</summary>
    const float PatrolTownMinScatter = 500;

    /// <summary>
    /// Where 36 Patrolling town walks next (<c>Task_PatrolTown</c> through <c>FUN_1405c6b30</c> and <c>setPatrolInput</c>, docs/game/ai-tasks.md "Town goals", Observed): its town's centre moved by up
    /// to half the scatter in X and Z, the scatter being half the town's radius but at least 500; of up to 10 draws the first on walkable ground (the original redraws a point on a building that
    /// is not public, allied or the player's, or under water for a race that avoids it; Meitou tests the ground only). Null without a town or a walkable draw.
    /// </summary>
    Vector3? PatrolPoint(World world, CharacterId self, int town)
    {
        if (town < 0 || town >= data.Sites.Count) return null;
        var site = data.Sites[town];
        float scatter = MathF.Max(PatrolTownMinScatter, site.Town.Radius * 0.5f);
        ulong roll = Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)world.Tick << 8 ^ (ulong)AiBehaviour.PatrolTown);
        for (int i = 0; i < 10; i++)
        {
            roll = Rng.Mix(roll);
            float x = site.Position.X + (Rng.Float(roll) - 0.5f) * scatter;
            roll = Rng.Mix(roll);
            float z = site.Position.Z + (Rng.Float(roll) - 0.5f) * scatter;
            if (world.Walkability.IsWalkable(x, z)) return new Vector3(x, float.NaN, z);
        }
        return null;
    }

    bool AtHomeTown(World world, Squad squad, Blackboard board, CharacterId self)
    {
        if (board.Town < 0 || !world.Characters.IsAlive(self)) return true;
        var site = data.Sites[board.Town];
        var p = world.Characters.Previous[self.Slot].Position;
        float r = site.Town.Radius;
        return Vector2.DistanceSquared(new Vector2(p.X, p.Z), new Vector2(site.Position.X, site.Position.Z)) <= r * r;
    }

    /// <summary>Acts out a goal: sets the movement task it maps to, unless the character is already doing it.</summary>
    void Act(World world, int slot, AiMind mind, AiGoal goal, Blackboard board)
    {
        var table = world.Characters;
        var type = goal.Task.Type;
        var behaviour = TaskBehaviours.Of(type);
        var profile = TaskProfiles.Of(type);
        // A goal taken up anew starts over: a fresh target and, for a goal it commits to, a fresh commitment. So does a committed goal chosen again after its
        // commitment ran out (engine reading: the original's target finders roll a new target then, so the goal counts as new).
        bool fresh = mind.Goal is not { } before || before.Task.Type != type || before.Task.Target != goal.Task.Target
            || profile.Commits && world.GameHours >= mind.CommitUntil;
        if (fresh)
        {
            mind.Walking = false;
            mind.FailedWalks = 0;
            mind.Target = null;
            // A spot given up is tried again from wherever the character is now (engine choice: the walk may have failed from the old place only).
            mind.HomeUnreachable = false;
            if (type is not (TaskType.RELAX_IN_TOWN_PACKAGE or TaskType.SIT_AROUND)) mind.SeatUnreachable = false;
            if (profile.Commits)
            {
                ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(slot)), RngPurpose.Think, SeedSalts.AiCommit ^ (ulong)world.Tick << 9 ^ (ulong)type);
                mind.CommitUntil = world.GameHours + profile.CommitMin + Rng.Float(roll) * profile.CommitRange;
            }
        }
        // Staying home sits on the chair its squad claimed for it (the original's 43 operates home furniture, chairs among it); without one it stands at home.
        if (type == TaskType.STAY_IN_HOME && board.SeatClaims.ContainsKey(slot) && !mind.SeatUnreachable) behaviour = AiBehaviour.Sit;
        if (behaviour == AiBehaviour.Investigate && (fresh || mind.Behaviour != AiBehaviour.Investigate)) mind.LookAbout = Crime.CrimeSystem.FirstLookAboutSeconds;
        mind.Goal = goal;
        mind.Behaviour = behaviour;
        mind.Version = board.Version;
        mind.WorkOperate = null;
        mind.CollectFrom = null;
        mind.WanderRadius = 0;
        mind.SitClip = null;   // set again below while it stays on its chair
        mind.SitHungerRate = 1;
        if (mind.WorkActive && behaviour != AiBehaviour.Work)
        {
            // It stopped working: the next pick is a new one and the work clip goes.
            mind.WorkActive = false;
            mind.WorkEpoch++;
            if (table.Cold(slot) is { } worker && mind.WorkClip is not null && worker.ActionClip == mind.WorkClip) worker.ActionClip = null;
            mind.WorkClip = null;
            mind.WorkBuilding = null;
        }
        mind.FollowTarget = CharacterId.None;   // set again below for a follow goal aimed at the squad's mission
        ref var n = ref table.Next[slot];
        bool leader = mind.Squad.Leader == table.IdOf(slot);
        // Another goal gets it out of its bed (the get-up step's own think, or the goal's act-out where the plan has none); it stands where it lay.
        if (behaviour != AiBehaviour.Sleep && table.Cold(slot)!.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, table.IdOf(slot));
        // Steps of the plan before the goal's own act-out: getting out of bed, sheathing (one think each); the walk and the rest are the goal's.
        switch (mind.Step)
        {
            case TaskType.GET_OUT_OF_BED or TaskType.GET_OUT_OF_BED_IF_ITS_EMERGENCY or TaskType.GET_OUT_OF_BED_ONCE_HEALED:
                Become(table, slot, CharacterTask.Idle);
                return;
            case TaskType.UNEQUIP_WEAPON:
                table.Cold(slot)!.DrawnWeapon = WeaponKinds.None;
                return;
            case TaskType.EQUIP_WEAPON or TaskType.EQUIP_CROSSBOW:
                // Task_EquipWeapon (6, 264): the weapon it carries comes out; the planner chose this step only with one in hand (B list 38, hasWeapon behind it).
                var armed = table.Cold(slot)!;
                if (armed.Fighter is { } fighter) armed.DrawnWeapon = Combat.CombatSystem.MeleeDrawn(fighter);
                return;
            case TaskType.GO_HOMEBUILDING:
                // 19 Going home before the goal (43 Staying home and the like away from home): the walk home of the GoHome act-out.
                behaviour = AiBehaviour.GoHome;
                break;
            case TaskType.ENTER_BUILDING:
                // 156 Task_EnterBuilding: the walk into its home building (the GoHome walk), done once inside.
                behaviour = AiBehaviour.GoHome;
                break;
            case TaskType.LEAVE_BUILDING when mind.Squad.Ai?.Home is { } leaving && world.Sight.BuildingOf(slot) == leaving.Key:
                // 200 Task_LeaveBuilding: to 1.1 of the door's span past its outer point (A + (A - B) x 1.1) of the home's first door, until outside.
                if (LeavePoint(world, leaving) is { } outside && !GoToSpot(world.Walkability, table, slot, mind, outside, GiveUp.Target, SpeedMode.Walk, slack: 10)) return;
                break;
            case TaskType.GET_NEAR_TO when !mind.Plan[0].Target.IsNone:
                // 40 Following to within 10 of the plan's target, then the goal's own act-out.
                if (!GoToSpot(world.Walkability, table, slot, mind, Flat(mind.Plan[0].Target.Position), GiveUp.Target, SpeedMode.Walk, slack: 10)) return;
                break;
        }
        if (mind.Resume)
        {
            // Back from its town's time unloaded: it is already where the plan's walk ends (docs/simulation.md "Off-screen tier", residents' schedule).
            mind.Resume = false;
            if (mind.Step is TaskType.MOVE_ON_FREE_WILL or TaskType.MOVE_ON_FREE_WILL_FAST && !mind.Plan[0].Target.IsNone)
            {
                var end = mind.Plan[0].Target.Position;
                // On a ring round the spot by member (engine choice): members sharing a bed or node are not put on one point, where separation would throw them off it.
                float angle = mind.MemberIndex * 2.39996f;
                end += new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * ResumeRing;
                var walk = world.Walkability;
                var at = !float.IsNaN(end.Y) && walk.FloorNear(end.X, end.Z, end.Y, FloorSlack) is { } floor ? floor
                    : new Vector3(end.X, float.IsNaN(end.Y) ? walk.GroundHeight(end.X, end.Z) : end.Y, end.Z);
                // Only onto walkable ground: a spot it could not walk to either is left to the walk and its give-up rule. The serial step puts it
                // there (after the movement, which steps from the last tick's place), and it thinks again on the next tick, from the spot.
                if (walk.IsWalkable(at.X, at.Z, at.Y))
                {
                    mind.PlaceAt = at;
                    mind.Version = -1;
                    Interlocked.Exchange(ref placing, 1);
                    Become(table, slot, CharacterTask.Idle);
                    return;
                }
            }
        }
        switch (behaviour)
        {
            case AiBehaviour.Mill:
                // Round its post; at a borrowed home (the bar of a bar package) round that.
                mind.WanderCentre = board.Home is { } borrowed && borrowed != board.OwnHome ? Flat(borrowed.Position) : mind.Post;
                mind.WanderRadius = Settings.MillRadius;
                Become(table, slot, CharacterTask.Wander);
                break;
            case AiBehaviour.Follow:
                mind.FollowTarget = goal.Task.Target == TaskTargetType.SquadMission ? board.MissionTarget : CharacterId.None;
                Become(table, slot, CharacterTask.Follow);
                break;
            case AiBehaviour.Patrol:
                if (board.CurrentClass is PackageClass.Patrol or PackageClass.PatrolRunning && board.RouteIndex < board.Route.Length)
                {
                    if (!leader) Become(table, slot, CharacterTask.Follow);
                    else WalkTo(table, slot, board.Route[board.RouteIndex], board.CurrentClass == PackageClass.PatrolRunning ? SpeedMode.Run : SpeedMode.Walk);
                }
                else Become(table, slot, CharacterTask.Wander);
                break;
            case AiBehaviour.Sleep when table.Cold(slot)!.InSomething == Locks.HeldState.Bed:
                break;   // lying in its bed
            case AiBehaviour.Post:
            case AiBehaviour.Sleep:
            case AiBehaviour.GoHome:
                // An occupier without a home sleeps in a bed of the town it holds.
                var occupied = behaviour == AiBehaviour.Sleep && board.Home is null && !mind.HomeUnreachable ? OccupiedBed(world, board, mind, slot) : null;
                var (target, home, floor, facing) = occupied is not null ? (Flat(occupied.Position), true, occupied.Position.Y, (float?)null) : SpotFor(mind, board, behaviour, type);
                if (GoToSpot(world.Walkability, table, slot, mind, target, home ? GiveUp.Home : GiveUp.Post, type is TaskType.RUN_AWAY_HOMETOWN or TaskType.TRAVEL_TO_TARGET_TOWN_FAST ? SpeedMode.Run : SpeedMode.Walk, height: floor))
                {
                    Become(table, slot, behaviour == AiBehaviour.Sleep ? CharacterTask.Rest : CharacterTask.Idle);
                    // At its home's bed: the serial step lies it in (Task_UseBed via 98 Sleeping); resting on the floor stays the fallback when no bed is free.
                    if (behaviour == AiBehaviour.Sleep && home && floor is not null && (occupied?.Id ?? BedSpotOf(board, mind)) is { } bed)
                    {
                        mind.BedAt = bed;
                        Interlocked.Exchange(ref bedding, 1);
                    }
                    if (home && board.Gate is { } gate && GateSpot(board, mind, type) is not null) table.Next[slot].Yaw = MathF.Atan2(gate.Out.X, gate.Out.Y);   // looking out
                    else if (facing is { } yaw) table.Next[slot].Yaw = yaw;   // at a node, facing its way (the barman over his counter)
                }
                break;
            case AiBehaviour.TownNode:
            case AiBehaviour.Shop:
            case AiBehaviour.ToTown:
                mind.Target ??= TargetFor(world, table.IdOf(slot), behaviour, board.Town) ?? (behaviour == AiBehaviour.TownNode ? OccupiedSpot(world, board, table.IdOf(slot)) : null);
                if (mind.Target is not { } goTo) Become(table, slot, CharacterTask.Idle);
                else if (GoToSpot(world.Walkability, table, slot, mind, Flat(goTo), GiveUp.Target, SpeedMode.Walk, height: behaviour == AiBehaviour.ToTown ? null : goTo.Y))
                    Become(table, slot, CharacterTask.Idle);
                break;
            case AiBehaviour.PatrolTown:
                Patrol(world, slot, mind, board);
                break;
            case AiBehaviour.Work:
                if (mind.Target is null)
                {
                    if (mind.WorkActive) mind.WorkEpoch++;
                    mind.WorkActive = true;
                    if (PickWork(world, table.IdOf(slot), board.Town, mind.WorkEpoch) is { } building)
                    {
                        mind.Target = new Vector3(building.Position.X, float.NaN, building.Position.Z);
                        mind.WorkBuilding = building;
                        mind.WorkClip = WorkClipOf(building);
                        mind.WorkHungerRate = WorkHungerRateOf(building);
                    }
                }
                if (mind.Target is not { } working) Become(table, slot, CharacterTask.Idle);
                else if (GoToSpot(world.Walkability, table, slot, mind, Flat(working), GiveUp.Target, SpeedMode.Walk, WorkReach))
                {
                    // At the building: facing it, at its work clip.
                    Become(table, slot, CharacterTask.Idle);
                    var face = working - n.Position;
                    if (face.X * face.X + face.Z * face.Z > 1e-4f) n.Yaw = MathF.Atan2(face.X, face.Z);
                    if (table.Cold(slot) is { } workerCold) workerCold.ActionClip = mind.WorkClip;
                    WorkAtMachine(mind, type);
                }
                break;
            case AiBehaviour.Investigate:
            {
                // Task_InvestigateAlarms: within 100 of the alarm it stops and faces it (the serial step counts the look-about down); else it runs to 30 short of it, on its side.
                if (AlarmOf(world, slot, type, board) is not { } alarm) { Become(table, slot, CharacterTask.Idle); break; }
                var gap = n.Position - alarm.Position;
                if (gap.LengthSquared() <= Crime.CrimeSystem.AlarmLookRange * Crime.CrimeSystem.AlarmLookRange)
                {
                    if (mind.Walking) Become(table, slot, CharacterTask.Idle);
                    mind.Walking = false;
                    if (gap.X * gap.X + gap.Z * gap.Z > 1e-4f) n.Yaw = MathF.Atan2(-gap.X, -gap.Z);
                    break;
                }
                var side = Flat(gap);
                var spot = Flat(alarm.Position) + (side.LengthSquared() > 1e-4f ? Vector2.Normalize(side) : Vector2.UnitX) * Crime.CrimeSystem.AlarmApproach;
                GoToSpot(world.Walkability, table, slot, mind, spot, GiveUp.Target, SpeedMode.Run);
                break;
            }
            case AiBehaviour.Travel:
                // To the mission town's centre; the goal is no longer wanted once inside the town (Desire), long before the centre.
                if (DestinationCentre(world, board) is not { } town) Become(table, slot, CharacterTask.Idle);
                else if (GoToSpot(world.Walkability, table, slot, mind, town, GiveUp.Target, type == TaskType.TRAVEL_TO_TARGET_TOWN_FAST ? SpeedMode.Run : SpeedMode.Walk))
                    Become(table, slot, CharacterTask.Idle);
                break;
            case AiBehaviour.Relax:
            case AiBehaviour.Sit:
                // At its claimed seat (a chair, else a node); sitting around without a home, where it stands.
                if (behaviour == AiBehaviour.Sit && board.Home is null) Become(table, slot, CharacterTask.Rest);
                else if (board.SeatClaims.TryGetValue(slot, out var claimed))
                {
                    bool chair = claimed.Kind == HomeSpotKind.Chair;
                    // Already on its chair (moved onto it below, off the floor point the walk ended at).
                    bool seated = chair && n.Task == (byte)CharacterTask.Rest && !mind.Walking
                        && Vector2.DistanceSquared(Flat(n.Position), Flat(claimed.Position)) < 1 && MathF.Abs(n.Position.Y - claimed.Position.Y) <= FloorSlack;
                    if (seated || GoToSpot(world.Walkability, table, slot, mind, Flat(claimed.Position), GiveUp.Seat, SpeedMode.Walk, height: claimed.Position.Y))
                    {
                        if (chair)
                        {
                            // On the seat's origin, with no offset, facing its way (docs/game/ai-tasks.md "Chairs"): the sitting clip carries the pose.
                            n.Position = claimed.Position;
                            n.Yaw = claimed.Yaw;
                            mind.SitClip = claimed.SitClip;
                            mind.SitHungerRate = claimed.HungerRate;
                        }
                        Become(table, slot, CharacterTask.Rest);
                    }
                }
                break;
            case AiBehaviour.Turret:
                if (board.TurretClaims.TryGetValue(slot, out int t) && (uint)t < (uint)board.Turrets.Count && board.Turrets[t].Turret is { } turret)
                {
                    var spot = TurretSpot(world, turret);
                    if (GoToSpot(world.Walkability, table, slot, mind, Flat(spot), GiveUp.Post, SpeedMode.Walk, TurretSlack, spot.Y))
                    {
                        table.Next[slot].Yaw = turret.Yaw;   // at the gun, facing its way
                        Become(table, slot, CharacterTask.Idle);
                    }
                }
                else Become(table, slot, CharacterTask.Idle);
                break;
        }
    }

    /// <summary>
    /// Where a goal is acted out: a node of the home building by goal (shopkeeper, guard, throne, general; any node when the home has none of that kind), a bed for
    /// sleep (the home itself when it has no bed), the home or the town for going home; else (no home, or its spots kept failing) the character's post. True in
    /// <c>Home</c> when the spot is the home's; <c>Floor</c> is the height of a node or bed (so the walk ends on the floor it is on, not on the ground below);
    /// <c>Yaw</c> the way a node faces, which the character turns to on arriving.
    /// </summary>
    (Vector2 Target, bool Home, float? Floor, float? Yaw) SpotFor(AiMind mind, Blackboard board, AiBehaviour behaviour, TaskType type)
    {
        if (behaviour == AiBehaviour.GoHome)
        {
            if (board.Home is { } h && !mind.HomeUnreachable) return (Flat(h.Position), true, null, null);
            if (board.Town >= 0) return (Flat(data.Sites[board.Town].Position), true, null, null);
            return (mind.Squad.HomeCentre, true, null, null);
        }
        if (!mind.HomeUnreachable && GateSpot(board, mind, type) is { } guard) return (Flat(guard), true, guard.Y, null);
        if (board.Home is not { } home || mind.HomeUnreachable || type is TaskType.HOLD_POSITION or TaskType.MAN_THE_GATE) return (mind.Post, false, null, null);
        HomeSpotKind kind = behaviour == AiBehaviour.Sleep ? HomeSpotKind.Bed : type switch
        {
            TaskType.STAND_AT_SHOPKEEPER_NODE => HomeSpotKind.Shopkeeper,
            TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING or TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY or TaskType.STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE => HomeSpotKind.Guard,
            TaskType.SIT_ON_THRONE => HomeSpotKind.Throne,
            _ => HomeSpotKind.General,
        };
        if (Pick(home, kind, mind.MemberIndex) is { } spot) return (Flat(spot.Position), true, spot.Position.Y, spot.IsNode ? spot.Yaw : null);
        // Guard posts fall back to general nodes (docs/game/ai-tasks.md "Town goals"); the counter is the shopkeeper's, so only its goal falls back onto it.
        if (kind == HomeSpotKind.Guard && Pick(home, HomeSpotKind.General, mind.MemberIndex) is { } general)
            return (Flat(general.Position), true, general.Position.Y, general.Yaw);
        if (kind != HomeSpotKind.Bed && Pick(home, null, mind.MemberIndex, kind != HomeSpotKind.Shopkeeper) is { } any) return (Flat(any.Position), true, any.Position.Y, any.Yaw);
        // No bed: it sleeps in the house; a node goal without nodes stays at its own post, except staying home.
        if (kind == HomeSpotKind.Bed || type == TaskType.STAY_IN_HOME) return (Flat(home.Position), true, null, null);
        return (mind.Post, false, null, null);
    }

    /// <summary>
    /// The <paramref name="index"/>-th spot of a kind (null: any node, <see cref="HomeSpot.IsNode"/>, shopkeeper nodes left out with <paramref name="notCounter"/>) of a
    /// building, round robin; null when it has none.
    /// </summary>
    static HomeSpot? Pick(TownBuilding home, HomeSpotKind? kind, int index, bool notCounter = false)
    {
        bool Takes(HomeSpot s) => kind is { } k ? s.Kind == k : s.IsNode && !(notCounter && s.Kind == HomeSpotKind.Shopkeeper);
        int count = 0;
        foreach (var s in home.Spots) if (Takes(s)) count++;
        if (count == 0) return null;
        int want = index % count;
        foreach (var s in home.Spots)
            if (Takes(s) && want-- == 0) return s;
        return null;
    }

    /// <summary>The fixture id of the bed <see cref="SpotFor"/> sends a sleeper to (the <see cref="Pick"/> of its home's beds, numbered as <c>FixtureRegistry</c> numbers them); null without one.</summary>
    static string? BedSpotOf(Blackboard board, AiMind mind)
    {
        if (board.Home is not { } home || mind.HomeUnreachable) return null;
        int count = 0;
        foreach (var s in home.Spots) if (s.Kind == HomeSpotKind.Bed) count++;
        return count == 0 ? null : $"{home.Key}@bed{(mind.MemberIndex % count).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>
    /// Where a turret is manned: behind its gun (the gunner stands between the gun and the walkway behind it), inside the turret's <c>use range</c>: the first of a
    /// few distances behind it whose ground is walkable at the turret's height, else beside or before it, else the turret's own place. The Y is the walkable surface
    /// there, so the path goes up to the tower's floor rather than to the ground below it. Engine choice: the game's gunner spot is not in the data.
    /// </summary>
    internal static Vector3 TurretSpot(World world, in TurretPost turret)
    {
        var at = Flat(turret.Position);
        var forward = new Vector2(MathF.Sin(turret.Yaw), MathF.Cos(turret.Yaw));
        var side = new Vector2(forward.Y, -forward.X);
        float nearY = turret.Position.Y;
        bool Walkable(Vector2 p) => world.Walkability.IsWalkable(p.X, p.Y, nearY);
        Vector3 On(Vector2 p) => new(p.X, world.Walkability.GroundHeight(p.X, p.Y, nearY), p.Y);
        float reach = MathF.Max(MathF.Min(turret.UseRange, 10), 2);
        for (float d = reach * 0.6f; d >= 2; d -= 2)
            if (Walkable(at - forward * d)) return On(at - forward * d);
        for (int k = 0; k < 2; k++)
            if (Walkable(at + side * (k == 0 ? 4 : -4))) return On(at + side * (k == 0 ? 4 : -4));
        return Walkable(at + forward * 4) ? On(at + forward * 4) : On(at);
    }

    /// <summary>
    /// Walks the character to a spot; true when it is there (and not walking). A walk that ends away from the spot counts as failed; after
    /// <see cref="MaxFailedWalks"/> in a row it gives the spot up as <paramref name="giveUp"/> says. <paramref name="slack"/> overrides
    /// <see cref="AiSettings.PostSlack"/> where the spot must be exact (a turret's gun); <paramref name="height"/> is the floor the spot is on, so the path reaches a
    /// floor or walkway above the ground; a spot over a hole furniture cuts in that floor is walked to beside it, on the floor (<see cref="IWalkability.FloorNear"/>).
    /// </summary>
    /// <summary>
    /// Where 200 Task_LeaveBuilding walks (docs/game/ai-tasks.md): A + (A − B) × 1.1 of the building's first door with a known leaf, A and B its points 15 either side of the leaf
    /// (engine reading of which is outer: the side away from the building's origin). Null: no door known.
    /// </summary>
    static Vector2? LeavePoint(World world, TownBuilding building)
    {
        foreach (var door in world.Fixtures.DoorsByBuilding[building.Key])
        {
            if (door.Spot is not { } s) continue;
            var centre = new Vector2(s.Centre.X, s.Centre.Z);
            var outward = new Vector2(s.Normal.X, s.Normal.Z);
            if (Vector2.Dot(outward, centre - new Vector2(building.Position.X, building.Position.Z)) < 0) outward = -outward;
            Vector2 a = centre + outward * 15, b = centre - outward * 15;
            return a + (a - b) * 1.1f;
        }
        return null;
    }

    bool GoToSpot(IWalkability walk, CharacterTable table, int slot, AiMind mind, Vector2 target, GiveUp giveUp, byte mode, float slack = 0, float? height = null)
    {
        if (height is { } spotY && walk.FloorNear(target.X, target.Y, spotY, FloorSlack) is { } onFloor) (target, height) = (Flat(onFloor), onFloor.Y);
        ref var n = ref table.Next[slot];
        var here = new Vector2(n.Position.X, n.Position.Z);
        float within = slack > 0 ? slack : Settings.PostSlack;
        // A spot on a floor counts as reached on that floor only, not on the ground below it.
        bool away = Vector2.DistanceSquared(here, target) > within * within || height is { } y && MathF.Abs(n.Position.Y - y) > FloorSlack;
        if (mind.Walking && n.Task != (byte)CharacterTask.GoTo)
        {
            // The walk is over: arrived, or it ended elsewhere (no way there, or a fight took it).
            mind.Walking = false;
            mind.FailedWalks = away ? mind.FailedWalks + 1 : 0;
            if (mind.FailedWalks >= MaxFailedWalks)
            {
                mind.FailedWalks = 0;
                switch (giveUp)
                {
                    case GiveUp.Home: mind.HomeUnreachable = true; break;
                    case GiveUp.Post: mind.Post = here; break;
                    case GiveUp.Target: mind.Target = null; break;
                    case GiveUp.Seat: mind.SeatUnreachable = true; break;
                }
                return false;   // the next think picks the new spot
            }
        }
        if (away)
        {
            WalkTo(table, slot, target, mode);
            if (height is { } floor && n.Task == (byte)CharacterTask.GoTo)
            {
                // A spot on a floor (a node, seat or gun) is walked onto, not to a stride off it.
                var cold = table.Cold(slot)!;
                (cold.GoalHeight, cold.GoalHeightAt, cold.ArriveWithin) = (floor, target, MovementSystem.SpotArrive);
            }
            mind.Walking = true;
            return false;
        }
        return n.Task != (byte)CharacterTask.GoTo;   // arriving: the movement system ends the walk
    }

    // Set by a think that left a resident to be placed (<see cref="AiMind.PlaceAt"/>); the serial step looks for them only then.
    int placing;

    /// <summary>Puts the residents back from their town's time unloaded on the spots their thinks chose (serial, after the movement).</summary>
    static void Place(CharacterTable table)
    {
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s)?.Ai is not { PlaceAt: { } at } mind) continue;
            mind.PlaceAt = null;
            MovementSystem.Stop(table, s);
            ref var n = ref table.Next[s];
            n.Position = at;
            n.Velocity = Vector3.Zero;
        }
    }

    // Set by a think that left a sleeper to be laid in its bed (<see cref="AiMind.BedAt"/>).
    int bedding;

    /// <summary>How far (X/Z) from where it stands a sleeper takes another free bed of its home when its own is taken (units; engine choice).</summary>
    const float BedReach = 30;

    /// <summary>
    /// Lies the sleepers that reached their bed spot in it (serial: beds are shared). A taken bed gives way to the nearest free bed of the same building within
    /// <see cref="BedReach"/>; with none free it keeps resting on the floor there (engine choice).
    /// </summary>
    static void LieDown(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { BedAt: { } id } mind } cold) continue;
            mind.BedAt = null;
            ref var n = ref table.Next[s];
            if (!n.Alive || mind.Behaviour != AiBehaviour.Sleep || n.Task != (byte)CharacterTask.Rest || world.Fixtures.Find(id) is not { } bed) continue;
            var here = new Vector2(n.Position.X, n.Position.Z);
            bool Near(Locks.Fixture f) => Vector2.DistanceSquared(here, Flat(f.Position)) <= BedReach * BedReach;
            if (!Locks.FixtureRegistry.IsFree(table, bed) || !Near(bed))
            {
                Locks.Fixture? best = null;
                float bestD = float.MaxValue;
                foreach (var f in world.Fixtures.OfBuilding(bed.Building))
                {
                    if (f.Kind != Locks.FixtureKind.Bed || !Locks.FixtureRegistry.IsFree(table, f) || !Near(f)) continue;
                    float d = Vector2.DistanceSquared(here, Flat(f.Position));
                    if (d < bestD) (best, bestD) = (f, d);
                }
                if (best is null) continue;
                bed = best;
            }
            if (cold.InSomething == Locks.HeldState.None) world.Fixtures.EnterBed(world, table.IdOf(s), bed);
        }
    }

    /// <summary>What a character gives up when its walks to a spot keep failing: its home's spots (it uses its post), its post (where it stands becomes it), the target, or relaxing at its bar seat.</summary>
    enum GiveUp : byte { Home, Post, Target, Seat }

    /// <summary>How far above or below a spot's floor a character may stand and still be at it (units).</summary>
    const float FloorSlack = 6;

    /// <summary>The radius of the ring a resident back from its town's time unloaded is put on round its spot (units; half a human's separation radius).</summary>
    const float ResumeRing = 4;

    /// <summary>Switches to a standing task (wander, follow, idle, rest), dropping a walk in progress; nothing when it is doing it already.</summary>
    static void Become(CharacterTable table, int slot, CharacterTask task)
    {
        ref var n = ref table.Next[slot];
        if (n.Task == (byte)task) return;
        MovementSystem.Stop(table, slot);
        n.Task = (byte)task;
        n.TaskTime = 0;
    }

    /// <summary>Sends it walking (or running) to a point, unless it is going there already.</summary>
    static void WalkTo(CharacterTable table, int slot, Vector2 to, byte mode)
    {
        ref var n = ref table.Next[slot];
        if (n.Task == (byte)CharacterTask.GoTo && Vector2.DistanceSquared(n.Goal, to) < 1) { n.Mode = mode; return; }
        MovementSystem.GoTo(table, slot, to, mode);
    }

    // ------------------------------------------------------------------ serial steps

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            var (squadId, packageId) = command switch { StartContract s => (s.SquadId, s.PackageId), ChangeAi a => (a.SquadId, a.PackageId), _ => (-1, "") };
            if (world.Squads.Find(squadId) is not { } squad || data.Ai.Package(packageId) is not { } package) continue;
            if (squad.Ai is null)
            {
                if (IsPlayers(world, squad)) continue;
                Attach(world, squad);
            }
            rules.Bind(this, world, squad);
            if (command is StartContract c) squad.Ai!.StartContract(package, world.GameHours, c.Hours, rules, c.Target);
            else squad.Ai!.Replace(package, rules);
        }
    }

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        if (Interlocked.Exchange(ref placing, 0) != 0) Place(table);
        if (Interlocked.Exchange(ref bedding, 0) != 0) LieDown(world);
        bool cadence = world.Tick % Math.Max(1, Settings.PackageEveryTicks) == 0;
        // Each town's turrets are claimed once per cadence, by the squads in squad order (derived, not hashed).
        if (cadence) { turretTaken.Clear(); seatTaken.Clear(); ReserveSeats(world); RefreshPlaces(world); }
        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is null)
            {
                if (!looked.Add(squad.Id) || !Runs(world, squad)) continue;
                Attach(world, squad);
                if (squad.Ai!.Town >= 0 && !places.ContainsKey(squad.Ai.Town)) RefreshPlaces(world);
            }
            else if (!cadence) continue;   // the rest is looked at on the package cadence only (and when the squad appears)
            var board = squad.Ai!;
            board.Intact = IsIntact(world, squad, board);
            UpdateInTown(world, squad, board);
            rules.Bind(this, world, squad);
            board.Update(rules, world.GameHours);
            StepPackage(world, squad, board);
            // A contract that ended: its leader says the package's contract end talk to the employer (event 30, FUN_14026a110).
            if (board.TakeEnded() is { Package.ContractEndTalk: { } talk, Target: var employer } && !squad.Leader.IsNone)
                world.Defer(new Dialogue.DialogueEvent(squad.Leader, employer, Dialogue.DialogueSystem.ContractJobEnded, talk) { Tick = world.Tick + 1 });
            AdvanceRoute(world, squad, board);
            board.Crowd.Clear();
            for (int k = 0; k < squad.Members.Count; k++)
            {
                if (!table.TryResolveNext(squad.Members[k], out int s)) continue;
                var cold = table.Cold(s)!;
                if (cold.Ai is null)
                {
                    var p = table.Next[s].Position;
                    cold.Ai = new AiMind
                    {
                        Squad = squad, MemberIndex = k, Post = OccupierPost(world, board, k) ?? new Vector2(p.X, p.Z), RaceGoals = cold.Race is { } race ? data.Ai.RaceGoals(race.StringId) : [], Resume = squad.Resume,
                    };
                }
                if (cold.Ai.Goal is { } g) board.Crowd[g.Task.Type] = board.Crowd.GetValueOrDefault(g.Task.Type) + 1;
            }
            squad.Resume = false;
            ClaimTurrets(world, board, squad);
            ClaimSeats(world, board, squad);
            FindGate(world, board, squad);
        }
        if (merges.Count > 0) ApplyMerges(world);
    }

    /// <summary>
    /// Sets <see cref="Blackboard.Gate"/>: the home when it is a gate, else, for a squad with a gate guard, the town's gate nearest the squad (the original's
    /// <c>HOME_GATE</c> chain, docs/game/ai-tasks.md "Town goals"); its first door's leaf, from the door gate (none headless). Kept while the gate stays the same.
    /// </summary>
    void FindGate(World world, Blackboard board, Squad squad)
    {
        TownBuilding? gate = board.Home is { IsGate: true } home ? home : null;
        if (gate is null && board.Crowd.ContainsKey(TaskType.MAN_THE_GATE) && board.Town >= 0)
        {
            float best = float.MaxValue;
            foreach (var b in data.Sites[board.Town].Buildings)
            {
                float d = Vector2.DistanceSquared(Flat(b.Position), Flat(squad.Position));
                if (b.IsGate && d < best) (gate, best) = (b, d);
            }
        }
        if (gate is null) { board.Gate = null; return; }
        if (board.Gate?.Building == gate) return;
        board.Gate = PostOf(world, gate, board.Town >= 0 ? Flat(data.Sites[board.Town].Position) : Flat(gate.Position));
    }

    /// <summary>The post of <paramref name="gate"/> (see <see cref="FindGate"/>) for a town centred at <paramref name="town"/>; null: no door, or its leaf's place not known.</summary>
    internal static GatePost? PostOf(World world, TownBuilding gate, Vector2 town)
    {
        Locks.Fixture? door = null;
        foreach (var f in world.Fixtures.OfBuilding(gate.Key))
            if (f.Kind == Locks.FixtureKind.Door && (door is null || f.DoorIndex < door.DoorIndex)) door = f;
        if (door is null || world.Fixtures.DoorSpotOf(door) is not { } leaf) return null;
        var way = Vector2.Normalize(new Vector2(leaf.Normal.X, leaf.Normal.Z));
        // Out of the town: the original's side is not decoded (engine choice).
        if (Vector2.Dot(way, Flat(leaf.Centre) - town) < 0) way = -way;
        return new GatePost(gate, leaf.Centre with { Y = leaf.Foot }, way, leaf.HalfWidth);
    }

    /// <summary>Where a gate's gate guard stands: <see cref="GateGuardOut"/> out from its leaf.</summary>
    internal static Vector3 FrontOf(GatePost gate) => gate.Door + new Vector3(gate.Out.X, 0, gate.Out.Y) * GateGuardOut;

    /// <summary>How far from a gate's leaf its gate guard stands (Observed, <c>Task_ManTheGate</c>: the door's point 15 along its normal).</summary>
    const float GateGuardOut = 15;

    /// <summary>
    /// Where a gate's guards stand: the gate guard 15 out from its leaf; a member whose node goal finds no node in a gate it lives in flanks the opening on that side,
    /// beyond the leaf's edges, alternately left and right in two rows (engine choice: the original's goal finds nothing there). Null for other goals.
    /// </summary>
    static Vector3? GateSpot(Blackboard board, AiMind mind, TaskType type)
    {
        if (board.Gate is not { } gate) return null;
        var front = FrontOf(gate);
        if (type == TaskType.MAN_THE_GATE) return front;
        if (type is not (TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING or TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY or TaskType.STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE
            or TaskType.STAND_AT_GENERAL_NODE) || board.Home != gate.Building) return null;
        foreach (var s in gate.Building.Spots) if (s.IsNode) return null;
        int step = mind.MemberIndex / 2;
        float side = (mind.MemberIndex & 1) == 0 ? 1 : -1;
        var along = new Vector3(-gate.Out.Y, 0, gate.Out.X) * side * (gate.HalfWidth + 6 + 7 * (step / 2));
        return front + new Vector3(gate.Out.X, 0, gate.Out.Y) * (8 * (step % 2)) + along;
    }

    /// <summary>
    /// Gives the members' slots the town's turrets to man (docs/simulation.md "AI", turrets): in member order each takes the nearest turret no earlier member claimed,
    /// by the distance to its post; a member whose goal is the home building's turrets first takes one mounted on its home. Rebuilt on the package cadence; a town
    /// without turrets gives none.
    /// </summary>
    void ClaimTurrets(World world, Blackboard board, Squad squad)
    {
        board.Turrets = data.Turrets(board.Town);
        board.TurretClaims.Clear();
        if (board.Turrets.Count == 0) return;
        if (!turretTaken.TryGetValue(board.Town, out var taken)) turretTaken[board.Town] = taken = [];
        int hour = world.Hour, claimed = 0;
        for (int pass = 0; pass < 2 && claimed < board.Turrets.Count; pass++)
            for (int k = 0; k < squad.Members.Count && claimed < board.Turrets.Count; k++)
            {
                var id = squad.Members[k];
                if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } cold || cold.Ai is not { } mind) continue;
                if (board.TurretClaims.ContainsKey(s) || !TurretGoal(board, squad, id, cold, hour, out bool homeOnly)) continue;
                if (pass == 0 ? !homeOnly : homeOnly) continue;
                int best = -1;
                float bestD = float.MaxValue;
                for (int t = 0; t < board.Turrets.Count; t++)
                {
                    if (taken.Contains(t) || board.Turrets[t].Turret is not { } turret) continue;
                    if (homeOnly && turret.MountedOn != board.Home?.Key) continue;
                    float d = Vector2.DistanceSquared(mind.Post, Flat(turret.Position));
                    if (d < bestD) (best, bestD) = (t, d);
                }
                if (best >= 0) { taken.Add(best); board.TurretClaims[s] = best; claimed++; }
            }
    }

    /// <summary>
    /// Gives the members whose role list has relaxing (under a bar package), sitting around or staying home open at this hour a seat of their <see cref="Blackboard.Home"/> (the
    /// package's bar under a bar package): in member order each takes the first seat no one in that building claimed in this cadence pass; members left without one
    /// do not relax or sit. Relaxing takes a chair, else a bar node, else a general node (the original operates furniture of the bar, docs/game/ai-tasks.md "Town
    /// goals", Observed; the node fallbacks are an engine choice); sitting around and staying home a chair only (the original's furniture not claimed by others, Observed; its
    /// training and throne furniture are left out). A member keeps the seat it had while it still relaxes or sits (engine choice: reassigning every pass in member
    /// order moved every later sitter to another chair whenever one came or went); the seats kept are reserved for every squad first (<see cref="ReserveSeats"/>).
    /// </summary>
    void ClaimSeats(World world, Blackboard board, Squad squad)
    {
        kept.Clear();
        foreach (var (slot, spot) in board.SeatClaims) kept[slot] = spot;
        board.SeatClaims.Clear();
        if (board.Home is not { } home) return;
        bool bar = board.BarTable >= 0;
        // The squad's table: members sit nearest it (engine choice); an outdoor table has no seats, they stand at it.
        Vector3? near = bar ? board.BarTable < home.Tables.Count ? home.Tables[board.BarTable] : home.Position : null;
        HashSet<int>? taken = null;
        int hour = world.Hour;
        for (int k = 0; k < squad.Members.Count; k++)
        {
            var id = squad.Members[k];
            if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } cold || cold.Ai is null) continue;
            var roles = board.Roles[(int)RoleOf(squad, id, cold)];
            bool relaxes = false, sits = false;
            for (int g = 0; g < roles.Count; g++)
            {
                if (!roles[g].InWindow(hour)) continue;
                relaxes |= bar && roles[g].Task.Type == TaskType.RELAX_IN_TOWN_PACKAGE;
                sits |= roles[g].Task.Type is TaskType.SIT_AROUND or TaskType.STAY_IN_HOME;
            }
            if (taken is null && !seatTaken.TryGetValue(home, out taken)) seatTaken[home] = taken = [];
            int had = kept.TryGetValue(s, out var old) ? IndexOf(home, old) : -1;
            if (!relaxes && !sits || cold.Ai.SeatUnreachable)   // one that gave its seat up leaves it to the next member
            {
                if (had >= 0) taken.Remove(had);
                continue;
            }
            if (had >= 0 && (relaxes || old.Kind == HomeSpotKind.Chair))
            {
                taken.Add(had);
                board.SeatClaims[s] = old;
                continue;
            }
            int seat = FreeSeat(home, taken, HomeSpotKind.Chair, near);
            if (seat < 0 && relaxes) seat = FreeSeat(home, taken, HomeSpotKind.Bar, near);
            if (seat < 0 && relaxes) seat = FreeSeat(home, taken, HomeSpotKind.General, near);
            if (seat < 0 && relaxes && home.IsOutdoorTable) board.SeatClaims[s] = new HomeSpot(HomeSpotKind.General, home.Position, home.Yaw);
            if (seat < 0) continue;
            taken.Add(seat);
            board.SeatClaims[s] = home.Spots[seat];
        }
    }

    readonly Dictionary<int, HomeSpot> kept = [];

    /// <summary>Marks every seat a squad's member holds from the last pass as taken, before any squad claims (the cadence pass; see <see cref="ClaimSeats"/>).</summary>
    void ReserveSeats(World world)
    {
        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is not { Home: { } home } board || board.SeatClaims.Count == 0) continue;
            if (!seatTaken.TryGetValue(home, out var taken)) seatTaken[home] = taken = [];
            foreach (var spot in board.SeatClaims.Values)
                if (IndexOf(home, spot) is >= 0 and var i) taken.Add(i);
        }
    }

    static int IndexOf(TownBuilding building, HomeSpot spot)
    {
        for (int i = 0; i < building.Spots.Count; i++)
            if (building.Spots[i] == spot) return i;
        return -1;
    }

    /// <summary>The first free spot of <paramref name="kind"/> in <paramref name="bar"/>, or with <paramref name="near"/> the nearest free one to it; -1: none.</summary>
    static int FreeSeat(TownBuilding bar, HashSet<int> taken, HomeSpotKind kind, Vector3? near = null)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < bar.Spots.Count; i++)
        {
            if (bar.Spots[i].Kind != kind || taken.Contains(i)) continue;
            if (near is not { } p) return i;
            float d = Vector3.DistanceSquared(bar.Spots[i].Position, p);
            if (d < bestD) (best, bestD) = (i, d);
        }
        return best;
    }

    /// <summary>
    /// Whether the member's role list has a turret goal open at <paramref name="hour"/>, and whether it is only the home building's (222): that one may only man a
    /// turret mounted on its home. Its role list only (not its race's goals): a race goal without the town's turret data names no turret.
    /// </summary>
    static bool TurretGoal(Blackboard board, Squad squad, CharacterId id, CharacterCold cold, int hour, out bool homeOnly)
    {
        var roles = board.Roles[(int)RoleOf(squad, id, cold)];
        bool home = false, town = false;
        for (int k = 0; k < roles.Count; k++)
        {
            if (!roles[k].InWindow(hour)) continue;
            var type = roles[k].Task.Type;
            if (type == TaskType.MAN_A_TURRET_ON_BUILDING) home = true;
            else if (type is TaskType.MAN_A_TURRET or TaskType.MAN_A_TURRET_PLAYER_JOB or TaskType.USE_TURRET) town = true;
        }
        homeOnly = home && !town;
        return home || town;
    }

    void Attach(World world, Squad squad)
    {
        var (packages, template) = TemplateOf(squad.TemplateId);
        var home = AssignHome(world, squad, template);
        // The template's initial door state applies to the home it is given (docs/game/locks.md "Doors"); the first squad's wins, a saved building keeps its own.
        if (home is not null) world.Fixtures.ApplyInitialDoorState(home.Key, template?.InitialDoorState ?? 0);
        ulong roll = Rng.Hash(world.Seed, (ulong)squad.Id, RngPurpose.Think, SeedSalts.AiPatrol);
        squad.Ai = new Blackboard(packages)
        {
            Town = squad.Town, Size = squad.Members.Count, OwnHome = home, Home = home, HomePublic = template?.PublicDay ?? true,
            NextPatrol = world.GameHours + Rng.Float(roll) * Settings.FirstPatrolHours,
        };
        // The town it starts in, taken as already entered (engine choice: no ENTER_TOWN line for every squad a town makes).
        if (LeaderPosition(world, squad) is { } at) (squad.Ai.InTown, squad.Ai.InTownSince) = (CurrentTown(world, at), world.GameHours);
        if (squad.SavedAi is { } saved)
        {
            rules.Bind(this, world, squad);
            saved.Apply(squad.Ai, data.Ai, rules);
            squad.SavedAi = null;
        }
        // An occupation it left the world in goes on in the player town it comes back in (the original keeps the occupier's platoon and package, docs/game/ai.md); the town
        // is the one it was made in, as a squad made this tick has no positions yet.
        if (squad.LeftPackage is { } left && data.Ai.Package(left) is { } package && PackageClasses.Of(package.Signal) == PackageClass.OccupyTown && squad.Ai.Contract is null
            && CurrentTown(world, squad.Position) is { PlayerTown: > 0 } there && squad.Ai.Resume(package))
        {
            squad.Ai.InTown = there;
            OccupyResumed(world, squad, squad.Ai);
        }
        squad.LeftPackage = null;
    }

    /// <summary>
    /// The AI runs a squad that is not the player's, not a roaming platoon's active half (a town's own saved squad, pinned, is run like the town's, a save's wild one like the wild
    /// sites' squads), and whose template lists AI packages.
    /// </summary>
    bool Runs(World world, Squad squad) => (squad.PlatoonId < 0 || squad.Pinned || squad.Wild) && !IsPlayers(world, squad) && TemplateOf(squad.TemplateId).Packages.Count > 0;

    bool IsPlayers(World world, Squad squad)
    {
        if (squad.Faction == data.PlayerFaction && data.PlayerFaction >= 0) return true;
        foreach (var m in squad.Members)
            if (world.Characters.TryResolveNext(m, out int s) && world.Characters.Cold(s)!.IsPlayer) return true;
        return false;
    }

    (IReadOnlyList<SlottedPackage> Packages, SquadTemplate? Template) TemplateOf(string templateId)
    {
        if (templates.TryGetValue(templateId, out var known)) return known;
        var template = data.Db.Find(templateId) is { } r && SquadFactory.IsTemplate(r) ? SquadTemplate.From(r) : null;
        var entry = (template is null ? (IReadOnlyList<SlottedPackage>)[] : data.Ai.Packages(template), template);
        templates[templateId] = entry;
        return entry;
    }

    /// <summary>
    /// The squad's own building: the one its town gave it (<see cref="Squad.HomeBuilding"/>, docs/game/factions-squads-towns.md "Town residents"); for a squad without
    /// one (made without building data, or a save's without a home) an <b>engine choice</b>: among its town's
    /// buildings no other squad has, the nearest of the template's <c>building</c>; else by <c>building designation</c> a shop (SHOP) or the bar (BAR, or one of the
    /// town's bar squads; shared);
    /// else the nearest with beds, else the nearest at all. None for a squad without a town or a town without building data.
    /// </summary>
    TownBuilding? AssignHome(World world, Squad squad, SquadTemplate? template)
    {
        if (squad.HomeBuilding is { } given) return given;
        if (squad.Town < 0 || data.Sites[squad.Town].Buildings is not { Count: > 0 } buildings) return null;
        var taken = new HashSet<TownBuilding>(ReferenceEqualityComparer.Instance);
        foreach (var other in world.Squads.All)
            if (other.Ai is { OwnHome: { } h } && other.Town == squad.Town) taken.Add(h);
        var at = new Vector2(squad.Position.X, squad.Position.Z);
        TownBuilding? Nearest(Func<TownBuilding, bool> accept, bool shared = false)
        {
            TownBuilding? best = null;
            float bestD = float.MaxValue;
            foreach (var b in buildings)
            {
                if (!accept(b) || (!shared && taken.Contains(b))) continue;
                float d = Vector2.DistanceSquared(at, Flat(b.Position));
                if (d < bestD) (best, bestD) = (b, d);
            }
            return best;
        }
        if (template?.Building is { } wanted && Nearest(b => b.BuildingId == wanted.Id) is { } own) return own;
        if (template?.BuildingDesignation == 1 && Nearest(b => b.IsShop) is { } shop) return shop;
        if ((template?.BuildingDesignation == 3 || IsBarSquad(squad)) && Nearest(b => b.IsBar, shared: true) is { } bar) return bar;
        return Nearest(b => b.Beds > 0) ?? Nearest(_ => true);
    }

    /// <summary>A squad made from one of its town's <c>bar squads</c> entries (the original gives it no home; Meitou keeps it at the bar, not in a free building with beds).</summary>
    bool IsBarSquad(Squad squad)
    {
        int f = squad.Town < data.SiteFactions.Count ? data.SiteFactions[squad.Town] : -1;
        return PopulationSystem.BarEntries(data.Sites[squad.Town].Town, f >= 0 ? data.Factions[f] : null).Any(e => e.Id == squad.TemplateId);
    }

    /// <summary>
    /// The intact rule of docs/game/perception.md 7.3 (Observed): able members are alive and not down; a squad of fewer than 4 is intact while its leader is up
    /// (while retreating, while anyone is up); a bigger one while at least 0.2 of its size is able (0.4 when the leader is down).
    /// </summary>
    static bool IsIntact(World world, Squad squad, Blackboard board)
    {
        var table = world.Characters;
        int able = 0;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && table.Cold(s)!.Medical is not { Incapacitated: true }) able++;
        bool leaderDown = squad.Leader.IsNone || !table.TryResolveNext(squad.Leader, out int ls) || table.Cold(ls)!.Medical is { Incapacitated: true };
        int size = Math.Max(board.Size, 1);
        if (size < 4) return board.CurrentClass == PackageClass.Retreat ? able > 0 : !leaderDown;
        return able >= size * (leaderDown ? 0.4f : 0.2f);
    }

    /// <summary>A running patrol moves on when its leader reaches the route point; the members think again at once.</summary>
    void AdvanceRoute(World world, Squad squad, Blackboard board)
    {
        if (board.CurrentClass is not (PackageClass.Patrol or PackageClass.PatrolRunning) || board.RouteIndex >= board.Route.Length) return;
        if (!world.Characters.TryResolveNext(squad.Leader, out int s)) return;
        var p = world.Characters.Next[s].Position;
        if (Vector2.Distance(new Vector2(p.X, p.Z), board.Route[board.RouteIndex]) > Settings.RouteArrive) return;
        board.RouteIndex++;
        board.Touch();
    }

    /// <summary>The package-class rules of <see cref="Blackboard.Update"/>, bound to the squad being updated (serial steps only).</summary>
    sealed class Rules : IPackageRules
    {
        AiSystem system = null!;
        World world = null!;
        Squad squad = null!;

        public void Bind(AiSystem s, World w, Squad q) => (system, world, squad) = (s, w, q);

        public bool CanStart(Blackboard board, AiPackage package, PackageClass kind) => kind switch
        {
            PackageClass.Retreat => !board.Intact,
            PackageClass.Patrol or PackageClass.PatrolRunning => board.Town >= 0 && world.GameHours >= board.NextPatrol && world.Characters.IsAlive(squad.Leader)
                && !system.CannibalPatrolHeld(world, squad, board, package),
            PackageClass.Bar => BarHere(board),
            PackageClass.BarOutdoors => true,
            PackageClass.CannibalRetreat or PackageClass.CannibalsAtHome or PackageClass.PrisonerGathering or PackageClass.SlaverShipping => system.CaptiveCanStart(world, squad, board, package, kind),
            PackageClass.WanderingTrader => system.TraderCanStart(world, squad, board),
            PackageClass.PassingAssault => system.AssaultCanStart(world, squad, board),
            PackageClass.Refugee => RefugeeCanStart(world, squad),
            _ => false,
        };

        /// <summary>
        /// Where a bar package can run: for a squad that is not a town's own (engine choice: a town's squads keep their town), away from home in a placed town not hostile to it that has a
        /// public bar (the original's <c>Package_HangAroundBar</c> takes the town the squad is in, docs/game/ai.md "Package classes"), in a player town not (engine choice: their bars
        /// are not read); at home (or in no town), with building data only where the town has a bar or an outdoor table, without it the package runs and the squad keeps its spots.
        /// </summary>
        bool BarHere(Blackboard board)
        {
            var t = board.InTown;
            if (!squad.TownOwn && t.PlayerTown > 0) return false;
            if (!squad.TownOwn && t.Site >= 0 && t.Site != board.Town)
            {
                int theirs = t.Site < system.Data.SiteFactions.Count ? system.Data.SiteFactions[t.Site] : -1;
                return !(squad.Faction >= 0 && theirs >= 0 && system.Data.Relations.IsHostile(squad.Faction, theirs)) && system.Places(t.Site).Bars.Count > 0;
            }
            return board.Town < 0 || system.Data.Sites[board.Town].Buildings.Count == 0
                || system.Places(board.Town) is { } p && (p.Bars.Count > 0 || p.OutdoorTables.Count > 0);
        }

        /// <summary>The town a bar package uses: for a squad that is not a town's own, the placed town it is in; else its own town.</summary>
        int BarTown(Blackboard board) => !squad.TownOwn && board.InTown.Site >= 0 ? board.InTown.Site : board.Town;

        public bool Finished(Blackboard board, AiPackage package, PackageClass kind) => kind switch
        {
            PackageClass.Retreat => board.Intact,
            PackageClass.Patrol or PackageClass.PatrolRunning => board.RouteIndex >= board.Route.Length || world.GameHours >= board.PatrolGiveUp || !world.Characters.IsAlive(squad.Leader),
            PackageClass.CannibalRetreat or PackageClass.CannibalsAtHome or PackageClass.PrisonerGathering or PackageClass.SlaverShipping => system.CaptiveFinished(world, squad, board, package, kind),
            // Away from home a bar package ends where it could not start (the original's finished test is its start test negated).
            PackageClass.Bar => !squad.TownOwn && !board.InTown.IsNone && board.InTown != CampaignTown.OfSite(board.Town) && !BarHere(board),
            PackageClass.WanderingTrader => board.TraderState == 2,
            PackageClass.PassingAssault => system.AssaultFinished(world, squad, board, package),
            PackageClass.OccupyTown => system.OccupyFinished(world, squad, board),
            PackageClass.Refugee => system.RefugeeFinished(world, squad, board),
            _ => false,
        };

        public bool MayLead(Blackboard board, AiPackage package, PackageClass kind) => kind != PackageClass.PassingAssault || system.AssaultMayLead(world, squad, board);

        public void Ended(Blackboard board, AiPackage package, PackageClass kind)
        {
            switch (kind)
            {
                case PackageClass.PassingAssault: system.AssaultEnded(world, squad); break;
                case PackageClass.OccupyTown: system.OccupyEnded(world, squad, board); break;
            }
        }

        public void Started(Blackboard board, AiPackage package, PackageClass kind)
        {
            switch (kind)
            {
                case PackageClass.Patrol:
                case PackageClass.PatrolRunning:
                    system.LayRoute(world, squad, board);
                    break;
                case PackageClass.Bar:
                case PackageClass.BarOutdoors:
                    if (BarTown(board) is >= 0 and var town && system.PickTable(world, town, squad, outdoors: kind == PackageClass.BarOutdoors) is var (bar, table))
                        (board.Home, board.BarTable) = (bar, table);
                    else if (kind == PackageClass.BarOutdoors) board.Home = null;
                    break;
                case PackageClass.CannibalRetreat:
                case PackageClass.SlaverShipping:
                    system.CaptiveStarted(world, squad, board, package, kind);
                    break;
                case PackageClass.WanderingTrader:
                    system.TraderStart(world, squad, board);
                    break;
                case PackageClass.PassingAssault:
                    system.AssaultStarted(world, squad, board);
                    break;
                case PackageClass.OccupyTown:
                    system.OccupyStarted(world, squad, board);
                    break;
                case PackageClass.Refugee:
                    system.RefugeeStarted(world, squad, board);
                    break;
            }
        }
    }

    /// <summary>
    /// The table a squad starting a bar package reserves (the original's <c>Package_HangAroundBar</c> start, docs/game/ai-tasks.md "Town goals"): the town's public bars
    /// are tried in turn from a random one (seeded by the squad and the tick), the first table of one no other squad's bar package holds wins; a town without bars, or
    /// <paramref name="outdoors"/>, takes the first free outdoor table. Null when all are taken: the squad stays home.
    /// </summary>
    (TownBuilding Bar, int Table)? PickTable(World world, int site, Squad squad, bool outdoors)
    {
        var places = Places(site);
        var held = new HashSet<(TownBuilding, int)>();
        foreach (var other in world.Squads.All)
            if (other != squad && other.Ai is { BarTable: >= 0, Home: { } at } b && b.CurrentClass is PackageClass.Bar or PackageClass.BarOutdoors) held.Add((at, b.BarTable));
        if (!outdoors && places.Bars.Count > 0)
        {
            int start = Rng.Int(Rng.Hash(world.Seed, (ulong)squad.Id << 32 ^ (ulong)world.Tick, RngPurpose.Think, SeedSalts.BarPick), places.Bars.Count);
            for (int k = 0; k < places.Bars.Count; k++)
            {
                var bar = places.Bars[(start + k) % places.Bars.Count];
                for (int t = 0; t < bar.Tables.Count; t++)
                    if (!held.Contains((bar, t))) return (bar, t);
            }
            return null;
        }
        foreach (var table in places.OutdoorTables)
            if (!held.Contains((table, 0))) return (table, 0);
        return null;
    }

    /// <summary>
    /// A patrol's route (<b>engine choice</b>: the original's <c>GoOutOnPatrol</c> is not decoded): <see cref="AiSettings.PatrolPoints"/> points round the home town at
    /// seeded angles and distances of 1.5 to 3 town radii, each pulled in towards the town until walkable (dropped when none is), then home. The next patrol may start
    /// 6 to 12 game hours after this one started; this one gives up after 6.
    /// </summary>
    void LayRoute(World world, Squad squad, Blackboard board)
    {
        if (board.Town < 0) { board.Route = []; return; }
        var site = data.Sites[board.Town];
        var centre = Flat(site.Position);
        float r = MathF.Max(site.Town.SizeRadius * site.Town.TownRadiusMult, 100);
        ulong key = Rng.Mix((ulong)squad.Id << 20 ^ (ulong)board.Version);
        float start = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Think, SeedSalts.AiPatrol)) * MathF.Tau;
        var route = new List<Vector2>();
        for (int k = 0; k < Settings.PatrolPoints; k++)
        {
            float angle = start + k * MathF.Tau / Settings.PatrolPoints;
            float f = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Think, (ulong)k + 1));
            float d = r * (Settings.PatrolRadiusMin + f * (Settings.PatrolRadiusMax - Settings.PatrolRadiusMin));
            for (int tries = 0; tries < 4; tries++, d *= 0.75f)
            {
                var p = centre + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * d;
                if (!world.Walkability.IsWalkable(p.X, p.Y)) continue;
                route.Add(p);
                break;
            }
        }
        route.Add(board.OwnHome is { } home ? Flat(home.Position) : centre);
        board.Route = [.. route];
        board.RouteIndex = 0;
        float every = Settings.PatrolEveryHoursMin + Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Think, 0x50)) * (Settings.PatrolEveryHoursMax - Settings.PatrolEveryHoursMin);
        board.NextPatrol = world.GameHours + every;
        board.PatrolGiveUp = world.GameHours + Settings.PatrolGiveUpHours;
    }
}
