using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Tests.Bodies;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Engine;

/// <summary>
/// The upstream interface additions must retain recipient privacy, authority and reliable replica withdrawal.
/// </summary>
public sealed class UpstreamInteractionNetworkTests
{
    /// <summary>
    /// Two independently controlled characters of one faction, each wearing an unlocked pair of shackles.
    /// </summary>
    sealed class Scene : IDisposable
    {
        public readonly SimWorld World = new(new WorldSettings { PublishSnapshots = false },
            new OpenGroundWalkability((_, _) => 0), [new LockSystem(BodyFixtures.BaseGame, BodyOptions.Default)]);
        public readonly WorldPlayer A;
        public readonly WorldPlayer B;
        public readonly CharacterId First;
        public readonly CharacterId Second;
        public readonly AuthorityConnection Connection;
        public readonly AuthoritativeCommandChannel Channel;

        public Scene()
        {
            A = World.Players.Register(new(2));
            B = World.Players.Register(new(3));
            World.Players.SetMembership(A.Id, 7, FactionPermission.All);
            World.Players.SetMembership(B.Id, 7, FactionPermission.All);
            First = Spawn(A);
            Second = Spawn(B);
            A.State.Selection.Add(First);
            B.State.Selection.Add(Second);
            Connection = World.Authority.Open(A.Id);
            Assert.True(World.Authority.BeginSynchronization(Connection));
            Assert.True(World.Authority.Activate(Connection));
            var hello = new CompatibilityHello(CompatibilityHandshake.Protocol, "test-build", new('A', 64), new('B', 64));
            Channel = new(hello, World.Authority);
            Channel.ReceiveHello(new(WireMessage.Hello, CompatibilityHandshake.Encode(hello)));
            Channel.BindAuthenticated(Connection);
        }

        CharacterId Spawn(WorldPlayer player)
        {
            var inventory = new Inventory();
            inventory.Items.Add(new ItemInstance { Record = "shackles", Name = "Shackles", Type = FcsRecordType.Armour,
                IsLocked = true, EquipmentSection = "boots", Section = "boots", Lock = new LockState { Locked = false } });
            var id = World.Characters.Spawn(new CharacterHot(), new CharacterCold
                { Faction = 7, IsPlayer = true, Inventory = inventory }, 0);
            World.Players.AssignInitial(player.Id, id);
            return id;
        }

        public PlayerWorldView View() => World.Authority.CaptureView(Connection, new(), new(100))!;
        public ItemInstance Cuffs(CharacterId id) => Assert.Single(World.Characters.Cold(id.Slot)!.Inventory!.Items);
        public WireFrame Command(ulong request = 1)
        {
            var view = View();
            var bound = ClientCommandBinding.Bind(view, new TakeOffShacklesOrder(First) { Tick = long.MaxValue }, [Second], 7);
            Assert.Equal(World.Players.ControlOf(First)!.Value.Entity, Assert.Single(bound.Expected.Roles).Entity);
            return CommandProtocol.Encode(view.World, request, bound.Command, bound.Expected);
        }

        public void Dispose()
        {
            Channel.Dispose();
            World.Dispose();
        }
    }

    [Fact]
    public async Task TakingOffOpenShacklesUsesTheWearerGrantAndReplaysDoNotRepeatTheMutation()
    {
        using var s = new Scene();
        var view = s.View();
        Assert.True(view.Snapshot.Characters.Single(c => c.Id == s.First).ShacklesOpen);
        Assert.False(view.Snapshot.Characters.Single(c => c.Id == s.Second).ShacklesOpen);
        Assert.Throws<InvalidOperationException>(() => ClientCommandBinding.Bind(view,
            new TakeOffShacklesOrder(s.Second) { Tick = 0 }, [s.First], 7));

        var command = s.Command();
        var decoded = Assert.IsType<TakeOffShacklesOrder>(CommandProtocol.Decode(command).Command);
        Assert.Equal(0, decoded.Tick);
        var pending = s.Channel.ReceiveCommand(command);
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.Evaluated, (await pending).Status);
        Assert.Equal("main", s.Cuffs(s.First).Section);
        Assert.Equal("boots", s.Cuffs(s.Second).Section);
        Assert.False(s.View().Snapshot.Characters.Single(c => c.Id == s.First).ShacklesOpen);
        Assert.Equal("shackles", Assert.Single(s.View().Events.Sounds, e => e.Value.Kind == SoundKind.ShacklesOff).Value.Item);

        var otherConnection = s.World.Authority.Open(s.B.Id);
        Assert.True(s.World.Authority.BeginSynchronization(otherConnection));
        Assert.Empty(s.World.Authority.CaptureView(otherConnection, new(), new(100))!.Events.Sounds);

        var replay = await s.Channel.ReceiveCommand(command);
        Assert.Equal(await pending, replay);
        Assert.Single(s.View().Events.Sounds, e => e.Value.Kind == SoundKind.ShacklesOff);
        Assert.Empty(s.World.GroundItems.Published());
    }

    [Fact]
    public async Task RelockingOrRevokingTheDisplayedGrantPreventsTakingOffShackles()
    {
        using var s = new Scene();
        var frame = s.Command();
        s.Cuffs(s.First).Lock!.Locked = true;
        var pending = s.Channel.ReceiveCommand(frame);
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.Evaluated, (await pending).Status);
        Assert.Equal("boots", s.Cuffs(s.First).Section);
        Assert.Empty(s.View().Events.Sounds);

        s.Cuffs(s.First).Lock!.Locked = false;
        var stale = s.Channel.ReceiveCommand(s.Command(2));
        s.World.Players.SetMembership(s.A.Id, 7, FactionPermission.ReadFunds);
        s.World.ApplyPausedInterfaceCommands();
        Assert.Equal(AuthorityStatus.StaleAuthority, (await stale).Status);
        Assert.Equal("boots", s.Cuffs(s.First).Section);
    }

    [Fact]
    public void NewStateAndSoundsRoundTripAndOldBaselinesCannotRestoreWithdrawnDetails()
    {
        var database = WorldViewCodecTests.Database();
        var codec = new WorldViewCodec(database);
        var source = WorldViewCodecTests.View(database);
        var character = source.Snapshot.Characters[0] with { Animations = [], ShacklesOpen = true };
        var sounds = new[]
        {
            new SoundEvent(SoundKind.ShacklesOff, character.Id, CharacterId.None, Vector3.One, Item: "shackles"),
            new SoundEvent(SoundKind.Victory, character.Id, CharacterId.None, Vector3.One),
        };
        source = source with
        {
            Snapshot = source.Snapshot with { Characters = [character], CraftStations = [new("bench", "Workshop 工坊")] },
            Events = new(2, []) { Sounds = [new(1, sounds[0]), new(2, sounds[1])] },
        };
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22);
        var baseline = publisher.Capture(source);
        var idle = publisher.Capture(source with { Publication = 2 });
        Assert.DoesNotContain((ushort)1073, idle.ComponentBytes.Keys);
        Assert.DoesNotContain((ushort)2111, idle.ComponentBytes.Keys);
        var changed = source with { Publication = 3, Snapshot = source.Snapshot with
            { Characters = [character with { ShacklesOpen = false }], CraftStations = [] } };
        var withdrawal = publisher.Capture(changed);
        var replica = new GameStateReplica(codec, 11, 22);
        foreach (var frame in withdrawal.Frames) replica.Apply(frame);
        foreach (var frame in baseline.Frames) replica.Apply(frame);
        Assert.False(Assert.Single(replica.View!.Snapshot.Characters).ShacklesOpen);
        Assert.Empty(replica.View.Snapshot.CraftStations);
        Assert.Equal(sounds, replica.View.Events.Sounds.Select(e => e.Value));
        Assert.Throws<InvalidDataException>(() => codec.Encode(source with { Snapshot = source.Snapshot with
            { CraftStations = [new("bench", "One"), new("bench", "Two")] } }));
        Assert.Throws<InvalidDataException>(() => codec.Encode(source with { Snapshot = source.Snapshot with
            { CraftStations = [new(new string('站', 342), "Too long in UTF-8")] } }));
    }

    [Fact]
    public void WallLineIntentRoundTripsButMalformedRangesAndGeometryAreRejected()
    {
        var intent = new WallLineIntent("wall", Vector3.Zero, new(20, 0, 0), true, false, true);
        var plans = new PlannedBuilding[] { new("wall", Vector3.Zero, Quaternion.Identity), new("wall", new(20, 0, 0), Quaternion.Identity) };
        var command = new PlaceBuildings(plans) { Tick = 0, Faction = 7, WallLines = [new(0, 2, intent)] };
        var expected = new ExpectedCommandAuthority([], 7, 1);
        var copy = Assert.IsType<PlaceBuildings>(CommandProtocol.Decode(CommandProtocol.Encode(Guid.NewGuid(), 1, command, expected)).Command);
        Assert.Equal(command.WallLines, copy.WallLines);
        PlannedWallLine[][] invalid = [[new(-1, 1, intent)], [new(1, 2, intent)], [new(0, 0, intent)],
            [new(0, 2, intent), new(1, 1, intent)], [new(0, 2, intent with { Start = new(float.NaN, 0, 0) })]];
        foreach (var lines in invalid)
            Assert.Throws<InvalidDataException>(() => CommandProtocol.Encode(Guid.NewGuid(), 1, command with { WallLines = lines }, expected));
    }
}
