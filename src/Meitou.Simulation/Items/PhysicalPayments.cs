using Meitou.Simulation.Player;

namespace Meitou.Simulation.Items;

/// <summary>
/// Native wallet transfers. Player money is a faction account. NPCs pay and receive the database's physical denominations.
/// </summary>
internal static class PhysicalPayments
{
    internal readonly record struct Purse(FactionState? Account, Inventory? Grid, bool MainClosed = false)
    {
        internal static Purse Of(World world, CharacterCold owner, Inventory? grid = null, bool preview = false)
        {
            if (!owner.IsPlayer) return new(null, grid ?? owner.Inventory, InventoryCommands.MainClosed(owner));
            var account = world.Factions.Find(owner.Faction);
            return preview && account is not null ? new(new FactionState(owner.Faction) { Money = account.Money }, null) : new(account, null);
        }
    }

    internal static bool Transfer(ItemFactory? factory, Purse from, Purse to, int amount)
    {
        var debit = from.Account;
        var credit = to.Account;
        if (amount <= 0 || debit is null && from.Grid is null || credit is null && to.Grid is null
            || debit is not null && (debit.Money < amount || debit.Index == credit?.Index)
            || credit is not null && (credit.Money < 0 || credit.Money > long.MaxValue - amount)
            || (from.Grid is not null || to.Grid is not null) && factory is null) return false;

        List<(ItemInstance Item, int Count)> tender = [];
        int change = 0;
        if (from.Grid is not null && !PrepareDebit(factory!, from, amount, tender, out change)) return false;
        if (to.Grid is not null && !Deposit(factory!, to.MainClosed, to.Grid.Copy(), amount)) return false;

        if (debit is not null) debit.Money -= amount;
        else
        {
            foreach (var (item, count) in tender) Withdraw(from.Grid!, item, count);
            Deposit(factory!, from.MainClosed, from.Grid!, change);
        }
        if (credit is not null) credit.Money += amount;
        else Deposit(factory!, to.MainClosed, to.Grid!, amount);
        return true;
    }

    static bool PrepareDebit(ItemFactory factory, Purse payer, int amount, List<(ItemInstance Item, int Count)> tender, out int change)
    {
        change = 0;
        if (payer.Grid is not { } grid) return false;
        var trial = grid.Copy();
        var copies = trial.All().ToArray();
        long remaining = amount;
        var candidates = grid.All().Select((item, index) => (Item: item, Index: index, Value: CoinValue(factory, item)))
            .Where(c => c.Value > 0).OrderByDescending(c => c.Value).ThenBy(c => c.Index).ToArray();
        // Use exact denominations first. If needed, break the smallest remaining coin and return its change.
        foreach (var (item, index, value) in candidates)
        {
            int count = (int)Math.Min(remaining / value, item.Quantity);
            if (count <= 0) continue;
            tender.Add((item, count));
            Withdraw(trial, copies[index], count);
            remaining -= (long)value * count;
            if (remaining == 0) return true;
        }
        foreach (var (item, index, value) in candidates.Reverse())
        {
            if (value <= remaining || !trial.All().Contains(copies[index])) continue;
            Withdraw(trial, copies[index], 1);
            tender.Add((item, 1));
            change = value - (int)remaining;
            return Deposit(factory, payer.MainClosed, trial, change);
        }
        return false;
    }

    static int CoinValue(ItemFactory factory, ItemInstance item) => item.ItemFunction == PlayerSystem.MoneyFunction
        && item.ModTag.Length == 0 && !item.LockedOn && !item.Death && item.Contents is null
        ? Math.Max(0, factory.Db.Find(item.Record)?.GetInt("value") ?? 0) : 0;

    static void Withdraw(Inventory grid, ItemInstance item, int count)
    {
        if (item.Quantity == count) grid.Remove(item);
        else item.Quantity -= count;
    }

    static bool Deposit(ItemFactory factory, bool mainClosed, Inventory grid, int amount)
    {
        long placed = 0;
        foreach (var stack in factory.EnumerateMoney(amount))
        {
            placed += (long)CoinValue(factory, stack) * stack.Quantity;
            if (!grid.Add(stack, mainClosed)) return false;
        }
        return placed == amount;
    }
}
