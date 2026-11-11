using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using TownBuilding = Meitou.Data.World.TownBuilding;

namespace Meitou.Simulation.Trading;

/// <summary>
/// What one purchase attempt of a shopper came to (<see cref="NpcTrade.Buy"/>).
/// </summary>
public enum NpcSale : byte
{
    /// <summary>
    /// No seller of the building has an item of the wanted function.
    /// </summary>
    NoItem,
    /// <summary>
    /// The buyer could not pay or had no room: nothing moved (the unit still counts as bought on the shopping list).
    /// </summary>
    Failed,
    /// <summary>
    /// One unit changed hands.
    /// </summary>
    Sold,
}

/// <summary>
/// An NPC buying one unit from a building's stock (docs/game/ai-tasks.md goal 118 "Buying", docs/game/economy.md "NPC purchases"): the sale
/// <c>FUN_14074a630</c> that <c>FUN_1409a14b0</c> makes for <c>Task_Shopping</c>. Changes shared state, so it runs in a serial step only.
/// <list type="bullet">
/// <item><b>Sellers</b>: the containers of the building (<b>Observed</b>, <c>FUN_14054add0</c> / <c>FUN_1409a14b0</c> list only container objects,
/// never a trader's carried stock): a building shop's stock rectangle per container, and the contents of a house's stock containers. The first with
/// an item of the function sells a random one of them. Engine choice: a unique item (an artifact) is never sold.</item>
/// <item><b>Price</b> (<b>Verified</b>): the item's stack price (slot 82) of one unit, with the sell flag set only for a buyer of the player's
/// faction. With no trade window open the trader factor is 1 and no town's market factor applies (<b>Observed</b>), so it is
/// <see cref="Market.StackPrice"/> with <see cref="Trader.None"/>.</item>
/// <item><b>Money</b> (<b>Verified</b>): from the buyer's squad purse (the player's purse for a buyer of the player's faction; none: 0) to the squad
/// that owns the stock. The buyer must hold the price in full.</item>
/// <item><b>The item</b> (<b>Verified</b>): when the stock's owner is not of the player's faction and a roll is above <see cref="KeepChance"/> the
/// unit is paid for and leaves the stock without going anywhere; otherwise it goes into the buyer's inventory, and with no room the sale is called
/// off.</item>
/// </list>
/// </summary>
public static class NpcTrade
{
    /// <summary>
    /// The roll an NPC seller's unit must not exceed for the buyer to keep it (<c>DAT_1416885d8</c>, <b>Verified</b>: 0.2).
    /// </summary>
    public const float KeepChance = 0.2f;

    /// <summary>
    /// Tries to buy one unit of an item of the item function <paramref name="function"/> in <paramref name="building"/> for the character in slot
    /// <paramref name="buyer"/>. <paramref name="roll"/> picks the item and decides whether it is kept.
    /// </summary>
    public static NpcSale Buy(World world, Market market, int playerFaction, int buyer, TownBuilding building, int function, ulong roll)
    {
        var table = world.Characters;
        if (table.Cold(buyer) is not { } cold) return NpcSale.NoItem;

        foreach (var (stock, region, owner) in Sellers(world, building))
        {
            int count = 0;
            foreach (var item in stock.Items)
                if (Offered(market, item, region, function)) count++;
            if (count == 0) continue;

            int pick = Rng.Int(roll, count);
            ItemInstance? chosen = null;

            foreach (var item in stock.Items)
            {
                if (Offered(market, item, region, function) && pick-- == 0)
                {
                    chosen = item;
                    break;
                }
            }

            if (chosen is null) continue;

            bool player = cold.IsPlayer || world.IsPlayerFaction(cold.Faction);
            if (player && world.Players.RequiresExplicitControl && (world.Players.ControlOf(table.IdOf(buyer)) is not { } grant || !world.Players.Validate(grant)
                || world.Players.Find(grant.Player)?.HasPermission(cold.Faction, FactionPermission.SpendFunds) != true)) return NpcSale.Failed;
            var funds = player && cold.Faction >= 0 ? world.Factions.GetOrCreate(cold.Faction) : null;
            var ownerFunds = owner is not null && world.IsPlayerFaction(owner.Faction) ? world.Factions.GetOrCreate(owner.Faction) : null;
            var purse = player ? null : world.Squads.Find(cold.SquadId);
            var unit = chosen.WithQuantity(1);
            long price = market.StackPrice(unit, player, Trader.None, owner?.Faction ?? -1);
            if ((player ? funds?.Money ?? 0 : purse?.Money ?? 0) < price) return NpcSale.Failed;
            if (price < 0 || ownerFunds is not null && ownerFunds.Money > long.MaxValue - price || ownerFunds is null && owner is not null && owner.Money > long.MaxValue - price) return NpcSale.Failed;

            bool toBuyer = owner is { } own && world.IsPlayerFaction(own.Faction) || Rng.Float(Rng.Mix(roll)) <= KeepChance;
            if (toBuyer && (cold.Inventory is null || !cold.Inventory.Add(unit)))
                return NpcSale.Failed;

            if (chosen.Quantity <= 1) stock.Remove(chosen);
            else chosen.Quantity--;
            if (player) funds!.TryDebit(price);
            else if (purse is not null) purse.Money -= price;
            if (ownerFunds is not null) ownerFunds.TryCredit(price);
            else
                if (owner is not null) owner.Money += price;

            return NpcSale.Sold;
        }

        return NpcSale.NoItem;
    }

    /// <summary>
    /// Whether the item is offered in an NPC trade: not unique, no full bag, inside <paramref name="region"/> when one is given, and of item
    /// function <paramref name="function"/>.
    /// </summary>
    static bool Offered(Market market, ItemInstance item, GridRegion? region, int function) =>
        !item.Unique && item.Contents is not { Items.Count: > 0 } && (region is not { } r || r.Holds(item))
        && market.Items.Info(item.Record)?.Function == function;

    /// <summary>
    /// The building's stock containers in a fixed order: the squads that live in it with a building shop (one rectangle of the shop's stock per
    /// container), then the contents of its stock containers (a house's goods), which belong to the first squad living in it (none: the money goes
    /// nowhere; the original pays the owner faction).
    /// </summary>
    static IEnumerable<(Inventory Stock, GridRegion? Region, Squad? Owner)> Sellers(World world, TownBuilding building)
    {
        Squad? resident = null;

        foreach (var squad in world.Squads.All)
        {
            if (!ReferenceEquals(squad.Ai?.OwnHome ?? squad.HomeBuilding, building)) continue;

            resident ??= squad;

            if (squad.Shop is { Building: not null } shop)
            {
                for (int k = 0; k < shop.Containers.Count; k++)
                    yield return (shop.Stock, shop.Stock.Regions![k], squad);
            }
        }

        foreach (var f in building.Fixtures)
        {
            if (f.HoldsShopStock && world.Fixtures.Find(f.Id)?.Contents is { } box) yield return (box, null, resident);
        }
    }
}