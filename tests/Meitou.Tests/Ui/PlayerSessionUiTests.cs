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
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Ui;

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
        var target = new CharacterSnapshot(new(2, 1), null, new(10, 0, 0), 0, []) { Faction = 18, WarParty = true, HostileToPlayer = true };
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
