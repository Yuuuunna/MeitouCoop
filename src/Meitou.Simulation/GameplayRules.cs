using Meitou.Simulation.Bodies;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
namespace Meitou.Simulation;

/// <summary>
/// Public world-wide rules. Contains no account, role, faction-resource or mod-private state.
/// </summary>
public sealed record GameplayRules
{
    public float GlobalDamageMultiplier { get; init; } = 1;
    public float HungerTime { get; init; } = 1;
    public float ChanceOfDeath { get; init; } = 1;
    /// <summary>
    /// The authority's experience multiplier for all player factions, shared by every connected player.
    /// </summary>
    public float SquadXp { get; init; } = 1;
    /// <summary>
    /// Per-stat authority experience overrides; negative values follow SquadXp, and an empty array means all skills follow.
    /// </summary>
    public float[] SquadSkillXp { get; init; } = [];
    /// <summary>
    /// The authority's experience multiplier for NPC factions and animals.
    /// </summary>
    public float OthersXp { get; init; } = 1;
    public float ProductionSpeed { get; init; } = 1;
    public float ResearchSpeed { get; init; } = 1;
    public float BuildingSpeed { get; init; } = 1;
    public float NestsMultiplier { get; init; } = 1;
    public Bodies.Dismemberment Dismemberment { get; init; } = Bodies.Dismemberment.Rare;
    public bool Decapitation { get; init; } = true;
    public bool AdvancedRagdolls { get; init; } = true;
    public int? AttackSlots { get; init; } = null;
    /// <summary>
    /// Optional authority faction cap; null keeps the content's game constant.
    /// </summary>
    public int? MaxFactionSize { get; init; }
    /// <summary>
    /// Optional authority squad cap; null keeps the content's game constant.
    /// </summary>
    public int? MaxSquadSize { get; init; }
    /// <summary>
    /// Optional authority led squad cap; null keeps the content's game constant.
    /// </summary>
    public int? MaxSquads { get; init; }
    public int HarpoonLimit { get; init; } = 60;
    public bool LateDefences { get; init; } = false;
    public bool GunnerFacesBarrel { get; init; } = true;
    public bool PunchReachFix { get; init; } = true;
    public bool BenchFacingFix { get; init; } = true;
    /// <summary>
    /// Positions a sitter using a bed clip at the fixture's use node.
    /// </summary>
    public bool SeatBedFix { get; init; } = true;
    /// <summary>
    /// Attributes ground item theft to the item's actual owner.
    /// </summary>
    public bool PickUpTheftFix { get; init; } = true;
    /// <summary>
    /// Stops guards arresting targets they have lost sight of.
    /// </summary>
    public bool ArrestLostFix { get; init; } = true;
    /// <summary>
    /// Uses animal moves for natural weapons whose item category would choose an unavailable person animation.
    /// </summary>
    public bool AnimalMovesFix { get; init; } = true;
    /// <summary>
    /// Records turret attacks as assault for the authority's crime system.
    /// </summary>
    public bool TurretAssault { get; init; } = true;
    /// <summary>
    /// Keeps wounded prisoners in their cage or pole pose.
    /// </summary>
    public bool PrisonPoseFix { get; init; } = true;
    /// <summary>
    /// Keeps caged animals in their corrected prisoner pose.
    /// </summary>
    public bool CagedAnimalFix { get; init; } = true;
    /// <summary>
    /// Preserves campaign elapsed time when a save is loaded.
    /// </summary>
    public bool KeepCampaignClocks { get; init; } = true;
    /// <summary>
    /// Avoids repeatedly lifting prisoners when no destination cage can accept them.
    /// </summary>
    public bool ArrestLiftFix { get; init; } = true;
    /// <summary>
    /// Repairs original terrain holes and needles before navigation and presentation load their heightmaps.
    /// </summary>
    public bool GroundRepair { get; init; } = true;
    /// <summary>
    /// Searches carried bags and the worn backpack during confiscation and scavenging.
    /// </summary>
    public bool BagSearchFix { get; init; } = true;
    /// <summary>
    /// Approaches a shopkeeper from the front of the counter.
    /// </summary>
    public bool CounterTalk { get; init; } = true;
    /// <summary>
    /// Allows an authorized looter to drag an item out of the body window onto the ground, with the ordinary theft checks.
    /// </summary>
    public bool DropFromLoot { get; init; } = true;
    /// <summary>
    /// Cage guards spare captives of their own faction when the authority enables the optional rule.
    /// </summary>
    public bool CagesSpareOwn { get; init; }
    /// <summary>
    /// Whether player animals may collect loose items into their bags.
    /// </summary>
    public bool AnimalsPickUp { get; init; } = false;
    public float SquadSize { get; init; } = 1;
    public float Population { get; init; } = 1;
    public float RaidSize { get; init; } = 1;
    public float RaidFrequency { get; init; } = 1;
    public int TownAttacks { get; init; } = 2;
    public bool FastZoneHopping { get; init; } = false;
    public bool KeepRoamers { get; init; } = true;
    public bool OffscreenFights { get; init; } = true;
    public bool OffscreenHealing { get; init; } = true;
    public bool CampaignAncestorsOnce { get; init; } = true;
    public bool KeepWild { get; init; } = true;
    /// <summary>
    /// Keeps a knocked-out squad member in a loaded region when its squad leaves.
    /// </summary>
    public bool KeepDowned { get; init; } = true;
    public bool ResidentSchedule { get; init; } = true;
    /// <summary>
    /// Applies town overrides while their regions are unloaded.
    /// </summary>
    public bool OffscreenOverrides { get; init; } = true;
    public bool UnloadedProduction { get; init; } = true;
    public bool Luckyren { get; init; } = false;
    public int GroundItemLimit { get; init; } = Items.GroundItems.DefaultLimit;
    public bool InteriorLampsAllDay { get; init; } = true;
    public int AutoSaveSeconds { get; init; } = 600;

    public static GameplayRules Capture(World world)
    {
        var bodies = world.Systems.OfType<BodySystem>().FirstOrDefault()?.Options;
        var combat = world.Systems.OfType<Combat.CombatSystem>().FirstOrDefault();
        var population = world.Systems.OfType<PopulationSystem>().FirstOrDefault();
        var ai = world.Systems.OfType<Ai.AiSystem>().FirstOrDefault();
        var production = world.Systems.OfType<Production.ProductionSystem>().FirstOrDefault();
        var captives = world.Systems.OfType<Kidnap.CaptiveSystem>().FirstOrDefault();
        return new()
        {
            GlobalDamageMultiplier = bodies?.GlobalDamageMultiplier ?? 1,
            HungerTime = bodies?.HungerTime ?? 1,
            ChanceOfDeath = bodies?.ChanceOfDeath ?? 1,
            SquadXp = bodies?.SquadXp ?? 1,
            SquadSkillXp = bodies?.SquadSkillXp is { } rates ? [.. rates] : [],
            OthersXp = bodies?.OthersXp ?? 1,
            ProductionSpeed = production?.Settings.ProductionOption ?? 1,
            ResearchSpeed = production?.Settings.ResearchOption ?? 1,
            BuildingSpeed = world.Systems.OfType<Construction.ConstructionSystem>().FirstOrDefault()?.Settings.BuildingSpeed ?? 1,
            NestsMultiplier = population?.Settings.NestsMultiplier ?? 1,
            Dismemberment = bodies?.Dismemberment ?? Bodies.Dismemberment.Rare,
            Decapitation = bodies?.Decapitation ?? true,
            AdvancedRagdolls = bodies?.AdvancedRagdolls ?? true,
            AttackSlots = combat?.AttackSlots,
            MaxFactionSize = bodies?.MaxFactionSize,
            MaxSquadSize = bodies?.MaxSquadSize,
            MaxSquads = bodies?.MaxSquads,
            HarpoonLimit = combat?.StuckLimit ?? 60,
            LateDefences = combat?.LateDefences ?? false,
            GunnerFacesBarrel = combat?.GunnerFacesBarrel ?? true,
            PunchReachFix = combat?.PunchReachFix ?? true,
            BenchFacingFix = world.BenchFacingFix,
            SeatBedFix = world.SeatBedFix,
            PickUpTheftFix = world.PickUpTheftFix,
            ArrestLostFix = world.SystemOf<Crime.ArrestSystem>()?.Settings.LostFix ?? true,
            AnimalMovesFix = combat?.AnimalMovesFix ?? true,
            TurretAssault = combat?.TurretAssault ?? true,
            PrisonPoseFix = world.PrisonPoseFix,
            CagedAnimalFix = world.CagedAnimalFix,
            KeepCampaignClocks = population?.Settings.KeepCampaignClocks ?? true,
            ArrestLiftFix = world.SystemOf<Crime.ArrestSystem>()?.Settings.LiftFix ?? true,
            GroundRepair = world.GroundRepair,
            BagSearchFix = world.BagSearchFix,
            CounterTalk = world.CounterTalk,
            DropFromLoot = world.DropFromLoot,
            CagesSpareOwn = captives?.Settings.SpareOwnFaction ?? false,
            AnimalsPickUp = world.SystemOf<PlayerSystem>()?.AnimalsPickUp ?? false,
            SquadSize = population?.Settings.SquadSizeMultiplier ?? 1,
            Population = population?.Settings.PopulationMultiplier ?? 1,
            RaidSize = population?.Settings.RaidSizeMult ?? 1,
            RaidFrequency = population?.Settings.RaidFrequencyMult ?? 1,
            TownAttacks = ai?.Settings.TownAttacks ?? 2,
            FastZoneHopping = population?.Settings.FastZoneHopping ?? false,
            KeepRoamers = population?.KeepRoamers ?? true,
            OffscreenFights = population?.OffscreenFights ?? true,
            OffscreenHealing = population?.OffscreenHealing ?? true,
            CampaignAncestorsOnce = population?.CampaignAncestorsOnce ?? true,
            KeepWild = population?.KeepWild ?? true,
            KeepDowned = population?.KeepDowned ?? true,
            ResidentSchedule = population?.ResidentSchedule ?? true,
            OffscreenOverrides = population?.OffscreenOverrides ?? true,
            UnloadedProduction = production?.UnloadedProduction ?? true,
            Luckyren = world.Luckyren,
            GroundItemLimit = world.GroundItems.Limit,
            InteriorLampsAllDay = world.Sight.InteriorLampsAllDay,
            AutoSaveSeconds = world.AuthorityAutoSaveSeconds,
        };
    }
}

public sealed partial class World
{
    /// <summary>
    /// Host persistence policy, exposed to clients as a read-only rule; zero disables scheduled saves.
    /// </summary>
    public int AuthorityAutoSaveSeconds { get; set; } = 600;

    /// <summary>
    /// The terrain-repair choice captured before this world's content is loaded; retained through checkpoint replacement.
    /// </summary>
    public bool GroundRepair { get; set; } = true;
}
