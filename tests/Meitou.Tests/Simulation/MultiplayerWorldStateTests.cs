using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Factions;

namespace Meitou.Tests.Simulation;

public class MultiplayerWorldStateTests
{
    sealed class Fixture
    {
        public FactionRelations Relations { get; }
        public WorldStates States { get; }
        public UniqueCharacters Uniques { get; } = new();
        public int A { get; }
        public int B { get; }
        public int X { get; }
        public int Y { get; }

        public Fixture()
        {
            var npc = SyntheticTown.Rec("story", FcsRecordType.Character, "Story NPC");
            npc.Bools["unique"] = true;
            var diplomacy = SyntheticTown.Rec("diplomacy", FcsRecordType.WorldEventState, "Diplomacy");
            diplomacy.References["player ally"] = [SyntheticTown.Ref("x", 1)];
            diplomacy.References["player enemy"] = [SyntheticTown.Ref("y", 1)];
            var involved = SyntheticTown.Rec("involved", FcsRecordType.WorldEventState, "Involved");
            involved.References["player ally"] = [SyntheticTown.Ref("x", 1)];
            involved.References["player enemy"] = [SyntheticTown.Ref("y", 1)];
            involved.References["NPC is"] = [SyntheticTown.Ref("story", 0)];
            involved.Bools["player involvement"] = true;
            var global = SyntheticTown.Rec("global", FcsRecordType.WorldEventState, "Global death");
            global.References["NPC is"] = [SyntheticTown.Ref("story", 0)];
            var variable = SyntheticTown.Rec("progress", (FcsRecordType)VariableData.RecordType, "Progress");
            variable.Ints["value"] = 0;
            var gated = SyntheticTown.Rec("gated", FcsRecordType.WorldEventState, "Gated involvement");
            gated.References["player ally"] = [SyntheticTown.Ref("x", 1)];
            gated.References["player enemy"] = [SyntheticTown.Ref("y", 1)];
            gated.References["NPC is"] = [SyntheticTown.Ref("story", 0)];
            gated.Bools["player involvement"] = true;
            gated.References["variable equals"] = [SyntheticTown.Ref("progress", 1)];
            var variableOnly = SyntheticTown.Rec("variable-only", FcsRecordType.WorldEventState, "Global variable");
            variableOnly.References["variable equals"] = [SyntheticTown.Ref("progress", 1)];
            var invalid = SyntheticTown.Rec("invalid-variable", FcsRecordType.WorldEventState, "Missing variable");
            invalid.References["variable equals"] = [SyntheticTown.Ref("missing-variable", 1)];
            GameDatabase db = SyntheticTown.Database(extra: [npc, diplomacy, involved, global, variable, gated, variableOnly, invalid,
                SyntheticTown.Rec("a", FcsRecordType.Faction, "A"), SyntheticTown.Rec("b", FcsRecordType.Faction, "B"),
                SyntheticTown.Rec("x", FcsRecordType.Faction, "X"), SyntheticTown.Rec("y", FcsRecordType.Faction, "Y")]);
            Relations = FactionRelations.Build(db);
            A = Relations.IndexOf("a"); B = Relations.IndexOf("b"); X = Relations.IndexOf("x"); Y = Relations.IndexOf("y");
            States = new WorldStates(db, Relations);
        }
    }

    [Fact]
    public void OneFactionMustSatisfyEveryDiplomaticConditionAndWorkersSeeOneSnapshot()
    {
        var f = new Fixture();
        f.Relations.Set(f.X, f.A, 100);
        f.Relations.Set(f.Y, f.A, 0);
        f.Relations.Set(f.X, f.B, 0);
        f.Relations.Set(f.Y, f.B, -100);
        f.States.Refresh(f.Uniques, [f.A, f.B]);
        Assert.False(f.States.IsTrue("diplomacy"));
        f.Relations.Set(f.Y, f.A, -100);
        Assert.False(f.States.IsTrue("diplomacy")); // no live mutable relation reads from a population worker
        f.States.Refresh(f.Uniques, [f.B, f.A, f.A]);
        Parallel.For(0, 20, _ => Assert.True(f.States.IsTrue("diplomacy")));
    }

    [Fact]
    public void AnotherFactionsDeedCannotSupplyTheDiplomaticWitness()
    {
        var f = new Fixture();
        f.Relations.Set(f.X, f.B, 100);
        f.Relations.Set(f.Y, f.B, -100);
        f.Uniques.Died("story", true, f.A);
        f.States.Refresh(f.Uniques, [f.A, f.B]);
        Assert.False(f.States.IsTrue("involved"));
        Assert.True(f.States.IsTrue("global"));
        f.Relations.Set(f.X, f.A, 100);
        f.Relations.Set(f.Y, f.A, -100);
        f.States.Refresh(f.Uniques, [f.A, f.B]);
        Assert.True(f.States.IsTrue("involved"));
    }

    [Fact]
    public void LegacyPlayerInvolvementBelongsOnlyToTheImportedFaction()
    {
        var f = new Fixture();
        f.Uniques.Load("story", UniqueState.Dead, true, Hand.Null);
        f.Relations.Set(f.X, f.B, 100);
        f.Relations.Set(f.Y, f.B, -100);
        f.States.Refresh(f.Uniques, [f.A, f.B], legacyPlayerFaction: f.A);
        Assert.False(f.States.IsTrue("involved"));
        f.Relations.Set(f.X, f.A, 100);
        f.Relations.Set(f.Y, f.A, -100);
        f.States.Refresh(f.Uniques, f.A); // the single-player path retains its original interpretation
        Assert.True(f.States.IsTrue("involved"));
    }

    [Fact]
    public void GlobalFactsSurviveAnEmptyPlayerSetAndFreeingClearsCaptureProvenance()
    {
        var f = new Fixture();
        f.Uniques.Captured("story", true, f.A);
        Assert.Contains(f.A, f.Uniques.Find("story")!.InvolvedFactions);
        f.Uniques.Freed("story");
        Assert.Empty(f.Uniques.Find("story")!.InvolvedFactions);
        Assert.False(f.Uniques.Find("story")!.Player);
        f.Uniques.Died("story", false);
        f.States.Refresh(f.Uniques, Array.Empty<int>());
        Assert.True(f.States.IsTrue("global"));
        Assert.False(f.States.IsTrue("diplomacy"));
    }

    /// <summary>
    /// Variable updates are visible immediately, but cannot combine one player's deed with another player's diplomatic conditions.
    /// </summary>
    [Fact]
    public void LiveVariablesGateTheSameFactionWitnessWithoutCombiningPlayersFacts()
    {
        var f = new Fixture();
        var variables = new WorldVariables();
        variables.Set("progress", 1);
        f.Uniques.Died("story", true, f.A);
        f.Relations.Set(f.X, f.B, 100);
        f.Relations.Set(f.Y, f.B, -100);
        f.States.Refresh(f.Uniques, [f.A, f.B], variables: variables);

        Assert.False(f.States.IsTrue("gated"));
        Assert.False(f.States.IsTrueForFaction("gated", f.A));
        Assert.False(f.States.IsTrueForFaction("gated", f.B));

        f.Relations.Set(f.X, f.A, 100);
        f.Relations.Set(f.Y, f.A, -100);
        f.States.Refresh(f.Uniques, [f.A, f.B], variables: variables);
        Assert.True(f.States.IsTrue("gated"));
        Assert.True(f.States.IsTrueForFaction("gated", f.A));
        Assert.False(f.States.IsTrueForFaction("gated", f.B));

        variables.Set("progress", 0);
        Assert.False(f.States.IsTrue("gated"));
        Assert.False(f.States.IsTrueForFaction("gated", f.A));
    }

    /// <summary>
    /// Global variable gates survive an empty player set; explicit mod overrides still win and missing variable lists cannot pass.
    /// </summary>
    [Fact]
    public void GlobalVariablesAndModOverridesKeepTheirPrecedenceWithoutPlayers()
    {
        var f = new Fixture();
        var variables = new WorldVariables();
        var mods = new ModWorldStates();
        f.States.Refresh(f.Uniques, Array.Empty<int>(), mods: mods, variables: variables);
        Assert.False(f.States.IsTrue("variable-only"));

        variables.Set("progress", 1);
        Assert.True(f.States.IsTrue("variable-only"));
        Assert.False(f.States.IsTrue("invalid-variable"));

        variables.Set("progress", 0);
        mods.Set("variable-only", true);
        f.States.Refresh(f.Uniques, Array.Empty<int>(), mods: mods, variables: variables);
        Assert.True(f.States.IsTrue("variable-only"));
        Assert.True(f.States.IsTrueForFaction("variable-only", f.A));

        mods.Clear("variable-only");
        f.States.Refresh(f.Uniques, f.A, mods, variables);
        Assert.False(f.States.IsTrue("variable-only"));
        variables.Set("progress", 1);
        Assert.True(f.States.IsTrueForFaction("variable-only", f.A));
    }
}
