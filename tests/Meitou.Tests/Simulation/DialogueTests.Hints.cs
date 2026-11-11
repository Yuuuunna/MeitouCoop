using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using static Meitou.Tests.Gameplay.EconomyTests;

namespace Meitou.Tests.Simulation;

public partial class DialogueTests
{
    [Fact]
    public void RevocationStopsPendingPrivateBubbleLinesBeforeExpansionAndLogging()
    {
        var data = PopulationData.Create(Db([
            Rec("204-gamedata.base", FcsRecordType.FACTION, r => r.Name = "Nameless"),
            Rec("f-d", FcsRecordType.FACTION, r => r.Name = "Villagers"),
            Rec("npc-d", FcsRecordType.CHARACTER, r => r.Name = "Speaker"),
            Rec("mono-d", FcsRecordType.DIALOGUE, r => { r.Bools["monologue"] = true; r.References["lines"] = [Ref("first-l")]; }),
            Rec("first-l", FcsRecordType.DIALOGUE_LINE, r => { r.Strings["text0"] = "First line."; r.References["lines"] = [Ref("later-l")]; }),
            Rec("later-l", FcsRecordType.DIALOGUE_LINE, r => r.Strings["text0"] = "Private future line."),
        ]), []);
        using var v = new MultiplayerVillage(data);
        v.World.Commands.Enqueue(new DialogueEvent(v.NpcA, v.First, 3, "mono-d") { Tick = 0 });
        v.World.RunTick();
        Assert.Equal("First line.", Assert.Single(v.A.State.Messages.Capture().Items).Text);
        v.World.Players.RemoveMembership(v.A.Id, v.World.Characters.Cold(v.First.Slot)!.Faction);
        v.World.RunTicks(150);
        Assert.Equal("First line.", Assert.Single(v.A.State.Messages.Capture().Items).Text);
        Assert.DoesNotContain(v.Dialogue.PublishSpeech(), line => line.Text == "Private future line.");
        Assert.Empty(v.B.State.Messages.Capture().Items);
    }

    [Fact]
    public void ConversationLogsStayWithTheirParticipantsAndHintQueriesDoNotPostMessages()
    {
        using var v = new MultiplayerVillage(); v.B.State.Money = 100;
        v.OpenBoth();
        Assert.Contains(v.A.State.Messages.Capture().Items, m => m.Kind == PlayerMessageKind.Dialogue && m.Text.Contains("rich", StringComparison.Ordinal));
        Assert.Contains(v.B.State.Messages.Capture().Items, m => m.Kind == PlayerMessageKind.Dialogue && m.Text == "Hello.");
        Assert.DoesNotContain(v.B.State.Messages.Capture().Items, m => m.Text.Contains("rich", StringComparison.Ordinal));
        Assert.Empty(v.World.Player.Messages.Capture().Items); Assert.Empty(v.World.TakeSaid());
        var before = v.A.State.Messages.Capture().HighWater;
        _ = v.World.CanTalk(v.NpcA, v.First);
        Assert.Equal(before, v.A.State.Messages.Capture().HighWater);
    }

    [Fact]
    public void FollowupInterruptBelongsToItsPlayerEvenWhenAnotherMemberSharesTheFaction()
    {
        var data = PopulationData.Create(Db([
            Rec("204-gamedata.base", FcsRecordType.FACTION, r => r.Name = "Nameless"),
            Rec("f-d", FcsRecordType.FACTION, r => r.Name = "Villagers"),
            Rec("npc-d", FcsRecordType.CHARACTER, r => r.References["dialogue"] = [Ref("intro-d")]),
            Rec("intro-d", FcsRecordType.DIALOGUE, r => r.References["lines"] = [Ref("intro-l")]),
            Rec("intro-l", FcsRecordType.DIALOGUE_LINE, r =>
            {
                r.Strings["text0"] = "Come back later.";
                r.References["locks"] = [Ref("intro-d")]; r.References["interrupt"] = [Ref("follow-d")];
            }),
            Rec("follow-d", FcsRecordType.DIALOGUE, r => r.References["lines"] = [Ref("follow-l")]),
            Rec("follow-l", FcsRecordType.DIALOGUE_LINE, r => r.Strings["text0"] = "Your promised follow-up."),
        ]), []);
        using var v = new MultiplayerVillage(data, sharedFaction: true);
        v.Send(v.A.Id, new TalkOrder(v.First, v.NpcA) { Tick = 0 }); v.World.RunTicks(2);
        var first = v.View(v.A.Id);
        Assert.True(v.World.WantsToTalk(v.NpcA, v.A.Id)); Assert.False(v.World.WantsToTalk(v.NpcA, v.B.Id));
        v.Send(v.A.Id, new EndConversation { Tick = 0, SessionId = first.SessionId }); v.World.RunTick();
        ulong before = v.World.StateHash();
        Assert.True(v.World.CanTalk(v.NpcA, v.First)); Assert.False(v.World.CanTalk(v.NpcA, v.Second));
        Assert.Equal(before, v.World.StateHash());
        int otherFaction = data.RegisterFaction("alternate", "Alternate", data.PlayerFaction);
        v.World.Players.SetMembership(v.A.Id, otherFaction, FactionPermission.All);
        var alternate = v.Village.Spawn("alternate", Vector2.Zero, otherFaction, true, "");
        v.World.Players.AssignInitial(v.A.Id, alternate);
        Assert.False(v.World.CanTalk(v.NpcA, alternate));
        v.Send(v.A.Id, new TalkOrder(alternate, v.NpcA) { Tick = 0 }); v.World.RunTicks(2);
        Assert.Null(v.Dialogue.PublishConversation(v.A.Id)); Assert.True(v.World.WantsToTalk(v.NpcA, v.A.Id));
        var ca = v.World.Authority.Open(v.A.Id); var cb = v.World.Authority.Open(v.B.Id);
        Assert.True(v.World.Authority.BeginSynchronization(ca)); Assert.True(v.World.Authority.BeginSynchronization(cb));
        Assert.True(v.World.Authority.CaptureView(ca, new(), new(100))!.Snapshot.Characters.Single(c => c.Id == v.NpcA).WantsToTalk);
        Assert.False(v.World.Authority.CaptureView(cb, new(), new(100))!.Snapshot.Characters.Single(c => c.Id == v.NpcA).WantsToTalk);
        v.Send(v.B.Id, new TalkOrder(v.Second, v.NpcA) { Tick = 0 }); v.World.RunTicks(2);
        Assert.Null(v.Dialogue.PublishConversation(v.B.Id)); Assert.True(v.World.WantsToTalk(v.NpcA, v.A.Id));
        v.Send(v.A.Id, new TalkOrder(v.First, v.NpcA) { Tick = 0 }); v.World.RunTicks(2);
        Assert.Equal("Your promised follow-up.", v.View(v.A.Id).Text);
        Assert.False(v.World.WantsToTalk(v.NpcA, v.A.Id));
    }

    [Fact]
    public void TalkAffordanceQueriesRecheckActorGrantAndCurrentObservationWithoutChangingWorldState()
    {
        using var v = new MultiplayerVillage();
        var connection = v.World.Authority.Open(v.A.Id);
        var grant = v.World.Players.ControlOf(v.First)!.Value;
        ulong membership = v.A.MembershipRevision(grant.Faction);
        PlayerTalkHint Query(CharacterId target, PlayerViewRequest? filter = null) => v.World.Authority.QueryTalk(connection,
            v.First, target, grant, membership, filter ?? new(), new(100));
        Assert.Equal(TalkHintStatus.Unknown, Query(v.NpcA).Status);
        Assert.True(v.World.Authority.BeginSynchronization(connection));
        var view = v.World.Authority.CaptureView(connection, new(), new(100))!;
        Assert.True(v.World.Authority.ActivateAfterView(connection, view.Publication));
        ulong before = v.World.StateHash();
        Assert.Equal(TalkHintStatus.Available, Query(v.NpcA).Status);
        Assert.Equal(before, v.World.StateHash());
        Assert.Equal(TalkHintStatus.Unknown, Query(new(20000, 1)).Status);
        Assert.Equal(TalkHintStatus.Unknown, Query(v.NpcA, new([])).Status);
        int npcFaction = v.World.Characters.Cold(v.NpcA.Slot)!.Faction;
        v.World.Players.SetMembership(v.A.Id, npcFaction, FactionPermission.All);
        v.World.Players.AssignInitial(v.A.Id, v.NpcA);
        Assert.Equal(TalkHintStatus.Unavailable, Query(v.NpcA).Status);
        v.World.Players.RemoveMembership(v.A.Id, npcFaction);
        Assert.Equal(TalkHintStatus.Available, Query(v.NpcA).Status);
        v.World.Characters.Place(v.NpcA.Slot, new Vector3(20000, 150, 0), 0);
        Assert.Equal(TalkHintStatus.Unknown, Query(v.NpcA).Status);
        v.World.Players.SetMembership(v.A.Id, grant.Faction, FactionPermission.ReadFunds);
        Assert.Equal(TalkHintStatus.StaleAuthority, Query(v.NpcA).Status);
    }

    [Fact]
    public void AttributedTalkThatBecameHostileDoesNotTurnIntoAnAttack()
    {
        using var v = new MultiplayerVillage();
        Assert.True(v.World.CanTalk(v.NpcA, v.First));
        v.Village.Data.Relations.Set(v.World.Characters.Cold(v.NpcA.Slot)!.Faction, v.World.Characters.Cold(v.First.Slot)!.Faction, -100);
        Assert.False(v.World.CanTalk(v.NpcA, v.First));
        v.Send(v.A.Id, new TalkOrder(v.First, v.NpcA) { Tick = 0 }); v.World.RunTick();
        Assert.Null(v.Dialogue.PublishConversation(v.A.Id));
        Assert.DoesNotContain(v.World.Commands.TakeDue(long.MaxValue), c => c is Meitou.Simulation.Combat.AttackOrder);
        Assert.Contains("This character will not talk right now.", v.World.TakeMessages(v.A.Id));
        Assert.Empty(v.World.TakeMessages(v.B.Id));
    }

    [Fact]
    public void HeadlessGameplayRulesEnableAmbientBubblesAndSelfOwnedNpcContext()
    {
        Assert.True(Meitou.Runtime.HeadlessWorld.ConfigureGameplay(null, new GameplayRules()).Dialogue!.AmbientEvents);
        var disabled = new StandardSystemOptions { Dialogue = new DialogueSettings { AmbientEvents = false } };
        Assert.False(Meitou.Runtime.HeadlessWorld.ConfigureGameplay(disabled, new GameplayRules()).Dialogue!.AmbientEvents);

        var data = PopulationData.Create(Db([
            Rec("204-gamedata.base", FcsRecordType.FACTION, r => r.Name = "Nameless"),
            Rec("f-d", FcsRecordType.FACTION, r => r.Name = "Villagers"),
            Rec("npc-d", FcsRecordType.CHARACTER, r => r.Name = "Speaker"),
            Rec("self-d", FcsRecordType.DIALOGUE, r => { r.Bools["monologue"] = true; r.References["lines"] = [Ref("self-l")]; }),
            Rec("self-l", FcsRecordType.DIALOGUE_LINE, r => r.Strings["text0"] = "Still breathing."),
        ]), []);
        using var v = new MultiplayerVillage(data);
        v.World.Commands.Enqueue(new DialogueEvent(v.First, CharacterId.None, 3, "self-d") { Tick = 0 });
        v.World.RunTick();
        Assert.Contains(v.Dialogue.PublishSpeech(), b => b.Speaker == v.First && b.Text == "Still breathing.");
        Assert.Equal("Still breathing.", Assert.Single(v.A.State.Messages.Capture().Items).Text);
        Assert.Empty(v.B.State.Messages.Capture().Items);
    }
}
