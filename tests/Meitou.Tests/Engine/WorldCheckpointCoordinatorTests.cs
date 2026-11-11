using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Save;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Engine;

public partial class WorldCheckpointCoordinatorTests
{
    [Fact]
    public void FirstAccountInAnEmptyServerWorldPersistsWithoutInventingFactionOrCharacters()
    {
        string root = SaveSamples.Temp("empty-server-account");
        var data = Data();
        using var runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), [new PlayerSystem(data)]);
        try
        {
            var store = new CheckpointStore(root);
            var coordinator = new WorldCheckpointCoordinator(runtime, data, store, Options(data));
            var accounts = new Meitou.Server.TokenAccountStore(Path.Combine(root, "accounts.json"));
            var credential = accounts.Create("new-player");
            var request = new Meitou.Networking.AuthenticationRequest(credential.Account, credential.Token);
            var sessions = new Meitou.Server.ServerAccountSessions(accounts, runtime, coordinator);
            var login = sessions.Open(request)!;
            Assert.Equal(PlayerEntryRoute.NewPlayer, runtime.World.Authority.AssessEntry(login.Connection)!.Route);
            Assert.Equal(0, runtime.World.Characters.Count); Assert.Empty(runtime.World.Players.Find(login.Connection.Player)!.Memberships);
            Assert.False(runtime.World.Authority.BeginSynchronization(login.Connection));
            var candidate = Data();
            var restored = coordinator.Restore(store.LoadCurrent()!, candidate, d => [new PlayerSystem(d)], Options(candidate));
            var next = new Meitou.Server.ServerAccountSessions(accounts, runtime, restored.Coordinator).Open(request)!;
            Assert.Equal(login.Connection.Player, next.Connection.Player);
            Assert.Equal(PlayerEntryRoute.NewPlayer, runtime.World.Authority.AssessEntry(next.Connection)!.Route);
            Assert.Empty(runtime.World.CreationQualifications.For(next.Connection.Player));
            Assert.Equal(0, runtime.World.Characters.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AuthenticatedAccountBindingCommitsWithRolesAndReconnectsAfterWorldRestore()
    {
        using var s = new Scene();
        var accounts = new Meitou.Server.TokenAccountStore(Path.Combine(s.Root, "accounts.json"));
        var credential = accounts.Create("existing-role-owner");
        var request = new Meitou.Networking.AuthenticationRequest(credential.Account, credential.Token);
        var principal = accounts.Authenticate(request)!;
        s.Runtime.World.Players.BindAccount(s.Player.Id, principal.Identity);
        var sessions = new Meitou.Server.ServerAccountSessions(accounts, s.Runtime, s.Coordinator);
        var connected = sessions.Open(request)!;
        Assert.Equal(s.Player.Id, connected.Connection.Player);
        Assert.False(s.Runtime.World.Players.AccountRequiresCheckpoint(principal.Identity));
        var checkpoint = s.Store.LoadCurrent()!;
        Assert.NotNull(checkpoint);
        var data = Data();
        var restored = s.Coordinator.Restore(checkpoint, data, d => [new PlayerSystem(d)], Options(data));
        Assert.False(sessions.IsCurrent(connected));
        var nextSessions = new Meitou.Server.ServerAccountSessions(accounts, s.Runtime, restored.Coordinator);
        var next = nextSessions.Open(request)!;
        Assert.Equal(s.Player.Id, next.Connection.Player); Assert.NotEqual(connected.Connection.World, next.Connection.World);
        Assert.Single(s.Runtime.World.Players.KnownRoles(next.Connection.Player));
        Assert.Equal(100, s.Runtime.World.Players.Find(next.Connection.Player)!.State.Money);
        Assert.Equal(PlayerEntryRoute.EnterWorld, s.Runtime.World.Authority.AssessEntry(next.Connection)!.Route);
        Assert.Equal(AdmissionPhase.Preparing, s.Runtime.World.Authority.GetPhase(next.Connection));
    }

    [Fact]
    public void CreationQualificationsAndRevocationSurviveAnActualCheckpoint()
    {
        using var s = new Scene();
        var recipient = s.Runtime.World.Players.Register(new PlayerId(3));
        s.Runtime.World.Players.SetMembership(recipient.Id, s.Player.State.Faction, FactionPermission.None);
        var newcomer = s.Runtime.World.Players.Register(new PlayerId(4));
        var initial = s.Runtime.World.CreationQualifications.IssueInitial(newcomer.Id, "starts:v1", 4);
        Guid approval = Guid.NewGuid();
        var sponsored = s.Runtime.World.CreationQualifications.ApproveFaction(s.Player.Id, approval, recipient.Id, s.Player.State.Faction, "recruit:v1", 2, 40);
        Assert.True(s.Runtime.World.CreationQualifications.Revoke(sponsored.Id, sponsored.Revision));
        var captured = s.Coordinator.Capture();
        s.Runtime.World.CreationQualifications.Revoke(initial.Id, initial.Revision);
        s.Coordinator.Commit(captured);
        var data = Data();
        s.Coordinator.Restore(s.Store.LoadCurrent()!, data, d => [new PlayerSystem(d)], Options(data));
        var qualifications = s.Runtime.World.CreationQualifications;
        Assert.Equal(CreationEligibility.Eligible, qualifications.Assess(newcomer.Id, initial.Id, initial.Revision, 4));
        var replay = qualifications.ApproveFaction(s.Player.Id, approval, recipient.Id, sponsored.Faction, "recruit:v1", 2, 40);
        Assert.Equal(sponsored.Id, replay.Id); Assert.Equal(CreationQualificationStatus.Revoked, replay.Status);
        Assert.False(replay.IncludesStartResources);
        var later = qualifications.ApproveFaction(s.Player.Id, Guid.NewGuid(), recipient.Id, sponsored.Faction, "recruit:v1", 1, 10);
        Assert.True(later.Id > sponsored.Id); Assert.True(later.Revision > replay.Revision);
        Assert.Equal(100, s.Runtime.World.Players.Find(s.Player.Id)!.State.Money);
    }

    static PopulationData Data() => PopulationData.Create(SyntheticTown.Database(), []);
    static SaveCaptureOptions Options(PopulationData data) => new() { DataFiles = data.Db.Files };

    sealed class Scene : IDisposable
    {
        public string Root { get; } = SaveSamples.Temp("coordinated-checkpoint");
        public PopulationData Data { get; } = WorldCheckpointCoordinatorTests.Data();
        public WorldRuntime Runtime { get; }
        public WorldPlayer Player { get; }
        public CharacterId Role { get; }
        public CheckpointStore Store { get; }
        public WorldCheckpointCoordinator Coordinator { get; }
        public Scene(ITickSystem? extra = null)
        {
            int faction = Data.RegisterFaction("runtime:a", "A", Data.PlayerFaction);
            Runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0),
                extra is null ? [new PlayerSystem(Data)] : [new PlayerSystem(Data), extra]);
            var world = Runtime.World;
            Player = world.Players.Register(new PlayerId(2));
            world.Players.SetMembership(Player.Id, faction, FactionPermission.All);
            Player.State.Money = 100;
            var squad = SquadManagement.NewSquad(world, Data.Db, Vector3.Zero, "A", faction);
            Role = world.Characters.Spawn(new CharacterHot { Position = Vector3.One }, new CharacterCold
            {
                Name = "Saved role", RecordId = Data.Db.OfType(FcsRecordType.Character).First().StringId,
                Faction = faction, IsPlayer = true, SquadId = squad.Id,
            }, 0);
            squad.Members.Add(Role); squad.Leader = Role;
            world.Players.AssignInitial(Player.Id, Role);
            world.GameHours = 100.123456789;
            Store = new CheckpointStore(Root);
            Coordinator = new WorldCheckpointCoordinator(Runtime, Data, Store, Options(Data));
        }
        public void Dispose()
        {
            try { Runtime.Dispose(); }
            finally { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }
    }

    [Fact]
    public async Task CaptureDrainsAuthorityAndRestoresDetachedWorldResourcesAndPreciseTime()
    {
        using var s = new Scene();
        var world = s.Runtime.World;
        var grant = world.Players.ControlOf(s.Role)!.Value;
        var connection = world.Authority.Open(s.Player.Id);
        Assert.True(world.Authority.BeginSynchronization(connection)); Assert.True(world.Authority.Activate(connection));
        var request = world.Authority.Submit(connection, 1, new RenameFactionCommand("Captured") { Tick = 0, Faction = s.Player.State.Faction },
            new AuthorityContext([], s.Player.State.Faction, s.Player.MembershipRevision(s.Player.State.Faction)));
        var captured = s.Coordinator.Capture();
        Assert.Equal(AuthorityStatus.Evaluated, (await request).Status);
        Assert.Equal(0, captured.Point.Tick);
        Assert.Equal((await request).Revision, captured.Point.Revision);
        s.Player.State.Money = 700; s.Player.State.FactionName = "Later";
        world.Characters.Cold(s.Role.Slot)!.Name = "Changed after capture";
        await Task.Run(() => s.Coordinator.Commit(captured));
        var data = Data();
        var restored = s.Coordinator.Restore(s.Store.LoadCurrent()!, data, d => [new PlayerSystem(d)], Options(data));
        Assert.Null(restored.CleanupFailure);
        Assert.NotSame(world, s.Runtime.World);
        Assert.Equal(100, s.Runtime.World.Players.Find(s.Player.Id)!.State.Money);
        Assert.Equal("Captured", s.Runtime.World.Players.Find(s.Player.Id)!.State.FactionName);
        Assert.True(s.Runtime.World.Entities.TryResolve(grant.Entity, out var role));
        Assert.Equal("Saved role", s.Runtime.World.Characters.Cold(role.Slot)!.Name);
        Assert.True(s.Runtime.World.Players.Validate(grant));
        Assert.Equal(captured.Point.GameHours, s.Runtime.World.GameHours);
        Assert.Equal(0, s.Runtime.World.Tick); // Legacy saved timers rebase at the new world's tick zero.
        Assert.Throws<InvalidOperationException>(() => s.Coordinator.Commit(captured));
        s.Runtime.Step();
        Assert.Equal(captured.Point.GameHours + WorldRuntime.TickSeconds / SimWorld.SecondsPerGameHour, s.Runtime.World.GameHours, 9);
        Assert.NotNull(restored.Coordinator.Capture());
    }

    [Fact]
    public void OlderDraftCannotOverwriteANewerCommitAndSameDraftRetryIsIdempotent()
    {
        using var s = new Scene();
        var older = s.Coordinator.Capture();
        s.Player.State.Money = 200;
        var newer = s.Coordinator.Capture();
        Guid id = s.Coordinator.Commit(newer);
        Assert.Equal(id, s.Coordinator.Commit(newer));
        Assert.Throws<InvalidOperationException>(() => s.Coordinator.Commit(older));
        Assert.Equal(id, s.Store.LoadCurrent()!.Id);
        Assert.Single(Directory.GetDirectories(Path.Combine(s.Root, "checkpoints")));
    }

    [Fact]
    public void ProducerHandoffAndWorldReplacementInvalidateOutstandingDrafts()
    {
        using var s = new Scene();
        var old = s.Coordinator.Capture();
        var replacement = new WorldCheckpointCoordinator(s.Runtime, s.Data, s.Store, Options(s.Data));
        Assert.Throws<InvalidOperationException>(() => s.Coordinator.Commit(old));
        Assert.Throws<ArgumentException>(() => replacement.Commit(old));
        var current = replacement.Capture();
        s.Runtime.ReplaceWorld([]);
        Assert.Throws<InvalidOperationException>(() => replacement.Commit(current));
        Assert.Null(s.Store.LoadCurrent());
    }

    [Fact]
    public async Task CommitQueuedDuringRestoreCannotPublishTheRetiredWorld()
    {
        using var s = new Scene();
        Guid current = s.Coordinator.Commit(s.Coordinator.Capture());
        var checkpoint = s.Store.LoadCurrent()!;
        s.Player.State.Money = 200;
        var retiredDraft = s.Coordinator.Capture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var data = Data();
        var restoring = Task.Run(() => s.Coordinator.Restore(checkpoint, data, d =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release candidate construction.");
            return [new PlayerSystem(d)];
        }, Options(data)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var writing = Task.Run(() => { attempted.SetResult(); return s.Coordinator.Commit(retiredDraft); });
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.Set();
        var result = await restoring;
        Assert.Null(result.CleanupFailure);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await writing);
        Assert.Equal(current, s.Store.LoadCurrent()!.Id);
        Assert.Equal(100, s.Runtime.World.Players.Find(s.Player.Id)!.State.Money);
    }

    [Fact]
    public void WriteFailureKeepsTheLiveWorldUsableAndTheSameDetachedDraftCanBeRetried()
    {
        using var s = new Scene();
        var capture = s.Coordinator.Capture();
        Directory.CreateDirectory(s.Root);
        using (var competingWriter = new FileStream(Path.Combine(s.Root, "writer.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => s.Coordinator.Commit(capture));
        Assert.False(s.Runtime.Faulted);
        Assert.Null(s.Store.LoadCurrent());
        s.Runtime.Step();
        Assert.NotEqual(Guid.Empty, s.Coordinator.Commit(capture));
    }

    [Fact]
    public void CorruptRestoreKeepsTheCurrentWorldAndItsCatalogueUntouched()
    {
        using var s = new Scene();
        s.Coordinator.Commit(s.Coordinator.Capture());
        var checkpoint = s.Store.LoadCurrent()!;
        checkpoint.World.Platoons.Single().Characters.Single().GameState!.Strings[EntitySave.CharacterKey] = "unreadable";
        var before = s.Runtime.World;
        var data = Data();
        Assert.Throws<InvalidDataException>(() => s.Coordinator.Restore(checkpoint, data, d => [new PlayerSystem(d)], Options(data)));
        Assert.Same(before, s.Runtime.World);
        Assert.False(s.Runtime.Faulted);
        Assert.True(before.Players.CanControl(s.Player.Id, s.Role));
        Assert.Equal(100, s.Player.State.Money);
        Assert.NotNull(s.Coordinator.Capture());
    }

    [Fact]
    public void RestoreCannotMutateTheLivePopulationCatalogue()
    {
        using var s = new Scene();
        s.Coordinator.Commit(s.Coordinator.Capture());
        Assert.Throws<ArgumentException>(() => s.Coordinator.Restore(s.Store.LoadCurrent()!, s.Data, d => [new PlayerSystem(d)], Options(s.Data)));
        Assert.True(s.Runtime.World.Players.CanControl(s.Player.Id, s.Role));
    }

    [Fact]
    public void ReentrantWorldChangeDuringCaptureIsRejected()
    {
        using var s = new Scene();
        bool change = true;
        var coordinator = new WorldCheckpointCoordinator(s.Runtime, s.Data, s.Store, new SaveCaptureOptions
        {
            DataFiles = s.Data.Db.Files,
            Bodies = _ => { if (change) { change = false; s.Runtime.Step(); } return (null, null); },
        });
        Assert.Throws<InvalidOperationException>(() => coordinator.Capture());
        Assert.Null(s.Store.LoadCurrent());
        Assert.False(s.Runtime.Faulted);
        Assert.NotNull(coordinator.Capture());
    }

    sealed class BadCleanup : ITickSystem, IDisposable
    {
        public void Dispose() => throw new IOException("Injected old-world disposal failure");
    }

    [Fact]
    public void CompletedRestoreReportsCleanupFailureAndReturnsAUsableNewCoordinator()
    {
        using var s = new Scene(new BadCleanup());
        s.Coordinator.Commit(s.Coordinator.Capture());
        var data = Data();
        var result = s.Coordinator.Restore(s.Store.LoadCurrent()!, data, d => [new PlayerSystem(d)], Options(data));
        Assert.NotNull(result.CleanupFailure);
        Assert.False(s.Runtime.Faulted);
        Assert.NotNull(result.Coordinator.Capture());
    }

    [Fact]
    public void MissingRegionSourceAndFaultedRuntimeCannotProduceCheckpoints()
    {
        using var s = new Scene();
        s.Runtime.World.VisitedZones.Add(new ZoneCoordinate(1, 1));
        Assert.Throws<InvalidDataException>(() => s.Coordinator.Capture());
        s.Runtime.World.VisitedZones.Clear();
        s.Runtime.World.Authority.MarkFaulted();
        Assert.Throws<InvalidOperationException>(() => s.Coordinator.Capture());
        Assert.True(s.Runtime.Faulted);
        Assert.Null(s.Store.LoadCurrent());
    }

    [Fact]
    public void RevokedLastRoleRemainsAnExistingPlayerAfterCheckpointAndRestart()
    {
        using var s = new Scene();
        var entity = s.Runtime.World.Players.ControlOf(s.Role)!.Value.Entity;
        s.Runtime.World.Players.RemoveMembership(s.Player.Id, s.Player.State.Faction);
        Assert.Empty(s.Runtime.World.Players.KnownRoles(s.Player.Id));
        s.Coordinator.Commit(s.Coordinator.Capture());
        var data = Data();
        var result = s.Coordinator.Restore(s.Store.LoadCurrent()!, data, d => [new PlayerSystem(d)], Options(data));
        Assert.Null(result.CleanupFailure);
        Assert.Equal(PlayerRoleHistory.Assigned, s.Runtime.World.Players.Find(s.Player.Id)!.RoleHistory);
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, s.Runtime.World.Players.AssessEntry(s.Player.Id).Route);
        Assert.True(s.Runtime.World.Entities.TryResolve(entity, out var role));
        Assert.False(s.Runtime.World.Players.CanControl(s.Player.Id, role));
        Assert.False(s.Runtime.World.Authority.BeginSynchronization(s.Runtime.World.Authority.Open(s.Player.Id)));
    }
}
