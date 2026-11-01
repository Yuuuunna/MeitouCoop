using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Trading;

public sealed partial class TradeSystem
{
    /// <summary>One player's transient interaction; stock, prices and restocking belong to the parent world system.</summary>
    sealed class Interaction(TradeSystem owner, PlayerId playerId)
    {
        PopulationData data => owner.data;
        CharacterAssembly assembly => owner.assembly;
        TradeSettings Settings => owner.Settings;
        Crime.CrimeSystem? Crimes => owner.Crimes;
        Market Market(World world) => owner.Market(world);
        CharacterId customer = CharacterId.None, trader = CharacterId.None;
        int customerFaction = -1;
        bool open, attributed;
        TradeSession session = new();
        long approachUntil;
        CharacterControl? authorization;
        ulong membershipRevision;
        public ulong SessionId { get; private set; }
        public PlayerId Actor => playerId;
        public TradeView? View { get; private set; }

        public void Input(World world, SimCommand command)
        {
            if (command is TradeCommand scoped && (command.Issuer.HasValue != attributed ||
                command.Issuer is not null && scoped.SessionId == 0 || scoped.SessionId != 0 && scoped.SessionId != SessionId)) return;
            switch (command)
            {
                case OpenTrade o:
                    if (o.Customer == o.Trader) return;
                    if (world.SystemOf<Dialogue.DialogueSystem>()?.IsServingOther(o.Trader, playerId) == true)
                    { world.Post(playerId, "This character is busy."); return; }
                    if (!world.MayOrder(command, o.Customer) || !world.Characters.TryResolveNext(o.Customer, out int initial)) return;
                    int faction = world.Characters.Cold(initial)!.Faction;
                    if (faction < 0) return;
                    if (command.Issuer is not null && (command.FactionGrantRevision == 0 ||
                        world.Players.Find(playerId)!.MembershipRevision(faction) != command.FactionGrantRevision)) return;
                    Close(world);
                    customer = o.Customer;
                    trader = o.Trader;
                    customerFaction = faction;
                    attributed = command.Issuer.HasValue;
                    authorization = command.Issuer is null ? null : world.Players.ControlOf(customer);
                    membershipRevision = command.FactionGrantRevision;
                    SessionId = owner.nextSession = checked(owner.nextSession + 1);
                    approachUntil = world.Tick + (long)(Settings.ApproachSeconds / world.TickSeconds);
                    if (!Resolve(world, out int c, out int t, out _))
                    {
                        if (world.Characters.TryResolveNext(trader, out int who) && world.Characters.Cold(who) is { } tc && AggroSystem.IsEnemy(data.Relations, faction, tc.Faction))
                            Defer(world, new Combat.AttackOrder([customer], trader) { Tick = world.Tick + 1 });
                        Close(world);
                        break;
                    }
                    owner.Hold(world, trader, this);
                    if (!InReach(world, c, t)) Defer(world, new MoveOrder([customer], world.Characters.Next[t].Position) { Tick = world.Tick + 1 });
                    break;
                case CloseTrade:
                    Close(world);
                    break;
                case BuyItem buy when open:
                    if (command.Issuer is not null && buy.Quantity <= 0) return;
                    if (CanSpend(world)) Buy(world, buy);
                    break;
                case SellItem sell when open:
                    if (command.Issuer is not null && sell.Quantity <= 0) return;
                    Sell(world, sell);
                    break;
                case ArrangeStock when open:
                    if (Resolve(world, out _, out _, out var arranged) && InventoryMatches(world, (TradeCommand)command, arranged!.Stock)) InventoryCommands.Arrange(arranged.Stock);
                    break;
            }
        }

        public void Advance(World world)
        {
            if (!customer.IsNone)
            {
                if (!Resolve(world, out int c, out int t, out _)) Close(world);
                else if (InReach(world, c, t)) { open = true; owner.Hold(world, trader, this); }
                else if (open || world.Tick >= approachUntil) Close(world);
                else owner.Hold(world, trader, this);
            }
            RefreshView(world);
        }

        public void RefreshView(World world) => View = open && Resolve(world, out int c, out int t, out var shop) ? Publish(world, c, t, shop!) : null;

        public TradeView? PublishAuthorized(World world) => attributed && authorization is not null && open
            && Resolve(world, out int c, out int t, out var shop) ? Publish(world, c, t, shop!) : null;

        public void Close(World world)
        {
            session.Active = false;
            session = new();
            owner.Release(world, trader, this);
            customer = trader = CharacterId.None;
            customerFaction = -1;
            open = false;
            attributed = false;
            View = null;
            authorization = null;
            membershipRevision = 0;
            SessionId = 0;
        }

        void Defer(World world, SimCommand command) => world.Defer(authorization is { } grant
            ? command with { Issuer = playerId, ControlGrants = [grant] } : command);

        bool CanSpend(World world) => authorization is null || world.Players.Find(playerId)?.HasPermission(customerFaction, FactionPermission.SpendFunds) == true;
        bool CanReadFunds(World world) => authorization is null || world.Players.Find(playerId)?.HasPermission(customerFaction, FactionPermission.ReadFunds) == true;
        PlayerState Player(World world) => world.Players.Find(playerId)!.State;
        FactionState Funds(World world) => world.Factions.GetOrCreate(customerFaction);
        static long Budget(long payer, long receiver) => Math.Min(Math.Max(0, payer), receiver < 0 ? long.MaxValue : long.MaxValue - receiver);

        static bool InventoryMatches(World world, TradeCommand command, Inventory source) =>
            world.InventoryMatches(command, source);

        // ------------------------------------------------------------------ trading

        bool Resolve(World world, out int c, out int t, out Shop? shop)
        {
            var table = world.Characters;
            shop = null;
            t = -1;
            if (!table.TryResolveNext(customer, out c) || !table.TryResolveNext(trader, out t)) return false;
            var cc = table.Cold(c)!;
            if (cc.Faction != customerFaction) return false;
            if (authorization is { } grant && (!world.Players.Validate(grant) ||
                world.Players.Find(playerId)?.MembershipRevision(customerFaction) != membershipRevision)) return false;
            var tc = table.Cold(t)!;
            if (!cc.IsPlayer || cc.Medical is { Incapacitated: true } || tc.Medical is { Incapacitated: true } || tc.InCombat || cc.Inventory is null) return false;
            if (AggroSystem.IsEnemy(data.Relations, cc.Faction, tc.Faction)) return false;
            shop = ShopOf(world, tc);
            return shop is not null && (!open || InReach(world, c, t));
        }

        bool InReach(World world, int c, int t)
        {
            var a = world.Characters.Current[c].Position;
            var b = world.Characters.Current[t].Position;
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return dx * dx + dz * dz <= Settings.Range * Settings.Range;
        }

        static ItemInstance? Find(Inventory inventory, InventoryItemToken token)
        {
            var item = inventory.All().ElementAtOrDefault(token.Index);
            return item is not null && item.Record == token.Record && item.Section == token.Section && item.X == token.X && item.Y == token.Y && item.Quantity == token.Quantity
                && item.Quality == token.Quality && (item.Contents is null || item.Quantity == 1) ? item : null;
        }

        // ------------------------------------------------------------------ the held item (PlayerSystem, Items/HeldItems.cs)

        /// <summary>The open trade's stock and its customer while the trade's windows show; null when no trade is open.</summary>
        internal Inventory? OpenStock(World world, out CharacterId buyer)
        {
            buyer = CharacterId.None;
            if (!open || !Resolve(world, out _, out _, out var shop)) return null;
            buyer = customer;
            return shop!.Stock;
        }

        /// <summary>
        /// How many of <paramref name="units"/> of a held item the payer can afford (the drop caps the quantity so, <c>FUN_140715560</c>); none: "You can't afford that." or "The shopkeeper can't
        /// afford that.".
        /// </summary>
        internal int AffordHeld(World world, ItemInstance item, int units, bool sell)
        {
            if (!Resolve(world, out int c, out int t, out var shop)) return 0;
            var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
            int n = Affordable(Market(world), item, Math.Clamp(units, 0, item.Quantity), sell, shop!.Terms, sell ? world.Characters.Cold(c)!.Faction : squad.Faction,
                sell ? Budget(squad.Money, Funds(world).Money) : CanSpend(world) ? Budget(Funds(world).Money, squad.Money) : 0);
            if (n == 0) world.Post(playerId, sell ? "The shopkeeper can't afford that." : "You can't afford that.");
            return n;
        }

        /// <summary>A held stolen item put on the stock of a shop that is no fence: the fence roll (8.4); caught, "Got caught selling stolen item." and the pending Fencing crime (true).</summary>
        internal bool CaughtSelling(World world, ItemInstance item)
        {
            if (!Resolve(world, out int c, out int t, out var shop)) return false;
            var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
            if (!item.IsStolenFor(world.Characters.Cold(c)!.Faction) || shop!.Terms.BuysStolen || !Caught(world, c, item, squad, shop.Terms)) return Smuggling(world, c, item, shop!.Terms);
            world.Post(playerId, "Got caught selling stolen item.");
            if (Crimes is { } crimes && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, trader, Crime.CrimeSystem.MinTimer))
                crimes.Book(world, c, squad.Faction, trader);
            return true;
        }

        /// <summary>
        /// The smuggling check of a sale (<c>FUN_140794e80</c>, <b>Observed</b>, run after the fence check): an item the culture of the trader's town's faction lists as illegal is refused by a trader that
        /// does not buy illegal goods, "Attempted smuggling!" (code 0xf), and the trader says THIEF_CAUGHT_STEALING_FROM_ME (8) at the seller unless it buys stolen goods. True when refused.
        /// </summary>
        bool Smuggling(World world, int seller, ItemInstance item, Trader terms)
        {
            if (terms.BuysIllegal || (uint)terms.Site >= (uint)data.SiteFactions.Count || Market(world).FactionCulture(data.SiteFactions[terms.Site]) is not { } culture || !culture.Illegal.Contains(item.Record)) return false;
            world.Post(playerId, "Attempted smuggling!");
            if (!terms.BuysStolen) world.Defer(new Dialogue.DialogueEvent(trader, world.Characters.IdOf(seller), 8) { Tick = world.Tick + 1 });
            return true;
        }

        /// <summary>
        /// Pays for the first <paramref name="units"/> of a held item that changed hands (priced before it moves, a buyback at its receipt's coins): the price goes from the buyer's purse to
        /// the seller's. Returns the moved units' receipt (null when this undid an earlier trade of this window).
        /// </summary>
        internal bool TryPayHeld(World world, ItemInstance item, int units, bool sell, out TradeReceipt? receipt)
        {
            receipt = null;
            if (units <= 0 || units > item.Quantity || !Resolve(world, out int c, out int t, out var shop) || !sell && !CanSpend(world)) return false;
            var cold = world.Characters.Cold(c)!;
            var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
            var m = Market(world);
            int holder = sell ? cold.Faction : squad.Faction;
            var paid = Receipt(m, item, units, sell, shop!.Terms, holder);
            long price = Price(m, item, units, sell, shop.Terms, holder);
            if (price < 0 || price > (sell ? Budget(squad.Money, Funds(world).Money) : Budget(Funds(world).Money, squad.Money))) return false;
            if (sell) { squad.Money -= price; Funds(world).Money += price; }
            else { Funds(world).Money -= price; squad.Money += price; }
            receipt = paid;
            world.Post(playerId, $"{cold.Name} {(sell ? "sold" : "bought")} {Units(units, item.Name)} for c.{price}");
            return true;
        }

        void Buy(World world, BuyItem buy)
        {
            if (!Resolve(world, out int c, out int t, out var shop)) return;
            if (!InventoryMatches(world, buy, shop!.Stock)) return;
            var cold = world.Characters.Cold(c)!;
            var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
            if (Find(shop!.Stock, buy.Item) is not { } item) return;
            if (item.Contents is { Items.Count: > 0 }) { world.Post(playerId, BagNotEmpty); return; }
            var m = Market(world);
            int units = Affordable(m, item, Math.Clamp(buy.Quantity, 1, item.Quantity), false, shop.Terms, squad.Faction, Budget(Funds(world).Money, squad.Money));
            if (units == 0)
            {
                world.Post(playerId, "You can't afford that.");
                return;
            }
            var receipt = Receipt(m, item, units, false, shop.Terms, squad.Faction);
            var bought = item.WithQuantity(units);
            bought.Buyback = receipt;
            long price = Price(m, item, units, false, shop.Terms, squad.Faction);
            int placed;
            if (item.Contents is not null)
            {
                // An empty bag moves itself (a copy would have no contents inventory); its receipt is replaced (none: this undid a sale).
                placed = TransferContainer(shop.Stock, buy.Item, cold.Inventory!, buy.Section, buy.X, buy.Y, buy.Equip, cold);
                if (placed > 0) item.Buyback = receipt;
                if (placed > 0 && InventoryCommands.IsEquipmentSection(item.Section)) assembly.RefreshEquipment(cold);
            }
            else if (buy.Section == "backpack_attach") placed = 0;
            else if (buy.Section is { } section) placed = PlaceAt(cold.Inventory!, bought, units, section, buy.X, buy.Y);
            else if (buy.Equip && InventoryCommands.TryEquip(cold.Inventory!, bought.WithQuantity(1), cold))
            {
                // One unit is worn; the rest of a stack goes where it fits.
                assembly.RefreshEquipment(cold);
                placed = 1;
                if (units > 1)
                {
                    var rest = bought.WithQuantity(units - 1);
                    rest.Buyback = bought.Buyback?.Skip(1);
                    placed += Place(cold.Inventory!, rest, units - 1);
                }
            }
            else placed = Place(cold.Inventory!, bought, units);
            if (placed == 0)
            {
                world.Post(playerId, "No room for that item.");
                return;
            }
            if (placed < units) price = Price(m, item, placed, false, shop.Terms, squad.Faction);
            if (item.Contents is null) Take(shop.Stock, item, placed);
            Funds(world).Money -= price;
            squad.Money += price;
            world.Post(playerId, $"{cold.Name} bought {Units(placed, item.Name)} for c.{price}");
        }

        void Sell(World world, SellItem sell)
        {
            if (!Resolve(world, out int c, out int t, out var shop)) return;
            var cold = world.Characters.Cold(c)!;
            if (!InventoryMatches(world, sell, cold.Inventory!)) return;
            var tc = world.Characters.Cold(t)!;
            var squad = world.Squads.Find(tc.SquadId)!;
            if (Find(cold.Inventory!, sell.Item) is not { } item || !InventoryCommands.IsMovableSection(item.Section)) return;
            if (item.LockedOn) { world.Post(playerId, "This item is locked in place and cannot be removed."); return; }
            if (item.Contents is { Items.Count: > 0 }) { world.Post(playerId, BagNotEmpty); return; }
            var m = Market(world);
            int units = Affordable(m, item, Math.Clamp(sell.Quantity, 1, item.Quantity), true, shop!.Terms, cold.Faction, Budget(squad.Money, Funds(world).Money));
            if (units == 0)
            {
                world.Post(playerId, "The shopkeeper can't afford that.");
                return;
            }
            if (item.IsStolenFor(cold.Faction) && !shop.Terms.BuysStolen && Caught(world, c, item, squad, shop.Terms))
            {
                world.Post(playerId, "Got caught selling stolen item.");
                if (Crimes is { } crimes && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, trader, Crime.CrimeSystem.MinTimer))
                    crimes.Book(world, c, squad.Faction, trader);
                return;
            }
            if (Smuggling(world, c, item, shop.Terms)) return;
            var receipt = Receipt(m, item, units, true, shop.Terms, cold.Faction);
            var sold = item.WithQuantity(units);
            sold.Buyback = receipt;
            long price = Price(m, item, units, true, shop.Terms, cold.Faction);
            bool equipped = InventoryCommands.IsEquipmentSection(item.Section);
            int placed = item.Contents is not null
                ? TransferContainer(cold.Inventory!, sell.Item, shop.Stock, sell.X is not null ? "main" : null, sell.X ?? 0, sell.Y, false, null)
                : sell.X is { } sx ? PlaceAt(shop.Stock, sold, units, "main", sx, sell.Y) : Place(shop.Stock, sold, units);
            if (placed == 0)
            {
                world.Post(playerId, "No room for that item.");
                return;
            }
            if (item.Contents is not null) item.Buyback = receipt;
            if (placed < units) price = Price(m, item, placed, true, shop.Terms, cold.Faction);
            if (item.Contents is null) Take(cold.Inventory!, item, placed);
            if (equipped) assembly.RefreshEquipment(cold);
            squad.Money -= price;
            Funds(world).Money += price;
            world.Post(playerId, $"{cold.Name} sold {Units(placed, item.Name)} for c.{price}");
        }

        /// <summary>
        /// The chance a shop that is no fence lets a stolen item through (8.4, <c>FUN_140793490</c>): lerp(thievery / 100, 0.4, 1.25) of the seller, halved when the item was stolen from the
        /// shop's faction; 1 or more never gets caught. The roll is the seller's and the tick's.
        /// </summary>
        public static float FenceChance(float thievery, bool stolenFromShopFaction)
        {
            float p = 0.4f + (1.25f - 0.4f) * (thievery / 100f);
            return stolenFromShopFaction ? p * 0.5f : p;
        }

        /// <summary>The odds <c>FUN_140793490</c> answers for goods nobody looks for (<c>DAT_14169bb44</c>, <b>Verified</b>: 1.1).</summary>
        const float NeverCaught = 1.1f;

        /// <summary>The faction the shop's squad sits in regards another as its own (the game's <c>FUN_1406b2630</c>: the same faction, or a relation of 50 or more).</summary>
        bool Allied(int a, int b) => a == b || data.Relations.Contains(a) && data.Relations.Contains(b) && data.Relations.IsAlly(a, b);

        /// <summary>
        /// The chance a shop that is no fence lets the seller's stolen item through (<c>FUN_140793490</c>, <b>Observed</b>): 1 or more, never caught, for a shop that buys stolen goods, for goods stolen
        /// from a bandit or adventurer faction, and for goods stolen from a faction that is neither the shop's, nor allied to it, nor in the shop's town (the victim's town is not tracked:
        /// read here as another town); else <see cref="FenceChance"/>.
        /// </summary>
        float FenceOdds(float thievery, ItemInstance item, int shopFaction, Trader terms)
        {
            int from = item.StolenFrom;
            if (from != shopFaction && from >= 0)
            {
                if (terms.BuysStolen || data.Relations.Factions[from].FundamentalType is FundamentalType.Bandit or FundamentalType.Adventurer) return 1;
                if (!Allied(shopFaction, from)) return NeverCaught;
            }
            return FenceChance(thievery, from == shopFaction);
        }

        bool Caught(World world, int seller, ItemInstance item, Squad shopSquad, Trader terms)
        {
            var cold = world.Characters.Cold(seller)!;
            float p = FenceOdds(cold.Stats?[StatsEnumerated.Thieving] ?? 0, item, shopSquad.Faction, terms);
            bool caught = p < 1 && Rng.Float(Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(seller)), RngPurpose.Trade, (ulong)world.Tick)) >= p;
            // The seller trains thievery whenever the odds are below 0.98 (<c>FUN_140793770</c>): 5 x 2 for a fence that works, 20 x 2 for one that is caught.
            if (p < Crime.CrimeSystem.FenceXpBelow) Crimes?.TrainThievery(world, seller, caught ? Crime.CrimeSystem.FenceCaughtXp : Crime.CrimeSystem.FenceXp);
            return caught;
        }

        /// <summary>How many of <paramref name="wanted"/> units the payer's <paramref name="money"/> covers (the trade window splits the stack to what is affordable).</summary>
        int Affordable(Market m, ItemInstance item, int wanted, bool sell, Trader terms, int holderFaction, long money)
        {
            for (int n = wanted; n > 0; n--)
                if (Price(m, item, n, sell, terms, holderFaction) <= money) return n;
            return 0;
        }

        // Containers move whole: stack copies intentionally omit Contents. Transfer validates the destination before changing either inventory.
        static int TransferContainer(Inventory from, InventoryItemToken token, Inventory into, string? section, int x, int y, bool equip, CharacterCold? wearer)
        {
            if (section is not (null or "main" or "backpack_attach")) return 0;
            if (section == "backpack_attach") return InventoryCommands.Transfer(from, token, into, section, 0, 0, wearer: wearer) ? 1 : 0;
            if (equip && InventoryCommands.Transfer(from, token, into, "backpack_attach", 0, 0, wearer: wearer)) return 1;
            if (section == "main" && InventoryCommands.Transfer(from, token, into, section, x, y)) return 1;
            var item = Find(from, token);
            return item is not null && into.FindCell("main", item.Width, item.Height, out int cx, out int cy)
                && InventoryCommands.Transfer(from, token, into, "main", cx, cy) ? 1 : 0;
        }

        bool IsBuyback(ItemInstance item, bool sell) => item.Buyback is { } r && ReferenceEquals(r.Session, session) && r.Sell == sell;

        long Price(Market market, ItemInstance item, int units, bool sell, Trader terms, int holder) => IsBuyback(item, sell)
            ? item.Buyback!.Price(units) : market.StackPrice(item.WithQuantity(units), sell, terms, holder);

        TradeReceipt? Receipt(Market market, ItemInstance item, int units, bool sell, Trader terms, int holder)
        {
            if (IsBuyback(item, sell)) return null;
            var coins = new long[units];
            long previous = 0;
            for (int n = 1; n <= units; n++)
            {
                long total = Price(market, item, n, sell, terms, holder);
                coins[n - 1] = total - previous;
                previous = total;
            }
            return new(session, !sell, coins);
        }

        /// <summary>
        /// Puts the units at the cell they were dropped on (the trade window's <c>placeItemFromMouse</c>): the cell of <paramref name="section"/> in <paramref name="root"/> (the backpack's
        /// contents for <c>backpack_content</c>) when the footprint is inside the grid, free and the stack within the limit; otherwise wherever they fit, as <see cref="Place"/>.
        /// </summary>
        static int PlaceAt(Inventory root, ItemInstance copy, int units, string section, int x, int y)
        {
            var grid = section == "main" ? root : section == "backpack_content" ? root.Backpack?.Contents : null;
            if (grid is not null && x >= 0 && y >= 0 && x + copy.Width <= grid.Width && y + copy.Height <= grid.Height && units <= grid.LimitOf(copy)
                && grid.IsFree(section, x, y, copy.Width, copy.Height))
            {
                copy.Section = section;
                copy.X = x;
                copy.Y = y;
                grid.Items.Add(copy);
                return units;
            }
            return Place(root, copy, units);
        }

        static int Place(Inventory into, ItemInstance copy, int units)
        {
            copy.Section = "main";
            return into.Add(copy) ? units : units - copy.Quantity;
        }

        /// <summary>Takes <paramref name="units"/> off an item's stack, removing it when none are left.</summary>
        static void Take(Inventory from, ItemInstance item, int units)
        {
            if (units >= item.Quantity) from.Remove(item);
            else item.Quantity -= units;
        }

        static string Units(int n, string name) => n == 1 ? name : $"{n} x {name}";

        /// <summary>A bag trades only empty (engine choice; its contents would go unpriced and unchecked for theft).</summary>


        TradeView Publish(World world, int c, int t, Shop shop)
        {
            var m = Market(world);
            var cold = world.Characters.Cold(c)!;
            var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
            float thievery = cold.Stats?[StatsEnumerated.Thieving] ?? 0;
            TradeItemView View(ItemInstance item, int index, bool sell, int holder)
            {
                long value = m.UnitPrice(item, false, shop.Terms, holder), sellValue = m.UnitPrice(item, true, shop.Terms, holder);
                bool buyback = IsBuyback(item, sell);
                if (buyback)
                {
                    if (sell) sellValue = Price(m, item, 1, true, shop.Terms, holder);
                    else value = Price(m, item, 1, false, shop.Terms, holder);
                }
                bool stolen = item.IsStolenFor(holder);
                return new(index, item.Record, item.Name, item.Section, item.X, item.Y, item.Width, item.Height, item.Quantity,
                    item.Quality, Price(m, item, item.Quantity, sell, shop.Terms, holder), Price(m, item, 1, sell, shop.Terms, holder), stolen)
                {
                    Buyback = buyback, Container = item.Contents is not null,
                    Material = item.MaterialId, Colour = item.ColourId, Charge = item.ChargeFraction, Value = value, SellValue = sellValue, Weight = item.Weight, PriceFactor = m.TraderFactor(item, shop.Terms, holder),
                    MarketFactor = m.MarketFactor(shop.Terms.Site, item.Record), Company = item.CompanyId, Level = item.Level, Charges = item.Charges, MaxCharges = item.MaxCharges, StolenFrom = stolen ? item.StolenFrom : -1,
                    FenceChance = sell && stolen && item.StolenFrom == squad.Faction ? Math.Clamp(FenceChance(thievery, true), 0, 1) : null,
                    FenceOdds = sell && stolen && !shop.Terms.BuysStolen && FenceOdds(thievery, item, squad.Faction, shop.Terms) is < 1 and var odds ? Math.Max(odds, 0) : null,
                    Illegal = sell && (uint)shop.Terms.Site < (uint)data.SiteFactions.Count && m.FactionCulture(data.SiteFactions[shop.Terms.Site]) is { } culture && culture.Illegal.Contains(item.Record),
                };
            }
            // Only the stock grid's own items, at their All() indices (which tokens use): a bag's contents are not loose stock.
            var stock = new List<TradeItemView>(shop.Stock.Items.Count);
            int at = 0;
            foreach (var item in shop.Stock.Items)
            {
                stock.Add(View(item, at, false, squad.Faction));
                at += 1 + (item.Contents?.All().Count() ?? 0);
            }
            var goods = cold.Inventory!.All().Select((item, index) => (item, index)).Where(p => InventoryCommands.IsMovableSection(p.item.Section))
                .Select(p => View(p.item, p.index, true, cold.Faction)).ToArray();
            var held = Player(world).Held switch
            {
                { From.Holder: ItemHolder.Stock } h => View(h.Item, -1, false, squad.Faction),
                { From.Holder: ItemHolder.Character } h when h.From.Character == customer => View(h.Item, -1, true, cold.Faction),
                _ => null,
            };
            return new TradeView(customer, trader, world.Characters.Cold(t)!.Name, squad.Money, CanReadFunds(world) ? Funds(world).Money : 0, shop.Stock.Width, shop.Stock.Height, stock, goods, shop.Terms.BuysStolen, shop.Terms.BuysIllegal)
            {
                Held = held, Unused = shop.UnusedCells, SessionId = SessionId, FundsVisible = CanReadFunds(world), CanSpendFunds = CanSpend(world),
                StockRevision = world.InventoryRevision(shop.Stock), GoodsRevision = world.InventoryRevision(cold.Inventory!),
            };
        }
    }
}
