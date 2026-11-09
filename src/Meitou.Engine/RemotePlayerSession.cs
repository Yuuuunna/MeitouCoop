using System.Numerics;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Engine;

public sealed record RemoteCommandCompletion(ulong Request, AuthorityReceipt? Receipt, Exception? Error);

/// <summary>
/// Main-thread presentation/command adapter for an authenticated connection. Owns only camera, UI selection,
/// display clock and received snapshots; never constructs or steps a World. Dispose owns the connection.
/// </summary>
public sealed partial class RemotePlayerSession : IClientSession, IAsyncDisposable
{
    readonly RemoteGameSession connection;
    readonly List<CharacterId> selection = [];
    readonly List<(ulong Request, Task<CommandReceiptMessage> Reply)> pending = [];
    readonly Queue<Floater> floaters = [];
    readonly Queue<SoundEvent> sounds = [];
    readonly Queue<SaidLine> publicSpeech = [];
    ulong soundFloor, floaterFloor;
    bool soundsEnabled;
    ulong eventHighWater;
    bool eventBaseline, floatersEnabled = true;
    PlayerWorldView? view;
    int? chosenFaction;
    ulong requestCounter;
    bool disposed;
    Task? disposal;
    double elapsed, blendSeconds = .2, blendAge, blendStartTick;
    double lastGameHours;
    bool clockReady;

    public RemotePlayerSession(RemoteGameSession connection, Func<float, float, float> ground, InputBindings? bindings = null,
        GameClock? clock = null, double tickRate = FixedStepClock.DefaultTickRate)
    {
        this.connection = connection;
        Bindings = bindings ?? new();
        Clock = clock ?? new(); Ticks = new(tickRate);
        Camera = new((x, z) => MathF.Max(ground(x, z), Meitou.Data.World.WorldWater.Height));
        requestCounter = connection.State.Admission.State.CommandHighWater;
    }

    public RemoteGameSession Connection => connection;
    public object WorldIdentity { get; } = new();
    public bool HasWorldView => !disposed && view is not null && connection.State.View is not null;
    public GameplayRules? AuthorityRules => HasWorldView ? view?.AuthorityRules : null;
    public PlayerMessageBatch Messages => view?.Messages ?? PlayerMessageBatch.Empty;
    public Meitou.Data.World.WeatherFrame? Weather => HasWorldView ? view?.Weather : null;
    public Meitou.Data.Particles.WeatherEffectFrame? WeatherEffects => HasWorldView ? view?.WeatherEffects ?? Meitou.Data.Particles.WeatherEffectFrame.Empty : Meitou.Data.Particles.WeatherEffectFrame.Empty;
    public bool TryDequeuePublicSpeech(out SaidLine line) => publicSpeech.TryDequeue(out line);
    public WorldSnapshot CurrentSnapshot { get; private set; } = WorldSnapshot.Empty;
    public WorldSnapshot PreviousSnapshot { get; private set; } = WorldSnapshot.Empty;
    public float SimulationAlpha => (float)Math.Clamp(blendAge / blendSeconds, 0, 1);
    public double PresentationTick => CurrentSnapshot.Tick < 0 ? 0
        : blendStartTick + (CurrentSnapshot.Tick - blendStartTick) * SimulationAlpha;
    public double PresentationSeconds => Math.Max(0, Clock.GameSeconds - Math.Max(0, CurrentSnapshot.Tick - PresentationTick) / SimulationClock.TickRate);
    public long CommandTick => Math.Max(0, CurrentSnapshot.Tick);
    public float SimulationStepSeconds => view?.SimulationStepSeconds ?? 1f / 30;
    public CameraRig Camera { get; }
    public InputState Input { get; } = new();
    public InputBindings Bindings { get; }
    public GameClock Clock { get; }
    public FixedStepClock Ticks { get; }
    public float Alpha => Ticks.Alpha;
    public CameraState CameraAt() => Camera.At(Alpha);
    public int ActiveFaction => chosenFaction ?? view?.ActiveFaction ?? -1;
    public IReadOnlyList<FactionIdentityView> FactionCatalogue => view?.FactionCatalogue ?? [];
    public FactionIdentityView? DescribeFaction(int faction) => FactionCatalogue.FirstOrDefault(f => f.Faction == faction);
    public FactionIdentityView? DescribeFaction(string recordId) => FactionCatalogue.FirstOrDefault(f => f.RecordId == recordId);
    public FactionStandingView? StandingTowards(int faction) => Faction?.Standings.FirstOrDefault(f => f.Faction == faction);
    public (IReadOnlyList<FactionInfoLine> Contracts, IReadOnlyList<FactionInfoLine> Events) FactionInformation
        => (Faction?.Contracts.Select(c => c.ToLine()).ToArray() ?? [], Faction?.UpcomingEvents.Select(e => e.ToLine()).ToArray() ?? []);
    PlayerFactionView? Faction => view?.Factions.FirstOrDefault(f => f.Faction == ActiveFaction);
    public long? Money => Faction?.Money;
    public bool HasPlayer => connection.State.Admission.State.Roles.Length != 0;
    public IReadOnlyList<string> CreationRaces => []; // remote creation uses the server catalogue; the legacy editor is not wired
    public int Formation => Faction?.Formation ?? (int)SquadFormation.Random;
    public VerbFacts? VerbFactsOf(CharacterId lead, CharacterId target) => HasWorldView && view?.Snapshot.VerbLead == lead
        ? view.Snapshot.Verbs.FirstOrDefault(v => v.Target == target)?.Facts : null;
    public bool RegardsFriendly(CharacterId target, CharacterId lead) => VerbFactsOf(lead, target)?.RegardsLead == true;
    public bool? ArrivalForget(int serial) => view?.Snapshot.ArrivalSerial == serial ? view.Snapshot.ArrivalForget : null;
    CharacterId pointed = CharacterId.None;
    public void SetPointed(CharacterId target)
    {
        if (!CanControl) { pointed = CharacterId.None; return; }
        if (target == pointed) return;
        pointed = target;
        Submit(new PointedCommand(target) { Tick = CommandTick });
    }
    public bool IsRemote => true;
    public bool CanSaveWorld => false;
    public bool CanChangeSpeed => false;
    public double TimeScale => view?.TimeScale ?? 0;
    public bool IsPaused => TimeScale == 0;
    public bool CanControl => !disposed && view is not null && connection.State.CanControl;
    public int PendingCommands => pending.Count;
    public RemoteCommandCompletion? LastCompletion { get; private set; }
    /// <summary>Delivered only on the UI thread, by Submit or Advance.</summary>
    public event Action<RemoteCommandCompletion>? CommandCompleted;
    public event Action<ActionState>? Ticked;
    public void SetSpeed(double speed) => throw new InvalidOperationException("World speed is controlled by the server.");
    public void TogglePause() => throw new InvalidOperationException("World pause is controlled by the server.");
    public void SetModalPause(bool paused) { } // a client menu never pauses other players

    public bool WantsToTalk(CharacterId target) => view?.Snapshot.Characters.Any(c => c.Id == target && c.WantsToTalk) == true;
    public ProspectReport? Prospected => view?.Prospected;
    public void EnableFloaters(bool enabled)
    {
        if (enabled && !floatersEnabled) floaterFloor = connection.State.View?.Events.HighWater ?? eventHighWater;
        floatersEnabled = enabled; if (!enabled) floaters.Clear();
    }
    public bool TryDequeueFloater(out Floater floater) => floaters.TryDequeue(out floater);
    public void EnableSounds(bool enabled)
    {
        if (enabled && !soundsEnabled) soundFloor = connection.State.View?.Events.HighWater ?? eventHighWater;
        soundsEnabled = enabled; if (!enabled) sounds.Clear();
    }
    public bool TryDequeueSound(out SoundEvent sound) => sounds.TryDequeue(out sound);

    public void SelectFaction(int faction)
    {
        if (view?.Factions.Any(f => f.Faction == faction) != true) throw new InvalidOperationException("Faction is outside this player's memberships.");
        chosenFaction = faction;
        RefreshPresentation();
    }

    public void Submit(SimCommand intent)
    {
        ulong request = 0;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!CanControl) throw new InvalidOperationException("No active remote world is ready for commands.");
            if (pending.Count >= 64) throw new InvalidOperationException("Too many unconfirmed commands.");
            var bound = ClientCommandBinding.Bind(view!, intent, selection, ActiveFaction);
            requestCounter = Math.Max(requestCounter, connection.State.Admission.State.CommandHighWater);
            request = requestCounter = checked(requestCounter + 1);
            var reply = connection.CommandAsync(request, bound.Command, bound.Expected);
            pending.Add((request, reply));
            if (bound.Command is SelectCommand select)
            {
                if (select.Characters.Count > 0)
                {
                    bool toggled = select.Toggle && select.Characters.Count == 1 && selection.Contains(select.Characters[0]);
                    if (toggled)
                    {
                        if (selection.Count >= 2) selection.Remove(select.Characters[0]);
                    }
                    else
                    {
                        if (!select.Additive) selection.Clear();
                        foreach (var id in select.Characters) if (!selection.Contains(id)) selection.Add(id);
                    }
                }
                if (selection.Count > 0 && view!.Roles.FirstOrDefault(r => r.Character == selection[0]) is { } role)
                    chosenFaction = role.Faction;
                RefreshPresentation();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or ArgumentException or OverflowException)
        { Complete(new(request, null, error)); }
    }

    public int Advance(double realSeconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(realSeconds) || realSeconds < 0) throw new ArgumentOutOfRangeException(nameof(realSeconds));
        elapsed += realSeconds; blendAge += realSeconds;
        PumpTalkHints();
        var state = connection.State;
        if (state.View is not { } next)
        {
            view = null; selection.Clear(); chosenFaction = null;
            ClearProjectionRevisions();
            floaters.Clear(); sounds.Clear(); publicSpeech.Clear(); ClearCombatVisuals(); eventBaseline = false;
            PreviousSnapshot = CurrentSnapshot = WorldSnapshot.Empty;
        }
        else if (view?.Publication != next.Publication)
        {
            var previous = CurrentSnapshot;
            var previousFrom = PreviousSnapshot;
            float previousAlpha = SimulationAlpha;
            double displayedTick = PresentationTick;
            bool first = view is null;
            bool sameTick = !first && next.Snapshot.Tick == previous.Tick;
            view = next;
            if (eventBaseline && floatersEnabled)
                foreach (var item in next.Events.Floaters.Where(e => e.Id > eventHighWater && e.Id > floaterFloor).OrderBy(e => e.Id))
                {
                    if (floaters.Count == 128) floaters.Dequeue();
                    floaters.Enqueue(item.Value);
                }
            if (eventBaseline && soundsEnabled)
                foreach (var item in next.Events.Sounds.Where(e => e.Id > eventHighWater && e.Id > soundFloor).OrderBy(e => e.Id))
                {
                    if (sounds.Count == 128) sounds.Dequeue();
                    sounds.Enqueue(item.Value);
                }
            if (eventBaseline)
                foreach (var item in next.Events.Speech.Where(e => e.Id > eventHighWater).OrderBy(e => e.Id))
                {
                    if (publicSpeech.Count == 128) publicSpeech.Dequeue();
                    publicSpeech.Enqueue(item.Line);
                }
            ReceiveCombatVisuals(next.Events);
            eventHighWater = Math.Max(eventHighWater, next.Events.HighWater); eventBaseline = true;
            if (chosenFaction is { } faction && !next.Factions.Any(f => f.Faction == faction)) chosenFaction = null;
            selection.RemoveAll(id => !next.Roles.Any(r => r.Character == id && r.Available && !r.Dead));
            if (first) selection.AddRange(next.Snapshot.Characters.Where(c => c.Selected && next.Roles.Any(r => r.Character == c.Id && r.Available)).Select(c => c.Id));
            RefreshPresentation();
            // Only retain old poses for still-visible entities. Previous frames never retain revoked private details.
            var old = previous.Characters.ToDictionary(c => c.Id);
            var oldFrom = previousFrom.Characters.ToDictionary(c => c.Id);
            blendStartTick = first ? CurrentSnapshot.Tick : sameTick ? blendStartTick : Math.Min(displayedTick, CurrentSnapshot.Tick);
            float startAlpha = sameTick ? 0 : previousAlpha;
            PreviousSnapshot = CurrentSnapshot with { Tick = (long)blendStartTick, Characters = CurrentSnapshot.Characters.Select(c =>
            {
                if (!old.TryGetValue(c.Id, out var before)) return c;
                var from = oldFrom.GetValueOrDefault(c.Id, before);
                // A packet may arrive before the previous blend finishes. Start at the pose actually
                // being displayed, not its unreached endpoint, or bursty delivery makes actors jump.
                return c with { Position = Vector3.Lerp(from.Position, before.Position, startAlpha),
                    Yaw = Interp.LerpAngle(from.Yaw, before.Yaw, startAlpha),
                    DrawYaw = Interp.LerpAngle(float.IsNaN(from.DrawYaw) ? from.Yaw : from.DrawYaw,
                        float.IsNaN(before.DrawYaw) ? before.Yaw : before.DrawYaw, startAlpha),
                    Animations = sameTick ? from.Animations : before.Animations,
                    Carry = sameTick ? from.Carry : before.Carry, Ragdoll = sameTick ? from.Ragdoll : before.Ragdoll };
            }).ToArray() };
            if (!sameTick)
            {
                // Keep roughly two ordinary publications of visual headroom. Use simulation
                // distance, not the previous packet's arrival gap, to avoid alternating fast
                // catch-up and a stationary endpoint when delivery is uneven. Never extrapolate.
                double remaining = Math.Max(0, CurrentSnapshot.Tick - blendStartTick) * SimulationStepSeconds;
                blendSeconds = Math.Clamp(Math.Max(.2, remaining / (TimeScale > 0 ? TimeScale : 1)), .2, .5);
                blendAge = first || previous.Tick > CurrentSnapshot.Tick ? blendSeconds : 0;
            }
            if (!clockReady || next.GameHours < lastGameHours)
            {
                double day = Math.Floor(next.GameHours / 24);
                Clock.SetTime((long)day, next.GameHours - day * 24);
            }
            else Clock.Advance((next.GameHours - lastGameHours) * GameClock.SecondsPerGameHour);
            clockReady = true; lastGameHours = next.GameHours;
        }
        foreach (var item in pending.Where(p => p.Reply.IsCompleted).ToArray())
        {
            pending.Remove(item);
            RemoteCommandCompletion completion;
            try { completion = new(item.Request, item.Reply.GetAwaiter().GetResult().Receipt, null); }
            catch (Exception error) { completion = new(item.Request, null, error); }
            Complete(completion);
        }
        int ticks = Ticks.Advance(realSeconds);
        for (int i = 0; i < ticks; i++) Tick();
        return ticks;
    }

    public ActionState Tick()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var actions = Bindings.Resolve(Input.Consume());
        Camera.Update((float)Ticks.TickSeconds, actions);
        Ticked?.Invoke(actions);
        return actions;
    }

    void RefreshPresentation()
    {
        if (view is null) return;
        var faction = Faction;
        CurrentSnapshot = ProjectRevisions(view.Snapshot) with
        {
            Characters = view.Snapshot.Characters.Select(c => c with { Selected = selection.Contains(c.Id) }).ToArray(),
            Research = faction?.Research ?? ResearchSnapshot.Empty,
            UnlockedBuildings = faction?.UnlockedBuildings ?? new HashSet<string>(),
            PlayerSquads = faction?.Squads ?? [],
            ActiveSquad = faction?.Squads.Any(s => s.Id == view.Snapshot.ActiveSquad) == true ? view.Snapshot.ActiveSquad : faction?.Squads.FirstOrDefault()?.Id ?? -1,
        };
    }

    void Complete(RemoteCommandCompletion completion)
    {
        LastCompletion = completion;
        CommandCompleted?.Invoke(completion);
    }

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

    async Task DisposeCoreAsync()
    {
        disposed = true;
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { await Task.WhenAll(pending.Select(p => (Task)p.Reply).Concat(talkHints.Values.Select(h => (Task)h.Pending))).ConfigureAwait(false); }
            catch { /* Interrupted requests remain unknown; the connection reports the failure. */ }
            pending.Clear(); selection.Clear(); view = null;
            ClearProjectionRevisions();
            talkHints.Clear();
            floaters.Clear(); sounds.Clear(); publicSpeech.Clear(); ClearCombatVisuals();
            PreviousSnapshot = CurrentSnapshot = WorldSnapshot.Empty;
        }
    }
}
