using System.Numerics;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

internal static class InventoryTransfer
{
    readonly record struct InventoryDestination(Inventory Grid, CharacterCold Owner, string Section, int X, int Y);

    /// <summary>Serial preflight followed by one commit. Both roles and both displayed inventories must still match.</summary>
    internal static void Transfer(World world, TransferInventoryItem command, CharacterAssembly? assembly, PopulationData? data)
    {
        var table = world.Characters;
        if (!world.MayOrder(command, command.From) || !world.MayOrder(command, command.To)
            || !table.TryResolveNext(command.From, out int fromSlot) || !table.TryResolveNext(command.To, out int toSlot)
            || table.Cold(fromSlot) is not { IsPlayer: true, Inventory: { } from } source
            || table.Cold(toSlot) is not { IsPlayer: true, Inventory: { } into } target
            || !world.InventoryMatches(command, from)
            || !(command.Issuer is null && command.TargetRevision == 0 || command.TargetRevision != 0 && world.InventoryRevision(into) == command.TargetRevision)) return;
        float distance = Vector3.DistanceSquared(table.Next[fromSlot].Position, table.Next[toSlot].Position);
        if (!float.IsFinite(distance) || distance > HeldItems.GiveRange * HeldItems.GiveRange) return;
        var item = from.All().ElementAtOrDefault(command.Item.Index);
        if (!HeldItems.Matches(item, command.Item) || item is null || item.Death || item.LockedOn || command.Count <= 0 || command.Count > item.Quantity
            || !CanDetach(source, item) || HeldItems.HolderOf(from, item) is not { } sourceGrid) return;
        bool whole = command.Count == item.Quantity;
        if ((!whole && item.Contents is not null) || Destination(into, target, item, command.Count, command.Section, command.X, command.Y, data) is not { } destination) return;
        if (ReferenceEquals(item.Contents, destination.Grid)) return; // cannot put the worn backpack inside itself

        var under = Overlapping(destination, item).Where(other => other != item || !whole).ToArray();
        if (under.Length > 1 || under.Length == 1 && ReferenceEquals(under[0], item)) return;
        var other = under.FirstOrDefault();
        bool merge = other is not null && command.Section is "main" or "backpack_content" && InventoryCommands.Stackable(item, other);
        InventoryDestination? back = null;
        if (other is not null)
        {
            if (other.LockedOn || other.Death) return;
            if (merge)
            {
                if (command.Count > (long)destination.Grid.LimitOf(item) - other.Quantity) return;
            }
            else
            {
                if (!command.Swap || !whole || !CanDetach(target, other)
                    || Destination(from, source, other, other.Quantity, item.Section, item.X, item.Y, data) is not { } replacement
                    || ReferenceEquals(other.Contents, replacement.Grid)
                    || Overlapping(replacement, other).Any(i => i != item && i != other)) return;
                // Two replacement footprints in the same grid must not overlap each other after the swap.
                if (replacement.Grid == destination.Grid && replacement.Section == destination.Section
                    && Intersects(destination.X, destination.Y, item.Width, item.Height, replacement.X, replacement.Y, other.Width, other.Height)) return;
                back = replacement;
            }
        }
        if (item.Contents is not null && back is { } replacementBack && ReferenceEquals(item.Contents, replacementBack.Grid)) return;
        if (other?.Contents is not null && ReferenceEquals(other.Contents, sourceGrid)) return;

        // Everything that can refuse has been checked. No external callbacks or currency changes occur during commit.
        var unit = whole ? item : HeldItems.Part(item, command.Count);
        if (whole) { sourceGrid.Items.Remove(item); Detach(source, item); }
        if (from != into) unit.Death = false;
        if (merge)
        {
            var kept = other!.Buyback;
            var coming = unit.Buyback;
            InventoryCommands.Merge(other, unit, command.Count);
            if (kept is not null && coming is not null && kept.Joins(coming)) other.Buyback = kept.Append(coming.Take(command.Count));
        }
        else
        {
            if (other is not null && back is { } returning)
            {
                destination.Grid.Items.Remove(other);
                Detach(target, other);
                PutInventory(returning, other, data);
            }
            PutInventory(destination, unit, data);
        }
        assembly?.RefreshEquipment(source);
        if (source != target) assembly?.RefreshEquipment(target);
        world.InventoryRevision(from);
        if (from != into) world.InventoryRevision(into);
    }

    static bool CanDetach(CharacterCold owner, ItemInstance item) => InventoryCommands.IsMovableSection(item.Section)
        || InventoryCommands.LimbSlotOf(item.Section) is >= 0 and var limb
        && owner.Medical?.LimbPart(limb) is { Limb: LimbState.Replaced } part && ReferenceEquals(part.ReplacementItem, item);

    static InventoryDestination? Destination(Inventory root, CharacterCold owner, ItemInstance item, int count, string section, int x, int y, PopulationData? data)
    {
        var grid = section == "backpack_content" ? root.Backpack?.Contents : root;
        if (grid is null || item.Width <= 0 || item.Height <= 0 || count <= 0 || count > grid.LimitOf(item)
            || InventoryCommands.SlotRefusal(section, item.Type, item.EquipmentSection, item.Contents,
                item.WearableBy(owner.Race?.StringId, SquadRegistry.IsAnimal(owner))) is not null || InventoryCommands.SectionClosed(owner, section)) return null;
        bool equipment = InventoryCommands.IsEquipmentSection(section) || InventoryCommands.IsLimbSection(section);
        if (equipment && count != 1) return null;
        if (InventoryCommands.LimbSlotOf(section) is >= 0 and var limb
            && (data?.Items.Limb(item.Record)?.LimbSlot != limb || owner.Medical?.LimbPart(limb) is not { Limb: LimbState.Stump or LimbState.Replaced })) return null;
        var size = section is "back" or "hip" ? InventoryCommands.SectionSize(section)
            : equipment ? (item.Width, item.Height) : (grid.Width, grid.Height);
        if (x < 0 || y < 0 || x > size.Item1 - item.Width || y > size.Item2 - item.Height || !grid.InRegion(x, y, item.Width, item.Height)) return null;
        return new(grid, owner, section, x, y);
    }

    static IEnumerable<ItemInstance> Overlapping(InventoryDestination destination, ItemInstance item) => destination.Grid.Items.Where(other =>
        other.Section == destination.Section && Intersects(destination.X, destination.Y, item.Width, item.Height, other.X, other.Y, other.Width, other.Height));

    static bool Intersects(int x, int y, int width, int height, int otherX, int otherY, int otherWidth, int otherHeight) =>
        x < (long)otherX + otherWidth && otherX < (long)x + width && y < (long)otherY + otherHeight && otherY < (long)y + height;

    static void Detach(CharacterCold owner, ItemInstance item)
    {
        if (InventoryCommands.LimbSlotOf(item.Section) is >= 0 and var limb) owner.Medical?.RemoveReplacement(limb);
    }

    static void PutInventory(InventoryDestination destination, ItemInstance item, PopulationData? data)
    {
        item.Section = destination.Section; item.X = destination.X; item.Y = destination.Y;
        destination.Grid.Items.Add(item);
        if (InventoryCommands.LimbSlotOf(destination.Section) >= 0 && destination.Owner.Medical is { } medical && data?.Items.Limb(item.Record) is { } limb)
        {
            medical.FitReplacement(limb, item, item.LimbDamage, item.LimbWear, item.LimbStun);
            (item.LimbDamage, item.LimbWear, item.LimbStun) = (0, 0, 0);
        }
    }
}