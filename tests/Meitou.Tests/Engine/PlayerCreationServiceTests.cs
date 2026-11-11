using Meitou.Simulation.Player;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Save;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Engine;

public class PlayerCreationServiceTests
{
    [Fact]
    public void PreviewConsumesNothingAndItsGeneratedMemberStatsAndKitMatchCommittedCreation()
    {
        using var s = new Scene(); var qualification = s.Sponsored();
        var request = s.Request(qualification) with { Choices = [new CreationMemberChoice(0, "Preview name")] };
        var connection = s.Runtime.World.Authority.Open(s.Member.Id);
        var preview = s.Service.Preview(connection, request);
        Assert.Equal(s.Start.StartPosition, s.Service.Arrival(connection, request));
        Assert.Equal(0, s.Runtime.World.Characters.Count); Assert.Empty(s.Runtime.World.Squads.All);
        Assert.Equal(500, s.Approver.State.Money); Assert.Equal(CreationQualificationStatus.Available, s.Runtime.World.CreationQualifications.Find(qualification.Id)!.Status);
        Assert.Equal(0, preview.StartMoney); Assert.False(preview.IncludesStartResources); Assert.Equal(40, preview.Cost);
        var repeated = s.Service.Preview(connection, request);
        Assert.Equal(preview.Fingerprint, repeated.Fingerprint); Assert.Equal(preview.Members.Select(m => m.Name), repeated.Members.Select(m => m.Name));
        var receipt = s.Service.Execute(connection, request);
        Assert.Equal(preview.Fingerprint, receipt.Fingerprint); Assert.Equal(preview.Members.Length, receipt.Roles.Length);
        for (int i = 0; i < receipt.Roles.Length; i++)
        {
            Assert.True(s.Runtime.World.Entities.TryResolve(receipt.Roles[i], out var id));
            var character = s.Runtime.World.Characters.Cold(id.Slot)!;
            Assert.Equal(preview.Members[i].Name, character.Name); Assert.Equal(preview.Members[i].Skills, character.Stats!.ToArray());
            Assert.Equal(preview.Members[i].Equipment.Select(e => (e.Record, e.Quantity)), character.Inventory!.Shown().Select(e => (e.Item.Record, e.Item.Quantity)));
        }
    }

    [Fact]
    public void AuthenticatedReconnectCanResumeItsFrozenCreationWithoutRequiringTheOriginalDraft()
    {
        using var s = new Scene();
        var accounts = new Meitou.Server.TokenAccountStore(Path.Combine(s.Root, "accounts.json"));
        var credential = accounts.Create("member"); var login = new Meitou.Networking.AuthenticationRequest(credential.Account, credential.Token);
        s.Runtime.World.Players.BindAccount(s.Member.Id, accounts.Authenticate(login)!.Identity);
        var sessions = new Meitou.Server.ServerAccountSessions(accounts, s.Runtime, s.Checkpoints);
        var first = sessions.Open(login)!; var grant = s.Sponsored(); var request = s.Request(grant);
        AuthenticatedResume();
        void AuthenticatedResume()
        {
            Meitou.Server.AuthenticatedPlayerSession reconnected;
            int count;
            using (var locked = new FileStream(Path.Combine(s.Root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => s.Service.Execute(first.Connection, request));
                count = s.Runtime.World.Characters.Count;
                reconnected = sessions.Open(login)!;
                Assert.NotNull(reconnected); Assert.Equal(request.Id, s.Service.PendingRequest(reconnected.Connection));
                Assert.True(s.Runtime.World.Authority.ReadAdmission(reconnected.Connection)!.Busy);
                Assert.Empty(s.Runtime.World.Authority.ReadAdmission(reconnected.Connection)!.Roles);
                var unknown = accounts.Create("unknown"); int players = s.Runtime.World.Players.All.Count();
                Assert.Throws<InvalidOperationException>(() => sessions.Open(new(unknown.Account, unknown.Token)));
                Assert.Equal(players, s.Runtime.World.Players.All.Count());
                Assert.Throws<InvalidOperationException>(() => s.Service.Lookup(first.Connection, request.Id));
            }
            var result = s.Service.ResumePersistence(reconnected.Connection, request.Id);
            Assert.Equal(CreationRequestStatus.Committed, result.Status); Assert.Equal(count, s.Runtime.World.Characters.Count);
            Assert.Equal(460, s.Approver.State.Money); Assert.False(s.Runtime.World.HasPendingDurableMutation);
            Assert.Null(s.Service.PendingRequest(reconnected.Connection));
        }
    }

    [Fact]
    public void CreationNamesAreCommittedAndChangedChoicesConflictWithTheOriginalRequest()
    {
        using var s = new Scene();
        var request = s.Request(s.Sponsored()) with { Choices = [new CreationMemberChoice(0, "Chosen name")] };
        var connection = s.Runtime.World.Authority.Open(s.Member.Id);
        var result = s.Service.Execute(connection, request);
        Assert.True(s.Runtime.World.Entities.TryResolve(result.Roles[0], out var role));
        Assert.Equal("Chosen name", s.Runtime.World.Characters.Cold(role.Slot)!.Name);
        Assert.Equal(result.Roles, s.Service.Execute(connection, request).Roles);
        Assert.Throws<CreationRequestConflictException>(() => s.Service.Execute(connection, request with { Choices = [new CreationMemberChoice(0, "Other name")] }));
        var data = Data();
        s.Checkpoints.Restore(s.Store.LoadCurrent()!, data, Systems, Options(data));
        Assert.True(s.Runtime.World.Entities.TryResolve(result.Roles[0], out role));
        Assert.Equal("Chosen name", s.Runtime.World.Characters.Cold(role.Slot)!.Name);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("index")]
    [InlineData("name")]
    [InlineData("appearance")]
    public void InvalidCreationChoicesCannotSpawnChargeOrConsume(string defect)
    {
        using var s = new Scene(); var grant = s.Sponsored();
        IReadOnlyList<CreationMemberChoice> choices = defect switch
        {
            "duplicate" => [new(0, "A"), new(0, "B")],
            "index" => [new(999, "A")],
            "name" => [new(0, "\n")],
            _ => [new(0, Appearance: new Meitou.Data.Characters.CreationAppearanceChoice("missing", false, [], []))],
        };
        var request = s.Request(grant) with { Choices = choices };
        var connection = s.Runtime.World.Authority.Open(s.Member.Id);
        if (defect == "duplicate") Assert.Throws<ArgumentException>(() => s.Service.Execute(connection, request));
        else Assert.Throws<InvalidDataException>(() => s.Service.Execute(connection, request));
        Assert.Equal(0, s.Runtime.World.Characters.Count); Assert.Equal(500, s.Approver.State.Money);
        Assert.Equal(CreationQualificationStatus.Available, s.Runtime.World.CreationQualifications.Find(grant.Id)!.Status);
        Assert.False(s.Runtime.World.HasPendingDurableMutation);
    }

    [Fact]
    public void ForceRaceIsNotRelaxedWhenItsContentIsUnavailable()
    {
        using var s = new Scene(); var request = s.Request(s.Sponsored());
        var service = new PlayerCreationService(s.Checkpoints, (_, _) => new ApprovedCreationStart(
            s.Start with { ForceRace = [new RecordLink("missing-race", 1, 0, 0)] }, s.Data.PlayerFaction));
        Assert.Throws<InvalidDataException>(() => service.Execute(s.Runtime.World.Authority.Open(s.Member.Id), request));
        Assert.Equal(0, s.Runtime.World.Characters.Count); Assert.Equal(500, s.Approver.State.Money);
    }

    static PopulationData Data() { var data = PopulationData.Create(SyntheticTown.Database(), []); data.Relations.PlayerFaction = -1; return data; }
    static ITickSystem[] Systems(PopulationData data) => [new PlayerSystem(data), new PopulationSystem(data, new PopulationSettings { Background = false })];
    static SaveCaptureOptions Options(PopulationData data) => new() { DataFiles = data.Db.Files };
    sealed class Scene : IDisposable
    {
        public string Root { get; } = SaveSamples.Temp("creation-transaction");
        public PopulationData Data { get; } = PlayerCreationServiceTests.Data();
        public WorldRuntime Runtime { get; }
        public CheckpointStore Store { get; }
        public WorldCheckpointCoordinator Checkpoints { get; }
        public PlayerCreationService Service { get; }
        public NewGameStart Start { get; }
        public WorldPlayer Approver { get; }
        public WorldPlayer Member { get; }
        public WorldPlayer Newcomer { get; }
        public Scene(IWalkability? walk = null)
        {
            Runtime = new WorldRuntime(new WorldSettings { PublishSnapshots = false }, walk ?? new OpenGroundWalkability((_, _) => 300), Systems(Data));
            Approver = Runtime.World.Players.Register(new PlayerId(2)); Member = Runtime.World.Players.Register(new PlayerId(3)); Newcomer = Runtime.World.Players.Register(new PlayerId(4));
            Runtime.World.Players.SetMembership(Approver.Id, Data.PlayerFaction, FactionPermission.All);
            Runtime.World.Players.SetMembership(Member.Id, Data.PlayerFaction, FactionPermission.None);
            Approver.State.Money = 500;
            Start = NewGameStart.From(Data.Db.Find("40-t")!) with { ForceStartPos = true, StartPosition = new Vector2(1000, 2000), Money = 1234 };
            Store = new CheckpointStore(Root);
            Checkpoints = new WorldCheckpointCoordinator(Runtime, Data, Store, Options(Data));
            Service = MakeService(Checkpoints, Data);
        }
        public PlayerCreationService MakeService(WorldCheckpointCoordinator checkpoints, PopulationData data)
            => new(checkpoints, (policy, start) => policy == "approved" && start == Start.Id ? new ApprovedCreationStart(Start, data.PlayerFaction) : null);
        public CreationQualification Sponsored() => Runtime.World.CreationQualifications.ApproveFaction(Approver.Id, Guid.NewGuid(), Member.Id, Data.PlayerFaction, "approved", 10, 40);
        public PlayerCreationRequest Request(CreationQualification grant, string? name = null) => new(Guid.NewGuid(), grant.Id, grant.Revision, Start.Id, name);
        public void Dispose() { try { Runtime.Dispose(); } finally { if (Directory.Exists(Root)) Directory.Delete(Root, true); } }
    }

    [Fact]
    public void SponsoredCreationChargesOncePersistsReceiptAndReplaysAfterRestart()
    {
        using var s = new Scene();
        var grant = s.Sponsored(); var request = s.Request(grant);
        var connection = s.Runtime.World.Authority.Open(s.Member.Id);
        var receipt = s.Service.Execute(connection, request);
        Assert.Equal(40, receipt.Paid); Assert.False(receipt.IncludesStartResources);
        Assert.Equal(460, s.Approver.State.Money); Assert.NotEmpty(receipt.Roles);
        Assert.Equal(CreationQualificationStatus.Consumed, s.Runtime.World.CreationQualifications.Find(grant.Id)!.Status);
        Assert.Equal(receipt.Roles, s.Service.Execute(connection, request).Roles);
        Assert.Equal(460, s.Approver.State.Money);
        var data = Data();
        var restored = s.Checkpoints.Restore(s.Store.LoadCurrent()!, data, Systems, Options(data));
        Assert.Null(restored.CleanupFailure);
        var service = s.MakeService(restored.Coordinator, data);
        var reconnected = s.Runtime.World.Authority.Open(s.Member.Id);
        var replay = service.Execute(reconnected, request);
        Assert.Equal(receipt.Roles, replay.Roles); Assert.Equal(receipt.RequestId, replay.RequestId);
        var lookup = service.Lookup(reconnected, request.Id);
        Assert.Equal(CreationRequestStatus.Committed, lookup.Status); Assert.Equal(receipt.Roles, lookup.Receipt!.Roles);
        Assert.Equal(CreationRequestStatus.Unknown, service.Lookup(s.Runtime.World.Authority.Open(s.Approver.Id), request.Id).Status);
        Assert.Equal(460, s.Runtime.World.Players.Find(s.Approver.Id)!.State.Money);
        foreach (var entity in receipt.Roles)
        {
            Assert.True(s.Runtime.World.Entities.TryResolve(entity, out var role));
            Assert.True(s.Runtime.World.Players.CanControl(s.Member.Id, role));
        }
        Assert.Throws<CreationRequestConflictException>(() => service.Execute(reconnected, request with { Start = "changed" }));
        Assert.Throws<InvalidOperationException>(() => service.Execute(reconnected, request with { Id = Guid.NewGuid() }));
    }

    [Fact]
    public void InitialCreationUsesIndependentFactionAndDoesNotReissueStartResources()
    {
        using var s = new Scene();
        var grant = s.Runtime.World.CreationQualifications.IssueInitial(s.Newcomer.Id, "approved", 10);
        var request = s.Request(grant, "Independent");
        var connection = s.Runtime.World.Authority.Open(s.Newcomer.Id);
        var receipt = s.Service.Execute(connection, request);
        Assert.True(receipt.IncludesStartResources); Assert.Equal(0, receipt.Paid);
        Assert.NotEqual(s.Data.PlayerFaction, receipt.Faction);
        Assert.Equal(1234, s.Runtime.World.Factions.Find(receipt.Faction)!.Money);
        Assert.Equal(500, s.Approver.State.Money);
        Assert.Equal(PlayerRoleHistory.Assigned, s.Newcomer.RoleHistory);
        Assert.Equal(receipt.Roles, s.Service.Execute(connection, request).Roles);
        Assert.Equal(1234, s.Runtime.World.Factions.Find(receipt.Faction)!.Money);
        Assert.False(s.Runtime.World.HasPendingDurableMutation);
        Assert.NotNull(s.Store.LoadCurrent());
    }

    [Fact]
    public void DiskFailureFreezesTheSameResultAndReconnectRetriesWithoutRegenerationOrSecondCharge()
    {
        using var s = new Scene();
        var otherService = s.MakeService(s.Checkpoints, s.Data);
        var grant = s.Sponsored(); var request = s.Request(grant);
        var old = s.Runtime.World.Authority.Open(s.Member.Id);
        Directory.CreateDirectory(s.Root);
        using (var competingWriter = new FileStream(Path.Combine(s.Root, "writer.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => s.Service.Execute(old, request));
            Assert.True(s.Service.WaitingForPersistence); Assert.True(s.Runtime.World.HasPendingDurableMutation);
            var lookup = s.Service.Lookup(old, request.Id);
            Assert.Equal(CreationRequestStatus.PendingPersistence, lookup.Status); Assert.Null(lookup.Receipt);
            Assert.Equal(CreationRequestStatus.Busy, otherService.Lookup(old, request.Id).Status);
            Assert.Equal(460, s.Approver.State.Money);
            Assert.Throws<InvalidOperationException>(() => s.Runtime.Step());
            Assert.Throws<InvalidOperationException>(() => s.Runtime.World.RunTick());
            Assert.Throws<InvalidOperationException>(() => s.Runtime.World.ApplyPausedInterfaceCommands());
            Assert.Throws<InvalidOperationException>(() => otherService.Execute(old, request));
            Assert.Throws<InvalidOperationException>(() => s.Checkpoints.Capture());
            Assert.False(s.Runtime.World.Authority.BeginSynchronization(old));
            Assert.Null(s.Store.LoadCurrent());
        }
        var expected = s.Runtime.World.CreationQualifications.Receipt(s.Member.Id, request.Id)!.Roles;
        var current = s.Runtime.World.Authority.Open(s.Member.Id);
        Assert.Throws<InvalidOperationException>(() => s.Service.Execute(old, request));
        var receipt = s.Service.Execute(current, request);
        Assert.Equal(expected, receipt.Roles); Assert.Equal(460, s.Approver.State.Money);
        Assert.False(s.Service.WaitingForPersistence); Assert.False(s.Runtime.World.HasPendingDurableMutation);
        Assert.True(s.Runtime.World.Authority.BeginSynchronization(current));
        s.Runtime.Step();
    }

    [Fact]
    public void FailedPolicyOrRevokedApprovalCreatesNothingAndDoesNotConsumeQualification()
    {
        using var s = new Scene(); var grant = s.Sponsored(); var request = s.Request(grant);
        var connection = s.Runtime.World.Authority.Open(s.Member.Id);
        Assert.Throws<InvalidOperationException>(() => s.Service.Execute(connection, request with { Start = "not-approved" }));
        s.Runtime.World.Players.SetMembership(s.Approver.Id, s.Data.PlayerFaction, FactionPermission.None);
        Assert.Throws<InvalidOperationException>(() => s.Service.Execute(connection, request));
        Assert.Equal(0, s.Runtime.World.Characters.Count); Assert.Equal(500, s.Approver.State.Money);
        Assert.Equal(CreationQualificationStatus.Available, s.Runtime.World.CreationQualifications.Find(grant.Id)!.Status);
        Assert.False(s.Runtime.World.HasPendingDurableMutation);
    }

    [Fact]
    public void ReceiptRemainsHistoricalAfterDeathAndMembershipRevocation()
    {
        using var s = new Scene(); var grant = s.Sponsored(); var request = s.Request(grant);
        var receipt = s.Service.Execute(s.Runtime.World.Authority.Open(s.Member.Id), request);
        foreach (var entity in receipt.Roles)
        {
            Assert.True(s.Runtime.World.Entities.TryResolve(entity, out var role));
            s.Runtime.World.Characters.Cold(role.Slot)!.DiedTick = 0;
        }
        s.Runtime.World.Players.RemoveMembership(s.Member.Id, s.Data.PlayerFaction);
        s.Checkpoints.Commit(s.Checkpoints.Capture());
        var data = Data();
        var restored = s.Checkpoints.Restore(s.Store.LoadCurrent()!, data, Systems, Options(data));
        var service = s.MakeService(restored.Coordinator, data);
        var connection = s.Runtime.World.Authority.Open(s.Member.Id);
        Assert.Equal(receipt.Roles, service.Execute(connection, request).Roles);
        Assert.False(s.Runtime.World.Authority.BeginSynchronization(connection));
        Assert.Equal(PlayerEntryRoute.AwaitingAssignment, s.Runtime.World.Players.AssessEntry(s.Member.Id).Route);
        Assert.Equal(460, s.Runtime.World.Factions.Find(data.PlayerFaction)!.Money);
    }

    [Fact]
    public void SupersededCheckpointProducerCannotStartCreatingRoles()
    {
        using var s = new Scene(); var request = s.Request(s.Sponsored());
        _ = new WorldCheckpointCoordinator(s.Runtime, s.Data, s.Store, Options(s.Data));
        Assert.Throws<InvalidOperationException>(() => s.Service.Execute(s.Runtime.World.Authority.Open(s.Member.Id), request));
        Assert.Equal(0, s.Runtime.World.Characters.Count); Assert.Equal(500, s.Approver.State.Money);
        Assert.False(s.Runtime.World.HasPendingDurableMutation);
    }

    sealed class FailingGround : IWalkability
    {
        public Func<bool>? Fail;
        public float GroundHeight(float x, float z) => Fail?.Invoke() == true ? throw new IOException("Injected prepared-region failure") : 300;
        public bool IsWalkable(float x, float z) => true;
        public PathResult FindPath(Vector3 from, Vector3 to) => PathResult.NotFound;
    }

    [Fact]
    public void FailedInitialMaterializationReleasesQualificationAndRetriesItsReservedFaction()
    {
        var ground = new FailingGround();
        using var s = new Scene(ground);
        var grant = s.Runtime.World.CreationQualifications.IssueInitial(s.Newcomer.Id, "approved", 10);
        var request = s.Request(grant, "Retry faction");
        string identity = "creation:" + s.Newcomer.Id.Value + ":" + request.Id.ToString("N");
        ground.Fail = () => s.Data.FactionIndex(identity) is >= 0 and var f && s.Runtime.World.Factions.Find(f) is not null;
        var connection = s.Runtime.World.Authority.Open(s.Newcomer.Id);
        Assert.Throws<IOException>(() => s.Service.Execute(connection, request));
        int reserved = s.Data.FactionIndex(identity);
        Assert.True(reserved >= 0); Assert.Null(s.Runtime.World.Factions.Find(reserved));
        Assert.Equal(0, s.Runtime.World.Characters.Count); Assert.Empty(s.Newcomer.Memberships);
        Assert.Equal(PlayerRoleHistory.NeverAssigned, s.Newcomer.RoleHistory);
        Assert.Equal(CreationQualificationStatus.Available, s.Runtime.World.CreationQualifications.Find(grant.Id)!.Status);
        Assert.False(s.Runtime.World.HasPendingDurableMutation); Assert.False(s.Service.WaitingForPersistence);
        ground.Fail = null;
        var receipt = s.Service.Execute(connection, request);
        Assert.Equal(reserved, receipt.Faction); Assert.Equal(1234, s.Runtime.World.Factions.Find(reserved)!.Money);
        Assert.Equal(500, s.Approver.State.Money);
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("payment")]
    [InlineData("missing")]
    public void CorruptCreationReceiptsCannotReplaceTheLiveWorld(string defect)
    {
        using var s = new Scene(); var request = s.Request(s.Sponsored());
        var receipt = s.Service.Execute(s.Runtime.World.Authority.Open(s.Member.Id), request);
        var checkpoint = s.Store.LoadCurrent()!;
        var json = System.Text.Json.Nodes.JsonNode.Parse(checkpoint.Metadata)!;
        var receipts = json["Qualifications"]!["Receipts"]!.AsArray();
        if (defect == "roles") receipts[0]!["Roles"]![0]!["Value"] = ulong.MaxValue;
        if (defect == "payment") receipts[0]!["Paid"] = 0;
        if (defect == "missing") receipts.Clear();
        checkpoint = checkpoint with { Metadata = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()) };
        var world = s.Runtime.World; var data = Data();
        Assert.Throws<InvalidDataException>(() => s.Checkpoints.Restore(checkpoint, data, Systems, Options(data)));
        Assert.Same(world, s.Runtime.World);
        Assert.Equal(receipt.Roles, world.CreationQualifications.Receipt(s.Member.Id, request.Id)!.Roles);
    }

    [Fact]
    public void UnexpectedFailureAfterGrantingFaultsTheWorldUntilConsistentRestore()
    {
        using var s = new Scene();
        s.Checkpoints.Commit(s.Checkpoints.Capture());
        var previous = s.Store.LoadCurrent()!;
        var grant = new CreationQualification(1, ulong.MaxValue, s.Member.Id, CreationQualificationKind.FactionApproved,
            "approved", 10, s.Data.PlayerFaction, 40, null, s.Approver.Id,
            s.Approver.MembershipRevision(s.Data.PlayerFaction), s.Member.MembershipRevision(s.Data.PlayerFaction),
            Guid.NewGuid(), CreationQualificationStatus.Available);
        s.Runtime.World.CreationQualifications.Restore(new CreationQualificationState(2, 1, ulong.MaxValue, [grant], []), s.Data.Factions.Count);
        Assert.Throws<OverflowException>(() => s.Service.Execute(s.Runtime.World.Authority.Open(s.Member.Id), s.Request(grant)));
        Assert.True(s.Runtime.Faulted); Assert.True(s.Runtime.World.HasPendingDurableMutation);
        Assert.Equal(500, s.Approver.State.Money);
        Assert.Equal(previous.Id, s.Store.LoadCurrent()!.Id);
        var data = Data();
        s.Checkpoints.Restore(previous, data, Systems, Options(data));
        Assert.False(s.Runtime.Faulted); Assert.False(s.Runtime.World.HasPendingDurableMutation);
        Assert.Equal(0, s.Runtime.World.Characters.Count);
    }
}
