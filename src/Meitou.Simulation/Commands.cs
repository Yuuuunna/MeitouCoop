using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;

namespace Meitou.Simulation;

/// <summary>
/// Something the player (or a test) tells the world, applied at the start of simulation tick <see cref="Tick"/>
/// (docs/simulation.md, "Threading": inputs are step 1 of a tick). Commands never touch the world directly, so the same commands
/// at the same ticks give the same world.
/// </summary>
public abstract record SimCommand
{
    /// <summary>
    /// The simulation tick the command applies at; a command for a tick already run applies at the next one.
    /// </summary>
    public required long Tick { get; init; }
    /// <summary>
    /// Assigned by trusted host dispatch, never taken from a client's serialized payload. Null denotes a local/internal command.
    /// </summary>
    public PlayerId? Issuer { get; internal init; }

    internal IReadOnlyList<CharacterControl> ControlGrants { get; init; } = [];

    internal ulong FactionGrantRevision { get; init; }

}

/// <summary>
/// Move order: the characters walk to <see cref="Target"/> (world units; the height is taken from the ground).
/// </summary>
public sealed record MoveOrder(IReadOnlyList<CharacterId> Characters, Vector3 Target) : OrderCommand;

/// <summary>
/// The context menu's Follow (order 31, <c>Task_Follow</c>; docs/game/ui-input.md "Context menu verbs"): the characters (the selection when empty)
/// keep a formation place behind <see cref="Target"/> until another order replaces it or the target is gone
/// (<see cref="CharacterCold.FollowOrder"/>). <b>Engine</b>: the squad follower's slot rule, not saved.
/// </summary>
public sealed record FollowOrder(IReadOnlyList<CharacterId> Characters, CharacterId Target) : OrderCommand
{
    /// <summary>
    /// Bodyguard (order 45) rather than Follow (31): both fight an attacker of the target within 40 of it (state 16), a bodyguard also its own
    /// attacker within 40 (state 39; <see cref="CharacterCold.Guarding"/>, <c>RetaliationSystem.Guards</c>, docs/game/ai-tasks.md "Precedence"); the
    /// Bodyguard job's follow is the same row.
    /// </summary>
    public bool Guard { get; init; }
}

/// <summary>
/// Sandbox only: fixes a character's speed mode (null: leave) and/or its walking speed in units per second (null: leave; 0: the speed chain's again).
/// The animation sandbox sends it, the game never does.
/// </summary>
public sealed record SandboxMovement(CharacterId Character, byte? Mode, float? Speed) : SimCommand;

/// <summary>
/// Commands waiting for their tick. Any thread may <see cref="Enqueue"/> (the UI runs on the host's thread); the simulation takes
/// the due ones with <see cref="TakeDue"/> in a fixed order: by tick, then in the order they were queued.
/// </summary>
public sealed class CommandQueue
{
    /// <summary>
    /// Guards <see cref="_pending"/> and <see cref="_sequence"/>.
    /// </summary>
    readonly Lock _gate = new();
    /// <summary>
    /// The commands waiting, each with its place in the queue.
    /// </summary>
    readonly List<(SimCommand Command, long Sequence)> _pending = [];
    /// <summary>
    /// The place the next command gets.
    /// </summary>
    long _sequence;

    /// <summary>
    /// The number of commands waiting.
    /// </summary>
    public int Count
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>
    /// Queues a command for its tick (any thread).
    /// </summary>
    public void Enqueue(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Issuer is not null && !PlayerCommandRouting.Supports(command))
            throw new NotSupportedException($"Attributed {command.GetType().Name} has not been migrated to player-scoped execution.");
        lock (_gate) _pending.Add((command, _sequence++));
    }

    /// <summary>
    /// Removes and returns the commands whose tick is at or before <paramref name="tick"/>, oldest tick first, then queue order.
    /// </summary>
    public IReadOnlyList<SimCommand> TakeDue(long tick, Func<SimCommand, bool>? include = null)
    {
        List<(SimCommand Command, long Sequence)>? due = null;
        lock (_gate)
        {
            if (_pending.Count == 0) return [];
            int kept = 0;
            for (int k = 0; k < _pending.Count; k++)
            {
                var p = _pending[k];
                if (p.Command.Tick <= tick && (include is null || include(p.Command))) (due ??= []).Add(p);
                else _pending[kept++] = p;
            }

            if (due is null) return [];
            _pending.RemoveRange(kept, _pending.Count - kept);
        }

        due.Sort(static (a, b) => a.Command.Tick != b.Command.Tick ? a.Command.Tick.CompareTo(b.Command.Tick) : a.Sequence.CompareTo(b.Sequence));
        var commands = new SimCommand[due.Count];
        for (int k = 0; k < commands.Length; k++) commands[k] = due[k].Command;
        return commands;
    }

    /// <summary>
    /// Trusted host dispatch after authentication. Gameplay systems still check permissions at execution.
    /// </summary>
    public void Enqueue(PlayerId issuer, SimCommand command)
    {
        if (issuer.IsNone) throw new ArgumentException("A player identity is required.", nameof(issuer));
        ArgumentNullException.ThrowIfNull(command);
        if (_world is null) throw new InvalidOperationException("Attributed commands require a world-owned queue.");
        var grants = new List<CharacterControl>();
        foreach (var id in PlayerCommandRouting.Characters(command))
            if (_world.Players.ControlOf(id) is { } grant && grant.Player == issuer) grants.Add(grant);
        int faction = PlayerCommandRouting.Faction(_world, command);
        ulong factionRevision = faction >= 0 ? _world.Players.Find(issuer)?.MembershipRevision(faction) ?? 0 : 0;
        Enqueue(command with { Issuer = issuer, ControlGrants = grants.ToArray(), FactionGrantRevision = factionRevision });
    }

    internal CommandQueue(World world) => this._world = world;

    public CommandQueue() { }

    readonly World? _world;

}

/// <summary>
/// Tells the world where the player is looking (the camera focus), which zones to keep active and whose town residents to load
/// (docs/game/game-loop.md "Zones": the game activates zones around the player's characters; until there is a player, the camera).
/// </summary>
public sealed record FocusCommand(Vector3 Position) : SimCommand;

/// <summary>
/// Selects the player's own characters. No valid targets retains the selection; otherwise non-additive replaces it and additive (shift) adds to it.
/// <paramref name="Toggle"/> (shift on a single character's click, portrait or number key, <c>FUN_1407f9000</c> / <c>FUN_1407f8f60</c>): a character
/// already selected leaves the selection unless it is the only one; one not selected joins it.
/// </summary>
public sealed record SelectCommand(IReadOnlyList<CharacterId> Characters, bool Additive = false, bool Toggle = false) : SimCommand
{
    /// <summary>
    /// With no characters and not additive: empty the selection (agent-playtest control channel, <c>select --none</c>); the game's own empty selects
    /// change nothing.
    /// </summary>
    public bool Clear { get; init; }
}

/// <summary>
/// Clears order selection and shows another faction's character in the HUD panels. <see cref="CharacterId.None"/> clears inspection only.
/// </summary>
public sealed record InspectCommand(CharacterId Target) : SimCommand;

/// <summary>
/// The player's characters the pinned inventory windows show: the snapshot carries their item grids while they are not selected. Display only.
/// </summary>
public sealed record ShowInventoryCommand(IReadOnlyList<CharacterId> Targets) : SimCommand;

/// <summary>
/// The building the HUD panels show (docs/game/ui-screens.md "Selected building"): the snapshot then carries its doors' state. A null instance clears
/// it. Display only.
/// </summary>
public sealed record InspectBuildingCommand(string? Instance) : SimCommand;

/// <summary>
/// The map was closed: towns lose their recently-discovered mark (<see cref="TownKnowledge.Recent"/>).
/// </summary>
public sealed record SeenDiscoveriesCommand : SimCommand;

/// <summary>
/// Stop: the characters (the selection when the list is empty) drop their orders and stand.
/// </summary>
public sealed record StopCommand(IReadOnlyList<CharacterId> Characters) : SimCommand;

/// <summary>
/// The navmesh changed (a zone became ready, a door moved): characters on the way ask for their path again (those with a go-to in hand).
/// Paths made while a zone was still building came from the open-ground stand-in and may cross buildings.
/// With <see cref="Zones"/>, only those whose path still to walk crosses one of them, and those whose answer is on its way; null: all.
/// </summary>
public sealed record RepathCommand : SimCommand
{
    /// <summary>
    /// The zones whose crossers ask again (null: everyone on the way).
    /// </summary>
    public IReadOnlySet<ZoneCoordinate>? Zones { get; init; }
}

/// <summary>
/// An operation on a specific faction. An attributed command must name its faction explicitly.
/// </summary>
public abstract record FactionCommand : SimCommand
{
    public int Faction { get; init; } = -1;
}



/// <summary>
/// Fail closed until each command's handlers support explicit actors; this is not the network protocol allowlist.
/// </summary>
public static class PlayerCommandRouting
{
    public static IReadOnlyList<CharacterId> Characters(SimCommand command) => command switch
    {
        MoveOrder c => c.Characters,
        FollowOrder c => c.Characters,
        StopCommand c => c.Characters,
        SelectCommand c => c.Characters,
        Combat.AttackOrder c => c.Attackers,
        Combat.ShootAtOrder c => c.Gunners,
        Trading.OpenTrade c => [c.Customer],
        Dialogue.TalkOrder c => [c.Talker],
        Dialogue.AreaArrival c => [c.Leader],
        FirstAid.TreatOrder c => c.Medics,
        FirstAid.SplintOrder c => c.Medics,
        FirstAid.RestOrder c => c.Characters,
        FirstAid.MedicJobCommand c => c.Characters,
        Editing.EditCharacter c => [c.Character],
        Carrying.PickUpOrder c => c.Carriers,
        Carrying.PutDownOrder c => c.Carriers,
        Carrying.LootOrder c => c.Looters,
        Carrying.OpenLootOrder c => c.Looters,
        Carrying.StealthKoOrder c => c.Attackers,
        Carrying.RescueJobCommand c => c.Characters,
        Carrying.LootTransferCommand c => [c.Actor],
        Carrying.CloseLoot c => [c.Actor],
        Crime.PutInCageOrder c => c.Carriers,
        Crime.PayBailOrder c => [c.Payer],
        Locks.PickLockOrder c => c.Characters,
        Locks.GetOutOrder c => c.Characters,
        Locks.DoorOrder c => c.Characters,
        Locks.DoorLockOrder c => c.Characters,
        Locks.SetFreeOrder c => c.Characters,
        Locks.GetInCageOrder c => c.Characters,
        Locks.ForceLockOrder c => c.Characters,
        Locks.ShackleOrder c => c.Characters,
        Locks.PutShacklesOrder c => c.Characters,
        Locks.UseBedOrder c => c.Characters,
        Locks.PutInBedOrder c => c.Carriers,
        Items.HeldCommand c => [.. new[] { c is Items.LiftItem lift ? lift.Actor : c.HolderActor }.Concat(c.ControlledCharacters).Distinct()],
        Trading.OpenPlayerTrade c => [c.Actor],
        Trading.AcceptPlayerTrade c => [c.Actor],
        Locks.BuyBuilding c => [c.Buyer],
        MoveToSquadCommand c => c.SwapWith is { } other ? [c.Character, other] : [c.Character],
        DismissCommand c => [c.Character],
        Items.TransferInventoryItem c => [c.From, c.To],
        Items.PickUpItemOrder c => c.Characters,
        ShowInventoryCommand c => c.Targets,
        Items.OpenContainerOrder c => c.Characters,
        Production.PutInMachine c => [c.Character],
        Production.TakeFromMachine c => [c.Character],
        Production.RemoveJob c => [c.Character],
        Production.MoveJob c => [c.Character],
        Production.AddOrderJob c => c.Characters,
        Production.ProspectOrder c => [c.Character],
        Production.OperateOrder c => c.Characters,
        Production.UseTurretOrder c => c.Characters,
        Production.AddJob c => c.Characters,
        Production.DisposeCorpseOrder c => c.Characters,
        Construction.BuildOrder c => c.Characters,
        Construction.DismantleOrder c => c.Characters,
        Items.ArrangeInventory c => [c.Character],
        Items.QuickMoveItem c => c.Into.IsNone ? [c.Character] : [c.Character, c.Into],
        Items.ReadMapItem c => [c.Character],
        OrdersCommand c => c.Characters,
        RunSpeedCommand c => c.Characters,
        StandUpCommand c => c.Characters,
        ManoeuvreCommand c => c.Characters,
        _ => [],
    };
    public static CommandFactionScope FactionScope(SimCommand command) => command switch
    {
        FactionCommand c => new(CommandFactionSource.Explicit, Faction: c.Faction),
        Construction.ConstructionSiteCommand c => new(CommandFactionSource.ConstructionRoot, Site: c switch
        {
            Construction.BuildOrder q => q.Site,
            Construction.DismantleOrder q => q.Site,
            Construction.ConfirmDismantle q => q.Site,
            Construction.CancelSite q => q.Site,
            Construction.PauseSite q => q.Site,
            Construction.UpgradeSite q => q.Site,
            Construction.DowngradeSite q => q.Site,
            Construction.SetBuildingPublic q => q.Building,
            _ => "",
        }),
        Items.OpenContainerOrder c => new(CommandFactionSource.Container, Site: c.Fixture),
        Production.PutInMachine c => new(CommandFactionSource.WorkSite, Site: c.Site),
        Production.TakeFromMachine c => new(CommandFactionSource.WorkSite, Site: c.Site),
        Production.SiteWorkCommand c => new(CommandFactionSource.WorkSite, Site: c switch
        {
            Production.OperateOrder q => q.Site,
            Production.UseTurretOrder q => q.Site,
            Production.AddJob q => q.Site,
            Production.DisposeCorpseOrder q => q.Site,
            _ => "",
        }),
        Production.MachineControlCommand c => new(CommandFactionSource.WorkSite, Site: c switch
        {
            Production.QueueCraft q => q.Site,
            Production.CancelCraft q => q.Site,
            Production.RepeatCrafts q => q.Site,
            Production.SetMachinePower q => q.Site,
            Production.IncinerateFurnace q => q.Site,
            _ => "",
        }),
        Trading.OpenTrade c => new(CommandFactionSource.Character, Character: c.Customer),
        Dialogue.TalkOrder c => new(CommandFactionSource.Character, Character: c.Talker),
        Dialogue.AreaArrival c => new(CommandFactionSource.Character, Character: c.Leader),
        Items.LiftItem { From.Holder: Items.ItemHolder.Site } c => new(CommandFactionSource.WorkSite, Site: c.From.Site),
        Items.PlaceHeld { Into.Holder: Items.ItemHolder.Site } c => new(CommandFactionSource.WorkSite, Site: c.Into.Site),
        Trading.OpenPlayerTrade c => new(CommandFactionSource.Explicit, Faction: c.PaymentFaction),
        Trading.AcceptPlayerTrade c => new(CommandFactionSource.Explicit, Faction: c.PaymentFaction),
        Locks.PickLockOrder or Locks.GetOutOrder or Locks.DoorOrder or Locks.DoorLockOrder or Locks.SetFreeOrder or Locks.GetInCageOrder
            or Locks.ForceLockOrder or Locks.ShackleOrder or Locks.PutShacklesOrder or Locks.UseBedOrder or Locks.PutInBedOrder
            or Crime.PutInCageOrder or Crime.PayBailOrder
            => new(CommandFactionSource.Character, Character: Characters(command).FirstOrDefault()),
        _ => new(CommandFactionSource.None),
    };
    internal static int Faction(World world, SimCommand command)
    {
        var scope = FactionScope(command);
        return scope.Source switch
        {
            CommandFactionSource.Explicit => scope.Faction,
            CommandFactionSource.Character when world.Characters.TryResolveNext(scope.Character, out int slot) => world.Characters.Cold(slot)!.Faction,
            CommandFactionSource.ConstructionRoot => Construction.ConstructionSystem.Root(world.Buildings, world.Buildings.Find(scope.Site!))?.Owner ?? world.Fixtures.DoorOwner(scope.Site!),
            CommandFactionSource.Container => world.Fixtures.Find(scope.Site!)?.Owner ?? -1,
            CommandFactionSource.WorkSite => world.SystemOf<Production.ProductionSystem>()?.StorageOwner(world, scope.Site!) ?? -1,
            _ => -1,
        };
    }
    internal static bool Supports(SimCommand command) => command is
        Production.QueueResearch or Production.CancelResearch or MoveOrder or FollowOrder or StopCommand or SelectCommand
        or Combat.AttackOrder or Combat.ShootAtOrder or Trading.OpenTrade or Trading.BuyItem or Trading.SellItem or Trading.CloseTrade or Trading.ArrangeStock
        or Items.ArrangeInventory or Items.QuickMoveItem or Items.ReadMapItem or Items.TransferInventoryItem or OrdersCommand or RunSpeedCommand or StandUpCommand or ManoeuvreCommand
        or Dialogue.AreaArrival or Dialogue.TalkOrder or Dialogue.DialogueChoice or Dialogue.EndConversation or Dialogue.ConfirmCharacterTrade or Dialogue.CloseCharacterTrade
        or NewSquadCommand or RenameSquadCommand or RemoveSquadCommand or MoveToSquadCommand or ReorderSquadCommand or ActiveSquadCommand or DismissCommand
        or RenameFactionCommand or SquadFormationCommand or SquadAiCommand or Items.OpenContainerOrder or Items.CloseContainer
        or Production.PutInMachine or Production.TakeFromMachine or Production.QueueCraft or Production.CancelCraft
        or Production.RepeatCrafts or Production.SetMachinePower or Production.IncinerateFurnace
        or Production.RemoveJob or Production.MoveJob or Production.AddOrderJob or Production.ProspectOrder
        or Production.OperateOrder or Production.UseTurretOrder or Production.AddJob
        or Construction.BuildOrder or Construction.DismantleOrder or Construction.ConfirmDismantle or Construction.CancelSite
        or Construction.PauseSite or Construction.UpgradeSite or Construction.DowngradeSite or Construction.SetBuildingPublic
        or Construction.RenameTown or Construction.SetTownPublic or Construction.PlaceBuildings or Locks.BuyBuilding
        or FirstAid.TreatOrder or FirstAid.SplintOrder or FirstAid.RestOrder or FirstAid.MedicJobCommand or Editing.EditCharacter
        or Carrying.PickUpOrder or Carrying.PutDownOrder or Carrying.LootOrder or Carrying.OpenLootOrder
        or Carrying.StealthKoOrder or Carrying.RescueJobCommand or Carrying.TakeLootItem or Carrying.PutLootItem
        or Carrying.CloseLoot or Production.DisposeCorpseOrder or Crime.PutInCageOrder or Crime.PayBailOrder
        or Locks.PickLockOrder or Locks.GetOutOrder or Locks.DoorOrder or Locks.DoorLockOrder or Locks.SetFreeOrder or Locks.GetInCageOrder
        or Locks.ForceLockOrder or Locks.ShackleOrder or Locks.PutShacklesOrder or Locks.UseBedOrder or Locks.PutInBedOrder
        or Locks.ToggleDoorsOrder or Locks.ToggleDoorLocksOrder
        or Items.LiftItem or Items.PlaceHeld or Items.GiveHeld or Items.DropHeld or Items.ReturnHeld
        or Items.PickUpItemOrder
        or PointedCommand or InspectCommand or InspectBuildingCommand or ShowInventoryCommand
        or Trading.OpenPlayerTrade or Trading.AcceptPlayerTrade or Trading.RevisePlayerTrade or Trading.ConfirmPlayerTrade or Trading.CancelPlayerTrade;
}



/// <summary>
/// Recipient-scoped pointer hint; grants no control or observation rights.
/// </summary>
public sealed record PointedCommand(CharacterId Target) : SimCommand;