using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Stealth KO uses the victim's current sense entry for both recipient previews and the swing (docs/game/combat.md "Stealth knock-out").
/// </summary>
public class StealthKoTests
{
    /// <summary>
    /// Two independently controlled sneakers, one guard and another observer on stationary, open ground.
    /// </summary>
    sealed class Scene : IDisposable
    {
        /// <summary>
        /// The authoritative simulation, including recipient capture and real KO tasks.
        /// </summary>
        public SimWorld World { get; }
        /// <summary>
        /// The real sense passes and forced sightings.
        /// </summary>
        public AggroSystem Aggro { get; }
        /// <summary>
        /// The swings whose pre-XP chances are checked against the published previews.
        /// </summary>
        public CarrySystem Carry { get; }
        /// <summary>
        /// The two character owners.
        /// </summary>
        public WorldPlayer A, B;
        /// <summary>
        /// Their independently synchronized connections.
        /// </summary>
        public AuthorityConnection ConnectionA, ConnectionB;
        /// <summary>
        /// The player characters and the two NPC lookers.
        /// </summary>
        public CharacterId First, Second, Guard, Observer;
        /// <summary>
        /// The next authenticated request number on the first connection.
        /// </summary>
        ulong _sequence;

        /// <summary>
        /// Frequent scans reproduce losing sight; sparse scans isolate the sense flags across preview and strike.
        /// </summary>
        public Scene(bool frequentScans = false)
        {
            var relations = FactionRelations.Build([new FactionData { Id = "guards", Name = "guards" },
                new FactionData { Id = "first", Name = "first" }, new FactionData { Id = "second", Name = "second" }]);
            Aggro = new AggroSystem(relations, new AggroSettings { AttackOnSight = false, ScanEveryTicks = frequentScans ? 1 : 10000 });
            Carry = new CarrySystem(BaseGame, BodyOptions.Default) { Aggro = Aggro };
            World = new SimWorld(new WorldSettings { Seed = 7, Threads = 1, PublishSnapshots = false },
                new OpenGroundWalkability((_, _) => 150), [new PlayerSystem(), Carry, Aggro]);
            A = Register(2, 1);
            B = Register(3, 2);
            First = Spawn(new(2000, 150, frequentScans ? 2008 : 1990), 1, player: true);
            Second = Spawn(new(2000, 150, 1992), 2, player: true);
            Guard = Spawn(new(2000, 150, 2000), 0);
            Observer = Spawn(new(3000, 150, 2000), 0);
            World.Players.AssignInitial(A.Id, First);
            World.Players.AssignInitial(B.Id, Second);
            A.State.Selection.Add(First);
            B.State.Selection.Add(Second);
            A.State.Pointed = B.State.Pointed = Guard;
            World.RunTicks(4);
            ConnectionA = Connect(A.Id);
            ConnectionB = Connect(B.Id);
        }

        /// <summary>
        /// Registers one owner in its own faction.
        /// </summary>
        WorldPlayer Register(ulong number, int faction)
        {
            var player = World.Players.Register(new PlayerId(number));
            World.Players.SetMembership(player.Id, faction, FactionPermission.All);
            return player;
        }

        /// <summary>
        /// Healthy humans with assassination 55 against toughness 40 have an unmodified KO chance of 75 per cent.
        /// </summary>
        public CharacterId Spawn(Vector3 position, int faction, bool player = false)
        {
            var stats = new CharacterStats();
            stats[StatsEnumerated.Assassination] = player ? 55 : 1;
            stats[StatsEnumerated.Toughness] = 40;
            var cold = new CharacterCold
            {
                Name = "C" + World.Characters.HighWater, Race = Human, Stats = stats, Medical = MedicalState.Create(Human, 50),
                Faction = faction, IsPlayer = player, Orders = player ? CharacterOrders.Sneak : CharacterOrders.None,
            };
            return World.Characters.Spawn(new CharacterHot { Position = position }, cold, World.Tick);
        }

        /// <summary>
        /// Opens a recipient view without advancing the simulation.
        /// </summary>
        AuthorityConnection Connect(PlayerId player)
        {
            var connection = World.Authority.Open(player);
            Assert.True(World.Authority.BeginSynchronization(connection));
            var baseline = World.Authority.CaptureView(connection, new(), new(2000))!;
            Assert.True(World.Authority.ActivateAfterView(connection, baseline.Publication));
            return connection;
        }

        /// <summary>
        /// The guard's public interaction figures as delivered to one authenticated recipient.
        /// </summary>
        public CharacterSnapshot View(bool second = false) => World.Authority
            .CaptureView(second ? ConnectionB : ConnectionA, new(), new(2000))!.Snapshot.Characters.Single(c => c.Id == Guard);

        /// <summary>
        /// Sets an existing or remembered sense entry, independent of the spotting meter and the hearing RNG.
        /// </summary>
        public void Sense(bool seen, bool heard, bool known = true, bool staleGeneration = false)
        {
            var senses = Aggro.SensesOf(Guard.Slot)!;
            senses.EntryList.Clear();
            if (!known) return;

            senses.EntryList.Add(new SenseEntry
            {
                Target = First.Slot, Generation = First.Generation + (staleGeneration ? 1 : 0), Tick = World.Tick,
                Place = World.Characters.Previous[First.Slot].Position, Seen = seen, Heard = heard, Tags = SenseTag.Neutral,
            });
        }

        /// <summary>
        /// Gives a real authenticated order and advances through its swing; a cancellation leaves no rolled entry.
        /// </summary>
        public void Attempt(params CharacterId[] attackers)
        {
            var receipt = Start(attackers);
            World.RunTicks(20);
            Assert.Equal(AuthorityStatus.Evaluated, receipt.GetAwaiter().GetResult().Status);
        }

        /// <summary>
        /// Submits the order without advancing, allowing awareness to change before the roll.
        /// </summary>
        public Task<AuthorityReceipt> Start(params CharacterId[] attackers)
        {
            var grants = attackers.Select(id => World.Players.ControlOf(id)!.Value).ToArray();
            var order = new StealthKoOrder(attackers, Guard) { Tick = World.Tick };
            return World.Authority.Submit(ConnectionA, ++_sequence, order, new(grants));
        }

        /// <summary>
        /// Releases the headless world.
        /// </summary>
        public void Dispose() => World.Dispose();
    }

    /// <summary>
    /// The same guard may see one player while another remains behind it; losing sight restores both preview and execution despite a full meter.
    /// </summary>
    [Fact]
    public void Losing_sight_restores_the_chance_and_swing_for_only_the_affected_player()
    {
        using var s = new Scene(frequentScans: true);
        s.World.RunTicks(100);
        Assert.True(s.Aggro.Spotted(s.Guard.Slot, s.First));
        Assert.Equal(0, s.View().StealthKoChance);
        Assert.Equal(75, s.View(second: true).StealthKoChance);
        s.Attempt(s.First);
        Assert.Empty(s.Carry.Knockouts);

        s.World.Characters.Place(s.First.Slot, new(2000, 150, 1990), 0);
        s.World.RunTicks(2);

        Assert.True(s.Aggro.Spotted(s.Guard.Slot, s.First), "the historical meter is still full");
        Assert.True(s.Aggro.SensesOf(s.Guard.Slot)!.TryGet(s.First, out var entry));
        Assert.False(entry.Seen);
        Assert.Equal(75, s.View().StealthKoChance);
        Assert.Equal(75, s.View(second: true).StealthKoChance);
        s.Attempt(s.First);
        Assert.Equal(75, Assert.Single(s.Carry.Knockouts).Chance);
    }

    /// <summary>
    /// Seen means zero and silent cancellation; heard alone halves the chance; a stale generation must not identify a different character.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, 0)]
    [InlineData(false, true, false, 37.5f)]
    [InlineData(false, false, false, 75)]
    [InlineData(true, true, true, 75)]
    public void Preview_and_strike_use_the_same_current_sense_flags(bool seen, bool heard, bool staleGeneration, float chance)
    {
        using var s = new Scene();
        s.Sense(seen, heard, staleGeneration: staleGeneration);
        Assert.Equal(chance, s.View().StealthKoChance);

        s.Attempt(s.First);

        if (seen && !staleGeneration) Assert.Empty(s.Carry.Knockouts);
        else Assert.Equal(chance, Assert.Single(s.Carry.Knockouts).Chance);
    }

    /// <summary>
    /// A fighting target needs a known entry and an exposed attacker for the fight zero; a remembered entry alone still suppresses miss XP.
    /// </summary>
    [Theory]
    [InlineData(false, false, 75)]
    [InlineData(false, true, 75)]
    [InlineData(true, false, 75)]
    [InlineData(true, true, 0)]
    public void Fight_zero_uses_entry_existence_and_current_exposure(bool known, bool exposed, float chance)
    {
        using var s = new Scene();
        s.Sense(false, false, known);
        s.World.Characters.Cold(s.Guard.Slot)!.InCombat = true;
        if (exposed) s.Aggro.Force(s.World, s.Observer.Slot, s.First.Slot);
        Assert.Equal(chance, s.View().StealthKoChance);

        s.Attempt(s.First);

        Assert.Equal(chance, Assert.Single(s.Carry.Knockouts).Chance);
    }

    /// <summary>
    /// A remembered attacker of a fighting target gives no XP on a miss even after its seen and heard flags have cleared.
    /// </summary>
    [Fact]
    public void A_remembered_entry_suppresses_failed_assassination_experience_in_a_fight()
    {
        using var s = new Scene();
        s.Sense(false, false);
        s.World.Characters.Cold(s.Guard.Slot)!.InCombat = true;
        var stats = s.World.Characters.Cold(s.First.Slot)!.Stats!;
        stats[StatsEnumerated.Assassination] = 10;
        float preview = s.View().StealthKoChance!.Value;

        s.Attempt(s.First);

        var roll = Assert.Single(s.Carry.Knockouts);
        Assert.Equal(preview, roll.Chance);
        Assert.False(roll.Success);
        Assert.True(roll.NoXp);
        Assert.Equal(10, stats[StatsEnumerated.Assassination]);
    }

    /// <summary>
    /// The first eligible sneaker supplies both the published percentage and the actual order, even with a nearer or non-sneaking selection.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selection_order_chooses_the_same_attacker_for_preview_and_execution(bool nearerSneaks)
    {
        using var s = new Scene();
        var nearer = s.Spawn(new(2000, 150, 1999), 1, player: true);
        s.World.Players.AssignInitial(s.A.Id, nearer);
        var cold = s.World.Characters.Cold(nearer.Slot)!;
        cold.Stats![StatsEnumerated.Assassination] = 10;
        cold.Orders = nearerSneaks ? CharacterOrders.Sneak : CharacterOrders.None;
        CharacterId[] selection = nearerSneaks ? [s.First, nearer] : [nearer, s.First];
        s.A.State.Selection.Clear();
        s.A.State.Selection.AddRange(selection);
        Assert.Equal(75, s.View().StealthKoChance);

        s.Attempt(selection);

        var roll = Assert.Single(s.Carry.Knockouts);
        Assert.Equal(s.First, roll.Attacker);
        Assert.Equal(75, roll.Chance);
    }

    /// <summary>
    /// Preview and strike share current flags rather than caching a hearing roll at the start of the animation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Awareness_is_rechecked_during_the_swing(bool seen)
    {
        using var s = new Scene();
        s.Sense(false, false);
        Assert.Equal(75, s.View().StealthKoChance);
        var receipt = s.Start(s.First);
        s.World.RunTicks(2);
        s.Sense(seen, heard: true);
        Assert.Equal(seen ? 0 : 37.5f, s.View().StealthKoChance);

        s.World.RunTicks(18);

        Assert.Equal(AuthorityStatus.Evaluated, (await receipt).Status);
        if (seen) Assert.Empty(s.Carry.Knockouts);
        else Assert.Equal(37.5f, Assert.Single(s.Carry.Knockouts).Chance);
    }

    /// <summary>
    /// Existing meters must stop exposing a sneaker even when no sneaker is in range, stealth is off or the looker is knocked out.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Inactive_spotting_passes_do_not_leave_historical_exposure(bool stopSneaking, bool knockOutGuard)
    {
        using var s = new Scene(frequentScans: true);
        s.World.RunTicks(100);
        Assert.False(s.Aggro.Unseen(s.World.Characters, s.First));
        if (stopSneaking) s.World.Characters.Cold(s.First.Slot)!.Orders = CharacterOrders.None;
        if (knockOutGuard) s.World.Characters.Cold(s.Guard.Slot)!.Medical!.KnockOut(100);
        s.World.Characters.Place(s.First.Slot, new(2000, 150, 9000), 0);

        s.World.RunTicks(2);

        Assert.True(s.Aggro.Spotted(s.Guard.Slot, s.First), "the meter has not drained yet");
        Assert.True(s.Aggro.Unseen(s.World.Characters, s.First));
    }
}
