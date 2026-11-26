using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.Ui;
using Meitou.Game.Host;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Simulation.Session;
using Meitou.Game;
using Meitou.Localisation;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;
using Meitou.Tests.Simulation;
using Meitou.Tests.Engine;

namespace Meitou.Tests.Ui;

[Collection("Shown text")]
public class PlayerSessionUiTests
{
    // Intentionally owns no World or simulation clock: the UI may only consume this presentation boundary.
    sealed class RemotePresentation : IPlayerSession
    {
        public WorldSnapshot CurrentSnapshot { get; set; } = WorldSnapshot.Empty;
        public bool HasWorldView { get; set; } = true;
        public PlayerMessageBatch Messages { get; set; } = PlayerMessageBatch.Empty;
        public object WorldIdentity { get; set; } = new();
        public long CommandTick => 123;
        public CameraRig Camera { get; } = new((_, _) => 0);
        public InputState Input { get; } = new();
        public InputBindings Bindings { get; } = new();
        public GameClock Clock { get; } = new();
        public int ActiveFaction => 17;
        public long? Money { get; set; }
        public bool HasPlayer => true;
        public IReadOnlyList<string> CreationRaces => [];
        public int Formation => (int)SquadFormation.Random;
        public bool IsRemote => true;
        public bool CanSaveWorld => false;
        public bool CanChangeSpeed => false;
        public double TimeScale => 1;
        public void SetSpeed(double speed) => throw new InvalidOperationException("UI must not set shared speed.");
        public void TogglePause() => throw new InvalidOperationException("UI must not pause the shared world.");
        public bool ModalOpen { get; private set; }
        public void SetModalPause(bool paused) => ModalOpen = paused;
        public List<SimCommand> Commands { get; } = [];
        public void Submit(SimCommand command) => Commands.Add(command);
        public bool TalkAvailable, TalkPending, FollowupWaiting;
        public bool CanTalk(CharacterId target, CharacterId? speaker = null) => TalkAvailable;
        public bool TalkHintPending(CharacterId target, CharacterId? speaker = null) => TalkPending;
        public bool WantsToTalk(CharacterId target) => FollowupWaiting;
        public ProspectReport? Prospected => null;
        public void EnableFloaters(bool enabled) { }
        public bool TryDequeueFloater(out Floater floater) { floater = default; return false; }
    }

    [Fact]
    public void InterfaceSubmitsIntentThroughSessionWithoutALocalWorld()
    {
        var session = new RemotePresentation();
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position);
        var role = new CharacterId(4, 2);
        ui.Select([role], additive: false);
        ui.Move(new(5, 6, 7), queued: true);
        var select = Assert.IsType<SelectCommand>(session.Commands[0]);
        Assert.Equal(role, Assert.Single(select.Characters)); Assert.Equal(123, select.Tick);
        var move = Assert.IsType<MoveOrder>(session.Commands[1]);
        Assert.Equal(new Vector3(5, 6, 7), move.Target); Assert.True(move.Queued); Assert.Equal(123, move.Tick);
        Assert.Same(WorldSnapshot.Empty, session.CurrentSnapshot);
    }

    /// <summary>
    /// A remote UI resolves same-named characters from compatible presentation records and exact replicated IDs, without a local population.
    /// Text refresh stays local, and an old baseline cannot restore a character after the authority withdraws it.
    /// </summary>
    [Fact]
    public void Remote_character_names_use_presentation_records_and_replicated_identity_and_respect_withdrawal()
    {
        var database = WorldViewCodecTests.Database();
        var source = WorldViewCodecTests.View(database);
        var first = source.Snapshot.Characters[0] with
        {
            Name = "Guard", RecordId = "2-t", LastingId = new(0xfedcba9876543210), Animations = [],
        };
        var second = first with { Id = new(2, 2), LastingId = new(0xfedcba9876543211) };
        source = source with { Snapshot = source.Snapshot with { Characters = [first, second] } };
        var codec = new WorldViewCodec(database);
        var publisher = new GameStatePublisher(codec, id => (ulong)id.Slot + 100, 11, 22);
        var baseline = publisher.Capture(source);
        var replica = new GameStateReplica(codec, 11, 22);
        foreach (var frame in baseline.Frames) replica.Apply(frame);
        var session = new RemotePresentation { CurrentSnapshot = replica.View!.Snapshot };
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position)
        {
            PopulationOf = () => throw new InvalidOperationException("Remote presentation must not inspect a local population."),
            FactionName = _ => "Testfolk",
        };
        ui.ConfigureFrontend(database, () => [], "");
        var host = new object();
        string title = "Captain";
        ShownText.Add(host, "remote-character-names", context => context.Character == 0 ? null : $"{title}:{context.Character:X16}:{context.Place}");
        try
        {
            var a = session.CurrentSnapshot.Characters.Single(c => c.Id == first.Id);
            var b = session.CurrentSnapshot.Characters.Single(c => c.Id == second.Id);
            Assert.Equal(first.LastingId, a.LastingId);
            Assert.Equal(second.LastingId, b.LastingId);
            Assert.Equal("Captain:FEDCBA9876543210:Label", ui.ShownName(a, ShownPlace.Label));
            Assert.Equal("Captain:FEDCBA9876543211:Label", ui.ShownName(b, ShownPlace.Label));
            Assert.Equal("Captain:FEDCBA9876543210:Panel", ui.ShownLeadName(a, ShownPlace.Panel));
            Assert.Equal("Testfolk", ui.ShownFactionName(17));
            Assert.Equal("Testfolk", ui.ShownFactionName("Testfolk"));
            title = "Commander";
            ShownText.Refresh();
            Assert.Equal("Commander:FEDCBA9876543210:Label", ui.ShownName(a, ShownPlace.Label));
            Assert.All(session.CurrentSnapshot.Characters, character => Assert.Equal("Guard", character.Name));

            var unchanged = publisher.Capture(source with { Publication = 2 });
            Assert.DoesNotContain((ushort)2007, unchanged.ComponentBytes.Keys);
            Assert.DoesNotContain((ushort)2008, unchanged.ComponentBytes.Keys);
            Assert.DoesNotContain((ushort)2104, unchanged.ComponentBytes.Keys);
            var withdrawal = publisher.Capture(source with { Publication = 3, Snapshot = source.Snapshot with { Characters = [] } });
            foreach (var frame in withdrawal.Frames) replica.Apply(frame);
            foreach (var frame in baseline.Frames) replica.Apply(frame);
            session.CurrentSnapshot = replica.View!.Snapshot;
            Assert.Empty(session.CurrentSnapshot.Characters);
            Assert.Null(ui.ShownLeadName(session.CurrentSnapshot.Characters.FirstOrDefault(), ShownPlace.Panel));
            Assert.Empty(session.Commands);
        }
        finally
        {
            ShownText.Remove(host);
            ui.DisposeUi();
        }
    }

    /// <summary>
    /// A quick KO keeps selection order, like the context menu and server preview, instead of substituting the nearest selected character.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Quick_stealth_ko_submits_the_selection_used_for_the_displayed_chance(bool hostile)
    {
        var first = new CharacterSnapshot(new(1, 1), null, Vector3.Zero, 0, []) { IsPlayer = true, Faction = 17 };
        var nearer = new CharacterSnapshot(new(2, 1), null, new(9, 0, 0), 0, []) { IsPlayer = true, Faction = 17 };
        var target = new CharacterSnapshot(new(3, 1), null, new(10, 0, 0), 0, [])
        {
            Faction = 18, HostileToPlayer = hostile, CanStealthKo = true, StealthKoChance = 75,
        };
        var session = new RemotePresentation { CurrentSnapshot = new(1, [first, nearer, target]) };
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position);
        CharacterId[] selection = [first.Id, nearer.Id];

        var quick = ui.QuickOrderFor(target, selection)!.Value;
        quick.Do!();

        Assert.Equal("knockout", quick.Pointer);
        var order = Assert.IsType<Meitou.Simulation.Carrying.StealthKoOrder>(Assert.Single(session.Commands));
        Assert.Equal(selection, order.Attackers);
        Assert.Equal(target.Id, order.Target);
    }

    [Fact]
    public void RemoteHistoryRestoresWithoutPopupReplayAndNewNoticesAreDeliveredOnce()
    {
        var session = new RemotePresentation { HasWorldView = false };
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position);
        ui.PollSessionMessages();
        session.HasWorldView = true;
        var old = new PlayerMessage(1, PlayerMessageKind.Notice, "", "Old private notice");
        session.Messages = new(1, [old]); ui.PollSessionMessages();
        Assert.Contains("Old private notice", ui.MessagesText()); Assert.Equal(0, ui.PendingNoticeCount);
        session.Messages = new(2, [old, new(2, PlayerMessageKind.Notice, "", "Fresh private notice")]);
        ui.PollSessionMessages(); ui.PollSessionMessages();
        Assert.Equal(1, ui.PendingNoticeCount);
        Assert.Equal(2, ui.MessagesText().Split("Fresh private notice", StringSplitOptions.None).Length);
        session.HasWorldView = false; ui.PollSessionMessages(); session.HasWorldView = true; ui.PollSessionMessages();
        Assert.Equal(1, ui.PendingNoticeCount);
    }

    [Fact]
    public void PrivateFollowupMakesAnEnemyTalkCandidateWithoutExposingCampaignInternals()
    {
        var own = new CharacterSnapshot(new(1, 1), null, Vector3.Zero, 0, []) { IsPlayer = true, Faction = 17 };
        var target = new CharacterSnapshot(new(2, 1), null, new(10, 0, 0), 0, []) { Faction = 18, HostileToPlayer = true };
        var session = new RemotePresentation { CurrentSnapshot = new(1, [own, target]), FollowupWaiting = true, TalkPending = true };
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position);
        ui.QuickOrderFor(target, [own.Id])!.Value.Do!();
        ui.PollPendingTalk(); Assert.Empty(session.Commands);
        session.TalkPending = false; session.TalkAvailable = true; ui.PollPendingTalk();
        Assert.IsType<Meitou.Simulation.Dialogue.TalkOrder>(Assert.Single(session.Commands));
    }

    [Fact]
    public void UnknownHostileDialogueDoesNotBecomeAnAttackAndANewerOrderCancelsTheDeferredClick()
    {
        var own = new CharacterSnapshot(new(1, 1), null, Vector3.Zero, 0, []) { IsPlayer = true, Faction = 17 };
        var target = new CharacterSnapshot(new(2, 1), null, new(10, 0, 0), 0, []) { Faction = 18, IsDiplomat = true, HostileToPlayer = true };
        var session = new RemotePresentation { CurrentSnapshot = new(1, [own, target]), TalkPending = true };
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position);
        ui.QuickOrderFor(target, [own.Id])!.Value.Do!();
        ui.PollPendingTalk(); Assert.Empty(session.Commands);
        session.TalkPending = false; session.TalkAvailable = true;
        ui.PollPendingTalk();
        Assert.IsType<Meitou.Simulation.Dialogue.TalkOrder>(Assert.Single(session.Commands));
        session.Commands.Clear(); session.TalkAvailable = false; session.TalkPending = true;
        ui.QuickOrderFor(target, [own.Id])!.Value.Do!();
        ui.Move(new(50, 0, 0), false);
        session.TalkPending = false; session.TalkAvailable = true; ui.PollPendingTalk();
        Assert.IsType<MoveOrder>(Assert.Single(session.Commands));
        session.Commands.Clear(); session.TalkAvailable = false; session.TalkPending = true;
        ui.QuickOrderFor(target, [own.Id])!.Value.Do!();
        session.WorldIdentity = new(); // the same slots may belong to a different world after a session change
        session.TalkPending = false; session.TalkAvailable = true; ui.PollPendingTalk();
        Assert.Empty(session.Commands);
    }

    [Theory, Slow]
    [InlineData(UiMode.Faithful)]
    [InlineData(UiMode.Meitou)]
    public void RemoteHudHidesUnauthorizedFundsAndLocalSaveActionsAndDoesNotOfferSharedPause(UiMode mode)
    {
        var install = GameInstall.Locate(); Assert.SkipWhen(install is null, "No Kenshi install configured.");
        var resources = OgreScriptResources.ForInstall(install!, LoadOrder.FromInstall(install!));
        var session = new RemotePresentation();
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { Width = 1600, Height = 900 };
        ui.LoadUi(mode, name => resources.Find(name));
        ui.ConfigureFrontend(SyntheticTown.Database(), () => throw new InvalidOperationException("Remote UI must not enumerate local saves."), "");
        try
        {
            var hud = ui.FaithfulBindings("hud").Bindings;
            Assert.Equal("—", hud.Text["MoneyAmountText"]);
            for (int i = 1; i <= 4; i++) Assert.Contains($"TimeSpeedButton{i}", hud.Disabled);
            session.Money = 731;
            Assert.Equal("c.731", ui.FaithfulBindings("hud").Bindings.Text["MoneyAmountText"]);
            ui.ShowTitle();
            var title = ui.FaithfulBindings("title").Bindings;
            foreach (string button in new[] { "NewGameButton", "SaveGameButton", "LoadGameButton", "ImportGameButton", "ContinueButton" })
                Assert.Contains(button, title.Hidden);
            ui.CloseFrontend(); ui.ShowUiPanel("menu");
            Assert.True(session.ModalOpen);
            Assert.Contains("SaveGameButton", ui.FaithfulBindings("menu").Bindings.Hidden);
            foreach (string panel in new[] { "save", "load", "import", "new-game", "title-load", "title-import", "title-new-game" })
            {
                ui.ShowUiPanel(panel);
                Assert.DoesNotContain(ui.OpenScreens, s => s is "save" or "load" or "import" or "new-game");
            }
            ui.CloseFrontend(); Assert.False(session.ModalOpen); Assert.Equal(1, session.TimeScale);
        }
        finally { ui.DisposeUi(); }
    }
}
