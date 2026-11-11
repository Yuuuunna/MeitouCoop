using Meitou.Simulation.Player;
using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class PlayerCommandTests
{
    sealed class Scene : IDisposable
    {
        readonly MovementSystem _movement;
        public SimWorld World { get; }
        public PlayerId A { get; } = new(2);
        public PlayerId B { get; } = new(3);
        public CharacterId First { get; }
        public CharacterId Second { get; }

        public Scene(bool sameFaction = false)
        {
            var walk = new OpenGroundWalkability((_, _) => 300);
            _movement = new MovementSystem(new PathService(walk, true));
            World = new SimWorld(new WorldSettings { PublishSnapshots = false }, walk, [new PlayerSystem(), _movement]);
            World.Players.Register(A);
            World.Players.Register(B);
            World.Players.SetMembership(A, 6, FactionPermission.All);
            World.Players.SetMembership(B, sameFaction ? 6 : 7, FactionPermission.All);
            First = Spawn(6, Vector3.Zero);
            Second = Spawn(sameFaction ? 6 : 7, new Vector3(100, 0, 0));
            World.Players.AssignInitial(A, First);
            World.Players.AssignInitial(B, Second);
        }

        CharacterId Spawn(int faction, Vector3 at) => World.Characters.Spawn(
            new CharacterHot { Position = at + new Vector3(0, 300, 0), MaxSpeed = 40, WalkSpeed = 15, Task = (byte)CharacterTask.Idle },
            new CharacterCold { IsPlayer = true, Faction = faction, Race = Bodies.BodyFixtures.Human,
                Medical = Bodies.BodyFixtures.Healthy(), Stats = new Meitou.Simulation.Bodies.CharacterStats() }, 0);

        public void Dispose() { World.Dispose(); _movement.Dispose(); }
    }

    [Fact]
    public void PlayersSelectAndMoveTheirOwnRolesInOneWorld()
    {
        using var scene = new Scene();
        var world = scene.World;
        world.Commands.Enqueue(scene.A, new SelectCommand([scene.First]) { Tick = 0 });
        world.Commands.Enqueue(scene.B, new SelectCommand([scene.Second]) { Tick = 0 });
        world.Commands.Enqueue(scene.A, new MoveOrder([scene.First], new Vector3(500, 300, 0)) { Tick = 0 });
        world.Commands.Enqueue(scene.B, new MoveOrder([scene.Second], new Vector3(100, 300, 500)) { Tick = 0 });
        world.RunTicks(30);
        Assert.Equal(scene.First, Assert.Single(world.Players.Find(scene.A)!.State.Selection));
        Assert.Equal(scene.Second, Assert.Single(world.Players.Find(scene.B)!.State.Selection));
        Assert.Empty(world.Player.Selection);
        Assert.True(world.Characters.Previous[scene.First.Slot].Position.X > 0);
        Assert.True(world.Characters.Previous[scene.Second.Slot].Position.Z > 0);
        Assert.Equal(new Vector2(500, 0), world.Characters.Previous[scene.First.Slot].Goal);
        Assert.Equal(new Vector2(100, 500), world.Characters.Previous[scene.Second.Slot].Goal);
    }

    [Fact]
    public void AttributedOrdersCannotBorrowLocalSelectionOrOtherPlayersRoles()
    {
        using var scene = new Scene(sameFaction: true);
        var world = scene.World;
        world.Player.Selection.Add(scene.Second);
        world.Commands.Enqueue(scene.A, new MoveOrder([], new Vector3(500, 300, 0)) { Tick = 0 });
        world.Commands.Enqueue(scene.A, new MoveOrder([scene.First, scene.Second], new Vector3(500, 300, 0)) { Tick = 0 });
        world.Commands.Enqueue(scene.A, new SelectCommand([scene.Second]) { Tick = 0 });
        world.Commands.Enqueue(new PlayerId(999), new MoveOrder([scene.First], new Vector3(500, 300, 0)) { Tick = 0 });
        world.RunTick();
        Assert.Equal((byte)CharacterTask.Idle, world.Characters.Previous[scene.First.Slot].Task);
        Assert.Equal((byte)CharacterTask.Idle, world.Characters.Previous[scene.Second.Slot].Task);
        Assert.Empty(world.Players.Find(scene.A)!.State.Selection);
    }

    [Fact]
    public void QueuedOrdersRemainInvalidAfterControlLeavesAndReturnsToTheirIssuer()
    {
        using var scene = new Scene(sameFaction: true);
        var world = scene.World;
        world.Commands.Enqueue(scene.A, new MoveOrder([scene.First], new Vector3(500, 300, 0)) { Tick = 0 });
        var original = world.Players.ControlOf(scene.First)!.Value;
        Assert.True(world.Players.TryTransfer(scene.A, original, scene.B, out var moved));
        Assert.True(world.Players.TryTransfer(scene.A, moved, scene.A, out _));
        world.RunTick();
        Assert.Equal((byte)CharacterTask.Idle, world.Characters.Previous[scene.First.Slot].Task);
        world.Commands.Enqueue(scene.A, new MoveOrder([scene.First], new Vector3(500, 300, 0)) { Tick = world.Tick });
        world.RunTick();
        Assert.Equal((byte)CharacterTask.GoTo, world.Characters.Previous[scene.First.Slot].Task);
    }

    [Fact]
    public void AttributedDebugOrUnauditedCommandsFailClosed()
    {
        using var scene = new Scene();
        Assert.Throws<NotSupportedException>(() => scene.World.Commands.Enqueue(scene.A,
            new SandboxMovement(scene.First, null, 10000) { Tick = 0 }));
        Assert.Throws<NotSupportedException>(() => scene.World.Commands.Enqueue(scene.A,
            new RepathCommand { Tick = 0 }));
        Assert.Equal(0, scene.World.Commands.Count);
    }
}
