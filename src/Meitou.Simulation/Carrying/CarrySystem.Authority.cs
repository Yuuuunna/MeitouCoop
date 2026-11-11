using Meitou.Simulation.Ai;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Carrying;

public sealed partial class CarrySystem
{
    readonly Dictionary<CharacterId, (SimCommand Command, CharacterId Target, CharacterTask Task)> _issuedTasks = [];
    readonly Dictionary<PlayerId, CharacterControl> _lootGrants = [];

    void Remember(World world, int slot, SimCommand command)
    {
        if (command.Issuer is null) return;
        var table = world.Characters;
        _issuedTasks[table.IdOf(slot)] = (command, table.Cold(slot)!.CarryTarget, (CharacterTask)table.Next[slot].Task);
        if (command is OpenLootOrder && world.Players.ControlOf(table.IdOf(slot)) is { } grant)
            _lootGrants[command.Issuer.Value] = grant;
    }

    void RememberJob(World world, int slot)
    {
        if (!world.Players.RequiresExplicitControl || world.Players.ControlOf(world.Characters.IdOf(slot)) is not { } grant) return;
        Remember(world, slot, new RescueJobCommand([world.Characters.IdOf(slot)])
        { Tick = world.Tick, Issuer = grant.Player, ControlGrants = [grant] });
    }

    void ValidateTasks(World world)
    {
        foreach (var (actor, issued) in _issuedTasks.ToArray())
        {
            if (!world.Characters.TryResolveNext(actor, out int slot)) { _issuedTasks.Remove(actor); continue; }
            var cold = world.Characters.Cold(slot)!;
            if ((CharacterTask)world.Characters.Next[slot].Task != issued.Task || cold.CarryTarget != issued.Target)
            { _issuedTasks.Remove(actor); continue; }
            if (world.MayOrder(issued.Command, actor)) continue;
            // End the instruction, preserving any body already attached to the carrier.
            End(world, slot);
            _issuedTasks.Remove(actor);
        }
    }

    PlayerState? OpeningPlayer(World world, int slot)
    {
        var actor = world.Characters.IdOf(slot);
        var player = world.PlayerOf(actor);
        if (player is null || player.LootWalker != actor || player.OpeningLoot != world.Characters.Cold(slot)!.CarryTarget) return null;
        if (world.Players.RequiresExplicitControl && (world.Players.ControlOf(actor) is not { } current
            || !_lootGrants.TryGetValue(current.Player, out var original) || original != current || !world.Players.Validate(original))) return null;
        return player;
    }

    bool ValidTransfer(World world, LootTransferCommand command, int actor, int target)
    {
        if (command.Issuer is null) return true;
        var table = world.Characters;
        if (!world.MayOrder(command, table.IdOf(actor)) || command.Actor != table.IdOf(actor) || command.Target != table.IdOf(target)
            || world.CommandPlayer(command) is not { } player || player.LootSessionId != command.SessionId || command.SessionId <= 0) return false;
        var source = command is TakeLootItem ? table.Cold(target)!.Inventory! : table.Cold(actor)!.Inventory!;
        var destination = command is TakeLootItem ? table.Cold(actor)!.Inventory! : table.Cold(target)!.Inventory!;
        return command.InventoryRevision != 0 && command.DestinationRevision != 0
            && world.InventoryRevision(source) == command.InventoryRevision && world.InventoryRevision(destination) == command.DestinationRevision;
    }

    public bool ValidateHeldSession(World world, PlayerId player, long session, CharacterId actor, CharacterId body) =>
        world.Players.Find(player)?.State.LootSessionId == session && session > 0
        && OpenLoot(world, player) is { } pair && pair.Looter == actor && pair.Body == body;

    bool CanLiftFor(World world, int carrier, int body)
    {
        var target = world.Characters.Cold(body)!;
        return CanBeLifted(target) || world.Characters.Cold(carrier)!.Snatching && Snatchable(target) || InOpenCage(world, target) is not null || AwakeMate(target)
            && (!world.Players.RequiresExplicitControl || world.Players.ControlOf(world.Characters.IdOf(carrier)) is { } grant
                && world.Players.CanControl(grant.Player, world.Characters.IdOf(body)));
    }

    static bool OpenableFor(World world, int actor, int target)
    {
        var table = world.Characters;
        var body = table.Cold(target)!;
        if (CanBeLooted(body)) return true;
        if (body.Inventory is null) return false;
        if (!world.Players.RequiresExplicitControl) return Openable(table, target);
        return world.Players.ControlOf(table.IdOf(actor)) is { } grant && world.Players.CanControl(grant.Player, table.IdOf(target))
            || body.CarriedBy.IsNone && (body.IsImprisoned || Asleep(table, target));
    }

    internal void CancelInstruction(World world, int slot) => End(world, slot);

    public void ClosePlayerSession(World world, PlayerId player)
    {
        if (world.Players.Find(player)?.State is { } state)
        {
            state.OpenLoot = state.OpeningLoot = CharacterId.None;
            state.LootOpener = state.LootWalker = CharacterId.None;
            state.LootSessionId++;
        }
        _lootGrants.Remove(player);
        _seenLoot.Remove(player);
        foreach (var (actor, issued) in _issuedTasks.ToArray())
        {
            if (issued.Command.Issuer != player) continue;
            if (world.Characters.TryResolveNext(actor, out int slot) && world.Characters.Cold(slot)!.CarryTarget == issued.Target
                && (CharacterTask)world.Characters.Next[slot].Task == issued.Task) End(world, slot);
            _issuedTasks.Remove(actor);
        }
    }

    static void PostActor(World world, int slot, string message)
    {
        if (world.Players.ControlOf(world.Characters.IdOf(slot)) is { } grant) world.Post(grant.Player, message);
        else if (!world.Players.RequiresExplicitControl) world.Post(message);
    }

    void Cash(World world, ItemInstance item, int units, int actor)
    {
        long cats = (long)(Data?.Items.Info(item.Record)?.Value ?? 0) * units;
        world.Factions.GetOrCreate(world.Characters.Cold(actor)!.Faction).Money += cats;
        PostActor(world, actor, $"{cats} cats added.");
    }
}