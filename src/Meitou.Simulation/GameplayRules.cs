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
    public float ProductionSpeed { get; init; } = 1;
    public float ResearchSpeed { get; init; } = 1;
    public float BuildingSpeed { get; init; } = 1;
    public float NestsMultiplier { get; init; } = 1;
    public Bodies.Dismemberment Dismemberment { get; init; } = Bodies.Dismemberment.Rare;
    public bool Decapitation { get; init; } = true;
    public bool AdvancedRagdolls { get; init; } = true;
    public int? AttackSlots { get; init; } = null;
    public int HarpoonLimit { get; init; } = 60;
    public bool LateDefences { get; init; } = false;
    public bool GunnerFacesBarrel { get; init; } = true;
    public bool PunchReachFix { get; init; } = true;
    public bool BenchFacingFix { get; init; } = true;
    public bool DoorsCloseBehind { get; init; } = true;
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
    public bool ResidentSchedule { get; init; } = true;
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
        var locks = world.Systems.OfType<Locks.LockSystem>().FirstOrDefault();
        return new()
        {
            GlobalDamageMultiplier = bodies?.GlobalDamageMultiplier ?? 1,
            HungerTime = bodies?.HungerTime ?? 1,
            ChanceOfDeath = bodies?.ChanceOfDeath ?? 1,
            ProductionSpeed = production?.Settings.ProductionOption ?? 1,
            ResearchSpeed = production?.Settings.ResearchOption ?? 1,
            BuildingSpeed = world.Systems.OfType<Construction.ConstructionSystem>().FirstOrDefault()?.Settings.BuildingSpeed ?? 1,
            NestsMultiplier = population?.Settings.NestsMultiplier ?? 1,
            Dismemberment = bodies?.Dismemberment ?? Bodies.Dismemberment.Rare,
            Decapitation = bodies?.Decapitation ?? true,
            AdvancedRagdolls = bodies?.AdvancedRagdolls ?? true,
            AttackSlots = combat?.AttackSlots,
            HarpoonLimit = combat?.StuckLimit ?? 60,
            LateDefences = combat?.LateDefences ?? false,
            GunnerFacesBarrel = combat?.GunnerFacesBarrel ?? true,
            PunchReachFix = combat?.PunchReachFix ?? true,
            BenchFacingFix = world.BenchFacingFix,
            DoorsCloseBehind = locks?.DoorsCloseBehind ?? true,
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
            ResidentSchedule = population?.ResidentSchedule ?? true,
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
}