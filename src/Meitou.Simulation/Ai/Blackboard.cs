using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The package classes the engine builds, from the package's <c>signal func</c> (docs/game/ai.md "Signal functions and the package classes", Verified mapping).
/// <see cref="Retreat"/> follows docs/game/perception.md 7.3 (Observed); the trader, the passing-by assault and the occupation follow their decoded classes
/// (docs/game/ai.md "Package classes"); the rest are engine choices (docs/simulation.md "AI", Package classes).
/// </summary>
public enum PackageClass : byte
{
    /// <summary>The base <c>AIPackage</c> (signals 0, 2, 3, 7, 8, 9, 18, 21): can always start, never finishes.</summary>
    Plain = 0,
    /// <summary><c>RetreatAndRefresh</c> (5): starts when the squad is not intact, finishes when it is.</summary>
    Retreat = 1,
    /// <summary><c>GoOutOnPatrol</c> (6) and <c>GoOutOnPatrol_Cannibal</c> (4): a walk round the outskirts of the home town every few hours.</summary>
    Patrol = 2,
    /// <summary><c>GoOutOnPatrol_Running</c> (23): the same at a run.</summary>
    PatrolRunning = 3,
    /// <summary><c>Package_HangAroundBar</c> with argument 0 (17): the squad's home becomes the town's bar.</summary>
    Bar = 4,
    /// <summary><c>Package_HangAroundBar</c> with argument 1 (26, outdoors only): the squad stays out of doors.</summary>
    BarOutdoors = 5,
    /// <summary>The contract classes (12 to 15, 33): started only as a contract (<see cref="StartContract"/>), finished when its hours are over.</summary>
    Contract = 6,
    /// <summary>
    /// Wars and campaigns (19, 20, 32, 31: the campaign drives its squads, <see cref="PopulationSystem"/>) and the rest of the unread classes: not built; never
    /// start (except as a lone slot-0 package), never finish.
    /// </summary>
    Unbuilt = 7,
    /// <summary><c>CannibalRetreatFromRaid</c> (1) and <c>CannibalRetreatFromRaid_nearestNest</c> (25): back to the mission town while someone carries (AiSystem.Captives.cs).</summary>
    CannibalRetreat = 8,
    /// <summary><c>Package_CannibalsAtHome</c> (27): while the squad is in its home town.</summary>
    CannibalsAtHome = 9,
    /// <summary><c>PrisonerGathering</c> (10) and <c>PrisonerGathering_unholy</c> (24): while a town near the leader holds prisoners to gather.</summary>
    PrisonerGathering = 10,
    /// <summary><c>SlaverPrisonerShipping_Camps</c> (11) and <c>_AnywhereTownsIncluded</c> (28): while the squad has slaves, to its mission town.</summary>
    SlaverShipping = 11,
    /// <summary><c>Package_WanderingTrader</c> (16): to a town the chooser draws, a stay there, then on (docs/game/ai.md "Package classes", Verified by code).</summary>
    WanderingTrader = 12,
    /// <summary><c>Package_PassingByTownAssault</c> (29): attacks a player town its leader walks into while the player's people are about.</summary>
    PassingAssault = 13,
    /// <summary><c>Package_OccupyTown</c> (30): stays in the player town it took (only by <c>Leads to</c>: it can never start of itself).</summary>
    OccupyTown = 14,
    /// <summary><c>Package_Refugee</c> (22): a broken or tiny squad flees to the nearest other town of its faction and joins a healthy squad there (docs/game/ai.md "Package classes", Verified by code).</summary>
    Refugee = 15,
}

public static class PackageClasses
{
    public static PackageClass Of(int signal) => signal switch
    {
        0 or 2 or 3 or 7 or 8 or 9 or 18 or 21 => PackageClass.Plain,
        5 => PackageClass.Retreat,
        4 or 6 => PackageClass.Patrol,
        23 => PackageClass.PatrolRunning,
        17 => PackageClass.Bar,
        26 => PackageClass.BarOutdoors,
        12 or 13 or 14 or 15 or 33 => PackageClass.Contract,
        1 or 25 => PackageClass.CannibalRetreat,
        27 => PackageClass.CannibalsAtHome,
        10 or 24 => PackageClass.PrisonerGathering,
        11 or 28 => PackageClass.SlaverShipping,
        16 => PackageClass.WanderingTrader,
        29 => PackageClass.PassingAssault,
        30 => PackageClass.OccupyTown,
        22 => PackageClass.Refugee,
        _ => PackageClass.Unbuilt,
    };
}

/// <summary>What a package of a class needs from the world when the blackboard chooses (<see cref="AiSystem"/> gives it; null: the base-class rules only).</summary>
public interface IPackageRules
{
    bool CanStart(Blackboard board, AiPackage package, PackageClass kind);
    bool Finished(Blackboard board, AiPackage package, PackageClass kind);
    /// <summary>A package of the class just started (a patrol lays out its route, a bar package moves the home).</summary>
    void Started(Blackboard board, AiPackage package, PackageClass kind);
    /// <summary>The running package gives way to another (the original's deactivate, package vtable +0x38).</summary>
    void Ended(Blackboard board, AiPackage package, PackageClass kind) { }
    /// <summary>A finished package may hand over to its <c>Leads to</c> (the original's vtable +0x90; the base class always may).</summary>
    bool MayLead(Blackboard board, AiPackage package, PackageClass kind) => true;
}

/// <summary>A guarded gate: its door leaf's centre at the leaf's foot, the unit X/Z way through it pointing away from the town, and half the leaf's width.</summary>
public readonly record struct GatePost(TownBuilding Building, Vector3 Door, Vector2 Out, float HalfWidth);

/// <summary>
/// A squad's AI state (docs/game/ai.md "Blackboard update"): its packages by slot, the package running and the role lists it loaded, a contract package that
/// overrides the slots, and what the running package needs: a home building, a patrol route. Changed only in <see cref="AiSystem"/>'s serial step; the members' think reads it.
/// </summary>
public sealed class Blackboard
{
    static readonly IReadOnlyList<AiGoal>[] NoRoles = [[], [], [], [], []];

    readonly SortedDictionary<int, List<AiPackage>> slots = [];

    public Blackboard(IEnumerable<SlottedPackage> packages)
    {
        foreach (var (slot, package) in packages)
        {
            if (!slots.TryGetValue(slot, out var list)) slots[slot] = list = [];
            list.Add(package);
        }
    }

    /// <summary>The package running (null: none could start; the members take only their default goals).</summary>
    public AiPackage? Current { get; private set; }
    public PackageClass CurrentClass => Current is { } p ? PackageClasses.Of(p.Signal) : PackageClass.Plain;
    public int CurrentSlot { get; private set; } = -1;
    /// <summary>The current package's place in its slot's list (-1: none, or a contract).</summary>
    public int CurrentIndex { get; private set; } = -1;
    /// <summary>Raised whenever another package starts, so the members choose again at once.</summary>
    public int Version { get; private set; }
    /// <summary>The current package's goals per <see cref="AiRole"/>.</summary>
    public IReadOnlyList<AiGoal>[] Roles { get; private set; } = NoRoles;
    /// <summary>How many members act out each goal type (the crowding count of the original's <c>TaskInfo</c>), as counted on the last package cadence.</summary>
    public Dictionary<TaskType, int> Crowd { get; } = [];
    /// <summary>All packages in slot order (highest first), as listed.</summary>
    public IEnumerable<SlottedPackage> Packages => slots.Reverse().SelectMany(s => s.Value.Select(p => new SlottedPackage(s.Key, p)));

    /// <summary>A contract package (the original's <c>contractjob</c>, set by dialogue's <c>AI contract</c>): while it runs it takes precedence over every slot.</summary>
    public AiPackage? Contract { get; private set; }
    /// <summary>The game hours (<see cref="World.GameHours"/>) the contract ends at.</summary>
    public double ContractEnd { get; private set; }

    /// <summary>The town the squad belongs to (a site index, -1: none) and the building it lives in (null: none known); <see cref="OwnHome"/> is the one it was given, <see cref="Home"/> the one in use (a bar package moves it to the bar).</summary>
    public int Town { get; init; } = -1;
    /// <summary>The town's turrets (<see cref="PopulationData.Turrets"/>) and the turret index each member's slot claimed to man, as the last serial step found them (derived, not hashed).</summary>
    public IReadOnlyList<TownBuilding> Turrets { get; set; } = [];
    public Dictionary<int, int> TurretClaims { get; } = [];
    /// <summary>The seat of the bar (<see cref="Home"/> under a bar package) each relaxing member's slot claimed, as the last serial step found it (derived, not hashed).</summary>
    public Dictionary<int, HomeSpot> SeatClaims { get; } = [];
    /// <summary>The gate the squad guards (its home when that is a gate, else for a gate guard goal the town's gate nearest the squad), as the last serial step found it (derived, not hashed); null: none, or its door's place not known.</summary>
    public GatePost? Gate { get; set; }
    public TownBuilding? OwnHome { get; set; }
    /// <summary>The template's <c>public day</c>, which the original writes over <see cref="OwnHome"/>'s public flag (docs/game/locks.md "Data"): what trespassing reads.</summary>
    public bool HomePublic { get; init; } = true;
    public TownBuilding? Home { get; set; }
    /// <summary>The table a bar package reserved for the squad: an index into <see cref="Home"/>'s <see cref="TownBuilding.Tables"/>, 0 for an outdoor table (the home itself); -1: none.</summary>
    public int BarTable { get; set; } = -1;
    /// <summary>The members it was made with: the squad size of the intact rule.</summary>
    public int Size { get; set; }
    /// <summary>The squad is intact (docs/game/perception.md 7.3), as the last serial step found it.</summary>
    public bool Intact { get; set; } = true;
    /// <summary>A patrol's route (world X/Z) and the point the leader walks to next; empty when no patrol runs.</summary>
    public Vector2[] Route { get; set; } = [];
    public int RouteIndex { get; set; }
    /// <summary>The game hours from which the next patrol may start, and when the running one gives up.</summary>
    public double NextPatrol { get; set; }
    public double PatrolGiveUp { get; set; }
    /// <summary>The town (site index) a retreat or slave shipping package goes to, set when it starts; -1: none.</summary>
    public int MissionTown { get; set; } = -1;

    /// <summary>
    /// The town the squad is in (the original's Blackboard +0x138, docs/game/ai.md "Package classes": its leader's town, else the town whose radius holds the squad) and the
    /// game hour it came in (+0x140); <see cref="CampaignTown.None"/> outside every town. Set on the package cadence before the choice.
    /// </summary>
    public CampaignTown InTown { get; set; } = CampaignTown.None;
    public double InTownSince { get; set; }
    /// <summary>The running package's mission town (its <c>mission town</c> hand): the trader's next town, the assaulted or occupied player town; none for other classes.</summary>
    public CampaignTown Destination { get; set; } = CampaignTown.None;
    /// <summary>
    /// The trader's state (<c>Package_WanderingTrader</c> +0xe0: 0 on the way, 1 just in the town, 2 done there) and the hours it stays in a town before it may go on
    /// (+0xd4, uniform [5, 8) at each start). Kept while other packages run, as the original's package object.
    /// </summary>
    public int TraderState { get; set; }
    public float TraderWait { get; set; }
    /// <summary>The passing-by assault (<c>Package_PassingByTownAssault</c>): the game hour it started (+0xb8), the faction it attacks (+0x100, the town's), whether it is spent (+0xb4: it never starts again) and its faction's wait after the last such assault ends (+0xb0, hours).</summary>
    public double AssaultStart { get; set; }
    public int AssaultEnemy { get; set; } = -1;
    public bool AssaultDone { get; set; }
    public float AssaultCooldown { get; set; }
    /// <summary>The occupation's clock (<c>Package_OccupyTown</c> +0xc0): game seconds in a row with no able member in the town; past 4 it ends.</summary>
    public float OccupyAway { get; set; }
    /// <summary>The refugee package (<c>Package_Refugee</c>): the squad was not intact when it started (+0xb0), and the squad it makes for to join (its mission target, +0x28; -1: none yet).</summary>
    public bool RefugeeBroken { get; set; }
    public int RefugeeTarget { get; set; } = -1;

    /// <summary>
    /// One pass of the original's package choice (docs/game/ai.md "Blackboard update", Verified): a running contract first; then slots from the highest down until
    /// one reports success. In a slot the list is scanned in order: the current package stays while it is not finished, or hands over to its <c>Leads to</c> package
    /// when it is; an entry before it that can start pre-empts it; a lone slot-0 package starts even when it cannot. Without <paramref name="rules"/> every
    /// package keeps the base class's rules except that only plain packages can start.
    /// </summary>
    public void Update(IPackageRules? rules = null, double hours = 0)
    {
        if (Contract is { } contract)
        {
            if (hours < ContractEnd) return;
            ended = (contract, MissionTarget);
            Contract = null;
            MissionTarget = CharacterId.None;
            Current = null;   // the slots choose again
        }
        foreach (var (slot, list) in slots.Reverse())
            if (TrySlot(slot, list, rules)) return;
    }

    bool TrySlot(int slot, List<AiPackage> list, IPackageRules? rules)
    {
        if (slot == 0 && list.Count == 1)
        {
            if (Current != list[0]) Start(list[0], 0, rules);
            else if (Finished(list[0], rules))
            {
                // Finished and alone: its Leads to, else itself again once it can start (a patrol's next round).
                if (list[0].LeadsTo is { } hop && Find(hop) is { } follow && MayLead(list[0], rules)) Start(follow.Package, follow.Slot, rules);
                else if (CanStart(list[0], rules)) Start(list[0], 0, rules);
            }
            return true;
        }
        foreach (var p in list)
        {
            if (p == Current)
            {
                if (!Finished(p, rules)) return true;
                if (p.LeadsTo is { } next && Find(next) is { } follow && MayLead(p, rules))
                {
                    Start(follow.Package, follow.Slot, rules);
                    return true;
                }
                return false;
            }
            if (CanStart(p, rules))
            {
                Start(p, slot, rules);
                return true;
            }
        }
        return false;
    }

    bool CanStart(AiPackage p, IPackageRules? rules)
    {
        var kind = PackageClasses.Of(p.Signal);
        return kind == PackageClass.Plain || (rules?.CanStart(this, p, kind) ?? false);
    }

    bool Finished(AiPackage p, IPackageRules? rules)
    {
        var kind = PackageClasses.Of(p.Signal);
        return kind != PackageClass.Plain && (rules?.Finished(this, p, kind) ?? false);
    }

    bool MayLead(AiPackage p, IPackageRules? rules) => rules?.MayLead(this, p, PackageClasses.Of(p.Signal)) ?? true;

    SlottedPackage? Find(string id)
    {
        foreach (var (slot, list) in slots)
            foreach (var p in list)
                if (p.Id == id) return new SlottedPackage(slot, p);
        return null;
    }

    void Start(AiPackage package, int slot, IPackageRules? rules)
    {
        if (Current is { } old && old != package) rules?.Ended(this, old, PackageClasses.Of(old.Signal));
        Current = package;
        CurrentSlot = slot;
        CurrentIndex = slot >= 0 && slots.TryGetValue(slot, out var list) ? list.IndexOf(package) : -1;
        Roles = package.Roles;
        Version++;
        Route = [];
        RouteIndex = 0;
        Home = OwnHome;
        BarTable = -1;
        MissionTown = -1;
        Destination = CampaignTown.None;
        RefugeeBroken = false;
        RefugeeTarget = -1;
        rules?.Started(this, package, PackageClasses.Of(package.Signal));
    }

    /// <summary>Makes the members think again at once (a patrol moved on).</summary>
    public void Touch() => Version++;

    /// <summary>
    /// Starts a contract package that runs for <paramref name="hours"/> game hours from <paramref name="now"/> (docs/game/ai.md "Dialogue and AI": under 1 hour
    /// means 10 minutes, Observed), aimed at <paramref name="target"/> (its goals aimed at the squad's mission, as a bodyguard's, follow it). It takes precedence over the
    /// slots until it ends; then the slots choose again.
    /// </summary>
    public void StartContract(AiPackage package, double now, double hours, IPackageRules? rules = null, CharacterId? target = null)
    {
        Contract = package;
        ContractEnd = now + (hours < 1 ? 10.0 / 60 : hours);
        MissionTarget = target ?? CharacterId.None;
        Start(package, -1, rules);
    }

    /// <summary>A saved contract (<see cref="Saving.AiSave"/>) running again until <paramref name="end"/>, with no target.</summary>
    /// <summary>
    /// Makes a package of the slots the current one again without its start (the original keeps the running package object on its platoon while the squad is out of the
    /// world); false when no slot lists it.
    /// </summary>
    public bool Resume(AiPackage package)
    {
        foreach (var (slot, list) in slots)
            if (list.Contains(package))
            {
                Start(package, slot, null);
                return true;
            }
        return false;
    }

    public void ResumeContract(AiPackage package, double end, IPackageRules? rules = null)
    {
        Contract = package;
        ContractEnd = end;
        MissionTarget = CharacterId.None;
        Start(package, -1, rules);
    }

    /// <summary>The package <c>change AI</c> put in place of the template's (saved as <c>replacementAI</c>); null: the template's.</summary>
    public AiPackage? Replacement { get; private set; }

    /// <summary>The squad's mission target: whom its running contract is aimed at (none without one).</summary>
    public CharacterId MissionTarget { get; private set; } = CharacterId.None;

    /// <summary>The contract that ended on the last <see cref="Update"/> and whom it was aimed at, until <see cref="TakeEnded"/> takes it.</summary>
    (AiPackage Package, CharacterId Target)? ended;

    /// <summary>The contract that ended since the last call (the AI fires the leader's <c>contract end talk passive</c>, event 30), or null.</summary>
    public (AiPackage Package, CharacterId Target)? TakeEnded()
    {
        var e = ended;
        ended = null;
        return e;
    }

    /// <summary>
    /// Dialogue's <c>change AI</c> (<c>FUN_140272430</c>): the squad's packages become <paramref name="package"/> alone and it starts at once (every member chooses again). A running
    /// contract keeps running.
    /// </summary>
    public void Replace(AiPackage package, IPackageRules? rules = null)
    {
        Replacement = package;
        slots.Clear();
        slots[0] = [package];
        if (Contract is null) Start(package, 0, rules);
        else Version++;
    }

    internal void Hash(ref StateHasher hasher)
    {
        hasher.Add(CurrentSlot);
        hasher.Add(CurrentIndex);
        hasher.Add(Version);
        hasher.Add(Contract is not null);
        hasher.Add(ContractEnd);
        if (!MissionTarget.IsNone) hasher.Add(MissionTarget);
        if (Replacement is not null) hasher.Add(Rng.StableHash(Replacement.Id));
        hasher.Add(Intact);
        hasher.Add(Route.Length);
        foreach (var p in Route) hasher.Add(p);
        hasher.Add(RouteIndex);
        hasher.Add(NextPatrol);
        hasher.Add(PatrolGiveUp);
        hasher.Add(Home?.Position ?? default);
        if (BarTable >= 0) hasher.Add(BarTable);
        if (MissionTown >= 0) hasher.Add(MissionTown + 0x4D69);
        // The package classes' own state, only once one has run (worlds without them hash as before).
        if (!Destination.IsNone) { hasher.Add(Destination.Site); hasher.Add(Destination.PlayerTown); }
        if (TraderWait > 0) { hasher.Add(TraderState); hasher.Add(TraderWait); hasher.Add(InTown.Site); hasher.Add(InTown.PlayerTown); hasher.Add(InTownSince); }
        if (AssaultEnemy >= 0 || AssaultDone) { hasher.Add(AssaultStart); hasher.Add(AssaultEnemy); hasher.Add(AssaultDone); hasher.Add(AssaultCooldown); }
        if (OccupyAway > 0) hasher.Add(OccupyAway);
        if (RefugeeBroken || RefugeeTarget >= 0) { hasher.Add(RefugeeBroken); hasher.Add(RefugeeTarget); }
    }
}

/// <summary>How readily an AI character starts a fight with someone it sees (docs/simulation.md "AI", Combat goals): from the combat goals of its lists in their hours.</summary>
public enum AiStance : byte
{
    /// <summary>Starts no fight (it still fights back when attacked).</summary>
    Passive = 0,
    /// <summary>Attacks enemies fighting its own squad: a mate's target or a mate's attacker (PROTECT_OWN_SQUAD, docs/game/ai-tasks.md "Combat goals").</summary>
    ProtectSquad = 1,
    /// <summary>Attacks enemies that are fighting (PROTECT_ALLIES and the like).</summary>
    Defensive = 2,
    /// <summary>Attacks enemies within its home (TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME).</summary>
    Territorial = 3,
    /// <summary>Attacks enemies on sight (ATTACK_ENEMIES; also the default for a character whose lists hold no combat goal, the old rule).</summary>
    Aggressive = 4,
    /// <summary>Attacks enemies and neutrals on sight (ATTACK_ENEMIES_AND_NEUTRALS).</summary>
    AggressiveNeutrals = 5,
}

/// <summary>What the AI decided for one character (<see cref="CharacterCold.Ai"/>): the goal it acts out, where its post and bed are, how readily it fights.</summary>
public sealed class AiMind
{
    /// <summary>The squad it thinks for (fixed for the mind's life: a character does not change squads), held so the think needs no lookup. Not hashed (the squad is).</summary>
    public required Squad Squad { get; init; }
    /// <summary>Its place among the squad's members when the mind was made: which bed and which node of the home it takes.</summary>
    public int MemberIndex { get; init; }
    /// <summary>The goal being acted out (null before the first think).</summary>
    public AiGoal? Goal { get; set; }
    /// <summary>The goal's plan (<see cref="TaskPlanner"/>), leaf first: the step carried out is the first. Planned again at every think.</summary>
    public List<PlanStep> Plan { get; } = [];
    /// <summary>The plan's first step (None without a plan).</summary>
    public TaskType Step => Plan.Count > 0 ? Plan[0].Type : TaskType.None;
    public AiBehaviour Behaviour { get; set; }
    public AiStance Stance { get; set; } = AiStance.Aggressive;
    /// <summary>The <see cref="Blackboard.Version"/> the goal was chosen under.</summary>
    public int Version { get; set; } = -1;
    /// <summary>Its own spot: where it stood when the AI took it over (the stand-in where the home has no node or bed for it).</summary>
    public Vector2 Post { get; set; }
    /// <summary>The game hours (<see cref="World.GameHours"/>) until which it is committed to its goal (docs/game/ai.md "Packages and goals").</summary>
    public double CommitUntil { get; set; }
    /// <summary>Where its town goal goes (a node, a shop, a town; null: not picked yet, picked when the goal is acted out).</summary>
    public Vector3? Target { get; set; }
    /// <summary>The centre and radius <see cref="MovementSystem"/>'s wander uses instead of the squad's home (radius 0: the squad's).</summary>
    public Vector2 WanderCentre { get; set; }
    public float WanderRadius { get; set; }
    /// <summary>Its race's <c>AI Goals</c>, looked up once when the mind is made (the lookup takes a lock; the think runs on every worker). Follows from the race, so not hashed.</summary>
    public IReadOnlyList<AiGoal> RaceGoals { get; init; } = [];
    /// <summary>It was sent walking to a spot and has not arrived.</summary>
    public bool Walking { get; set; }
    /// <summary>Walks in a row that ended away from the spot (no way there, or interrupted); at <see cref="AiSystem.MaxFailedWalks"/> it gives the spot up.</summary>
    public int FailedWalks { get; set; }
    /// <summary>The home's spots kept failing: it uses <see cref="Post"/> instead until its home changes.</summary>
    public bool HomeUnreachable { get; set; }
    /// <summary>Walks to its seat kept failing: it does not relax or sit there any more.</summary>
    public bool SeatUnreachable { get; set; }
    /// <summary>The sitting clip of the chair it sits on (<see cref="HomeSpot.SitClip"/>), for <see cref="AnimationSystem"/>; null when not seated. Follows from the goal and seat, so not hashed.</summary>
    public string? SitClip { get; set; }
    /// <summary>That chair's functionality <c>hunger rate</c> (<see cref="HomeSpot.HungerRate"/>; 1 when not seated). Set with <see cref="SitClip"/>, so not hashed.</summary>
    public float SitHungerRate { get; set; } = 1;
    /// <summary>
    /// The work goals' pick of building (<see cref="AiBehaviour.Work"/>): the number of picks made since it last stopped working, so the plan's finder and the act-out name the same
    /// building (the pick is a roll of this and the character), and whether it is working now.
    /// </summary>
    public int WorkEpoch { get; set; }
    public bool WorkActive { get; set; }
    /// <summary>The chosen canonical level placement and this think's operation/collection signal (derived, not hashed).</summary>
    public Meitou.Data.World.TownBuilding? WorkBuilding { get; set; }
    public Meitou.Data.World.TownBuilding? WorkOperate { get; set; }
    public Meitou.Data.World.TownBuilding? CollectFrom { get; set; }
    /// <summary>The clip it plays at its work (the ANIMATION its building's functionality names), cleared with <see cref="CharacterCold.ActionClip"/> when it stops; null when not working.</summary>
    public string? WorkClip { get; set; }
    /// <summary>Its work building's functionality <c>hunger rate</c> (1 by default), what <c>Task_OperateMachine</c> gives its operator. Set with <see cref="WorkClip"/>, so not hashed.</summary>
    public float WorkHungerRate { get; set; } = 1;
    /// <summary>Its first think puts it where its plan's walk ends (<see cref="Squad.Resume"/>: back from its town's time unloaded); cleared by that think.</summary>
    public bool Resume { get; set; }
    /// <summary>Where the serial step puts it this tick (set by the think that took <see cref="Resume"/> up); null otherwise.</summary>
    public Vector3? PlaceAt { get; set; }
    /// <summary>The bed the serial step lies it in this tick (set by the think that arrived at its home's bed spot to sleep); null otherwise.</summary>
    public string? BedAt { get; set; }
    /// <summary>Whom its follow goal follows instead of the squad leader: the contract's mission target for a goal aimed at it (none: the leader).</summary>
    public CharacterId FollowTarget { get; set; } = CharacterId.None;
    /// <summary>Whether its lists hold any combat goal at all (else the stance stays the old default).</summary>
    public bool HasCombatGoals { get; set; }
    /// <summary>Whether a PROTECT_ALLIES goal (15 or 288) is in its lists and hours: it defends allies of other squads against any attacker (<see cref="RetaliationSystem"/>).</summary>
    public bool ProtectsAllies { get; set; }
    /// <summary>The role list and hour <see cref="Stance"/> was worked out for (a cache; the stance itself is hashed).</summary>
    internal IReadOnlyList<AiGoal>? StanceFrom { get; set; }
    internal int StanceHour { get; set; } = -1;
    /// <summary>The investigation's look-about count in seconds (<see cref="Crime.CrimeSystem.LookAboutSeconds"/>; the task starts it at 15).</summary>
    public float LookAbout { get; set; } = Crime.CrimeSystem.FirstLookAboutSeconds;

    internal void Hash(ref StateHasher hasher)
    {
        hasher.Add((int)(Goal?.Task.Type ?? TaskType.None));
        hasher.Add((int)Step);
        hasher.Add((int)Behaviour);
        hasher.Add((int)Stance);
        hasher.Add(Version);
        hasher.Add(Post);
        hasher.Add(WanderCentre);
        hasher.Add(WanderRadius);
        hasher.Add(Walking);
        hasher.Add(FailedWalks);
        hasher.Add(HomeUnreachable);
        hasher.Add(SeatUnreachable);
        hasher.Add(CommitUntil);
        hasher.Add(Target ?? new Vector3(float.NaN));
        if (WorkActive || WorkEpoch != 0) { hasher.Add(WorkEpoch); hasher.Add(WorkActive); }
        if (Resume) hasher.Add(0x52657375);
        if (PlaceAt is { } place) hasher.Add(place);
        if (BedAt is { } bed) hasher.Add(Rng.StableHash(bed));
        if (!FollowTarget.IsNone) hasher.Add(FollowTarget);
        if (LookAbout != Crime.CrimeSystem.FirstLookAboutSeconds) hasher.Add(LookAbout);
    }
}
