using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Combat;
using Meitou.Tests.Save;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Cages, locks, arrests and jail (docs/simulation.md "Cages, beds and locks", "Arrest and jail"; docs/game/locks.md, docs/game/economy.md
/// 11.5-11.8).
/// </summary>
public class ArrestTests
{
    static readonly Vector3 Centre = new(2000, 150, 2000);
    static readonly RaceData Person = new() { StringId = "17-c", Name = "Greenlander", StatMultipliers = Human.StatMultipliers,
            Anatomy = Human.Anatomy, Carriable = true };

    /// <summary>
    /// A town of a faction that books bounties, with a house of two doors and two cages.
    /// </summary>
    internal static PopulationData Town(IEnumerable<FcsRecord>? extra = null)
    {
        static FcsRecord Rec(string id, FcsRecordType type, string name) => SyntheticTown.Rec(id, type, name);
        var city = Rec("50-a", FcsRecordType.Faction, "city");
        city.Bools["offers bounties"] = true;
        var nameless = Rec("204-gamedata.base", FcsRecordType.Faction, "Nameless");
        var door = Rec("51-a", FcsRecordType.Building, "Door");
        var house = Rec("52-a", FcsRecordType.Building, "House");
        house.Floats["hardness"] = 40;
        house.References["doors"] = [SyntheticTown.Ref("51-a"), SyntheticTown.Ref("51-a")];
        var function = Rec("53-a", FcsRecordType.BuildingFunctionality, "Cage");
        function.Ints["function"] = TownBuildings.CageFunction;
        function.Floats["use range"] = 25;
        var cage = Rec("54-a", FcsRecordType.Building, "Prisoner Cage");
        cage.Floats["hardness"] = 50;
        cage.References["functionality"] = [SyntheticTown.Ref("53-a")];
        var town = Rec("55-a", FcsRecordType.Town, "Jailtown");
        town.Ints["type"] = 2;
        town.Floats["size radius"] = 600;
        town.References["faction"] = [SyntheticTown.Ref("50-a")];
        var file = new FcsFile();
        file.Records.AddRange([city, nameless, door, house, function, cage, town]);
        if (extra is not null) file.Records.AddRange(extra);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        TownBuilding B(string id, string record, Vector2 at) => new(id, record, record, Centre + new Vector3(at.X, 0, at.Y), 0, false, false, []);
        var buildings = new Dictionary<string, List<TownBuilding>>
        {
            ["jailtown"] = [B("house", "52-a", new(0, 400)), B("cageA", "54-a", new(200, 0)), B("cageB", "54-a", new(-200, 0))],
        };
        return PopulationData.Create(db, [new TownPlacement("jailtown", "55-a", Centre, null)], buildings: buildings);
    }

    /// <summary>
    /// The town above: two city policemen and two player characters. One, wanted for 5000 and just booked, stands by the first policeman, who arrests
    /// him (fight, lift, carry, cage A); the other starts in cage B, picks its lock and gets out at tick 900 (an escape). Hashes at five checkpoints,
    /// pinned in <see cref="GoldenHashTests"/>.
    /// </summary>
    internal static List<ulong> Scripted(int threads)
    {
        var data = Town();
        var relations = data.Relations;
        using var world = ScriptedWorld(data, relations, threads, out var arrest);
        int city = data.FactionIndex("50-a");
        var playerSquad = new Squad { Id = 1000, TemplateId = "p", Faction = relations.PlayerFaction };
        world.Squads.Add(playerSquad);

        Police(world, city, new Vector2(0, 60));
        Police(world, city, new Vector2(-200, 200));
        var hero = Hero(world, relations, city, playerSquad, new Vector2(0, 0), "hero", 5000,
                new Combatant { Attack = 20, Defence = 40, Dodge = 40, Toughness = 50 });
        world.Characters.Cold(hero.Slot)!.RecentlyWanted = CrimeSystem.RecentlyWantedSeconds;
        var picker = Hero(world, relations, city, playerSquad, new Vector2(-200, 0), "picker", 2500, new Combatant());
        world.Characters.Cold(picker.Slot)!.Stats![StatsEnumerated.Lockpicking] = 60;
        var cageB = world.Fixtures.Find("0.0:cageB")!;

        Assert.True(world.Fixtures.Imprison(world, picker, cageB));

        var (hashes, picked) = RunTheScript(world, picker, cageB);
        var heroCold = world.Characters.Cold(hero.Slot)!;
        var seen = (Jailed: heroCold.IsImprisoned && heroCold.InWhat == "0.0:cageA" && arrest.Log.Any(e => e.Kind == ArrestEventKind.Jailed
                && e.Target == hero),
            Picked: picked, Escaped: world.Characters.Cold(picker.Slot)!.Escaped || arrest.Log.Any(e => e.Target == picker));

        // It covers what it is for: the arrest reached the cage, the prisoner's pick opened the other cage, and getting out was an escape.
        Assert.True(seen.Jailed && seen.Picked && seen.Escaped, $"{seen}");

        return hashes;
    }

    /// <summary>
    /// The world of <see cref="Scripted"/>: player, pursuit, movement, bodies, carrying, combat, retaliation, aggro, crime, arrest and locks, the
    /// town's site registered.
    /// </summary>
    static SimWorld ScriptedWorld(PopulationData data, FactionRelations relations, int threads, out ArrestSystem arrest, DialogueSystem? talk = null)
    {
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths,
                new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = talk is not null }) { Relations = relations };
        var carry = new CarrySystem(BaseGame, BodyOptions.Default, 1) { Relations = relations };
        var crime = new CrimeSystem(relations);
        combat.Crimes = crime;
        carry.Crimes = crime;
        arrest = new ArrestSystem(crime, BaseGame, new ArrestSettings { RecordLog = true }) { Carry = carry, Talk = talk };
        var locks = new LockSystem(BaseGame, BodyOptions.Default) { Crimes = crime };
        var walk = new OpenGroundWalkability((_, _) => 150);
        var world = new SimWorld(new WorldSettings { Seed = 31, Threads = threads, PublishSnapshots = false, MinPartitionSize = 1 }, walk,
            [new PlayerSystem(), new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), carry, combat,
             new RetaliationSystem(combat), new AggroSystem(relations), crime, arrest, locks, .. talk is null ? [] : new ITickSystem[] { talk }]);
        world.Fixtures.RegisterSite(world, data, 0);
        return world;
    }

    /// <summary>
    /// A city policeman in a squad of his own homed on the town, standing at <paramref name="at"/> from the centre and facing it.
    /// </summary>
    static CharacterId Police(SimWorld world, int city, Vector2 at)
    {
        var cold = new Combatant { Name = "officer", Attack = 70, Strength = 70 }.MakeCold();
        cold.Faction = city;
        cold.NpcClass = FundamentalType.LawEnforcement;
        var squad = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = city, Town = 0,
                HomeCentre = new Vector2(Centre.X, Centre.Z), HomeRadius = 500 };
        world.Squads.Add(squad);
        cold.SquadId = squad.Id;
        var id = world.Characters.Spawn(new CharacterHot { Position = Centre + new Vector3(at.X, 0, at.Y), Health = 100, MaxSpeed = 60,
                WalkSpeed = 15, Yaw = MathF.Atan2(-at.X, -at.Y), Task = (byte)CharacterTask.Idle }, cold, 0);
        squad.Members.Add(id);
        squad.Leader = id;
        return id;
    }

    /// <summary>
    /// A player character of the player's squad, wanted by the city for <paramref name="bounty"/>, standing at <paramref name="at"/> from the
    /// centre.
    /// </summary>
    static CharacterId Hero(SimWorld world, FactionRelations relations, int city, Squad playerSquad, Vector2 at, string name, int bounty,
            Combatant stats)
    {
        var cold = stats.MakeCold(name);
        cold.Race = Person;
        cold.Faction = relations.PlayerFaction;
        cold.IsPlayer = true;
        cold.NpcClass = FundamentalType.Adventurer;
        cold.SquadId = playerSquad.Id;
        var entry = (cold.Bounties = new BountyLedger()).Get(city, "city");
        entry.Amount = bounty;
        var id = world.Characters.Spawn(new CharacterHot { Position = Centre + new Vector3(at.X, 0, at.Y), Health = 100, MaxSpeed = 60,
                WalkSpeed = 15, Yaw = MathF.PI }, cold, 0);
        playerSquad.Members.Add(id);
        return id;
    }

    /// <summary>
    /// Runs to the five checkpoints: the prisoner starts picking at the first and gets out at tick 900; returns the hashes at the checkpoints and
    /// whether the cage was picked open by then.
    /// </summary>
    static (List<ulong> Hashes, bool Picked) RunTheScript(SimWorld world, CharacterId picker, Fixture cageB)
    {
        var hashes = new List<ulong>();
        bool picked = false;
        foreach (int target in new[] { 1, 300, 900, 1800, 3000 })
        {
            world.RunTicks(target - (int)world.Tick);
            if (target == 1) world.Commands.Enqueue(new PickLockOrder([picker], picker) { Tick = world.Tick });
            if (target == 900)
            {
                picked = cageB.Lock is { EffectivelyLocked: false };
                world.Commands.Enqueue(new GetOutOrder([picker]) { Tick = world.Tick });
            }
            hashes.Add(world.StateHash());
        }
        return (hashes, picked);
    }

    /// <summary>
    /// Escape is order 207 Escape cage, given to every selected character with its queue cleared (the giver <c>FUN_1407f9e20</c>, commit
    /// <c>2bf01bae6</c>): of two selected characters with a move queued, the one in a locked cage holds 207 (list B <c>!54</c>) until the cage is
    /// unlocked and then gets out as an escape; the free one's 207 is done at once (list A <c>!79</c>) and leaves its queue empty. Catches the old
    /// giver, which refused a locked cage and let the order go, a queue kept under the order, and a free character left holding it.
    /// </summary>
    [Fact]
    public void Escape_holds_a_caged_character_until_its_cage_is_unlocked_and_leaves_a_free_one_at_once()
    {
        var data = Town();
        var relations = data.Relations;
        using var world = ScriptedWorld(data, relations, 1, out _);
        int city = data.FactionIndex("50-a");
        var playerSquad = new Squad { Id = 1000, TemplateId = "p", Faction = relations.PlayerFaction };
        world.Squads.Add(playerSquad);
        var caged = Hero(world, relations, city, playerSquad, new Vector2(-200, 0), "caged", 2500, new Combatant());
        var free = Hero(world, relations, city, playerSquad, new Vector2(0, 100), "free", 0, new Combatant());
        var cage = world.Fixtures.Find("0.0:cageB")!;

        Assert.True(world.Fixtures.Imprison(world, caged, cage));

        world.Commands.Enqueue(new MoveOrder([caged, free], Centre + new Vector3(0, 0, -400)) { Tick = world.Tick });
        world.RunTick();
        world.Commands.Enqueue(new GetOutOrder([caged, free]) { Tick = world.Tick });
        world.RunTick();
        var (cagedCold, freeCold) = (world.Characters.Cold(caged.Slot)!, world.Characters.Cold(free.Slot)!);

        Assert.Equal([TaskType.GetOutOfCageEscape], cagedCold.OrderQueue.Select(e => e.Row));
        Assert.Empty(freeCold.OrderQueue);

        // Locked: the order waits and the prisoner stays in.
        world.RunTicks(120);

        Assert.True(cagedCold.IsImprisoned);
        Assert.Equal([TaskType.GetOutOfCageEscape], cagedCold.OrderQueue.Select(e => e.Row));

        world.Fixtures.SetLocked(cage, false);
        for (int i = 0; i < 60 && cagedCold.IsImprisoned; i++) world.RunTick();

        Assert.False(cagedCold.IsImprisoned);
        Assert.True(cagedCold.Escaped);
        Assert.Empty(cagedCold.OrderQueue);
    }

    [Fact]
    public void The_sentence_and_the_lockpick_rules_hold_at_their_edges()
    {
        // trunc(bounty x 0.02 x prison time) hours; above 25000 for life (economy.md 11.5).
        Assert.Equal(0, FixtureRegistry.SentenceHours(49, 1));
        Assert.Equal(1, FixtureRegistry.SentenceHours(50, 1));
        Assert.Equal(100, FixtureRegistry.SentenceHours(2500, 2));
        Assert.Equal(500, FixtureRegistry.SentenceHours(25000, 1));
        Assert.Equal(FixtureRegistry.LifeSentence, FixtureRegistry.SentenceHours(25001, 1));

        // 0.9 at or above the level, halved every 10 below (locks.md "Lockpicking").
        Assert.Equal(0.9f, Lockpicking.Chance(40, 40));
        Assert.Equal(0.9f, Lockpicking.Chance(40, 90));
        Assert.Equal(0.45f, Lockpicking.Chance(40, 30), 5);

        // Allowed while the chance is above 0.05 (strict): 41 below the level is, 42 is not; a caged picker rolls at skill - 10 but is allowed by its
        // full skill.
        Assert.Equal(0.05f, BaseGame.MinimumLockpickChance, 5);

        var picks = new Lockpicking(BaseGame, BodyOptions.Default);
        var picker = new Combatant().MakeCold();
        picker.Stats![StatsEnumerated.Lockpicking] = 20;

        Assert.True(picks.Allowed(picker, 61));
        Assert.False(picks.Allowed(picker, 62));

        picker.InSomething = HeldState.Imprisoned;

        Assert.True(picks.Allowed(picker, 61));
        Assert.Equal(Lockpicking.Chance(61, 10), Lockpicking.RollChance(picker, 61));

        picker.InSomething = HeldState.None;
        picker.Medical!.KnockOut(1000);

        Assert.False(picks.Allowed(picker, 20));
    }

    [Fact]
    public void A_bounty_is_perceived_while_the_target_is_recently_wanted_or_is_down_and_an_enemy()
    {
        // FUN_140853fd0 behind goal 204's finder (FUN_14099bfc0): the entry of the guard's booking faction, visible with the recently-wanted marker,
        // or on a downed enemy.
        var data = Town();
        var relations = data.Relations;
        int city = data.FactionIndex("50-a");
        var arrest = new ArrestSystem(new CrimeSystem(relations), BaseGame);
        using var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150), []);
        var guard = new Combatant().MakeCold();
        guard.Faction = city;
        var target = new Combatant().MakeCold();
        target.Faction = relations.PlayerFaction;
        int g = world.Characters.Spawn(new CharacterHot(), guard, 0).Slot;
        int t = world.Characters.Spawn(new CharacterHot(), target, 0).Slot;

        Assert.False(arrest.PerceivedBounty(world, g, guard, t, target));

        (target.Bounties = new BountyLedger()).Get(city, "city").Amount = 500;
        target.RecentlyWanted = CrimeSystem.RecentlyWantedSeconds;

        Assert.True(arrest.PerceivedBounty(world, g, guard, t, target));
    }

    /// <summary>
    /// A three-line event-44 bubble ending in DA 29, without slave conditions, as the base bounty recognition chain does.
    /// </summary>
    static FcsRecord[] SlaveBountyLines()
    {
        var guard = SyntheticTown.Rec("arrest-c", FcsRecordType.Character, "Guard");
        guard.References["dialogue package"] = [SyntheticTown.Ref("arrest-p")];
        var package = SyntheticTown.Rec("arrest-p", FcsRecordType.DialoguePackage, "Law");
        package.References["dialogs"] = [SyntheticTown.Ref("arrest-d", ArrestSystem.BountySpotted)];
        var root = SyntheticTown.Rec("arrest-d", FcsRecordType.Dialogue, "Bounty recognition");
        root.Bools["monologue"] = true;
        root.Ints["repetition limit"] = 1;
        root.References["lines"] = [SyntheticTown.Ref("arrest-l1")];
        var first = SyntheticTown.Rec("arrest-l1", FcsRecordType.DialogueLine, "Recognise");
        first.Strings["text0"] = "Familiar.";
        first.References["lines"] = [SyntheticTown.Ref("arrest-l2")];
        var second = SyntheticTown.Rec("arrest-l2", FcsRecordType.DialogueLine, "Wanted");
        second.Strings["text0"] = "Wanted.";
        second.References["lines"] = [SyntheticTown.Ref("arrest-l3")];
        var last = SyntheticTown.Rec("arrest-l3", FcsRecordType.DialogueLine, "Arrest");
        last.Strings["text0"] = "Arrest.";
        last.References["effects"] = [SyntheticTown.Ref("arrest-a")];
        var effect = SyntheticTown.Rec("arrest-a", FcsRecordType.DialogAction, "Arrest target");
        effect.Ints["action name"] = 29;
        return [guard, package, root, first, second, last, effect];
    }

    /// <summary>
    /// A wanted, shackled player slave and its owner-faction guard, with the recognition dialogue and a retained bounty of 13500.
    /// </summary>
    static (SimWorld World, ArrestSystem Arrest, DialogueSystem Talk, CharacterId Guard, CharacterId Slave) SlaveBountyWorld(byte state, bool ownTown)
    {
        var data = Town(SlaveBountyLines());
        var talk = new DialogueSystem(data, new DialogueSettings { AmbientEvents = true });
        var world = ScriptedWorld(data, data.Relations, 1, out var arrest, talk);
        talk.Arrests = arrest;
        world.Player.Faction = data.PlayerFaction;
        int city = data.FactionIndex("50-a");
        var guard = Police(world, city, new Vector2(0, 60));
        var gc = world.Characters.Cold(guard.Slot)!;
        gc.RecordId = "arrest-c";
        if (!ownTown) world.Squads.Find(gc.SquadId)!.HomeCentre += new Vector2(10000, 0);

        var squad = new Squad { Id = 1000, TemplateId = "p", Faction = data.PlayerFaction };
        world.Squads.Add(squad);
        var slave = Hero(world, data.Relations, city, squad, Vector2.Zero, "slave", 13500, new Combatant());
        var sc = world.Characters.Cold(slave.Slot)!;
        sc.Inventory = new Inventory();
        sc.Inventory.Items.Add(new ItemInstance { Record = "shackles", Section = "boots", Lock = new LockState { Locked = true } });
        FixtureRegistry.Enslave(world, sc, city, gc.SquadId);
        if (state == 2) FixtureRegistry.TagEscaped(world, sc);

        return (world, arrest, talk, guard, slave);
    }

    /// <summary>
    /// The full bubble chain still speaks and opens no conversation. Its DA 29 spares an owned obedient slave in or out of town, but still
    /// pursues an escaped slave. Catches a dialogue effect that bypasses the ordinary target-score protection or tags the slave as an enemy.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    public void Bounty_bubbles_spare_obedient_slaves_but_still_pursue_escapees(byte state, bool ownTown)
    {
        var (world, arrest, talk, guard, slave) = SlaveBountyWorld(state, ownTown);
        using var owned = world;
        world.Commands.Enqueue(new DialogueEvent(guard, slave, ArrestSystem.BountySpotted) { Tick = world.Tick });
        world.RunTicks(450);

        Assert.Contains(world.DialogueLines.Take(), line => line.Contains("Arrest.", StringComparison.Ordinal));
        Assert.Null(talk.PublishConversation());
        bool pursued = arrest.Log.Any(e => e.Target == slave && e.Kind is ArrestEventKind.Arrest or ArrestEventKind.Attack);
        Assert.Equal(state == 2, pursued);
        if (state != 1) return;

        var sc = world.Characters.Cold(slave.Slot)!;
        Assert.Equal(13500, sc.Bounties!.Entries.Single().Amount);
        Assert.False(world.Characters.Cold(guard.Slot)!.InCombat);
        Assert.Empty(world.Characters.Cold(guard.Slot)!.OrderQueue);
        Assert.False(talk.Memory.HasShort(world, world.Characters.Cold(guard.Slot)!.SquadId, slave, MemoryTag.TempEnemy));
        Assert.Equal(0u, (talk.Memory.Find(world, sc.SquadId, guard)?.Long ?? 0) & (1u << MemoryTag.Captor));
    }

    /// <summary>
    /// An NPC's already queued capture and deferred attack both refuse the obedient slave. Catches saved orders and commands issued before the
    /// dialogue guard, including a first swing before the ordinary target chooser runs.
    /// </summary>
    [Fact]
    public void Queued_capture_and_deferred_attack_refuse_an_owned_obedient_slave()
    {
        var (world, arrest, _, guard, slave) = SlaveBountyWorld(1, true);
        using var owned = world;
        PlayerOrders.Give(world, guard.Slot, new OrderEntry(TaskType.LiftPersonSnatchingAllowedInTownOnly, arrest) { Target = slave }, queued: false);
        world.Commands.Enqueue(new AttackOrder([guard], slave) { Tick = world.Tick, Source = TaskSource.Order, Row = TaskType.MeleeAttack });
        world.RunTicks(1);
        Assert.False(world.Characters.Cold(guard.Slot)!.InCombat);

        world.RunTicks(179);

        Assert.False(world.Characters.Cold(guard.Slot)!.InCombat);
        Assert.Empty(world.Characters.Cold(guard.Slot)!.OrderQueue);
        Assert.DoesNotContain(world.SystemOf<CombatSystem>()!.Log, hit => hit.Defender == slave.Slot);
    }

    /// <summary>
    /// Re-enslaving a target during a running capture cancels its guard's fight and queued retries. Catches StepFight restarting the attack on an
    /// otherwise protected slave after the combat chooser dropped it.
    /// </summary>
    [Fact]
    public void A_running_arrest_stops_when_its_target_becomes_an_owned_obedient_slave()
    {
        var (world, arrest, _, guard, slave) = SlaveBountyWorld(2, true);
        using var owned = world;
        Assert.True(arrest.ArrestFromDialogue(world, guard, slave, capture: true));
        world.RunTicks(2);
        Assert.True(world.Characters.Cold(guard.Slot)!.InCombat);

        var gc = world.Characters.Cold(guard.Slot)!;
        FixtureRegistry.Enslave(world, world.Characters.Cold(slave.Slot)!, gc.Faction, gc.SquadId);
        world.RunTicks(180);

        Assert.False(gc.InCombat);
        Assert.Empty(gc.OrderQueue);
        Assert.Equal(1, arrest.Log.Count(e => e.Target == slave && e.Kind == ArrestEventKind.Arrest));
    }
}
