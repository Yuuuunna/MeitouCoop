using System.Numerics;

namespace Meitou.Simulation.Items
{
    /// <summary>Pick up a ground item (<see cref="GroundItems"/> id): the nearest of the characters (the selection when empty) who may walks to it and puts it in its inventory.</summary>
    public sealed record PickUpItemOrder(IReadOnlyList<CharacterId> Characters, int Item) : OrderCommand
    {
        /// <summary>Alt was held (the original's steal modifier, <c>DAT_142133462</c>): another faction's item is taken only then; without it the order is the hint "Hold down the ALT key to steal items" and a walk to the item (<c>FUN_1407fc440</c>).</summary>
        public bool Alt { get; init; } = true;
    }
}

namespace Meitou.Simulation
{
    /// <summary>
    /// Picking up loose items (docs/simulation.md "Ground items"): walk to the item, then into the inventory (<see cref="Items.Inventory.Add"/>), or a message when it has no room. The
    /// pick-up and the walk to a container start through <see cref="TaskStart.Start"/> as row 3 "Collecting item", which the player system handles.
    /// </summary>
    public sealed partial class PlayerSystem : ITaskOwner, ITaskHandler, IOrderOwner
    {
        public void RegisterTasks(TaskHandlers tasks) => tasks.Register(Meitou.Data.Gameplay.Ai.TaskType.PICKUP, this);

        /// <summary>A pick-up of the ground item in <see cref="TaskRequest.Detail"/>, or the walk to the container <see cref="TaskRequest.Site"/> (to open it, or only to it: <see cref="ContainerWalk"/>).</summary>
        void ITaskHandler.Start(World world, int slot, in TaskRequest request)
        {
            var table = world.Characters;
            var walker = table.Cold(slot)!;
            var entry = request.Detail as OrderEntry;
            if (entry is not null && !world.ValidOrder(slot, entry)) return;
            var detail = entry?.Detail ?? request.Detail;

            if (detail is int item)
            {
                walker.PickingLock = null;
                walker.Shackles = null;
                walker.PickingUpItem = item;
                if (request.Task == CharacterTask.GoTo) MovementSystem.GoToFloor(world, slot, request.Point, walker.RunSpeed);
                return;
            }
            if (detail == ContainerWalk)
            {
                MovementSystem.GoToFloor(world, slot, request.Point, walker.RunSpeed);
                return;
            }
            var command = entry?.SourceCommand;
            var player = command is null ? world.PlayerOf(table.IdOf(slot)) : world.CommandPlayer(command);
            if (player is null || world.Fixtures.Find(request.Site!) is not { } fixture) return;
            var member = command?.Issuer is { } issuer ? world.Players.Find(issuer) : null;
            player.ContainerAccess = new(checked(++nextContainerSession), command?.Issuer, table.IdOf(slot), fixture, fixture.Owner,
                command?.Issuer is null ? null : world.Players.ControlOf(table.IdOf(slot)),
                member?.MembershipRevision(walker.Faction) ?? 0, member?.MembershipRevision(fixture.Owner) ?? 0);

            walker.PickingLock = null;
            walker.Shackles = null;
            walker.PickingUpItem = null;
            player.OpenContainer = null;
            player.OpeningContainer = request.Site;
            player.ContainerWalker = table.IdOf(slot);
            if (request.Task == CharacterTask.GoTo) MovementSystem.GoToFloor(world, slot, request.Point, walker.RunSpeed);
            else ContainerWorker(world, player);
        }

        /// <summary>A pick-up or a walk to a container that another task replaces or the Stop order ends is given up.</summary>
        void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
        {
            var table = world.Characters;
            table.Cold(slot)!.PickingUpItem = null;
            foreach (var owner in world.Players.All)
                if (owner.State.OpeningContainer is not null && owner.State.ContainerWalker == table.IdOf(slot)) owner.State.OpeningContainer = null;
        }

        /// <summary>The act-out a pick-up or a container's opening works in where the character stands (<see cref="CharacterTask.Idle"/>) is done once the item is taken or the container opened.</summary>
        bool ITaskHandler.Done(World world, int slot, CharacterTask task)
        {
            var table = world.Characters;
            if (table.Next[slot].Task != (byte)task) return true;
            return task == CharacterTask.Idle && table.Cold(slot)!.PickingUpItem is null && !world.Players.All.Any(owner => owner.State.OpeningContainer is not null && owner.State.ContainerWalker == table.IdOf(slot));
        }

        /// <summary>A walker takes the item within this distance of it (an engine choice).</summary>
        public const float PickUpReach = 15;

        static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

        readonly Dictionary<CharacterId, (CharacterControl Grant, Items.GroundItem Item)> pickupAuthority = [];

        void PickUpInput(World world, Items.PickUpItemOrder order)
        {
            if (world.GroundItems.Find(order.Item) is not { } item) return;
            var table = world.Characters;
            bool walkOnly = !order.Alt && item.OwnedBy >= 0 && item.OwnedBy != (world.CommandPlayer(order)?.Faction ?? -1);
            if (walkOnly) world.Post(order.Issuer ?? PlayerId.Local, "Hold down the ALT key to steal items");
            int best = -1;
            float bestDistance = float.MaxValue;
            foreach (var id in world.CommandCharacters(order, order.Characters))
            {
                if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, Inventory: not null, IsImprisoned: false } c || !c.CarriedBy.IsNone
                    || !TaskStart.Eligible(world, c, Meitou.Data.Gameplay.Ai.TaskType.PICKUP)) continue;
                float d = Vector2.Distance(Flat(table.Next[s].Position), Flat(item.Position));
                if (d < bestDistance) (best, bestDistance) = (s, d);
            }
            if (best < 0) return;
            if (walkOnly && world.SystemOf<MovementSystem>() is { } movement)
                PlayerOrders.Give(world, best, world.BindOrder(order, new OrderEntry(Meitou.Data.Gameplay.Ai.TaskType.MOVE_CUS_ORDERED, movement) { Point = item.Position }), order.Queued);
            else if (!walkOnly)
                PlayerOrders.Give(world, best, world.BindOrder(order, new OrderEntry(Meitou.Data.Gameplay.Ai.TaskType.PICKUP, this) { Point = item.Position, Detail = order.Item }), order.Queued);
        }

        OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
        {
            if (!world.ValidOrderOrQuery(slot, order)) return OrderCheck.Gone;
            var c = world.Characters.Cold(slot)!;
            if (!c.IsPlayer || (order.Detail is int item ? world.GroundItems.Find(item) is null : world.Fixtures.Find(order.Site!) is not { Contents: not null })) return OrderCheck.Gone;
            if (order.SourceCommand is Items.OpenContainerOrder container && !MayOpenContainer(world, slot, container)) return OrderCheck.Gone;
            return c is { Medical.Incapacitated: false, Inventory: not null, IsImprisoned: false } && c.CarriedBy.IsNone ? OrderCheck.Run : OrderCheck.Blocked();
        }

        void IOrderOwner.Start(World world, int slot, OrderEntry order)
        {
            if (!world.ValidOrder(slot, order)) return;
            if (order.Detail is not int item) { StartContainer(world, slot, order); return; }
            if (world.GroundItems.Find(item) is not { } ground) return;
            var actor = world.Characters.IdOf(slot);
            bool far = Vector2.Distance(Flat(world.Characters.Next[slot].Position), Flat(ground.Position)) > PickUpReach;
            if (TaskStart.Start(world, slot, new TaskRequest(far ? CharacterTask.GoTo : CharacterTask.Idle, Meitou.Data.Gameplay.Ai.TaskType.PICKUP, TaskSource.Order) { Point = ground.Position, Detail = order }) == StartResult.Started)
            {
                pickupAuthority.Remove(actor);
                if (order.SourceCommand?.Issuer is not null && world.Players.ControlOf(actor) is { } grant) pickupAuthority[actor] = (grant, ground);
            }
        }

        /// <summary>Each tick: a walker in reach takes its item; one whose item is gone, who was given another goal or stopped, or who is down gives up.</summary>
        void PickUpWorkers(World world)
        {
            var table = world.Characters;
            var next = table.Next;
            foreach (var actor in pickupAuthority.Keys.ToArray())
                if (!table.TryResolveNext(actor, out int current) || table.Cold(current)?.PickingUpItem != pickupAuthority[actor].Item.Id) pickupAuthority.Remove(actor);
            for (int s = 0; s < table.HighWater; s++)
            {
                if (!next[s].Alive || table.Cold(s) is not { PickingUpItem: { } id } c) continue;
                var actor = table.IdOf(s);
                if (pickupAuthority.TryGetValue(actor, out var authority)
                    && (!world.Players.Validate(authority.Grant) || !ReferenceEquals(world.GroundItems.Find(id), authority.Item)))
                {
                    pickupAuthority.Remove(actor);
                    c.PickingUpItem = null;
                    continue;
                }
                if (world.GroundItems.Find(id) is not { } item || c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone || c.Inventory is not { } inventory)
                {
                    c.PickingUpItem = null;
                    continue;
                }
                var at = Flat(item.Position);
                if (Vector2.Distance(Flat(next[s].Position), at) > PickUpReach)
                {
                    if (next[s].Task != (byte)CharacterTask.GoTo || Vector2.Distance(next[s].Goal, at) > PickUpReach) c.PickingUpItem = null;
                    continue;
                }
                c.PickingUpItem = null;
                pickupAuthority.Remove(actor);
                if (next[s].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, s);
                // An animal takes a limb in its mouth and eats it later (docs/game/character-stats.md "Eating").
                if (Items.FeedSystem.IsMouthPickUp(c, item.Item))
                {
                    TakeInMouth(world, s, c, item);
                    continue;
                }
                // Another faction's item is stolen from it (the "Steal" pick-up); what stays on the ground keeps its old mark.
                int before = item.Item.Quantity;
                var mark = item.Item.Mark;
                if (item.OwnedBy >= 0 && item.OwnedBy != c.Faction) item.Item.StolenFrom = item.OwnedBy;
                // What does not fit stays on the ground (Inventory.Add leaves it in the item); gear that does not fit is worn when its slot is free.
                bool wear = false;
                if (inventory.Add(item.Item) || item.Item.Quantity == before && (wear = Items.InventoryCommands.TryEquip(inventory, item.Item, c)))
                {
                    world.GroundItems.Take(id);
                    // Task_Pickup: another faction's item taken inside a building is a Theft.
                    if (item.OwnedBy >= 0 && item.OwnedBy != c.Faction && world.Sight.BuildingOf(s) is not null) world.SystemOf<Crime.CrimeSystem>()?.StolePickUp(world, s, item.OwnedBy);
                    if (wear) assembly?.RefreshEquipment(c);
                    world.EmitSound(SoundKind.PickedUp, s, next[s].Position);
                }
                else
                {
                    item.Item.Mark = mark;
                    // Task_Pickup sounds when any of the stack went in.
                    if (item.Item.Quantity < before) world.EmitSound(SoundKind.PickedUp, s, next[s].Position);
                    if (item.Item.Quantity < before && item.OwnedBy >= 0 && item.OwnedBy != c.Faction && world.Sight.BuildingOf(s) is not null)
                        world.SystemOf<Crime.CrimeSystem>()?.StolePickUp(world, s, item.OwnedBy);
                    world.Post(authority.Grant.Player.IsNone ? PlayerId.Local : authority.Grant.Player, $"{c.Name}: Not enough room in my inventory");
                }
            }
        }

        /// <summary>An animal's pick-up of a limb: into its mouth when it has the point, else nothing (no sound or message, the limb stays where it lies).</summary>
        static void TakeInMouth(World world, int slot, CharacterCold cold, Items.GroundItem item)
        {
            if (!Items.FeedSystem.HasMouth(cold)) return;
            world.GroundItems.Take(item.Id);
            Items.FeedSystem.HoldInMouth(world, slot, cold, item.Item, item.Fall?.SkinTone ?? default);
            world.EmitSound(SoundKind.PickedUp, slot, world.Characters.Next[slot].Position);
        }
    }
}
