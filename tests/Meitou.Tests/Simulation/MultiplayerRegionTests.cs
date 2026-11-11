using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class MultiplayerRegionTests
{
    static readonly Vector3 Here = SyntheticTown.Centre, Far = new(50000, 300, 50000), Away = new(-50000, 300, -50000);
    sealed class Scene : IDisposable
    {
        public PopulationData Data { get; }
        public PopulationSystem Population { get; }
        public SimWorld World { get; }
        public WorldPlayer A { get; }
        public WorldPlayer B { get; }
        public CharacterId First { get; }
        public CharacterId Second { get; }
        public Scene(bool sameFaction = false, bool apart = false)
        {
            Data = PopulationData.Create(SyntheticTown.Database(residentSquads: 1, roaming: true),
                [new TownPlacement("near", "30-t", Here, null), new TownPlacement("far", "31-t", Far, null)]);
            int remote = sameFaction ? Data.PlayerFaction : Data.RegisterFaction("remote", "Remote", Data.PlayerFaction);
            Population = new PopulationSystem(Data, new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 0, Background = false });
            World = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300), [new PlayerSystem(Data), Population]);
            A = World.Players.Register(new PlayerId(2)); B = World.Players.Register(new PlayerId(3));
            World.Players.SetMembership(A.Id, Data.PlayerFaction, FactionPermission.All);
            World.Players.SetMembership(B.Id, remote, FactionPermission.All);
            First = Spawn(A, Here + new Vector3(1000, 0, 0)); Second = Spawn(B, (apart ? Far : Here) + new Vector3(1000, 0, 0));
        }
        CharacterId Spawn(WorldPlayer player, Vector3 at)
        {
            var id = World.Characters.Spawn(new CharacterHot { Position = at }, new CharacterCold { Faction = player.State.Faction, IsPlayer = true }, 0);
            World.Players.AssignInitial(player.Id, id); return id;
        }
        public CharacterId[] Residents() => World.Squads.All.Where(s => s.Town == 0 && s.PlatoonId < 0)
            .SelectMany(s => s.Members).OrderBy(id => id.Slot).ThenBy(id => id.Generation).ToArray();
        public void Dispose() { World.Dispose(); Population.Dispose(); }
    }
    static bool Knows(PlayerState player, int site) => site < player.Towns.Length && (player.Towns[site] & TownKnowledge.Discovered) != 0;

    [Fact]
    public void RemoteRolesActivateOneSharedPopulationWithoutALocalPlayerAndKeepItUntilBothLeave()
    {
        using var s = new Scene();
        Assert.False(s.World.Player.Exists);
        s.World.RunTicks((int)Math.Ceiling(1 / s.World.TickSeconds) + 1); Assert.Equal(SiteStatus.Loaded, s.Population.StatusOf(0));
        var before = s.Residents(); Assert.NotEmpty(before);
        s.World.Characters.Place(s.First.Slot, Away, 0); s.World.RunTicks(220);
        Assert.Equal(SiteStatus.Loaded, s.Population.StatusOf(0)); Assert.Equal(before, s.Residents());
        s.World.Characters.Place(s.Second.Slot, Far, 0); s.World.RunTicks(220);
        Assert.Equal(SiteStatus.Unloaded, s.Population.StatusOf(0)); Assert.Equal(SiteStatus.Loaded, s.Population.StatusOf(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplorationAndNoticesArePrivateEvenForMembersOfTheSameFaction(bool sameFaction)
    {
        using var s = new Scene(sameFaction, apart: true);
        var spectator = s.World.Players.Register(new PlayerId(4));
        s.World.Players.SetMembership(spectator.Id, s.A.State.Faction, FactionPermission.None);
        s.World.RunTicks((int)Math.Ceiling(1 / s.World.TickSeconds) + 1);
        Assert.True(Knows(s.A.State, 0)); Assert.False(Knows(s.A.State, 1));
        Assert.True(Knows(s.B.State, 1)); Assert.False(Knows(s.B.State, 0));
        Assert.Empty(s.World.Player.Towns); Assert.Empty(spectator.State.Towns);
        string name = s.Data.Sites[0].Town.UnexploredName is { Length: > 0 } unknown ? unknown : s.Data.Sites[0].Town.Name;
        Assert.Contains(s.World.TakeMessages(s.A.Id), message => message.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain(s.World.TakeMessages(s.B.Id), message => message.Contains(name, StringComparison.Ordinal));
        Assert.Empty(s.World.TakeMessages());
    }

    [Fact]
    public void CameraFocusCannotActivateAnotherAreaAndOnePlayersEditorDoesNotStopAnotherPlayersDiscovery()
    {
        using var s = new Scene();
        s.B.State.InCreation = true;
        s.World.Commands.Enqueue(new FocusCommand(Far) { Tick = 0 });
        s.World.RunTicks((int)Math.Ceiling(1 / s.World.TickSeconds) + 1);
        Assert.Equal(SiteStatus.Unloaded, s.Population.StatusOf(1));
        Assert.DoesNotContain(WorldLayout.ZoneOf(Far.X, Far.Z), s.World.ActiveZones!);
        Assert.True(Knows(s.A.State, 0)); Assert.False(Knows(s.B.State, 0));
    }

    [Fact]
    public void TrustedPreparationCanHoldAnUnloadedRoleAreaWithoutGivingAPlayerMapKnowledge()
    {
        using var s = new Scene();
        using var preparing = s.World.Regions.Retain(ZoneActivation.ZonesAround(Far, 0));
        s.World.RunTicks((int)Math.Ceiling(1 / s.World.TickSeconds) + 1);
        Assert.Equal(SiteStatus.Loaded, s.Population.StatusOf(1)); Assert.False(Knows(s.A.State, 1)); Assert.False(Knows(s.B.State, 1));
        preparing.Dispose(); s.World.RunTicks(220); Assert.Equal(SiteStatus.Unloaded, s.Population.StatusOf(1));
    }

    [Fact]
    public void LosingAllLivingRolesExpiresRegionsWithoutErasingDeadRoleOwnership()
    {
        using var s = new Scene(); s.World.RunTicks((int)Math.Ceiling(1 / s.World.TickSeconds) + 1);
        s.World.Characters.Cold(s.First.Slot)!.DiedTick = s.World.Tick;
        s.World.Characters.Cold(s.Second.Slot)!.DiedTick = s.World.Tick;
        s.World.RunTicks(220);
        Assert.Empty(s.World.ActiveZones!); Assert.Equal(SiteStatus.Unloaded, s.Population.StatusOf(0));
        Assert.Single(s.World.Players.KnownRoles(s.A.Id)); Assert.Single(s.World.Players.KnownRoles(s.B.Id));
    }
}
