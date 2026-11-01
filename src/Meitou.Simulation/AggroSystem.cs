using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>Settings of the <see cref="AggroSystem"/> (docs/simulation.md "Hostility and relations"; the original's figures in docs/game/perception.md).</summary>
public sealed record AggroSettings
{
    /// <summary>A character looks round once every this many ticks (staggered by slot: slot s on the ticks where tick % n == s % n). Engine choice: the original
    /// updates the senses of 8 characters per frame in turn (docs/game/perception.md "Update rate").</summary>
    public int ScanEveryTicks { get; init; } = 15;
    /// <summary>How far an NPC sees, in world units (dm), before the race's <c>vision range mult</c>: the original's outdoor range (1500, <c>FUN_140851bc0</c>, Verified),
    /// in full daylight; straight ahead only, the view cone shortens it to the side (<see cref="AggroSystem.Visibility"/>).</summary>
    public float SightRange { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>Keep a <see cref="AggroSystem.Log"/> of every fight started (the host prints it; tests read it).</summary>
    public bool RecordLog { get; init; }
}

/// <summary>A fight the <see cref="AggroSystem"/> started: who saw whom, the factions, the distance and how many squad mates joined.</summary>
public readonly record struct AggroEntry(long Tick, CharacterId Attacker, CharacterId Target, int AttackerFaction, int TargetFaction, float Distance, int Joined);

/// <summary>
/// Hostility by faction relations (an <b>engine stand-in</b> for the stage 9 AI's senses and target choice, docs/simulation.md "Combat"):
/// <list type="bullet">
/// <item><b>Look</b> (<c>Act</c>, parallel, own slot, reading the last tick's positions through the <see cref="SpatialGrid"/>): an awake NPC with a fighter, not fighting,
/// not fleeing, not treating or resting, whose turn it is (one in <see cref="AggroSettings.ScanEveryTicks"/>, by slot) finds the nearest living, awake character with a body
/// that it sees (<see cref="SightMap.Sees"/>: places, light and its range R = <see cref="AggroSettings.SightRange"/> x its race's <c>vision range mult</c> times the view-cone factor
/// of <see cref="Visibility"/>) whose faction its own is hostile to (relation -30 or lower) and that neither faction coexists with; ties go to the lower slot. It emits one
/// effect at that target.</item>
/// <item><b>Decide</b> (<c>Apply</c>, serial commit order): the looker, if still free, and its free squad mates within twice their own range R of the target (the original's
/// squad-shared sightings, <c>GroupSense</c>) get an <see cref="AttackOrder"/> for the next tick through <see cref="World.Defer"/>. The player's characters look and join only with
/// the attacking-enemies job (<see cref="HuntsEnemies"/>); they have no leash, and after the fight go back to following or stand idle.</item>
/// <item><b>Chase give-up and stand down</b> (<c>SlowWorld</c>, serial, every tick): an NPC running after its target (<see cref="CharacterCold.CombatRunning"/>) senses that
/// target on its look cadence (<c>Act</c>) and gives it up by the original's <c>ChaseMode</c> rule (<see cref="GiveUpGateSeconds"/>, <see cref="GiveUpSenseSeconds"/>,
/// <see cref="GiveUpReach"/>). When its fight is over (target down, or stopped) it goes back to what it did before (wander or follow; a go-to becomes idle, which the roaming
/// squads order again).</item>
/// </list>
/// The live relation table is hashed here once a deed has changed it (<see cref="FactionRelations.Changes"/>).
/// </summary>
public sealed class AggroSystem(FactionRelations relations, AggroSettings? settings = null) : ITickSystem, IStateHashed
{
    /// <summary>The <see cref="Effect.Kind"/> of a sighting: <c>Target</c> is the enemy, <c>Amount</c> the distance.</summary>
    public const int SightEffect = 1;
    /// <summary>The <see cref="Effect.Kind"/> of a hostile looker's exposure of a sneaker: <c>Target</c> is the sneaker, <c>Amount</c> its <see cref="StealthExposure"/>.</summary>
    public const int ExposureEffect = 2;
    const byte NoTask = 0xFF;

    /// <summary>The original's vision range outdoors before the race multiplier (<c>FUN_140851bc0</c>: race <c>vision range mult</c> x 1500; 500 flat inside a building).</summary>
    public const float OutdoorSight = 1500;
    /// <summary>The edge of the original's view cone: the cosine of the angle off the facing at which the view factor reaches 0 (about 78.5 degrees; <c>FUN_140852f70</c>).</summary>
    public const float ConeEdge = 0.2f;
    /// <summary>The smallest view factor that counts as seen (<c>FUN_140857b10</c>: above 0.01).</summary>
    public const float MinVisibility = 0.01f;

    /// <summary>
    /// The original's sight test for an NPC looking at a character who is not sneaking or crawling, in full daylight with both lit alike (<c>FUN_140852f70</c>): the
    /// view factor v = (cos a - 0.2) / 0.8, a the angle between the looker's facing and the direction to the target (3D), and the target is seen when v is above 0.01
    /// and the distance is at most R x v (v x <paramref name="posture"/>: 0.5 for a target crawling or on the ground, <c>FUN_140852f70</c>). Returns v when seen, else 0. A target at the looker's own position counts as seen straight ahead (an engine choice).
    /// </summary>
    public static float Visibility(Vector3 eye, float yaw, Vector3 target, float range, float posture = 1)
    {
        var d = target - eye;
        float dist = d.Length();
        if (dist < 1e-3f) return 1;
        if (dist > range) return 0;
        float cos = (MathF.Sin(yaw) * d.X + MathF.Cos(yaw) * d.Z) / dist;
        float v = (cos - ConeEdge) / (1 - ConeEdge) * posture;
        return v > MinVisibility && dist <= range * v ? v : 0;
    }

    /// <summary>
    /// The sight test for a sneaking target: the view factor v of <see cref="Visibility"/>'s cone, scaled by <see cref="Orders.SneakView"/>, must reach the distance
    /// (d at most R x v'). Returns v' when it does, else 0; in view, the target still has to fill the looker's spot meter (<see cref="Orders.SpotSeconds"/>).
    /// </summary>
    public static float SneakVisibility(Vector3 eye, float yaw, Vector3 target, float range, float stealth, float posture = 1)
    {
        var d = target - eye;
        float dist = d.Length();
        if (dist < 1e-3f) return 1;
        if (dist > range) return 0;
        float v = Orders.SneakView(((MathF.Sin(yaw) * d.X + MathF.Cos(yaw) * d.Z) / dist - ConeEdge) / (1 - ConeEdge) * posture, stealth);
        return v > MinVisibility && dist <= range * v ? v : 0;
    }

    /// <summary>The weather's factor on the noise of the character in <paramref name="slot"/> (<see cref="WeatherSystem.NoiseFactor"/>; 1 without a cold row).</summary>
    static float Noise(CharacterTable table, int slot) => table.Cold(slot) is { } c ? WeatherSystem.NoiseFactor(c) : 1;

    /// <summary>The noise a moving character makes reaches lerp(speed / max speed, 350, 600) units (<c>FUN_140851d10</c>, Verified constants; clear weather: the caller applies <see cref="WeatherSystem.NoiseFactor"/>; not sneaking).</summary>
    public const float NoiseNear = 350, NoiseFar = 600;
    /// <summary>The chance per check that a character within the noise range is heard when it is not sneaking (<c>FUN_140857500</c>, Verified constant).</summary>
    public const float HearOdds = 0.75f;

    /// <summary>How far a character moving at <paramref name="speed"/> (its top speed <paramref name="maxSpeed"/>) is heard; 0 when it stands still (the original hears only movers, Observed).</summary>
    public static float NoiseRange(float speed, float maxSpeed)
    {
        if (!(speed > 0.01f)) return 0;
        float t = maxSpeed > 0 ? Math.Clamp(speed / maxSpeed, 0, 1) : 1;
        return NoiseNear + (NoiseFar - NoiseNear) * t;
    }

    /// <summary>The original's chance to hear a character at <paramref name="distance"/> with noise range <paramref name="noise"/>: 0.75 x (1 - d^2 / N^2), rolled each check.</summary>
    public static float HearChance(float distance, float noise) => noise > 0 && distance < noise ? HearOdds * (1 - distance * distance / (noise * noise)) : 0;

    /// <summary>
    /// The building rules of the hearing check (<c>SensoryData::canHear</c> <c>FUN_140857500</c> steps 3, 4, 6 and 8, <b>Verified</b>): the factor on the target's noise range, 0 when it
    /// cannot be heard. Inside another building than the listener, or on another floor of the same one, it is heard only in combat mode; from one building into another never; across
    /// floors at most one apart. Another interior halves the range, and again when the building (the target's, else the listener's) has no open door. <paramref name="sure"/>: in combat
    /// mode in the listener's place and on its floor, heard whenever the chance is above 0. Engine: the floor test skips the original's cavernous-building exception.
    /// </summary>
    static float HearPlace(World world, int listener, int target, Vector3 listenerAt, Vector3 targetAt, bool combat, out bool sure)
    {
        sure = false;
        var sight = world.Sight;
        int a = sight.PlaceOf(listener), b = sight.PlaceOf(target);
        if (a != b)
        {
            if (!combat || a >= 0 && b >= 0) return 0;
            return sight.BuildingOf(b >= 0 ? target : listener) is { } building && !world.Fixtures.HasOpenDoor(building) ? 0.25f : 0.5f;
        }
        if (a >= 0 && sight.BuildingOf(listener) is { } same && sight.FloorAt(same, listenerAt) is var fa && sight.FloorAt(same, targetAt) is var fb && fa != fb)
            return combat && Math.Abs(fa - fb) <= 1 ? 1 : 0;
        sure = combat;
        return 1;
    }

    /// <summary>A character's daylight sight range R: <paramref name="sight"/> x its race's <c>vision range mult</c> x the weather's factor (<see cref="WeatherSystem.SightFactor"/>).</summary>
    public static float RangeOf(CharacterCold cold, float sight) => sight * (cold.Race?.VisionRangeMult ?? 1) * WeatherSystem.SightFactor(cold);

    struct Slot
    {
        public int Generation;
        public bool Tracked;
        public byte Task;
        /// <summary>The tick the running chase began (-1: none), the tick and place its target was last seen or heard, and whether the last look sensed it.</summary>
        public long ChaseStart, SensedTick;
        public Vector2 SensedAt;
        public bool Sensed;
        /// <summary>The looker's spot meters on sneaking characters, in target slot order (null: none); written by its own partition only.</summary>
        public List<Spot>? Spots;
    }

    /// <summary>
    /// One spot meter (the original's <c>SensoryData::SpottingPeopleMgr::Spot</c>): it fills by dt / <see cref="Orders.SpotSeconds"/> while the looker sees the sneaking target,
    /// empties by dt / <see cref="Orders.SpotForgetSeconds"/> while it does not, and is dropped at 0. Above 1 the target counts as seen.
    /// </summary>
    struct Spot
    {
        public int Target, Generation;
        public float Value;
    }

    /// <summary>A sneaking target the looker sees this scan, with its spot time.</summary>
    readonly record struct Sighting(int Target, int Generation, float Seconds);

    [ThreadStatic] static List<Sighting>? sightings;

    Slot[] slots = [];
    int size;
    readonly List<AggroEntry> log = [];
    readonly HashSet<int> taken = [];
    long takenTick = -1;

    public AggroSettings Settings { get; } = settings ?? new AggroSettings();
    public FactionRelations Relations { get; } = relations;
    /// <summary>The fights started since the last <see cref="TakeLog"/> (with <see cref="AggroSettings.RecordLog"/>).</summary>
    public IReadOnlyList<AggroEntry> Log => log;

    /// <summary>Takes the log (the host, between ticks).</summary>
    public AggroEntry[] TakeLog()
    {
        AggroEntry[] all = [.. log];
        log.Clear();
        return all;
    }

    /// <summary>The fights this system started so far.</summary>
    public long Started { get; private set; }

    /// <summary>The fights, for <see cref="Ai.AiStance.ProtectSquad"/> (null: that stance joins any fighting enemy, as <see cref="Ai.AiStance.Defensive"/>).</summary>
    public CombatSystem? Combat { get; set; }

    /// <summary>
    /// The squads' memories (the dialogue system's): a squad's short-term TEMP_ENEMY tag on a character of another faction makes it an enemy of every member whatever the relations say, and
    /// TEMP_ALLY unmakes an enemy (<c>FUN_14079c950</c>, docs/game/dialogue.md "Squad memory"). Null: relations only.
    /// </summary>
    public Dialogue.SquadMemory? Memory { get; set; }

    public void Schedule(World world)
    {
        Sync(world.Characters);
        SyncSquadFights(world.Characters);
    }

    /// <summary>(squad, slot) for every character fighting a member of that squad or targeted by one: whom PROTECT_OWN_SQUAD joins against (read only during the looks).</summary>
    readonly HashSet<(int Squad, int Slot)> squadFights = [];

    void SyncSquadFights(CharacterTable table)
    {
        squadFights.Clear();
        if (Combat is null) return;
        var next = table.Next;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive || next[i].Task != (byte)CharacterTask.Attack) continue;
            var st = Combat.StateOf(new CharacterId(i, next[i].Generation));
            if (st.TargetSlot is not (>= 0 and var t) || !next[t].Alive || next[t].Generation != st.TargetGeneration) continue;
            if (table.Cold(i) is { SquadId: >= 0 } a) squadFights.Add((a.SquadId, t));   // a mate's target
            if (table.Cold(t) is { SquadId: >= 0 } b) squadFights.Add((b.SquadId, i));   // a mate's attacker
        }
    }

    void Sync(CharacterTable table)
    {
        int n = table.HighWater;
        if (slots.Length < n)
        {
            int old = slots.Length;
            Array.Resize(ref slots, Math.Max(n, slots.Length * 2));
            for (int i = old; i < slots.Length; i++) slots[i] = new Slot { Generation = -1, Task = NoTask, ChaseStart = -1 };
        }
        size = Math.Max(size, n);
        var next = table.Next;
        if (present.Length != Relations.Count) { present = new bool[Relations.Count]; hasEnemy = new bool[Relations.Count]; }
        Array.Clear(present);
        for (int i = 0; i < n; i++)
        {
            int generation = next[i].Alive ? next[i].Generation : -1;
            if (slots[i].Generation != generation) slots[i] = new Slot { Generation = generation, Task = NoTask, ChaseStart = -1 };
            if (generation >= 0 && table.Cold(i) is { Medical: not null } c && Relations.Contains(c.Faction))
            {
                present[c.Faction] = true;
            }
        }
        // Which factions have an enemy among the factions present: a peaceful crowd skips its looks. Worked out again only when the set changes.
        if (present.AsSpan().SequenceEqual(presentBefore) && Relations.Changes == changesBefore) return;
        presentBefore = (bool[])present.Clone();
        changesBefore = Relations.Changes;
        presentList.Clear();
        for (int f = 0; f < present.Length; f++) if (present[f]) presentList.Add(f);
        foreach (int f in presentList)
        {
            hasEnemy[f] = false;
            foreach (int g in presentList)
                if (IsEnemy(Relations, f, g)) { hasEnemy[f] = true; break; }
        }
    }

    bool[] present = [], presentBefore = [], hasEnemy = [];
    long changesBefore = -1;
    readonly List<int> presentList = [];

    // ------------------------------------------------------------------ look

    struct Scan
    {
        public CharacterTable Table;
        public FactionRelations Relations;
        public World World;
        public Dialogue.SquadMemory? Memory;
        public int Self, Faction, Squad;
        /// <summary>The squad fights (<see cref="squadFights"/>; null without a combat system).</summary>
        public HashSet<(int Squad, int Slot)>? SquadFights;
        public Vector3 Eye;
        public CharacterCold? Cold;
        public float Yaw, Range;
        /// <summary>The looker's outdoor sight (race multiplier applied) for the places and lights (<see cref="SightMap.Sees"/>); <see cref="Range"/> is the farthest it can see anyone.</summary>
        public float Outdoor;
        public SightMap Sight;
        public ulong Seed, Key, Tick;
        /// <summary>The looker's AI stance (<see cref="Ai.AiStance.Aggressive"/> for a character the AI does not run) and, for the territorial one, its home.</summary>
        public Ai.AiStance Stance;
        /// <summary>A campaign squad's assault targets (<see cref="Squad.War"/>; none for others).</summary>
        public WarTargets War;
        /// <summary>The faction a hired squad works for (<c>Contract_HiredAlly</c> 12, its employer's) or defends (<c>Contract_DefendPlayerBase</c> 15, the hirer's); -1: none. Its members are never enemies, nor, hired, its allies.</summary>
        public int Employer;
        public bool EmployerAllies;
        public Vector2 Home;
        public float HomeR2;
        public int Best;
        public float BestD2;
        /// <summary>The best target's score (<see cref="Combat.TargetScore"/>).</summary>
        public float BestScore;
        /// <summary>The combat state the score reads (null: hand-built worlds without one, every candidate counts as unfought).</summary>
        public Combat.CombatSystem? Combat;
        /// <summary>The looker: one of the player's, on Hold (and its hold point), crawling, an animal.</summary>
        public bool Player, Hold, Prone, Animal;
        public Vector2 HoldPoint;
        /// <summary>The looker's spot meters (read only during the query) and the sneakers it sees this scan.</summary>
        public List<Spot>? Spots;
        public List<Sighting> Sightings;
        public EffectBuffer Effects;
        /// <summary>Characters gathered this scan, and those taken past the cap (<see cref="GatherCap"/>, <see cref="GatherExtra"/>).</summary>
        public int Gathered, Extra;
    }

    /// <summary>The sensed-list gather (<c>FUN_14085a8d0</c>, <b>Verified</b> literals): up to 40 characters within range; past that, up to 20 more within 250.</summary>
    public const int GatherCap = 40, GatherExtra = 20;
    public const float GatherNear = 250;

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        int every = Math.Max(1, Settings.ScanEveryTicks);
        int phase = (int)(world.Tick % every);
        int first = part.Start + ((phase - part.Start % every) % every + every) % every;
        for (int i = first; i < part.End; i += every)
        {
            if (!prev[i].Alive || i >= slots.Length) continue;
            byte task = next[i].Task;
            if (task == (byte)CharacterTask.Attack && slots[i].ChaseStart >= 0)
            {
                SenseQuarry(world, table, i, ref slots[i]);
                continue;
            }
            if (task is (byte)CharacterTask.Attack or (byte)CharacterTask.Treat || task == (byte)CharacterTask.Rest && !AiSleeper(table.Cold(i))) continue;
            if (table.Cold(i) is not { InCombat: false, Fleeing: false, IsImprisoned: false, SlaveState: not 1, Fighter.CanAttack: true, Medical.Incapacitated: false } cold
                || !Relations.Contains(cold.Faction) || cold.IsPlayer && !HuntsEnemies(cold, task) && !HoldsGround(cold, task)) continue;
            var stance = cold.Ai?.Stance ?? Ai.AiStance.Aggressive;
            var war = cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { War.IsSet: true } assaulting ? assaulting.War : WarTargets.None;
            if (stance == Ai.AiStance.Passive || !hasEnemy[cold.Faction] && stance != Ai.AiStance.AggressiveNeutrals && !war.IsSet && !(Memory?.AnyTempEnemy(cold.SquadId) ?? false)) continue;
            float outdoor = RangeOf(cold, Settings.SightRange);
            if (!(outdoor > 0)) continue;
            float range = world.Sight.MaxRange(i, outdoor);
            var scan = new Scan
            {
                Table = table, Relations = Relations, World = world, Memory = Memory, Self = i, Faction = cold.Faction, Squad = cold.SquadId, SquadFights = Combat is null ? null : squadFights,
                Eye = prev[i].Position, Cold = cold, Yaw = prev[i].Yaw, Range = range, Outdoor = outdoor, Sight = world.Sight, Best = -1, BestD2 = float.MaxValue, BestScore = float.NegativeInfinity,
                Combat = Combat, Player = cold.IsPlayer, Hold = Orders.Holding(cold) && cold.Fighter.Crossbow is null, Prone = cold.Medical.Prone, Animal = cold.Fighter.IsAnimal,
                HoldPoint = Orders.HoldPointOf(cold, prev[i].Position),
                Seed = world.Seed, Key = Rng.Key(new CharacterId(i, prev[i].Generation)), Tick = (ulong)world.Tick,
                Stance = stance, War = war,
                Employer = EmployerOf(world, cold, out bool allies), EmployerAllies = allies,
                Home = cold.Ai?.Squad.HomeCentre ?? default, HomeR2 = cold.Ai is { } mind ? mind.Squad.HomeRadius * mind.Squad.HomeRadius : 0,
                Spots = slots[i].Spots, Sightings = sightings ??= [], Effects = effects,
            };
            scan.Sightings.Clear();
            world.Grid.Query(scan.Eye.X, scan.Eye.Z, MathF.Max(range, NoiseFar), ref scan, Visit);
            if (scan.Best >= 0) effects.Emit(i, scan.Best, SightEffect, MathF.Sqrt(scan.BestD2));
            if (scan.Spots is not null || scan.Sightings.Count > 0) slots[i].Spots = AdvanceSpots(scan.Spots, scan.Sightings, every * world.TickSeconds, table);
        }
    }

    /// <summary>
    /// A chaser's look at its own fight target (the original's sense entry that <c>ChaseMode</c> reads, docs/game/combat.md "Leaving a fight"): seen by the sight test, a sneaking
    /// target with its stealth but without the spot meter (engine choice), else heard by the noise roll. Own senses only: whether squad-shared sightings count is not decoded.
    /// Writes only the chaser's own slot.
    /// </summary>
    void SenseQuarry(World world, CharacterTable table, int i, ref Slot s)
    {
        if (Combat is null) return;
        var prev = table.Previous;
        var st = Combat.StateOf(new CharacterId(i, prev[i].Generation));
        int t = st.TargetSlot;
        if (t < 0 || t >= table.HighWater || !prev[t].Alive || prev[t].Generation != st.TargetGeneration || table.Cold(i) is not { } cold) return;
        ref readonly var other = ref prev[t];
        var tc = table.Cold(t);
        float stealth = tc is not null && (tc.Orders & CharacterOrders.Sneak) != 0 ? Orders.Stealth(tc) : -1;
        float outdoor = RangeOf(cold, Settings.SightRange);
        bool sensed = outdoor > 0 && world.Sight.Sees(i, t, prev[i].Position, prev[i].Yaw, other.Position, outdoor, stealth) > 0;
        if (!sensed)
        {
            float d = Vector3.Distance(prev[i].Position, other.Position);
            float speed = new Vector2(other.Velocity.X, other.Velocity.Z).Length();
            bool sure = false;
            float place = tc is null ? 1 : HearPlace(world, i, t, prev[i].Position, other.Position, tc.InCombat, out sure);
            float p = !(place > 0) ? 0 : stealth >= 0
                ? speed > 0.01f ? Orders.SneakHearChance(d, Orders.SneakNoise(stealth, Noise(table, t)) * place, stealth) : 0
                : HearChance(d, NoiseRange(speed, other.MaxSpeed) * Noise(table, t) * place);
            sensed = p > 0 && (sure || Rng.Float(Rng.Hash(world.Seed, Rng.Key(new CharacterId(i, prev[i].Generation)), RngPurpose.ChaseSense, ((ulong)world.Tick << 20) ^ (ulong)(uint)t)) < p);
        }
        s.Sensed = sensed;
        if (!sensed) return;
        s.SensedTick = world.Tick;
        s.SensedAt = new Vector2(other.Position.X, other.Position.Z);
    }

    /// <summary>
    /// Moves a looker's spot meters on by one scan of <paramref name="dt"/> seconds: a seen sneaker's meter fills by dt / its spot time (a new one starts at 0), the others
    /// empty by dt / <see cref="Orders.SpotForgetSeconds"/>; empty meters and gone targets are dropped. Returns the list in target slot order (null when empty).
    /// </summary>
    static List<Spot>? AdvanceSpots(List<Spot>? spots, List<Sighting> seen, float dt, CharacterTable table)
    {
        spots ??= [];
        foreach (var x in seen)
        {
            int k = spots.FindIndex(p => p.Target == x.Target && p.Generation == x.Generation);
            if (k < 0) spots.Add(new Spot { Target = x.Target, Generation = x.Generation, Value = dt / x.Seconds });
            else spots[k] = spots[k] with { Value = spots[k].Value + dt / x.Seconds };
        }
        for (int k = spots.Count - 1; k >= 0; k--)
        {
            var p = spots[k];
            if (!table.Previous[p.Target].Alive || table.Previous[p.Target].Generation != p.Generation) { spots.RemoveAt(k); continue; }
            if (seen.Exists(x => x.Target == p.Target)) continue;
            p.Value -= dt / Orders.SpotForgetSeconds;
            if (p.Value <= 0) spots.RemoveAt(k);
            else spots[k] = p;
        }
        if (spots.Count == 0) return null;
        spots.Sort((a, b) => a.Target.CompareTo(b.Target));
        return spots;
    }

    /// <summary>Whether the NPC in slot <paramref name="looker"/> has spotted the sneaker <paramref name="target"/> (its spot meter is full; serial phases).</summary>
    public bool Spotted(int looker, CharacterId target) => looker < slots.Length && SpotOf(slots[looker].Spots, target.Slot, target.Generation) > 1;

    /// <summary>
    /// Whether the sneaker <paramref name="target"/> is unseen (Character +0xe8 = 1, docs/game/perception.md "Spotting a sneaker"): not fighting, and no looker's spot meter on it is
    /// filling or full (red or yellow); serial phases.
    /// </summary>
    public bool Unseen(CharacterTable table, CharacterId target)
    {
        if (table.Cold(target.Slot) is { InCombat: true }) return false;
        for (int k = 0; k < size && k < slots.Length; k++)
            if (SpotOf(slots[k].Spots, target.Slot, target.Generation) > 0) return false;
        return true;
    }

    /// <summary>The looker's spot meter on <paramref name="slot"/> before this scan (0: none).</summary>
    static float SpotOf(List<Spot>? spots, int slot, int generation)
    {
        if (spots is null) return 0;
        foreach (var p in spots) if (p.Target == slot && p.Generation == generation) return p.Value;
        return 0;
    }

    /// <summary>The faction <paramref name="cold"/>'s squad is contracted to (<see cref="Scan.Employer"/>): the contract target's (the hirer) for signals 12 and 15; -1 otherwise.</summary>
    static int EmployerOf(World world, CharacterCold cold, out bool allies)
    {
        allies = false;
        var table = world.Characters;
        if (cold.Ai?.Squad.Ai is not { Contract.Signal: 12 or 15 } board || board.MissionTarget is not { IsNone: false } id || id.Slot >= table.HighWater
            || table.Previous[id.Slot].Generation != id.Generation || table.Cold(id.Slot) is not { } h) return -1;
        allies = board.Contract.Signal == 12;
        return h.Faction;
    }

    static void Visit(int slot, ref Scan s)
    {
        if (slot == s.Self) return;
        ref readonly var other = ref s.Table.Previous[slot];
        float d2 = Vector3.DistanceSquared(other.Position, s.Eye);
        if (d2 > s.Range * s.Range && d2 > NoiseFar * NoiseFar) return;
        if (s.Gathered < GatherCap) s.Gathered++;
        else if (s.Extra < GatherExtra && d2 <= GatherNear * GatherNear) s.Extra++;
        else return;
        // A caged prisoner is nobody's target, an owned slave nobody's but its owner's enemies' (engine choice: the original's sensed-list filter is not decoded; town cages
        // hold enemies of their guards, and slaves of a hostile faction, such as Heng's Outlaws, stand among them).
        if (s.Table.Cold(slot) is not { Medical.Incapacitated: false, IsImprisoned: false } c || c.SlaveState == 1 && !IsEnemy(s.Relations, s.Faction, c.SlaveOwner)) return;
        bool sneaking = (c.Orders & CharacterOrders.Sneak) != 0;
        // Wanted characters are the arrest system's (Crime.ArrestSystem): the police spot, fine or arrest them there. A working disguise stands in for the target's faction
        // (slot 125, docs/game/perception.md "Sense tags"; Crime.ArrestSystem.SeenFaction).
        int seenAs = Crime.ArrestSystem.SeenFaction(c);
        bool enemy = s.Stance != Ai.AiStance.Passive
            && (IsEnemy(s.Relations, s.Faction, seenAs) || s.Stance == Ai.AiStance.AggressiveNeutrals && IsNeutral(s.Relations, s.Faction, seenAs) || WarEnemy(s.War, s.Faction, c, other.Position));
        // A campaign force's member is sensed as NEUTRAL, not ENEMY (FUN_1408580d0 over FUN_140796240, docs/game/perception.md "Sense tags"): a raid walks up to talk first.
        // The looker still takes it when it attacks neutrals or its own campaign targets it; a TEMP_ENEMY tag (below) makes it an enemy again.
        if (enemy && s.Stance != Ai.AiStance.AggressiveNeutrals && !WarEnemy(s.War, s.Faction, c, other.Position) && c.SquadId >= 0
            && s.World.Squads.Find(c.SquadId) is { PlatoonId: >= 0 } force && s.World.Platoons.Find(force.PlatoonId)?.Campaign is not null) enemy = false;
        // The squads' temporary tags (FUN_14079c950, other faction only): the looker's squad holds TEMP_ENEMY on the target, or, for a player-faction looker, the target's squad on it,
        // makes an enemy; the looker's squad holding TEMP_ALLY on the target unmakes one.
        if (s.Stance != Ai.AiStance.Passive && s.Memory is { } memory && c.Faction != s.Faction)
        {
            var target = new CharacterId(slot, other.Generation);
            if (s.Squad >= 0 && memory.HasShort(s.World, s.Squad, target, Dialogue.MemoryTag.TempEnemy)
                || s.World.Player.Faction == s.Faction && c.SquadId >= 0 && memory.HasShort(s.World, c.SquadId, new CharacterId(s.Self, s.Table.Previous[s.Self].Generation), Dialogue.MemoryTag.TempEnemy)) enemy = true;
            else if (enemy && s.Squad >= 0 && memory.HasShort(s.World, s.Squad, target, Dialogue.MemoryTag.TempAlly)) enemy = false;
        }
        // A contract's employer (Contract_HiredAlly / Contract_DefendPlayerBase assessNeutral, and HiredAlly's assessEnemy for the employer's allies, Verified): TEMP_ALLY, not an enemy.
        if (enemy && s.Employer >= 0 && (c.Faction == s.Employer || s.EmployerAllies && s.Relations.Contains(c.Faction) && s.Relations.IsAlly(s.Employer, c.Faction))) enemy = false;
        if (!enemy) return;
        // One it found no path to lately is never picked (isPathImpossible in the attack-on-sight finder's score, docs/game/combat.md "Target score"): else a guard shut in with its
        // enemy (a cage, a locked room) takes it as target again every look and never turns on anyone it can reach.
        if (s.Cold is { } looker && PursuitSystem.PathImpossible(looker, new CharacterId(slot, other.Generation), (long)s.Tick)) return;
        // The AI's stances (docs/simulation.md "AI", Combat goals): the defensive one joins fights only, the territorial one keeps to its home.
        if (s.Stance == Ai.AiStance.Defensive && !c.InCombat) return;
        if (s.Stance == Ai.AiStance.ProtectSquad && (s.SquadFights is { } fights ? s.Squad < 0 || !fights.Contains((s.Squad, slot)) : !c.InCombat)) return;
        if (s.Stance == Ai.AiStance.Territorial && Vector2.DistanceSquared(new Vector2(other.Position.X, other.Position.Z), s.Home) > s.HomeR2) return;
        // The target score (Combat.TargetScore; Taunt and Passive weigh in there), after the Hold post test for a holding looker. Strictly better only: the grid visits a cell's
        // slots in ascending order, so a tie keeps the lower slot within a cell; across cells the order is the grid's fixed one. A sneaker that scores lower still gets its spot meter.
        float score = Score(ref s, slot, c, d2) ?? float.NegativeInfinity;
        bool better = score > s.BestScore;
        if (!better && !sneaking) return;
        // A sneaking target is harder to see and to hear (the Sneak order; docs/game/perception.md "Sight", "Spotting a sneaker" and "Hearing"): in view it fills the
        // looker's spot meter and counts as seen only once the meter was full before this scan.
        float stealth = sneaking ? Orders.Stealth(c) : -1;
        bool seen;
        if (sneaking)
        {
            float v = s.Sight.Sees(s.Self, slot, s.Eye, s.Yaw, other.Position, s.Outdoor, stealth);
            bool full = SpotOf(s.Spots, slot, other.Generation) > 1;
            if (v > 0) s.Sightings.Add(new Sighting(slot, other.Generation, Orders.SpotSeconds(v, stealth)));
            if (d2 <= s.Range * s.Range)
                s.Effects.Emit(s.Self, slot, ExposureEffect, (float)(full && v > 0 ? StealthExposure.Detected : v > 0 ? StealthExposure.Noticed : StealthExposure.Near));
            if (!better) return;
            seen = full && v > 0;
        }
        else seen = s.Sight.Sees(s.Self, slot, s.Eye, s.Yaw, other.Position, s.Outdoor) > 0;
        if (!seen)
        {
            float speed = new Vector2(other.Velocity.X, other.Velocity.Z).Length();
            float place = HearPlace(s.World, s.Self, slot, s.Table.Previous[s.Self].Position, other.Position, c.InCombat, out bool sure);
            float p = !(place > 0) ? 0 : stealth >= 0
                ? speed > 0.01f ? Orders.SneakHearChance(MathF.Sqrt(d2), Orders.SneakNoise(stealth, Noise(s.Table, slot)) * place, stealth) : 0
                : HearChance(MathF.Sqrt(d2), NoiseRange(speed, other.MaxSpeed) * Noise(s.Table, slot) * place);
            if (!(p > 0) || !sure && Rng.Float(Rng.Hash(s.Seed, s.Key, RngPurpose.Sense, (s.Tick << 20) ^ (ulong)(uint)slot)) >= p) return;
        }
        s.Best = slot;
        s.BestD2 = d2;
        s.BestScore = score;
    }

    /// <summary>The attack-on-sight score of <paramref name="c"/> in <paramref name="slot"/> (goal 16's finder picks by <c>FUN_14059e300</c>); null when dropped.</summary>
    static float? Score(ref Scan s, int slot, CharacterCold c, float d2)
    {
        ref readonly var other = ref s.Table.Previous[slot];
        var fight = s.Combat?.StateOf(new CharacterId(slot, other.Generation)) ?? Meitou.Simulation.Combat.CombatSlot.Fresh(other.Generation);
        if (s.Hold && !Orders.HoldPost(s.Eye, s.HoldPoint, other.Position, other.Velocity, false, fight.TargetSlot == s.Self)) return null;
        var candidate = new Meitou.Simulation.Combat.TargetCandidate
        {
            Staggered = fight.StumbleClip >= 0 && (int)s.Tick < fight.StunUntil,
            Attackers = s.Combat?.AttackerCount(s.World, slot) ?? 0,
            Taunt = (c.Orders & CharacterOrders.Taunt) != 0,
            Passive = (c.Orders & CharacterOrders.Passive) != 0,
            Distance = MathF.Sqrt(d2),
            Fighting = c.InCombat,
            Gunner = c.Gunning is not null,
            Crippled = c.Medical!.Crawling || c.Fighter is not { CanAttack: true },
            Peaceful = !c.InCombat && !IsEnemy(s.Relations, c.Faction, s.Faction),
            GettingUp = c.GettingUpUntil > (long)s.Tick,
        };
        return Meitou.Simulation.Combat.TargetScore.Score(candidate, s.Player, s.Hold, s.Prone, s.Animal);
    }

    /// <summary>
    /// Whether a character of faction <paramref name="mine"/> picks a fight with one of <paramref name="theirs"/>: hostile one way (mine to theirs, -30 or lower),
    /// and neither lists the other in <c>coexistence</c> (engine choice: the flag's effect in the original is <b>Unknown</b>, so either side's is enough).
    /// Same faction and allies are never enemies (an ally is at 50 or above, so not hostile).
    /// </summary>
    /// <summary>Neither an ally (nor the same faction) nor hostile: whom ATTACK_ENEMIES_AND_NEUTRALS adds to the enemies.</summary>
    public static bool IsNeutral(FactionRelations relations, int mine, int theirs) =>
        relations.Contains(theirs) && !relations.IsAlly(mine, theirs) && !relations.IsHostile(mine, theirs);

    /// <summary>A character the AI put to bed or sat down (<see cref="Ai.AiBehaviour.Sleep"/>, <see cref="Ai.AiBehaviour.Sit"/>): unlike one resting by order, it can be roused into a fight.</summary>
    static bool AiSleeper(CharacterCold? cold) => cold?.Ai is { Behaviour: Ai.AiBehaviour.Sleep or Ai.AiBehaviour.Sit };

    /// <summary>
    /// A campaign assault's extra enemy (<see cref="WarTargets"/>): of the target faction, or with <c>Everyone</c> anyone not of its own faction in the target town except animals and
    /// bandits (<c>Package_WarAssault</c> assessNeutral <c>FUN_14028a3f0</c>, <b>Verified</b>; it also skips fake factions, and takes people outside every town too).
    /// </summary>
    static bool WarEnemy(WarTargets war, int mine, CharacterCold c, Vector3 at) =>
        war.IsSet && c.Faction != mine && (c.Faction == war.Faction || war.Everyone && c.Fighter?.IsAnimal != true && c.NpcClass != FundamentalType.Bandit && Vector2.DistanceSquared(new Vector2(at.X, at.Z), war.Centre) <= war.Radius * war.Radius);

    public static bool IsEnemy(FactionRelations relations, int mine, int theirs) =>
        relations.Contains(theirs) && relations.IsHostile(mine, theirs) && !relations.Coexists(mine, theirs) && !relations.Coexists(theirs, mine);

    // ------------------------------------------------------------------ decide

    public void Apply(World world, in Effect e)
    {
        if (e.Kind == ExposureEffect)
        {
            // The sneaker keeps the highest exposure any looker gave it this tick.
            if (world.Characters.Cold(e.Target) is not { } sneaker) return;
            var level = (StealthExposure)(int)e.Amount;
            if (sneaker.StealthExposureTick != world.Tick || level > sneaker.StealthExposure) sneaker.StealthExposure = level;
            sneaker.StealthExposureTick = world.Tick;
            if (level >= StealthExposure.Noticed) Watch(world, sneaker, world.Characters.IdOf(e.Source), level);
            return;
        }
        if (e.Kind != SightEffect) return;
        if (takenTick != world.Tick) { taken.Clear(); takenTick = world.Tick; }
        var table = world.Characters;
        var next = table.Next;
        int i = e.Source, t = e.Target;
        if (!Free(table, i, taken) || !next[t].Alive || table.Cold(t) is not { Medical.Incapacitated: false } target) return;
        var fighters = new List<CharacterId> { table.IdOf(i) };
        taken.Add(i);
        JoinMates(world, table, i, t, fighters);
        var targetId = table.IdOf(t);
        world.Defer(new AttackOrder(fighters, targetId) { Tick = world.Tick + 1 });
        Started++;
        if (world.HasSoundSink) foreach (var fighter in fighters) world.EmitSound(new SoundEvent(SoundKind.Alert, fighter, targetId, next[fighter.Slot].Position));   // the fighters' alert voices
        if (Settings.RecordLog) log.Add(new AggroEntry(world.Tick, fighters[0], targetId, table.Cold(i)!.Faction, target.Faction, e.Amount, fighters.Count - 1));
    }

    /// <summary>How long a watcher stays on a sneaker's list after its last look (<c>FUN_1408586d0</c>: entries older than 4 s are dropped, Verified literal).</summary>
    public const float WatcherSeconds = 4;

    /// <summary>Puts <paramref name="looker"/> on the sneaker's watcher list at <paramref name="level"/> (or updates it) and drops lapsed entries.</summary>
    static void Watch(World world, CharacterCold sneaker, CharacterId looker, StealthExposure level)
    {
        var list = sneaker.Watchers ??= [];
        long lapse = world.Tick - (long)MathF.Ceiling(WatcherSeconds / world.TickSeconds);
        list.RemoveAll(w => w.Tick < lapse || w.Looker == looker);
        list.Add(new StealthWatcher(looker, level, world.Tick));
    }

    /// <summary>
    /// The looker's free squad mates join when the target is within twice their own range R (in the squad's member order). The original shares a squad's sightings
    /// (<c>GroupSense</c>): a member counts a character a mate saw or heard as seen within 2 R, with no view cone or light test (<c>FUN_140857b10</c>, Observed).
    /// </summary>
    void JoinMates(World world, CharacterTable table, int slot, int target, List<CharacterId> fighters)
    {
        var squad = table.Cold(slot)!.SquadId >= 0 ? world.Squads.Find(table.Cold(slot)!.SquadId) : null;
        if (squad is null) return;
        var at = table.Next[target].Position;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || s == slot || !Free(table, s, taken)) continue;
            float r = 2 * RangeOf(table.Cold(s)!, Settings.SightRange);
            if (Vector3.DistanceSquared(table.Next[s].Position, at) > r * r) continue;
            fighters.Add(m);
            taken.Add(s);
        }
    }

    static bool Free(CharacterTable table, int slot, HashSet<int> taken)
    {
        if (taken.Contains(slot) || !table.Next[slot].Alive) return false;
        byte task = table.Next[slot].Task;
        if (task is (byte)CharacterTask.Attack or (byte)CharacterTask.Treat || task == (byte)CharacterTask.Rest && !AiSleeper(table.Cold(slot))) return false;
        return table.Cold(slot) is { InCombat: false, Fleeing: false, IsImprisoned: false, Fighter.CanAttack: true, Medical.Incapacitated: false, Gunning: null, Ai: null or { Behaviour: not Ai.AiBehaviour.Turret } } cold
            // a carrier fights like anyone (docs/game/carry-loot.md "A carrier in a fight"); a turret guard keeps to its turret (task 146: only a melee attacker takes it off)
            && (!cold.IsPlayer || HuntsEnemies(cold, task) || HoldsGround(cold, task));
    }

    /// <summary>
    /// Whether one of the player's characters attacks on sight through the Hold order: Hold adds goal 16 (standing order 12), whose desire is 0 while sneaking, Passive or
    /// not standing; it is a priority-3 goal, so only without an order (docs/game/combat.md "Fight lifecycle and orders"). Its targets pass the Hold post test (<see cref="Orders.HoldPost"/>).
    /// </summary>
    static bool HoldsGround(CharacterCold cold, byte task) =>
        task == (byte)CharacterTask.Idle && Orders.Holding(cold) && (cold.Orders & CharacterOrders.Passive) == 0 && cold.Medical is { Prone: false };

    /// <summary>
    /// Whether one of the player's characters looks for enemies like an NPC: it has the job 16 "Attacking enemies" (Shift with an attack; it runs with the jobs switch off,
    /// docs/game/ai-tasks.md "Perma-jobs"), is free for it (<see cref="Production.PermaJobs.FreeFor"/>) and not in non-combat mode. The job's finder is the original's attack on sight
    /// (<c>FUN_14059f3a0</c>, "Combat goals"), which this system's look stands in for.
    /// </summary>
    static bool HuntsEnemies(CharacterCold cold, byte task) =>
        (cold.Orders & CharacterOrders.Passive) == 0 && Production.PermaJobs.FreeFor(cold, (CharacterTask)task, Production.PermaJobs.IndexOf(cold, Meitou.Data.Gameplay.Ai.TaskType.ATTACK_ENEMIES));

    // ------------------------------------------------------------------ chase give-up and stand down

    /// <summary>No give-up before this many game seconds of a chase: the original's 5 game minutes (<c>ChaseMode</c>, Verified literal; 1 game hour = 1200/11 s).</summary>
    public const float GiveUpGateSeconds = 5f / 60 * 1200 / 11;
    /// <summary>A target unsensed for longer than this is given up: the original's 12 sense-age units of hours x 3600 / 33 x 0.5 (<c>FUN_140852150</c>), i.e. 24 s.</summary>
    public const float GiveUpSenseSeconds = 24;
    /// <summary>A chaser within this of where it last sensed an unsensed target gives it up (the original's squared 3600, Verified literal).</summary>
    public const float GiveUpReach = 60;

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        Sync(table);   // characters spawned this tick
        var next = table.Next;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive) continue;
            var cold = table.Cold(i)!;
            // The player's characters never give up a chase (the original's gate: the chaser's faction is not the player's); one with the attacking-enemies job goes back
            // to following after its fight.
            bool player = cold.IsPlayer;
            if (player && Production.PermaJobs.IndexOf(cold, Meitou.Data.Gameplay.Ai.TaskType.ATTACK_ENEMIES) < 0) continue;
            ref var s = ref slots[i];
            if (n.Task == (byte)CharacterTask.Attack)
            {
                s.Tracked = true;
                if (!player) Chase(world, table, i, ref s, cold);
                continue;
            }
            s.ChaseStart = -1;
            if (!s.Tracked)
            {
                s.Task = n.Task;   // the routine to go back to after a fight
                continue;
            }
            s.Tracked = false;
            if (cold.Medical is { Incapacitated: true } || cold.InCombat || n.Task != (byte)CharacterTask.Idle) continue;   // down (it wakes into wandering) or busy with something else
            MovementSystem.Stop(table, i);
            n.Task = player ? (s.Task == (byte)CharacterTask.Follow ? s.Task : (byte)CharacterTask.Idle)
                : s.Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Follow or (byte)CharacterTask.Idle ? s.Task
                : s.Task == (byte)CharacterTask.GoTo ? (byte)CharacterTask.Idle : (byte)CharacterTask.Wander;
            n.TaskTime = 0;
        }
    }

    /// <summary>
    /// The original's chase give-up (<c>ChaseMode</c>, docs/game/combat.md "Leaving a fight"): after <see cref="GiveUpGateSeconds"/> of a chase, a target the chaser's last look
    /// did not sense is given up when it was last sensed more than <see cref="GiveUpSenseSeconds"/> ago or the chaser stands within <see cref="GiveUpReach"/> of where it was.
    /// The chaser stops and says so to the target (event 60 GIVE_UP_CHASE). Engine choice: the original tests this only in its path chase (fight state 11), Meitou in every
    /// running chase (<see cref="CharacterCold.CombatRunning"/>), since how the original lets go on open ground is Unknown; a target still sensed is chased on either way.
    /// </summary>
    void Chase(World world, CharacterTable table, int i, ref Slot s, CharacterCold cold)
    {
        var next = table.Next;
        if (!cold.CombatRunning || Combat?.StateOf(table.IdOf(i)) is not { TargetSlot: >= 0 and var t } st || t >= table.HighWater || !next[t].Alive
            || next[t].Generation != st.TargetGeneration || table.Cold(t) is { Medical.Incapacitated: true })
        {
            s.ChaseStart = -1;
            return;
        }
        var target = new Vector2(next[t].Position.X, next[t].Position.Z);
        if (s.ChaseStart < 0)
        {
            // A chase begins with its target in view: the record starts as sensed now, so the first look's gap gives nothing up.
            s.ChaseStart = s.SensedTick = world.Tick;
            s.SensedAt = target;
            s.Sensed = true;
            return;
        }
        if (s.Sensed || (world.Tick - s.ChaseStart) * world.TickSeconds < GiveUpGateSeconds) return;
        var here = new Vector2(next[i].Position.X, next[i].Position.Z);
        // Engine choice: a runner within GiveUpReach of the chaser itself has not got away, though a look missed it (a chaser running past it has it at its back); a sneaker has.
        if (Vector2.DistanceSquared(here, target) < GiveUpReach * GiveUpReach && (table.Cold(t)!.Orders & CharacterOrders.Sneak) == 0) return;
        if ((world.Tick - s.SensedTick) * world.TickSeconds <= GiveUpSenseSeconds && Vector2.DistanceSquared(here, s.SensedAt) >= GiveUpReach * GiveUpReach) return;
        s.ChaseStart = -1;
        world.Defer(new StopCommand([table.IdOf(i)]) { Tick = world.Tick + 1 });
        world.Defer(new Dialogue.DialogueEvent(table.IdOf(i), table.IdOf(t), 60) { Tick = world.Tick + 1 });
    }

    public void Hash(ref StateHasher hasher)
    {
        hasher.Add(Relations.Changes);
        if (Relations.Changes > 0)
        {
            int n = Relations.Count;
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++) hasher.Add(Relations.Get(a, b));
            for (int a = 0; a < n; a++)
                if (Relations.Rank(a) is var rank and not 0) { hasher.Add(a); hasher.Add(rank); }
            for (int a = 0; a < n; a++)
            {
                if (Relations.GlobalTrust(a) is var trust and not 0) { hasher.Add(a); hasher.Add(trust); }
                for (int b = 0; b < n; b++)
                    if (Relations.AtWar(a, b)) { hasher.Add(a); hasher.Add(b); }
            }
        }
        hasher.Add(size);
        for (int i = 0; i < size; i++)
        {
            ref readonly var s = ref slots[i];
            hasher.Add(s.Generation);
            hasher.Add(s.Tracked);
            hasher.Add(s.Task);
            if (s.ChaseStart >= 0)   // only while a chase runs
            {
                hasher.Add(s.ChaseStart);
                hasher.Add(s.SensedTick);
                hasher.Add(s.SensedAt.X);
                hasher.Add(s.SensedAt.Y);
                hasher.Add(s.Sensed);
            }
            if (s.Spots is { } spots)   // only while a looker has meters, so worlds without sneakers hash as before
                foreach (var p in spots)
                {
                    hasher.Add(p.Target);
                    hasher.Add(p.Generation);
                    hasher.Add(p.Value);
                }
        }
        hasher.Add(Started);
    }
}
