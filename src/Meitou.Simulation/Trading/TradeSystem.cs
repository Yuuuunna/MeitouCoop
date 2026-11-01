using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Trading;

/// <summary>The player opens a trade between one of their characters and a trader: the customer walks up to the trader and the trade window opens in reach.</summary>
public sealed record OpenTrade(CharacterId Customer, CharacterId Trader) : SimCommand;
/// <summary>An operation within an existing trade window.</summary>
public abstract record TradeCommand : InventoryCommand
{
    /// <summary>The displayed window's server identity. Attributed commands must echo it.</summary>
    public ulong SessionId { get; init; }
}

/// <summary>Closes only the issuing player's trade.</summary>
public sealed record CloseTrade : TradeCommand;
/// <summary>
/// Buys <see cref="Quantity"/> units of the stock item <see cref="Item"/> (its index in the shop's stock) into the customer's inventory: at the cell <see cref="X"/>, <see cref="Y"/> of
/// <see cref="Section"/> (<c>main</c> or <c>backpack_content</c>, the cell the item was dropped on) when that is free, else wherever it fits.
/// A backpack may also target the single <c>backpack_attach</c> equipment slot.
/// </summary>
public sealed record BuyItem(InventoryItemToken Item, int Quantity) : TradeCommand
{
    public string? Section { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    /// <summary>The window's right click (no cell chosen): one unit goes into the equipment slot that takes it when that is free, before the grids (<see cref="InventoryCommands.TryEquip"/>).</summary>
    public bool Equip { get; init; }
}

/// <summary>Sells <see cref="Quantity"/> units of the customer's item <see cref="Item"/> (its index in the customer's inventory) to the shop, at the stock cell dropped on when free.</summary>
public sealed record SellItem(InventoryItemToken Item, int Quantity) : TradeCommand
{
    public int? X { get; init; }
    public int Y { get; init; }
}

/// <summary>One item of a trade window, with what it costs (stock) or fetches (the customer's goods) as a whole stack.</summary>
public sealed record TradeItemView(int Index, string Record, string Name, string Section, int X, int Y, int Width, int Height, int Quantity, float Quality, long Price, long UnitPrice, bool Stolen)
{
    /// <summary>The current transfer reverses a purchase or sale from this trade window at its original price.</summary>
    public bool Buyback { get; init; }
    /// <summary>A bag (a container with a contents grid): it trades whole, and may go into the backpack slot.</summary>
    public bool Container { get; init; }
    /// <summary>The item's material record ("" none), which picks its icon.</summary>
    public string Material { get; init; } = "";
    /// <summary>The COLOR_DATA it is dyed with (<see cref="Items.ItemInstance.ColourId"/>), which picks its icon.</summary>
    public string Colour { get; init; } = "";
    /// <summary>How full the item is (0 to 1); null: no charge bar.</summary>
    public float? Charge { get; init; }
    /// <summary>The item's manufacturer record (<c>company sid</c>; "" none), model level and charges, and the trader factor of its price (<c>FUN_14079e6d0</c>, 1: none).</summary>
    public string Company { get; init; } = "";
    public int Level { get; init; }
    public float Charges { get; init; }
    public float MaxCharges { get; init; }
    public float PriceFactor { get; init; } = 1;
    /// <summary>The trader's town market factor of the item (<see cref="Market.MarketFactor"/>, 1: none); with <see cref="PriceFactor"/> it makes a trade item's <c>Price mark-up</c>.</summary>
    public float MarketFactor { get; init; } = 1;
    /// <summary>The faction a stolen item was stolen from, -1 when it is not stolen.</summary>
    public int StolenFrom { get; init; } = -1;
    /// <summary>The tooltip's two prices of one unit (<c>FUN_14079e7a0</c> calls slot 81, <b>Verified</b>): what the shop asks for it (Value) and what it pays (Sell value).</summary>
    public long Value { get; init; }
    public long SellValue { get; init; }
    /// <summary>The stack's weight in kg.</summary>
    public float Weight { get; init; }
    /// <summary>A stolen good of the customer's that was stolen from the shop's own faction: the chance a sale goes through unnoticed (the fencing prompt's number); null otherwise.</summary>
    public float? FenceChance { get; init; }
    /// <summary>
    /// A stolen good of the customer's that a shop which is no fence would buy only with a risk: the chance (0 to 1, below 1) a sale goes through unnoticed, whoever it was stolen from
    /// (<c>FUN_140715290</c> asks "Fence it anyway?" with the opposite in per cent); null when the sale is safe or the item is not stolen.
    /// </summary>
    public float? FenceOdds { get; init; }
    /// <summary>A good of the customer's that the culture of the trader's faction lists as illegal (the smuggling prompt, <c>FUN_140712920</c>).</summary>
    public bool Illegal { get; init; }
    public InventoryItemToken Token => new(Index, Record, Section, X, Y, Quantity, Quality);
}

/// <summary>Arranges the open trade's stock (the trader window's ARRANGE button): the stock's grid as <see cref="InventoryCommands.Arrange"/>.</summary>
public sealed record ArrangeStock : TradeCommand;

/// <summary>The open trade as the UI shows it (published with the snapshot).</summary>
public sealed record TradeView(CharacterId Customer, CharacterId Trader, string TraderName, long ShopMoney, long PlayerMoney, int StockWidth, int StockHeight,
    IReadOnlyList<TradeItemView> Stock, IReadOnlyList<TradeItemView> Goods, bool BuysStolen, bool BuysIllegal = false)
{
    /// <summary>The held item (<see cref="PlayerState.Held"/>) priced as stock (lifted from the stock) or as the customer's goods (lifted from the customer); null for none or another's.</summary>
    public TradeItemView? Held { get; init; }
    public ulong SessionId { get; init; }
    public bool FundsVisible { get; init; } = true;
    public bool CanSpendFunds { get; init; } = true;
    public ulong StockRevision { get; init; }
    public ulong GoodsRevision { get; init; }
    /// <summary>The stock cells outside every container of a building shop (<see cref="Shop.Unused"/>), which hold nothing; empty for a squad shop.</summary>
    public IReadOnlyList<GridRegion> Unused { get; init; } = [];
}

/// <summary>A system that has an open trade to publish (the trade system).</summary>
public interface ITradeSource
{
    TradeView? PublishTrade();
    TradeView? PublishTrade(PlayerId player) => player == PlayerId.Local ? PublishTrade() : null;
    TradeView? PublishTrade(World world, PlayerId player) => null;
}

public sealed class TradeSettings
{
    /// <summary>How close the customer must stand to the trader (the original's "Out of trading range." distance is <b>Unknown</b>; engine choice).</summary>
    public float Range { get; init; } = 30;
    /// <summary>Ticks between looks at the squads for new shops and due restocks (engine choice: half a game second).</summary>
    public int CheckEveryTicks { get; init; } = 15;
    /// <summary>Game seconds the customer has to reach the trader before the opening is dropped (engine choice).</summary>
    public float ApproachSeconds { get; init; } = 60;
}

/// <summary>
/// The economy in play (docs/simulation.md "Trade", docs/game/economy.md sections 8 and 9):
/// <list type="bullet">
/// <item><b>Shops</b>: a squad made from a template with <c>is trader</c> and vendor lists gets a <see cref="Shop"/> when it appears (the game's first fill at squad creation), filled by
/// <see cref="ShopFiller"/>, with the purse in <see cref="Squad.Money"/>; it restocks every <c>vendors refresh time</c> game hours (9.3). Squads of the player's faction never get one.
/// A housed one whose template has no <c>prisoners</c> or <c>slaves</c> gets a building shop when its home has stock containers (<see cref="Shop.ForBuilding"/>).</item>
/// <item><b>Trading</b> (the trade window <c>FUN_140715560</c>, <b>Observed</b>): a purchase is priced by <see cref="Market.StackPrice"/> without the sell flag, a sale with it; when the payer
/// cannot afford the stack the number of units it can afford moves instead, none: "You can't afford that." (3) or "The shopkeeper can't afford that." (4). What does not fit: "No room for
/// that item." (2). The cats move between the player's purse and the shop's.</item>
/// <item><b>Fencing</b> (8.4, <b>Observed</b>): a stolen item sold to a shop that is not a knowing fence (<c>buys stolen</c>) is caught unless a roll is below
/// lerp(thievery / 100, 0.4, 1.25), halved when the item was stolen from the shop's own faction (never caught when it was stolen from a bandit or adventurer faction, or from one the shop's faction is not allied to, <see cref="FenceOdds"/>); the seller trains thievery either way; caught: "Got caught selling stolen item." (9), the sale is refused and the seller gets a
/// pending Fencing crime against the shop's faction, booked at once with the shopkeeper as the witness. The prompt that shows the chance is the UI's. An illegal item (the culture of the town's faction) is refused by a trader that does not buy illegal goods:
/// "Attempted smuggling!" (<see cref="Smuggling"/>); the rest of the smuggling rules (the trader's answer for a smuggler, read by no caller seen) are not built.</item>
/// </list>
/// The shops' stock and restock times are in the state hash (through the squads). The open trade is not, but the buyback receipts it leaves on items are, and they are not saved:
/// a save made mid-trade reloads with a different hash (docs/simulation.md, trading).
/// </summary>
public sealed partial class TradeSystem : ITickSystem, ITradeSource
{
    readonly PopulationData data;
    readonly CharacterAssembly assembly;
    readonly Dictionary<string, SquadTemplate?> templates = new(StringComparer.Ordinal), homeTemplates = new(StringComparer.Ordinal);
    readonly Dictionary<PlayerId, Interaction> interactions = [];
    readonly Dictionary<CharacterId, (HashSet<Interaction> Owners, bool Wandered)> holds = [];
    Market? market;
    ulong nextSession;

    public TradeSystem(PopulationData data, TradeSettings? settings = null)
    {
        this.data = data;
        assembly = new(data);
        Settings = settings ?? new TradeSettings();
    }

    public TradeSettings Settings { get; }
    public Crime.CrimeSystem? Crimes { get; set; }
    public Market Market(World world) => market ??= new Market(data, world.Seed);

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            if (command is not (OpenTrade or TradeCommand)) continue;
            var actor = command.Issuer ?? PlayerId.Local;
            if (world.Players.Find(actor) is null) continue;
            if (!interactions.TryGetValue(actor, out var interaction))
            {
                if (command is not OpenTrade) continue;
                interactions.Add(actor, interaction = new Interaction(this, actor));
            }
            interaction.Input(world, command);
        }
    }

    public void SlowWorld(World world)
    {
        if (world.Tick % Settings.CheckEveryTicks == 0) { Stock(world); PayWages(world); }
        foreach (var interaction in interactions.Values) interaction.Advance(world);
    }

    internal void RefreshView(World world)
    {
        foreach (var interaction in interactions.Values) interaction.RefreshView(world);
    }

    public TradeView? PublishTrade() => PublishTrade(PlayerId.Local);
    public TradeView? PublishTrade(PlayerId player) => interactions.GetValueOrDefault(player)?.View;
    public TradeView? PublishTrade(World world, PlayerId player) => interactions.GetValueOrDefault(player)?.PublishAuthorized(world);
    internal bool HoldsNpc(CharacterId npc) => holds.ContainsKey(npc);
    internal bool IsServingOther(CharacterId npc, PlayerId player) => holds.TryGetValue(npc, out var held) && held.Owners.Any(i => i.Actor != player);

    public void CloseForPlayer(World world, PlayerId player)
    {
        if (interactions.Remove(player, out var interaction)) interaction.Close(world);
    }

    internal bool HeldSessionMatches(World world, PlayerId player, ulong sessionId) => sessionId != 0
        && interactions.TryGetValue(player, out var interaction) && interaction.SessionId == sessionId && interaction.OpenStock(world, out _) is not null;

    internal Inventory? OpenStock(World world, out CharacterId buyer, PlayerId? player = null)
    {
        buyer = CharacterId.None;
        return interactions.TryGetValue(player ?? PlayerId.Local, out var interaction) ? interaction.OpenStock(world, out buyer) : null;
    }

    internal int AffordHeld(World world, ItemInstance item, int units, bool sell, PlayerId? player = null) =>
        interactions.GetValueOrDefault(player ?? PlayerId.Local)?.AffordHeld(world, item, units, sell) ?? 0;
    internal bool CaughtSelling(World world, ItemInstance item, PlayerId? player = null) =>
        interactions.GetValueOrDefault(player ?? PlayerId.Local)?.CaughtSelling(world, item) ?? false;
    internal bool TryPayHeld(World world, ItemInstance item, int units, bool sell, PlayerId player, out TradeReceipt? receipt)
    {
        receipt = null;
        return interactions.TryGetValue(player, out var interaction) && interaction.TryPayHeld(world, item, units, sell, out receipt);
    }

    public static float FenceChance(float thievery, bool stolenFromShopFaction) => Interaction.FenceChance(thievery, stolenFromShopFaction);
    internal const string BagNotEmpty = "That bag isn't empty.";

    void Hold(World world, CharacterId trader, Interaction interaction)
    {
        if (!world.Characters.TryResolveNext(trader, out int t)) return;
        ref var n = ref world.Characters.Next[t];
        if (!holds.TryGetValue(trader, out var held)) held = ([], false);
        held.Owners.Add(interaction);
        held.Wandered |= n.Task == (byte)CharacterTask.Wander;
        holds[trader] = held;
        world.Characters.Cold(t)!.Engaged = true;
        if (n.Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Idle) MovementSystem.Stop(world.Characters, t);
    }

    void Release(World world, CharacterId trader, Interaction interaction)
    {
        if (!holds.TryGetValue(trader, out var held)) return;
        held.Owners.Remove(interaction);
        if (held.Owners.Count != 0) return;
        holds.Remove(trader);
        if (!world.Characters.TryResolveNext(trader, out int t)) return;
        if (world.SystemOf<Dialogue.DialogueSystem>()?.HoldsNpc(trader) != true)
        {
            world.Characters.Cold(t)!.Engaged = false;
            if (held.Wandered && world.Characters.Next[t].Task == (byte)CharacterTask.Idle)
                world.Characters.Next[t].Task = (byte)CharacterTask.Wander;
        }
    }

    // CHARACTER `wages` (Character::giveBirth and Character::dailyUpdate, Verified): a character not of the player's faction with wages of 1 or more sets (not adds to) its squad's money
    // to them when it is made and every day; with several waged members the last one wins. Engine choices: the day pass runs when the game day changes, members in squad order; "when
    // made" is the first pass that sees the squad, except the squads already there at this system's first pass (a loaded game keeps its saved purses until the next day).
    readonly Dictionary<string, int> wages = new(StringComparer.Ordinal);
    readonly HashSet<int> paidSquads = [];
    long wageDay = long.MinValue;

    void PayWages(World world)
    {
        long day = (long)Math.Floor(world.GameHours / 24);
        bool first = wageDay == long.MinValue, newDay = !first && day != wageDay;
        wageDay = day;
        foreach (var squad in world.Squads.All)
        {
            if (!paidSquads.Add(squad.Id) && !newDay || first) continue;
            foreach (var id in squad.Members)
            {
                if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } c || c.IsPlayer || world.IsPlayerFaction(c.Faction) || data.PlayerFaction >= 0 && c.Faction == data.PlayerFaction) continue;
                if (!wages.TryGetValue(c.RecordId, out int pay)) wages[c.RecordId] = pay = data.Db.Find(c.RecordId) is { Type: Meitou.Data.Fcs.FcsRecordType.CHARACTER } r ? r.GetInt("wages", 0) : 0;
                if (pay >= 1) squad.Money = pay;
            }
        }
    }

    // ------------------------------------------------------------------ shops

    SquadTemplate? TemplateOf(string id)
    {
        if (!templates.TryGetValue(id, out var t))
            templates[id] = t = data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) && SquadTemplate.From(r) is { HasShop: true } shop ? shop : null;
        return t;
    }

    void Stock(World world)
    {
        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction >= 0 && (world.IsPlayerFaction(squad.Faction) || squad.Faction == data.PlayerFaction)) continue;
            bool living = Living(world, squad);
            if (squad.Shop is { Building: not null } closing && !living)
            {
                // Nobody of the squad is left alive: its stock goes back into its containers, which keep it, as the original's containers hold the stock (artifacts included).
                GiveBack(world, closing, all: true);
                squad.Shop = null;
                continue;
            }
            if (squad.Shop is { } shop)
            {
                if (world.Tick < shop.NextRestockTick || TemplateOf(squad.TemplateId) is not { } again) continue;
                Refill(world, squad, shop, again);
            }
            else if (living && TemplateOf(squad.TemplateId) is { } template)
            {
                var terms = new Trader(squad.Town, squad.Faction, template.BuyMult, template.BuysStolen, template.BuysIllegal);
                // A housed trader whose template makes no prisoner or slave squads keeps a building shop when its home has stock containers (the squad factory, Observed).
                squad.Shop = (squad.HomeBuilding is { } home && template.Prisoners.Count == 0 && template.Slaves.Count == 0 ? Shop.ForBuilding(home, terms) : null)
                    ?? new Shop { Terms = terms };
                TakeContents(world, squad.Shop);
                Refill(world, squad, squad.Shop, template);
            }
            else if (squad.HomeBuilding is { } home && HomeTemplateOf(squad.TemplateId) is { } contents && ShopFiller.VendorLinks(contents, FactionRecord(squad.Faction)).Count > 0) FillHome(world, squad, contents, home);
        }
    }

    /// <summary>
    /// A new building shop's stock containers hand what they hold (a town's artifacts) to the stock, each into its own rectangle (in the original the stock is what they hold,
    /// docs/game/economy.md "Artifacts").
    /// </summary>
    static void TakeContents(World world, Shop shop)
    {
        for (int k = 0; k < shop.Containers.Count; k++)
        {
            if (world.Fixtures.Find(shop.Containers[k]) is not { Contents: { } grid } || grid.Items.Count == 0) continue;
            var region = shop.Stock.Regions![k];
            foreach (var item in grid.Items)
            {
                item.X += region.X;
                item.Y += region.Y;
                shop.Stock.Items.Add(item);
            }
            grid.Items.Clear();
        }
    }

    /// <summary>Whether someone of the squad is alive.</summary>
    static bool Living(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int slot) && table.Cold(slot) is { DiedTick: < 0 }) return true;
        return false;
    }

    /// <summary>
    /// A building shop whose squad is dropped, made afresh or all dead hands its unique items (artifacts; with <paramref name="all"/>, its whole stock) back to their containers,
    /// which keep them for the next shop, as the original's containers keep them until taken (docs/game/economy.md "Artifacts").
    /// </summary>
    internal static void GiveBack(World world, Shop shop, bool all = false)
    {
        if (shop.Stock.Regions is not { } regions) return;
        for (int k = 0; k < shop.Containers.Count; k++)
        {
            if (world.Fixtures.Find(shop.Containers[k]) is not { Contents: { } grid }) continue;
            foreach (var item in shop.Stock.Items.Where(i => (all || i.Unique) && regions[k].Holds(i)).ToList())
            {
                shop.Stock.Items.Remove(item);
                (item.X, item.Y) = (item.X - regions[k].X, item.Y - regions[k].Y);
                if (grid.IsFree("main", item.X, item.Y, item.Width, item.Height)) grid.Items.Add(item);
                else grid.AddTo("main", item);
            }
        }
    }

    Meitou.Data.GameRecord? FactionRecord(int faction) => (uint)faction < (uint)data.Factions.Count ? data.Db.Find(data.Factions[faction].Id) : null;

    SquadTemplate? HomeTemplateOf(string id)
    {
        if (!homeTemplates.TryGetValue(id, out var t))
            homeTemplates[id] = t = data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) && SquadTemplate.From(r) is { IsTrader: false } fill && (fill.Vendors.Count > 0 || fill.BuildingDesignation is 2 or 3 or 5 or 6 or 8 or 9 or 11) ? fill : null;
        return t;
    }

    /// <summary>
    /// A housed squad with <c>vendors</c> that is no trader (a resident's <c>house contents</c>): the building filler fills its home's storage containers as a trader's (the squad factory
    /// <c>FUN_140583a10</c> tests <c>vendors</c>, not <c>is trader</c>, <b>Observed</b>), unlocking them; here each unit goes into the first container with room and what fits nowhere is
    /// lost. Engine choice: again every <c>vendors refresh time</c> hours, but only while the containers hold nothing but unique items (a loaded save keeps what they hold); the purse is not touched.
    /// </summary>
    void FillHome(World world, Squad squad, SquadTemplate template, Meitou.Data.World.TownBuilding home)
    {
        if (world.Tick < squad.NextHomeFillTick) return;
        bool first = squad.NextHomeFillTick == 0;
        squad.NextHomeFillTick = world.Tick + (long)(template.VendorsRefreshTime / BodySystem.HoursPerTick);
        var boxes = new List<Locks.Fixture>();
        foreach (var f in home.Fixtures)
            if (f.HoldsShopStock && world.Fixtures.Find(f.Id) is { Contents: not null } box) boxes.Add(box);
        if (boxes.Count == 0 || boxes.Any(b => b.Contents!.Items.Any(i => !i.Unique))) return;
        if (Shop.ForBuilding(home, new Trader(squad.Town, squad.Faction, template.BuyMult, template.BuysStolen, template.BuysIllegal)) is not { } merged) return;
        merged.Fills = (int)world.Tick;
        ShopFiller.Fill(merged, template, Market(world), data, world.Seed, (ulong)squad.Id, specials: first);
        foreach (var item in merged.Stock.Items)
            foreach (var box in boxes)
                if (box.Contents!.Add(item)) break;
        foreach (var box in boxes)
            if (box.Lock is { } l) l.Locked = false;
    }

    void Refill(World world, Squad squad, Shop shop, SquadTemplate template)
    {
        squad.Money = ShopFiller.Fill(shop, template, Market(world), data, world.Seed, (ulong)squad.Id, specials: shop.Fills == 0);
        // The building filler unlocks the stock containers it fills (FUN_14095b210).
        if (shop.Building is not null && squad.HomeBuilding is { } home)
            foreach (var f in home.Fixtures)
                if (f.HoldsShopStock && world.Fixtures.Find(f.Id)?.Lock is { } l) l.Locked = false;
        shop.NextRestockTick = world.Tick + (long)(template.VendorsRefreshTime / BodySystem.HoursPerTick);
    }

    /// <summary>The shop a character trades for: its squad's (null: not a trader).</summary>
    public static Shop? ShopOf(World world, CharacterCold cold) => cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId)?.Shop : null;

}
