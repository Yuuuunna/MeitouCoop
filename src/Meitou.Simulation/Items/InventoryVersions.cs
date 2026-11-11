using System.Runtime.CompilerServices;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Items;

/// <summary>World-session inventory revisions, including object replacement with identical visible token fields.</summary>
internal sealed class InventoryVersions
{
    sealed class Stamp
    {
        public ItemState[] Items = [];
        public ulong Revision;
    }

    readonly record struct ItemState(ItemInstance Instance, string Section, int X, int Y, int Quantity, float Quality,
        string Material, string Company, int Level, string Colour, string Crafter, string Uniform, float Charges, float MaxCharges,
        float LimbDamage, float LimbWear, float LimbStun, int StolenFrom, bool Locked, bool Death, bool Unique, int Function, TradeReceipt? Receipt)
    {
        public static ItemState Of(ItemInstance item) => new(item, item.Section, item.X, item.Y, item.Quantity, item.Quality,
            item.MaterialId, item.CompanyId, item.Level, item.ColourId, item.Crafter, item.Uniform, item.Charges, item.MaxCharges,
            item.LimbDamage, item.LimbWear, item.LimbStun, item.StolenFrom, item.LockedOn, item.Death, item.Unique, item.ItemFunction, item.Buyback);
    }

    readonly ConditionalWeakTable<Inventory, Stamp> _stamps = new();
    ulong _revision;

    /// <summary>Call only at a world boundary or during a serial phase, like other inventory reads.</summary>
    public ulong Read(Inventory inventory)
    {
        var stamp = _stamps.GetOrCreateValue(inventory);
        var items = inventory.All().Select(ItemState.Of).ToArray();
        if (stamp.Revision == 0 || !stamp.Items.AsSpan().SequenceEqual(items))
        {
            stamp.Revision = _revision = checked(_revision + 1);
            stamp.Items = items;
        }
        return stamp.Revision;
    }
}