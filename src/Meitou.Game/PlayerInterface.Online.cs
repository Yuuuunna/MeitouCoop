using System.Text.Json;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Ui;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using Meitou.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    public GameInstall? OnlineInstall { get; set; }
    public string LastServerAddress { get; set; } = "localhost:27800";
    public Action<string>? RememberServerAddress { get; set; }
    OnlineLobby? lobby;
    Task? onlineWork;
    Action? onlineDone;
    bool onlineCancelling;
    string onlineStatus = "Enter the server address. Your identity is created automatically and remembered on this computer.";
    string sentAppearance = "";
    long lastOnlinePreview;
    readonly Dictionary<CharacterId, int> onlineMembers = [];
    public bool OnlineEditor => lobby is not null && onlineMembers.Count > 0 && EditorOpen;
    public RemoteGameSession? ReadyOnline { get; private set; }

    static MyGuiWidget OnlineWidget(string type, string name, string caption, UiRect at, string? skin = null) =>
        new(type, name, skin ?? (type == "Button" ? "Kenshi_Button1" : "Kenshi_TextboxStandardText"), "", "", at, true,
            new Dictionary<string, string> { ["Caption"] = Loc.Tr(caption), ["TextColour"] = type == "EditBox" ? MyGuiColours.InkText : MyGuiColours.Named("Main", "afa68b"), ["TextAlign"] = "Left VCenter", ["WordWrap"] = "true" }, new Dictionary<string, string>(), []);

    static MyGuiLayout WithMultiplayer(MyGuiLayout layout)
    {
        var source = layout.Find("NewGameButton");
        if (source is null) return layout;
        var button = source with { Name = "MultiplayerButton", Properties = new Dictionary<string, string>(source.Properties) { ["Caption"] = "Multiplayer" },
            Children = [], Position = source.Position with { Y = source.Position.Y - source.Position.Height * 1.15f } };
        MyGuiWidget Insert(MyGuiWidget w) => w with { Children = w.Children.Any(c => c.Name == source.Name)
            ? [button, .. w.Children] : w.Children.Select(Insert).ToArray() };
        return layout with { Roots = layout.Roots.Any(w => w.Name == source.Name) ? [button, .. layout.Roots] : layout.Roots.Select(Insert).ToArray() };
    }

    static MyGuiLayout OnlineLayout() => new("3.2.0", [
        OnlineWidget("Window", "OnlineRoot", "Multiplayer", new(.26f, .24f, .48f, .45f), "Kenshi_WindowC") with { Children = [
            OnlineWidget("TextBox", "ServerLabel", "Server (host:port)", new(.05f, .07f, .90f, .08f)),
            OnlineWidget("EditBox", "ServerAddress", "localhost:27800", new(.05f, .19f, .90f, .13f), "Kenshi_EditBox"),
            OnlineWidget("TextBox", "OnlineStatus", "", new(.05f, .39f, .90f, .30f)),
            OnlineWidget("Button", "OnlineConnect", "Connect", new(.05f, .78f, .42f, .14f)),
            OnlineWidget("Button", "OnlineCancel", "Cancel", new(.53f, .78f, .42f, .14f)),
        ] }
    ]);

    bool HandleOnlineAction(MyGuiEvent e)
    {
        if (e.Widget == "MultiplayerButton")
        {
            if (session.IsRemote || OnlineInstall is null) return true;
            CloseTitleWindows();
            faithfulScreens.Open("online");
            faithfulScreens.SetText("online", "ServerAddress", LastServerAddress);
            return true;
        }
        if (e.Screen != "online") return false;
        if (e.Widget == "OnlineCancel") { CancelOnline(); return true; }
        if (e.Widget != "OnlineConnect" || onlineWork is not null || lobby is not null || OnlineInstall is null || gameData is null) return true;
        string address = faithfulScreens.GetText("online", "ServerAddress").Trim();
        if (address.Length == 0) { onlineStatus = "Enter the server address."; return true; }
        LastServerAddress = address;
        RememberServerAddress?.Invoke(address);
        lobby = new(OnlineInstall, gameData);
        StartOnlineWork(() => lobby.ConnectAsync(address), "Connecting and checking content…", () =>
        {
            if (lobby.Ready) return;
            var all = NewGameStart.LoadAll(gameData);
            starts = lobby.Catalog!.Starts.Select(s => all.FirstOrDefault(local => local.Id == s.Id)
                ?? new NewGameStart { Id = s.Id, Name = s.Name, Description = s.Description, Money = s.StartMoney }).ToArray();
            startIndex = 0;
            advancedOpen = false;
            faithfulScreens.Close("online");
            faithfulScreens.Open("new-game");
        });
        return true;
    }

    bool BindOnline(string id, UiBindings b)
    {
        if (id != "online") return false;
        b.Text["OnlineStatus"] = Loc.Tr(onlineStatus);
        b.Actions["OnlineCancel"] = "OnlineCancel"; b.Disabled.Remove("OnlineCancel");
        if (onlineWork is null && lobby is null) { b.Actions["OnlineConnect"] = "OnlineConnect"; b.Disabled.Remove("OnlineConnect"); }
        if (onlineWork is not null || lobby is not null) b.Disabled.Add("ServerAddress");
        b.Properties["OnlineConnect"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
        return true;
    }

    void StartOnlineWork(Func<Task> work, string status, Action? completed = null, bool blocking = true)
    {
        onlineStatus = status;
        onlineDone = completed;
        onlineWork = Task.Run(work);
        if (blocking) faithfulScreens.Open("online");
    }

    public void PumpOnline()
    {
        if (onlineWork is { IsCompleted: true } task)
        {
            onlineWork = null;
            var done = onlineDone; onlineDone = null;
            try
            {
                task.GetAwaiter().GetResult();
                if (!onlineCancelling)
                {
                    faithfulScreens.Close("online");
                    done?.Invoke();
                    if (lobby?.Ready == true) { ReadyOnline = lobby.TakeConnection(); CancelOnline(); }
                }
            }
            catch (Exception ex)
            {
                if (!onlineCancelling)
                {
                    Console.Error.WriteLine($"online    {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    onlineStatus = ex.Message;
                    // Reconnect recovers the durable request if a commit's outcome was lost.
                    onlineCancelling = true;
                    faithfulScreens.Open("online");
                }
            }
        }
        if (onlineCancelling && onlineWork is null)
        {
            lobby?.DisposeAsync().AsTask().GetAwaiter().GetResult(); lobby = null;
            onlineCancelling = false;
            if (onlineMembers.Count > 0) CloseEditor();
            onlineMembers.Clear();
            starts = gameData is null ? [] : NewGameStart.LoadAll(gameData);
            startIndex = 0;
            faithfulScreens.Close("new-game");
        }
        if (onlineWork is null && OnlineEditor && lobby?.Request is { } request &&
            System.Diagnostics.Stopwatch.GetElapsedTime(lastOnlinePreview).TotalSeconds > .2)
        {
            var choices = OnlineChoices();
            if (choices.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 30 || c.Name.Any(char.IsControl))) return;
            string signature = JsonSerializer.Serialize(choices);
            if (signature != sentAppearance)
            {
                sentAppearance = signature;
                lastOnlinePreview = System.Diagnostics.Stopwatch.GetTimestamp();
                StartOnlineWork(() => lobby.PreviewAsync(request with { Choices = choices }), "Updating appearance…", blocking: false);
            }
        }
    }

    void CancelOnline()
    {
        lobby?.Cancel();
        onlineCancelling = true;
        faithfulScreens.Close("online");
        faithfulScreens.Close("new-game");
        if (OnlineEditor) CloseEditor();
    }

    public void DisposeOnline()
    {
        lobby?.Cancel();
        try { onlineWork?.GetAwaiter().GetResult(); } catch (Exception) { /* cancellation or already presented network failure */ }
        lobby?.DisposeAsync().AsTask().GetAwaiter().GetResult(); lobby = null;
    }

    void BeginOnlineStart()
    {
        if (lobby?.Catalog is not { } catalog || onlineWork is not null) return;
        var grant = catalog.Qualification.Grant;
        var request = new PlayerCreationRequest(Guid.NewGuid(), grant.Id, grant.Revision, starts[startIndex].Id);
        StartOnlineWork(() => lobby.PreviewAsync(request), "Preparing the selected beginning…", OpenOnlineEditor);
    }

    void OpenOnlineEditor()
    {
        if (lobby?.Preview is not { } preview || EditorData is not { } data) return;
        onlineMembers.Clear();
        var drafts = new Dictionary<CharacterId, CharacterDraft>();
        foreach (var member in preview.Members.Where(m => !m.Animal && m.Editor is not null))
        {
            var view = member.Editor!;
            var record = new FcsRecord { Type = (int)FcsRecordType.CHARACTER_APPEARANCE };
            record.References["race"] = [new(view.Race, 0, 0, 0)];
            record.Bools["sex female"] = view.Female;
            var choice = new CreationAppearanceChoice(view.Race, view.Female,
                view.Controls.Select(c => new CreationSlider(c.Tab, c.Kind, c.Label, c.Value)).ToArray(),
                view.Pickers.Select(p => new CreationPicker(p.Kind, p.Selected)).ToArray());
            record = CreationAppearanceEditing.Apply(data.Db, data.InstallRoot, record, choice, 1);
            var id = new CharacterId(member.Member, int.MaxValue);
            onlineMembers[id] = member.Member;
            drafts[id] = new(data.Db, data.InstallRoot, record, member.Name);
        }
        if (drafts.Count == 0)
        {
            Confirm("Create characters", $"Create {preview.Members.Length} character(s) and enter the server?", CommitOnlineEditor);
            return;
        }
        editor = new(EditKind.Creation, drafts.Keys.ToArray(), new HashSet<CharacterId>(), CharacterId.None);
        foreach (var (id, draft) in drafts) { editor.Drafts[id] = draft; editor.StartNames[id] = draft.Name; }
        sentAppearance = JsonSerializer.Serialize(OnlineChoices());
        previews.Clear();
        faithfulScreens.Close("new-game");
        faithfulScreens.Open("editor");
        ShowEditorCharacter();
        UpdateMenuPause();
    }

    CreationMemberChoice[] OnlineChoices() => editor is null ? [] : editor.Drafts.Select(pair =>
    {
        var draft = pair.Value;
        var controls = Enum.GetValues<EditorTab>().SelectMany(tab => draft.Controls(tab).Where(c => !c.IsPicker)
            .Select(c => new CreationSlider(tab, c.Kind, c.Label, draft.Value(c)))).ToArray();
        var pickers = Enum.GetValues<EditorTab>().SelectMany(draft.Controls).Where(c => c.IsPicker).Select(c => c.Kind).Distinct()
            .Select(kind => new CreationPicker(kind, draft.Options(kind).ElementAtOrDefault(draft.OptionIndex(kind))?.StringId)).ToArray();
        return new CreationMemberChoice(onlineMembers[pair.Key], draft.Name, new(draft.Race.StringId, draft.Female, controls, pickers));
    }).ToArray();

    void CommitOnlineEditor()
    {
        if (onlineWork is not null || lobby?.Request is not { } request) return;
        var choices = OnlineChoices();
        if (choices.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 30 || c.Name.Any(char.IsControl)))
        { MessageBox("Character name", "Names must contain 1–30 characters."); return; }
        var final = request with { Choices = choices };
        StartOnlineWork(async () => { await lobby.PreviewAsync(final).ConfigureAwait(false); await lobby.CommitAsync(final).ConfigureAwait(false); },
            "Creating characters and preparing entry…");
    }

    CharacterAppearance? onlineDressed, onlineUndressed;
    public CharacterAppearance? OnlineEditorAppearance
    {
        get
        {
            if (!OnlineEditor || editor is not { } edit || lobby?.Preview is not { } preview) return null;
            var own = preview.Members[onlineMembers[edit.Current]].Appearance;
            if (own is null || edit.Clothes || gameData is null) return own;
            if (!ReferenceEquals(own, onlineDressed))
            {
                onlineDressed = own;
                var display = own.CaptureDisplay(gameData);
                onlineUndressed = CharacterAppearance.FromDisplay(display with
                {
                    Parts = display.Parts.Where(p => p.Record.Type is not (FcsRecordType.ARMOUR or FcsRecordType.CONTAINER)).ToArray(),
                    BodyLayers = [], HiddenParts = 0,
                });
            }
            return onlineUndressed;
        }
    }
}
