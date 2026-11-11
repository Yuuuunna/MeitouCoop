using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Population;

namespace Meitou.Tests.Gameplay;

public class RuntimeFactionTests
{
    static FactionRelations Relations() => FactionRelations.Build([
        new FactionData { Id = "npc", Name = "NPC", DefaultRelation = -5,
            Relations = new Dictionary<string, int> { ["start"] = 12 }, Coexistence = ["start"] },
        new FactionData { Id = "start", Name = "Start", DefaultRelation = 0,
            Relations = new Dictionary<string, int> { ["npc"] = -9 } },
    ]);

    [Fact]
    public void NewFactionsPreserveLiveDiplomacyAndUseTemplateDefaultsNotAnotherPlayersHistory()
    {
        var relations = Relations();
        relations.Set(0, 1, -80);
        relations.SetWar(0, 1, true);
        relations.SetRank(0, 4);
        relations.SetGlobalTrust(1, 7);
        var catalogue = relations.Factions;
        int a = relations.RegisterFaction("runtime:a", "A", 1);
        Assert.Equal(2, a);
        Assert.Equal(3, catalogue.Count);
        Assert.Equal(-80, relations.Get(0, 1));
        Assert.True(relations.AtWar(0, 1));
        Assert.Equal(4, relations.Rank(0));
        Assert.Equal(7, relations.GlobalTrust(1));
        Assert.Equal(12, relations.Get(0, a));
        Assert.Equal(-9, relations.Get(a, 0));
        Assert.True(relations.Coexists(0, a));
        Assert.False(relations.AtWar(0, a));
        Assert.Equal("start", relations.TemplateId(a));

        relations.Set(0, a, -90);
        relations.SetWar(a, 0, true);
        int b = relations.RegisterFaction("runtime:b", "B", a);
        Assert.Equal(3, b);
        Assert.Equal(-90, relations.Get(0, a));
        Assert.True(relations.AtWar(a, 0));
        Assert.Equal(12, relations.Get(0, b));
        Assert.Equal(-9, relations.Get(b, 0));
        Assert.Equal(0, relations.Get(a, b));
        Assert.Equal(0, relations.Get(b, a));
        Assert.Equal(100, relations.Get(b, b));
        Assert.Equal(0, relations.Rank(b));
        Assert.Equal(0, relations.GlobalTrust(b));
        Assert.Equal("start", relations.TemplateId(b));
        Assert.Equal(b, relations.IndexOf("runtime:b"));
    }

    [Fact]
    public void InvalidRegistrationDoesNotMutateCatalogueOrRelations()
    {
        var relations = Relations();
        long changes = relations.Changes;
        Assert.Throws<InvalidOperationException>(() => relations.RegisterFaction("npc", "duplicate", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => relations.RegisterFaction("new", "invalid", 9));
        Assert.Throws<ArgumentException>(() => relations.RegisterFaction(" ", "invalid", 1));
        Assert.Equal(2, relations.Count);
        Assert.Equal(changes, relations.Changes);
        Assert.Equal(12, relations.Get(0, 1));
    }

    [Fact]
    public void ResetKeepsRuntimeIdentitiesAndRestoresTheirTemplateRelations()
    {
        var relations = Relations();
        int a = relations.RegisterFaction("runtime:a", "A", 1);
        int b = relations.RegisterFaction("runtime:b", "B", 1);
        relations.Set(0, a, -90);
        relations.SetWar(b, 0, true);
        relations.Reset();
        Assert.Equal(a, relations.IndexOf("runtime:a"));
        Assert.Equal(b, relations.IndexOf("runtime:b"));
        Assert.Equal(12, relations.Get(0, a));
        Assert.Equal(-9, relations.Get(b, 0));
        Assert.False(relations.AtWar(b, 0));
        Assert.True(relations.Coexists(0, b));
    }

    [Fact]
    public void PopulationLookupsFollowRuntimeRegistrationAndRetainTheContentTemplate()
    {
        var relations = Relations();
        var data = new PopulationData
        {
            Db = Simulation.SyntheticTown.Database(), Sites = [], Factions = relations.Factions, Relations = relations,
        };
        Assert.Equal(-1, data.FactionIndex("runtime:a")); // populate the lookup cache before registration
        int a = data.RegisterFaction("runtime:a", "A", 1);
        Assert.Equal(a, data.FactionIndex("runtime:a"));
        Assert.Equal("runtime:a", data.Factions[a].Id);
        Assert.Equal("start", data.FactionTemplate(a));
        Assert.Equal("A", data.Factions[a].Name);
        Assert.Equal(12, data.Relations.Get(0, a));
    }
}
