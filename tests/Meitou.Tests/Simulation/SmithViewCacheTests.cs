using Meitou.Data;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Localisation;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Production;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Cached smith names follow a mod's first assignment of a lasting character identity, including while a bench is idle.
/// </summary>
[Collection("Shown text")]
public sealed class SmithViewCacheTests
{
    /// <summary>
    /// Giving an existing smith an ID changes the text source's context without changing the raw name, machine inputs or text revision.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Giving_a_smith_his_first_identity_refreshes_the_cached_name(bool machinesWorked)
    {
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
        var cold = new CharacterCold
        {
            Name = "Beep", Race = Human, Stats = new CharacterStats(), Medical = MedicalState.Create(Human, 50),
        };
        var smith = world.Characters.Spawn(new CharacterHot { Health = 100 }, cold, 0);
        var machine = new Machine
        {
            Site = "bench", Smith = smith, Craft = new CraftState(),
            Data = new MachineData { Id = "bench", Name = "Bench", Stat = (int)StatsEnumerated.Engineering, Craft = new CraftBench() },
        };
        world.Machines.Add(machine);
        var cache = new SmithViewCache(new GameDatabase());
        var host = new object();
        var asked = new List<ShownTextContext>();
        ShownText.Add(host, "smith-names", context =>
        {
            asked.Add(context);
            return context.Character == 0 ? "Unassigned smith" : "Assigned smith";
        });
        try
        {
            var before = cache.Of(world);
            Assert.Equal("Unassigned smith", Assert.Single(before).Value.Name);
            Assert.Same(before, cache.Of(world));
            int revision = ShownText.Revision;

            var identity = world.LastingIds.GetOrGive(smith);
            if (machinesWorked) world.Machines.Work();
            var after = cache.Of(world);
            Assert.NotSame(before, after);
            Assert.Equal("Assigned smith", Assert.Single(after).Value.Name);
            Assert.Same(after, cache.Of(world));
            Assert.Equal(revision, ShownText.Revision);
            Assert.Equal("Beep", cold.Name);
            Assert.Equal(2, asked.Count);
            Assert.Equal(0ul, asked[0].Character);
            Assert.Equal(identity.Value, asked[1].Character);
            Assert.All(asked, context => Assert.Equal(ShownPlace.Window, context.Place));
        }
        finally { ShownText.Remove(host); }
    }
}
