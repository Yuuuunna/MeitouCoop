using System.Numerics;
using Meitou.Data.Save;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Tests.Engine;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public sealed class NativeInteractionTests
{
    sealed class Input(Action<SimWorld> action) : ITickSystem
    {
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands) => action(world);
    }

    sealed class OrdersSeen : IPlayerOrderObserver
    {
        public readonly List<(PlayerId Player, CharacterId Character)> Seen = [];
        public void OnPlayerOrder(SimWorld world, PlayerId issuer, CharacterId character, SimCommand command) => Seen.Add((issuer, character));
    }

    static CharacterId Spawn(SimWorld world, float x, bool player, int faction = 1) =>
        world.Characters.Spawn(new CharacterHot { Position = new(x, 0, 0), Health = 100 },
            new CharacterCold { Name = "speaker", IsPlayer = player, Faction = faction }, world.Tick);

    static AuthorityConnection Admit(SimWorld world, PlayerId owner, CharacterId id)
    {
        world.Players.Register(owner);
        world.Players.SetMembership(owner, world.Characters.Cold(id.Slot)!.Faction, FactionPermission.All);
        world.Players.AssignInitial(owner, id);
        var connection = world.Authority.Open(owner);
        Assert.True(world.Authority.BeginSynchronization(connection));
        return connection;
    }

    [Theory]
    [InlineData(SpeechStyle.Normal, "hello", false)]
    [InlineData(SpeechStyle.Whisper, "(hello)", false)]
    [InlineData(SpeechStyle.Shout, "hello", true)]
    public void BubbleUsesAuthorizedNativeSnapshotAndSpeechJournal(SpeechStyle style, string shown, bool large)
    {
        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 0));
        var speaker = Spawn(world, 0, true);
        var a = Admit(world, new(2), speaker);
        var b = Admit(world, new(3), Spawn(world, 20000, true, 2));
        world.AddSystem(new Input(w =>
        {
            ulong before = w.StateHash();
            Assert.True(w.PostSpeechBubble(speaker, "hello", style));
            Assert.Equal(before, w.StateHash());
        }));

        world.RunTick();
        var near = world.Authority.CaptureView(a, new(), new(100))!;
        var far = world.Authority.CaptureView(b, new(), new(100))!;
        var codec = new WorldViewCodec(WorldViewCodecTests.Database());
        var bubble = Assert.Single(codec.Decode(codec.Encode(near)).Snapshot.Speech);
        Assert.Equal((speaker, shown, large), (bubble.Speaker, bubble.Text, bubble.Large));
        Assert.Equal("hello", Assert.Single(near.Events.Speech).Line.Text);
        Assert.Empty(far.Snapshot.Speech);
        Assert.Empty(far.Events.Speech);
    }

    [Fact]
    public void ListenerFacesSpeakerAndCombatOrFleeingInterruptsWithoutOverwritingRoute()
    {
        var walk = new OpenGroundWalkability((_, _) => 0);
        using var movement = new MovementSystem(new PathService(walk, true));
        using var world = new SimWorld(new WorldSettings(), walk, [movement]);
        var speaker = Spawn(world, 20, true);
        var npc = Spawn(world, 0, false);
        world.StarterAvatars.Capture(PlayerId.Local, [speaker]);
        world.AddSystem(new Input(w =>
        {
            if (w.Tick != 0) return;
            Assert.False(w.GiveTalkPause(speaker, npc, 30));
            Assert.True(w.GiveTalkPause(npc, speaker, 30));
        }));
        world.RunTick();
        Assert.True(world.TalkPauses.Active);
        Assert.Equal(MathF.PI / 2, world.Characters.Previous[npc.Slot].Yaw, 4);
        Assert.Equal(Vector3.Zero, world.Characters.Previous[npc.Slot].Velocity);
        world.Characters.Cold(npc.Slot)!.Fleeing = true;
        world.RunTick();
        Assert.False(world.TalkPauses.Active);
    }

    [Fact]
    public void StarterProvenanceRetainsDeadAndUnloadedRolesAndRequiresExplicitSuccessor()
    {
        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 0));
        var first = Spawn(world, 0, true);
        var second = Spawn(world, 4, true);
        var recruit = Spawn(world, 6, true);
        var owner = PlayerId.Local;
        Assert.True(world.StarterAvatars.IsProtected(recruit));
        world.StarterAvatars.Capture(owner, [first, second]);
        Assert.False(world.StarterAvatars.IsProtected(recruit));
        world.Characters.Cold(first.Slot)!.DiedTick = 0;
        world.AddSystem(new Input(w =>
        {
            Assert.False(w.StarterAvatars.TryInherit(new(2), recruit));
            if (w.Tick == 0) Assert.False(w.StarterAvatars.TryInherit(owner, recruit));
            else if (w.Tick == 1) Assert.True(w.StarterAvatars.TryInherit(owner, recruit));
            else Assert.False(w.StarterAvatars.TryInherit(owner, recruit));
        }));
        world.RunTick();
        world.Characters.Cold(second.Slot)!.DiedTick = 1;
        world.RunTicks(2);
        Assert.True(world.StarterAvatars.IsProtected(recruit));
        var bytes = SaveLastingIds.Write([], 1, 4, world.StarterAvatars.Entries);
        var loaded = SaveLastingIds.Read(bytes)!.Value.Starters;
        Assert.Equal(3, loaded.Count);
        Assert.Equal(2, loaded.Count(s => s.Dead));
        using var restored = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 0));
        restored.StarterAvatars.Restore(loaded);
        var candidate = Spawn(restored, 0, true);
        restored.AddSystem(new Input(w => Assert.False(w.StarterAvatars.TryInherit(owner, candidate))));
        restored.RunTick();
    }

    [Fact]
    public void OnlyManualAuthorizedOrdersInterruptAutomation()
    {
        var observer = new OrdersSeen();
        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 0), [new PlayerSystem(), observer]);
        var aRole = Spawn(world, 0, true);
        var bRole = Spawn(world, 4, true, 2);
        var a = new PlayerId(2);
        var b = new PlayerId(3);
        Admit(world, a, aRole);
        Admit(world, b, bRole);
        world.Commands.Enqueue(new StopCommand([aRole]) { Tick = 0 });
        world.Commands.Enqueue(a, new OrdersCommand([bRole], CharacterOrders.Hold) { Tick = 0 });
        world.Commands.Enqueue(b, new OrdersCommand([bRole], CharacterOrders.Hold) { Tick = 0 });
        world.Commands.Enqueue(a, new MoveOrder([aRole], new(10, 0, 10)) { Tick = 0 });
        world.RunTick();
        Assert.Equal(new[] { (b, bRole), (a, aRole) }, observer.Seen);
    }
}
