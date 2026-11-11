using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Trading;

public sealed partial class TradeSystem
{
    /// <summary>One player's transient interaction; stock, prices and restocking belong to the parent world system.</summary>
    sealed class Interaction(TradeSystem owner, PlayerId playerId)
    {
        PopulationData Data => owner._data;
        CharacterAssembly Assembly => owner._assembly;
        TradeSettings Settings => owner.Settings;
        Crime.CrimeSystem? Crimes => owner.Crimes;
        Market Market(World world) => owner.Market(world);
        CharacterId _customer = CharacterId.None, _trader = CharacterId.None;
        int _customerFaction = -1;
        bool _open, _attributed;
        TradeSession _session = new();
        Dialogue.WalkUp? _approach;
        CharacterControl? _authorization;
        ulong _membershipRevision;
        public ulong SessionId { get; private set; }
        public PlayerId Actor => playerId;
        public TradeView? View { get; private set; }

        public void Input(World world, SimCommand command)
        {
            if (command is TradeCommand scoped && (command.Issuer.HasValue != _attributed ||
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
                    _customer = o.Customer;
                    _trader = o.Trader;
                    _customerFaction = faction;
                    _attributed = command.Issuer.HasValue;
                    _authorization = command.Issuer is null ? null : world.Players.ControlOf(_customer);
                    _membershipRevision = command.FactionGrantRevision;
                    SessionId = owner._nextSession = checked(owner._nextSession + 1);
                    _approach = new(Settings.ApproachSeconds, Defer);
                    if (world.Characters.TryResolveNext(_trader, out int stocked)) owner.StockFor(world, stocked);
                    if (!Resolve(world, out int c, out int t, out _))
                    {
                        if (world.Characters.TryResolveNext(_trader, out int who) && world.Characters.Cold(who) is { } tc && AggroSystem.IsEnemy(Data.Relations, faction, tc.Faction))
                            Defer(world, new Combat.AttackOrder([_customer], _trader) { Tick = world.Tick + 1 });
                        Close(world);
                        break;
                    }
                    owner.Hold(world, _trader, this);
                    _approach.Begin(world, _customer, c, t);
                    break;
                case CloseTrade:
                    Close(world);
                    break;
                case BuyItem buy when _open:
                    if (command.Issuer is not null && buy.Quantity <= 0) return;
                    if (CustomerTrades(world, buy.Into, buy) && CanSpend(world)) Buy(world, buy);
                    break;
                case SellItem sell when _open:
                    if (command.Issuer is not null && sell.Quantity <= 0) return;
                    if (CustomerTrades(world, sell.From, sell)) Sell(world, sell);
                    break;
                case ArrangeStock when _open:
                    if (Resolve(world, out _, out _, out var arranged) && InventoryMatches(world, (TradeCommand)command, arranged!.Stock)) InventoryCommands.Arrange(arranged.Stock);
                    break;
            }
        }

        public void Advance(World world)
        {
            if (_open) FollowSelection(world);
            if (!_customer.IsNone)
            {
                if (!Resolve(world, out int c, out int t, out _)) Close(world);
                else if (InReach(world, c, t)) { _open = true; owner.Hold(world, _trader, this); }
                else if (_open || _approach is null || !_approach.Follow(world, _customer, c, t)) Close(world);
                else owner.Hold(world, _trader, this);
            }
            RefreshView(world);
        }

        public void RefreshView(World world) => View = _open && Resolve(world, out int c, out int t, out var shop) ? Publish(world, c, t, shop!) : null;

        public TradeView? PublishAuthorized(World world) => _attributed && _authorization is not null && _open
            && Resolve(world, out int c, out int t, out var shop) ? Publish(world, c, t, shop!) : null;

        public void Close(World world)
        {
            _session.Active = false;
            _session = new();
            owner.Release(world, _trader, this);
            _customer = _trader = CharacterId.None;
            _customerFaction = -1;
            _open = false;
            _attributed = false;
            View = null;
            _authorization = null;
            _membershipRevision = 0;
            SessionId = 0;
            _approach = null;
        }

        void Defer(World world, SimCommand command) => world.Defer(_authorization is { } grant
            ? command with { Issuer = playerId, ControlGrants = [grant] } : command);

        bool CanSpend(World world) => _authorization is null || world.Players.Find(playerId)?.HasPermission(_customerFaction, FactionPermission.SpendFunds) == true;
        bool CanReadFunds(World world) => _authorization is null || world.Players.Find(playerId)?.HasPermission(_customerFaction, FactionPermission.ReadFunds) == true;
        PlayerState Player(World world) => world.Players.Find(playerId)!.State;
        FactionState Funds(World world) => world.Factions.GetOrCreate(_customerFaction);
        static long Budget(long payer, long receiver) => Math.Min(Math.Max(0, payer), receiver < 0 ? long.MaxValue : long.MaxValue - receiver);

        static bool InventoryMatches(World world, TradeCommand command, Inventory source) =>
            world.InventoryMatches(command, source);

        // ------------------------------------------------------------------ trading

        bool Resolve(World world, out int c, out int t, out Shop? shop)
        {
            var table = world.Characters;
            shop = null;
            t = -1;
            if (!table.TryResolveNext(_customer, out c) || !table.TryResolveNext(_trader, out t)) return false;
            var cc = table.Cold(c)!;
            if (cc.Faction != _customerFaction) return false;
            if (_authorization is { } grant && (!world.Players.Validate(grant) ||
                world.Players.Find(playerId)?.MembershipRevision(_customerFaction) != _membershipRevision)) return false;
            var tc = table.Cold(t)!;
            if (!cc.IsPlayer || cc.Medical is { Incapacitated: true } || tc.Medical is { Incapacitated: true } || tc.InCombat || cc.Inventory is null) return false;
            if (AggroSystem.IsEnemy(Data.Relations, cc.Faction, tc.Faction)) return false;
            shop = ShopOf(world, tc);
            return shop is not null && (!_open || InReach(world, c, t));
        }

        bool InReach(World world, int c, int t)
        {
            if (!_open) return Dialogue.DialogueSystem.InTalkRange(world.Characters, c, t);
            var a = world.Characters.Next[c].Position;
            var b = world.Characters.Next[t].Position;
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return dx * dx + dz * dz <= Settings.PassRange * Settings.PassRange && MathF.Abs(a.Y - b.Y) <= MovementSystem.FloorSlack;
        }

        internal bool TradesWith(World world, Squad squad) => !_customer.IsNone && world.Characters.TryResolveNext(_trader, out int t) && world.Characters.Cold(t)!.SquadId == squad.Id;

        bool FollowSelection(World world)
        {
            var selection = Player(world).Selection;
            if (selection.Count == 0 || selection.Contains(_customer)) return true;
            foreach (var id in selection) if (TradeWith(world, id)) return true;
            return false;
        }

        bool CustomerTrades(World world, CharacterId window, SimCommand command) =>
            window.IsNone ? FollowSelection(world) : TradeWith(world, window, command);

        internal bool TradeWith(World world, CharacterId who, SimCommand? command = null)
        {
            if (who == _customer) return true;
            if (!_open || !Resolve(world, out _, out int t, out _) || !world.Characters.TryResolveNext(who, out int c)
                || world.Characters.Cold(c) is not { IsPlayer: true, Inventory: not null, Medical.Incapacitated: false } cold || cold.Faction != _customerFaction) return false;
            if (_attributed && (command is not null ? !world.MayOrder(command, who) : !world.Players.CanControl(playerId, who))) return false;
            if (!InReach(world, c, t)) { world.Post(playerId, "Out of trading range."); return false; }
            _customer = who;
            if (_attributed) _authorization = world.Players.ControlOf(who);
            return true;
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
            if (!_open || !Resolve(world, out _, out _, out var shop)) return null;
            buyer = _customer;
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
            if (!item.IsStolenFor(world.Characters.Cold(c)!.Faction, Data) || !Fences(item, squad, shop!.Terms) || !Caught(world, c, t, item, squad, shop.Terms)) return Smuggling(world, c, item, shop!.Terms);
            world.Post(playerId, "Got caught selling stolen item.");
            if (Crimes is { } crimes && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, _trader, Crime.CrimeSystem.MinTimer, viaWrapper: true))
                crimes.Book(world, c, squad.Faction, _trader);
            return true;
        }

        /// <summary>
        /// The smuggling check of a sale (<c>FUN_140794e80</c>, <b>Observed</b>, run after the fence check): an item the culture of the trader's town's faction lists as illegal is refused by a trader that
        /// does not buy illegal goods, "Attempted smuggling!" (code 0xf), and the trader says THIEF_CAUGHT_STEALING_FROM_ME (8) at the seller unless it buys stolen goods. True when refused.
        /// </summary>
        bool Smuggling(World world, int seller, ItemInstance item, Trader terms)
        {
            if (terms.BuysIllegal || (uint)terms.Site >= (uint)Data.SiteFactions.Count || Market(world).FactionCulture(Data.SiteFactions[terms.Site]) is not { } culture || !culture.Illegal.Contains(item.Record)) return false;
            world.Post(playerId, "Attempted smuggling!");
            if (!terms.BuysStolen) world.Defer(new Dialogue.DialogueEvent(_trader, world.Characters.IdOf(seller), 8) { Tick = world.Tick + 1 });
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
            world.Post(playerId, Loc.Tr(sell ? "{1} sold {2} for c.{3}" : "{1} bought {2} for c.{3}", cold.Name, Units(units, item.Name), price));
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
                if (placed > 0 && InventoryCommands.IsEquipmentSection(item.Section)) Assembly.RefreshEquipment(cold);
            }
            else if (buy.Section == "backpack_attach") placed = 0;
            else if (buy.Section is { } section) placed = PlaceAt(cold.Inventory!, bought, units, section, buy.X, buy.Y);
            else if (buy.Equip && InventoryCommands.TryEquip(cold.Inventory!, bought.WithQuantity(1), cold))
            {
                // One unit is worn; the rest of a stack goes where it fits.
                Assembly.RefreshEquipment(cold);
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
            world.Post(playerId, Loc.Tr("{1} bought {2} for c.{3}", cold.Name, Units(placed, item.Name), price));
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
            if (item.IsStolenFor(cold.Faction, Data) && Fences(item, squad, shop.Terms) && Caught(world, c, t, item, squad, shop.Terms))
            {
                world.Post(playerId, "Got caught selling stolen item.");
                if (Crimes is { } crimes && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, _trader, Crime.CrimeSystem.MinTimer, viaWrapper: true))
                    crimes.Book(world, c, squad.Faction, _trader);
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
            if (equipped) Assembly.RefreshEquipment(cold);
            squad.Money -= price;
            Funds(world).Money += price;
            world.Post(playerId, Loc.Tr("{1} sold {2} for c.{3}", cold.Name, Units(placed, item.Name), price));
        }

        /// <summary>
        /// The chance a shop that is no fence lets a stolen item through (8.4, <c>FUN_140793490</c>): lerp(thievery / 100, 0.4, 1.25) of the seller, halved when the item was stolen from the
        /// shop's faction; 1 or more never gets caught. The roll is the seller's and the tick's.
        /// </summary>
        public static float FenceChance(float thievery, bool stolenFromShopFaction, bool stolenFromShopSquad = false)
        {
            float p = 0.4f + (1.25f - 0.4f) * (thievery / 100f);
            if (stolenFromShopFaction) p *= 0.5f;

            return stolenFromShopSquad ? p * 0.5f : p;
        }

        /// <summary>The odds <c>FUN_140793490</c> answers for goods nobody looks for (<c>DAT_14169bb44</c>, <b>Verified</b>: 1.1).</summary>
        const float NeverCaught = 1.1f;

        /// <summary>The faction the shop's squad sits in regards another as its own (the game's <c>FUN_1406b2630</c>: the same faction, or a relation of 50 or more).</summary>
        bool Allied(int a, int b) => a == b || Data.Relations.Contains(a) && Data.Relations.Contains(b) && Data.Relations.IsAlly(a, b);

        /// <summary>
        /// The fencing probability (<c>FUN_140793490</c>, <b>Observed</b>): reads the current owner, its faction, squad and town. A missing owner or
        /// the seller's own squad is safe; a foreign owner is pursued by shops allied to it or in its town. Goods of the shop's own squad halve
        /// the chance again, including at a shop that knowingly buys stolen goods.
        /// </summary>
        float FenceOdds(World world, float thievery, ItemInstance item, int seller, int trader, Squad shopSquad, Trader terms)
        {
            var table = world.Characters;
            int from = item.StolenFrom;
            CampaignTown? home = null;

            if (!item.Owner.IsNone)
            {
                if (!table.TryResolveNext(item.Owner, out int o) || table.Cold(o) is not { } owner) return 1;
                if (item.OwnerSquad == table.Cold(seller)!.SquadId) return 1;

                from = owner.Faction;
                home = world.Squads.Find(owner.SquadId) is { Town: >= 0 } squad ? CampaignTown.OfSite(squad.Town) : CampaignTown.None;
            }

            int shopFaction = shopSquad.Faction;

            if (from != shopFaction && from >= 0)
            {
                if (!Fences(item, shopSquad, terms)
                    || Data.Relations.Factions[from].FundamentalType is FundamentalType.Bandit or FundamentalType.Adventurer)
                    return 1;
                if (!Allied(shopFaction, from) && home != AiSystem.TownAt(Data, world, table.Next[trader].Position))
                    return NeverCaught;
            }

            return FenceChance(thievery, from == shopFaction, item.OwnerSquad >= 0 && item.OwnerSquad == shopSquad.Id);
        }

        bool Caught(World world, int seller, int trader, ItemInstance item, Squad shopSquad, Trader terms)
        {
            var cold = world.Characters.Cold(seller)!;
            float p = FenceOdds(world, cold.Stats?[StatsEnumerated.Thieving] ?? 0, item, seller, trader, shopSquad, terms);
            bool caught = p < 1 && Rng.Float(Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(seller)), RngPurpose.Trade, (ulong)world.Tick)) >= p;

            // The seller trains thievery whenever the odds are below 0.98 (<c>FUN_140793770</c>): 5 x 2 for a fence that works, 20 x 2 for one that is
            // caught.
            if (p < Crime.CrimeSystem.FenceXpBelow)
                Crimes?.TrainThievery(world, seller, caught ? Crime.CrimeSystem.FenceCaughtXp : Crime.CrimeSystem.FenceXp);

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

        bool IsBuyback(ItemInstance item, bool sell) => item.Buyback is { } r && ReferenceEquals(r.Session, _session) && r.Sell == sell;

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
            return new(_session, !sell, coins);
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
            TradeItemView View(ItemInstance item, int index, bool sell, int holder, float? skill = null, int? seller = null)
            {
                long value = m.UnitPrice(item, false, shop.Terms, holder), sellValue = m.UnitPrice(item, true, shop.Terms, holder);
                bool buyback = IsBuyback(item, sell);
                if (buyback)
                {
                    if (sell) sellValue = Price(m, item, 1, true, shop.Terms, holder);
                    else value = Price(m, item, 1, false, shop.Terms, holder);
                }
                bool stolen = item.IsStolenFor(holder, Data);
                float? odds = sell && stolen && Fences(item, squad, shop.Terms)
                    ? FenceOdds(world, skill ?? thievery, item, seller ?? c, t, squad, shop.Terms) : null;
                return new(index, item.Record, item.Name, item.Section, item.X, item.Y, item.Width, item.Height, item.Quantity,
                    item.Quality, Price(m, item, item.Quantity, sell, shop.Terms, holder), Price(m, item, 1, sell, shop.Terms, holder), stolen)
                {
                    Buyback = buyback,
                    Container = item.Contents is not null,
                    Unique = item.Unique,
                    BagItems = item.Contents?.Items.Count ?? -1,
                    Material = item.MaterialId,
                    Colour = item.ColourId,
                    Charge = item.ChargeFraction,
                    Value = value,
                    SellValue = sellValue,
                    Weight = item.Weight,
                    PriceFactor = m.TraderFactor(item, shop.Terms, holder),
                    MarketFactor = m.MarketFactor(shop.Terms.Site, item.Record),
                    Company = item.CompanyId,
                    Level = item.Level,
                    Charges = item.Charges,
                    MaxCharges = item.MaxCharges,
                    StolenFrom = stolen ? item.StolenFrom : -1,
                    FenceChance = odds is { } shown ? Math.Clamp(shown, 0, 1) : null,
                    FenceOdds = odds is < 1 ? Math.Max(odds.Value, 0) : null,
                    Illegal = sell && (uint)shop.Terms.Site < (uint)Data.SiteFactions.Count && m.FactionCulture(Data.SiteFactions[shop.Terms.Site]) is { } culture && culture.Illegal.Contains(item.Record),
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
            var goodsOf = new Dictionary<CharacterId, IReadOnlyList<TradeItemView>>();
            for (int candidate = 0; candidate < world.Characters.HighWater; candidate++)
            {
                if (!world.Characters.Next[candidate].Alive || world.Characters.Cold(candidate) is not { IsPlayer: true, Inventory: { } own, Medical.Incapacitated: false } seller
                    || seller.Faction != _customerFaction || _attributed && !world.Players.CanControl(playerId, world.Characters.IdOf(candidate)) || !InReach(world, candidate, t)) continue;
                goodsOf[world.Characters.IdOf(candidate)] = own.All().Select((item, index) => (item, index)).Where(p => InventoryCommands.IsMovableSection(p.item.Section))
                    .Select(p => View(p.item, p.index, true, seller.Faction, seller.Stats?[StatsEnumerated.Thieving] ?? 0, candidate)).ToArray();
            }
            var held = Player(world).Held switch
            {
                { From.Holder: ItemHolder.Stock } h => View(h.Item, -1, false, squad.Faction),
                { From.Holder: ItemHolder.Character } h when goodsOf.ContainsKey(h.From.Character) && world.Characters.TryResolveNext(h.From.Character, out int sellerSlot) => View(h.Item, -1, true, world.Characters.Cold(sellerSlot)!.Faction, world.Characters.Cold(sellerSlot)!.Stats?[StatsEnumerated.Thieving] ?? 0, sellerSlot),
                _ => null,
            };
            return new TradeView(_customer, _trader, world.Characters.Cold(t)!.Name, squad.Money, CanReadFunds(world) ? Funds(world).Money : 0, shop.Stock.Width, shop.Stock.Height, stock, goods, shop.Terms.BuysStolen, shop.Terms.BuysIllegal)
            {
                Held = held,
                GoodsOf = goodsOf,
                Unused = shop.UnusedCells,
                SessionId = SessionId,
                FundsVisible = CanReadFunds(world),
                CanSpendFunds = CanSpend(world),
                StockRevision = world.InventoryRevision(shop.Stock),
                GoodsRevision = world.InventoryRevision(cold.Inventory!),
                GoodsRevisions = goodsOf.Keys.ToDictionary(id => id, id => world.InventoryRevision(world.Characters.Cold(id.Slot)!.Inventory!)),
            };
        }

        static bool Fences(ItemInstance item, Squad shopSquad, Trader terms) =>
                !terms.BuysStolen || item.OwnerSquad >= 0 && item.OwnerSquad == shopSquad.Id;
    }
}