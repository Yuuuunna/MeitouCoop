using System.Numerics;
using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation;

public sealed partial class World
{
    public OrderEntry BindOrder(SimCommand command, OrderEntry entry)
    {
        entry.SourceCommand = command;
        return entry;
    }

    public bool ValidOrder(int slot, OrderEntry entry) => !entry.ApplicabilityQuery && (entry.SourceCommand is { } source
        ? MayOrder(source, Characters.IdOf(slot)) && (source.Issuer is null || source.ControlGrants.All(Players.Validate))
        : !Players.RequiresExplicitControl || Characters.Cold(slot) is not { IsPlayer: true });

    internal bool ValidOrderOrQuery(int slot, OrderEntry entry) => entry.ApplicabilityQuery || ValidOrder(slot, entry);

    /// <summary>
    /// Mechanical applicability of an order between ticks: repertoire, owner preconditions and threat rules.
    /// This query grants no authority; actual submission and execution independently validate the issuer's captured grants.
    /// </summary>
    public (bool Can, string? Why) CanOrder(OrderEntry order, CharacterId actor)
    {
        if (Characters.Phase != TablePhase.Outside || !Characters.TryResolveNext(actor, out int slot) || Characters.Cold(slot) is null) return (false, null);
        var query = new OrderEntry(order.Row, order.Owner)
        {
            Target = order.Target, Site = order.Site, Point = order.Point, Detail = order.Detail,
            SourceCommand = order.SourceCommand, ApplicabilityQuery = true,
        };
        var check = PlayerOrders.CheckApplicability(this, slot, query);
        return (check.CanRun, check.Why);
    }

    /// <summary>
    /// <see cref="CanOrder(OrderEntry, CharacterId)"/> for an order row given by its parts (a target character, a site or fixture id, a point) as the menu's verbs give them; false without a reason for a row no
    /// system owns as an order. The variants an owner tells apart by its detail (loot against open inventory, a lock order in place or on a walk) are read as their common case.
    /// </summary>
    public (bool Can, string? Why) CanOrder(TaskType row, CharacterId actor, CharacterId target = default, string? site = null, Vector3 point = default)
    {
        var entry = OrderOwnerOf(row) is { } owner ? new OrderEntry(row, owner) { Target = target, Site = site, Point = point, Detail = owner is Locks.LockSystem locks ? locks.WorkDetail() : null } : null;
        return entry is null ? (false, null) : CanOrder(entry, actor);
    }

    /// <summary>The system whose <see cref="IOrderOwner"/> checks and starts the order row (the one each owner gives its orders with), or null when none does.</summary>
    internal IOrderOwner? OrderOwnerOf(TaskType row) => row switch
    {
        TaskType.MOVE_CUS_ORDERED or TaskType.STAY_CLOSE_TO_TARGET or TaskType.BODYGUARD => SystemOf<MovementSystem>(),
        TaskType.LIFT_PERSON_PLAYER_ORDER or TaskType.KIDNAP_ORDER or TaskType.LOOT_TARGET or TaskType.STEALTH_KNOCKOUT or TaskType.PUT_DOWN_OBJECT => SystemOf<Carrying.CarrySystem>(),
        TaskType.FOCUSED_MELEE_ATTACK or TaskType.UNPROVOKED_FOCUSED_MELEE_ATTACK => SystemOf<Combat.CombatSystem>(),
        TaskType.BUILD or TaskType.REPAIR or TaskType.DISMANTLE => SystemOf<Construction.ConstructionSystem>(),
        TaskType.OPERATE_MACHINERY or TaskType.OPERATE_AUTOMATIC_MACHINERY or TaskType.OPERATE_STORAGE or TaskType.USE_TRAINING_DUMMY or TaskType.USE_TURRET or TaskType.PROSPECTING or TaskType.FEED_CORPSE_INTO_MACHINE => SystemOf<Production.ProductionSystem>(),
        TaskType.FIRST_AID_ORDER or TaskType.SPLINT_ORDER or TaskType.REST => SystemOf<FirstAid.FirstAidSystem>(),
        TaskType.PICKUP => SystemOf<PlayerSystem>(),
        TaskType.USE_BED or TaskType.PUT_SOMEONE_IN_BED => SystemOf<Locks.BedSystem>(),
        TaskType.PUT_IN_CAGE => SystemOf<Crime.ArrestSystem>(),
        _ => Locks.LockSystem.OwnsRow(row) ? SystemOf<Locks.LockSystem>() : null,
    };
}
