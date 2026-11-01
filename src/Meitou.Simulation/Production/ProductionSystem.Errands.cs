using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Production;

// The carrying jobs of the lists (docs/game/ai-tasks.md "Perma-jobs" and "Finders of the jobs"): corpse disposal (137 with its finder 138) and the removal of intruders
// (223 / 273 with their companions 224 / 274). A pass every ErrandEveryTicks ticks lets a free character with the job lift what the job's finder names and walks it to where the companion
// task takes it; the lift and the put-down are the carry system's.
public sealed partial class ProductionSystem
{
    /// <summary>The carry system that lifts and puts down the bodies of the carrying jobs; null: they never run.</summary>
    public CarrySystem? Carry { get; set; }

    /// <summary>Ticks between the looks of the carrying jobs (engine choice, as the rescue job's).</summary>
    public const int ErrandEveryTicks = 15;
    /// <summary>A body carried out is put down within this of its goal, or after <see cref="ErrandWalks"/> walks that ended short (engine choices).</summary>
    public const float ErrandArrive = 40;
    public const int ErrandWalks = 4;
    /// <summary>How far past a door an intruder is carried (the original's point is the door's position pushed out by 1.1 of a reference vector, <b>Unknown</b>), and past a town's farthest building (<b>Observed</b>: 200 beyond the town's edge).</summary>
    public const float OutsideDoor = 30, OutsideTown = 200;
    /// <summary>The finders look this far for a body or an intruder (the sensed list's reach, an engine stand-in as the rescue job's).</summary>
    const float ErrandSight = 1600;

    enum ErrandKind : byte { Corpse, OutOfBuilding, OutOfTown }

    /// <summary>One carrying job under way: what the character carries it for, the goal of its walk and how many walks ended short.</summary>
    sealed class Errand
    {
        public ErrandKind Kind;
        public Vector2 Goal;
        public int Tries;
        public CharacterId Body;
        /// <summary>The carrier's current GoTo is this job's own (else a GoTo task is an order of the player's).</summary>
        public bool Walking;
        public CharacterControl? Control;
        public DisposeCorpseOrder? Command;
        public BuildSite? Site;
        public Machine? Machine;
        public ulong SiteRevision;
        public ulong MembershipRevision;
        public int Owner;
    }

    // Derived state, not saved or hashed: a lift under way is looked at again from the characters' own state when it is lost.
    readonly Dictionary<CharacterId, Errand> errands = [];
    // Bodies a carrier gave up on (no machine to take them to, or walks that kept ending short): the original's give-up list of the squad.
    readonly HashSet<CharacterId> givenUp = [];

    /// <summary>The finders' kinds a job type runs, with the companion type that must be in the list for the carrying out.</summary>
    static ErrandKind? KindOf(TaskType type) => type switch
    {
        TaskType.FIND_A_DEAD_GUY => ErrandKind.Corpse,
        TaskType.PICKUP_INTRUDERS => ErrandKind.OutOfBuilding,
        TaskType.PICKUP_INTRUDERS_TOWN => ErrandKind.OutOfTown,
        _ => null,
    };

    /// <summary>
    /// The carrying jobs of every player character (serial, slot order): one with an errand under way is stepped; a free one (<see cref="PermaJobs.FreeFor"/>, able to carry) with a job whose
    /// finder finds something lifts it, the first job of its list that does. The corpse job needs its finder 138 and its delivery 137 in the list, an intruder job its companion 224 / 274.
    /// </summary>
    void Errands(World world)
    {
        if (Carry is null) return;
        ValidateErrands(world);
        if (world.Tick % ErrandEveryTicks != 0) return;
        var table = world.Characters;
        foreach (var gone in errands.Keys.Where(k => !table.IsAlive(k)).ToList()) errands.Remove(gone);
        HashSet<CharacterId>? lifted = null;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { IsPlayer: true } cold) continue;
            var id = table.IdOf(i);
            if (errands.TryGetValue(id, out var errand))
            {
                StepErrand(world, i, cold, errand);
                continue;
            }
            if (world.Players.RequiresExplicitControl && world.Players.ControlOf(id) is null) continue;
            if (cold.Jobs.Count == 0 || !cold.Carrying.IsNone || !CarrySystem.CanCarry(cold) || cold.Fleeing) continue;
            var task = (CharacterTask)table.Next[i].Task;
            for (int k = 0; k < cold.Jobs.Count; k++)
            {
                if (KindOf(cold.Jobs[k].Type) is not { } kind || !PermaJobs.FreeFor(cold, task, k)) continue;
                var companion = kind switch { ErrandKind.Corpse => TaskType.DEAD_GUYS_GO_IN_THE_POT, ErrandKind.OutOfBuilding => TaskType.TAKE_INTRUDER_OUTSIDE, _ => TaskType.TAKE_INTRUDER_OUTSIDE_TOWN };
                if (PermaJobs.IndexOf(cold, companion) < 0) continue;
                lifted ??= Lifted(table);
                var found = kind == ErrandKind.Corpse ? FindCorpse(world, i, cold, lifted) : FindIntruder(world, i, cold, lifted, kind);
                if (found is not { } body) continue;
                if (kind == ErrandKind.Corpse && (NearestIncinerator(world, table.Next[i].Position, cold.Faction) is not { } authorizedSite
                    || !MayWork(world, cold, authorizedSite.Site))) continue;
                var resume = cold.JobIndex >= 0 || cold.Implicit is not null ? cold.ResumeTask : task;
                if (!Carry.BeginPickUp(world, i, body.Id, resume)) continue;
                lifted.Add(body.Id);
                var started = new Errand { Kind = kind, Goal = body.Goal, Body = body.Id, Control = world.Players.ControlOf(id) };
                if (kind == ErrandKind.Corpse && NearestIncinerator(world, table.Next[i].Position, cold.Faction) is { } disposal)
                    BindDisposal(world, started, disposal.Site, disposal.Machine);
                errands[id] = started;
                break;
            }
        }
    }

    /// <summary>The Corpse Disposal order (<see cref="DisposeCorpseOrder"/>): a corpse errand for each player character that carries a dead body.</summary>
    void DisposeCarried(World world, DisposeCorpseOrder order)
    {
        var table = world.Characters;
        if (Site(world, order.Site) is not { Complete: true } site || world.Machines.Find(order.Site) is not { } machine
            || machine.Data.Function != Meitou.Data.World.TownBuildings.CorpseDisposalFunction || !MayStartWork(world, order, site)) return;
        var actors = world.CommandCharacters(order, order.Characters);
        if (order.Issuer is not null && order.Bodies.Count != actors.Count) return;
        for (int index = 0; index < actors.Count; index++)
        {
            var id = actors[index];
            if (!table.TryResolveNext(id, out int i) || table.Cold(i) is not { IsPlayer: true, Medical.Incapacitated: false } cold || cold.Carrying.IsNone || !MayWork(world, cold, site)) continue;
            if (order.Issuer is not null && order.Bodies[index] != cold.Carrying) continue;
            if (!table.TryResolveNext(cold.Carrying, out int b) || table.Cold(b)?.Medical is not { Dead: true }) continue;
            var errand = new Errand { Kind = ErrandKind.Corpse, Body = cold.Carrying, Command = order, Control = world.Players.ControlOf(id) };
            BindDisposal(world, errand, site, machine);
            errands[id] = errand;
        }
    }

    void BindDisposal(World world, Errand errand, BuildSite site, Machine machine)
    {
        errand.Site = site;
        errand.Machine = machine;
        errand.Owner = site.Owner;
        errand.SiteRevision = world.BuildingRevision(site);
        errand.MembershipRevision = errand.Control is { } grant ? world.Players.Find(grant.Player)?.MembershipRevision(site.Owner) ?? 0 : 0;
    }

    public void CloseCorpseErrands(World world, PlayerId player)
    {
        foreach (var (actor, errand) in errands.ToArray())
        {
            if (errand.Control?.Player != player) continue;
            CancelErrand(world, actor, errand);
        }
    }

    void CancelErrand(World world, CharacterId actor, Errand errand)
    {
        var table = world.Characters;
        if (table.TryResolveNext(actor, out int slot))
        {
            var cold = table.Cold(slot)!;
            if (table.Next[slot].Task == (byte)CharacterTask.PickUp && cold.CarryTarget == errand.Body)
                Carry!.CancelInstruction(world, slot);
            else if (errand.Walking && table.Next[slot].Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, slot);
        }
        errands.Remove(actor);
    }

    void ValidateErrands(World world)
    {
        foreach (var (actor, errand) in errands.ToArray())
        {
            var table = world.Characters;
            if (!table.TryResolveNext(actor, out int slot)) { errands.Remove(actor); continue; }
            var cold = table.Cold(slot)!;
            bool valid = !world.Players.RequiresExplicitControl || errand.Control is { } grant && world.Players.Validate(grant)
                && (errand.Command is null || world.MayOrder(errand.Command, actor));
            if (valid && errand.Site is { } site)
            {
                valid = ReferenceEquals(Site(world, site.Id), site) && ReferenceEquals(world.Machines.Find(site.Id), errand.Machine)
                    && site.Owner == errand.Owner && site.Complete && MayWork(world, cold, site)
                    && world.BuildingRevision(site) == errand.SiteRevision;
                if (valid && errand.Control is { } control)
                    valid = world.Players.Find(control.Player) is { } player && player.HasPermission(site.Owner, FactionPermission.ManageAssets)
                        && player.MembershipRevision(site.Owner) == errand.MembershipRevision;
            }
            if (valid && (!cold.Carrying.IsNone && cold.Carrying != errand.Body || cold.Medical is not { Incapacitated: false } || !cold.CarriedBy.IsNone)) valid = false;
            if (valid) continue;
            // Losing authority cancels work; the existing carried entity and its items remain linked.
            CancelErrand(world, actor, errand);
        }
    }

    /// <summary>The bodies being lifted or carried now.</summary>
    static HashSet<CharacterId> Lifted(CharacterTable table)
    {
        var set = new HashSet<CharacterId>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { } c)
            {
                if (!c.Carrying.IsNone) set.Add(c.Carrying);
                if (table.Next[i].Task == (byte)CharacterTask.PickUp && !c.CarryTarget.IsNone) set.Add(c.CarryTarget);
            }
        return set;
    }

    // ---- stepping a carrying job ----

    void StepErrand(World world, int i, CharacterCold cold, Errand e)
    {
        var table = world.Characters;
        var id = table.IdOf(i);
        ref var n = ref table.Next[i];
        var task = (CharacterTask)n.Task;
        if (task is CharacterTask.PickUp or CharacterTask.PutDown) return;
        if (cold.Carrying.IsNone)
        {
            // Not lifted (the lift broke off) or put down: over.
            errands.Remove(id);
            return;
        }
        // An order of the player took the carrier elsewhere: the job is over, the body stays with it.
        if (task == CharacterTask.GoTo && !e.Walking) { errands.Remove(id); return; }
        if (task is not (CharacterTask.GoTo or CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow)) { errands.Remove(id); return; }
        if (task != CharacterTask.GoTo) e.Walking = false;
        Vector2 goal = e.Goal;
        BuildSite? site = null;
        Machine? machine = null;
        if (e.Kind == ErrandKind.Corpse)
        {
            if (e.Site is { } bound && e.Machine is { } boundMachine) (site, machine) = (bound, boundMachine);
            else
            {
                if (NearestIncinerator(world, n.Position, cold.Faction) is not { } found) { GiveUp(world, i, e); return; }
                (site, machine) = found;
                BindDisposal(world, e, site, machine);
            }
            if (!MayWork(world, cold, site) || OperatorsOf(world, site.Id) >= machine.Data.MaxOperators) return;
            goal = GoalOf(world, site, machine);
        }
        bool arrived = site is not null ? AtSite(n, site, machine!) : Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), goal) <= ErrandArrive;
        if (arrived)
        {
            if (e.Kind == ErrandKind.Corpse)
            {
                if (cold.Carrying != e.Body || !table.TryResolveNext(e.Body, out int body) || table.Cold(body)?.Medical is not { Dead: true })
                { errands.Remove(id); return; }
                Incinerate(world, i, cold, site!, machine!);
            }
            else Carry!.BeginPutDown(world, i);
            errands.Remove(id);
            return;
        }
        if (task == CharacterTask.GoTo) return;   // on its way
        if (++e.Tries > ErrandWalks)
        {
            if (e.Kind == ErrandKind.Corpse) GiveUp(world, i, e);
            else
            {
                Carry!.BeginPutDown(world, i);
                errands.Remove(id);
            }
            return;
        }
        e.Goal = goal;
        e.Walking = true;
        MovementSystem.GoTo(table, i, goal, cold.RunSpeed);
    }

    /// <summary>The carrier drops the body where it stands and the job leaves it alone from now on (the squad's give-up list).</summary>
    void GiveUp(World world, int i, Errand e)
    {
        givenUp.Add(e.Body);
        Carry!.LetGo(world, i);
        errands.Remove(world.Characters.IdOf(i));
    }

    // ---- corpse disposal (137 / 138) ----

    /// <summary>
    /// Feeds the carried body into the machine (<c>Task_FeedCorpseIntoMachine</c> slot 3, <b>Verified</b> by decompile): the carrier lets go of it and the character is removed from the world
    /// ("incinerated or cooked"), its items with it. The original plays the lift clip back first and the sound <c>Character_Drop_Person</c>; Meitou removes it at once.
    /// The machine's functionality <c>string</c> floats up from 5 units over it (<c>FUN_1403567a0</c>).
    /// </summary>
    void Incinerate(World world, int i, CharacterCold cold, BuildSite site, Machine machine)
    {
        var table = world.Characters;
        var body = cold.Carrying;
        if (!Carry!.LetGo(world, i) || !table.IsAlive(body)) return;
        if (machine.Data.Label.Length > 0) world.EmitFloater(Floater.Label(site.Position + new Vector3(0, 5, 0), machine.Data.Label));
        givenUp.Remove(body);
        world.Uniques.Left(body);
        table.Remove(body);
    }

    /// <summary>
    /// The finder of job 138 (<c>FUN_1408cfb00</c>, <b>Verified</b> by decompile): a dead, non-robot body in a cage of the character's town first (<c>FUN_1408cbea0</c> with the cage mask), else
    /// the nearest dead body in sight that nobody carries (<c>FUN_1408ce170</c>), neither in the give-up list. Meitou's cages are the player's own, the town is the haul range, and sight is
    /// <see cref="ErrandSight"/> (engine stand-ins). The goal is the machine (found again on each step).
    /// </summary>
    (CharacterId Id, Vector2 Goal)? FindCorpse(World world, int slot, CharacterCold cold, HashSet<CharacterId> lifted)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        Machine? any = NearestIncinerator(world, at, cold.Faction) is { } inc ? inc.Machine : null;
        if (any is null) return null;   // nothing to take it to: the job is blocked
        CharacterId? best = null;
        float bestD = float.MaxValue;
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || f.Owner != cold.Faction || f.Occupant.IsNone || !table.TryResolveNext(f.Occupant, out int s)) continue;
            if (!Liftable(world, table, s, f.Occupant, lifted)) continue;
            float d = Gap(at, f.Position);
            if (d <= Settings.HaulRange && d < bestD) (best, bestD) = (f.Occupant, d);
        }
        if (best is null)
        {
            for (int s = 0; s < table.HighWater; s++)
            {
                if (s == slot || !table.Next[s].Alive) continue;
                var id = table.IdOf(s);
                if (!Liftable(world, table, s, id, lifted)) continue;
                float d = Gap(at, table.Next[s].Position);
                if (d <= ErrandSight && d < bestD) (best, bestD) = (id, d);
            }
        }
        return best is { } body ? (body, Vector2.Zero) : null;
    }

    /// <summary>A dead, non-robot body that can be lifted, that nobody has and that is not in the give-up list.</summary>
    bool Liftable(World world, CharacterTable table, int s, CharacterId id, HashSet<CharacterId> lifted) =>
        table.Cold(s) is { Medical.Dead: true } body && body.Race is { IsRobot: false } && !lifted.Contains(id) && !givenUp.Contains(id) && CarrySystem.CanBeLifted(world, body);

    /// <summary>
    /// The corpse machine a body is taken to (<c>FUN_1408d0770</c> for function 11, <b>Verified</b> by decompile): the player's complete corpse-disposal building with an operator place free, the
    /// nearest weighing most (the original's weight is clamp(1 - d^2 / 2.1e7, 1e-5, 1), x100 for the character's own faction, the other factors do not apply to the player's own buildings).
    /// </summary>
    (BuildSite Site, Machine Machine)? NearestIncinerator(World world, Vector3 from, int faction)
    {
        BuildSite? bestSite = null;
        Machine? best = null;
        float bestGap = float.MaxValue;
        foreach (var m in world.Machines.All)
        {
            if (m.Data.Function != Meitou.Data.World.TownBuildings.CorpseDisposalFunction || Site(world, m.Site) is not { Complete: true } site || site.Owner != faction || !WorkSiteOpen(world, site)) continue;
            if (OperatorsOf(world, site.Id) >= m.Data.MaxOperators) continue;
            float gap = Gap(site.Position, from);
            if (gap > bestGap || (gap == bestGap && string.CompareOrdinal(m.Site, best?.Site) > 0)) continue;
            (bestSite, best, bestGap) = (site, m, gap);
        }
        return best is null ? null : (bestSite!, best);
    }

    // ---- intruders (223 / 273) ----

    /// <summary>
    /// The finders of jobs 223 (<c>FUN_14099c110</c>) and 273 (<c>FUN_14099c250</c>, whose own test is the carrier's town, docs/game/ai-tasks.md; the AI's: <see cref="Kidnap.TownDutySystem"/>) for a
    /// player character, <b>Observed</b>: a downed character in the building the carrier stands in, not carried,
    /// not caged (223: not in a bed either, special state 0). Engine choices: the building is the player's complete building with doors whose footprint holds the carrier (the original's
    /// "home" of 223 is the Ownerships' +0x38, whose value for the player faction is <b>Unknown</b>); an intruder is knocked out, alive, and neither of the carrier's faction nor an ally (the
    /// sensed list's exclude bits 0x201 are <b>Unknown</b>); the extra test of 273 (a character vfunc 0x118 above 0) is taken as true. The goal is outside the door (223 / 224,
    /// <c>Task_TakeIntruderOutside</c>) or outside the town (273 / 274, <c>Task_TakeIntruderOutsideTown</c>).
    /// </summary>
    (CharacterId Id, Vector2 Goal)? FindIntruder(World world, int slot, CharacterCold cold, HashSet<CharacterId> lifted, ErrandKind kind)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        if (EnclosedAt(world, at, cold.Faction) is not { } site) return null;
        CharacterId? best = null;
        float bestD = float.MaxValue;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !table.Next[s].Alive || table.Cold(s) is not { Medical: { Unconscious: true, Dead: false } } body) continue;
            var id = table.IdOf(s);
            if (lifted.Contains(id) || givenUp.Contains(id) || body.IsImprisoned || !body.CarriedBy.IsNone || !CarrySystem.CanBeLifted(body)) continue;
            if (kind == ErrandKind.OutOfBuilding && body.InSomething != HeldState.None) continue;
            if (Carry!.Friendly(cold, body)) continue;
            if (EnclosedAt(world, table.Next[s].Position, cold.Faction) != site) continue;
            float d = Gap(at, table.Next[s].Position);
            if (d <= ErrandSight && d < bestD) (best, bestD) = (id, d);
        }
        return best is { } found ? (found, OutsideGoal(world, site, at, kind)) : null;
    }

    /// <summary>A point lies in one of the player's complete buildings with doors (<see cref="EnclosedAt"/>): the context menu's Throw out of home.</summary>
    public bool InPlayerBuilding(World world, Vector3 at) => InPlayerBuilding(world, at, world.Player.Faction);
    public bool InPlayerBuilding(World world, Vector3 at, int faction) => EnclosedAt(world, at, faction) is not null;

    /// <summary>The player's complete building with doors whose footprint holds a point (the nearest by origin), or null.</summary>
    BuildSite? EnclosedAt(World world, Vector3 at, int faction)
    {
        BuildSite? best = null;
        float bestGap = float.MaxValue;
        foreach (var site in world.Buildings.All)
        {
            float radius = Settings.Footprint?.Invoke(site.Record) ?? 0;
            if (!site.Complete || site.Owner != faction || radius <= 0) continue;
            float gap = Gap(site.Position, at);
            if (gap > radius || gap >= bestGap || !world.Fixtures.OfBuilding(site.Id).Any(f => f.Kind == FixtureKind.Door)) continue;
            (best, bestGap) = (site, gap);
        }
        return best;
    }

    /// <summary>
    /// Where an intruder is put down: just past the building's door nearest the carrier (<see cref="OutsideDoor"/>), or past the farthest building of its town (<see cref="OutsideTown"/>) on the
    /// side of the carrier. Pulled in towards the building until the ground is walkable.
    /// </summary>
    Vector2 OutsideGoal(World world, BuildSite site, Vector3 from, ErrandKind kind)
    {
        var centre = new Vector2(site.Position.X, site.Position.Z);
        var start = new Vector2(from.X, from.Z);
        Vector2 origin = centre, dir;
        float distance;
        if (kind == ErrandKind.OutOfTown && world.Buildings.FindTown(site.Town) is { } town)
        {
            origin = new Vector2(town.Position.X, town.Position.Z);
            float far = 0;
            foreach (var other in world.Buildings.All)
                if (other.Town == town.Number) far = MathF.Max(far, Vector2.Distance(origin, new Vector2(other.Position.X, other.Position.Z)));
            dir = start - origin;
            distance = far + OutsideTown;
        }
        else
        {
            var door = world.Fixtures.DoorPicks.Where(d => d.Building == site.Id).OrderBy(d => Gap(d.Position, from)).FirstOrDefault();
            var doorAt = door is null ? centre : new Vector2(door.Position.X, door.Position.Z);
            dir = door is null ? start - centre : doorAt - centre;
            origin = doorAt;
            distance = OutsideDoor;
            if (door is null) distance = (Settings.Footprint?.Invoke(site.Record) ?? 0) + OutsideDoor;
        }
        dir = dir.LengthSquared() > 1e-4f ? Vector2.Normalize(dir) : Vector2.UnitX;
        for (int tries = 0; tries < 4; tries++, distance *= 0.75f)
        {
            var p = origin + dir * distance;
            if (world.Walkability.IsWalkable(p.X, p.Y)) return p;
        }
        return origin + dir * distance;
    }
}
