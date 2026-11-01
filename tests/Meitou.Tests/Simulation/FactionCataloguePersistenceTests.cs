using Meitou.Simulation.Saving;

namespace Meitou.Tests.Simulation;

public class FactionCataloguePersistenceTests
{
    [Fact]
    public void FreshContentReconstructsStableRuntimeIndicesAndFlattenedTemplates()
    {
        var source = SyntheticTown.Data(SyntheticTown.Database());
        int content = source.Factions.Count;
        int a = source.RegisterFaction("runtime:a", "A", source.PlayerFaction);
        int b = source.RegisterFaction("runtime:b", "B", a);
        var state = FactionCatalogueSave.Capture(source);
        var target = SyntheticTown.Data(SyntheticTown.Database());
        Assert.Equal(-1, target.FactionIndex("runtime:a")); // Exercise the pre-existing cached lookup.
        FactionCatalogueSave.Restore(target, state);
        Assert.Equal(content, target.Relations.ContentCount);
        Assert.Equal(a, target.FactionIndex("runtime:a"));
        Assert.Equal(b, target.FactionIndex("runtime:b"));
        Assert.Equal(target.Factions[target.PlayerFaction].Id, target.FactionTemplate(b));
        Assert.Equal("A", target.Factions[a].Name);
        Assert.Equal("B", target.Factions[b].Name);
        FactionCatalogueSave.Restore(target, state); // Matching catalogue is reusable, never duplicated.
        Assert.Equal(content + 2, target.Factions.Count);
        Assert.Equal(state.Factions, FactionCatalogueSave.Capture(target).Factions);
    }

    [Theory]
    [InlineData("late-template")]
    [InlineData("duplicate")]
    [InlineData("base-order")]
    [InlineData("base-count")]
    [InlineData("version")]
    public void InvalidCatalogueIsRejectedBeforeAppendingAnyRuntimeFaction(string defect)
    {
        var source = SyntheticTown.Data(SyntheticTown.Database());
        int a = source.RegisterFaction("runtime:a", "A", source.PlayerFaction);
        int b = source.RegisterFaction("runtime:b", "B", a);
        var state = FactionCatalogueSave.Capture(source);
        switch (defect)
        {
            case "late-template": state.Factions[b] = state.Factions[b] with { Template = "missing" }; break;
            case "duplicate": state.Factions[b] = state.Factions[b] with { Id = state.Factions[a].Id }; break;
            case "base-order": (state.Factions[0], state.Factions[1]) = (state.Factions[1], state.Factions[0]); break;
            case "base-count": state = state with { ContentCount = 0 }; break;
            case "version": state = state with { Version = 99 }; break;
        }
        var target = SyntheticTown.Data(SyntheticTown.Database());
        int count = target.Factions.Count;
        var diplomacy = target.Relations.Capture();
        Assert.Throws<InvalidDataException>(() => FactionCatalogueSave.Restore(target, state));
        Assert.Equal(count, target.Factions.Count);
        Assert.Equal(-1, target.FactionIndex("runtime:a"));
        Assert.Equal(diplomacy.Relations, target.Relations.Capture().Relations);
    }

    [Fact]
    public void ConflictingExistingRuntimeFactionIsNeverSilentlyReplaced()
    {
        var source = SyntheticTown.Data(SyntheticTown.Database());
        source.RegisterFaction("runtime:a", "A", source.PlayerFaction);
        var state = FactionCatalogueSave.Capture(source);
        var target = SyntheticTown.Data(SyntheticTown.Database());
        int index = target.RegisterFaction("runtime:a", "Different creation name", target.PlayerFaction);
        Assert.Throws<InvalidDataException>(() => FactionCatalogueSave.Restore(target, state));
        Assert.Equal("Different creation name", target.Factions[index].Name);
    }

    [Fact]
    public void DiplomacyCapturePreservesRawDirectionsEvenWhenLegacyQueriesAliasThePlayerRow()
    {
        var source = SyntheticTown.Data(SyntheticTown.Database());
        int player = source.PlayerFaction, other = source.FactionIndex("20-t");
        source.Relations.PlayerFaction = player;
        source.Relations.Set(player, other, 19);
        source.Relations.Set(other, player, -77);
        source.Relations.SetWar(player, other, true);
        source.Relations.SetGlobalTrust(other, -12.5f);
        source.Relations.SetRank(other, 3);
        var saved = source.Relations.Capture();
        Assert.Equal(-77, source.Relations.Get(player, other));
        var target = SyntheticTown.Data(SyntheticTown.Database());
        target.Relations.Restore(saved);
        Assert.Equal(player, target.Relations.PlayerFaction);
        target.Relations.PlayerFaction = -1;
        Assert.Equal(19, target.Relations.Get(player, other));
        Assert.Equal(-77, target.Relations.Get(other, player));
        Assert.True(target.Relations.AtWar(player, other));
        Assert.False(target.Relations.AtWar(other, player));
        Assert.Equal(-12.5f, target.Relations.GlobalTrust(other));
        Assert.Equal(3, target.Relations.Rank(other));
        saved.Relations[player * saved.Factions.Length + other] = 1;
        Assert.Equal(19, target.Relations.Get(player, other));
    }

    [Fact]
    public void InvalidDiplomacyTablesCannotPartiallyReplaceTheLiveTable()
    {
        var data = SyntheticTown.Data(SyntheticTown.Database());
        var saved = data.Relations.Capture();
        var before = data.Relations.Capture();
        Assert.Throws<InvalidDataException>(() => data.Relations.Restore(saved with { GlobalTrust = [float.NaN, 0] }));
        Assert.Throws<InvalidDataException>(() => data.Relations.Restore(saved with { Wars = [] }));
        saved.Wars[0] = true;
        Assert.Throws<InvalidDataException>(() => data.Relations.Restore(saved));
        Assert.Equal(before.Relations, data.Relations.Capture().Relations);
        Assert.Equal(before.GlobalTrust, data.Relations.Capture().GlobalTrust);
        Assert.Equal(before.Wars, data.Relations.Capture().Wars);
    }
}
