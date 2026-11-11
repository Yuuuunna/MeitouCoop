using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Player;

/// <summary>
/// The faction tab's rename (<c>FUN_1404923d0</c>, docs/game/ui-screens.md "Faction tab"): a name no faction has yet becomes the player faction's
/// name; a taken one is refused with "This faction name already exists" (an empty name is refused too, <b>engine choice</b>: the original's
/// empty-name rule is <b>Unknown</b>).
/// </summary>
public sealed record RenameFactionCommand(string Name) : FactionCommand;

/// <summary>
/// Applies the player's selection and stop commands (step 1 of the tick). Move orders are the movement system's. The item on the mouse is
/// <see cref="HeldItems"/>, a right click in an own window <see cref="QuickMoves"/>.
/// </summary>
public sealed partial class PlayerSystem(PopulationData? data = null) : ITickSystem, ITaskOwner
{
    /// <summary>
    /// Dresses the characters the debug cheats and the character creation change (null without game data).
    /// </summary>
    readonly CharacterAssembly? _assembly = data is null ? null : new(data);

    /// <summary>
    /// The item on the mouse.
    /// </summary>
    readonly HeldItems _held = new(data);

    /// <summary>
    /// The right clicks on items of the player's own windows.
    /// </summary>
    readonly QuickMoves _quickMoves = new(data);

    /// <summary>
    /// The pick-ups of loose items and the walks to containers (row 3 "Collecting item").
    /// </summary>
    readonly PickUps _pickUps = new();

    /// <summary>
    /// The game data the system was made with (null in data-free tests).
    /// </summary>
    internal PopulationData? Data => data;

    /// <summary>
    /// The owner of the pick-up and container orders (row 3) and their act-outs.
    /// </summary>
    internal PickUps PickUps => _pickUps;

    /// <summary>
    /// The player's animals take a pick-up order on a loose item into their bags (<see cref="PickUps.AnimalsPickUp"/>).
    /// </summary>
    public bool AnimalsPickUp
    {
        get => _pickUps.AnimalsPickUp;
        set => _pickUps.AnimalsPickUp = value;
    }

    #region Faction name

    /// <summary>
    /// The longest faction name taken (engine choice: the original's box limit is <b>Unknown</b>).
    /// </summary>
    public const int FactionNameLength = 64;

    /// <summary>
    /// The name the player faction goes by: the rename, else the data's.
    /// </summary>
    public string? PlayerFactionName(World world) =>
        world.Player.FactionName ?? (data is not null && world.Player.Faction >= 0
                && world.Player.Faction < data.Factions.Count ? data.Factions[world.Player.Faction].Name : null);

    /// <summary>
    /// Renames the player faction (<see cref="RenameFactionCommand"/>), cut to <see cref="FactionNameLength"/>.
    /// </summary>
    void RenameFaction(World world, RenameFactionCommand command)
    {
        if (world.CommandFaction(command, FactionPermission.ManageFaction) is not { Index: >= 0 } faction) return;
        string name = command.Name.Trim();   // FUN_1404923d0 keeps the text as typed (no trim)
        if (name.Length > FactionNameLength) name = name[..FactionNameLength];
        string? Original(int index) => data is not null && (uint)index < (uint)data.Factions.Count ? data.Factions[index].Name : null;
        if (name.Length == 0 || name == (faction.Name ?? Original(faction.Index))) return;
        // FUN_1402e7950: any other faction already named so. (The data's names; the player's own old name is free again.)
        var indices = (data is not null ? Enumerable.Range(0, data.Factions.Count) : Enumerable.Empty<int>()).Concat(world.Factions.All.Select(f => f.Index)).Distinct();
        if (indices.Any(i => i != faction.Index && (world.Factions.Find(i)?.Name ?? Original(i)) == name))
        {
            world.Post(command.Issuer ?? PlayerId.Local, "This faction name already exists");
            return;
        }
        faction.Name = name == Original(faction.Index) ? null : name;
    }

    #endregion

    #region Commands

    /// <summary>
    /// The characters a right-click order goes to (an empty list: the selection), null for any other command.
    /// </summary>
    static IReadOnlyList<CharacterId>? Ordered(SimCommand command) => command switch
    {
        MoveOrder o => o.Characters,
        FollowOrder o => o.Characters,
        AttackOrder o => o.Attackers,
        ShootAtOrder o => o.Gunners,
        Carrying.PickUpOrder o => o.Carriers,
        Carrying.PutDownOrder o => o.Carriers,
        Carrying.LootOrder o => o.Looters,
        Carrying.OpenLootOrder o => o.Looters,
        Carrying.StealthKoOrder o => o.Attackers,
        FirstAid.TreatOrder o => o.Medics,
        FirstAid.SplintOrder o => o.Medics,
        Locks.PickLockOrder o => o.Characters,
        Locks.UseBedOrder o => o.Characters,
        Production.OperateOrder o => o.Characters,
        Construction.BuildOrder o => o.Characters,
        // Every other right-click verb that names characters goes through the same dispatcher (FUN_1407f9e20 :96 sets the flag before any
        // order-specific code); only the job giver's orders (45, 0x111, 0xdf) skip it.
        Dialogue.TalkOrder o => [o.Talker],
        FirstAid.RestOrder o => o.Characters,
        Items.OpenContainerOrder o => o.Characters,
        Items.PickUpItemOrder o => o.Characters,
        Locks.DoorOrder o => o.Characters,
        Locks.DoorLockOrder o => o.Characters,
        Locks.ForceLockOrder o => o.Characters,
        Locks.PutShacklesOrder o => o.Characters,
        Locks.ShackleOrder o => o.Characters,
        Locks.SetFreeOrder o => o.Characters,
        Locks.GetInCageOrder o => o.Characters,
        Locks.GetOutOrder o => o.Characters,
        Locks.PutInBedOrder o => o.Carriers,
        Crime.PutInCageOrder o => o.Carriers,
        Construction.DismantleOrder o => o.Characters,
        Production.ProspectOrder o => [o.Character],
        _ => null,
    };

    /// <summary>
    /// <c>playerWantsMeToGetUp</c> (Character <c>+0xec</c>, docs/game/ragdolls.md "Staying low"): the order dispatch sets it on each selected
    /// character it reaches; one playing dead gets up (staying low when sneaking), one still getting up stands rather than staying low. Internal
    /// orders (an empty selection, or none of it) leave it.
    /// </summary>
    static void WantsToGetUp(World world, PlayerState player, SimCommand command, IReadOnlyList<CharacterId> ordered)
    {
        var table = world.Characters;
        foreach (var id in world.CommandCharacters(command, ordered))
        {
            if (command.Issuer is null && !player.Selection.Contains(id) || !table.TryResolveNext(id, out int slot)
                    || table.Cold(slot) is not { IsPlayer: true, Medical: { } medical } cold)
                continue;
            if (medical.PlayingDead)
            {
                bool low = Orders.Has(cold, CharacterOrders.Sneak);
                medical.StandUp(low);
                // Standing up (not staying low) on the order trains toughness when enemies are about.
                if (!low) world.SystemOf<BodySystem>()?.GotUpByOrder(world, slot, cold);
            }
            else if (medical.StayingLow && CharacterFacts.GettingUp(cold, world.Tick)) medical.StandUp();
        }
    }
    /// <summary>
    /// Applies each command against its authorized personal state.
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            if (world.CommandPlayer(command) is not { } player) continue;
            if (Ordered(command) is { } ordered) WantsToGetUp(world, player, command, ordered);
            if (!ApplyViewCommand(world, player, command)) ApplyActionCommand(world, player, command);
        }
    }
    /// <summary>
    /// Applies personal selection and window commands.
    /// </summary>
    bool ApplyViewCommand(World world, PlayerState player, SimCommand command)
    {
        var table = world.Characters;
        switch (command)
        {
            case SelectCommand select:
                Select(world, player, select);
                break;
            case SquadCommand squadCommand:
                SquadManagement.Apply(world, data, squadCommand);
                break;
            case SeenDiscoveriesCommand:
                player.SeeDiscoveries();
                break;
            case PointedCommand pointer:
                player.Pointed = table.TryResolveNext(pointer.Target, out _) ? pointer.Target : CharacterId.None;
                break;
            case InspectCommand inspect:
                player.Inspected = inspect.Target;
                if (table.TryResolveNext(inspect.Target, out int inspected)) player.Meet(table.Cold(inspected)!.Faction);
                break;
            case ShowInventoryCommand show:
                player.InventoryShown.Clear(); player.InventoryShown.AddRange(show.Targets);
                break;
            case Items.OpenContainerOrder open:
                ContainerAccess.OpenContainerInput(world, open, _pickUps);
                break;
            case Items.CloseContainer close:
                ContainerAccess.CloseContainerInput(world, close);
                break;
            case InspectBuildingCommand building:
                player.InspectedBuilding = building.Instance;
                // The original selects a building with a door as that door; Meitou keeps a building's owner on its doors and other fixtures.
                if (building.Instance is { } instance && world.Fixtures.OfBuilding(instance) is var fixtures)
                    player.Meet((fixtures.FirstOrDefault(f => f.Kind == Locks.FixtureKind.Door) ?? fixtures.FirstOrDefault())?.Owner ?? -1);
                break;
            default: return false;
        }
        return true;
    }
    /// <summary>
    /// Applies orders and transactions using the current command's grants.
    /// </summary>
    void ApplyActionCommand(World world, PlayerState player, SimCommand command)
    {
        var table = world.Characters;
        switch (command)
        {
            case StopCommand stop:
                Stop(world, stop);
                break;
            case Items.ReadMapItem read:
                ReadMap(world, read);
                break;
            case Items.HeldCommand held:
                _held.Input(world, held);
                break;
            case Items.QuickMoveItem quick:
                _quickMoves.QuickMove(world, quick);
                break;
            case Items.TransferInventoryItem transfer:
                InventoryTransfer.Transfer(world, transfer, _assembly, data);
                break;
            case RenameFactionCommand rename:
                RenameFaction(world, rename);
                break;
            case SquadAiCommand option:
                if (!SquadAiKeys.Keys.Any(pair => pair.Option == option.Option)) break;
                player.SquadAi = option.On ? player.SquadAi | option.Option : player.SquadAi & ~option.Option;
                break;
            case SquadFormationCommand formation:
                if (Enum.IsDefined(formation.Formation) && world.CommandFaction(formation, FactionPermission.ManageSquads) is { } formed)
                    SquadAiKeys.SetFormation(world, formed.Index, (int)formation.Formation, data?.Factions);
                break;
            case OrdersCommand or RunSpeedCommand or StandUpCommand or ManoeuvreCommand:
                Orders.Apply(world, command);
                break;
            case Editing.EditCharacter edit:
                if (data is not null) Editing.CharacterEditing.Apply(world, data, edit);
                break;
            case DebugCommand debug:
                DebugCheats.Apply(world, data, _assembly, debug);
                break;
            case Editing.FinishCreation:
                if (command.Issuer is not null || world.Players.RequiresExplicitControl) break;
                if (player.InCreation) Editing.CharacterEditing.EndCreation(world, _assembly);
                player.InCreation = false;
                break;
            case Items.PickUpItemOrder pick:
                _pickUps.PickUpInput(world, pick);
                break;
            case Items.ArrangeInventory arrange:
                if (world.MayOrder(arrange, arrange.Character) && table.TryResolveNext(arrange.Character, out int character)
                    && table.Cold(character) is { IsPlayer: true, Inventory: { } carried } && world.InventoryMatches(arrange, carried)
                    && !Items.InventoryCommands.Arrange(carried, arrange.Bag)) world.Post(arrange.Issuer ?? PlayerId.Local, "Cannot arrange inventory: the items do not fit.");
                break;
        }
    }
    /// <summary>
    /// Selects living characters allowed by the issuer's current control grants.
    /// </summary>
    void Select(World world, PlayerState player, SelectCommand select)
    {
        var table = world.Characters;
        var candidates = (select.Issuer is null ? select.Characters : world.CommandCharacters(select, select.Characters)).Where(id =>
            table.TryResolveNext(id, out int slot) && table.Cold(slot)!.IsPlayer).ToArray();
        if (candidates.Length == 0) return;
        // A non-additive select with any candidate clears the selection first; the gate then keeps out the dead (FUN_1407f3b50, FUN_1407f7f20).
        if (!select.Additive && !(select.Toggle && candidates.Length == 1 && player.Selection.Contains(candidates[0]))) player.Selection.Clear();
        var selectable = candidates.Where(id => !(table.TryResolveNext(id, out int living) && table.Cold(living)!.Medical is { Dead: true })).ToArray();
        if (selectable.Length == 0) return;
        // A shift-click on a selected character takes it out, never emptying the selection (it stays when it is the only one).
        if (select.Toggle && selectable.Length == 1 && player.Selection.Contains(selectable[0]))
        {
            if (player.Selection.Count >= 2) player.Selection.Remove(selectable[0]);
            return;
        }
        // Selecting meets the owner's faction (FUN_1407f7f20), here the player's.
        player.Meet(player.Faction);
        // Picking one of your own shows them: the inspected character of another faction no longer takes the HUD.
        player.Inspected = CharacterId.None;
        if (!select.Additive) player.Selection.Clear();
        foreach (var id in selectable)
            if (!player.Selection.Contains(id)) player.Selection.Add(id);
        // Selecting someone of another squad shows that squad (engine reading of the original's cached squad).
        if (!select.Additive && table.TryResolveNext(selectable[0], out int first) && world.Squads.Find(table.Cold(first)!.SquadId) is { } theirs)
        {
            if (select.Issuer is not null) player.Faction = theirs.Faction;
            if (theirs.Faction == player.Faction) player.Squad = theirs.Id;
        }
    }
    /// <summary>
    /// Clears the current issuer's authorized actors' orders.
    /// </summary>
    static void Stop(World world, StopCommand stop)
    {
        var table = world.Characters;
        foreach (var id in world.CommandCharacters(stop, stop.Characters))
        {
            if (!table.TryResolveNext(id, out int slot) || !table.Cold(slot)!.IsPlayer) continue;
            table.Cold(slot)!.OrderQueue.Clear();   // drops the queued orders too
            TaskStart.End(world, slot, TaskEnd.Stopped);
            table.Cold(slot)!.FollowOrder = CharacterId.None;
        }
    }

    #endregion

    #region Upkeep

    /// <summary>
    /// Registers the act-out of row 3 "Collecting item" (<see cref="PickUps"/>).
    /// </summary>
    public void RegisterTasks(TaskHandlers tasks) => tasks.Register(Meitou.Data.Gameplay.Ai.TaskType.Pickup, _pickUps);

    /// <summary>
    /// The serial upkeep: the debug cheats, burying the dead, the selection's and the active squad's upkeep, the pick-up and container walks and the
    /// held item's check.
    /// </summary>
    public void SlowWorld(World world)
    {
        DebugCheats.GodTick(world, data, _assembly);
        DebugCheats.GuardTick(world, data, _assembly);
        SquadManagement.BuryDead(world, data?.Db);
        world.Players.Prune();
        SquadManagement.KeepActive(world);
        _pickUps.PickUpWorkers(world, _assembly);
        ContainerAccess.ContainerWorkers(world);
        _held.KeepHeld(world);
    }

    #endregion

    #region Items and money

    /// <summary>
    /// The ITEM <c>item function</c> of money (the "Cats" item): a right click or a move into the player's window turns it into cats
    /// (<c>FUN_140714a20</c>).
    /// </summary>
    public const int MoneyFunction = 14;

    /// <summary>
    /// <paramref name="units"/> of the money item <paramref name="item"/> into the player's purse, "{N} cats added.", priced at the record's
    /// <c>value</c>.
    /// </summary>
    internal static void Cash(World world, PopulationData? data, ItemInstance item, int units)
    {
        long cats = (long)(data?.Items.Info(item.Record)?.Value ?? 0) * units;
        world.Player.Money += cats;
        world.Post($"{cats} cats added.");
    }

    /// <summary>
    /// Puts the item on the mouse back (<see cref="HeldItems.ReturnHeldItem"/>).
    /// </summary>
    internal void ReturnHeldItem(World world) => _held.ReturnHeldItem(world, PlayerId.Local);

    /// <summary>
    /// A map item of a player character's inventory: its towns become discovered and recently discovered, and it is used up; with nothing new it
    /// stays.
    /// </summary>
    void ReadMap(World world, Items.ReadMapItem read)
    {
        var table = world.Characters;
        if (data is null || world.CommandPlayer(read) is not { } player || !world.MayOrder(read, read.Character) || !table.TryResolveNext(read.Character, out int owner)
                || table.Cold(owner) is not { IsPlayer: true, Inventory: { } inventory }
            || !world.InventoryMatches(read, inventory))
            return;
        var t = read.Item;
        var item = inventory.All().ElementAtOrDefault(t.Index);
        if (item is null || !HeldItems.Matches(item, t)
                || item.Type != Meitou.Data.Fcs.FcsRecordType.MapItem
            || data.Db.Find(item.Record) is not { } record) return;
        var sites = Items.MapItems.Reveals(data, record, player.Towns, world.Seed);
        if (sites.Count == 0)
        {
            world.Post(read.Issuer ?? PlayerId.Local, "You already know all these locations, no new information can be learned.");
            return;
        }
        foreach (int site in sites) player.Know(site, TownKnowledge.Discovered | TownKnowledge.Recent);
        inventory.RemoveOne(item);
        world.Post(read.Issuer ?? PlayerId.Local, $"{sites.Count} location(s) added to the map.");
    }

    #endregion

    /// <summary>
    /// The reach of container use, shared with the extracted owner.
    /// </summary>
    public const float ContainerReach = ContainerAccess.ContainerReach;
    /// <summary>
    /// The floor slack of container use.
    /// </summary>
    public const float ContainerFloorSlack = ContainerAccess.ContainerFloorSlack;
    /// <summary>
    /// The trade interaction range.
    /// </summary>
    public const float TradingRange = ContainerAccess.TradingRange;
    /// <summary>
    /// The range of giving an item to another character.
    /// </summary>
    internal const float GiveRange = HeldItems.GiveRange;
    /// <summary>
    /// The range of stealing a held item.
    /// </summary>
    internal const float StealRange = HeldItems.StealRange;
    /// <summary>
    /// Returns a player's held custody before closing personal interactions.
    /// </summary>
    internal void ReturnHeldItem(World world, PlayerId player) => _held.ReturnHeldItem(world, player);
    /// <summary>
    /// Returns every player's held custody before save capture.
    /// </summary>
    internal void ReturnAllHeldItems(World world) => _held.ReturnAllHeldItems(world);
    /// <summary>
    /// Closes one player's container window and its access session.
    /// </summary>
    internal static void CloseContainer(PlayerState player) => ContainerAccess.CloseContainer(player);
    /// <summary>
    /// The inventory region behind a container window.
    /// </summary>
    internal static Items.Inventory GridOf(World world, Locks.Fixture fixture, out Items.GridRegion region) =>
        ContainerAccess.GridOf(world, fixture, out region);
    /// <summary>
    /// The inventory behind a container window.
    /// </summary>
    internal static Items.Inventory GridOf(World world, Locks.Fixture fixture) => ContainerAccess.GridOf(world, fixture);
}