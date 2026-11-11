namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>Serialized host teardown; a replaced connection cannot close its replacement's interaction windows or stop its roles.</summary>
    public void ReleasePlayerConnection(AuthorityConnection connection)
    {
        if (!AtTickBoundary) throw new InvalidOperationException("Connection cleanup requires the world boundary.");
        if (Authority.GetPhase(connection) is null) return;
        try { if (!HasPendingDurableMutation) ResetPlayerInteractions(connection.Player); }
        finally { Authority.Close(connection); }
    }

    /// <summary>Trusted owner resets a newly authenticated player's transient windows before replacing its connection.</summary>
    public void ResetPlayerInteractions(PlayerId id)
    {
        if (!AtTickBoundary || HasPendingDurableMutation) throw new InvalidOperationException("Interaction cleanup requires an available world boundary.");
        if (Players.Find(id) is { } player)
        {
            Characters.BeginTick(); Characters.Phase = TablePhase.Inputs;
            try
            {
                foreach (var trade in systems.OfType<Trading.TradeSystem>()) trade.CloseForPlayer(this, id);
                foreach (var dialogue in systems.OfType<Dialogue.DialogueSystem>()) dialogue.CloseForPlayer(this, id);
                foreach (var carry in systems.OfType<Carrying.CarrySystem>()) carry.ClosePlayerSession(this, id);
                foreach (var producer in systems.OfType<Production.ProductionSystem>()) producer.CloseCorpseErrands(this, id);
                Editing.CharacterEditing.CloseForPlayer(this, id);
                foreach (var playerSystem in systems.OfType<PlayerSystem>()) playerSystem.ReturnHeldItem(this, id);
                PlayerTrades.Disconnect(id);
                PlayerSystem.CloseContainer(player.State);
                player.State.OpenLoot = player.State.LootOpener = player.State.LootWalker = player.State.OpeningLoot = CharacterId.None;
                StateRevision = checked(StateRevision + 1);
            }
            finally { Characters.Phase = TablePhase.Outside; Characters.EndTick(); }
        }
    }

    /// <summary>Serialized input/tick boundaries for the active host's external services; unbound candidate worlds have no subscribers.</summary>
    public event Action<World>? RuntimeBoundary;

    internal void ApplyAuthorityCommand(SimCommand command)
    {
        if (Characters.Phase != TablePhase.Outside) throw new InvalidOperationException("Authority requires a tick boundary.");
        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try
        {
            IReadOnlyList<SimCommand> single = [command];
            PlayerTrades.Inputs(this, single);
            foreach (var system in systems) system.Inputs(this, single);
            StateRevision = checked(StateRevision + 1);
        }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
    }

    /// <summary>Invalidates affected routes; only additions that leave existing navigation unchanged may retain and splice their old route.</summary>
    public void InvalidateNavigation(IReadOnlySet<Meitou.Data.World.ZoneCoordinate> zones, bool onlyAddedZones = false)
    {
        if (Characters.Phase is not (TablePhase.Outside or TablePhase.Inputs)) throw new InvalidOperationException("Navigation changes require a serial world boundary.");
        bool copy = Characters.Phase == TablePhase.Outside;
        if (copy) Characters.BeginTick();
        try { foreach (var movement in systems.OfType<MovementSystem>()) movement.InvalidatePaths(this, zones, onlyAddedZones); }
        finally { if (copy) Characters.EndTick(); }
    }
    readonly Items.InventoryVersions inventoryVersions = new();
    readonly Production.MachineVersions machineVersions = new();
    readonly Production.JobVersions jobVersions = new();
    readonly Construction.SiteVersions siteVersions = new();
    readonly Construction.TownVersions townVersions = new();
    public ulong InventoryRevision(Items.Inventory inventory) => inventoryVersions.Read(inventory);
    public ulong MachineRevision(Production.Machine machine) => machineVersions.Read(this, machine);
    public ulong MachineControlRevision(Production.Machine machine) => machineVersions.ReadControl(machine);
    public ulong JobsRevision(CharacterId character) => Characters.TryResolveNext(character, out int slot) ? jobVersions.Read(Characters.Cold(slot)!) : 0;
    public ulong BuildingRevision(Construction.BuildSite site) => siteVersions.Read(site);
    public ulong TownRevision(Construction.PlayerTown town) => townVersions.Read(town);

    /// <summary>Continuous player work uses the current controller's rights; disconnected players keep their grants.</summary>
    internal bool MayUseAssets(CharacterCold worker, int owner)
    {
        if (!Players.RequiresExplicitControl || !worker.IsPlayer) return true;
        return Players.KnownControl(worker.Entity) is { } grant && Players.Validate(grant)
            && (owner < 0 || Players.Find(grant.Player)?.HasPermission(owner, FactionPermission.ManageAssets) == true);
    }

    internal bool InventoryMatches(Items.InventoryCommand command, Items.Inventory inventory) =>
        command.Issuer is null && command.InventoryRevision == 0 || command.InventoryRevision != 0 && InventoryRevision(inventory) == command.InventoryRevision;

    public bool IsPlayerFaction(int faction) => faction >= 0 &&
        (Player.Faction == faction || Factions.Find(faction)?.PlayerOwned == true);

    internal IReadOnlyList<int> PlayerFactionIndices() => [.. Factions.All.Where(f => f.PlayerOwned).Select(f => f.Index)
        .Concat(Player.Faction >= 0 ? [Player.Faction] : Array.Empty<int>()).Distinct().Order()];

    internal bool MayManage(SimCommand command, int faction, FactionPermission permission) =>
        command.Issuer is not { } issuer || faction >= 0 && Players.Find(issuer)?.HasPermission(faction, permission) == true;

    internal PlayerState? CommandPlayer(SimCommand command) => command.Issuer is { } issuer ? Players.Find(issuer)?.State : Player;

    internal bool MayOrder(SimCommand command, CharacterId character) =>
        command.Issuer is not { } issuer || command.ControlGrants.Any(grant => grant.Player == issuer && Players.Validate(grant)
            && Entities.TryResolve(grant.Entity, out var current) && current == character);

    /// <summary>Attributed orders name every character; an invalid grant rejects the whole group. Legacy/internal orders may use local selection.</summary>
    internal IReadOnlyList<CharacterId> CommandCharacters(SimCommand command, IReadOnlyList<CharacterId> characters)
    {
        if (command.Issuer is null) return characters.Count > 0 ? characters : Player.Selection.ToArray();
        return characters.Count > 0 && characters.All(id => MayOrder(command, id)) ? characters : [];
    }

    internal PlayerState? PlayerOf(CharacterId character)
    {
        if (Players.ControlOf(character) is { } grant) return Players.Find(grant.Player)?.State;
        return !Players.RequiresExplicitControl && Characters.TryResolveNext(character, out int slot) &&
            Characters.Cold(slot) is { IsPlayer: true } cold && cold.Faction == Player.Faction ? Player : null;
    }

    internal PlayerId? NoticeOwner(CharacterCold? cold) =>
        cold is not null && !cold.Entity.IsNone && Players.KnownControl(cold.Entity) is { } grant
            && grant.Faction == cold.Faction && Players.Find(grant.Player)?.IsMember(grant.Faction) == true
            ? grant.Player : null;

    internal void PostCharacter(CharacterCold? cold, string message)
    {
        if (NoticeOwner(cold) is { } owner) Post(owner, message);
        else if (!Players.RequiresExplicitControl) Post(message);
    }

    internal void PostCharacter(int slot, string message) => PostCharacter(Characters.Cold(slot), message);

    internal void PostCharacter(CharacterId character, string message) =>
        PostCharacter(Characters.TryResolveNext(character, out int slot) ? Characters.Cold(slot) : null, message);

    internal PlayerState? PlayerOf(CharacterCold character)
    {
        if (Players.KnownControl(character.Entity) is { } grant && Players.Validate(grant)
            && Entities.TryResolve(character.Entity, out var id) && ReferenceEquals(Characters.Cold(id.Slot), character))
            return Players.Find(grant.Player)?.State;
        return !Players.RequiresExplicitControl && character.IsPlayer && character.Faction == Player.Faction ? Player : null;
    }

    /// <summary>Offline controllers keep their preferences; unassigned roles use ordinary defaults.</summary>
    internal SquadAi SquadOptions(CharacterCold character) => !Players.RequiresExplicitControl ? Player.SquadAi
        : PlayerOf(character)?.SquadAi ?? SquadAi.Defaults;

    /// <summary>Resolve an explicit faction operation without creating faction state or borrowing another player's permissions.</summary>
    internal FactionState? CommandFaction(FactionCommand command, FactionPermission permission)
    {
        int faction = command.Faction;
        if (command.Issuer is not { } issuer)
            return faction < 0 ? Player.FactionState : Factions.Find(faction);
        return faction >= 0 && command.FactionGrantRevision != 0 && Players.Find(issuer) is { } player
            && player.MembershipRevision(faction) == command.FactionGrantRevision
            && (permission == FactionPermission.None ? player.IsMember(faction) : player.HasPermission(faction, permission)) ? Factions.Find(faction) : null;
    }

    /// <summary>
    /// Applies the interface's commands between ticks while paused, without advancing time or running simulation phases: selection, order toggles, job lists, crafting and
    /// research queues, squads, inventory, open-shop transactions, building settings, the Stop order and every order (<see cref="OrderCommand"/>: each goes into its characters'
    /// queues through the one order path, <see cref="PlayerOrders.Give"/>, and sets their tasks; nobody moves or fights until the next tick).
    /// </summary>
    public void ApplyPausedInterfaceCommands()
    {
        RequireNoDurableMutation();
        if (Characters.Phase != TablePhase.Outside) throw new InvalidOperationException("interface commands require a tick boundary");
        int authorityCount = Authority.ProcessPending();
        var due = Commands.TakeDue(Tick, c => c is OrderCommand or ManoeuvreCommand or SelectCommand or OrdersCommand or RunSpeedCommand or StandUpCommand or SquadCommand or SquadAiCommand or SquadFormationCommand or RenameFactionCommand or SeenDiscoveriesCommand
            or Production.OperateOrder or Production.UseTurretOrder or Production.AddJob or Production.AddOrderJob or Production.RemoveJob or Production.MoveJob or FirstAid.MedicJobCommand { Job: true } or Carrying.RescueJobCommand { Job: true }
            or Production.QueueCraft or Production.CancelCraft or Production.RepeatCrafts or Production.SetMachinePower or Production.IncinerateFurnace or Production.QueueResearch or Production.CancelResearch
            or Items.OpenContainerOrder or Items.CloseContainer or Locks.BuyBuilding or Construction.UpgradeSite or Construction.DowngradeSite or Construction.SetBuildingPublic or InspectCommand or InspectBuildingCommand or ShowInventoryCommand or Items.HeldCommand or Items.QuickMoveItem or Items.ReadMapItem or Items.ArrangeInventory or Items.TransferInventoryItem
            or Carrying.TakeLootItem or Carrying.PutLootItem or Production.PutInMachine or Production.TakeFromMachine
            or Trading.BuyItem or Trading.SellItem or Trading.ArrangeStock or Trading.CloseTrade or Dialogue.DialogueChoice or Dialogue.EndConversation or Dialogue.ConfirmCharacterTrade or Dialogue.CloseCharacterTrade
            or Construction.BuildOrder or Construction.DismantleOrder or Construction.RenameTown or Construction.SetTownPublic or Construction.PlaceBuildings or Construction.CancelSite or Construction.ConfirmDismantle or Construction.PauseSite or Editing.EditCharacter or Editing.FinishCreation
            or MoveOrder or FollowOrder or StopCommand or FirstAid.TreatOrder or FirstAid.SplintOrder or FirstAid.RestOrder or Combat.AttackOrder or Combat.ShootAtOrder
            or Locks.PickLockOrder or Locks.GetOutOrder or Locks.ShackleOrder or Locks.PutShacklesOrder or Locks.ToggleDoorsOrder or Locks.ToggleDoorLocksOrder
            or Locks.DoorOrder or Locks.SetFreeOrder or Locks.GetInCageOrder or Locks.ForceLockOrder or Locks.UseBedOrder or Locks.PutInBedOrder
            or Carrying.PickUpOrder or Carrying.PutDownOrder or Carrying.LootOrder or Carrying.OpenLootOrder or Carrying.StealthKoOrder
            or Items.PickUpItemOrder or Trading.OpenPlayerTrade or Trading.PlayerTradeCommand or Carrying.CloseLoot
            or Production.DisposeCorpseOrder or Crime.PutInCageOrder or Crime.PayBailOrder);
        if (due.Count == 0 && authorityCount == 0) { RuntimeBoundary?.Invoke(this); return; }
        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try
        {
            PlayerTrades.Inputs(this, due);
            foreach (var system in systems)
                if (due.Count > 0 && (system is IOrderOwner || system is (PlayerSystem or Carrying.CarrySystem or Production.ProductionSystem or Trading.TradeSystem or Construction.ConstructionSystem or Dialogue.DialogueSystem
                    or FirstAid.FirstAidSystem or Locks.LockSystem or Locks.BedSystem or Crime.ArrestSystem or MovementSystem or Combat.CombatSystem)))
                    system.Inputs(this, due);
            PlayerOrders.ValidateAuthority(this);
            Decisions.Run(this, paused: true);
            RuntimeBoundary?.Invoke(this);
            if (due.Count > 0) StateRevision = checked(StateRevision + 1);
        }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
        foreach (var trade in systems.OfType<Trading.TradeSystem>()) trade.RefreshView(this);
        foreach (var talk in systems.OfType<Dialogue.DialogueSystem>()) talk.RefreshView(this);
        if (Settings.PublishSnapshots)
        {
            var (previous, buffer) = (PreviousSnapshot, previousBuffer);
            Publish();
            KeepPrevious(previous, buffer);
        }
    }

    /// <summary>The first system of type <typeparamref name="T"/> this world runs, or null. Remembered per type (the finders ask once per candidate pair; any thread).</summary>
    internal T? SystemOf<T>() where T : class => (T?)systemsByType.GetOrAdd(typeof(T), static (_, list) => list.OfType<T>().FirstOrDefault(), systems);

    readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object?> systemsByType = new();

    /// <summary>
    /// Puts the item on the mouse back where it came from (<see cref="PlayerState.Held"/>, <see cref="PlayerSystem.ReturnHeldItem"/>): a save does this first, so the item is saved in its
    /// window. Only at a tick boundary; nothing when none is held.
    /// </summary>
    public void ReturnHeldItem()
    {
        if (Player.Held is null || Characters.Phase != TablePhase.Outside || SystemOf<PlayerSystem>() is not { } players) return;
        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try { players.ReturnHeldItem(this); StateRevision = checked(StateRevision + 1); }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
    }

    public void ReturnAllHeldItems()
    {
        if (!AtTickBoundary) throw new InvalidOperationException("Held item cleanup requires the world boundary.");
        if (!Players.All.Any(p => p.State.Held is not null) || SystemOf<PlayerSystem>() is not { } players) return;
        Characters.BeginTick(); Characters.Phase = TablePhase.Inputs;
        try { players.ReturnAllHeldItems(this); StateRevision = checked(StateRevision + 1); }
        finally { Characters.Phase = TablePhase.Outside; Characters.EndTick(); }
    }
}
