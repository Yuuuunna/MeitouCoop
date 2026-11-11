using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Game.Host;
public sealed partial class WorldSession : IPlayerSession
{
    object _presentationIdentity = new();
    public GameplayRules AuthorityRules => GameplayRules.Capture(World);
    public object WorldIdentity => _presentationIdentity;
    public PlayerMessageBatch Messages => World.Player.Messages.Capture();
    public Meitou.Data.World.WeatherFrame? Weather => (World.Weather as IWeatherPresentationSource)?.CaptureWeather();
    public Meitou.Data.Particles.WeatherEffectFrame? WeatherEffects => (World.Weather as IWeatherPresentationSource)?.CaptureEffects();
    public long CommandTick => World.Tick;
    public float SimulationStepSeconds => World.TickSeconds;
    public int ActiveFaction => World.Player.Faction;
    public IReadOnlyList<FactionIdentityView> FactionCatalogue => World.LocalFactionCatalogue();

    public FactionIdentityView? DescribeFaction(int faction) => World.DescribeFaction(faction);
    public FactionIdentityView? DescribeFaction(string recordId) => FactionCatalogue.FirstOrDefault(f => f.RecordId == recordId);
    public FactionStandingView? StandingTowards(int faction) => World.DescribeStanding(ActiveFaction, faction);
    public (IReadOnlyList<FactionInfoLine> Contracts, IReadOnlyList<FactionInfoLine> Events) FactionInformation
    {
        get
        {
            string? Name(int faction) => DescribeFaction(faction)?.Name;
            try
            {
                return (World.ActiveContracts(Name), World.Systems.OfType<PopulationSystem>().FirstOrDefault()?.UpcomingEvents(World, Name) ?? []);
            }
            catch (InvalidOperationException)
            {
                return ([], []);
            }
        }
    }

    public long? Money => World.Player.Money;
    public bool HasPlayer => World.Player.Exists;
    public IReadOnlyList<string> CreationRaces => World.Player.CreationRaces;
    public int Formation => SquadAiKeys.FormationOf(World, ActiveFaction, null);
    public IReadOnlyList<double> TimeScales => Simulation.Speeds;
    public bool IsRemote => false;
    public bool CanSaveWorld => true;
    public bool CanChangeSpeed => true;

    public void SetSpeed(double speed) => Simulation.SetSpeed(speed);
    public void TogglePause() => Simulation.TogglePause();
    public void SetModalPause(bool paused) => Simulation.RequestedPause = paused;
    public void Submit(SimCommand command) => World.Commands.Enqueue(command);
    public VerbFacts? VerbFactsOf(CharacterId lead, CharacterId target) => World.VerbFactsOf(lead, target);
    public bool RegardsFriendly(CharacterId target, CharacterId lead) => World.RegardsFriendly(target, lead);
    public bool? ArrivalForget(int serial) => World.ArrivalForget(serial);
    public void SetPointed(CharacterId target) => World.Player.Pointed = target;
    public bool CanTalk(CharacterId target, CharacterId? speaker = null) => World.CanTalk(target, speaker);
    public bool WantsToTalk(CharacterId target) => World.WantsToTalk(target);
    public ProspectReport? Prospected => World.Prospected;

    public void EnableFloaters(bool enabled) => World.Floaters = enabled ? World.Floaters ?? new() : null;
    public bool TryDequeueFloater(out Floater floater)
    {
        floater = default;
        return World.Floaters?.TryDequeue(out floater) == true;
    }

    public void EnableSounds(bool enabled) => World.Sounds = enabled ? World.Sounds ?? new() : null;
    public bool TryDequeueSound(out SoundEvent sound)
    {
        sound = default;
        return World.Sounds?.TryDequeue(out sound) == true;
    }

    public void EnableBlood(bool enabled) => World.BloodMarks = enabled ? World.BloodMarks ?? new() : null;
    public bool TryDequeueBlood(out BloodVisual blood)
    {
        blood = default;
        if (World.BloodMarks?.TryDequeue(out var mark) != true)
            return false;
        blood = new(mark, World.Tick, World.TickSeconds, BodySystem.CorpseTicksOf(World));
        return true;
    }

    public void EnableBlockSparks(bool enabled) => World.BlockSparks = enabled ? World.BlockSparks ?? new() : null;
    public bool TryDequeueBlockSpark(out CharacterId who)
    {
        who = default;
        return World.BlockSparks?.TryDequeue(out who) == true;
    }
}