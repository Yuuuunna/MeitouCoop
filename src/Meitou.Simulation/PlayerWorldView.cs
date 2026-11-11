using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;
using Meitou.Simulation.Weather;

namespace Meitou.Simulation;

/// <summary>
/// Server-owned observation policy, independent of simulation activation and client camera distance.
/// </summary>
public sealed record PlayerObservationRules(float Radius = WorldLayout.ZoneSize)
{
    internal void Validate()
    {
        if (!float.IsFinite(Radius) || Radius <= 0 || Radius > WorldLayout.ZoneSize * 2)
            throw new ArgumentOutOfRangeException(nameof(Radius));
    }
}

/// <summary>
/// Optional bandwidth filter. Requested zones can reduce authorized observation, never enlarge it.
/// </summary>
public sealed record PlayerViewRequest(IReadOnlyList<ZoneCoordinate>? Zones = null);
public sealed record PlayerRoleView(EntityId Entity, CharacterId Character, int Faction, ulong Revision, bool Available, bool Dead)
{
    public PlayerRoleAvailability Availability { get; init; } = Available ? PlayerRoleAvailability.Available : Dead ? PlayerRoleAvailability.Dead : PlayerRoleAvailability.Unknown;
}
public sealed record PlayerFactionView(int Faction, string Name, FactionPermission Permissions, ulong MembershipRevision,
    long? Money, ResearchSnapshot Research, IReadOnlyList<SquadView> Squads)
{
    public int Formation { get; init; } = (int)SquadFormation.Random;
    public IReadOnlySet<string> UnlockedBuildings { get; init; } = new HashSet<string>();
    public IReadOnlyList<FactionStandingView> Standings { get; init; } = [];
    public IReadOnlyList<FactionInfoView> Contracts { get; init; } = [];
    public IReadOnlyList<FactionInfoView> UpcomingEvents { get; init; } = [];
}

/// <summary>
/// A detached recipient-specific projection. It must not be broadcast to another connection.
/// </summary>
public sealed record PlayerWorldView(Guid World, PlayerId Player, ulong ConnectionGeneration, ulong Publication, double GameHours,
    WorldSnapshot Snapshot, IReadOnlyList<PlayerRoleView> Roles, IReadOnlyList<PlayerFactionView> Factions)
{
    public GameplayRules? AuthorityRules { get; init; }
    public int ActiveFaction { get; init; } = -1;
    /// <summary>
    /// The host's authoritative clock speed; supplied at publication by the runtime owner.
    /// </summary>
    public double TimeScale { get; init; } = 1;
    public float SimulationStepSeconds { get; init; } = 1f / 30;
    public ProspectReport? Prospected { get; init; }
    public PresentationEventBatch Events { get; init; } = PresentationEventBatch.Empty;
    public PlayerMessageBatch Messages { get; init; } = PlayerMessageBatch.Empty;
    public IReadOnlyList<FactionIdentityView> FactionCatalogue { get; init; } = [];
    public WeatherFrame? Weather { get; init; }
    public Meitou.Data.Particles.WeatherEffectFrame? WeatherEffects { get; init; }
}

public sealed partial class World
{
    internal PlayerWorldView CapturePlayerView(AuthorityConnection connection, PlayerViewRequest request, PlayerObservationRules rules)
    {
        rules.Validate();
        if (request.Zones is { Count: > 256 } || request.Zones?.Any(z => !z.IsInsideGrid) == true)
            throw new ArgumentException("Invalid observation zone filter.", nameof(request));
        var zones = request.Zones?.ToHashSet();
        var population = SystemOf<PopulationSystem>()?.Data;
        var player = Players.Find(connection.Player)!;
        var state = player.State;
        var owned = new HashSet<CharacterId>();
        var roles = new List<PlayerRoleView>();
        var anchors = new List<Vector3>();
        foreach (var grant in Players.KnownRoles(connection.Player))
        {
            bool loaded = Entities.TryResolve(grant.Entity, out var id);
            var cold = loaded ? Characters.Cold(id.Slot) : null;
            bool available = Players.Validate(grant);
            var location = Players.LocateRole(connection.Player, grant.Entity);
            roles.Add(new(grant.Entity, id, grant.Faction, grant.Revision, available, location?.Availability == PlayerRoleAvailability.Dead)
            { Availability = location?.Availability ?? PlayerRoleAvailability.Unavailable });
            if (cold is not null && cold.Faction == grant.Faction && player.IsMember(grant.Faction)) owned.Add(id);
            if (!available) continue;
            // Captivity/unconsciousness does not create a new spawn or disable the owner's observation.
            if (ObservationAnchor(grant) is { } anchor) anchors.Add(anchor);
        }
        float radiusSquared = rules.Radius * rules.Radius;
        bool Nearby(Vector3 position) => InsideObservation(position, zones, anchors, radiusSquared);
        bool VisibleEffect(Meitou.Data.Particles.EffectNodeSnapshot node)
        {
            double reach = rules.Radius + (double)Math.Max(node.AffectRadius, node.Radius ?? 0);
            return InsideObservation(node.Position, zones, anchors, (float)Math.Min(float.MaxValue, reach * reach));
        }
        bool Manage(int faction) => player.HasPermission(faction, FactionPermission.ManageAssets);
        bool Include(int slot) => owned.Contains(Characters.IdOf(slot)) || Nearby(Characters.Current[slot].Position);
        var loot = SystemOf<Carrying.CarrySystem>()?.OpenLoot(this, connection.Player);
        var characters = PublishCharacters(state, Include, id => owned.Contains(id) || loot?.Body == id, fullDetails: true, recipient: connection.Player);
        var visible = characters.Select(c => c.Id).ToHashSet();
        for (int i = 0; i < characters.Length; i++)
        {
            var character = characters[i];
            if (!owned.Contains(character.Id)) character = PublicCharacter(character) with
            {
                ItemGrid = loot?.Body == character.Id ? character.ItemGrid : null,
                ClosedSlots = loot?.Body == character.Id ? character.ClosedSlots : [],
                Prison = character.Prison,
                NoLootGuilt = character.NoLootGuilt,
                StealthKoChance = character.StealthKoChance,
                KidnapChance = character.KidnapChance,
            };
            characters[i] = character with
            {
                CarriedBy = visible.Contains(character.CarriedBy) ? character.CarriedBy : CharacterId.None,
                Carrying = visible.Contains(character.Carrying) ? character.Carrying : CharacterId.None,
                WantsToTalk = Characters.Cold(character.Id.Slot) is { IsPlayer: false } && Players.ControlOf(character.Id) is null &&
                    _dialogues?.HasInterrupt(character.Id, connection.Player) == true,
                Controller = Players.ControlOf(character.Id)?.Player,
            };
        }
        var factions = player.Memberships.Select(member =>
        {
            var resource = Factions.Find(member.Key);
            bool allContracts = player.HasPermission(member.Key, FactionPermission.ManageFaction | FactionPermission.ManageSquads);
            bool events = player.HasPermission(member.Key, FactionPermission.ManageFaction) || Manage(member.Key);
            string? FactionName(int index) => DescribeFaction(index)?.Name;
            var squads = SquadManagement.PlayerSquads(this, member.Key)
                .Where(s => player.HasPermission(member.Key, FactionPermission.ManageSquads) || s.Members.Any(owned.Contains))
                .Select(s => new SquadView(s.Id, s.Name, s.Members.Where(id => owned.Contains(id) || player.HasPermission(member.Key, FactionPermission.ManageSquads)).ToArray())).ToArray();
            return new PlayerFactionView(member.Key, DescribeFaction(member.Key)?.Name ?? "", member.Value, player.MembershipRevision(member.Key),
                player.HasPermission(member.Key, FactionPermission.ReadFunds) ? resource?.Money ?? 0 : null,
                _production?.ResearchView(this, member.Key, connection.Player) ?? ResearchSnapshot.Empty, squads)
            {
                Formation = SquadAiKeys.FormationOf(this, member.Key, null),
                UnlockedBuildings = resource?.Research.Unlocked.ToHashSet(StringComparer.Ordinal) ?? [],
                Contracts = ActiveContracts(FactionName, member.Key, id => allContracts || owned.Contains(id)).Select(FactionInfoView.From).ToArray(),
                UpcomingEvents = events ? (SystemOf<PopulationSystem>()?.UpcomingEvents(this, FactionName, member.Key) ?? []).Select(FactionInfoView.From).ToArray() : []
            };
        }).ToArray();
        var buildings = Buildings.Published(BuildingRevision).Where(b => Nearby(b.Position) || Manage(b.Owner))
            .Select(b => (Manage(b.Owner) ? b : b with { Materials = [] }) with
            { AuthorityFaction = Construction.ConstructionSystem.Root(Buildings, Buildings.Find(b.Id))?.Owner ?? -1 }).ToArray();
        var managedMachines = _production is null ? [] : Machines.All.Where(m => Manage(_production.StorageOwner(this, m.Site)))
            .Select(m => m.Site).ToHashSet(StringComparer.Ordinal);
        var machines = _production is null ? [] : Machines.All.Where(m => managedMachines.Contains(m.Site) || Nearby(m.Position))
            .Select(m => managedMachines.Contains(m.Site) ? MachineSnapshot.Of(m, _production.Items) with
            {
                StorageRevision = MachineRevision(m),
                ControlRevision = MachineControlRevision(m),
                SiteRevision = _production.WorkSiteRevision(this, m.Site),
            } : PublicMachine(MachineSnapshot.Of(m, _production.Items))).ToArray();
        var openContainer = PublishContainer(connection.Player);
        if (openContainer is not null && (!visible.Contains(openContainer.ContainerOpener)
            || !Players.CanControl(connection.Player, openContainer.ContainerOpener))) openContainer = null;
        if (openContainer is not null) machines = [.. machines.Where(m => m.Site != openContainer.Site), openContainer];
        machines = machines.Select(m => m with { AuthorityFaction = _production?.StorageOwner(this, m.Site) ?? -1 }).ToArray();
        var doors = Fixtures.DoorPicks.Where(d => Nearby(d.Position) || Manage(d.Owner)).ToArray();
        var doorKeys = doors.Select(d => (d.Building, d.Door)).ToHashSet();
        var activeFaction = factions.FirstOrDefault(f => f.Faction == state.Faction);
        var trade = _trades?.PublishTrade(this, connection.Player);
        // A cached interaction view may predate a grant revocation. Never publish it using identity alone.
        if (trade is not null && !Players.CanControl(connection.Player, trade.Customer)) trade = null;
        var conversation = _dialogues?.PublishConversation(this, connection.Player);
        if (conversation is not null && !Players.CanControl(connection.Player, conversation.Player)) conversation = null;
        var characterTrade = _dialogues?.PublishCharacterTrade(this, connection.Player);
        if (characterTrade is not null && !Players.CanControl(connection.Player, characterTrade.Player)) characterTrade = null;
        var dialogueRequest = _dialogues?.PublishRequest(this, connection.Player);
        if (dialogueRequest is not null && !Players.CanControl(connection.Player, dialogueRequest.Player)) dialogueRequest = null;
        var lead = state.Selection.LastOrDefault(id => owned.Contains(id) && visible.Contains(id), CharacterId.None);
        var verbs = lead.IsNone ? [] : characters.Select(c => VerbFactsOf(lead, c.Id, state.Faction) is { } facts ? new CharacterVerbView(c.Id, facts) : null)
            .OfType<CharacterVerbView>().ToArray();
        var arrival = _dialogues?.ArrivalResultFor(connection.Player) ?? default;
        var snapshot = new WorldSnapshot(Tick, characters)
        {
            TutorialFacts = PublishTutorialFacts(population, owned),
            FoliageOwnedTowns = population?.Sites.Where(s =>
                (uint)s.Index < (uint)population!.SiteFactions.Count
                && IsPlayerFaction(population!.SiteFactions[s.Index]) && (Nearby(s.Position) || Manage(population!.SiteFactions[s.Index])))
                .Select(s => FoliageWorld.TownKey(s.Position.X, s.Position.Z)).Distinct().Select(k => new FoliageTownOwnership(k.Item1, k.Item2)).ToArray() ?? [],
            VerbLead = lead,
            Verbs = verbs,
            ArrivalSerial = arrival.Serial,
            ArrivalForget = arrival.Forget,
            StateRevision = StateRevision,
            Trade = trade,
            Conversation = conversation,
            CharacterTrade = characterTrade,
            DialogueRequest = dialogueRequest,
            OpenLoot = loot?.Body ?? CharacterId.None,
            LootOpener = loot?.Looter ?? CharacterId.None,
            LootSessionId = state.LootSessionId,
            ContainerOpener = openContainer?.ContainerOpener ?? CharacterId.None,
            Held = state.Held is { ControlGrant: { } heldGrant } held && heldGrant.Player == connection.Player && Players.Validate(heldGrant)
                && (held.SourceControl is not { } sourceGrant || Players.Validate(sourceGrant))
                ? Items.HeldItemView.Of(held) : null,
            HeldSerial = state.HeldSerial,
            PlayerTrade = PlayerTrades.Publish(this, connection.Player),
            Projectiles = (_projectiles?.PublishProjectiles() ?? []).Where(p => Nearby(p.Position)).Select(p => p with { Shooter = visible.Contains(p.Shooter) ? p.Shooter : CharacterId.None }).ToArray(),
            Speech = PublishSpeech().Where(s => visible.Contains(s.Speaker)).ToArray(),
            Towns = state.Towns.ToArray(),
            Camps = state.Camps.ToArray(),
            KnownFactions = state.KnownFactions.ToHashSet(),
            SquadAi = state.SquadAi,
            PlayerSquads = activeFaction?.Squads ?? [],
            ActiveSquad = activeFaction?.Squads.Any(s => s.Id == state.Squad) == true ? state.Squad : -1,
            Buildings = buildings,
            BuildingsRevision = Buildings.Revision,
            NestBuildings = NestBuildings.Published().Where(b => Nearby(b.Position)).Select(b => b with { Materials = [] }).ToArray(),
            NestMeshes = NestBuildings.Meshes().Where(m => Nearby(m.Position)).ToArray(),
            NestBuildingsRevision = NestBuildings.Revision,
            Ruins = Fixtures.PublishedRuins().Where(r => Nearby(r.Building.Position)).ToArray(),
            RuinsRevision = Fixtures.RuinsRevision,
            EmptiedHouses = Fixtures.PublishedEmptied().Where(b => Nearby(b.Position)).ToArray(),
            EmptiedHousesRevision = Fixtures.EmptiedRevision,
            Dummies = Fixtures.TrainingSpots.Where(d => Nearby(d.Position) || Manage(d.Owner)).Select(d => d with { SiteRevision = _production?.WorkSiteRevision(this, d.Id) ?? 0 }).ToArray(),
            Seats = Fixtures.SeatSpots.Where(d => Nearby(d.Position) || Manage(d.Owner)).Select(d => d with { SiteRevision = _production?.WorkSiteRevision(this, d.Id) ?? 0 }).ToArray(),
            GroundItems = GroundItems.Published().Where(g => Nearby(g.Position)).ToArray(),
            GroundItemsRevision = GroundItems.Revision,
            Doors = Fixtures.PublishedDoors().Where(p => doorKeys.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value),
            DoorPicks = doors,
            DoorGroupRevisions = doors.Select(d => d.Building).Distinct().ToDictionary(b => b, Fixtures.DoorGroupRevision, StringComparer.Ordinal),
            Beds = Fixtures.BedSpots.Where(b => Nearby(b.Position) || Manage(b.Owner)).ToArray(),
            Containers = Fixtures.ContainerSpots.Where(c => Nearby(c.Position) || Manage(c.Owner)).ToArray(),
            Cages = Fixtures.CageSpots(Characters).Where(c => Nearby(c.Position) || Manage(c.Owner))
                .Select(c => c with { Occupant = visible.Contains(c.Occupant) ? c.Occupant : CharacterId.None }).ToArray(),
            OwnedTownBuildings = player.Memberships.Where(m => Manage(m.Key)).SelectMany(m => Fixtures.OwnedProperties(m.Key)).ToHashSet(StringComparer.Ordinal),
            Machines = machines,
            MachinesVersion = Machines.Version,
            Research = activeFaction?.Research ?? ResearchSnapshot.Empty,
            UnlockedBuildings = activeFaction is not null ? Factions.Find(activeFaction.Faction)!.Research.Unlocked.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(),
            Outposts = (_production?.OutpostView(this) ?? []).Where(o => Manage(o.Owner)).ToArray(),
            Smiths = (_production?.SmithsView(this) ?? new Dictionary<string, CraftSmithView>())
                .Where(p => managedMachines.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Crafts = (_production?.CraftsView(this) ?? new Dictionary<string, IReadOnlyList<CraftOptionView>>())
                .Where(p => managedMachines.Contains(p.Key)).ToDictionary(p => p.Key, p => (IReadOnlyList<CraftOptionView>)p.Value.ToArray(), StringComparer.Ordinal),
        };
        if (state.InspectedBuilding is { } inspected && (buildings.Any(b => b.Id == inspected) || doors.Any(d => d.Building == inspected)
            || snapshot.Beds.Any(b => b.Id == inspected) || snapshot.Cages.Any(c => c.Id == inspected) || snapshot.Containers.Any(c => c.Id == inspected)))
        {
            var detail = SystemOf<Locks.BedSystem>()?.DetailOf(this, inspected, state.Faction) ?? Fixtures.DetailOf(inspected, state.Faction);
            if (detail is not null) snapshot = snapshot with
            {
                InspectedBuilding = detail with
                { Owned = Manage(Fixtures.OfBuilding(inspected).FirstOrDefault()?.Owner ?? -1) }
            };
        }
        var catalogue = CaptureFactionCatalogue(state, player.Memberships.Select(m => m.Key), snapshot);
        factions = factions.Select(f => f with { Standings = catalogue.Select(c => DescribeStanding(f.Faction, c.Faction)).OfType<FactionStandingView>().ToArray() }).ToArray();
        return new(connection.World, connection.Player, connection.Generation, 0, GameHours, snapshot, roles.ToArray(), factions)
        {
            AuthorityRules = GameplayRules.Capture(this),
            ActiveFaction = activeFaction?.Faction ?? -1,
            Prospected = state.Prospected,
            SimulationStepSeconds = TickSeconds,
            Events = _presentationEvents.Capture(connection.Player, visible, Nearby),
            Messages = state.Messages.Capture(),
            FactionCatalogue = catalogue,
            WeatherEffects = Weather is IWeatherPresentationSource effects ? new(effects.CaptureEffects().Nodes.Where(VisibleEffect).ToArray())
                    : Weather is null ? Meitou.Data.Particles.WeatherEffectFrame.Empty : null,
            Weather = Weather is IWeatherPresentationSource source ? source.CaptureWeather() : Weather is null
                    ? new WeatherFrame(WeatherTime.FromHours(GameHours), []) { Enabled = false } : null
        };
    }

    // Public character fields are opt-in. Adding a private field to the local snapshot never exposes it here.
    static CharacterSnapshot PublicCharacter(CharacterSnapshot c) => new(c.Id, c.Appearance, c.Position, c.Yaw, c.Animations)
    {
        DrawYaw = c.DrawYaw,
        RecordId = c.RecordId,
        DrawDistanceScale = c.DrawDistanceScale,
        Knockdown = c.Knockdown,
        IsDiplomat = c.IsDiplomat,
        Down = c.Down,
        Conscious = c.Conscious,
        Standing = c.Standing,
        Animal = c.Animal,
        CanOpenInventory = c.CanOpenInventory,
        CanStealthKo = c.CanStealthKo,
        FightsPlayer = c.FightsPlayer,
        HostileToPlayer = c.HostileToPlayer,
        CanBeShackled = c.CanBeShackled,
        ShacklesLocked = c.ShacklesLocked,
        Faction = c.Faction,
        Name = c.Name,
        IsTrader = c.IsTrader,
        Inspected = c.Inspected,
        Skills = c.Inspected ? c.Skills : null,
        Hud = c.Inspected ? c.Hud : null,
        HealthParts = c.Inspected ? c.HealthParts : [],
        Bounties = c.Inspected ? c.Bounties : [],
        Slave = c.Inspected ? c.Slave : null,
        Disguise = c.Inspected ? c.Disguise : null,
        Status = c.Inspected ? c.Status : null,
        Activity = c.Inspected ? c.Activity : null,
        RangedLine = c.Inspected ? c.RangedLine : null,
        BedRecoveryRate = c.Inspected ? c.BedRecoveryRate : null,
        WeatherNotice = c.Inspected ? c.WeatherNotice : WeatherNotice.None,
        WeatherNoticeProtection = c.Inspected ? c.WeatherNoticeProtection : 0,
        Orders = c.Inspected ? c.Orders & CharacterOrders.Block : CharacterOrders.None,
        WeaponDrawn = c.WeaponDrawn,
        RangedDrawn = c.RangedDrawn,
        BackDrawn = c.BackDrawn,
        CrossbowShots = c.CrossbowShots,
        StuckBolts = c.StuckBolts,
        HasFlies = c.HasFlies,
        Build = c.Build,
        LimbBits = c.LimbBits,
        BloodAmounts = c.BloodAmounts,
        CarriedBy = c.CarriedBy,
        Carrying = c.Carrying,
        Carry = c.Carry,
        HeldTool = c.HeldTool,
        Scale = c.Scale,
        Task = c.Task,
        Body = c.Body is { } body ? body with { MedicJob = false, RescueJob = false } : null,
        InBed = c.InBed,
        Imprisoned = c.Imprisoned,
        StayingLow = c.StayingLow,
        InCombat = c.InCombat,
        CombatMode = c.CombatMode,
        Asleep = c.Asleep,
        Shackled = c.Shackled,
        PortraitSerial = c.PortraitSerial,
        Ragdoll = c.Ragdoll,
    };

    static MachineSnapshot PublicMachine(MachineSnapshot m) => new(m.Site, m.Record, [], null, null, m.Farm, m.Status, 0, 0)
    {
        Sound = m.Sound,
        Rotation = m.Rotation,
        Wind = m.Wind,
        Broken = m.Broken,
    };
}
