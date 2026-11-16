using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Trading;

/// <summary>
/// The player opens a trade between one of their characters and a trader: the customer walks up to the trader and the trade window opens in reach.
/// </summary>
public sealed record OpenTrade(CharacterId Customer, CharacterId Trader) : OrderCommand;
/// <summary>
/// Closes the open trade.
/// </summary>
public sealed record CloseTrade : TradeCommand;
/// <summary>
/// Buys <see cref="Quantity"/> units of the stock item <see cref="Item"/> (its index in the shop's stock) into the customer's inventory: at the cell
/// <see cref="X"/>, <see cref="Y"/> of <see cref="Section"/> (<c>main</c> or <c>backpack_content</c>, the cell the item was dropped on) when that is
/// free, else wherever it fits. A backpack may also target the single <c>backpack_attach</c> equipment slot.
/// </summary>
public sealed record BuyItem(InventoryItemToken Item, int Quantity) : TradeCommand
{
    /// <summary>
    /// The section of the cell the item was dropped on (<c>main</c>, <c>backpack_content</c> or <c>backpack_attach</c>); null: wherever it fits.
    /// </summary>
    public string? Section { get; init; }
    /// <summary>
    /// The column of the cell dropped on.
    /// </summary>
    public int X { get; init; }
    /// <summary>
    /// The row of the cell dropped on.
    /// </summary>
    public int Y { get; init; }
    /// <summary>
    /// The window's right click (no cell chosen): one unit goes into the equipment slot that takes it when that is free, before the grids
    /// (<see cref="InventoryCommands.TryEquip"/>).
    /// </summary>
    public bool Equip { get; init; }
    /// <summary>
    /// The character whose window it goes into: the trade passes to them (<see cref="TradeSystem.TradeWith"/>). None: the customer, or the
    /// selection's.
    /// </summary>
    public CharacterId Into { get; init; } = CharacterId.None;
}

/// <summary>
/// Sells <see cref="Quantity"/> units of the customer's item <see cref="Item"/> (its index in the customer's inventory) to the shop, at the stock
/// cell dropped on when free.
/// </summary>
public sealed record SellItem(InventoryItemToken Item, int Quantity) : TradeCommand
{
    /// <summary>
    /// The column of the stock cell dropped on; null: wherever it fits.
    /// </summary>
    public int? X { get; init; }
    /// <summary>
    /// The row of the stock cell dropped on.
    /// </summary>
    public int Y { get; init; }
    /// <summary>
    /// The character whose window it comes from: the trade passes to them (<see cref="TradeSystem.TradeWith"/>). None: the customer, or the
    /// selection's.
    /// </summary>
    public CharacterId From { get; init; } = CharacterId.None;
}

/// <summary>
/// One item of a trade window, with what it costs (stock) or fetches (the customer's goods) as a whole stack.
/// </summary>
public sealed record TradeItemView(int Index, string Record, string Name, string Section, int X, int Y, int Width, int Height, int Quantity,
    float Quality, long Price, long UnitPrice, bool Stolen)
{
    /// <summary>
    /// The current transfer reverses a purchase or sale from this trade window at its original price.
    /// </summary>
    public bool Buyback { get; init; }
    /// <summary>
    /// A bag (a container with a contents grid): it trades whole, and may go into the backpack slot.
    /// </summary>
    public bool Container { get; init; }
    /// <summary>
    /// The item's material record ("" none), which picks its icon.
    /// </summary>
    public string Material { get; init; } = "";
    /// <summary>
    /// The item lies turned a quarter (<see cref="Items.ItemInstance.Rotated"/>): its size is the turned footprint's, and the icon is drawn turned.
    /// </summary>
    public bool Rotated { get; init; }
    /// <summary>
    /// The COLOR_DATA it is dyed with (<see cref="Items.ItemInstance.ColourId"/>), which picks its icon.
    /// </summary>
    public string Colour { get; init; } = "";
    /// <summary>
    /// How full the item is (0 to 1); null: no charge bar.
    /// </summary>
    public float? Charge { get; init; }
    /// <summary>
    /// The item's manufacturer record (<c>company sid</c>; "" none), model level and charges, and the trader factor of its price
    /// (<c>FUN_14079e6d0</c>, 1: none).
    /// </summary>
    public string Company { get; init; } = "";
    /// <summary>
    /// The item's model level.
    /// </summary>
    public int Level { get; init; }
    /// <summary>
    /// The item's charges.
    /// </summary>
    public float Charges { get; init; }
    /// <summary>
    /// The charges of a full unit.
    /// </summary>
    public float MaxCharges { get; init; }
    /// <summary>
    /// The trader factor of its price (1: none).
    /// </summary>
    public float PriceFactor { get; init; } = 1;
    /// <summary>
    /// The trader's town market factor of the item (<see cref="Market.MarketFactor"/>, 1: none); with <see cref="PriceFactor"/> it makes a trade
    /// item's <c>Price mark-up</c>.
    /// </summary>
    public float MarketFactor { get; init; } = 1;
    /// <summary>
    /// The faction a stolen item was stolen from, -1 when it is not stolen.
    /// </summary>
    public int StolenFrom { get; init; } = -1;
    /// <summary>
    /// The name of the character a stolen item was taken from, "" when that character is not in the world, null when the item is not stolen or its
    /// mark names no character (as <see cref="InventoryItemView.StolenOwner"/>).
    /// </summary>
    public string? StolenOwner { get; init; }
    /// <summary>
    /// The item's <c>unique</c> flag (<see cref="Items.ItemInstance.Unique"/>): the tooltip's [Unique] row.
    /// </summary>
    public bool Unique { get; init; }
    /// <summary>
    /// How many items a bag holds (-1: not a bag); an empty one's tooltip name ends in " (empty)".
    /// </summary>
    public int BagItems { get; init; } = -1;
    /// <summary>
    /// The tooltip's two prices of one unit (<c>FUN_14079e7a0</c> calls slot 81, <b>Verified</b>): what the shop asks for it (Value) and what it pays
    /// (Sell value).
    /// </summary>
    public long Value { get; init; }
    /// <summary>
    /// What the shop pays for one unit.
    /// </summary>
    public long SellValue { get; init; }
    /// <summary>
    /// The stack's weight in kg.
    /// </summary>
    public float Weight { get; init; }
    /// <summary>
    /// A stolen good of the customer's that the shop would roll on (<c>FUN_140792830</c>, whoever it was stolen from): the chance (0 to 1) a sale
    /// goes through unnoticed, <see cref="TradeSystem"/>'s fence odds, the hover box's number; null otherwise.
    /// </summary>
    public float? FenceChance { get; init; }
    /// <summary>
    /// A stolen good of the customer's that a shop which is no fence would buy only with a risk: the chance (0 to 1, below 1) a sale goes through
    /// unnoticed, whoever it was stolen from (<c>FUN_140715290</c> asks "Fence it anyway?" with the opposite in per cent); null when the sale is safe
    /// or the item is not stolen.
    /// </summary>
    public float? FenceOdds { get; init; }
    /// <summary>
    /// A good of the customer's that the culture of the trader's faction lists as illegal (the smuggling prompt, <c>FUN_140712920</c>).
    /// </summary>
    public bool Illegal { get; init; }
    /// <summary>
    /// The code mod tag, present in local views and replaced by its digest for network recipients.
    /// </summary>
    public string ModTag { get; init; } = "";
    /// <summary>The fixed-size identity of a network item tag.</summary>
    public string ModTagDigest { get; init; } = "";
    /// <summary>The token addressing the item in its inventory.</summary>
    public InventoryItemToken Token => new(Index, Record, Section, X, Y, Quantity, Quality) { ModTag = ModTag, ModTagDigest = ModTagDigest };
}

/// <summary>
/// Arranges the open trade's stock (the trader window's ARRANGE button): the stock's grid as <see cref="InventoryCommands.Arrange"/>.
/// </summary>
public sealed record ArrangeStock : TradeCommand;

/// <summary>
/// The open trade as the UI shows it (published with the snapshot).
/// </summary>
public sealed record TradeView(CharacterId Customer, CharacterId Trader, string TraderName, long ShopMoney, long PlayerMoney,
    int StockWidth, int StockHeight,
    IReadOnlyList<TradeItemView> Stock, IReadOnlyList<TradeItemView> Goods, bool BuysStolen, bool BuysIllegal = false)
{
    /// <summary>
    /// The held item (<see cref="PlayerState.Held"/>) priced as stock (lifted from the stock) or as a seller's goods (lifted from one in
    /// <see cref="GoodsOf"/>); null otherwise.
    /// </summary>
    public TradeItemView? Held { get; init; }
    /// <summary>
    /// The movable items of every player character who could take the trade over (the customer's are <see cref="Goods"/>), priced for that seller;
    /// keyed by character.
    /// </summary>
    public ulong SessionId { get; init; }
    public bool FundsVisible { get; init; } = true;
    public bool CanSpendFunds { get; init; } = true;
    public ulong StockRevision { get; init; }
    public ulong GoodsRevision { get; init; }
    public IReadOnlyDictionary<CharacterId, ulong> GoodsRevisions { get; init; } = new Dictionary<CharacterId, ulong>();
    /// <summary>
    /// The movable items of every player character who could take the trade over (the customer's are <see cref="Goods"/>), priced for that seller; keyed by character.
    /// </summary>
    public IReadOnlyDictionary<CharacterId, IReadOnlyList<TradeItemView>> GoodsOf { get; init; } =
        new Dictionary<CharacterId, IReadOnlyList<TradeItemView>>();
}

/// <summary>
/// A system that has an open trade to publish (the trade system).
/// </summary>
public interface ITradeSource
{
    TradeView? PublishTrade(World world, PlayerId player) => null;

    TradeView? PublishTrade();

    /// <summary>
    /// The open trade as the window shows it; null: none.
    /// </summary>
    TradeView? PublishTrade(PlayerId player) => player == PlayerId.Local ? PublishTrade() : null;
}

/// <summary>
/// Settings of the <see cref="TradeSystem"/>.
/// </summary>
public sealed class TradeSettings
{
    /// <summary>
    /// How close another selected character must stand to the trader to take over the open trade: the original's item move between two characters'
    /// inventories (the stock dropped on a character window) allows 1000 when unflagged (<c>FUN_14070d290</c>, <b>Observed</b>; whether the trade
    /// window flags it is <b>Unknown</b>, docs/game/economy.md "Trading range").
    /// </summary>
    public float PassRange { get; init; } = 1000;
    /// <summary>
    /// Ticks between looks at the squads for new shops and due restocks (engine choice: half a game second).
    /// </summary>
    public int CheckEveryTicks { get; init; } = 15;
    /// <summary>
    /// Game seconds the customer may spend not moving on its walk to the trader before the opening is dropped (engine choice,
    /// <see cref="Dialogue.WalkUp"/>).
    /// </summary>
    public float ApproachSeconds { get; init; } = 60;
}

/// <summary>
/// The economy in play (docs/simulation.md "Trade", docs/game/economy.md sections 8 and 9):
/// <list type="bullet">
/// <item><b>Shops</b>: a squad made from a template with <c>is trader</c> and vendor lists gets a <see cref="Shop"/> when it appears (the game's
/// first fill at squad creation), filled by <see cref="ShopFiller"/>, with the purse in <see cref="Squad.Money"/>; it restocks every <c>vendors
/// refresh time</c> game hours (9.3). Squads of the player's faction never get one. A housed one gets a building shop when its home has stock
/// containers (<see cref="Shop.ForBuilding"/>).</item>
/// <item><b>Trading</b> (the trade window <c>FUN_140715560</c>, <b>Observed</b>): a purchase is priced by <see cref="Market.StackPrice"/> without the
/// sell flag, a sale with it; when the payer cannot afford the stack the number of units it can afford moves instead, none: "You can't afford that."
/// (3) or "The shopkeeper can't afford that." (4). What does not fit: "No room for that item." (2). The cats move between the player's purse and the
/// shop's.</item>
/// <item><b>Fencing</b> (8.4, <b>Observed</b>): a stolen item sold to a shop that is not a knowing fence (<c>buys stolen</c>; a knowing fence only
/// for goods stolen from its own squad) is caught unless a roll is below lerp(thievery / 100, 0.4, 1.25), halved when the item was stolen from the
/// shop's own faction and again from its own squad (never caught when it was stolen from a bandit or adventurer faction, or from one the shop's
/// faction is not allied to whose home town is not the trader's, <see cref="FenceOdds"/>); the seller trains thievery either way; caught: "Got caught
/// selling stolen item." (9), the sale is refused and the seller gets a pending Fencing crime against the shop's faction, booked at once with the
/// shopkeeper as the witness. The prompt that shows the chance is the UI's. An illegal item (the culture of the town's faction) is refused by a
/// trader that does not buy illegal goods: "Attempted smuggling!" (<see cref="Smuggling"/>); the rest of the smuggling rules (the trader's answer for
/// a smuggler, read by no caller seen) are not built.</item>
/// </list>
/// A squad shop's stock and every shop's restock time are in the state hash (through the squads; a building shop's goods through its containers).
/// The open trade is not, but the buyback receipts it leaves on items are, and they are not saved: a save made mid-trade reloads with a different
/// hash (docs/simulation.md, trading).
/// </summary>
public sealed partial class TradeSystem : ITickSystem, ITradeSource
{
    readonly PopulationData _data;
    readonly CharacterAssembly _assembly;
    readonly Dictionary<string, SquadTemplate?> _templates = new(StringComparer.Ordinal), _homeTemplates = new(StringComparer.Ordinal);
    readonly Dictionary<PlayerId, Interaction> _interactions = [];
    readonly Dictionary<CharacterId, (HashSet<Interaction> Owners, bool Wandered)> _holds = [];
    Market? _market;
    ulong _nextSession;

    public TradeSystem(PopulationData data, TradeSettings? settings = null)
    {
        this._data = data;
        _assembly = new(data);
        Settings = settings ?? new TradeSettings();
        _wages = new(data);
    }

    public TradeSettings Settings { get; }
    public Crime.CrimeSystem? Crimes { get; set; }
    public Market Market(World world) => _market ??= new Market(_data, world.Seed);

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            if (command is not (OpenTrade or TradeCommand)) continue;
            var actor = command.Issuer ?? PlayerId.Local;
            if (world.Players.Find(actor) is null) continue;
            if (!_interactions.TryGetValue(actor, out var interaction))
            {
                if (command is not OpenTrade) continue;
                _interactions.Add(actor, interaction = new Interaction(this, actor));
            }
            interaction.Input(world, command);
            interaction.SettleStock(world);
        }

        WriteOpenView(world);
    }

    public void SlowWorld(World world)
    {
        if (world.Tick % Settings.CheckEveryTicks == 0)
        {
            // Fresh characters receive wages before their shop's first fill replaces its purse.
            _wages.Pay(world);
            Stock(world);
        }
        foreach (var interaction in _interactions.Values) interaction.Advance(world);
        WriteOpenView(world);
    }

    internal void RefreshView(World world)
    {
        foreach (var interaction in _interactions.Values) interaction.RefreshView(world);
    }

    public TradeView? PublishTrade() => PublishTrade(PlayerId.Local);
    public TradeView? PublishTrade(PlayerId player) => _interactions.GetValueOrDefault(player)?.View;
    public TradeView? PublishTrade(World world, PlayerId player) => _interactions.GetValueOrDefault(player)?.PublishAuthorized(world);
    internal bool HoldsNpc(CharacterId npc) => _holds.ContainsKey(npc);
    internal bool IsServingOther(CharacterId npc, PlayerId player) => _holds.TryGetValue(npc, out var held) && held.Owners.Any(i => i.Actor != player);

    public void CloseForPlayer(World world, PlayerId player)
    {
        if (_interactions.Remove(player, out var interaction)) interaction.Close(world);
    }

    internal bool HeldSessionMatches(World world, PlayerId player, ulong sessionId) => sessionId != 0
        && _interactions.TryGetValue(player, out var interaction) && interaction.SessionId == sessionId && interaction.OpenStock(world, out _) is not null;

    internal bool TradeWith(World world, CharacterId who, PlayerId? player = null, SimCommand? command = null) =>
        _interactions.TryGetValue(player ?? PlayerId.Local, out var interaction) && interaction.TradeWith(world, who, command);

    internal Inventory? OpenStock(World world, out CharacterId buyer, PlayerId? player = null)
    {
        buyer = CharacterId.None;
        return _interactions.TryGetValue(player ?? PlayerId.Local, out var interaction) ? interaction.OpenStock(world, out buyer) : null;
    }

    internal int AffordHeld(World world, ItemInstance item, int units, bool sell, PlayerId? player = null) =>
        _interactions.GetValueOrDefault(player ?? PlayerId.Local)?.AffordHeld(world, item, units, sell) ?? 0;
    internal bool CaughtSelling(World world, ItemInstance item, PlayerId? player = null) =>
        _interactions.GetValueOrDefault(player ?? PlayerId.Local)?.CaughtSelling(world, item) ?? false;
    internal bool TryPayHeld(World world, ItemInstance item, int units, bool sell, PlayerId player, out TradeReceipt? receipt)
    {
        receipt = null;
        return _interactions.TryGetValue(player, out var interaction) && interaction.TryPayHeld(world, item, units, sell, out receipt);
    }

    public static float FenceChance(float thievery, bool stolenFromShopFaction, bool stolenFromShopSquad = false) =>
        Interaction.FenceChance(thievery, stolenFromShopFaction, stolenFromShopSquad);
    internal const string BagNotEmpty = "That bag isn't empty.";

    void Hold(World world, CharacterId trader, Interaction interaction)
    {
        if (!world.Characters.TryResolveNext(trader, out int t)) return;
        ref var n = ref world.Characters.Next[t];
        if (!_holds.TryGetValue(trader, out var held)) held = ([], false);
        held.Owners.Add(interaction);
        held.Wandered |= n.Task == (byte)CharacterTask.Wander;
        _holds[trader] = held;
        world.Characters.Cold(t)!.Engaged = true;
        if (n.Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Idle) MovementSystem.Stop(world.Characters, t);
    }

    void Release(World world, CharacterId trader, Interaction interaction)
    {
        if (!_holds.TryGetValue(trader, out var held)) return;
        held.Owners.Remove(interaction);
        if (held.Owners.Count != 0) return;
        _holds.Remove(trader);
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
    // made" is the first pass that sees the squad, except the squads already there at this system's first pass and a squad of characters back from a save or from out of the world
    // (Squad.Restored: giveBirth pays only a character made without saved state, so a loaded game keeps its saved purses until the next day).

    // ------------------------------------------------------------------ shops

    /// <summary>
    /// Separates the stock placement stream from the stock selection stream.
    /// </summary>
    const ulong SpreadSalt = 0x5EEDB0C5UL;

    SquadTemplate? TemplateOf(string id)
    {
        if (!_templates.TryGetValue(id, out var t))
            _templates[id] = t = _data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) && SquadTemplate.From(r) is { HasShop: true } shop ? shop : null;
        return t;
    }

    void Stock(World world)
    {
        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction >= 0 && (world.IsPlayerFaction(squad.Faction) || squad.Faction == _data.PlayerFaction)) continue;
            bool living = Living(world, squad);
            if (squad.Shop is { Building: not null } building) SettleView(world, building);

            if (squad.Shop is { Building: not null } && !living)
            {
                // Nobody of the squad is left alive: its containers keep the goods, as the original's do (artifacts included).
                squad.Shop = null;
                continue;
            }

            if (squad.Shop is { Building: not null } && TemplateOf(squad.TemplateId) is null && !TradesWith(world, squad))
            {
                // A housed squad that keeps no shop traded from its home's containers (StockFor) only for that trade; the house filling goes on.
                squad.Shop = null;
                continue;
            }
            if (squad.Shop is { } shop)
            {
                if (world.Tick < shop.NextRestockTick || TemplateOf(squad.TemplateId) is not { } again) continue;
                Refill(world, squad, shop, again);
            }
            else if (living && TemplateOf(squad.TemplateId) is { } template) MakeShop(world, squad, template);
            else if (squad.HomeBuilding is { } home && HomeTemplateOf(squad.TemplateId) is { } contents && ShopFiller.VendorLinks(contents, FactionRecord(squad.Faction)).Count > 0) FillHome(world, squad, contents, home);
        }
    }

    bool PlayersSquad(World world, Squad squad) => squad.Faction >= 0 && (world.IsPlayerFaction(squad.Faction) || squad.Faction == _data.PlayerFaction);

    /// <summary>Makes a trader squad's shop and its first fill.</summary>
    void MakeShop(World world, Squad squad, SquadTemplate template)
    {
        // A squad with a home building stocks only that building's containers, whether or not it made prisoner or slave squads, and never a squad stock; a home without stock containers
        // gets nothing (FUN_140583a10 / FUN_1404ff5e0 / FUN_14095b210, Verified). Only a squad with no home building has a squad shop (s14-f26).
        var terms = TermsOf(squad, template);
        squad.Shop = squad.HomeBuilding is { } home ? Shop.ForBuilding(home, terms) : new Shop { Terms = terms };
        if (squad.Shop is null) return;

        Refill(world, squad, squad.Shop, template);
    }

    internal static Trader TermsOf(Squad squad, SquadTemplate? template) =>
        new(squad.Town, squad.Faction, template?.BuyMult ?? 1, template?.BuysStolen ?? false, template?.BuysIllegal ?? false);

    /// <summary>
    /// A trade opened with a member of a squad that has no shop yet: a trader squad gets its shop now; any other trades from what the original's
    /// window shows (docs/game/economy.md "Who trades"): a squad with no home building (the Traders Guild's <c>Caravan</c>: <c>vendors</c> but no
    /// <c>is trader</c>) from a stock that is never filled and takes what its members' worn bags hold (<see cref="TakeBags"/>: the pack animals'
    /// loads, <see cref="Items.PackLoad"/>); a housed one from its home's stock containers for that trade (<see cref="Stock"/> drops the shop after
    /// it), none without.
    /// </summary>
    void StockFor(World world, int t)
    {
        if (world.Squads.Find(world.Characters.Cold(t)!.SquadId) is not { Shop: null } squad || PlayersSquad(world, squad)) return;
        if (TemplateOf(squad.TemplateId) is { } template)
        {
            MakeShop(world, squad, template);
            return;
        }
        squad.Shop = UnfilledShop(squad, TemplateRecord(_data, squad.TemplateId));
        if (squad.Shop is { Building: null } carried) TakeBags(world, squad, carried);
    }

    /// <summary>
    /// A homeless squad's new unfilled shop takes what its living members' worn bags hold: the original's window shows those bags merged
    /// (<c>FUN_140953850</c>, docs/game/ui-screens.md section 6). <b>Engine choice</b>: the goods stay in the squad's stock from then on, as a
    /// trader squad's do; what the stock has no room for stays in its bag.
    /// </summary>
    static void TakeBags(World world, Squad squad, Shop shop)
    {
        var table = world.Characters;

        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int slot) || table.Cold(slot) is not { DiedTick: < 0, Inventory.Backpack.Contents: { } bag }) continue;

            foreach (var item in bag.Items.ToList())
                if (shop.Stock.Add(item)) bag.Items.Remove(item);
        }
    }

    /// <summary>The shop of a squad whose template fills none: a housed squad's over its home's stock containers (null without), else an empty stock.</summary>
    internal static Shop? UnfilledShop(Squad squad, SquadTemplate? template) =>
        squad.HomeBuilding is { } home ? Shop.ForBuilding(home, TermsOf(squad, template)) : new Shop { Terms = TermsOf(squad, template) };

    bool TradesWith(World world, Squad squad) => _interactions.Values.Any(i => i.TradesWith(world, squad));

    /// <summary>The squad template <paramref name="id"/> names; null when it names none.</summary>
    internal static SquadTemplate? TemplateRecord(PopulationData data, string id) =>
        data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) ? SquadTemplate.From(r) : null;

    /// <summary>
    /// Whether someone of the squad is alive.
    /// </summary>
    static bool Living(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int slot) && table.Cold(slot) is { DiedTick: < 0 }) return true;
        return false;
    }

    /// <summary>
    /// A building shop whose squad is dropped, made afresh or all dead writes what its trader window changed into its containers
    /// (<see cref="SettleView"/>), which keep the goods for the next shop, as the original's containers keep them.
    /// </summary>
    internal static void Settle(World world, Shop shop) => world.SystemOf<TradeSystem>()?.SettleView(world, shop);

    Meitou.Data.GameRecord? FactionRecord(int faction) => (uint)faction < (uint)_data.Factions.Count ? _data.Db.Find(_data.Factions[faction].Id) : null;

    SquadTemplate? HomeTemplateOf(string id)
    {
        if (!_homeTemplates.TryGetValue(id, out var t))
            _homeTemplates[id] = t = _data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r) && SquadTemplate.From(r) is { } fill && (!fill.IsTrader || fill.Vendors.Count == 0) && (fill.Vendors.Count > 0 || fill.BuildingDesignation is 2 or 3 or 5 or 6 or 8 or 9 or 11) ? fill : null;
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
        StockHome(world, home, template, new Trader(squad.Town, squad.Faction, template.BuyMult, template.BuysStolen, template.BuysIllegal), (ulong)squad.Id, first);
    }

    /// <summary>
    /// A home whose template made no squad (a ruin's or a shed's contents: every list empty, or every count 0) is stocked once when the town is made: the squad factory
    /// (<c>FUN_140583a10</c>, <b>Verified</b>) calls the building filler <c>FUN_14095b210</c> with the specials when the template has vendor lists (its own or its building designation's, by the faction)
    /// or special items, and no refresh follows (no platoon restocks it). Base data: 26 empty templates hold vendors or special items (the lost libraries, ghost bars, Flotsam safehouse).
    /// </summary>
    public void StockEmptyHome(World world, Meitou.Data.World.TownBuilding home, SquadTemplate template, int site, int faction)
    {
        if (!HoldsStock(template, FactionRecord(faction))) return;
        StockHome(world, home, template, new Trader(site, faction, template.BuyMult, template.BuysStolen, template.BuysIllegal), Rng.StableHash(home.Key), true);
    }

    bool HoldsStock(SquadTemplate template, Meitou.Data.GameRecord? faction) =>
        ShopFiller.VendorLinks(template, faction).Count > 0 || _data.Db.Find(template.Id) is { } r && (r.GetReferences("special items").Count > 0 || r.GetReferences("special map items").Count > 0);

    /// <summary>
    /// The building filler (<c>FUN_14095b210</c>, <b>Observed</b>) returns at once for a building that is another object's child: its location hand <c>+0x88</c> not null (set only from a parent), its parent
    /// <c>+0x238</c> set (a turret mounted on a building) or its door flag <c>+0x1a0</c> (<c>DoorStuff</c>, made only for a parent building's door). A level-data home is none of these; a turret post is.
    /// </summary>
    void StockHome(World world, Meitou.Data.World.TownBuilding home, SquadTemplate template, Trader terms, ulong key, bool first)
    {
        if (home.Turret is not null) return;

        var boxes = StockBoxes(world, home);
        if (boxes.Count == 0 || boxes.Any(b => b.Grid.Items.Any(i => !i.Unique))) return;
        if (Shop.ForBuilding(home, terms) is not { } merged) return;

        // The filler unlocks every stock container, then keeps those that can hold what the lists make (FUN_14095b210, FUN_14095a5b0).
        foreach (var box in boxes)
            if (box.Fixture.Lock is { } l) l.Locked = false;

        var usable = HomeStock.Usable(boxes, VendorEntries(template, FactionRecord(terms.Faction)));
        if (usable.Count == 0) return;

        merged.Fills = (int)world.Tick;
        merged.Stock = FillGrid(merged);
        ShopFiller.Fill(merged, template, Market(world), _data, world.Seed, key, specials: first);

        // Engine: the special items are spread with the vendor items (the original gives each to a container of a shuffle of its own first).
        var rng = new RandomStream(world.Seed, Rng.Mix(key ^ SpreadSalt ^ ((ulong)merged.Fills << 40)), RngPurpose.Trade);
        HomeStock.Spread(usable, merged.Stock.Items, Market(world).BaseValue, ref rng);

        foreach (var box in usable)
            if (box.Fixture.Lock is { } l) l.Locked = box.Grid.Items.Count > 0;   // FUN_1409565e0 locks the filled ones
    }

    /// <summary>
    /// The home's stock containers (<see cref="Meitou.Data.World.TownFixture.HoldsShopStock"/>) with their records' storage rules.
    /// </summary>
    List<StockBox> StockBoxes(World world, Meitou.Data.World.TownBuilding home)
    {
        var boxes = new List<StockBox>();

        foreach (var f in home.Fixtures)
            if (f.HoldsShopStock && world.Fixtures.Find(f.Id) is { Contents: not null } box) boxes.Add(BoxOf(box));

        return boxes;
    }

    /// <summary>
    /// A building shop's stock containers that are registered, in its order; <paramref name="all"/> false when some are not (their zone not loaded).
    /// </summary>
    List<StockBox> BoxesOf(World world, Shop shop, out bool all)
    {
        var boxes = new List<StockBox>(shop.Containers.Count);

        foreach (var id in shop.Containers)
            if (world.Fixtures.Find(id) is { Contents: not null } box) boxes.Add(BoxOf(box));

        all = boxes.Count == shop.Containers.Count;
        return boxes;
    }

    /// <summary>
    /// A stock container with its record's storage rules.
    /// </summary>
    StockBox BoxOf(Locks.Fixture box)
    {
        var record = _data.Db.Find(box.RecordId);
        return new StockBox(box, record is null ? null : Data.Gameplay.Construction.MachineData.From(_data.Db, record));
    }

    /// <summary>
    /// Every entry of the vendor lists the template stocks from (<see cref="ShopFiller.VendorLinks"/>).
    /// </summary>
    List<VendorEntry> VendorEntries(SquadTemplate template, Data.GameRecord? faction)
    {
        var entries = new List<VendorEntry>();

        foreach (var link in ShopFiller.VendorLinks(template, faction))
        {
            if (_data.Db.Find(link.Id) is { Type: Data.Fcs.FcsRecordType.VendorList } record)
                entries.AddRange(VendorList.From(_data.Db, record).Entries);
        }

        return entries;
    }

    /// <summary>
    /// The rows of the scratch grid a building shop's fill is made in before it is spread over the containers: room for any fill.
    /// </summary>
    const int FillRows = 1024;

    /// <summary>
    /// Fills the shop again (the specials on its first fill), sets the squad's purse, spreads a building shop's new stock over its containers and
    /// relocks those that hold some, and sets the next restock tick. A building shop none of whose containers is registered waits: the original's
    /// filler returns while the building or its interior is missing and is called again (docs/game/economy.md "Building shops").
    /// </summary>
    void Refill(World world, Squad squad, Shop shop, SquadTemplate template)
    {
        var home = squad.HomeBuilding;

        if (shop.Building is not null)
        {
            // Not while an item lifted from the window is held (it goes back to the grid it came from), nor before a save's goods are handed over.
            bool held = shop.View.Live && StockHeld(world);
            if (home is null || BoxesOf(world, shop, out _).Count == 0 || held || shop.View.Pending) return;

            shop.Stock = FillGrid(shop);
        }

        squad.Money = ShopFiller.Fill(shop, template, Market(world), _data, world.Seed, (ulong)squad.Id, specials: shop.Fills == 0);

        if (shop.Building is not null)
        {
            SpreadStock(world, shop, home!, template, (ulong)squad.Id);
            Relock(world, home!);
            ReopenView(world, shop);
        }

        shop.NextRestockTick = world.Tick + (long)(template.VendorsRefreshTime / BodySystem.HoursPerTick);
    }

    /// <summary>
    /// The scratch grid a building shop's fill is made in (<see cref="FillRows"/> rows of its trader window's columns).
    /// </summary>
    static Inventory FillGrid(Shop shop) => new(shop.ViewSize.Columns, FillRows) { StackBonusMult = Shop.StockStackMult };

    /// <summary>
    /// A building shop's new stock (its fill's scratch grid) is spread over its containers as a stocked home's (<see cref="HomeStock"/>): the
    /// original's building filler (<c>FUN_14095b210</c>) fills a trader's home the same way, after clearing the containers but their unique items
    /// (<c>FUN_14074a5b0</c>). What no usable container takes is lost.
    /// </summary>
    void SpreadStock(World world, Shop shop, Meitou.Data.World.TownBuilding home, SquadTemplate template, ulong key)
    {
        var boxes = StockBoxes(world, home).Where(b => shop.Holds(b.Fixture.Id)).ToList();

        foreach (var box in boxes) box.Grid.Items.RemoveAll(i => !i.Unique);

        var usable = HomeStock.Usable(boxes, VendorEntries(template, FactionRecord(shop.Terms.Faction)));
        var rng = new RandomStream(world.Seed, Rng.Mix(key ^ SpreadSalt ^ ((ulong)shop.Fills << 40)), RngPurpose.Trade);
        HomeStock.Spread(usable, shop.Stock.Items, Market(world).BaseValue, ref rng);
    }

    /// <summary>
    /// The building filler unlocks the stock containers it fills (<c>FUN_14095b210</c>), then locks each that holds something (<c>FUN_1409565e0</c>,
    /// #328).
    /// </summary>
    static void Relock(World world, Meitou.Data.World.TownBuilding home)
    {
        foreach (var f in home.Fixtures)
        {
            if (f.HoldsShopStock && world.Fixtures.Find(f.Id) is { Lock: { } l } box)
                l.Locked = box.Contents is { Items.Count: > 0 };
        }
    }

    /// <summary>
    /// The shop a character trades for: its squad's (null: not a trader).
    /// </summary>
    public static Shop? ShopOf(World world, CharacterCold cold) => cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId)?.Shop : null;

    /// <summary>Prepares a building shop's shared projection before a trade mutates it.</summary>
    void PrepareView(World world, Shop shop)
    {
        if (shop.Building is null) return;
        SettleView(world, shop);
        if (!shop.View.Live) shop.View.Build(shop, BoxesOf(world, shop, out _));
    }

    /// <summary>Writes each open projection into its physical containers.</summary>
    void WriteOpenView(World world)
    {
        foreach (var squad in world.Squads.All)
            if (squad.Shop is { Building: not null } shop) SettleView(world, shop);
    }

    /// <summary>Settles all physical stock before saving or returning a held item.</summary>
    internal void SettleAll(World world) => WriteOpenView(world);

    /// <summary>Flushes the player's held-stock mutation before another command can use that stock.</summary>
    internal void SettleOpenStock(World world, PlayerId player) => _interactions.GetValueOrDefault(player)?.SettleStock(world);

    /// <summary>
    /// Applies trading deltas to physical stock and refreshes a projection when another actor changed its containers.
    /// </summary>
    internal void SettleView(World world, Shop shop)
    {
        if (!shop.View.Live) return;
        var boxes = BoxesOf(world, shop, out bool all);
        if (!all && shop.View.Pending) return;
        bool external = !shop.View.Pending && shop.View.ContainersChanged(boxes);
        shop.View.Write(shop, boxes);
        if (external) shop.View.Build(shop, boxes);
        if (!Shows(world, shop) && !StockHeld(world)) shop.View.Drop(shop);
    }

    /// <summary>Restocks rebuild any open projection, otherwise discard the fill's scratch grid.</summary>
    void ReopenView(World world, Shop shop)
    {
        if (Shows(world, shop)) shop.View.Build(shop, BoxesOf(world, shop, out _));
        else shop.View.Drop(shop);
    }

    bool Shows(World world, Shop shop) => _interactions.Values.Any(i => i.Shows(world, shop));
    static bool StockHeld(World world) => world.Players.All.Any(p => p.State.Held?.From.Holder == ItemHolder.Stock);

    readonly SquadWages _wages;
}

public abstract record TradeCommand : InventoryCommand { public ulong SessionId { get; init; } }
