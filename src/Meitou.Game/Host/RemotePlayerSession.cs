using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Simulation.Movement;
using System.Numerics;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Simulation.Session;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Game.Host;
public sealed record RemoteCommandCompletion(ulong Request, AuthorityReceipt? Receipt, Exception? Error);
/// <summary>
/// Main-thread presentation/command adapter for an authenticated connection. Owns only camera, UI selection,
/// display clock and received snapshots; never constructs or steps a World. Dispose owns the connection.
/// </summary>
public sealed partial class RemotePlayerSession : IClientSession, IAsyncDisposable
{
    readonly RemoteGameSession _connection;
    readonly List<CharacterId> _selection = [];
    readonly List<(ulong Request, Task<CommandReceiptMessage> Reply)> _pending = [];
    readonly Queue<Floater> _floaters = [];
    readonly Queue<SoundEvent> _sounds = [];
    readonly Queue<SaidLine> _publicSpeech = [];
    ulong _soundFloor, _floaterFloor;
    bool _soundsEnabled;
    ulong _eventHighWater;
    bool _eventBaseline, _floatersEnabled = true;
    PlayerWorldView? _view;
    RemotePresentationFrame? _presentationFrame;
    int? _chosenFaction;
    ulong _requestCounter;
    bool _disposed;
    Task? _disposal;
    double _elapsed, _lastGameHours;
    /// <summary>
    /// Smoothed drawn-frame duration for the same camera response as a local session.
    /// </summary>
    double _frameSeconds;
    bool _clockReady;
    long _receivedVersion = -1;
    CharacterId _pointed = CharacterId.None;
    public RemotePlayerSession(RemoteGameSession connection, Func<float, float, float> ground, InputBindings? bindings = null, GameClock? clock = null, double tickRate = FixedStepClock.DefaultTickRate)
    {
        this._connection = connection;
        connection.RegisterPresentationEventConsumer();
        Bindings = bindings ?? new();
        Clock = clock ?? new();
        Ticks = new(tickRate);
        Camera = new((x, z) => MathF.Max(ground(x, z), Meitou.Data.World.WorldWater.Height));
        _requestCounter = connection.State.Admission.State.CommandHighWater;
    }

    public RemoteGameSession Connection => _connection;

    /// <summary>
    /// Called only after submitting a visible world root to the renderer's draw list.
    /// </summary>
    public void TraceRendererRootSubmission(CharacterId character, Vector3 root, long frame)
    {
        if (_view is not null && _presentationFrame is { } sampled)
            _connection.TraceRendererRootSubmission(sampled, character, root, frame);
    }

    public object WorldIdentity { get; } = new();
    public bool HasWorldView => !_disposed && _view is not null && _connection.State.View is not null;
    public GameplayRules? AuthorityRules => HasWorldView ? _view?.AuthorityRules : null;
    public PlayerMessageBatch Messages => _view?.Messages ?? PlayerMessageBatch.Empty;
    public Meitou.Data.World.WeatherFrame? Weather => HasWorldView ? _view?.Weather : null;
    public Meitou.Data.Particles.WeatherEffectFrame? WeatherEffects => HasWorldView ? _view?.WeatherEffects ?? Meitou.Data.Particles.WeatherEffectFrame.Empty : Meitou.Data.Particles.WeatherEffectFrame.Empty;

    public bool TryDequeuePublicSpeech(out SaidLine line) => _publicSpeech.TryDequeue(out line);
    public WorldSnapshot CurrentSnapshot { get; private set; } = WorldSnapshot.Empty;
    public WorldSnapshot PreviousSnapshot { get; private set; } = WorldSnapshot.Empty;
    public float SimulationAlpha => 1;
    public double PresentationTick => _presentationFrame?.Tick ?? 0;
    public double PresentationSeconds => Math.Max(0, Clock.GameSeconds - Math.Max(0, CurrentSnapshot.Tick - PresentationTick) / SimulationClock.TickRate);
    public long CommandTick => Math.Max(0, CurrentSnapshot.Tick);
    public float SimulationStepSeconds => _view?.SimulationStepSeconds ?? 1f / 30;
    public CameraRig Camera { get; }
    public InputState Input { get; } = new();
    public InputBindings Bindings { get; }
    public GameClock Clock { get; }
    public FixedStepClock Ticks { get; }
    public float Alpha => Ticks.Alpha;

    public CameraState CameraAt() => Camera.At(Alpha);
    public int ActiveFaction => _chosenFaction ?? _view?.ActiveFaction ?? -1;
    public IReadOnlyList<FactionIdentityView> FactionCatalogue => _view?.FactionCatalogue ?? [];

    public FactionIdentityView? DescribeFaction(int faction) => FactionCatalogue.FirstOrDefault(f => f.Faction == faction);
    public FactionIdentityView? DescribeFaction(string recordId) => FactionCatalogue.FirstOrDefault(f => f.RecordId == recordId);
    public FactionStandingView? StandingTowards(int faction) => Faction?.Standings.FirstOrDefault(f => f.Faction == faction);
    public (IReadOnlyList<FactionInfoLine> Contracts, IReadOnlyList<FactionInfoLine> Events) FactionInformation => (Faction?.Contracts.Select(c => c.ToLine()).ToArray() ?? [], Faction?.UpcomingEvents.Select(e => e.ToLine()).ToArray() ?? []);

    PlayerFactionView? Faction => _view?.Factions.FirstOrDefault(f => f.Faction == ActiveFaction);
    public long? Money => Faction?.Money;
    public bool HasPlayer => _connection.State.Admission.State.Roles.Length != 0;
    public IReadOnlyList<string> CreationRaces => []; // remote creation uses the server catalogue; the legacy editor is not wired
    public int Formation => Faction?.Formation ?? (int)SquadFormation.Random;

    public VerbFacts? VerbFactsOf(CharacterId lead, CharacterId target) => HasWorldView && _view?.Snapshot.VerbLead == lead ? _view.Snapshot.Verbs.FirstOrDefault(v => v.Target == target)?.Facts : null;
    public bool RegardsFriendly(CharacterId target, CharacterId lead) => VerbFactsOf(lead, target)?.RegardsLead == true;
    public bool? ArrivalForget(int serial) => _view?.Snapshot.ArrivalSerial == serial ? _view.Snapshot.ArrivalForget : null;
    public void SetPointed(CharacterId target)
    {
        if (!CanControl)
        {
            _pointed = CharacterId.None;
            return;
        }

        if (target == _pointed)
            return;
        _pointed = target;
        Submit(new PointedCommand(target) { Tick = CommandTick });
    }

    public bool IsRemote => true;
    public bool CanSaveWorld => false;
    public bool CanChangeSpeed => false;
    public double TimeScale => _view?.TimeScale ?? 0;
    public bool IsPaused => TimeScale == 0;
    public bool CanControl => !_disposed && _view is not null && _connection.State.CanControl;
    public int PendingCommands => _pending.Count;
    public RemoteCommandCompletion? LastCompletion { get; private set; }

    /// <summary>
    /// Delivered only on the UI thread, by Submit or Advance.
    /// </summary>
    public event Action<RemoteCommandCompletion>? CommandCompleted;
    public event Action<ActionState>? Ticked;
    public void SetSpeed(double speed) => throw new InvalidOperationException("World speed is controlled by the server.");
    public void TogglePause() => throw new InvalidOperationException("World pause is controlled by the server.");
    public void SetModalPause(bool paused)
    {
    } // a client menu never pauses other players

    public bool WantsToTalk(CharacterId target) => _view?.Snapshot.Characters.Any(c => c.Id == target && c.WantsToTalk) == true;
    public ProspectReport? Prospected => _view?.Prospected;

    public void EnableFloaters(bool enabled)
    {
        if (enabled && !_floatersEnabled)
            _floaterFloor = _connection.State.View?.Events.HighWater ?? _eventHighWater;
        _floatersEnabled = enabled;
        if (!enabled)
            _floaters.Clear();
    }

    public bool TryDequeueFloater(out Floater floater) => _floaters.TryDequeue(out floater);
    public void EnableSounds(bool enabled)
    {
        if (enabled && !_soundsEnabled)
            _soundFloor = _connection.State.View?.Events.HighWater ?? _eventHighWater;
        _soundsEnabled = enabled;
        if (!enabled)
            _sounds.Clear();
    }

    public bool TryDequeueSound(out SoundEvent sound) => _sounds.TryDequeue(out sound);
    public void SelectFaction(int faction)
    {
        if (_view?.Factions.Any(f => f.Faction == faction) != true)
            throw new InvalidOperationException("Faction is outside this player's memberships.");
        _chosenFaction = faction;
        RefreshPresentation();
    }

    public void Submit(SimCommand intent)
    {
        ulong request = 0;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CanControl)
                throw new InvalidOperationException("No active remote world is ready for commands.");
            if (_pending.Count >= 64)
                throw new InvalidOperationException("Too many unconfirmed commands.");
            var bound = ClientCommandBinding.Bind(_view!, intent, _selection, ActiveFaction);
            _requestCounter = Math.Max(_requestCounter, _connection.State.Admission.State.CommandHighWater);
            request = _requestCounter = checked(_requestCounter + 1);
            var reply = _connection.CommandAsync(request, bound.Command, bound.Expected);
            _pending.Add((request, reply));
            if (bound.Command is SelectCommand select)
            {
                if (select.Characters.Count > 0)
                {
                    bool toggled = select.Toggle && select.Characters.Count == 1 && _selection.Contains(select.Characters[0]);
                    if (toggled)
                    {
                        if (_selection.Count >= 2)
                            _selection.Remove(select.Characters[0]);
                    }
                    else
                    {
                        if (!select.Additive)
                            _selection.Clear();
                        foreach (var id in select.Characters)
                            if (!_selection.Contains(id))
                                _selection.Add(id);
                    }
                }

                if (_selection.Count > 0 && _view!.Roles.FirstOrDefault(r => r.Character == _selection[0])is { } role)
                    _chosenFaction = role.Faction;
                RefreshPresentation();
            }
        }
        catch (Exception error)when (error is InvalidOperationException or InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            Complete(new(request, null, error));
        }
    }

    public int Advance(double realSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(realSeconds) || realSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(realSeconds));
        _frameSeconds = _frameSeconds == 0 ? realSeconds : _frameSeconds + (realSeconds - _frameSeconds) * 0.1;
        Camera.Strategy.DrawnFrameSeconds = (float)_frameSeconds;

        _elapsed += realSeconds;
        _connection.AdvancePresentation(realSeconds);
        PumpTalkHints();
        _presentationFrame = _connection.CapturePresentation();
        var state = _presentationFrame.State;
        if (state.View is not { } next)
        {
            _view = null;
            _selection.Clear();
            _chosenFaction = null;
            ClearProjectionRevisions();
            // Clear withdrawn payloads, but keep the same-session dedup cursor across view replacement.
            _floaters.Clear();
            _sounds.Clear();
            _publicSpeech.Clear();
            ClearCombatVisuals();
            PreviousSnapshot = CurrentSnapshot = WorldSnapshot.Empty;
        }
        else if (_receivedVersion != state.Version)
        {
            bool first = _view is null;
            _view = next;
            var presentationEvents = _connection.ReadPresentationEvents();
            if (_eventBaseline && _floatersEnabled)
                foreach (var item in presentationEvents.Floaters.Where(e => e.Id > _eventHighWater && e.Id > _floaterFloor).OrderBy(e => e.Id))
                {
                    if (_floaters.Count == 128)
                        _floaters.Dequeue();
                    _floaters.Enqueue(item.Value);
                }

            if (_eventBaseline && _soundsEnabled)
                foreach (var item in presentationEvents.Sounds.Where(e => e.Id > _eventHighWater && e.Id > _soundFloor).OrderBy(e => e.Id))
                {
                    if (_sounds.Count == 128)
                        _sounds.Dequeue();
                    _sounds.Enqueue(item.Value);
                }

            if (_eventBaseline)
                foreach (var item in presentationEvents.Speech.Where(e => e.Id > _eventHighWater).OrderBy(e => e.Id))
                {
                    if (_publicSpeech.Count == 128)
                        _publicSpeech.Dequeue();
                    _publicSpeech.Enqueue(item.Line);
                }

            ReceiveCombatVisuals(presentationEvents);
            _eventHighWater = Math.Max(_eventHighWater, presentationEvents.HighWater);
            _eventBaseline = true;
            _connection.AcknowledgePresentationEvents(_eventHighWater);
            if (_chosenFaction is { } faction && !next.Factions.Any(f => f.Faction == faction))
                _chosenFaction = null;
            _selection.RemoveAll(id => !next.Roles.Any(r => r.Character == id && r.Available && !r.Dead));
            if (first)
                _selection.AddRange(next.Snapshot.Characters.Where(c => c.Selected && next.Roles.Any(r => r.Character == c.Id && r.Available)).Select(c => c.Id));
            RefreshPresentation();
            if (!_clockReady || next.GameHours < _lastGameHours)
            {
                double day = Math.Floor(next.GameHours / 24);
                Clock.SetTime((long)day, next.GameHours - day * 24);
            }
            else
                Clock.Advance((next.GameHours - _lastGameHours) * GameClock.SecondsPerGameHour);
            _clockReady = true;
            _lastGameHours = next.GameHours;
        }

        _receivedVersion = state.Version;
        if (_view is not null)
            RefreshPresentation();
        PreviousSnapshot = CurrentSnapshot;
        foreach (var item in _pending.Where(p => p.Reply.IsCompleted).ToArray())
        {
            _pending.Remove(item);
            RemoteCommandCompletion completion;
            try
            {
                completion = new(item.Request, item.Reply.GetAwaiter().GetResult().Receipt, null);
            }
            catch (Exception error)
            {
                completion = new(item.Request, null, error);
            }

            Complete(completion);
        }

        int ticks = Ticks.Advance(realSeconds);
        for (int i = 0; i < ticks; i++)
            Tick();
        return ticks;
    }

    public ActionState Tick()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var actions = Bindings.Resolve(Input.Consume());
        Camera.Update((float)Ticks.TickSeconds, actions);
        Ticked?.Invoke(actions);
        return actions;
    }

    void RefreshPresentation()
    {
        if (_view is null)
            return;
        var faction = Faction;
        var presented = _presentationFrame?.Snapshot ?? _view.Snapshot;
        var (opener, session) = PresentedContainer(presented);
        presented = FilterContainerWindow(presented, opener, session);
        PreviousSnapshot = FilterContainerWindow(PreviousSnapshot, opener, session);
        CurrentSnapshot = ProjectRevisions(presented)with
        {
            Characters = presented.Characters.Select(c => c with { Selected = _selection.Contains(c.Id) }).ToArray(),
            Research = faction?.Research ?? ResearchSnapshot.Empty,
            UnlockedBuildings = faction?.UnlockedBuildings ?? new HashSet<string>(),
            PlayerSquads = faction?.Squads ?? [],
            ActiveSquad = faction?.Squads.Any(s => s.Id == _view.Snapshot.ActiveSquad) == true ? _view.Snapshot.ActiveSquad : faction?.Squads.FirstOrDefault()?.Id ?? -1,
        };
    }

    /// <summary>
    /// One current presentation supplies the visible actor and private session used by both displayed snapshots.
    /// </summary>
    (CharacterId Opener, ulong Session) PresentedContainer(WorldSnapshot snapshot)
    {
        var opener = snapshot.ContainerOpener;
        if (!HasWorldView || !CanControl || opener.IsNone || !snapshot.Characters.Any(c => c.Id == opener)
            || _view?.Roles.Any(r => r.Character == opener && r.Available && !r.Dead) != true)
            return (CharacterId.None, 0);
        var container = snapshot.Machines.FirstOrDefault(m => m.IsContainer && m.ContainerOpener == opener && m.ContainerSessionId != 0);
        return container is null ? (CharacterId.None, 0) : (opener, container.ContainerSessionId);
    }

    /// <summary>
    /// Masks a retired or independently delayed window without changing raw component values or their revision fences.
    /// </summary>
    static WorldSnapshot FilterContainerWindow(WorldSnapshot snapshot, CharacterId opener, ulong session)
    {
        bool current = !opener.IsNone && session != 0 && snapshot.ContainerOpener == opener
            && snapshot.Characters.Any(c => c.Id == opener)
            && snapshot.Machines.Any(m => m.IsContainer && m.ContainerOpener == opener && m.ContainerSessionId == session);
        bool Retain(MachineSnapshot machine) => !machine.IsContainer
            || current && machine.ContainerOpener == opener && machine.ContainerSessionId == session;
        var visibleOpener = current ? opener : CharacterId.None;
        if (snapshot.ContainerOpener == visibleOpener && snapshot.Machines.All(Retain)) return snapshot;
        return snapshot with { ContainerOpener = visibleOpener, Machines = snapshot.Machines.Where(Retain).ToArray() };
    }

    void Complete(RemoteCommandCompletion completion)
    {
        LastCompletion = completion;
        CommandCompleted?.Invoke(completion);
    }

    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());
    async Task DisposeCoreAsync()
    {
        _disposed = true;
        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await Task.WhenAll(_pending.Select(p => (Task)p.Reply).Concat(_talkHints.Values.Select(h => (Task)h.Pending))).ConfigureAwait(false);
            }
            catch
            { /* Interrupted requests remain unknown; the connection reports the failure. */
            }

            _pending.Clear();
            _selection.Clear();
            _view = null;
            ClearProjectionRevisions();
            _talkHints.Clear();
            _floaters.Clear();
            _sounds.Clear();
            _publicSpeech.Clear();
            ClearCombatVisuals();
            PreviousSnapshot = CurrentSnapshot = WorldSnapshot.Empty;
        }
    }
}
