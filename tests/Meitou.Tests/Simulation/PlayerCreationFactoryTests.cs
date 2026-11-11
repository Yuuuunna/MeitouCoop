using Meitou.Simulation.Player;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class PlayerCreationFactoryTests
{
    sealed class Ground : IWalkability
    {
        public bool Ready = true;
        public int Calls, ThrowAt = int.MaxValue;
        public float GroundHeight(float x, float z)
        {
            if (++Calls == ThrowAt) throw new IOException("Injected arrival surface failure");
            return 300;
        }
        public bool IsWalkable(float x, float z) => Ready;
        public PathResult FindPath(Vector3 from, Vector3 to) => PathResult.NotFound;
    }
    sealed class Scene : IDisposable
    {
        public PopulationData Data { get; }
        public PopulationSystem Population { get; }
        public SimWorld World { get; }
        public Ground Walk { get; } = new();
        public NewGameStart Start { get; }
        public int A { get; }
        public int B { get; }
        public Scene()
        {
            var tech = SyntheticTown.Rec("tech-existing", FcsRecordType.Research, "Existing technology");
            var unique = SyntheticTown.Rec("unique-player", FcsRecordType.Character, "Unique");
            unique.Bools["unique"] = true; unique.References["race"] = [new("1-t", 100, 0, 0)];
            var randomized = SyntheticTown.Rec("random-player", FcsRecordType.Character, "Randomized");
            randomized.Ints["stats randomise"] = 20; randomized.References["race"] = [new("1-t", 100, 0, 0)];
            Data = SyntheticTown.Data(SyntheticTown.Database(extra: [tech, unique, randomized]));
            Data.Relations.PlayerFaction = -1;
            A = Data.RegisterFaction("runtime:a", "A", Data.PlayerFaction);
            B = Data.RegisterFaction("runtime:b", "B", Data.PlayerFaction);
            Population = new PopulationSystem(Data, new PopulationSettings { Background = false });
            World = new SimWorld(new WorldSettings { PublishSnapshots = false }, Walk, [new PlayerSystem(Data), Population]);
            World.Players.Register(new PlayerId(2)); World.Players.Register(new PlayerId(3));
            Start = NewGameStart.From(Data.Db.Find("40-t")!) with { ForceStartPos = true, StartPosition = new Vector2(1000, 2000) };
        }
        public void Dispose() { World.Dispose(); Population.Dispose(); }
    }

    [Fact]
    public void NewFactionAndRosterDoNotResetExistingPlayersDiplomacyResearchOrInterface()
    {
        using var s = new Scene(); var world = s.World;
        s.Population.InitializePlayerFaction(world, s.A, s.Start with { Money = 111 });
        world.Players.SetMembership(new PlayerId(2), s.A, FactionPermission.All);
        var first = s.Population.MaterializePlayerSquad(world, s.Population.PreparePlayerSquad(world, s.Start, s.A, 41, 10));
        foreach (var role in first.Members) world.Players.AssignInitial(new PlayerId(2), role);
        var existing = world.Factions.Find(s.A)!;
        existing.Research.Complete(s.Data.Db, "tech-existing");
        int npc = s.Data.FactionIndex("20-t");
        s.Data.Relations.Set(s.A, npc, 53); s.Data.Relations.Set(npc, s.A, -64); s.Data.Relations.SetWar(s.A, npc, true);
        var prior = s.Data.Relations.Capture();
        var start = s.Start with { Money = 222, FactionRelations = [new RecordLink("20-t", 25, 0, 0)] };
        s.Population.InitializePlayerFaction(world, s.B, start);
        world.Players.SetMembership(new PlayerId(3), s.B, FactionPermission.All);
        ulong before = world.StateHash();
        var recipe = s.Population.PreparePlayerSquad(world, start, s.B, 42, 10);
        Assert.Equal(before, world.StateHash());
        var second = s.Population.MaterializePlayerSquad(world, recipe);
        Assert.Equal(111, existing.Money); Assert.Contains("tech-existing", existing.Research.Completed);
        Assert.Equal(222, world.Factions.Find(s.B)!.Money);
        Assert.Equal(25, s.Data.Relations.Get(npc, s.B)); Assert.Equal(25, s.Data.Relations.Get(s.B, npc));
        var current = s.Data.Relations.Capture();
        for (int a = 0; a < s.Data.Factions.Count; a++)
            for (int b = 0; b < s.Data.Factions.Count; b++)
                if (a != s.B && b != s.B)
                {
                    Assert.Equal(prior.Relations[a * s.Data.Factions.Count + b], current.Relations[a * s.Data.Factions.Count + b]);
                    Assert.Equal(prior.Wars[a * s.Data.Factions.Count + b], current.Wars[a * s.Data.Factions.Count + b]);
                }
        Assert.All(second.Members, role => Assert.Equal(s.B, world.Characters.Cold(role.Slot)!.Faction));
        Assert.True(second.PlayerManaged); Assert.Equal(recipe.CharacterCount, second.Members.Count);
        Assert.False(world.Player.Exists); Assert.Empty(world.Player.Selection);
        Assert.False(world.Players.Find(new PlayerId(3))!.State.InCreation);
        Assert.Empty(world.Players.KnownRoles(new PlayerId(3))); // Transaction, not this materializer, assigns control.
        Assert.Throws<InvalidOperationException>(() => s.Population.MaterializePlayerSquad(world, recipe));
        Assert.Throws<InvalidOperationException>(() => s.Population.InitializePlayerFaction(world, s.A, start));
        Assert.Throws<InvalidOperationException>(() => s.Population.StartPlayer(world, start));
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("missing")]
    [InlineData("navigation")]
    public void InvalidPreparationCreatesNoCharactersOrSquads(string defect)
    {
        using var s = new Scene();
        var start = s.Start;
        if (defect == "missing") start = start with { Squad = [new RecordLink("missing", 1, 0, 0)] };
        if (defect == "navigation") s.Walk.Ready = false;
        if (defect == "missing") Assert.Throws<InvalidDataException>(() => s.Population.PreparePlayerSquad(s.World, start, s.A, 10, 10));
        else Assert.Throws<InvalidOperationException>(() => s.Population.PreparePlayerSquad(s.World, start, s.A, 10, defect == "limit" ? 1 : 10));
        Assert.Equal(0, s.World.Characters.Count); Assert.Empty(s.World.Squads.All);
        Assert.Null(s.World.Factions.Find(s.A));
    }

    [Fact]
    public void UniqueOccupancyAndNavigationAreCheckedAgainBeforeMaterialization()
    {
        using var s = new Scene();
        var start = s.Start with { Squad = [new RecordLink("unique-player", 1, 0, 0)] };
        var recipe = s.Population.PreparePlayerSquad(s.World, start, s.A, 10, 2);
        Assert.True(s.World.Uniques.TryClaim("unique-player"));
        Assert.Throws<InvalidOperationException>(() => s.Population.MaterializePlayerSquad(s.World, recipe));
        s.World.Uniques.Release("unique-player"); s.Walk.Ready = false;
        Assert.Throws<InvalidOperationException>(() => s.Population.MaterializePlayerSquad(s.World, recipe));
        Assert.Equal(0, s.World.Characters.Count);
        s.Walk.Ready = true;
        Assert.Single(s.Population.MaterializePlayerSquad(s.World, recipe).Members);
        Assert.Throws<InvalidOperationException>(() => s.Population.PreparePlayerSquad(s.World, start, s.B, 11, 2));
    }

    [Fact]
    public void AssemblyFailureRemovesPartialCharactersWithoutClaimingUniques()
    {
        using var s = new Scene();
        string normal = s.Data.Db.OfType(FcsRecordType.Character).First(r => r.StringId != "unique-player").StringId;
        var start = s.Start with { Squad = [new RecordLink("unique-player", 1, 0, 0), new RecordLink(normal, 1, 0, 0)] };
        var recipe = s.Population.PreparePlayerSquad(s.World, start, s.A, 10, 2);
        s.Walk.ThrowAt = s.Walk.Calls + recipe.CharacterCount + 3;
        Assert.Throws<IOException>(() => s.Population.MaterializePlayerSquad(s.World, recipe));
        Assert.Equal(0, s.World.Characters.Count); Assert.Empty(s.World.Squads.All);
        Assert.False(s.World.Uniques.IsClaimed("unique-player"));
        Assert.Throws<InvalidOperationException>(() => s.Population.MaterializePlayerSquad(s.World, recipe));
    }

    [Fact]
    public void PreparedRecipeCannotBeUsedByAnotherWorld()
    {
        using var source = new Scene(); using var target = new Scene();
        var recipe = source.Population.PreparePlayerSquad(source.World, source.Start, source.A, 10, 10);
        Assert.Throws<InvalidOperationException>(() => target.Population.MaterializePlayerSquad(target.World, recipe));
        Assert.Throws<InvalidOperationException>(() => source.Population.MaterializePlayerSquad(target.World, recipe));
        Assert.Equal(0, target.World.Characters.Count);
    }

    [Fact]
    public void ScopedGenerationSeedDoesNotDependOnOtherPlayersSlotAllocation()
    {
        using var first = new Scene(); using var second = new Scene(); using var third = new Scene();
        second.World.Characters.Spawn(new CharacterHot(), new CharacterCold(), 0);
        float[] Generate(Scene scene, ulong seed)
        {
            var start = scene.Start with { Squad = [new RecordLink("random-player", 1, 0, 0)] };
            var squad = scene.Population.MaterializePlayerSquad(scene.World, scene.Population.PreparePlayerSquad(scene.World, start, scene.A, seed, 1));
            return scene.World.Characters.Cold(squad.Leader.Slot)!.Stats!.ToArray();
        }
        var a = Generate(first, 123); var b = Generate(second, 123); var c = Generate(third, 124);
        Assert.Equal(a, b); Assert.False(a.SequenceEqual(c));
    }

    [Fact]
    public void CapacityLostAfterPreparationRejectsTheRosterWithoutPartialSpawning()
    {
        using var s = new Scene();
        var recipe = s.Population.PreparePlayerSquad(s.World, s.Start, s.A, 10, 10);
        for (int i = 0; i < s.Data.Bodies.Constants.MaxSquads; i++)
            SquadManagement.NewSquad(s.World, s.Data.Db, Vector3.Zero, "occupied", s.A);
        Assert.Throws<InvalidOperationException>(() => s.Population.MaterializePlayerSquad(s.World, recipe));
        Assert.Equal(0, s.World.Characters.Count);
    }
}
