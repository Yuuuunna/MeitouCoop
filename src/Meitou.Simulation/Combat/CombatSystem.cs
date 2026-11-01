using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>
/// Melee combat (docs/simulation.md "Combat", docs/game/combat.md). One system, three parts per tick:
/// <list type="bullet">
/// <item><b>Act</b> (parallel, per character, reading the last tick's <see cref="CombatSlot"/> array and the table's previous state, writing only its own): the medical tick; a
/// defender looks for blows coming at it and picks a reaction (block roll, wrong-direction block on a failure, dodge for fists); an attacker keeps its swing going and, when
/// the animation reaches a blow's <c>anim blocked frame</c>, emits a <see cref="BlowEffect"/> to the target; a free attacker in reach picks a technique and starts it.</item>
/// <item><b>Apply</b> (serial, in the commit's order: target, source, system, sequence): the outcome from the defender's reaction progress ("blocked", "dodged" or "hit", combat.md),
/// then for a hit the damage packet, part choice, armour, toughness and the medical hit, a stagger for a heavy hit, and XP for both sides.</item>
/// <item><b>Move</b>: faces the target and, with <see cref="CombatOptions.SelfApproach"/>, walks up to it.</item>
/// </list>
/// Everything random is a function of (seed, character, kind, counter) so the result does not depend on the thread count. Stats, bodies and gear change only here in the commit and in
/// the owner's own Act (the medical tick); other characters only read the <see cref="CombatSlot"/> copy.
/// </summary>
public sealed partial class CombatSystem : ITickSystem, IStateHashed
{
    /// <summary>The <see cref="Effect.Kind"/> of a blow: <c>Arg</c> is the blow (1 or 2) and <c>Amount</c> the attacker's attack number.</summary>
    public const int BlowEffect = 1;

    readonly CombatTechnique[] techniques, asWritten;
    readonly CombatConstants combat;
    readonly GameConstants constants;
    readonly BodyOptions options;
    readonly AnimationLengths lengths;
    readonly XpService xp;
    readonly List<CombatLogEntry> log = [];
    readonly List<MedicalEvent> hitEvents = [];
    CombatSlot[] read = [];
    CombatSlot[] write = [];

    public CombatSystem(IEnumerable<CombatTechnique> techniques, CombatConstants combat, GameConstants constants, BodyOptions options, AnimationLengths lengths, CombatOptions? settings = null)
    {
        this.techniques = [.. techniques];
        asWritten = [.. this.techniques];
        this.combat = combat;
        this.constants = constants;
        this.options = options;
        this.lengths = lengths;
        Settings = settings ?? new CombatOptions();
        AttackSlots = Math.Max(1, Settings.AttackSlots ?? combat.MaxNumAttackSlots);
        LateDefences = Settings.LateDefences;
        GunnerFacesBarrel = Settings.GunnerFacesBarrel;
        PunchReachFix = Settings.PunchReachFix;
        xp = new XpService(constants, options);
    }

    public CombatOptions Settings { get; }

    /// <summary>How many enemies may melee one target at once, before the target race's <c>extra attack slots</c>: CONSTANTS <c>max num attack slots</c> (1, Faithful) unless a setting changes it.</summary>
    public int AttackSlots { get; set; }

    /// <summary>A late but successfully rolled dodge or block still counts (<see cref="CombatOptions.LateDefences"/>); off is the game's rule.</summary>
    public bool LateDefences { get; set; }

    /// <summary>A turret's gunner faces along the barrel as the gun turns (<see cref="CombatOptions.GunnerFacesBarrel"/>); off is the game's facing.</summary>
    public bool GunnerFacesBarrel { get; set; }

    /// <summary>
    /// A fist attack whose <c>attack distance</c> is negative but above -100 (base data: "ma badpunch", -4, the only punch up to martial arts 20) reads it as positive
    /// (<see cref="CombatOptions.PunchReachFix"/>, Meitou fix; docs/game/combat.md "Attack technique choice"); off: as written, as the game does.
    /// </summary>
    public bool PunchReachFix
    {
        get => punchReachFix;
        set
        {
            punchReachFix = value;
            for (int t = 0; t < techniques.Length; t++)
                techniques[t] = value && asWritten[t] is { IsBlock: false, IsDodge: false, AttackDistance: < 0 and > -100 } w && (w.Kinds & WeaponKinds.Unarmed) != 0
                    ? w with { AttackDistance = -w.AttackDistance } : asWritten[t];
        }
    }
    bool punchReachFix;

    /// <summary>The buildings' collision that stops bolts and lines of fire (the ragdolls' environment, <see cref="SolidHits"/>); null: the terrain only.</summary>
    public Ragdolls.IRagdollEnvironment? Buildings { get; set; }

    /// <summary>The live relation table (<c>PopulationData.Relations</c>) that says who is an enemy; null: everyone of another faction is (hand-built test worlds).</summary>
    public FactionRelations? Relations { get; set; }
    /// <summary>The relation events of the landed blows (<see cref="FactionDeeds.Blow"/>); null: blows change no relation (hand-built test worlds).</summary>
    public FactionDeeds? Deeds { get; set; }
    /// <summary>The crime system the player's assaults and hits on the police are reported to (null: no crimes).</summary>
    public Crime.CrimeSystem? Crimes { get; set; }
    /// <summary>
    /// Picks the hit reaction clip of a hit (set by the animation system, which owns the clips): the defender, the hit part's LOCATIONAL_DAMAGE id, big stumble, the side
    /// (0 front, 1 rear) and a roll in [0, 1); returns the clip (an animation library index) and its length in seconds, or null when none fits. Null: hits play no clip.
    /// </summary>
    public Func<CharacterCold, string, bool, int, float, (int Clip, float Seconds)?>? StumbleChooser { get; set; }
    /// <summary>How long a woken character gets up (the animation system sets it from <c>standing up 3</c>: its 0.75 s blend and the clip to <c>GetUpEnd</c>); 0: no get-up.</summary>
    public float GetUpSeconds { get; set; }
    /// <summary>The chance that an attack on a character getting up with its fight off knocks it back down, and for how long (<c>AI::underMeleeAttack</c>, 0.1 and 10.0, <b>Verified</b>).</summary>
    public const float KnockDownChance = 0.1f, KnockDownSeconds = 10;

    /// <summary>
    /// <c>AI::underMeleeAttack</c> on <paramref name="t"/> getting up with its fight off (targeted by an attacker, or hit; <paramref name="counter"/> tells the attacks of one tick apart): a <see cref="KnockDownChance"/> roll knocks it out for
    /// <see cref="KnockDownSeconds"/> (its KO timer set). Serial phases.
    /// </summary>
    internal void UnderAttackGettingUp(World world, int t, CharacterCold tc, ulong counter)
    {
        if (tc.InCombat || tc.GettingUpUntil <= world.Tick || tc.Medical is not { Incapacitated: false } medical) return;
        float roll = CombatRolls.Float(world.Seed, Rng.Key(world.Characters.IdOf(t)), CombatRoll.KnockDown, ((ulong)world.Tick << 32) ^ counter);
        if (roll >= KnockDownChance) return;
        medical.KnockOut(KnockDownSeconds);
        tc.GettingUpUntil = 0;
    }
    /// <summary>The outdoor sight a fighter senses a current target within (before the race's multiplier; <see cref="AggroSettings.SightRange"/>'s default).</summary>
    public float SenseRange { get; set; } = AggroSystem.OutdoorSight;
    public IReadOnlyList<CombatTechnique> Techniques => techniques;

    /// <summary>The resolved blows so far (when <see cref="CombatOptions.RecordLog"/> is on).</summary>
    public IReadOnlyList<CombatLogEntry> Log => log;

    /// <summary>The combat state of a character as of the last finished tick (default when it has none).</summary>
    public CombatSlot StateOf(CharacterId id) => id.Slot >= 0 && id.Slot < read.Length && read[id.Slot].Generation == id.Generation ? read[id.Slot] : CombatSlot.Fresh(id.Generation);

    /// <summary>The technique a character is playing and its progress (0 to 1), for the animation layer; null when it is not attacking or reacting.</summary>
    public (CombatTechnique Technique, float Progress, bool IsAttack)? Playing(CharacterId id, long tick)
    {
        var s = StateOf(id);
        if (s.AttackTech >= 0 && s.AttackTicks > 0) return (techniques[s.AttackTech], Math.Clamp((tick - s.AttackStart) / (float)s.AttackTicks, 0, 1), true);
        if (s.ReactTech >= 0 && s.ReactTicks > 0) return (techniques[s.ReactTech], Math.Clamp((tick - s.ReactStart) / (float)s.ReactTicks, 0, 1), false);
        return null;
    }

    // ------------------------------------------------------------------ state arrays

    void Sync(CharacterTable table)
    {
        int n = table.HighWater;
        if (read.Length < n)
        {
            int size = Math.Max(n, read.Length * 2);
            int old = read.Length;
            Array.Resize(ref read, size);
            Array.Resize(ref write, size);
            for (int i = old; i < size; i++) { read[i] = CombatSlot.Fresh(-1); write[i] = CombatSlot.Fresh(-1); }
        }
        var next = table.Next;
        for (int i = 0; i < n; i++)
        {
            int generation = next[i].Alive ? next[i].Generation : -1;
            if (read[i].Generation != generation) read[i] = CombatSlot.Fresh(generation);
        }
    }

    public void Hash(ref StateHasher hasher)
    {
        hasher.Add(read.Length);
        for (int i = 0; i < read.Length; i++) read[i].Hash(ref hasher);
        hasher.Add(log.Count);
        HashRanged(ref hasher);
        HashTurrets(ref hasher);
    }

    // ------------------------------------------------------------------ inputs

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        Sync(table);
        int now = (int)world.Tick;
        foreach (var command in commands)
        {
            switch (command)
            {
                case AttackOrder order:
                {
                    // The order itself changes no relation (the original's relation events come from the blows, Deeds); an attack that is not self-defence is an assault.
                    foreach (var id in command.Issuer is null ? order.Attackers : world.CommandCharacters(order, order.Attackers))
                        if (Engage(world, table, id, order.Target, now, order.Picked) && !order.Defensive && Crimes is { } crimes && table.TryResolveNext(order.Target, out int victimSlot))
                            crimes.Assault(world, id.Slot, victimSlot);
                    break;
                }
                case ShootAtOrder shoot:
                    foreach (var id in world.CommandCharacters(shoot, shoot.Gunners))
                        if (table.TryResolveNext(id, out int g) && table.Cold(g) is { IsPlayer: true, Gunning: not null } gunner && table.TryResolveNext(shoot.Target, out int victim) && victim != g
                            && table.Cold(victim) is { Medical.Dead: false }) gunner.TurretTarget = shoot.Target;
                    break;
                case StopCommand stop:
                    foreach (var id in command.Issuer is null ? stop.Characters : world.CommandCharacters(stop, stop.Characters)) Disengage(table, id);
                    break;
                // A move or follow order replaces the fight (the original's new task drops the fight task, and a new movement goal clears the stance): the runner
                // neither swings at a chaser that comes into reach nor defends; its weapon stays out until it idles (docs/game/combat.md "Fight lifecycle and orders").
                case MoveOrder move:
                    foreach (var id in world.CommandCharacters(move, move.Characters)) LeaveFight(table, id);
                    break;
                case FollowOrder follow:
                    foreach (var id in world.CommandCharacters(follow, follow.Characters))
                        if (id != follow.Target) LeaveFight(table, id);
                    break;
            }
        }
    }

    bool Engage(World world, CharacterTable table, CharacterId attacker, CharacterId target, int now, bool picked)
    {
        if (!table.TryResolveNext(attacker, out int a) || !table.TryResolveNext(target, out int t) || a == t) return false;
        var cold = table.Cold(a)!;
        var tc = table.Cold(t)!;
        if (cold.Fighter is null || cold.Stats is null || cold.Medical is null || cold.Race is null || tc.Medical is null || cold.Medical.Incapacitated || tc.Medical.Incapacitated) return false;
        if (cold.IsImprisoned || !cold.CarriedBy.IsNone) return false;   // a prisoner or a carried mate does not fight (it may still be attacked: engine choice)
        // A fighter turns on no one it found no path to lately unless the player picked it (isPathImpossible in every finder's score, docs/game/combat.md "Target score"): a mate
        // that is called in on a sighting or an attack and cannot reach the enemy (a cage, a locked room) would otherwise stand at the nearest spot for ever.
        if (!picked && PursuitSystem.PathImpossible(cold, target, now)) return false;
        ref var s = ref read[a];
        if (s.TargetSlot != t || s.TargetGeneration != target.Generation)
        {
            s.Engaged = false;
            s.LastTech = -1;
        }
        s.TargetSlot = t;
        s.TargetGeneration = target.Generation;
        s.Picked = picked;
        s.PickTick = now + PickTicks(world, cold);
        s.SensedTick = now;
        s.ReadyTick = now;   // no wait before the first swing (startupState enters the attack at once)
        // The fight's first path is asked for at once: the wait an idle or wandering character was in (3 to 10 s) is not the pursuit's repath timer (PursuitSystem), and left in
        // it the fighter stood there until it ran out.
        if (table.Next[a].Task != (byte)CharacterTask.Attack) table.Next[a].TaskTime = 0;
        table.Next[a].Task = (byte)CharacterTask.Attack;
        cold.InCombat = true;
        cold.DrawnWeapon = s.Ranged ? HandHold.OfCategory((int)WeaponCategory.Bow).Weapon : MeleeDrawn(cold.Fighter);
        return true;
    }

    internal void Disengage(CharacterTable table, CharacterId id)
    {
        if (!table.TryResolveNext(id, out int slot)) return;
        ref var s = ref read[slot];
        s.TargetSlot = -1;
        s.AttackTech = -1;
        s.NextBlow = 0;
        s.Engaged = false;
        s.Picked = false;
        EndFight(table, slot);
    }

    void LeaveFight(CharacterTable table, CharacterId id)
    {
        if (table.TryResolveNext(id, out int slot) && (table.Cold(slot)!.InCombat || read[slot].TargetSlot >= 0)) Disengage(table, id);
    }

    static void EndFight(CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        cold.InCombat = false;
        cold.CombatRunning = false;
        cold.RangedCombatMode = false;
        cold.TargetFleeingTime = 0;
        // The fight's end sheathes nothing (docs/game/combat.md "Fight lifecycle and orders", weapon away): the planner puts it away before a goal that needs it away
        // (the AI's UNEQUIP_WEAPON step), a player's character once it stops moving on an order (Act). A character without either sheathes now.
        if (!cold.IsPlayer && cold.Ai is null) cold.DrawnWeapon = WeaponKinds.None;
        if (table.Next[slot].Task == (byte)CharacterTask.Attack) table.Next[slot].Task = (byte)CharacterTask.Idle;
    }

    // ------------------------------------------------------------------ schedule, move

    public void Schedule(World world)
    {
        Sync(world.Characters);
        Array.Copy(read, write, world.Characters.HighWater);
        FlyProjectiles(world);
        TickTurrets(world);
        index.Build(read, world.Characters, world.Grid);
    }

    public void Move(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float dt = world.TickSeconds;
        int now = (int)world.Tick;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || next[i].Task != (byte)CharacterTask.Attack) continue;
            ref readonly var s = ref read[i];
            int t = s.TargetSlot;
            if (t < 0 || t >= prev.Length || !prev[t].Alive || prev[t].Generation != s.TargetGeneration) continue;
            var cold = table.Cold(i)!;
            var from = new Vector2(prev[i].Position.X, prev[i].Position.Z);
            var to = new Vector2(prev[t].Position.X, prev[t].Position.Z);
            var delta = to - from;
            float distance = delta.Length();
            if (distance < 1e-3f) continue;
            var dir = delta / distance;
            ref var n = ref next[i];
            // On a path to the target (the pursuit's) the movement system steers it: facing the target here would undo each tick's turn towards a waypoint that lies
            // well off the straight line, and a walker more than 90 degrees off its waypoint does not step (MovementSystem.Turn).
            if (!Settings.SelfApproach && (n.Flags & (ushort)MoveFlags.HasPath) != 0) continue;
            n.Yaw = MathF.Atan2(dir.X, dir.Y);
            if (!Settings.SelfApproach) continue;
            // A crossbowman stands while it aims or reloads and walks up only while closing in.
            bool shooting = s.Ranged && s.RangedState != (byte)RangedStates.Closing;
            bool busy = s.Down || s.AttackTech >= 0 || s.ReactTech >= 0 || now < s.StunUntil || shooting;
            float gap = distance - CombatTuning.Footprint(cold) - CombatTuning.Footprint(table.Cold(t));
            float stand = CombatTuning.StandGap(s.Reach, cold.Fighter?.WeaponLength ?? 0, cold.CombatRunning);
            if (busy || gap <= stand)
            {
                n.Velocity = default;
                continue;
            }
            float speed = n.MaxSpeed > 0 ? n.MaxSpeed : Settings.ApproachSpeed;
            if (!cold.CombatRunning) speed = MathF.Min(speed, CombatTuning.FootworkSpeed);
            float step = MathF.Min(speed * dt, gap - stand);
            float x = prev[i].Position.X + dir.X * step, z = prev[i].Position.Z + dir.Y * step;
            if (world.Walkability.IsWalkable(x, z, prev[i].Position.Y))
            {
                n.Position = new Vector3(x, world.Walkability.GroundHeight(x, z, prev[i].Position.Y), z);
                n.Velocity = new Vector3(dir.X * step / dt, 0, dir.Y * step / dt);
            }
            else n.Velocity = default;
        }
    }

    MedicalContext MakeContext(World world, int slot, CharacterCold cold) =>
        new(constants, options, cold.Race!)
        {
            Toughness = cold.Stats![StatsEnumerated.Toughness],
            Strength = cold.Stats[StatsEnumerated.Strength],
            EncumbranceFactor = cold.Fighter?.EncumbranceFactor ?? 1,
            Seed = world.Seed,
            CharacterKey = Rng.Key(world.Characters.IdOf(slot)),
        };

    // ------------------------------------------------------------------ end of tick

    public void SlowWorld(World world) => (read, write) = (write, read);
}
