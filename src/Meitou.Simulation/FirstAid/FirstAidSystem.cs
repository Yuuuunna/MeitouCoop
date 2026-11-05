using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.FirstAid;

/// <summary>
/// First aid and resting in play (docs/simulation.md "Bodies", docs/game/character-stats.md "First aid, medkits and robot repair" and "Sleep, beds and resting").
/// <list type="bullet">
/// <item><b>Treat</b> (<see cref="CharacterTask.Treat"/>): <see cref="TreatOrder"/> (the player), the medic job and the NPC rule start it. <c>Schedule</c> walks the medic to the patient
/// (the pursuit system's pattern: a path at free speed, asked again when the patient has moved); <c>Act</c> (own slot, read-only on the patient) emits one effect per tick while the medic
/// is in reach, awake, not fighting and still, and the patient is still or down; <c>Apply</c> (serial commit, in target / source order) picks the kit, bandages the patient, drains the kit
/// and trains the medic. So nothing on another character is written in a parallel phase.</item>
/// <item><b>Rate</b> (<b>Observed</b>, character-stats.md): <c>lerp(1, 15, min(skill, kit quality) x 0.01) x medic speed mult</c> (x <c>robot medic speed mult</c> for a robot kit) per unit of <c>dt</c> on the first part with room (the next only once it fills), after any open wound is closed;
/// the kit's <c>charges</c> fall by <c>lerp(medkit drain 99, medkit drain 1, (100 - skill) / 100) x dt</c> per wound step or part bandaged (bandaging drains only the player's kits; character-stats.md "First aid update"). The skill is the medic's <c>medic</c> (first aid kit) or <c>robotics</c> (robot kit) times
/// the stat multiplier of injuries and hunger. <c>dt</c> is <b>game seconds</b> (<b>engine choice</b>, the unit is <b>Unknown</b>).</item>
/// <item><b>Auto</b> (<c>SlowWorld</c>, every <see cref="FirstAidSettings.AutoEveryTicks"/> ticks, serial in slot order): an idle, wandering or following NPC looks for a patient with the original's
/// Medic finder (<see cref="FindPatient"/>, FirstAidSystem.Finder.cs), as the player's characters do for their medic, robotics and splint jobs (<see cref="RunJobs"/>).</item>
/// <item><b>Rest</b> (<see cref="CharacterTask.Rest"/>): <see cref="RestOrder"/>; <see cref="BodySystem"/> reads the task as <see cref="MedicalContext.Resting"/>. Lying in a bed is
/// <see cref="Locks.BedSystem"/>'s (<see cref="CharacterTask.Sleep"/>); <see cref="BodySystem"/> gives it the bed's healing.</item>
/// </list>
/// Placed after the movement system (a later move order replaces a treatment, since it sets the task) and before the body system.
/// </summary>
public sealed partial class FirstAidSystem(GameConstants constants, BodyOptions options, ItemFactory items, FirstAidSettings? settings = null) : ITickSystem
{
    /// <summary>The effect kind: one tick of treatment from the source (medic) on the target (patient).</summary>
    public const int EffectTreat = 1;
    /// <summary>Untreated damage (HP) under which a treatment in progress counts as finished.</summary>
    const float Done = 1e-3f;
    /// <summary>The kit quality closing a wound without a kit (<c>FUN_14064d800</c>).</summary>
    const float KitlessQuality = 5;

    public FirstAidSettings Settings { get; } = settings ?? new FirstAidSettings();
    public GameConstants Constants { get; } = constants;
    public ItemFactory Items { get; } = items;
    /// <summary>The relation event of bandaging an unconscious patient of another faction (<see cref="FactionDeeds.FirstAid"/>); null: none.</summary>
    public FactionDeeds? Deeds { get; set; }
    readonly XpService xp = new(constants, options);
    readonly BodyOptions bodyOptions = options;

    // ------------------------------------------------------------------ orders

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        foreach (var command in commands)
        {
            switch (command)
            {
                case TreatOrder order:
                    if (!table.TryResolveNext(order.Patient, out int patient) || table.Cold(patient)!.Medical is not { Dead: false }) break;
                    foreach (var id in world.CommandCharacters(order, order.Medics))
                        if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { Race: not null, Stats: not null } cold && CanAct(cold) && AdmittedPatient(world, command, slot, patient))
                        {
                            if (FindKit(world, slot, cold, ItemInfo.FirstAidKitFunction) is null && FindKit(world, slot, cold, ItemInfo.RobotKitFunction) is null)
                                world.Bark(id, "I don't have a medkit.");   // refused before the medic moves
                            else Begin(world, slot, order.Patient, CharacterTask.Idle);
                        }
                    break;
                case SplintOrder splint:
                    if (!table.TryResolveNext(splint.Patient, out int splinted) || table.Cold(splinted)!.Medical is not { Dead: false }) break;
                    foreach (var id in world.CommandCharacters(splint, splint.Medics))
                        if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { Race: not null, Stats: not null } cold && CanAct(cold) && AdmittedPatient(world, command, slot, splinted))
                        {
                            if (OwnKit(cold, ItemInfo.SplintKitFunction) is null) world.Bark(id, "I don't have a splint kit.");   // the medic's own kit only (the original's kit search)
                            else Begin(world, slot, splint.Patient, CharacterTask.Idle, splint: true);
                        }
                    break;
                case RestOrder rest:
                    foreach (var id in world.CommandCharacters(rest, rest.Characters))
                    {
                        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold || !CanAct(cold)) continue;
                        MovementSystem.Stop(table, slot);
                        table.Next[slot].Task = (byte)CharacterTask.Rest;
                        cold.TreatTarget = CharacterId.None;
                        cold.TreatSplint = false;
                        cold.ResumeTask = CharacterTask.Idle;
                        RememberActivity(world, slot);
                    }
                    break;
                case MedicJobCommand job:
                {
                    var taken = job.Job ? null : Treated(table);
                    foreach (var id in world.CommandCharacters(job, job.Characters))
                    {
                        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
                        if (taken is null)
                        {
                            Production.PermaJobs.Add(cold, TaskType.JOB_MEDIC, Production.PermaJob.Untargeted(TaskType.JOB_MEDIC));
                            RememberJobs(world, slot);
                        }
                        else HealSomeone(world, slot, taken, robotics: false);
                    }
                    break;
                }
            }
        }
    }


    /// <summary>A character that can be ordered to treat or rest: it has a medical state, is awake and is not in a fight.</summary>
    static bool CanAct(CharacterCold cold) => cold.Medical is { Incapacitated: false } && !cold.InCombat && cold.CarriedBy.IsNone;

    /// <summary>Starts a treatment (serial phases): the medic stands, then walks to the patient. <paramref name="resume"/> is the task to go back to afterwards (kept if already treating).</summary>
    void Begin(World world, int slot, CharacterId patient, CharacterTask resume, bool splint = false)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (cold.TreatTarget != patient) Say(world, slot, patient, HealingOtherStart, BeingHealedStart);
        ref var n = ref table.Next[slot];
        var keep = n.Task == (byte)CharacterTask.Treat ? cold.ResumeTask : resume;
        MovementSystem.Stop(table, slot);
        n.Task = (byte)CharacterTask.Treat;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.TreatTarget = patient;
        cold.TreatSplint = splint;
        cold.ResumeTask = keep;
        cold.PathFailed = false;   // a failure from before is not this walk's
        RememberActivity(world, slot);
    }

    /// <summary>Dialogue events 18-22 (EV_HEALING_OTHER_START ... EV_FIRSTAID_KIT_EMPTY).</summary>
    const int HealingOtherStart = 18, BeingHealedStart = 19, HealingOtherFinished = 20, BeingHealedFinished = 21, KitEmpty = 22;

    /// <summary>
    /// The treatment's dialogue events (<c>Task_FirstAid</c> start and finish, <c>FUN_14034ecf0</c> / <c>FUN_1403563a0</c>): the medic fires <paramref name="medicEvent"/> at the
    /// patient and a patient that is awake fires <paramref name="patientEvent"/> back (-1: none); nothing for a medic treating itself. Said from the next tick.
    /// </summary>
    static void Say(World world, int medic, CharacterId patient, int medicEvent, int patientEvent = -1)
    {
        var table = world.Characters;
        var id = table.IdOf(medic);
        if (patient == id || patient.IsNone) return;
        world.Defer(new Dialogue.DialogueEvent(id, patient, medicEvent) { Tick = world.Tick + 1 });
        if (patientEvent >= 0 && table.TryResolveNext(patient, out int p) && table.Cold(p)?.Medical is { Incapacitated: false })
            world.Defer(new Dialogue.DialogueEvent(patient, id, patientEvent) { Tick = world.Tick + 1 });
    }

    /// <summary>Ends a treatment (serial phases): back to the task before it, path dropped.</summary>
    static void End(CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        if (n.Task == (byte)CharacterTask.Treat)
        {
            MovementSystem.Stop(table, slot);
            n.Task = (byte)cold.ResumeTask;
            n.TaskTime = 0;
        }
        cold.TreatTarget = CharacterId.None;
        cold.TreatSplint = false;
        cold.ResumeTask = CharacterTask.Idle;
    }

    /// <summary>
    /// Where a medic treats <paramref name="patient"/> from: a caged one's cage use node (in front of its door, <see cref="FixtureRegistry.ExitSpot"/>; the medic cannot stand in
    /// the cage), else where the patient is.
    /// </summary>
    static Vector3 TreatSpot(World world, CharacterCold patient, in CharacterHot at) =>
        patient is { IsImprisoned: true, InWhat: { } id } && world.Fixtures.Find(id) is { Kind: FixtureKind.Cage } cage && FixtureRegistry.ExitSpot(cage) is { } door
            ? door
            : at.Position;

    /// <summary>In reach of the treatment spot: within <see cref="FirstAidSettings.Reach"/> on X/Z and <see cref="FirstAidSettings.ReachHeight"/> in height (on its floor).</summary>
    bool InReach(in CharacterHot medic, Vector3 spot)
    {
        float dx = medic.Position.X - spot.X, dz = medic.Position.Z - spot.Z;
        return dx * dx + dz * dz <= Settings.Reach * Settings.Reach && MathF.Abs(medic.Position.Y - spot.Y) <= Settings.ReachHeight;
    }

    // ------------------------------------------------------------------ walking to the patient

    public void Schedule(World world)
    {
        PruneAuthority(world);
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || n.Task != (byte)CharacterTask.Treat) continue;
            var cold = table.Cold(i)!;
            if (!ActivityAllowed(world, i) || !CanAct(cold)
                || !table.TryResolveNext(cold.TreatTarget, out int t) || table.Cold(t)!.Medical is not { Dead: false })
            {
                End(table, i);
                continue;
            }
            const ushort Path = (ushort)MoveFlags.AnyPath;
            var spot = TreatSpot(world, table.Cold(t)!, next[t]);
            if (t == i || InReach(next[i], spot))
            {
                // In reach (or treating itself): stand still and let the Act step work.
                if ((n.Flags & Path) != 0)
                {
                    n.Flags = (ushort)(n.Flags & ~Path);
                    n.PathCursor = 0;
                    cold.PathRequest = 0;
                }
                n.TaskTime = 0;
                continue;
            }
            n.TaskTime -= dt;
            var goal = new Vector2(spot.X, spot.Z);
            bool asking = cold.NavigationWaiting || (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
            // No way to the patient: no path, or one that ends short of the asked goal (the walkability answers an unreachable goal with the way to the reachable point nearest it). The
            // original's task walk reports it (notifyPathImpossible): the patient goes on the medic's path failures for 20 s, which the finder skips, and the treatment ends. Without it
            // a medic asked for the same hopeless path every half second (issue #124: searches run to the iteration limit, ~280 MB/s of garbage).
            if (!asking && (cold.PathFailed || (n.Flags & (ushort)MoveFlags.HasPath) != 0 && cold.Path.Length > 0 && EndsShort(ReachedEnd(cold, n), n.Goal, spot.Y)))
            {
                cold.PathFailed = false;
                var target = table.IdOf(t);
                var failures = cold.PathFailures ??= [];
                failures.RemoveAll(f => f.Target == target || f.Until <= world.Tick);
                failures.Add((target, world.Tick + (long)MathF.Ceiling(PursuitSystem.PathFailureSeconds / world.TickSeconds)));
                End(table, i);
                continue;
            }
            bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0 || Vector2.DistanceSquared(goal, n.Goal) > PursuitSystem.RepathDistance * PursuitSystem.RepathDistance;
            if (asking || !stale || n.TaskTime > 0) continue;
            n.Goal = goal;
            (cold.GoalHeight, cold.GoalHeightAt) = (spot.Y, goal);   // walk to the treatment spot's floor
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = PursuitSystem.RepathSeconds;
        }
    }

    /// <summary>The known navigation endpoint, excluding a provisional route's straight extension to the goal.</summary>
    static Vector3 ReachedEnd(CharacterCold cold, in CharacterHot n) =>
        cold.PathExact >= cold.Path.Length ? cold.Path[^1] : cold.PathExact > 0 ? cold.Path[cold.PathExact - 1] : n.Position;

    /// <summary>A known endpoint farther than the reach from the goal on X/Z, or off the treatment spot's floor: the patient cannot be got to.</summary>
    bool EndsShort(Vector3 end, Vector2 goal, float spotHeight) =>
        Vector2.Distance(new Vector2(end.X, end.Z), goal) > Settings.Reach || MathF.Abs(end.Y - spotHeight) > Settings.ReachHeight;

    // ------------------------------------------------------------------ the treatment

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float still2 = Settings.StillSpeed * Settings.StillSpeed;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || next[i].Task != (byte)CharacterTask.Treat) continue;
            var cold = table.Cold(i)!;
            if (cold.Medical is not { Incapacitated: false } medical || cold.InCombat || medical.Aggregates().BothArmsDisabled || cold.StaggeredUntil > world.Tick) continue;
            var target = cold.TreatTarget;
            if (!table.IsAlive(target)) continue;
            int t = target.Slot;
            if (table.Cold(t) is not { Medical: { Dead: false } patient }) continue;
            if (Speed2(prev[i].Velocity) > still2) continue;
            if (t != i)
            {
                if (!InReach(prev[i], TreatSpot(world, table.Cold(t)!, prev[t]))) continue;
                if (!patient.Incapacitated && Speed2(prev[t].Velocity) > still2) continue;
            }
            effects.Emit(i, t, EffectTreat);
        }
    }

    static float Speed2(Vector3 v) => v.X * v.X + v.Z * v.Z;

    /// <summary>Serial, in (patient, medic) order: the kit, the bandaging, the drain and the medic's XP. Ends the treatment when nothing is left to bandage or no kit fits.</summary>
    public void Apply(World world, in Effect effect)
    {
        if (effect.Kind != EffectTreat) return;
        var table = world.Characters;
        int m = effect.Source, p = effect.Target;
        if (!table.Next[m].Alive || table.Next[m].Task != (byte)CharacterTask.Treat) return;
        var medicCold = table.Cold(m)!;
        if (!table.TryResolveNext(medicCold.TreatTarget, out int t) || t != p) return;
        var patientCold = table.Cold(p)!;
        if (!ActivityAllowed(world, m) || !CanAct(medicCold) || (m != p && !InReach(table.Next[m], TreatSpot(world, patientCold, table.Next[p]))))
        {
            End(table, m);
            return;
        }
        if (medicCold is not { Medical: { Incapacitated: false } medical, Stats: { } stats, Race: { } medicRace } || patientCold is not { Medical: { Dead: false } body, Race: { } race })
        {
            End(table, m);
            return;
        }
        if (medicCold.TreatSplint)
        {
            Splint(world, m, medicCold, medical, stats, medicRace, body, race);
            return;
        }
        float dt = world.TickSeconds;
        // The skill and trained stat: medic with a first aid kit, robotics with a robot repair kit; treating oneself x0.8 (FUN_14033c5a0, Task_FirstAid's kit phase).
        StatsEnumerated StatOf(bool robotKit) => robotKit ? StatsEnumerated.Robotics : StatsEnumerated.Medic;
        float SkillOf(bool robotKit) => stats[StatOf(robotKit)] * medical.StatMultiplier(StatOf(robotKit)) * (m == p ? 0.8f : 1);
        // A medic not of the player's treats an imprisoned patient without a kit (Task_FirstAid FUN_1403563a0 skips the kit search; also for a chained slave of the medic's faction, FUN_14032e2a0).
        bool playerMedic = medicCold.IsPlayer || world.Players.ControlOf(table.IdOf(m)) is not null;
        bool kitless = !playerMedic && (patientCold.IsImprisoned || OwnedChained(medicCold, patientCold));
        var ctx = new MedicalContext(Constants, bodyOptions, race);
        // Open wounds are closed first, newest first, with a kit matching the patient's race (none: quality 5); nothing is bandaged meanwhile (FUN_14064d800). This drain is not gated.
        if (body.Wounds.Count > 0)
        {
            var woundKit = kitless ? null : FindKit(world, m, medicCold, FunctionOf(race.IsRobot));
            if (woundKit is null && !kitless)
            {
                Say(world, m, medicCold.TreatTarget, KitEmpty);
                End(table, m);
                return;
            }
            float woundSkill = SkillOf(race.IsRobot && !kitless);
            body.CloseWound(dt, woundSkill, woundKit is { } wk ? Items.Info(wk.Item.Record)?.Quality ?? 0 : KitlessQuality, Constants);
            if (woundKit is { } used) Drain(used, woundSkill, dt, race.IsRobot, 1);
            medicCold.UseSkill(StatOf(race.IsRobot && !kitless), world.Tick);
            xp.Medic(stats, race, medicRace, dt, StatOf(race.IsRobot && !kitless));
            return;
        }
        if (kitless)
        {
            // Without a kit every part takes 1 HP a second, organic or robotic alike, and nothing drains (FUN_140649a00).
            // Healing converts bandage to flesh; ignore the same float dust as treatment with a kit.
            if (body.BandageRoom(race, false) + body.BandageRoom(race, true) <= Done || body.Bandage(dt, null, race, out _) <= 0)
            {
                Say(world, m, medicCold.TreatTarget, HealingOtherFinished, BeingHealedFinished);
                patientCold.StuckBolts = null;
                End(table, m);
                return;
            }
            medicCold.UseSkill(StatsEnumerated.Medic, world.Tick);
            xp.Medic(stats, race, medicRace, dt, StatsEnumerated.Medic);
            Deeds?.FirstAid(world, m, p, dt);
            return;
        }
        // Ordinary kits first; a robotic part is treated once the organic ones are done. Treatment runs until the last bit is bandaged (the start threshold is MedicalState.FirstAidThreshold).
        bool robot = body.BandageRoom(race, false) <= Done;
        if (robot && body.BandageRoom(race, true) <= Done)
        {
            Say(world, m, medicCold.TreatTarget, HealingOtherFinished, BeingHealedFinished);
            patientCold.StuckBolts = null;   // the bolts go when first aid has nothing left to do (FUN_14064d800 -> FUN_14052bc10)
            End(table, m);   // nothing left to bandage
            return;
        }
        if (FindKit(world, m, medicCold, FunctionOf(robot)) is not { } kit)
        {
            Say(world, m, medicCold.TreatTarget, KitEmpty);   // Task_FirstAidRig's empty kit (FUN_14034ef20)
            End(table, m);   // no kit of the right kind within reach
            return;
        }
        float skill = SkillOf(robot);
        float quality = Items.Info(kit.Item.Record)?.Quality ?? 0;
        float bandaged = body.Treat(dt, skill, quality, robot, ctx, out int touched);
        if (bandaged <= 0)
        {
            End(table, m);
            return;
        }
        if (playerMedic) Drain(kit, skill, dt, robot, touched);   // once per part bandaged; only the player's medics use up kits here (FUN_140649a00: faction +0x250)
        medicCold.UseSkill(StatOf(robot), world.Tick);
        xp.Medic(stats, race, medicRace, dt, StatOf(robot));
        // An unconscious patient's faction is grateful to the medic's (engine choice: every update that bandages; the original's update fires it only when its loop got past the wounds).
        Deeds?.FirstAid(world, m, p, dt);
    }

    /// <summary>
    /// One update of a splint (task "Splinting", <c>FUN_140649d50</c>, docs/game/character-stats.md "Splints"): the medic's <c>medic</c> skill and the kit's quality set the rate and the cap;
    /// the kit drains as first aid's only when a part took rig; medic XP every update. Ends when no part is left to splint or no splint kit is in reach.
    /// </summary>
    void Splint(World world, int m, CharacterCold medicCold, MedicalState medical, CharacterStats stats, RaceData medicRace, MedicalState body, RaceData race)
    {
        var table = world.Characters;
        if (OwnKit(medicCold, ItemInfo.SplintKitFunction) is not { } kit)
        {
            Say(world, m, medicCold.TreatTarget, KitEmpty);   // the rig task's kit search finds none (FUN_14034ef20, event 22)
            End(table, m);
            return;
        }
        var item = kit.Item;
        float dt = world.TickSeconds;
        float skill = stats[StatsEnumerated.Medic] * medical.StatMultiplier(StatsEnumerated.Medic);
        float quality = Items.Info(item.Record)?.Quality ?? 0;
        float added = body.Splint(dt, skill, quality, new MedicalContext(Constants, bodyOptions, race));
        if (added <= 0)
        {
            End(table, m);   // nothing left to splint
            return;
        }
        Drain(kit, skill, dt, false, 1);
        medicCold.UseSkill(StatsEnumerated.Medic, world.Tick);
        xp.Medic(stats, race, medicRace, dt, StatsEnumerated.Medic);
    }

    // ------------------------------------------------------------------ kits

    /// <summary>Uses <paramref name="times"/> drains of a kit (<see cref="MedicalState.KitDrain"/>); a used-up kit is gone.</summary>
    void Drain((ItemInstance Item, Inventory Owner) kit, float skill, float dt, bool robotKit, int times)
    {
        kit.Item.Charges -= MedicalState.KitDrain(Constants, skill, dt, robotKit) * times;
        if (kit.Item.Charges <= 0) kit.Owner.RemoveOne(kit.Item);
    }

    /// <summary>The best kit of a function in an inventory (and its backpack): the highest record quality with charges left; the first of equals.</summary>
    ItemInstance? Best(Inventory? inventory, int function)
    {
        if (inventory is null) return null;
        ItemInstance? best = null;
        float bestQuality = float.MinValue;
        foreach (var item in inventory.All())
        {
            if (item.Type != FcsRecordType.ITEM || item.ItemFunction != function || item.Charges <= 0 || item.Quantity <= 0) continue;
            float q = Items.Info(item.Record)?.Quality ?? 0;
            if (q > bestQuality)
            {
                best = item;
                bestQuality = q;
            }
        }
        return best;
    }

    static int FunctionOf(bool robot) => robot ? ItemInfo.RobotKitFunction : ItemInfo.FirstAidKitFunction;

    /// <summary>
    /// The kit a medic uses for splints, from its own inventory only (the original's kit search, <c>FUN_14033c740</c> at vtable +0x38 of the splint and first aid tasks, <b>Verified</b>):
    /// the main grid, else the worn backpack's contents; the item of the function with the fewest charges above zero (<see cref="Fewest"/>). Squad mates' kits are not searched.
    /// </summary>
    static (ItemInstance Item, Inventory Owner)? OwnKit(CharacterCold cold, int function)
    {
        if (cold.Inventory is not { } own) return null;
        if (Fewest(own.Items, function) is { } main) return (main, own);
        return Fewest(own.Backpack?.Contents?.Items, function) is { } worn ? (worn, own) : null;
    }

    /// <summary>
    /// The item of the function with charges above zero and the fewest of them, the first of equals (<c>FUN_140746480</c>, <b>Verified</b>: the search starts at 99999, so an item with
    /// 99999 charges or more never qualifies). Null when none.
    /// </summary>
    static ItemInstance? Fewest(IEnumerable<ItemInstance>? items, int function)
    {
        if (items is null) return null;
        ItemInstance? best = null;
        foreach (var item in items)
        {
            if (item.Type != FcsRecordType.ITEM || item.ItemFunction != function || item.Charges <= 0 || item.Charges >= 99999 || item.Quantity <= 0) continue;
            if (best is null || item.Charges < best.Charges) best = item;
        }
        return best;
    }

    /// <summary>The medic's own best kit of an <c>item function</c>, else a squad mate's within <see cref="FirstAidSettings.KitReach"/> (an engine choice).</summary>
    (ItemInstance Item, Inventory Owner)? FindKit(World world, int slot, CharacterCold cold, int function)
    {
        if (cold.Inventory is { } own && Best(own, function) is { } mine) return (mine, own);
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return null;
        var table = world.Characters;
        var here = table.Next[slot].Position;
        float reach2 = Settings.KitReach * Settings.KitReach;
        foreach (var m in squad.Members)
        {
            if (m.Slot == slot || !MayShareKit(world, slot, m) || !table.TryResolveNext(m, out int s) || table.Cold(s) is not { Inventory: { } mates, Medical: { Dead: false } }) continue;
            var d = table.Next[s].Position - here;
            if (d.X * d.X + d.Z * d.Z > reach2) continue;
            if (Best(mates, function) is { } found) return (found, mates);
        }
        return null;
    }

    // ------------------------------------------------------------------ treating by themselves

    public void SlowWorld(World world)
    {
        if (Settings.AutoEveryTicks <= 0 || world.Tick % Settings.AutoEveryTicks != 0) return;
        var table = world.Characters;
        var next = table.Next;
        var taken = Treated(table);
        BeginSweep(world);
        try
        {
            for (int i = 0; i < table.HighWater; i++)
            {
                if (!next[i].Alive) continue;
                var cold = table.Cold(i)!;
                if (cold.IsPlayer)
                {
                    if (cold.Jobs.Count > 0 && JobsAllowed(world, i)) RunJobs(world, i, cold, taken);
                    continue;
                }
                if (!Settings.AutoNpcs) continue;
                // Idle: no task, following, wandering or an implicit job.
                bool idle = next[i].Task is (byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow || cold.Implicit is not null;
                if (idle) HealSomeone(world, i, taken, robotics: false);
            }
        }
        finally { EndSweep(); }
    }

    /// <summary>
    /// A player character's first aid jobs, in list order (docs/game/ai-tasks.md "Perma-jobs"): the first of Medic 58, Robotics 57 and Splint Rigging 250 that may start
    /// (<see cref="Production.PermaJobs.FreeFor"/>; all three run with the jobs switch off) and whose finder finds a patient starts the treatment.
    /// </summary>
    void RunJobs(World world, int i, CharacterCold cold, List<(int Medic, int Patient)> taken)
    {
        var task = (CharacterTask)world.Characters.Next[i].Task;
        for (int k = 0; k < cold.Jobs.Count; k++)
        {
            var type = cold.Jobs[k].Type;
            if (type is not (TaskType.JOB_MEDIC or TaskType.JOB_REPAIR_ROBOT or TaskType.SPLINT_JOB) || !Production.PermaJobs.FreeFor(cold, task, k)) continue;
            if (type == TaskType.SPLINT_JOB ? SplintSomeone(world, i, taken) : HealSomeone(world, i, taken, robotics: type == TaskType.JOB_REPAIR_ROBOT)) return;
        }
    }

    /// <summary>A fight is on in the squad: a member in combat or with an attack order.</summary>
    static bool Fighting(CharacterTable table, Squad squad)
    {
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && (table.Cold(s)!.InCombat || table.Next[s].Task == (byte)CharacterTask.Attack)) return true;
        return false;
    }
}
