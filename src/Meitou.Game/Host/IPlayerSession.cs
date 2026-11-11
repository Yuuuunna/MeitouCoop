using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Simulation.Movement;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Simulation.Session;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Game.Host;

/// <summary>
/// The player's presentation and command boundary. Implementations may be local or remote; UI code
/// cannot reach a mutable World through this interface. Views are read-only to consumers.
/// </summary>
public interface IPlayerSession
{
    WorldSnapshot CurrentSnapshot { get; }

    bool HasWorldView => true;

    GameplayRules? AuthorityRules => null;

    PlayerMessageBatch Messages => PlayerMessageBatch.Empty;

    Meitou.Data.World.WeatherFrame? Weather => null;

    Meitou.Data.Particles.WeatherEffectFrame? WeatherEffects => null;

    bool TryDequeuePublicSpeech(out SaidLine line)
    {
        line = default;
        return false;
    }

    /// <summary>
    /// Stable reference while displaying the same world; changes when that world is replaced.
    /// </summary>
    object WorldIdentity { get; }

    /// <summary>
    /// Local scheduling hint only; remote authorities assign their own execution tick.
    /// </summary>
    long CommandTick { get; }

    float SimulationStepSeconds => 1f / 30;

    CameraRig Camera { get; }

    InputState Input { get; }

    InputBindings Bindings { get; }

    GameClock Clock { get; }

    int ActiveFaction { get; }

    IReadOnlyList<FactionIdentityView> FactionCatalogue => [];

    FactionIdentityView? DescribeFaction(int faction) => FactionCatalogue.FirstOrDefault(f => f.Faction == faction);
    FactionIdentityView? DescribeFaction(string recordId) => FactionCatalogue.FirstOrDefault(f => f.RecordId == recordId);
    FactionStandingView? StandingTowards(int faction) => null;
    (IReadOnlyList<FactionInfoLine> Contracts, IReadOnlyList<FactionInfoLine> Events) FactionInformation => ([], []);

    /// <summary>
    /// The active faction's readable balance, null when it is unavailable or not authorized.
    /// </summary>
    long? Money { get; }

    bool HasPlayer { get; }

    IReadOnlyList<string> CreationRaces { get; }

    int Formation { get; }

    bool IsRemote { get; }

    bool CanSaveWorld { get; }

    bool CanChangeSpeed { get; }

    double TimeScale { get; }

    IReadOnlyList<double> TimeScales => [1, 2, 5];

    bool IsPaused => TimeScale == 0;

    void SetSpeed(double speed);
    void TogglePause();
    /// <summary>
    /// A local menu/loading pause request. A remote implementation must not pause the shared world.
    /// </summary>
    void SetModalPause(bool paused);
    void Submit(SimCommand command);
    VerbFacts? VerbFactsOf(CharacterId lead, CharacterId target) => CurrentSnapshot.VerbLead == lead ? CurrentSnapshot.Verbs.FirstOrDefault(v => v.Target == target)?.Facts : null;
    bool RegardsFriendly(CharacterId target, CharacterId lead) => VerbFactsOf(lead, target)?.RegardsLead == true;
    bool? ArrivalForget(int serial) => CurrentSnapshot.ArrivalSerial == serial ? CurrentSnapshot.ArrivalForget : null;
    void SetPointed(CharacterId target) => Submit(new PointedCommand(target) { Tick = CommandTick });
    bool CanTalk(CharacterId target, CharacterId? speaker = null);
    /// <summary>
    /// True while eligibility is unknown; callers must not turn this into an attack fallback.
    /// </summary>
    bool TalkHintPending(CharacterId target, CharacterId? speaker = null) => false;
    bool WantsToTalk(CharacterId target);
    ProspectReport? Prospected { get; }

    void EnableFloaters(bool enabled);
    bool TryDequeueFloater(out Floater floater);
    void EnableSounds(bool enabled)
    {
    }

    bool TryDequeueSound(out SoundEvent sound)
    {
        sound = default;
        return false;
    }

    void EnableBlood(bool enabled)
    {
    }

    bool TryDequeueBlood(out BloodVisual blood)
    {
        blood = default;
        return false;
    }

    void EnableBlockSparks(bool enabled)
    {
    }

    bool TryDequeueBlockSpark(out CharacterId who)
    {
        who = default;
        return false;
    }
}