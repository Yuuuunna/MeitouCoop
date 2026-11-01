using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Production;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class MultiplayerProspectingTests
{
    static SimWorld World()
    {
        var data = SyntheticTown.Data(SyntheticTown.Database());
        return new(new WorldSettings { PublishSnapshots = false, Threads = 1 }, new OpenGroundWalkability((_, _) => 0), [new ProductionSystem(data)]);
    }
    static (WorldPlayer Player, CharacterId Character) Add(SimWorld world, ulong id, int faction)
    {
        var player = world.Players.Register(new(id));
        world.Players.SetMembership(player.Id, faction, FactionPermission.All);
        var role = world.Characters.Spawn(new() { Position = new Vector3(id * 10, 0, 0) }, new() { Name = "Prospector " + id, Faction = faction, IsPlayer = true }, 0);
        world.Players.AssignInitial(player.Id, role);
        return (player, role);
    }
    static void Run(SimWorld world, int count = 40) { for (int i = 0; i < count; i++) world.RunTick(); }

    [Fact]
    public void SimultaneousSurveysStayPrivateAndEachPlayerHasItsOwnResultSequence()
    {
        using var world = World(); var a = Add(world, 2, 7); var b = Add(world, 3, 8);
        world.Commands.Enqueue(a.Player.Id, new ProspectOrder(a.Character) { Tick = 0 });
        world.Commands.Enqueue(b.Player.Id, new ProspectOrder(b.Character) { Tick = 0 });
        Run(world);
        Assert.Equal("Prospector 2", a.Player.State.Prospected!.Prospector);
        Assert.Equal("Prospector 3", b.Player.State.Prospected!.Prospector);
        Assert.Equal(1, a.Player.State.Prospected.Serial); Assert.Equal(1, b.Player.State.Prospected.Serial);
        Assert.NotEqual(a.Player.State.Prospected.Position, b.Player.State.Prospected.Position);
        Assert.Null(world.Prospected);
        var ca = world.Authority.Open(a.Player.Id); Assert.True(world.Authority.BeginSynchronization(ca));
        var cb = world.Authority.Open(b.Player.Id); Assert.True(world.Authority.BeginSynchronization(cb));
        Assert.Equal(a.Player.State.Prospected, world.Authority.CaptureView(ca, new(), new(100))!.Prospected);
        Assert.Equal(b.Player.State.Prospected, world.Authority.CaptureView(cb, new(), new(100))!.Prospected);
        world.Commands.Enqueue(a.Player.Id, new ProspectOrder(b.Character) { Tick = world.Tick });
        Run(world); Assert.Equal(1, b.Player.State.Prospected.Serial);
        world.Commands.Enqueue(a.Player.Id, new ProspectOrder(a.Character) { Tick = world.Tick });
        Run(world); Assert.Equal(2, a.Player.State.Prospected.Serial); Assert.Equal(1, b.Player.State.Prospected.Serial);
    }

    [Fact]
    public void RoleTransferCancelsTheOldSurveyAndNewOwnerStartsWithFreshProgress()
    {
        using var world = World(); var a = Add(world, 2, 7); var b = Add(world, 3, 7);
        world.Commands.Enqueue(a.Player.Id, new ProspectOrder(a.Character) { Tick = 0 });
        Run(world, 15);
        Assert.True(world.Players.TryTransfer(a.Player.Id, world.Players.ControlOf(a.Character)!.Value, b.Player.Id, out _));
        Run(world);
        Assert.Null(a.Player.State.Prospected); Assert.Null(b.Player.State.Prospected);
        world.Commands.Enqueue(b.Player.Id, new ProspectOrder(a.Character) { Tick = world.Tick });
        Run(world, 15); Assert.Null(b.Player.State.Prospected);
        Run(world); Assert.Equal("Prospector 2", b.Player.State.Prospected!.Prospector); Assert.Null(a.Player.State.Prospected);
    }

    [Fact]
    public void LegacySurveyStillPublishesToTheLocalWorldProperty()
    {
        using var world = World(); world.Player.Faction = 0;
        var role = world.Characters.Spawn(new(), new() { Faction = 0, IsPlayer = true, Name = "Local" }, 0);
        world.Commands.Enqueue(new ProspectOrder(role) { Tick = 0 });
        Run(world);
        Assert.Equal("Local", world.Prospected!.Prospector); Assert.Same(world.Player.Prospected, world.Prospected);
        Assert.NotNull(world.Prospected.Map);
    }
}
