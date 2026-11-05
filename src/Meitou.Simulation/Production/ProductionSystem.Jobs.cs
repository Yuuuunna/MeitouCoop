using Meitou.Data.Gameplay.Ai;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

// Job lists (Perma-Jobs; docs/simulation.md "Production", the original's rules in docs/game/ai-tasks.md "Perma-jobs"): a player character whose task is idle or came from its list takes the
// first job of its list that is not blocked, so a higher job takes over as soon as it can run and a blocked one passes to the next. Meitou runs Operating machine (87, the
// operator below), Operating automatic machine (152: an operator that only hauls for it), Hauling to (124: a machine's output carried to the job's storage, CharacterTask.Haul; on an automatic machine, carried inputs into it), Engineer (125: ConstructionSystem builds the site its finder picks) and Turret guard (234: manning the job's turret as an operator). Blocked
// uses one test for starting and for going on.
public sealed partial class ProductionSystem
{
    /// <summary>How often (ticks) a character's job list is looked at, staggered by slot (engine choice; the original re-plans when its task ends or its commitment runs out).</summary>
    public const int JobCheckTicks = 10;

    void AddJobs(World world, AddJob add)
    {
        if (Site(world, add.Site) is not { } site || !MayStartWork(world, add, site)) return;
        if (add.Issuer is not null && add.Type is not (TaskType.OPERATE_MACHINERY or TaskType.OPERATE_AUTOMATIC_MACHINERY
            or TaskType.OPERATE_STORAGE or TaskType.USE_TURRET or TaskType.MAN_A_TURRET_PLAYER_JOB or TaskType.USE_TRAINING_DUMMY)) return;
        var table = world.Characters;
        foreach (var id in world.CommandCharacters(add, add.Characters))
            if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold && MayWork(world, cold, site)
                && PermaJobs.Add(cold, add.Type, new PermaJob(add.Type, add.Site))) sweep = true;
    }

    void AddOrderJobs(World world, AddOrderJob add)
    {
        if (add.Issuer is not null && add.Ordered is not (TaskType.FIRST_AID_ORDER or TaskType.FIRST_AID_ROBOT or TaskType.SPLINT_ORDER
            or TaskType.LOOT_TARGET or TaskType.MELEE_ATTACK or TaskType.FOCUSED_MELEE_ATTACK or TaskType.UNPROVOKED_FOCUSED_MELEE_ATTACK
            or TaskType.BODYGUARD or TaskType.STAY_CLOSE_TO_TARGET or TaskType.FIND_AND_RESCUE_IF_THERES_BEDS)) return;
        var table = world.Characters;
        var ordered = add.Ordered;
        // A first aid on a robot is the original's robot repair order (60), whose job is Robotics.
        if (ordered == TaskType.FIRST_AID_ORDER && table.TryResolveNext(add.Target, out int t) && table.Cold(t)?.Race is { IsRobot: true }) ordered = TaskType.FIRST_AID_ROBOT;
        // Follow (31) and Bodyguard (45) on a character become the jobs that follow it (docs/game/ai-tasks.md "Finders of the jobs": they act on the job's own target).
        bool onCharacter = ordered is TaskType.BODYGUARD or TaskType.STAY_CLOSE_TO_TARGET;
        foreach (var id in world.CommandCharacters(add, add.Characters))
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
            if (!onCharacter) PermaJobs.Add(cold, ordered, PermaJob.Untargeted(ordered));
            else if (id != add.Target && table.IsAlive(add.Target)) PermaJobs.Add(cold, ordered, PermaJob.OfCharacter(ordered, add.Target));
        }
    }

    void RemoveJobAt(World world, RemoveJob remove)
    {
        if (!MayEditJobs(world, remove, remove.Character)) return;
        var table = world.Characters;
        if (!table.TryResolveNext(remove.Character, out int slot) || table.Cold(slot) is not { } cold || (uint)remove.Index >= (uint)cold.Jobs.Count) return;
        cold.Jobs.RemoveAt(remove.Index);
        if (cold.JobIndex == remove.Index) End(table, slot);
        else if (cold.JobIndex > remove.Index) cold.JobIndex--;
    }

    void MoveJobTo(World world, MoveJob move)
    {
        if (!MayEditJobs(world, move, move.Character)) return;
        if (!world.Characters.TryResolveNext(move.Character, out int slot) || world.Characters.Cold(slot) is not { } cold) return;
        int count = cold.Jobs.Count;
        if ((uint)move.From >= (uint)count || (uint)move.To >= (uint)count || move.From == move.To) return;
        var job = cold.Jobs[move.From];
        cold.Jobs.RemoveAt(move.From);
        cold.Jobs.Insert(move.To, job);
        // The job the character is on keeps pointing at it.
        int k = cold.JobIndex;
        if (k == move.From) cold.JobIndex = move.To;
        else if (move.From < k && k <= move.To) cold.JobIndex--;
        else if (move.To <= k && k < move.From) cold.JobIndex++;
    }

    static bool MayEditJobs(World world, JobListCommand command, CharacterId character) => world.MayOrder(command, character)
        && (command.Issuer is null && command.JobsRevision == 0 || command.JobsRevision != 0 && world.JobsRevision(character) == command.JobsRevision);

    /// <summary>
    /// Once a tick (serial): characters due a look at their lists take the first job that can run, unless it is the one they are on; with none, an implicit job of the squad AI
    /// options (<see cref="ImplicitPick"/>).
    /// </summary>
    void PickJobs(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref table.Next[i];
            if (!n.Alive) continue;
            var cold = table.Cold(i)!;
            var task = (CharacterTask)n.Task;
            // An order replaced the job's task.
            if ((cold.JobIndex >= 0 || cold.Implicit is not null) && !JobTask(cold, task))
            {
                cold.JobIndex = -1;
                cold.Implicit = null;
                cold.HaulTo = null;
            }
            if (!cold.IsPlayer || (world.Tick + i) % JobCheckTicks != 0) continue;
            bool onJob = cold.JobIndex >= 0 || cold.Implicit is not null;
            // Idle as the medic job counts it: no task, or following or wandering.
            if (!onJob && task is not (CharacterTask.Idle or CharacterTask.Wander or CharacterTask.Follow)) continue;
            if (!Able(cold)) continue;
            int pick = -1;
            for (int k = 0; k < cold.Jobs.Count && pick < 0; k++)
                if (Runnable(world, i, cold, k)) pick = k;
            if (pick >= 0)
            {
                if (pick != cold.JobIndex) StartJob(world, i, pick);
                continue;
            }
            if (ImplicitPick(world, i, onJob ? cold.ResumeTask : task) is { } chosen)
            {
                string? current = chosen.Type == TaskType.DITCH_ALL_RESOURCES ? cold.HaulTo : cold.OperateSite;
                if (chosen.Type != cold.Implicit || chosen.Site != current) StartImplicit(world, i, chosen.Type, chosen.Site);
                continue;
            }
            if (onJob) End(table, i);
        }
    }

    /// <summary>How far (X/Z) from where it idles a character sits down on a chair (engine choice: the original's sitter looks in its home town only).</summary>
    public const float SeatRange = 600;

    /// <summary>
    /// The implicit job of a character whose list has none to run (docs/game/ai-tasks.md "Perma-jobs": the original's lowest keys, in this order), with the jobs switch on and not a
    /// slave. <see cref="SquadAi.Ditch"/>: carried items a storage takes go there (242; only with no queued orders). <see cref="SquadAi.Sleep"/>: an injured character lies in the bed
    /// the bed search finds (153, whose target finder it is, <b>Verified</b>; <see cref="Locks.FixtureRegistry.FindBed"/>) until healed (then 257 gets it up). <see cref="SquadAi.Sit"/>: a free
    /// chair (275). The one it is on is kept while it holds (a bed within <see cref="Locks.FixtureRegistry.BedSearchRange"/>). Distances are from where it idles, its squad leader when it
    /// follows, so a follower gets up when the leader walks off.
    /// </summary>
    (TaskType Type, string Site)? ImplicitPick(World world, int slot, CharacterTask idle)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (!world.MayUseAssets(cold, cold.Faction)) return null;
        var options = world.PlayerOf(table.IdOf(slot))?.SquadAi ?? SquadAi.None;
        if ((cold.Orders & CharacterOrders.Jobs) == 0 || cold.SlaveState != 0 || options == SquadAi.None) return null;
        var at = table.Next[slot].Position;
        if (idle == CharacterTask.Follow && cold.SquadId >= 0 && world.Squads.Find(cold.SquadId)?.Leader is { IsNone: false } leader && leader.Slot != slot && table.IsAlive(leader))
            at = table.Next[leader.Slot].Position;
        if ((options & SquadAi.Ditch) != 0)
        {
            if (cold.Implicit == TaskType.DITCH_ALL_RESOURCES && cold.HaulTo is { } keep) return (TaskType.DITCH_ALL_RESOURCES, keep);
            if (cold.OrderQueue.Count == 0 && cold.Inventory is { } inv
                && NearestSite(world, at, Settings.HaulRange, (_, m) => m.Data.IsStorage && m.Storage is not null && inv.All().Any(item => Takes(world, m, item)), cold.Faction) is { } store)
                return (TaskType.DITCH_ALL_RESOURCES, store);
        }
        if ((options & SquadAi.Sleep) != 0 && Injured(cold) && Beds is not null)
        {
            if (cold.Implicit == TaskType.GO_HOME_AND_GO_TO_BED && cold.OperateSite is { } bed && world.Fixtures.Find(bed) is { } kept
                && Gap(kept.Position, at) <= Locks.FixtureRegistry.BedSearchRange)
                return (TaskType.GO_HOME_AND_GO_TO_BED, bed);
            if (Beds.FindFor(world, slot) is { } free) return (TaskType.GO_HOME_AND_GO_TO_BED, free.Id);
        }
        if ((options & SquadAi.Sit) != 0)
        {
            if (cold.Implicit == TaskType.SIT_AROUND && cold.OperateSite is { } seat && Site(world, seat) is { } kept && Gap(kept.Position, at) <= SeatRange)
                return (TaskType.SIT_AROUND, seat);
            if (NearestSite(world, at, SeatRange, (s, m) => IsSeat(m) && Free(world, cold, s, m), cold.Faction) is { } free) return (TaskType.SIT_AROUND, free);
        }
        return null;
    }

    // Some player character has a Bodyguard or Staying close job, so the lists are looked at even with no machine (derived, looked for every 30 ticks; not saved or hashed).
    bool followJobs;

    void ScanFollowJobs(World world)
    {
        var table = world.Characters;
        followJobs = false;
        for (int i = 0; i < table.HighWater && !followJobs; i++)
            followJobs = table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, Jobs.Count: > 0 } cold && cold.Jobs.Exists(j => j.Type is TaskType.BODYGUARD or TaskType.STAY_CLOSE_TO_TARGET);
    }

    /// <summary>The training dummy's gain per second of the continuous entry (<b>Verified</b>, <c>UseableStuff</c> vfunc 0x4b0: <c>FUN_1408c64f0</c> with 2.0).</summary>
    public const float TrainingRate = 2;

    /// <summary>
    /// A user at a training dummy (<c>Task_UseTrainingDummy</c> slot 2 and <c>UseableStuff</c> vfunc 0x4b0, <b>Verified</b> by decompile): facing it, the building's <c>stat used</c> rises every
    /// tick through <see cref="XpService.Capped"/> at <see cref="TrainingRate"/>, up to the building's cap (<c>output rate</c>: MkI 6, MkII 11, MkIII 16, whose descriptions promise levels 5, 10 and 15,
    /// <b>Observed</b>; the field read as the cap is +0x1a4, not traced to the record). The gain also depends on the raw stat, not the injured one.
    /// </summary>
    void Train(ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m, float dt)
    {
        if (n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = MathF.Atan2(site.Position.X - n.Position.X, site.Position.Z - n.Position.Z);
        m.Operators++;
        cold.Operating = true;
        if (cold.Stats is { } stats && cold.Race is { } race) xp.Capped(stats, race, (Meitou.Data.Gameplay.Bodies.StatsEnumerated)m.Data.Stat, dt, TrainingRate, m.Data.OutputRate);
    }

    /// <summary>BF_CHAIR and BF_THRONE (17, 24; docs/game/ai-tasks.md "Town goals").</summary>
    static bool IsSeat(Machine m) => m.Data.Function is TownBuildings.ChairFunction or ThroneFunction;

    const int ThroneFunction = 24;

    /// <summary>Nobody else lies or sits on it.</summary>
    static bool Free(World world, CharacterCold cold, BuildSite site, Machine m) => OperatorsOf(world, site.Id) - (cold.OperateSite == site.Id ? 1 : 0) < m.Data.MaxOperators;

    /// <summary>A body part below full health (<b>engine choice</b>: the original's injury test is Unknown; its 257 is "get out of bed once healed").</summary>
    static bool Injured(CharacterCold cold) => cold.Medical is { Dead: false } medical && medical.Parts.Any(p => p.Fraction is >= 0 and < 1);

    /// <summary>The player's complete building nearest <paramref name="at"/> within <paramref name="range"/> whose machine passes <paramref name="ok"/> (ties: the lower site id).</summary>
    string? NearestSite(World world, Vector3 at, float range, Func<BuildSite, Machine, bool> ok, int faction)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var m in world.Machines.All)
        {
            if (Site(world, m.Site) is not { Complete: true } site || site.Owner != faction || world.Players.RequiresExplicitControl && !WorkSiteOpen(world, site)) continue;
            float gap = Gap(site.Position, at);
            if (gap > range || gap > bestGap || (gap == bestGap && string.CompareOrdinal(m.Site, best) > 0) || !ok(site, m)) continue;
            best = m.Site;
            bestGap = gap;
        }
        return best;
    }

    void StartImplicit(World world, int slot, TaskType type, string siteId)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var resume = cold.JobIndex >= 0 || cold.Implicit is not null ? cold.ResumeTask : (CharacterTask)table.Next[slot].Task;
        if (type == TaskType.GO_HOME_AND_GO_TO_BED)
        {
            // A bed fixture: the bed system walks it there and lays it in (task Sleep); the job keeps the bed's id.
            if (world.Fixtures.Find(siteId) is not { } bed || Beds is null) return;
            Beds.StartSleep(world, slot, bed);
            if (table.Next[slot].Task != (byte)CharacterTask.Sleep) return;
            cold.OperateSite = siteId;
        }
        else
        {
            var site = Site(world, siteId)!;
            var m = Ensure(world, site)!;
            if (type == TaskType.DITCH_ALL_RESOURCES) StartHaulTrip(world, slot, site, m);
            else Begin(world, slot, site, m);
        }
        cold.JobIndex = -1;
        cold.Implicit = type;
        cold.ResumeTask = resume;
    }

    /// <summary>A sitter or sleeper at its chair or bed: on the seat's origin facing its way, as the original's <c>Task_OperateMachine</c> (docs/game/ai-tasks.md "Town goals").</summary>
    static void Settle(ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m)
    {
        n.Position = site.Position;
        n.Yaw = TownBuildings.YawOf(site.Rotation);
        n.Velocity = default;
        m.Operators++;
        cold.Operating = true;
    }

    /// <summary>
    /// The square of how far (3D) from the character a job's target may be (<b>Verified</b>, <c>AI::isTargetInRangeForOrders</c> <c>FUN_140597790</c> in the default ROAM_FAR mode: the walk a job
    /// plans needs StateType 27 <c>isMovementAllowed</c>; about 6517 units). Farther, the job is skipped, so a character away from its base does not walk back for a job there.
    /// </summary>
    public const float JobRangeSq = 4.246733e7f;

    /// <summary>A job of the list that may run now: Meitou runs its type, the jobs switch is on (or the type ignores it), its target is within <see cref="JobRangeSq"/>, and it is not blocked.</summary>
    bool Runnable(World world, int slot, CharacterCold cold, int k)
    {
        var job = cold.Jobs[k];
        if (job.Type is TaskType.BODYGUARD or TaskType.STAY_CLOSE_TO_TARGET) return FollowedBy(world, slot, cold, k) is not null;
        if (!PermaJobs.Runs(job.Type) || ((cold.Orders & CharacterOrders.Jobs) == 0 && !PermaJobs.IgnoresSwitch(job.Type))) return false;
        var at = world.Characters.Next[slot].Position;
        // The engineer job ignores its target: its finder picks any site (the original's row 125 finder, docs/game/buildings-production.md "Builders and dismantle"); a pick out of range fails the job.
        if (job.Type == TaskType.JOB_BUILDER) return Construction?.EngineerSite(world, slot) is { } pick && Vector3.DistanceSquared(at, pick.Position) < JobRangeSq;
        if (job.Site is { } id && Site(world, id) is { } site && Vector3.DistanceSquared(at, site.Position) >= JobRangeSq) return false;
        return !Blocked(world, cold, job);
    }

    /// <summary>The task the job a character is on runs: building, delivering or fetching for the engineer job, following the job's character for Bodyguard and Staying close, else operating or hauling.</summary>
    static bool JobTask(CharacterCold cold, CharacterTask task)
    {
        var job = cold.JobIndex >= 0 && cold.JobIndex < cold.Jobs.Count ? cold.Jobs[cold.JobIndex] : null;
        if (job?.Type == TaskType.JOB_BUILDER) return task is CharacterTask.Build or CharacterTask.DeliverMaterials or CharacterTask.FetchMaterials;
        if (job?.Type is TaskType.BODYGUARD or TaskType.STAY_CLOSE_TO_TARGET) return task == CharacterTask.Follow && cold.FollowOrder == job.Character;
        return task is CharacterTask.Operate or CharacterTask.Haul or CharacterTask.Sleep;
    }

    /// <summary>
    /// The character a Bodyguard (45) or Staying close (31) job follows (the row has no finder: it acts on the job's own target, docs/game/ai-tasks.md "Finders of the jobs"): the job's character,
    /// found again from its saved handle after a load, alive and not the follower itself; for Staying close also standing (the row's state 7, <c>!isTargetDown</c>). Null: the job is blocked.
    /// </summary>
    static CharacterId? FollowedBy(World world, int slot, CharacterCold cold, int k)
    {
        var table = world.Characters;
        var job = cold.Jobs[k];
        var target = job.Character;
        if (target.IsNone && !job.Saved.IsNull)
        {
            for (int s = 0; s < table.HighWater && target.IsNone; s++)
                if (table.Next[s].Alive && table.Cold(s)?.Save?.Source.Handle == job.Saved) target = table.IdOf(s);
            if (!target.IsNone) cold.Jobs[k] = job with { Character = target };
        }
        if (target.IsNone || target.Slot == slot || !table.TryResolveNext(target, out int t)) return null;
        return job.Type == TaskType.STAY_CLOSE_TO_TARGET && table.Cold(t) is { Medical.Incapacitated: true } ? null : target;
    }

    /// <summary>
    /// Whether a job cannot run (the original skips such a job: an unmet start condition the planner cannot meet, or the target finder found nothing; docs/game/ai-tasks.md "Perma-jobs").
    /// Operating: the building is gone or not an operable machine, its operator places are taken by others, or it has no work (<see cref="HasWork"/>). Hauling to: the storage is gone, and
    /// the character carries nothing it takes and no machine within <see cref="ProductionSettings.HaulRange"/> has an output it takes. Automatic machines (152, and 124 on one): <see cref="AutoWork"/>.
    /// </summary>
    bool Blocked(World world, CharacterCold cold, PermaJob job)
    {
        if (job.Site is not { } id || Site(world, id) is not { Complete: true } site || !MayWork(world, cold, site) || Ensure(world, site) is not { } m) return true;
        switch (job.Type)
        {
            case TaskType.OPERATE_MACHINERY:
                if (!m.Data.Operable) return true;
                if (OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators) return true;
                return !HasWork(world, site, m, cold);
            case TaskType.MAN_A_TURRET_PLAYER_JOB:
                // Turret guard (234, the job a Shift order to man a turret becomes): the building is a turret no one else mans.
                if (m.Data.Function != Meitou.Data.World.ObjectRanges.TurretFunction) return true;
                return OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators;
            case TaskType.USE_TRAINING_DUMMY:
                // Training (97): a dummy with a place free for a character with an arm that works (the row's state 38; the rest of its conditions, 73, is Unknown). It goes on at the cap, as the original's has no end.
                if (!m.Data.IsTraining || OperatorsOf(world, id) - (cold.OperateSite == id ? 1 : 0) >= m.Data.MaxOperators) return true;
                return cold.Medical is null || !(cold.Medical.ArmWorks(left: true) || cold.Medical.ArmWorks(left: false));
            case TaskType.OPERATE_AUTOMATIC_MACHINERY:
                return !m.Data.IsAutomatic || !m.Data.IsProducer || !AutoWork(world, site, m, cold, fetch: true);
            case TaskType.OPERATE_STORAGE:
                // On an automatic machine (152's companion, the original's Task_FillMachine into it): what the character carries for its inputs.
                if (m.Data.IsAutomatic && m.Data.IsProducer) return !AutoWork(world, site, m, cold, fetch: false);
                if (!m.Data.IsStorage || m.Storage is null) return true;
                if (cold.Inventory is { } inv && inv.All().Any(i => Takes(world, m, i))) return false;
                return HaulSource(world, site, m) is null;
            default:
                return true;
        }
    }

    /// <summary>
    /// A machine an operator can work (the original's start conditions of task 87: output not full, inputs not empty, power; docs/game/ai-tasks.md): it has power, and a bench a queued
    /// research or craft; a farm a dead crop to clear, a ripe one to harvest or a growing one with water (tended); inputs that hold something or that the operator can fill
    /// (it carries the item or, with <see cref="ProductionSettings.AutoHaul"/>, <see cref="NearestSource"/> finds it); and room in the output or, with AutoHaul, a storage that takes it.
    /// </summary>
    bool HasWork(World world, BuildSite site, Machine m, CharacterCold cold)
    {
        if (WorkFactor(world, m) <= 0) return false;
        if (m.Data.IsResearch) return ResearchFor(world, m).Queue.Count > 0;
        if (m.Farm is { } f)
        {
            if (f.Death >= m.Data.Farm!.DeathThreshold) return true;
            if (f.Growth >= 1) return f.Harvested < f.Plants.Length && Room(world, site, m);
            return Supplied(world, site, m, cold);
        }
        if (m.Craft is { Queue.Count: 0 }) return false;
        return Supplied(world, site, m, cold) && (m.Craft is not null || Room(world, site, m));
    }

    /// <summary>Every input holds something or the operator can fill it: it carries the item or, with AutoHaul, <see cref="NearestSource"/> finds it.</summary>
    bool Supplied(World world, BuildSite site, Machine m, CharacterCold cold)
    {
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0 || input.Level + input.Count > 0) continue;
            if (cold.Inventory is { } inv && Carried(inv, input.Item).Any()) continue;
            if (Settings.AutoHaul && NearestSource(world, site, input.Item, cold) is not null) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Haul work at an automatic machine (engine reading of 152, whose goal is "no empty inputs", and of 124 on it): the character is on a trip for it, or carries an item an input has room for;
    /// with <paramref name="fetch"/> also an empty input a storage holds the item of, a full output a storage takes, or the machine's output carried that a storage takes.
    /// </summary>
    bool AutoWork(World world, BuildSite site, Machine m, CharacterCold cold, bool fetch)
    {
        if (cold.Inventory is not { } inv) return false;
        if (fetch && cold.HaulSite is not null && cold.OperateSite == site.Id) return true;
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0) continue;
            if (input.Count < input.Recipe.Capacity && Carried(inv, input.Item).Any()) return true;
            if (fetch && input.Count == 0 && NearestSource(world, site, input.Item, cold) is not null) return true;
        }
        if (!fetch || m.Output is not { } output || items.Create(output.Recipe.Item, 1) is not { } one) return false;
        if (!output.Full && !Carried(inv, one.Record).Any()) return false;
        return NearestStorage(world, site, s => s.Data.Accepts(one.Record, one.Type) && Fits(s.Storage!, one)) is not null;
    }

    /// <summary>The output has room, or with AutoHaul a storage takes it.</summary>
    bool Room(World world, BuildSite site, Machine m) =>
        m.Output is { } o && (!o.Full || Settings.AutoHaul && items.Create(o.Recipe.Item, 1) is { } one && NearestStorage(world, site, s => s.Data.Accepts(one.Record, one.Type) && Fits(s.Storage!, one)) is not null);

    /// <summary>A carried item a "Hauling to" job takes to its storage: a loose stack of something a player machine makes (not the character's own kit), that the storage takes and has room for.</summary>
    bool Takes(World world, Machine store, Items.ItemInstance item) =>
        item.Section is "main" or "backpack_content" && !item.LockedOn && item.Contents is null && store.Data.Accepts(item.Record, item.Type) && Fits(store.Storage!, item)
        && world.Machines.All.Any(s => !s.Data.IsStorage && Site(world, s.Site) is not { LevelMachine: true } && s.Output?.Recipe.Item == item.Record);

    /// <summary>The player's complete machine (not a storage) nearest a storage within <see cref="ProductionSettings.HaulRange"/> whose output holds items the storage takes (ties: the lower site id).</summary>
    string? HaulSource(World world, BuildSite storage, Machine store)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var s in world.Machines.All)
        {
            if (s.Data.IsStorage || s.Output is not { Count: > 0 } output || Site(world, s.Site) is not { Complete: true } site || site.Owner != storage.Owner
                || world.Players.RequiresExplicitControl && !WorkSiteOpen(world, site)) continue;
            float gap = Gap(site.Position, storage.Position);
            if (gap > Settings.HaulRange || gap > bestGap || (gap == bestGap && string.CompareOrdinal(s.Site, best) > 0)) continue;
            if (items.Create(output.Recipe.Item, 1) is not { } one || !store.Data.Accepts(one.Record, one.Type) || !Fits(store.Storage!, one)) continue;
            best = s.Site;
            bestGap = gap;
        }
        return best;
    }

    void StartJob(World world, int slot, int k)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var job = cold.Jobs[k];
        if (cold.JobIndex < 0) cold.ResumeTask = (CharacterTask)table.Next[slot].Task;
        if (job.Type == TaskType.JOB_BUILDER)
        {
            // From operating or hauling the character goes back to what it was doing before the list took it.
            var resume = cold.ResumeTask;
            End(table, slot);
            Construction!.StartBuilding(world, slot, Construction.EngineerSite(world, slot)!, resume);
            cold.JobIndex = k;
            return;
        }
        if (job.Type is TaskType.BODYGUARD or TaskType.STAY_CLOSE_TO_TARGET)
        {
            if (FollowedBy(world, slot, cold, k) is not { } target) return;
            // From operating or hauling the character goes back to what it was doing before the list took it.
            var back = cold.ResumeTask;
            End(table, slot);
            cold.ResumeTask = back;
            MovementSystem.Follow(world, new FollowOrder([table.IdOf(slot)], target) { Tick = world.Tick });
            cold.JobIndex = k;
            return;
        }
        var site = Site(world, job.Site!)!;
        var m = Ensure(world, site)!;
        if (job.Type is TaskType.OPERATE_MACHINERY or TaskType.MAN_A_TURRET_PLAYER_JOB or TaskType.USE_TRAINING_DUMMY or TaskType.OPERATE_AUTOMATIC_MACHINERY || !m.Data.IsStorage)
        {
            if (cold.OperateSite != site.Id) Begin(world, slot, site, m);
        }
        else StartHaulTrip(world, slot, site, m);
        cold.JobIndex = k;
    }

    /// <summary>A <see cref="CharacterTask.Haul"/> trip to a storage: what the character already carries for it goes there first; else it fetches a machine's output.</summary>
    void StartHaulTrip(World world, int slot, BuildSite storage, Machine store)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (!MayWork(world, cold, storage)) return;
        End(table, slot);
        ref var n = ref table.Next[slot];
        MovementSystem.Stop(table, slot);
        n.Task = (byte)CharacterTask.Haul;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        bool carries = cold.Inventory is { } inv && inv.All().Any(i => Takes(world, store, i));
        cold.HaulSite = carries ? storage.Id : HaulSource(world, storage, store);
        cold.HaulTo = storage.Id;
        sweep = true;
    }

    /// <summary>A hauler at the end of a leg (serial): at the source it takes the output, then makes for the storage; at the storage it puts in what the storage takes and the job's trip ends.</summary>
    void HaulStep(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        if (cold.HaulTo is not { } storeId || cold.HaulSite is not { } at
            || Site(world, storeId) is not { Complete: true } storeSite || !MayWork(world, cold, storeSite) || world.Machines.Find(storeId) is not { Storage: not null } store
            || Site(world, at) is not { Complete: true } atSite || !MayWork(world, cold, atSite) || world.Machines.Find(at) is not { } there || !Able(cold) || cold.Inventory is not { } inv)
        {
            End(table, slot);
            return;
        }
        if (!AtSite(n, atSite, there)) return;
        if (at != storeId)
        {
            if (there.Output is { Count: > 0 } output && items.Create(output.Recipe.Item, output.Count) is { } made && store.Data.Accepts(made.Record, made.Type))
            {
                int moved = Placed(inv, made);
                output.Count -= moved;
                world.Machines.Touch();
            }
            if (inv.All().Any(i => Takes(world, store, i))) cold.HaulSite = storeId;
            else End(table, slot);
            return;
        }
        foreach (var item in inv.All().Where(i => Takes(world, store, i)).ToList())
        {
            inv.Remove(item);
            if (store.Storage!.Add(item)) continue;
            if (!inv.Add(item)) world.GroundItems.Drop(item, store.Position, 0);
            break;
        }
        world.Machines.Touch();
        End(table, slot);
    }
}
