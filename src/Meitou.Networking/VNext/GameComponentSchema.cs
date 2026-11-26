using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Weather;
using Meitou.Simulation;

namespace Meitou.Networking.VNext;
// IDs are protocol constants. Append fields; never renumber or reuse an ID. Field 2074 (WarParty) is retired.
internal static class GameComponentSchema
{
    /// <summary>
    /// Protocol-only metadata for the public Meters property. It is independently revisioned, required in baselines, and does not exempt
    /// any public snapshot property from the reflection coverage check below.
    /// </summary>
    internal const ushort MeterTooltipsField = 2100;

    /// <summary>
    /// Immutable status-effect text and modifier metadata, independently revisioned from stacks and countdowns.
    /// </summary>
    internal const ushort EffectDefinitionsField = 2103;

    /// <summary>
    /// Resolved skin tints change independently of the rest of the public Appearance descriptor.
    /// </summary>
    internal const ushort AppearanceTintsField = 2106;

    /// <summary>
    /// Auxiliary wire components have no redundant public snapshot property; the replica joins them to their explicitly mapped value.
    /// </summary>
    internal static readonly IReadOnlyDictionary<ushort, string> AuxiliaryCharacterFields = new Dictionary<ushort, string>
    {
        [MeterTooltipsField] = "MeterTooltips",
        [EffectDefinitionsField] = "EffectDefinitions",
        [AppearanceTintsField] = "AppearanceTints"
    };

    internal static readonly Field<PlayerWorldView>[] ViewFields = [F(1, "Roles", (PlayerWorldView x) => x.Roles, (x, v) => x with { Roles = v }), F(2, "AuthorityRules", (PlayerWorldView x) => x.AuthorityRules, (x, v) => x with { AuthorityRules = v }), F(3, "ActiveFaction", (PlayerWorldView x) => x.ActiveFaction, (x, v) => x with { ActiveFaction = v }), F(4, "Factions", (PlayerWorldView x) => x.Factions, (x, v) => x with { Factions = v }), F(5, "FactionCatalogue", (PlayerWorldView x) => x.FactionCatalogue, (x, v) => x with { FactionCatalogue = v }), F(6, "Prospected", (PlayerWorldView x) => x.Prospected, (x, v) => x with { Prospected = v }), Events(), F(8, "Messages", (PlayerWorldView x) => x.Messages, (x, v) => x with { Messages = v }), F(9, "Weather", (PlayerWorldView x) => x.Weather is { } weather ? weather with { Time = default } : null, (x, v) => x with { Weather = v }), F(10, "WeatherEffects", (PlayerWorldView x) => x.WeatherEffects, (x, v) => x with { WeatherEffects = v }), ];
    internal static readonly Field<WorldSnapshot>[] SnapshotFields = [F(1001, "FoliageOwnedTowns", (WorldSnapshot x) => x.FoliageOwnedTowns, (x, v) => x with { FoliageOwnedTowns = v }), F(1002, "Buildings", (WorldSnapshot x) => x.Buildings, (x, v) => x with { Buildings = v }), F(1003, "BuildingsRevision", (WorldSnapshot x) => x.BuildingsRevision, (x, v) => x with { BuildingsRevision = v }), F(1004, "NestBuildings", (WorldSnapshot x) => x.NestBuildings, (x, v) => x with { NestBuildings = v }), F(1005, "NestMeshes", (WorldSnapshot x) => x.NestMeshes, (x, v) => x with { NestMeshes = v }), F(1006, "NestBuildingsRevision", (WorldSnapshot x) => x.NestBuildingsRevision, (x, v) => x with { NestBuildingsRevision = v }), F(1007, "Ruins", (WorldSnapshot x) => x.Ruins, (x, v) => x with { Ruins = v }), F(1008, "RuinsRevision", (WorldSnapshot x) => x.RuinsRevision, (x, v) => x with { RuinsRevision = v }), F(1009, "EmptiedHouses", (WorldSnapshot x) => x.EmptiedHouses, (x, v) => x with { EmptiedHouses = v }), F(1010, "EmptiedHousesRevision", (WorldSnapshot x) => x.EmptiedHousesRevision, (x, v) => x with { EmptiedHousesRevision = v }), F(1011, "GroundItems", (WorldSnapshot x) => x.GroundItems, (x, v) => x with { GroundItems = v }), F(1012, "GroundItemsRevision", (WorldSnapshot x) => x.GroundItemsRevision, (x, v) => x with { GroundItemsRevision = v }), F(1013, "Containers", (WorldSnapshot x) => x.Containers, (x, v) => x with { Containers = v }), F(1014, "Beds", (WorldSnapshot x) => x.Beds, (x, v) => x with { Beds = v }), F(1015, "Dummies", (WorldSnapshot x) => x.Dummies, (x, v) => x with { Dummies = v }), F(1016, "Seats", (WorldSnapshot x) => x.Seats, (x, v) => x with { Seats = v }), F(1017, "Cages", (WorldSnapshot x) => x.Cages, (x, v) => x with { Cages = v }), F(1018, "DoorPicks", (WorldSnapshot x) => x.DoorPicks, (x, v) => x with { DoorPicks = v }), F(1019, "Doors", (WorldSnapshot x) => x.Doors, (x, v) => x with { Doors = v }), F(1020, "DoorGroupRevisions", (WorldSnapshot x) => x.DoorGroupRevisions, (x, v) => x with { DoorGroupRevisions = v }), F(1021, "VerbLead", (WorldSnapshot x) => x.VerbLead, (x, v) => x with { VerbLead = v }), F(1022, "Verbs", (WorldSnapshot x) => x.Verbs, (x, v) => x with { Verbs = v }), F(1023, "ArrivalSerial", (WorldSnapshot x) => x.ArrivalSerial, (x, v) => x with { ArrivalSerial = v }), F(1024, "ArrivalForget", (WorldSnapshot x) => x.ArrivalForget, (x, v) => x with { ArrivalForget = v }), F(1025, "Trade", (WorldSnapshot x) => x.Trade, (x, v) => x with { Trade = v }), F(1026, "Conversation", (WorldSnapshot x) => x.Conversation, (x, v) => x with { Conversation = v }), F(1027, "CharacterTrade", (WorldSnapshot x) => x.CharacterTrade, (x, v) => x with { CharacterTrade = v }), F(1028, "DialogueRequest", (WorldSnapshot x) => x.DialogueRequest, (x, v) => x with { DialogueRequest = v }), F(1029, "OpenLoot", (WorldSnapshot x) => x.OpenLoot, (x, v) => x with { OpenLoot = v }), F(1030, "LootOpener", (WorldSnapshot x) => x.LootOpener, (x, v) => x with { LootOpener = v }), F(1031, "LootSessionId", (WorldSnapshot x) => x.LootSessionId, (x, v) => x with { LootSessionId = v }), F(1032, "Held", (WorldSnapshot x) => x.Held, (x, v) => x with { Held = v }), F(1033, "HeldSerial", (WorldSnapshot x) => x.HeldSerial, (x, v) => x with { HeldSerial = v }), F(1034, "PlayerTrade", (WorldSnapshot x) => x.PlayerTrade, (x, v) => x with { PlayerTrade = v }), F(1035, "InspectedBuilding", (WorldSnapshot x) => x.InspectedBuilding, (x, v) => x with { InspectedBuilding = v }), F(1036, "InspectedRichness", (WorldSnapshot x) => x.InspectedRichness, (x, v) => x with { InspectedRichness = v }), F(1037, "InspectedTurret", (WorldSnapshot x) => x.InspectedTurret, (x, v) => x with { InspectedTurret = v }), F(1038, "Towns", (WorldSnapshot x) => x.Towns, (x, v) => x with { Towns = v }), F(1039, "Camps", (WorldSnapshot x) => x.Camps, (x, v) => x with { Camps = v }), F(1040, "KnownFactions", (WorldSnapshot x) => x.KnownFactions, (x, v) => x with { KnownFactions = v }), F(1041, "TutorialFacts", (WorldSnapshot x) => x.TutorialFacts, (x, v) => x with { TutorialFacts = v }), F(1042, "SquadAi", (WorldSnapshot x) => x.SquadAi, (x, v) => x with { SquadAi = v }), F(1043, "PlayerSquads", (WorldSnapshot x) => x.PlayerSquads, (x, v) => x with { PlayerSquads = v }), F(1044, "ActiveSquad", (WorldSnapshot x) => x.ActiveSquad, (x, v) => x with { ActiveSquad = v }), F(1045, "Outposts", (WorldSnapshot x) => x.Outposts, (x, v) => x with { Outposts = v }), F(1046, "UnlockedBuildings", (WorldSnapshot x) => x.UnlockedBuildings, (x, v) => x with { UnlockedBuildings = v }), F(1047, "OwnedTownBuildings", (WorldSnapshot x) => x.OwnedTownBuildings, (x, v) => x with { OwnedTownBuildings = v }), F(1048, "Machines", (WorldSnapshot x) => x.Machines, (x, v) => x with { Machines = v }), F(1049, "MachinesVersion", (WorldSnapshot x) => x.MachinesVersion, (x, v) => x with { MachinesVersion = v }), F(1050, "Research", (WorldSnapshot x) => x.Research, (x, v) => x with { Research = v }), F(1051, "Crafts", (WorldSnapshot x) => x.Crafts, (x, v) => x with { Crafts = v }), F(1052, "Smiths", (WorldSnapshot x) => x.Smiths, (x, v) => x with { Smiths = v }), F(1053, "Projectiles", (WorldSnapshot x) => x.Projectiles, (x, v) => x with { Projectiles = v }), F(1054, "Speech", (WorldSnapshot x) => x.Speech, (x, v) => x with { Speech = v }), F(1055, "ContainerOpener", (WorldSnapshot x) => x.ContainerOpener, (x, v) => x with { ContainerOpener = v }),
        F(1056, "MapSquads", (WorldSnapshot x) => x.MapSquads, (x, v) => x with { MapSquads = v }),
        F(1057, "WorkSites", (WorldSnapshot x) => x.WorkSites, (x, v) => x with { WorkSites = v }),
        F(1058, "FoliageOutposts", (WorldSnapshot x) => x.FoliageOutposts, (x, v) => x with { FoliageOutposts = v }),
        new(1059, "InspectedLevelPower", (_, x) => ModPresentationCodec.EncodePower(x.InspectedLevelPower),
            (_, x, bytes) => x with { InspectedLevelPower = ModPresentationCodec.DecodePower(bytes) }),
        F(1060, "IndependentSquads", (WorldSnapshot x) => x.IndependentSquads, (x, v) => x with { IndependentSquads = v }),
        F(1061, "InventoriesClosed", (WorldSnapshot x) => x.InventoriesClosed, (x, v) => x with { InventoriesClosed = v }),
        F(1062, "ModJobNames", (WorldSnapshot x) => x.ModJobNames, (x, v) => x with { ModJobNames = v }),
        F(1063, "FactionHeads", (WorldSnapshot x) => x.FactionHeads, (x, v) => x with { FactionHeads = v }),
        F(1064, "CappedHeads", (WorldSnapshot x) => x.CappedHeads, (x, v) => x with { CappedHeads = v }),
        F(1065, "InspectedTownMachine", (WorldSnapshot x) => x.InspectedTownMachine, (x, v) => x with { InspectedTownMachine = v }),
        F(1066, "InspectedOwnMachine", (WorldSnapshot x) => x.InspectedOwnMachine, (x, v) => x with { InspectedOwnMachine = v }),
        F(1067, "ModJobOffers", (WorldSnapshot x) => x.ModJobOffers, (x, v) => x with { ModJobOffers = v }),
        F(1068, "ModVisuals", (WorldSnapshot x) => x.ModVisuals, (x, v) => x with { ModVisuals = v }),
        F(1069, "ItemTags", (WorldSnapshot x) => x.ItemTags, (x, v) => x with { ItemTags = v }),
        F(1070, "MaxFactionSize", (WorldSnapshot x) => x.MaxFactionSize, (x, v) => x with { MaxFactionSize = v }),
        F(1071, "MaxSquadSize", (WorldSnapshot x) => x.MaxSquadSize, (x, v) => x with { MaxSquadSize = v }),
        F(1072, "MaxSquads", (WorldSnapshot x) => x.MaxSquads, (x, v) => x with { MaxSquads = v }),
        F(1073, "CraftStations", (WorldSnapshot x) => x.CraftStations, (x, v) => x with { CraftStations = v }),
    ];
    /// <summary>
    /// Independently replicated character values; binary codecs retain their own uncompressed format.
    /// </summary>
    internal static readonly Field<CharacterSnapshot>[] CharacterFields =
    [
        F(2001, "Faction", (CharacterSnapshot x) => x.Faction, (x, v) => x with { Faction = v }),
        F(2002, "SquadId", (CharacterSnapshot x) => x.SquadId, (x, v) => x with { SquadId = v }),
        F(2003, "IsPlayer", (CharacterSnapshot x) => x.IsPlayer, (x, v) => x with { IsPlayer = v }),
        F(2004, "Controller", (CharacterSnapshot x) => x.Controller, (x, v) => x with { Controller = v }),
        new(2005, "Appearance", (codec, x) => CharacterTintsCodec.EncodeAppearance(codec, x.Appearance),
            (codec, x, bytes) => x with { Appearance = CharacterTintsCodec.DecodeAppearance(codec, bytes) }, CompressedJson: true),
        new(2006, "Build", (_, x) => BodyBuildCodec.Encode(x.Build), (_, x, bytes) => x with { Build = BodyBuildCodec.Decode(bytes) }),
        F(2007, "Name", (CharacterSnapshot x) => x.Name, (x, v) => x with { Name = v }),
        F(2008, "RecordId", (CharacterSnapshot x) => x.RecordId, (x, v) => x with { RecordId = v }),
        F(2009, "Scale", (CharacterSnapshot x) => x.Scale, (x, v) => x with { Scale = v }),
        F(2010, "DrawDistanceScale", (CharacterSnapshot x) => x.DrawDistanceScale, (x, v) => x with { DrawDistanceScale = v }),
        F(2011, "PortraitSerial", (CharacterSnapshot x) => x.PortraitSerial, (x, v) => x with { PortraitSerial = v }),
        F(2012, "WeaponDrawn", (CharacterSnapshot x) => x.WeaponDrawn, (x, v) => x with { WeaponDrawn = v }),
        F(2013, "RangedDrawn", (CharacterSnapshot x) => x.RangedDrawn, (x, v) => x with { RangedDrawn = v }),
        F(2014, "BackDrawn", (CharacterSnapshot x) => x.BackDrawn, (x, v) => x with { BackDrawn = v }),
        F(2015, "StuckBolts", (CharacterSnapshot x) => x.StuckBolts, (x, v) => x with { StuckBolts = v }),
        // Retired field 2016 was the removed display-only Inventory text; its ID is never reused.
        F(2017, "LimbBits", (CharacterSnapshot x) => x.LimbBits, (x, v) => x with { LimbBits = v }),
        F(2018, "BloodAmounts", (CharacterSnapshot x) => x.BloodAmounts, (x, v) => x with { BloodAmounts = v }),
        F(2019, "HeldTool", (CharacterSnapshot x) => x.HeldTool, (x, v) => x with { HeldTool = v }),
        F(2020, "Mouth", (CharacterSnapshot x) => x.Mouth, (x, v) => x with { Mouth = v }),
        F(2021, "MouthTone", (CharacterSnapshot x) => x.MouthTone, (x, v) => x with { MouthTone = v }),
        F(2022, "HasFlies", (CharacterSnapshot x) => x.HasFlies, (x, v) => x with { HasFlies = v }),
        F(2023, "CrossbowShots", (CharacterSnapshot x) => x.CrossbowShots, (x, v) => x with { CrossbowShots = v }),
        F(2024, "Knockdown", (CharacterSnapshot x) => x.Knockdown, (x, v) => x with { Knockdown = v }),
        F(2025, "CarriedBy", (CharacterSnapshot x) => x.CarriedBy, (x, v) => x with { CarriedBy = v }),
        F(2026, "Carry", (CharacterSnapshot x) => x.Carry, (x, v) => x with { Carry = v }),
        F(2027, "Carrying", (CharacterSnapshot x) => x.Carrying, (x, v) => x with { Carrying = v }),
        F(2028, "WorkEvents", (CharacterSnapshot x) => x.WorkEvents, (x, v) => x with { WorkEvents = v }),
        F(2029, "Task", (CharacterSnapshot x) => x.Task, (x, v) => x with { Task = v }),
        F(2030, "PickingLock", (CharacterSnapshot x) => x.PickingLock, (x, v) => x with { PickingLock = v }),
        F(2031, "WorkingShackles", (CharacterSnapshot x) => x.WorkingShackles, (x, v) => x with { WorkingShackles = v }),
        F(2032, "InBed", (CharacterSnapshot x) => x.InBed, (x, v) => x with { InBed = v }),
        F(2033, "StayingLow", (CharacterSnapshot x) => x.StayingLow, (x, v) => x with { StayingLow = v }),
        F(2034, "InCombat", (CharacterSnapshot x) => x.InCombat, (x, v) => x with { InCombat = v }),
        F(2035, "CombatMode", (CharacterSnapshot x) => x.CombatMode, (x, v) => x with { CombatMode = v }),
        F(2036, "Activity", (CharacterSnapshot x) => x.Activity, (x, v) => x with { Activity = v }),
        F(2037, "Turret", (CharacterSnapshot x) => x.Turret, (x, v) => x with { Turret = v }),
        new(2038, "Ragdoll", (_, x) => RagdollPoseCodec.Encode(x.Ragdoll),
            (_, x, bytes) => x with { Ragdoll = RagdollPoseCodec.Decode(bytes) },
            CompressedJson: false),
        new(2039, "Body", (_, x) => BodyStatusCodec.Encode(x.Body), (_, x, bytes) => x with { Body = BodyStatusCodec.Decode(bytes) }),
        F(2040, "HealthParts", (CharacterSnapshot x) => x.HealthParts, (x, v) => x with { HealthParts = v }),
        F(2041, "Asleep", (CharacterSnapshot x) => x.Asleep, (x, v) => x with { Asleep = v }),
        F(2042, "Down", (CharacterSnapshot x) => x.Down, (x, v) => x with { Down = v }),
        F(2043, "Conscious", (CharacterSnapshot x) => x.Conscious, (x, v) => x with { Conscious = v }),
        F(2044, "Standing", (CharacterSnapshot x) => x.Standing, (x, v) => x with { Standing = v }),
        F(2045, "Slave", (CharacterSnapshot x) => x.Slave, (x, v) => x with { Slave = v }),
        F(2046, "Disguise", (CharacterSnapshot x) => x.Disguise, (x, v) => x with { Disguise = v }),
        F(2047, "Status", (CharacterSnapshot x) => x.Status, (x, v) => x with { Status = v }),
        new(2048, "Bounties", (_, x) => GameHoursCountdownCodec.EncodeBounties(x.Bounties, 0.0),
            (_, x, bytes) => x with { Bounties = GameHoursCountdownCodec.ProjectBounties(GameHoursCountdownCodec.DecodeBounties(bytes), 0.0) }),
        F(2049, "Imprisoned", (CharacterSnapshot x) => x.Imprisoned, (x, v) => x with { Imprisoned = v }),
        F(2050, "CageOpen", (CharacterSnapshot x) => x.CageOpen, (x, v) => x with { CageOpen = v }),
        F(2051, "Shackled", (CharacterSnapshot x) => x.Shackled, (x, v) => x with { Shackled = v }),
        F(2052, "ShacklesLocked", (CharacterSnapshot x) => x.ShacklesLocked, (x, v) => x with { ShacklesLocked = v }),
        F(2053, "WeatherNotice", (CharacterSnapshot x) => x.WeatherNotice, (x, v) => x with { WeatherNotice = v }),
        F(2054, "WeatherNoticeProtection", (CharacterSnapshot x) => x.WeatherNoticeProtection, (x, v) => x with { WeatherNoticeProtection = v }),
        F(2055, "WantsToTalk", (CharacterSnapshot x) => x.WantsToTalk, (x, v) => x with { WantsToTalk = v }),
        F(2056, "IsTrader", (CharacterSnapshot x) => x.IsTrader, (x, v) => x with { IsTrader = v }),
        F(2057, "IsDiplomat", (CharacterSnapshot x) => x.IsDiplomat, (x, v) => x with { IsDiplomat = v }),
        F(2058, "Selected", (CharacterSnapshot x) => x.Selected, (x, v) => x with { Selected = v }),
        F(2059, "Inspected", (CharacterSnapshot x) => x.Inspected, (x, v) => x with { Inspected = v }),
        F(2060, "Orders", (CharacterSnapshot x) => x.Orders, (x, v) => x with { Orders = v }),
        F(2061, "RunSpeed", (CharacterSnapshot x) => x.RunSpeed, (x, v) => x with { RunSpeed = v }),
        F(2062, "Jobs", (CharacterSnapshot x) => x.Jobs, (x, v) => x with { Jobs = v }),
        F(2063, "JobsRevision", (CharacterSnapshot x) => x.JobsRevision, (x, v) => x with { JobsRevision = v }),
        F(2064, "Path", (CharacterSnapshot x) => x.Path, (x, v) => x with { Path = v }),
        F(2065, "PathExact", (CharacterSnapshot x) => x.PathExact, (x, v) => x with { PathExact = v }),
        F(2066, "Skills", (CharacterSnapshot x) => x.Skills, (x, v) => x with { Skills = v }),
        F(2067, "AllSkills", (CharacterSnapshot x) => x.AllSkills, (x, v) => x with { AllSkills = v }),
        F(2068, "SkillXpMultipliers", (CharacterSnapshot x) => x.SkillXpMultipliers, (x, v) => x with { SkillXpMultipliers = v }),
        F(2069, "ClosedSlots", (CharacterSnapshot x) => x.ClosedSlots, (x, v) => x with { ClosedSlots = v }),
        F(2070, "Hud", (CharacterSnapshot x) => x.Hud, (x, v) => x with { Hud = v }),
        F(2071, "ItemGrid", (CharacterSnapshot x) => x.ItemGrid, (x, v) => x with { ItemGrid = v }, compress: false),
        F(2072, "FightsPlayer", (CharacterSnapshot x) => x.FightsPlayer, (x, v) => x with { FightsPlayer = v }),
        F(2073, "NoLootGuilt", (CharacterSnapshot x) => x.NoLootGuilt, (x, v) => x with { NoLootGuilt = v }),
        F(2075, "CanBeShackled", (CharacterSnapshot x) => x.CanBeShackled, (x, v) => x with { CanBeShackled = v }),
        F(2076, "CarriesShackles", (CharacterSnapshot x) => x.CarriesShackles, (x, v) => x with { CarriesShackles = v }),
        F(2077, "Decision", (CharacterSnapshot x) => x.Decision, (x, v) => x with { Decision = v }),
        F(2078, "Stealth", (CharacterSnapshot x) => x.Stealth, (x, v) => x with { Stealth = v }),
        F(2079, "Light", (CharacterSnapshot x) => x.Light, (x, v) => x with { Light = v }),
        F(2080, "Bar", (CharacterSnapshot x) => x.Bar, (x, v) => x with { Bar = v }),
        F(2081, "StealthKoChance", (CharacterSnapshot x) => x.StealthKoChance, (x, v) => x with { StealthKoChance = v }),
        F(2082, "CanStealthKo", (CharacterSnapshot x) => x.CanStealthKo, (x, v) => x with { CanStealthKo = v }),
        F(2083, "KidnapChance", (CharacterSnapshot x) => x.KidnapChance, (x, v) => x with { KidnapChance = v }),
        F(2084, "LockpickSkill", (CharacterSnapshot x) => x.LockpickSkill, (x, v) => x with { LockpickSkill = v }),
        F(2085, "Animal", (CharacterSnapshot x) => x.Animal, (x, v) => x with { Animal = v }),
        F(2086, "HostileToPlayer", (CharacterSnapshot x) => x.HostileToPlayer, (x, v) => x with { HostileToPlayer = v }),
        F(2087, "CanOpenInventory", (CharacterSnapshot x) => x.CanOpenInventory, (x, v) => x with { CanOpenInventory = v }),
        new(2088, "Prison", (_, x) => GameHoursCountdownCodec.EncodePrison(x.Prison, 0.0),
            (_, x, bytes) => x with { Prison = GameHoursCountdownCodec.ProjectPrison(GameHoursCountdownCodec.DecodePrison(bytes), 0.0) }),
        F(2089, "RangedLine", (CharacterSnapshot x) => x.RangedLine, (x, v) => x with { RangedLine = v }),
        F(2090, "BedRecoveryRate", (CharacterSnapshot x) => x.BedRecoveryRate, (x, v) => x with { BedRecoveryRate = v }),
        F(2091, "InPlayerBuilding", (CharacterSnapshot x) => x.InPlayerBuilding, (x, v) => x with { InPlayerBuilding = v }),
        F(2092, "Watchers", (CharacterSnapshot x) => x.Watchers, (x, v) => x with { Watchers = v }),
        F(2093, "FirstSelected", (CharacterSnapshot x) => x.FirstSelected, (x, v) => x with { FirstSelected = v }),
        F(2094, "KidnapVictim", (CharacterSnapshot x) => x.KidnapVictim, (x, v) => x with { KidnapVictim = v }),
        F(2095, "Lean", (CharacterSnapshot x) => x.Lean, (x, v) => x with { Lean = v }),
        F(2096, "Anchor", (CharacterSnapshot x) => x.Anchor, (x, v) => x with { Anchor = v }),
        new(2097, "Meters", (_, x) => ModPresentationCodec.EncodeMeterBars(x.Meters),
            (_, x, bytes) => x with { Meters = ModPresentationCodec.DecodeMeterBars(bytes).Values }),
        new(2098, "ModSkills", (_, x) => ModPresentationCodec.EncodeSkills(x.ModSkills),
            (_, x, bytes) => x with { ModSkills = ModPresentationCodec.DecodeSkills(bytes) }),
        new(2099, "BoneMeshes", (_, x) => ModPresentationCodec.EncodeBoneMeshes(x.BoneMeshes),
            (_, x, bytes) => x with { BoneMeshes = ModPresentationCodec.DecodeBoneMeshes(bytes) }),
        new(2101, "LookAt", (_, x) => LookAtCodec.Encode(x.LookAt),
            (_, x, bytes) => x with { LookAt = LookAtCodec.Decode(bytes) }),
        new(2102, "Effects", (_, x) => StatusEffectPresentationCodec.EncodeTimes(x.Effects, 0, 1f / 30),
            (_, x, bytes) => x with { Effects = StatusEffectPresentationCodec.DecodeWithoutDefinitions(bytes) }),
        new(2104, "LastingId", (_, x) => CharacterIdentityCodec.EncodeLastingId(x.LastingId),
            (_, x, bytes) => x with { LastingId = CharacterIdentityCodec.DecodeLastingId(bytes) }),
        new(2105, "TagHeight", (_, x) => CharacterIdentityCodec.EncodeTagHeight(x.TagHeight),
            (_, x, bytes) => x with { TagHeight = CharacterIdentityCodec.DecodeTagHeight(bytes) }),
        F(2107, "Engineering", (CharacterSnapshot x) => x.Engineering, (x, v) => x with { Engineering = v }),
        F(2108, "Attacking", (CharacterSnapshot x) => x.Attacking, (x, v) => x with { Attacking = v }),
        F(2109, "HasMedkit", (CharacterSnapshot x) => x.HasMedkit, (x, v) => x with { HasMedkit = v }),
        F(2110, "HasSplintKit", (CharacterSnapshot x) => x.HasSplintKit, (x, v) => x with { HasSplintKit = v }),
        F(2111, "ShacklesOpen", (CharacterSnapshot x) => x.ShacklesOpen, (x, v) => x with { ShacklesOpen = v }),
    ];


    /// <summary>
    /// Only the declared JSON codecs use JSON compression. Events have their own bounded encoding; other binary fields keep their formats.
    /// </summary>
    static readonly IReadOnlySet<ushort> CompressedJsonFields = ViewFields.Where(f => f.CompressedJson).Select(f => f.Id)
        .Concat(SnapshotFields.Where(f => f.CompressedJson).Select(f => f.Id))
        .Concat(CharacterFields.Where(f => f.CompressedJson).Select(f => f.Id)).ToHashSet();

    static GameComponentSchema()
    {
        Coverage<PlayerWorldView>(ViewFields.Select(f => f.Name), "World Player ConnectionGeneration Publication GameHours TimeScale SimulationStepSeconds Snapshot");
        Coverage<WorldSnapshot>(SnapshotFields.Select(f => f.Name), "Tick StateRevision Characters");
        Coverage<CharacterSnapshot>(CharacterFields.Select(f => f.Name), "Id Position Yaw DrawYaw Animations");
    }

    static void Coverage<T>(IEnumerable<string> fields, string external)
    {
        var declared = fields.Concat(external.Split(' ')).Order(StringComparer.Ordinal);
        var actual = typeof(T).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal);
        if (!declared.SequenceEqual(actual))
            throw new InvalidOperationException($"Protocol component schema does not cover {typeof(T).Name}.");
    }

    /// <summary>
    /// One independently revisioned absolute value and whether its presence-wrapped JSON supports compression.
    /// </summary>
    internal sealed record Field<T>(ushort Id, string Name, Func<WorldViewCodec, T, byte[]> Encode,
        Func<WorldViewCodec, T, byte[], T> Apply, bool CompressedJson = false);

    /// <summary>
    /// The greater of the wire and expanded sizes, including the presence byte. Unknown or binary fields never interpret their bytes as JSON.
    /// </summary>
    internal static int ExpandedSize(ushort field, ReadOnlySpan<byte> bytes)
    {
        if (field == EffectDefinitionsField) return AbsolutePayloadCompression.ExpandedLength(bytes);
        if (field == 7) return ExpandedEvents(bytes);
        if (!CompressedJsonFields.Contains(field)) return bytes.Length;
        if (bytes.Length == 1 && bytes[0] == 0) return 1;
        if (bytes.Length <= 1 || bytes[0] != 1) throw new InvalidDataException("Invalid component value.");

        int expanded = JsonComponentCompression.DecodedLength(bytes[1..]);
        return Math.Max(bytes.Length, 1 + expanded);
    }

    /// <summary>
    /// The absolute event history keeps the presence wrapper and its original raw JSON retention charge.
    /// </summary>
    static Field<PlayerWorldView> Events() => new(7, "Events",
        (codec, source) => source.Events is { } value ? [1, .. PresentationEventsCodec.Encode(codec, value)] : [0],
        (codec, target, bytes) => target with
        {
            Events = bytes.Length == 1 && bytes[0] == 0 ? null!
                : bytes.Length > 1 && bytes[0] == 1 ? PresentationEventsCodec.Decode(codec, bytes.AsSpan(1))
                : throw new InvalidDataException("Invalid component value.")
        });

    /// <summary>
    /// Bounds the event envelope and expanded body before publication or reassembly can allocate their decoded representation.
    /// </summary>
    static int ExpandedEvents(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 1 && bytes[0] == 0) return 1;
        if (bytes.Length <= 1 || bytes[0] != 1) throw new InvalidDataException("Invalid component value.");
        return Math.Max(bytes.Length, 1 + PresentationEventsCodec.ExpandedSize(bytes[1..]));
    }

    /// <summary>
    /// A nullable absolute field: its presence byte stays outside the optional stateless JSON compression envelope.
    /// </summary>
    static Field<T> F<T, V>(ushort id, string name, Func<T, V> get, Func<T, V, T> set, bool compress = true) => new(id, name,
        (codec, source) => get(source) is { } value
            ? [1, .. (compress ? JsonComponentCompression.Encode(codec, value) : codec.EncodeComponent(value))] : [0],
        (codec, target, bytes) => bytes.Length == 1 && bytes[0] == 0 ? set(target, default!)
            : bytes.Length > 1 && bytes[0] == 1 ? set(target, JsonComponentCompression.Decode<V>(codec, bytes.AsSpan(1)))
            : throw new InvalidDataException("Invalid component value."), CompressedJson: true);
}
