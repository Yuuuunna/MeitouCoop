using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Ai;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class FactionPresentationTests
{
    [Fact]
    public void FactionPanelsFilterContractsCampaignsAndStaleTargetNamesByCurrentPermissions()
    {
        var data = PopulationData.Create(SyntheticTown.Database(), []);
        int fa = data.RegisterFaction("panel-a", "Panel A", data.PlayerFaction);
        int fb = data.RegisterFaction("panel-b", "Panel B", data.PlayerFaction);
        var enemies = Enumerable.Range(0, data.Factions.Count).Where(i => i != fa && i != fb).Take(2).ToArray();
        Assert.Equal(2, enemies.Length);
        var population = new PopulationSystem(data, new PopulationSettings { Background = false });
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), [new PlayerSystem(data), population]);
        (WorldPlayer Player, CharacterId Role, AuthorityConnection Connection) Add(ulong id, int faction, FactionPermission permissions, string name)
        {
            var player = world.Players.Register(new(id)); world.Players.SetMembership(player.Id, faction, permissions);
            var role = world.Characters.Spawn(new(), new() { Faction = faction, IsPlayer = true, Name = name }, 0);
            world.Players.AssignInitial(player.Id, role);
            var connection = world.Authority.Open(player.Id); Assert.True(world.Authority.BeginSynchronization(connection));
            return (player, role, connection);
        }
        var a = Add(2, fa, FactionPermission.All, "A protectee");
        var b = Add(3, fb, FactionPermission.All, "B protectee");
        var c = Add(4, fa, FactionPermission.None, "C protectee");
        var package = new AiPackage { Id = "guard-contract", Name = "Guard", Signal = 12, Roles = [[], [], [], [], []] };
        void Contract(CharacterId target)
        {
            var board = new Blackboard([]); board.StartContract(package, world.GameHours, 8, target: target);
            world.Squads.Add(new Squad { Id = world.Squads.NextId(), TemplateId = "guards", Faction = enemies[0], Ai = board });
        }
        Contract(a.Role); Contract(b.Role); Contract(c.Role);
        var townA = world.Buildings.AddTown(Vector3.Zero, fa, "A base");
        var townB = world.Buildings.AddTown(new(20000, 0, 0), fb, "B private base");
        population.EnsureWars(world);
        var war = population.Wars[enemies[0]];
        var active = new CampaignInstance { Faction = enemies[0], Id = 1, CampaignId = "active-A", Enemy = fa, Target = CampaignTown.OfPlayer(townA.Number) };
        war.Actives.Add(active);
        war.Requests.Add(new() { CampaignId = "queued-A", Enemy = fa, Due = world.GameHours + 5 });
        war.Requests.Add(new() { CampaignId = "queued-B", Enemy = fb, Due = world.GameHours + 7 });
        population.Wars[enemies[1]].Requests.Add(new() { CampaignId = "hidden-B", Enemy = fb, Due = world.GameHours + 9 });
        PlayerFactionView View(AuthorityConnection connection, int faction) => world.Authority.CaptureView(connection, new(), new(100))!.Factions.Single(f => f.Faction == faction);
        var av = View(a.Connection, fa); var bv = View(b.Connection, fb); var cv = View(c.Connection, fa);
        Assert.Contains(av.Contracts, line => line.Value == "A protectee"); Assert.Contains(av.Contracts, line => line.Value == "C protectee");
        Assert.DoesNotContain(av.Contracts, line => line.Value == "B protectee");
        Assert.Contains(bv.Contracts, line => line.Value == "B protectee"); Assert.DoesNotContain(bv.Contracts, line => line.Value == "A protectee");
        Assert.Contains(cv.Contracts, line => line.Value == "C protectee"); Assert.DoesNotContain(cv.Contracts, line => line.Value == "A protectee");
        Assert.Empty(cv.UpcomingEvents);
        Assert.Contains(av.UpcomingEvents, line => line.Label == "active-A"); Assert.Contains(av.UpcomingEvents, line => line.Label == "queued-A");
        Assert.DoesNotContain(av.UpcomingEvents, line => line.Label is "queued-B" or "hidden-B");
        Assert.Single(av.UpcomingEvents, line => line.Kind == FactionInfoKind.Heading);
        active.Target = CampaignTown.OfPlayer(townB.Number);
        Assert.DoesNotContain(View(a.Connection, fa).UpcomingEvents, line => line.Value.Contains("B private base", StringComparison.Ordinal));
        world.Players.SetMembership(a.Player.Id, fa, FactionPermission.ReadFunds);
        var limited = View(a.Connection, fa);
        Assert.Empty(limited.UpcomingEvents); Assert.Contains(limited.Contracts, line => line.Value == "A protectee");
        Assert.DoesNotContain(limited.Contracts, line => line.Value == "C protectee");
    }
}
