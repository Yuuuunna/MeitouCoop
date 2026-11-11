using Meitou.Simulation.Population;
using System.Text.Json;
using Meitou.Content;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Ui;
using Meitou.Data;
using Meitou.Game.Editor;
using Meitou.Game.Host;
using Meitou.Game.Settings;
using Meitou.Game.Ui;
using Meitou.Networking;
using Meitou.Simulation.Editing;
using Meitou.Simulation;

namespace Meitou.Game.Title;

/// <summary>
/// The multiplayer form, account admission and server-backed character editor, before a remote host takes the connection.
/// </summary>
sealed class OnlineAdmission(PlayerInterface player)
{
    /// <summary>
    /// The installed content used to verify server compatibility and prepare character appearances.
    /// </summary>
    public GameInstall? OnlineInstall { get; set; }

    /// <summary>
    /// The most recently entered server address, restored by the host.
    /// </summary>
    public string LastServerAddress { get; set; } = "localhost:27800";

    /// <summary>
    /// Persists a newly entered server address before connection begins.
    /// </summary>
    public Action<string>? RememberServerAddress { get; set; }

    /// <summary>
    /// The pending authenticated lobby; null outside online preparation.
    /// </summary>
    OnlineLobby? _lobby;

    /// <summary>
    /// Whether online admission owns the beginning and editor windows.
    /// </summary>
    internal bool OnlinePreparing => _lobby is not null;

    /// <summary>
    /// Whether a lobby operation is still running.
    /// </summary>
    internal bool OnlineBusy => _onlineWork is not null;

    /// <summary>
    /// The current network operation; consumed on the UI thread after completion.
    /// </summary>
    Task? _onlineWork;

    /// <summary>
    /// The UI transition to run when the current operation succeeds.
    /// </summary>
    Action? _onlineDone;

    /// <summary>
    /// Preparation is being abandoned after its running operation finishes.
    /// </summary>
    bool _onlineCancelling;

    /// <summary>
    /// The connection form's current status or error.
    /// </summary>
    string _onlineStatus = "Enter the server address. Your identity is created automatically and remembered on this computer.";

    /// <summary>
    /// The last appearance choices sent for a server preview, serialized for change detection.
    /// </summary>
    string _sentAppearance = "";

    /// <summary>
    /// The wall-clock timestamp of the last debounced appearance preview.
    /// </summary>
    long _lastOnlinePreview;

    /// <summary>
    /// Synthetic editor character ids mapped to the server's roster member indices.
    /// </summary>
    readonly Dictionary<CharacterId, int> _onlineMembers = [];

    /// <summary>
    /// The server appearance and its cached display variant without clothing.
    /// </summary>
    CharacterAppearance? _onlineDressed, _onlineUndressed;

    /// <summary>
    /// The races approved by the server for an editor member.
    /// </summary>
    internal IReadOnlySet<string> OnlineAllowedRaces(CharacterId id) =>
        _lobby?.Preview is { } preview && _onlineMembers.TryGetValue(id, out int member)
            ? preview.Members[member].Editor?.AllowedRaces.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>()
            : new HashSet<string>();

    /// <summary>
    /// Whether the editor is editing the server's uncommitted creation draft.
    /// </summary>
    public bool OnlineEditor => _lobby is not null && _onlineMembers.Count > 0 && player.CharacterEditor.EditorOpen;

    /// <summary>
    /// The admitted connection transferred to the graphical host; null until entry succeeds.
    /// </summary>
    public RemoteGameSession? ReadyOnline { get; private set; }

    /// <summary>
    /// A control of the connection form, using the original game's skins.
    /// </summary>
    static MyGuiWidget OnlineWidget(string type, string name, string caption, UiRect at, string? skin = null) =>
        new(type, name, skin ?? (type == "Button" ? "Kenshi_Button1" : "Kenshi_TextboxStandardText"), "", "", at, true,
            new Dictionary<string, string> { ["Caption"] = Loc.Tr(caption), ["TextColour"] = type == "EditBox" ? MyGuiColours.InkText : MyGuiColours.Named("Main", "afa68b"), ["TextAlign"] = "Left VCenter", ["WordWrap"] = "true" }, new Dictionary<string, string>(), []);

    /// <summary>
    /// Adds the Multiplayer button to the original title layout.
    /// </summary>
    internal static MyGuiLayout WithMultiplayer(MyGuiLayout layout)
    {
        var source = layout.Find("NewGameButton");
        if (source is null) return layout;
        var button = source with
        {
            Name = "MultiplayerButton",
            Properties = new Dictionary<string, string>(source.Properties) { ["Caption"] = "Multiplayer" },
            Children = [],
            Position = source.Position with { Y = source.Position.Y - source.Position.Height * 1.15f }
        };
        MyGuiWidget Insert(MyGuiWidget w) => w with
        {
            Children = w.Children.Any(c => c.Name == source.Name)
            ? [button, .. w.Children] : w.Children.Select(Insert).ToArray()
        };
        return layout with { Roots = layout.Roots.Any(w => w.Name == source.Name) ? [button, .. layout.Roots] : layout.Roots.Select(Insert).ToArray() };
    }

    /// <summary>
    /// The server address, status and connection buttons in the multiplayer form.
    /// </summary>
    internal static MyGuiLayout OnlineLayout() => new("3.2.0", [
        OnlineWidget("Window", "OnlineRoot", "Multiplayer", new(.26f, .24f, .48f, .45f), "Kenshi_WindowC") with { Children = [
            OnlineWidget("TextBox", "ServerLabel", "Server (host:port)", new(.05f, .07f, .90f, .08f)),
            OnlineWidget("EditBox", "ServerAddress", "localhost:27800", new(.05f, .19f, .90f, .13f), "Kenshi_EditBox"),
            OnlineWidget("TextBox", "OnlineStatus", "", new(.05f, .39f, .90f, .30f)),
            OnlineWidget("Button", "OnlineConnect", "Connect", new(.05f, .78f, .42f, .14f)),
            OnlineWidget("Button", "OnlineCancel", "Cancel", new(.53f, .78f, .42f, .14f)),
        ] }
    ]);

    /// <summary>
    /// Handles the title's multiplayer button and the connection form's actions.
    /// </summary>
    internal bool HandleOnlineAction(MyGuiEvent e)
    {
        if (e.Widget == "MultiplayerButton")
        {
            if (player.Session.IsRemote || OnlineInstall is null) return true;
            player.Frontend.CloseTitleWindows();
            player.FaithfulScreens.Open("online");
            player.FaithfulScreens.SetText("online", "ServerAddress", LastServerAddress);
            return true;
        }
        if (e.Screen != "online") return false;
        if (e.Widget == "OnlineCancel")
        {
            CancelOnline();
            return true;
        }
        if (e.Widget != "OnlineConnect" || _onlineWork is not null || _lobby is not null || OnlineInstall is null || player.Frontend.GameData is null) return true;
        string address = player.FaithfulScreens.GetText("online", "ServerAddress").Trim();
        if (address.Length == 0)
        {
            _onlineStatus = "Enter the server address.";
            return true;
        }
        LastServerAddress = address;
        RememberServerAddress?.Invoke(address);
        _lobby = new(OnlineInstall, player.Frontend.GameData);
        StartOnlineWork(() => _lobby.ConnectAsync(address), "Connecting and checking content…", () =>
        {
            if (_lobby.Ready) return;
            var all = NewGameStart.LoadAll(player.Frontend.GameData);
            player.Frontend.Starts = _lobby.Catalog!.Starts.Select(s => all.FirstOrDefault(local => local.Id == s.Id)
                ?? new NewGameStart { Id = s.Id, Name = s.Name, Description = s.Description, Money = s.StartMoney }).ToArray();
            player.Frontend.StartIndex = 0;
            player.AdvancedOptions.AdvancedOpen = false;
            player.FaithfulScreens.Close("online");
            player.FaithfulScreens.Open("new-game");
        });
        return true;
    }

    /// <summary>
    /// Binds the connection form and disables edits while an operation is running.
    /// </summary>
    internal bool BindOnline(string id, UiBindings b)
    {
        if (id != "online") return false;
        b.Text["OnlineStatus"] = Loc.Tr(_onlineStatus);
        b.Actions["OnlineCancel"] = "OnlineCancel";
        b.Disabled.Remove("OnlineCancel");
        if (_onlineWork is null && _lobby is null)
        {
            b.Actions["OnlineConnect"] = "OnlineConnect";
            b.Disabled.Remove("OnlineConnect");
        }
        if (_onlineWork is not null || _lobby is not null) b.Disabled.Add("ServerAddress");
        b.Properties["OnlineConnect"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
        return true;
    }

    /// <summary>
    /// Starts one asynchronous lobby operation and records its UI completion.
    /// </summary>
    void StartOnlineWork(Func<Task> work, string status, Action? completed = null, bool blocking = true)
    {
        _onlineStatus = status;
        _onlineDone = completed;
        _onlineWork = Task.Run(work);
        if (blocking) player.FaithfulScreens.Open("online");
    }

    /// <summary>
    /// Consumes completed lobby work and sends debounced appearance previews from the UI thread.
    /// </summary>
    public void PumpOnline()
    {
        ConsumeCompletedWork();
        FinishCancellation();
        UpdateAppearancePreview();
    }

    /// <summary>
    /// Cancels preparation and closes its windows; an accepted server commit remains durable.
    /// </summary>
    internal void CancelOnline()
    {
        _lobby?.Cancel();
        _onlineCancelling = true;
        player.FaithfulScreens.Close("online");
        player.FaithfulScreens.Close("new-game");
        if (OnlineEditor) player.CharacterEditor.CloseEditor();
    }

    /// <summary>
    /// Cancels remaining work and disposes any lobby connection still owned by the interface.
    /// </summary>
    public void DisposeOnline()
    {
        _lobby?.Cancel();
        try
        {
            _onlineWork?.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            /* cancellation or already presented network failure */
        }
        _lobby?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _lobby = null;
    }

    /// <summary>
    /// Requests a server preview for the currently selected approved beginning.
    /// </summary>
    internal void BeginOnlineStart()
    {
        if (_lobby?.Catalog is not { } catalog || _onlineWork is not null) return;
        var grant = catalog.Qualification.Grant;
        var request = new PlayerCreationRequest(Guid.NewGuid(), grant.Id, grant.Revision, player.Frontend.Starts[player.Frontend.StartIndex].Id);
        StartOnlineWork(() => _lobby.PreviewAsync(request), "Preparing the selected beginning…", OpenOnlineEditor);
    }

    /// <summary>
    /// Builds editable drafts solely from the server's public appearance controls.
    /// </summary>
    void OpenOnlineEditor()
    {
        if (_lobby?.Preview is not { } preview || player.CharacterEditor.EditorData is not { } data) return;
        _onlineMembers.Clear();
        var drafts = new Dictionary<CharacterId, CharacterDraft>();
        foreach (var member in preview.Members.Where(m => !m.Animal && m.Editor is not null))
        {
            var view = member.Editor!;
            var record = new FcsRecord { Type = (int)FcsRecordType.CharacterAppearance };
            record.References["race"] = [new(view.Race, 0, 0, 0)];
            record.Bools["sex female"] = view.Female;
            var choice = new CreationAppearanceChoice(view.Race, view.Female,
                view.Controls.Select(c => new CreationSlider(c.Tab, c.Kind, c.Label, c.Value)).ToArray(),
                view.Pickers.Select(p => new CreationPicker(p.Kind, p.Selected)).ToArray());
            record = CreationAppearanceEditing.Apply(data.Db, data.InstallRoot, record, choice, 1);
            var id = new CharacterId(member.Member, int.MaxValue);
            _onlineMembers[id] = member.Member;
            drafts[id] = new(data.Db, data.InstallRoot, record, member.Name);
        }
        if (drafts.Count == 0)
        {
            player.Confirm("Create characters", $"Create {preview.Members.Length} character(s) and enter the server?", CommitOnlineEditor);
            return;
        }
        player.CharacterEditor.Editor = new(EditKind.Creation, drafts.Keys.ToArray(), new HashSet<CharacterId>(), CharacterId.None);
        foreach (var (id, draft) in drafts)
        {
            player.CharacterEditor.Editor.Drafts[id] = draft;
            player.CharacterEditor.Editor.StartNames[id] = draft.Name;
        }
        _sentAppearance = JsonSerializer.Serialize(OnlineChoices());
        player.CharacterEditor.Previews.Clear();
        player.FaithfulScreens.Close("new-game");
        player.FaithfulScreens.Open("editor");
        player.CharacterEditor.ShowEditorCharacter();
        player.UpdateMenuPause();
    }

    /// <summary>
    /// The names and appearance controls currently selected in the online editor.
    /// </summary>
    CreationMemberChoice[] OnlineChoices() => player.CharacterEditor.Editor is null ? [] : player.CharacterEditor.Editor.Drafts.Select(OnlineChoice).ToArray();

    /// <summary>
    /// Validates names and asks the server to commit the exact choices before entry.
    /// </summary>
    internal void CommitOnlineEditor()
    {
        if (_onlineWork is not null || _lobby?.Request is not { } request) return;
        var choices = OnlineChoices();
        if (choices.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 30 || c.Name.Any(char.IsControl)))

        {
            player.MessageBox("Character name", "Names must contain 1–30 characters.");
            return;
        }
        var final = request with { Choices = choices };
        StartOnlineWork(() => CommitAsync(final),
            "Creating characters and preparing entry…");
    }

    /// <summary>
    /// The latest server preview for the shown editor member, with its clothing visibility applied.
    /// </summary>
    public CharacterAppearance? OnlineEditorAppearance
    {
        get
        {
            if (!OnlineEditor || player.CharacterEditor.Editor is not { } edit || _lobby?.Preview is not { } preview) return null;
            var own = preview.Members[_onlineMembers[edit.Current]].Appearance;
            if (own is null || edit.Clothes || player.Frontend.GameData is null) return own;
            if (!ReferenceEquals(own, _onlineDressed))
            {
                _onlineDressed = own;
                var display = own.CaptureDisplay(player.Frontend.GameData);
                _onlineUndressed = CharacterAppearance.FromDisplay(display with
                {
                    Parts = display.Parts.Where(p => p.Record.Type is not (FcsRecordType.Armour or FcsRecordType.Container)).ToArray(),
                    BodyLayers = [],
                    HiddenParts = 0,
                });
            }
            return _onlineUndressed;
        }
    }

    /// <summary>
    /// Consumes a completed network operation and presents its outcome on the UI thread.
    /// </summary>
    void ConsumeCompletedWork()
    {
        if (_onlineWork is { IsCompleted: true } task)
        {
            _onlineWork = null;
            var done = _onlineDone;
            _onlineDone = null;
            try
            {
                task.GetAwaiter().GetResult();
                if (!_onlineCancelling)
                {
                    player.FaithfulScreens.Close("online");
                    done?.Invoke();
                    if (_lobby?.Ready == true)
                    {
                        ReadyOnline = _lobby.TakeConnection();
                        CancelOnline();
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_onlineCancelling)
                {
                    Console.Error.WriteLine($"online    {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    _onlineStatus = ex.Message;
                    // Reconnect recovers the durable request if a commit's outcome was lost.
                    _onlineCancelling = true;
                    player.FaithfulScreens.Open("online");
                }
            }
        }
    }

    /// <summary>
    /// Disposes abandoned preparation after its running operation has completed.
    /// </summary>
    void FinishCancellation()
    {
        if (_onlineCancelling && _onlineWork is null)
        {
            _lobby?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _lobby = null;
            _onlineCancelling = false;
            if (_onlineMembers.Count > 0) player.CharacterEditor.CloseEditor();
            _onlineMembers.Clear();
            player.Frontend.Starts = player.Frontend.GameData is null ? [] : NewGameStart.LoadAll(player.Frontend.GameData);
            player.Frontend.StartIndex = 0;
            player.FaithfulScreens.Close("new-game");
        }
    }

    /// <summary>
    /// Sends the current valid editor choices when the debounce interval has passed.
    /// </summary>
    void UpdateAppearancePreview()
    {
        if (_onlineWork is null && OnlineEditor && _lobby?.Request is { } request &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_lastOnlinePreview).TotalSeconds > .2)
        {
            var choices = OnlineChoices();
            if (choices.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 30 || c.Name.Any(char.IsControl))) return;
            string signature = JsonSerializer.Serialize(choices);
            if (signature != _sentAppearance)
            {
                _sentAppearance = signature;
                _lastOnlinePreview = System.Diagnostics.Stopwatch.GetTimestamp();
                StartOnlineWork(() => _lobby.PreviewAsync(request with { Choices = choices }), "Updating appearance…", blocking: false);
            }
        }
    }

    /// <summary>
    /// Projects a draft to the public names and appearance controls accepted by the server.
    /// </summary>
    CreationMemberChoice OnlineChoice(KeyValuePair<CharacterId, CharacterDraft> pair)
    {
        var draft = pair.Value;
        var controls = Enum.GetValues<EditorTab>().SelectMany(tab => draft.Controls(tab).Where(c => !c.IsPicker)
            .Select(c => new CreationSlider(tab, c.Kind, c.Label, draft.Value(c)))).ToArray();
        var pickers = Enum.GetValues<EditorTab>().SelectMany(draft.Controls).Where(c => c.IsPicker).Select(c => c.Kind).Distinct()
            .Select(kind => new CreationPicker(kind, draft.Options(kind).ElementAtOrDefault(draft.OptionIndex(kind))?.StringId)).ToArray();
        return new CreationMemberChoice(_onlineMembers[pair.Key], draft.Name, new(draft.Race.StringId, draft.Female, controls, pickers));
    }

    /// <summary>
    /// Previews the final choices once more and commits that same request before admission.
    /// </summary>
    async Task CommitAsync(PlayerCreationRequest request)
    {
        await _lobby!.PreviewAsync(request).ConfigureAwait(false);
        await _lobby.CommitAsync(request).ConfigureAwait(false);
    }

}