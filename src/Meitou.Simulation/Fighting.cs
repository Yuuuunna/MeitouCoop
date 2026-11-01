using System.Numerics;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>
/// Walks an attacker to its target through the path service (docs/simulation.md "Combat"). The combat system is run with <c>SelfApproach</c> off, so a character with the
/// <see cref="CharacterTask.Attack"/> task would stand still; this system, placed before the movement system, asks for a path to the target, or to a point ahead of a fleeing
/// target (a new one when the goal has moved more than <see cref="RepathDistance"/>, at most every <see cref="RepathSeconds"/>) and drops the path once the target is in reach
/// or a swing is on.
/// </summary>
public sealed class PursuitSystem(CombatSystem combat) : ITickSystem
{
    public const float RepathDistance = 12, RepathSeconds = 0.5f;

    /// <summary>Slots per job of the parallel schedule.</summary>
    const int SlotsPerJob = 32;

    public void Schedule(World world)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        int count = table.HighWater;
        combat.IndexAttackers(world);   // for FightersAt: this schedule runs before combat's
        // Each attacker writes only its own slot and reads its target's place and the combat state of the last tick, so the slots run in parallel in any order.
        world.ParallelFor((count + SlotsPerJob - 1) / SlotsPerJob, job =>
        {
            for (int i = job * SlotsPerJob, end = Math.Min(count, i + SlotsPerJob); i < end; i++) Pursue(world, table, i, dt);
        });
    }

    void Pursue(World world, CharacterTable table, int i, float dt)
    {
        var next = table.Next;
        ref var n = ref next[i];
        var self = table.Cold(i);
        if (self is not null) self.FaceSlot = -1;
        if (!n.Alive || n.Task != (byte)CharacterTask.Attack || self?.Bashing is not null) return;   // a basher walks to its door (Locks.LockSystem)
        var st = combat.StateOf(new CharacterId(i, n.Generation));
        int t = st.TargetSlot;
        if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration) return;
        const ushort Path = (ushort)MoveFlags.AnyPath;
        var d = next[t].Position - n.Position;
        var cold = table.Cold(i)!;
        var tc = table.Cold(t);
        NotePathFailure(world, i, cold, new CharacterId(t, next[t].Generation));
        float gap = MathF.Sqrt(d.X * d.X + d.Z * d.Z) - CombatTuning.Footprint(cold) - CombatTuning.Footprint(tc);
        // It runs at the target out of the combat stance from RunGap, back to its footwork under FootworkGap, and after a target that keeps moving away at any gap.
        var tv = next[t].Velocity;
        bool away = tc is { InCombat: false } && tv.X * tv.X + tv.Z * tv.Z > CombatTuning.FleeingSpeed * CombatTuning.FleeingSpeed;
        cold.TargetFleeingTime = away ? cold.TargetFleeingTime + dt : 0;
        if (gap > CombatTuning.RunGap) cold.CombatRunning = true;
        else if (gap < CombatTuning.FootworkGap) cold.CombatRunning = false;
        bool fleeing = cold.TargetFleeingTime >= CombatTuning.FleeingSeconds;
        if (fleeing) cold.CombatRunning = true;
        // In its footwork it faces its target whichever way its path takes it, and side-steps (docs/animation.md "Side steps"; MovementSystem).
        if (!cold.CombatRunning) cold.FaceSlot = t;
        n.TaskTime -= dt;
        // Its swings are measured to where a target that is not fighting will be (CombatSystem.StartAttack's aim, AimLeadSeconds ahead), so standing and the run past are too:
        // stood within reach of the target itself, a runner's aim point would be out of reach and the chaser would stand there idle while the runner got away.
        var aim = tc is { InCombat: false } ? next[t].Position + tv * CombatTuning.AimLeadSeconds : next[t].Position;
        var da = aim - n.Position;
        float aimGap = MathF.Sqrt(da.X * da.X + da.Z * da.Z) - CombatTuning.Footprint(cold) - CombatTuning.Footprint(tc);
        // After a fleeing target it aims ahead of it (CombatTuning.LeadBase), so a faster chaser runs past; the lead follows the target's velocity, so it swings round
        // when the target turns. Engine rules: the lead and the run past hold only while the chaser is behind the nearer of the lead and aim points; once level with it
        // it turns on the target and stops with the aim point within reach, so the runner comes into its swing. A path's goal is reached and stopped at, and a
        // fighter's speed ramps up again slowly, so the goal is put RepathSeconds of the runner's way past the lead (the lead moves that far before the next path).
        // Bodies are solid here (the original's chaser runs through its runner), so the goal is also put PassWidth beside the runner's track, on the chaser's side
        // (by slot when dead behind), for the chaser to pass the runner rather than push it along.
        var goal = new Vector2(next[t].Position.X, next[t].Position.Z);
        float lead = fleeing && cold.Fighter is not null && cold.Stats is not null && cold.Medical is not null
            ? (1 - combat.AttackRate(cold)) * CombatTuning.LeadSlope + CombatTuning.LeadBase : 0;
        var level = next[t].Position + tv * MathF.Min(lead, CombatTuning.AimLeadSeconds) - n.Position;
        bool behind = lead > 0 && tv.X * level.X + tv.Z * level.Z > 0;
        // A crossbowman walks only while closing in (out of range or the line of fire blocked); aiming and reloading it stands.
        bool shooting = st.Ranged && st.RangedState != (byte)RangedStates.Closing;
        // On Hold it walks only to a target within the post test's reach of the hold point (80, 140 when the target fights it; Orders.HoldPost), else it stands (engine reading).
        bool holding = false;
        if (Orders.Holding(cold))
        {
            float far = combat.StateOf(new CharacterId(t, next[t].Generation)).TargetSlot == i ? 140 : 80;
            holding = Vector2.DistanceSquared(Orders.HoldPointOf(cold, n.Position), new Vector2(next[t].Position.X, next[t].Position.Z)) > far * far;
        }
        // In its footwork with a clear line to the target, the fight steering moves it (docs/game/combat.md "Fight steering"): its state sets the band, no paths.
        if (Steer(world, i, ref n, cold, st, t, holding)) return;
        float stand = CombatTuning.StandGap(st.Reach, cold.Fighter?.WeaponLength ?? 0, cold.CombatRunning);
        if (st.Down || st.AttackTech >= 0 || st.ReactTech >= 0 || world.Tick < st.StunUntil || shooting || !st.Ranged && !behind && aimGap <= stand || holding)
        {
            // In reach, mid-swing or staggered (the hit reaction carries it), or holding position: stand and fight.
            if ((n.Flags & Path) != 0)
            {
                n.Flags = (ushort)(n.Flags & ~Path);
                n.PathCursor = 0;
                table.Cold(i)!.PathRequest = 0;
            }
            return;
        }
        if ((n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0 || n.TaskTime > 0) return;
        // The point past the runner only now, at most once per RepathSeconds: the navmesh test is a point query per chaser.
        if (behind)
        {
            var heading = Vector2.Normalize(new Vector2(tv.X, tv.Z));
            var across = new Vector2(-heading.Y, heading.X);
            float side = across.X * -d.X + across.Y * -d.Z;
            float pass = CombatTuning.PassWidth * (CombatTuning.Footprint(cold) + CombatTuning.Footprint(tc));
            var ahead = goal + new Vector2(tv.X, tv.Z) * (lead + RepathSeconds) + across * (side > 0 || side == 0 && (i & 1) == 0 ? pass : -pass);
            if (world.Walkability.IsWalkable(ahead.X, ahead.Y, next[t].Position.Y)) goal = ahead;   // off the navmesh (a wall, a cliff) it goes for the target itself
        }
        bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0 || Vector2.DistanceSquared(goal, n.Goal) > RepathDistance * RepathDistance;
        if (!stale) return;
        n.Goal = goal;
        n.Mode = SpeedMode.Free;
        n.Flags |= (ushort)MoveFlags.NeedPath;
        n.TaskTime = RepathSeconds;
    }

    /// <summary>
    /// Hands fighter <paramref name="i"/> to the fight steering (<see cref="CombatSteering"/>) when it is in its footwork, melee, not holding and not down, and the line
    /// to its target is clear (<see cref="CombatSystem.SteersAt"/>, checked every <see cref="RepathSeconds"/>): its paths are dropped and its fight state sets the band
    /// (<c>combatMovementOffensive</c>'s callers, docs/game/combat.md "Fight steering"). A stumble holds it still (engine choice: the hit reaction carries it). False: it walks paths.
    /// </summary>
    bool Steer(World world, int i, ref CharacterHot n, CharacterCold cold, in CombatSlot st, int t, bool holding)
    {
        var steer = cold.Steer ??= new SteerState();
        bool was = steer.Active;
        steer.Active = false;
        // A swing has no steering: the movement is animation driven (the attack start and every swing tick set the movement's lock, FUN_14065e240; CharMovement update
        // FUN_14065ffa0 then moves it by the clip's root motion only), so it stands in its place (MovementSystem turns it, CombatSystem.SwingAim).
        if (cold.CombatRunning || st.Ranged || holding || st.Down || st.AttackTech >= 0) return false;
        if (world.Tick >= steer.LineTick || steer.Target != t)
        {
            steer.LineClear = combat.SteersAt(world, i, t);
            steer.LineTick = world.Tick + (long)MathF.Ceiling(RepathSeconds / world.TickSeconds);
        }
        if (!steer.LineClear) return false;
        float r = CombatTuning.Footprint(cold);
        float reach = CombatTuning.StandGap(st.Reach, cold.Fighter?.WeaponLength ?? 0, false);
        // The wait after its own swing is state 6 too, whether or not it is engaged (FUN_14060d6a0 forces it while +0x144 is set).
        bool waiting = st.AttackTech < 0 && st.ReactTech < 0 && world.Tick < st.PauseUntil;
        int around = st.AttackTech < 0 && st.ReactTech < 0 && (!st.Engaged || waiting) ? combat.FightersAt(world, t) : 0;
        // Swinging (AttackState), defending (CombatClassAI slot 12), engaged (attack start, state 5), else waiting (state 6).
        var (near, far, through, limit) = st.ReactTech >= 0 ? (r + 7.5f, r + 19, false, 16f)
            : st.Engaged && !waiting ? (1.4f * r, reach, !st.CircleState, 200f)
            : (around * 0.25f + CombatTuning.CircleNear, 3 * around + CombatTuning.CircleFar, false, 200f);
        (steer.Min, steer.Max) = CombatSteering.Band(near, far, r * (cold.Medical?.ScaleK ?? 1));
        (steer.PowerThrough, steer.Limit, steer.Target) = (through, limit, t);
        if ((n.Flags & (ushort)MoveFlags.AnyPath) != 0)
        {
            n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
            n.PathCursor = 0;
            cold.PathRequest = 0;
        }
        if (world.Tick < st.StunUntil) return true;
        if (!was) steer.Reset();
        steer.Active = true;
        return true;
    }

    /// <summary>How long a target the fighter found no path to is left out of its target choice (<b>Verified</b> literal 20, <c>AITaskSytem::notifyPathImpossible</c>; read as seconds).</summary>
    public const float PathFailureSeconds = 20;

    /// <summary>
    /// Lapses the path failures and, after a path request that found no way, adds <paramref name="target"/> (<see cref="CharacterCold.PathFailures"/>). A lapsed one is armed for another
    /// <see cref="PathFailureSeconds"/> while there is still no path to its target, else dropped (<c>AITaskSytem::update4Frame</c>, <b>Observed</b>; at most one dropped per call).
    /// </summary>
    internal static void NotePathFailure(World world, int i, CharacterCold cold, CharacterId target)
    {
        var failures = cold.PathFailures;
        long armed = world.Tick + (long)MathF.Ceiling(PathFailureSeconds / world.TickSeconds);
        bool dropped = false;
        if (failures is not null)
            for (int k = failures.Count - 1; k >= 0; k--)
            {
                if (cold.PathFailed && failures[k].Target == target) { failures.RemoveAt(k); continue; }
                if (failures[k].Until > world.Tick || dropped) continue;
                if (!PathExists(world, i, cold, failures[k].Target)) failures[k] = (failures[k].Target, armed);
                else { failures.RemoveAt(k); dropped = true; }
            }
        if (!cold.PathFailed) return;
        cold.PathFailed = false;
        (cold.PathFailures ??= []).Add((target, armed));
    }

    /// <summary>
    /// Whether fighter <paramref name="i"/> has a path to <paramref name="target"/> (<c>Character::pathExists</c>): a gone target counts as one (the entry is dropped). Engine reading:
    /// asked of the walkability on the spot, with the fighter's footprint and water costs, as its own path requests are.
    /// </summary>
    static bool PathExists(World world, int i, CharacterCold cold, CharacterId target)
    {
        var table = world.Characters;
        int t = target.Slot;
        var prev = table.Previous;
        if ((uint)t >= (uint)prev.Length || !prev[t].Alive || prev[t].Generation != target.Generation) return true;
        var next = table.Next;
        var water = WaterCost.Of(cold.WaterFactor, next[i].MaxSpeed, cold.Race, cold.SwimSpeed);
        var path = world.Walkability is IAgentWalkability agent
            ? agent.FindPath(next[i].Position, prev[t].Position, cold.FootprintRadius, water, cold.RoadPreference)
            : world.Walkability.FindPath(next[i].Position, prev[t].Position);
        return path.Points.Count > 0;
    }

    /// <summary>Whether <paramref name="cold"/> found no path to <paramref name="target"/> within the last <see cref="PathFailureSeconds"/>.</summary>
    public static bool PathImpossible(CharacterCold cold, CharacterId target, long tick)
    {
        if (cold.PathFailures is not { Count: > 0 } failures) return false;
        foreach (var (t, until) in failures) if (t == target && until > tick) return true;
        return false;
    }
}


/// <summary>
/// A minimal answer to being attacked (an <b>engine rule</b>, the original's AI is stage 9): at once when an attacker takes it as its target and that alerts it
/// (<see cref="CombatSystem.Alerts"/>: not through a wall or from another floor for the player's side, until a blow lands; the alert ends its sneaking), and again every
/// <see cref="CheckEveryTicks"/> ticks, a character that is awake, armed with a
/// <see cref="Fighter"/>, not fighting already, and that someone is attacking, fights that attacker, and so do its squad mates within <see cref="JoinRange"/> units of it that are
/// free too, whether or not the attacked one is free itself, against an attacker within <see cref="ProtectReach"/> of it (never against a mate of the same squad; a minded NPC
/// only by a protect goal, <see cref="ProtectsMates"/>). An NPC always answers; one of the player's characters answers only while it has no order (task <see cref="CharacterTask.Idle"/>: not walking to an order, treating or
/// resting), so a player who sends a character away is obeyed (<see cref="SelfDefends"/>); Passive keeps it from helping others, not from defending itself.
/// The orders go into the world's deferred list for the next tick (<see cref="World.Defer"/>) and are marked <see cref="AttackOrder.Defensive"/>. With <see cref="SquadAi.Help"/> the player's
/// free characters also defend characters of allied factions (<see cref="HelpAllies"/>).
/// </summary>
public sealed class RetaliationSystem(CombatSystem combat) : ITickSystem
{
    public const int CheckEveryTicks = 15;
    /// <summary>How near a mate must be to join (engine choice: the original's only limit is that the joiner senses the mate, up to its sight range).</summary>
    public const float JoinRange = 250;

    readonly List<int> guards = [], players = [], protectors = [];
    /// <summary>Per attacker slot: the target (slot, generation) it last took and whether that target has been alerted (<see cref="CombatSystem.Alerts"/>).</summary>
    int[] alertSlot = [], alertGeneration = [];
    bool[] alerted = [], justAlerted = [];

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        if (alertSlot.Length < table.HighWater)
        {
            int size = Math.Max(table.HighWater, alertSlot.Length * 2);
            Array.Resize(ref alertSlot, size);
            Array.Resize(ref alertGeneration, size);
            Array.Resize(ref alerted, size);
            Array.Resize(ref justAlerted, size);
        }
        // A target taken (or a blow landed) alerts at once and is answered in the same pass; the rest is looked at every CheckEveryTicks.
        bool fresh = false;
        for (int a = 0; a < table.HighWater; a++)
        {
            justAlerted[a] = false;
            int t = next[a].Alive && next[a].Task == (byte)CharacterTask.Attack ? combat.StateOf(new CharacterId(a, next[a].Generation)).TargetSlot : -1;
            if (t < 0 || t >= table.HighWater)
            {
                alertSlot[a] = -1;
                continue;
            }
            if (alertSlot[a] != t || alertGeneration[a] != next[t].Generation)
            {
                alertSlot[a] = t;
                alertGeneration[a] = next[t].Generation;
                alerted[a] = false;
            }
            if (alerted[a] || !combat.Alerts(world, a, t)) continue;
            alerted[a] = justAlerted[a] = fresh = true;
            // The alert ends the target's sneaking and its squad tags the attacker TEMP_ENEMY (AI::underMeleeAttack).
            world.SystemOf<Dialogue.DialogueSystem>()?.TagEnemy(world, t, a);
            if (table.Cold(t) is { } tc)
            {
                tc.Orders &= ~CharacterOrders.Sneak;
                // Under melee attack (Character +0x2b0) one staying low stands.
                if (tc.Medical is { } medical) medical.StayingLow = false;
                combat.UnderAttackGettingUp(world, t, tc, (ulong)(uint)a);
            }
        }
        bool periodic = world.Tick % CheckEveryTicks == 0;
        if (!periodic && !fresh) return;
        ResumeGuards(world, table);
        // The bodyguards and the player's characters, in slot order, for Guards and HelpAllies (each looked through the whole table per attacker before).
        guards.Clear();
        players.Clear();
        protectors.Clear();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { } c) continue;
            if (c.Guarding) guards.Add(s);
            if (c.IsPlayer) players.Add(s);
            if (c.Ai is { ProtectsAllies: true }) protectors.Add(s);
        }
        var taken = new HashSet<int>();
        for (int a = 0; a < table.HighWater; a++)
        {
            if (!next[a].Alive || next[a].Task != (byte)CharacterTask.Attack) continue;
            var attacker = new CharacterId(a, next[a].Generation);
            var st = combat.StateOf(attacker);
            int t = st.TargetSlot;
            if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration || table.Cold(a) is not { Medical.Incapacitated: false }) continue;
            // An attacker its target was not alerted to (the wall and floor filter) is left alone until a blow lands.
            if (alertSlot[a] != t || !alerted[a] || !periodic && !justAlerted[a]) continue;
            // A gunner leaves its turret only for a melee attacker (task 146's "!iAmBeingMeleeAttacked"); bolts do not bring it down.
            if (st.Ranged && table.Cold(t)?.Gunning is not null) continue;
            var defenders = new List<CharacterId>();
            if (SelfDefends(table, t, taken, next[a].Position))
            {
                defenders.Add(new(t, next[t].Generation));
                taken.Add(t);
            }
            // Free squad mates come to an attacked character that is already fighting (or down) too, not only to one that answers now, against an attacker within
            // ProtectReach of it (AI::findAttackerOf); a minded NPC only by a protect goal and to a mate that fights (ProtectsMates).
            var gap = next[a].Position - next[t].Position;
            bool fights = combat.StateOf(new CharacterId(t, next[t].Generation)).TargetSlot >= 0 || defenders.Count > 0;
            if (gap.X * gap.X + gap.Z * gap.Z <= ProtectReach * ProtectReach && (table.Cold(a)?.SquadId is not { } foeSquad || foeSquad < 0 || foeSquad != table.Cold(t)?.SquadId))
            {
                JoinMates(world, table, t, defenders, taken, fights ? CanProtectFighter : CanProtect);
                // Protect allies: NPCs of any squad that see an allied defender fighting come to it (a town's guards to a resident a neutral animal attacks).
                if (fights) ProtectAllies(world, table, t, a, defenders, taken);
            }
            // Bodyguards of the attacked character come too (Bodyguard, order 45).
            Guards(world, table, t, a, defenders, taken, guards);
            // Allies come to a defender that is already fighting too.
            HelpAllies(world, table, t, a, defenders, taken);
            // The Hold order takes on only an attacker its post test accepts (Orders.HoldPost).
            var from = next[a].Position;
            for (int k = defenders.Count - 1; k >= 0; k--)
            {
                int ds = defenders[k].Slot;
                if (table.Cold(ds) is { } dc && Orders.Holding(dc)
                    && !Orders.HoldPost(next[ds].Position, Orders.HoldPointOf(dc, next[ds].Position), from, next[a].Velocity, st.Down, t == ds)) defenders.RemoveAt(k);
            }
            if (defenders.Count == 0) continue;
            world.Defer(new AttackOrder(defenders, attacker) { Tick = world.Tick + 1, Defensive = true });
        }
    }

    /// <summary>
    /// Bodyguard (order 45, <b>engine</b> reading): the free characters following <paramref name="slot"/> as bodyguards (<see cref="CharacterCold.Guarding"/>) within <see cref="JoinRange"/> of it
    /// defend it against <paramref name="attacker"/>, in slot order; never against a character of their own faction.
    /// </summary>
    static void Guards(World world, CharacterTable table, int slot, int attacker, List<CharacterId> defenders, HashSet<int> taken, List<int> guards)
    {
        var next = table.Next;
        var target = new CharacterId(slot, next[slot].Generation);
        foreach (int s in guards)
        {
            if (!next[s].Alive || s == attacker || table.Cold(s) is not { Guarding: true } c || c.FollowOrder != target || table.Cold(attacker)?.Faction == c.Faction) continue;
            var d = next[s].Position - next[slot].Position;
            if (d.X * d.X + d.Z * d.Z > JoinRange * JoinRange || !CanFight(table, s, taken)) continue;
            defenders.Add(new CharacterId(s, next[s].Generation));
            taken.Add(s);
        }
    }

    /// <summary>A bodyguard whose fight ended (its task fell back to idle) goes back to its target.</summary>
    static void ResumeGuards(World world, CharacterTable table)
    {
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || next[s].Task != (byte)CharacterTask.Idle || table.Cold(s) is not { Guarding: true, InCombat: false } c) continue;
            if (c.FollowOrder.IsNone || !table.TryResolveNext(c.FollowOrder, out _)) { c.Guarding = false; c.FollowOrder = CharacterId.None; continue; }
            next[s].Task = (byte)CharacterTask.Follow;
            next[s].TaskTime = 0;
            c.FollowPathTime = 0;
        }
    }

    /// <summary>
    /// Adds the squad mates of <paramref name="slot"/> within <see cref="JoinRange"/> of it that <paramref name="free"/> accepts to <paramref name="fighters"/> and to
    /// <paramref name="taken"/>, in the squad's member order (the join rule retaliation and <see cref="AggroSystem"/> share).
    /// </summary>
    public static void JoinMates(World world, CharacterTable table, int slot, List<CharacterId> fighters, HashSet<int> taken, Func<CharacterTable, int, HashSet<int>, bool> free)
    {
        var next = table.Next;
        var squad = table.Cold(slot)!.SquadId >= 0 ? world.Squads.Find(table.Cold(slot)!.SquadId) : null;
        if (squad is null) return;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || s == slot || !free(table, s, taken)) continue;
            var d = next[s].Position - next[slot].Position;
            if (d.X * d.X + d.Z * d.Z > JoinRange * JoinRange) continue;
            fighters.Add(m);
            taken.Add(s);
        }
    }

    /// <summary>
    /// <see cref="SquadAi.Help"/> (the protect-allies finders <c>FUN_14099ca10</c> / <c>FUN_14099cf80</c>, which take a fighting character of another faction only with the global
    /// +0x111 set, and then with the weight squared): the player's free characters within <see cref="JoinRange"/> of a defender of another, allied faction (relation 50 or more) join
    /// its defence, in slot order. The original's distance weighting is not modelled (engine reduction).
    /// </summary>
    void HelpAllies(World world, CharacterTable table, int slot, int attacker, List<CharacterId> defenders, HashSet<int> taken)
    {
        if (combat.Relations is not { } relations || table.Cold(slot) is not { Medical.Incapacitated: false } ally || !relations.Contains(ally.Faction)) return;
        if (table.Cold(attacker) is not { } foe) return;
        var next = table.Next;
        var at = next[slot].Position;
        foreach (int s in players)
        {
            if (!next[s].Alive || table.Cold(s) is not { IsPlayer: true } c || c.Faction == ally.Faction || !relations.Contains(c.Faction) || !relations.IsAlly(c.Faction, ally.Faction)) continue;
            if ((world.SquadOptions(c) & SquadAi.Help) == 0 || c.Faction == foe.Faction || relations.Contains(foe.Faction) && relations.IsAlly(c.Faction, foe.Faction)) continue;
            var d = next[s].Position - at;
            if (d.X * d.X + d.Z * d.Z > JoinRange * JoinRange || !CanFight(table, s, taken)) continue;
            defenders.Add(new CharacterId(s, next[s].Generation));
            taken.Add(s);
        }
    }

    /// <summary>
    /// PROTECT_ALLIES (15, 288; finder <c>FUN_14099ca10</c>, achieved by 13 "Attacking attackers of", docs/game/ai-tasks.md "Combat goals"): an NPC with the goal that sees a
    /// fighting character of its own or an allied faction (relation 50 or more) attacks that character's attacker when the attacker is not its ally, whatever their relation, so
    /// neutrals count too. Engine reductions: the defender is sensed when seen (sight test) or within <see cref="AggroSystem.NoiseFar"/> in the protector's place (a fight
    /// is heard for sure), a gunner stays at its turret, and 288's town limit is not modelled.
    /// </summary>
    void ProtectAllies(World world, CharacterTable table, int slot, int attacker, List<CharacterId> defenders, HashSet<int> taken)
    {
        if (protectors.Count == 0 || combat.Relations is not { } relations || table.Cold(slot) is not { } ally || table.Cold(attacker) is not { } foe
            || !relations.Contains(ally.Faction) || !relations.Contains(foe.Faction)) return;
        var next = table.Next;
        var at = next[slot].Position;
        foreach (int s in protectors)
        {
            if (s == attacker || s == slot || !next[s].Alive || table.Cold(s) is not { Ai.ProtectsAllies: true, Gunning: null } c
                || !relations.IsAlly(c.Faction, ally.Faction) || relations.IsAlly(c.Faction, foe.Faction)) continue;
            float range = AggroSystem.RangeOf(c, AggroSystem.OutdoorSight), d2 = Vector3.DistanceSquared(next[s].Position, at);
            if (d2 > MathF.Max(range * range, AggroSystem.NoiseFar * AggroSystem.NoiseFar) || !CanFight(table, s, taken)) continue;
            // Seen, or heard: a fight in the protector's own place within the farthest noise is heard for sure (AggroSystem's hearing rule).
            bool heard = d2 <= AggroSystem.NoiseFar * AggroSystem.NoiseFar && world.Sight.PlaceOf(s) == world.Sight.PlaceOf(slot);
            if (!heard && !(d2 <= range * range && world.Sight.Sees(s, slot, next[s].Position, next[s].Yaw, at, range) > 0)) continue;
            defenders.Add(new CharacterId(s, next[s].Generation));
            taken.Add(s);
        }
    }

    /// <summary>A player's character defends itself only against an attacker within this (Self preservation's desire is 0 beyond squared 33000, docs/game/combat.md "Fight lifecycle and orders").</summary>
    public static readonly float SelfDefenceReach = MathF.Sqrt(33000);
    /// <summary>A threat this near is immediate (Character +0x2b0): it ends a Loot order.</summary>
    public const float ImmediateReach = 40;

    /// <summary>
    /// Whether the attacked character itself answers (Self preservation, docs/game/combat.md "Fight lifecycle and orders"): Passive does not stop it. A player's character has
    /// it only without an order (its orders outrank the goal), and only against an attacker within <see cref="SelfDefenceReach"/>; a Loot order gives way to one within
    /// <see cref="ImmediateReach"/>. An NPC always answers (engine choice: its attack-on-sight goal covers the farther attackers).
    /// </summary>
    static bool SelfDefends(CharacterTable table, int slot, HashSet<int> taken, Vector3 attacker)
    {
        if (!Able(table, slot, taken, out var cold, out byte task)) return false;
        if (!cold.IsPlayer) return true;
        var d = attacker - table.Next[slot].Position;
        float d2 = d.X * d.X + d.Z * d.Z;
        if (d2 > SelfDefenceReach * SelfDefenceReach) return false;
        return task == (byte)CharacterTask.Idle || cold.Guarding && task == (byte)CharacterTask.Follow || task == (byte)CharacterTask.Loot && d2 <= ImmediateReach * ImmediateReach;
    }

    /// <summary>Whether a character comes to another's defence: a player's character only without an order and not Passive (<c>scoreProtectAllies</c> is 0 when Passive).</summary>
    static bool CanFight(CharacterTable table, int slot, HashSet<int> taken) =>
        Able(table, slot, taken, out var cold, out byte task)
        && (!cold.IsPlayer || (task == (byte)CharacterTask.Idle || cold.Guarding && task == (byte)CharacterTask.Follow) && (cold.Orders & CharacterOrders.Passive) == 0);

    /// <summary>A mate is protected only against an attacker within this of it (<c>AI::findAttackerOf</c>, docs/game/combat.md "Fight lifecycle and orders").</summary>
    public const float ProtectReach = 150;

    /// <summary>
    /// Whether a squad mate comes to the defence of one that fights (<see cref="CanProtectFighter"/>) or not (<see cref="CanProtect"/>): one that <see cref="CanFight"/>, and of the
    /// NPCs with an AI only those whose combat goal protects (143 PROTECT_OWN_SQUAD finds the attackers of a mate holding a target; PROTECT_ALLIES and the like); the
    /// others join through their stance in <see cref="AggroSystem"/>, and one with no combat goal does not (docs/game/ai-tasks.md "Combat goals"). An NPC without an AI
    /// and the player's characters keep the old rule.
    /// </summary>
    static bool CanProtectFighter(CharacterTable table, int slot, HashSet<int> taken) => CanFight(table, slot, taken) && ProtectsMates(table.Cold(slot)!, true);

    static bool CanProtect(CharacterTable table, int slot, HashSet<int> taken) => CanFight(table, slot, taken) && ProtectsMates(table.Cold(slot)!, false);

    static bool ProtectsMates(CharacterCold cold, bool fights) =>
        cold.IsPlayer || cold.Ai is not { } mind || fights && mind.Stance is Ai.AiStance.ProtectSquad or Ai.AiStance.Defensive;

    static bool Able(CharacterTable table, int slot, HashSet<int> taken, out CharacterCold cold, out byte task)
    {
        task = table.Next[slot].Task;
        cold = table.Cold(slot)!;
        if (taken.Contains(slot) || task == (byte)CharacterTask.Attack) return false;
        // a carried mate does not answer; a carrier does (no attack goal of the original's tests carrying, docs/game/carry-loot.md "A carrier in a fight")
        return cold is { Fighter.CanAttack: true, Fleeing: false, IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true };
    }
}
