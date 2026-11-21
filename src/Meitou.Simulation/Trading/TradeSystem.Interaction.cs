using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
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
        CharacterControl? _authorization;
        ulong _membershipRevision;
        public ulong SessionId { get; private set; }
        public PlayerId Actor => playerId;
        public TradeView? View { get; private set; }

        public void Input(World world, SimCommand command)
        {
            if (command is TradeCommand scoped && (command.Issuer.HasValue != _attributed ||
                command.Issuer is not null && scoped.SessionId == 0 || scoped.SessionId != 0 && scoped.SessionId != SessionId)) return;
            if (_open && command is TradeCommand and not CloseTrade && Resolve(world, out _, out _, out var prepared))
                owner.PrepareView(world, prepared!);
            switch (command)
            {
                case OpenTrade order:
                    GiveTrade(world, order);
                    break;
                case OpenTradeNow now:
                    OpenNow(world, now.Customer, now.Trader, now);
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
                    if (Resolve(world, out _, out _, out var arranged) && InventoryMatches(world, (TradeCommand)command, arranged!.Window)) InventoryCommands.Arrange(arranged.Window);
                    break;
            }
        }

        public void Advance(World world)
        {
            if (_open)
            {
                FollowSelection(world);
                if (!Resolve(world, out _, out _, out _)) Close(world);
            }

            RefreshView(world);
        }

        /// <summary>Authenticates a player's parties before an order or immediate trade is allowed.</summary>
        bool Accepts(World world, CharacterId customer, CharacterId trader, SimCommand command, out int slot)
        {
            slot = -1;
            if (customer == trader || !world.MayOrder(command, customer) || !world.Characters.TryResolveNext(customer, out int initial)) return false;
            int faction = world.Characters.Cold(initial)!.Faction;
            if (faction < 0 || command.Issuer is not null && (command.FactionGrantRevision == 0 ||
                world.Players.Find(playerId)!.MembershipRevision(faction) != command.FactionGrantRevision)) return false;
            _customer = customer;
            _trader = trader;
            _customerFaction = faction;
            _attributed = command.Issuer.HasValue;
            _authorization = command.Issuer is null ? null : world.Players.ControlOf(customer);
            _membershipRevision = command.FactionGrantRevision;
            SessionId = owner._nextSession = checked(owner._nextSession + 1);
            if (world.Characters.TryResolveNext(trader, out int stocked)) owner.StockFor(world, stocked);
            return Resolve(world, out slot, out _, out _);
        }

        /// <summary>The attributed trade order uses the ordinary talk queue and opens only on its arrival.</summary>
        void GiveTrade(World world, OpenTrade order)
        {
            Close(world);
            bool trades = Accepts(world, order.Customer, order.Trader, order, out int slot);
            if (trades && world.OrderOwnerOf(TaskType.PlayerTalkTo) is { } talks)
            {
                Close(world);
                var entry = world.BindOrder(order, new OrderEntry(TaskType.PlayerTalkTo, talks)
                    { Target = order.Trader, Detail = Dialogue.DialogueOrders.Trade });
                PlayerOrders.Give(world, slot, entry, order.Queued);
                return;
            }
            if (!_customer.IsNone && world.Characters.TryResolveNext(order.Trader, out int who) && world.Characters.Cold(who) is { } tc
                && AggroSystem.IsEnemy(Data.Relations, _customerFaction, tc.Faction))
                Defer(world, new Combat.AttackOrder([order.Customer], order.Trader) { Tick = world.Tick + 1, Cause = "an order to trade with a hostile trader" });
            Close(world);
        }

        internal void OpenNow(World world, CharacterId customer, CharacterId trader, SimCommand source)
        {
            Close(world);
            if (!Accepts(world, customer, trader, source, out int slot) || !Resolve(world, out _, out _, out var shop))
            {
                Close(world);
                return;
            }
            TaskStart.Stand(world.Characters, slot);
            owner.PrepareView(world, shop!);
            _open = true;
            RefreshView(world);
        }

        internal bool Shows(World world, Shop shop) => _open && Resolve(world, out _, out _, out var open) && ReferenceEquals(open, shop);

        internal void SettleStock(World world)
        {
            if (Resolve(world, out _, out _, out var shop)) owner.SettleView(world, shop!);
        }

        public void RefreshView(World world) => View = _open && Resolve(world, out int c, out int t, out var shop) ? Publish(world, c, t, shop!) : null;

        public TradeView? PublishAuthorized(World world) => _attributed && _authorization is not null && _open
            && Resolve(world, out int c, out int t, out var shop) ? Publish(world, c, t, shop!) : null;

        /// <summary>
        /// Closes a visible trade on an inventory-close event while preserving a pending walk to another trader.
        /// </summary>
        internal void EndTrade(World world)
        {
            if (!_open) return;

            SettleStock(world);
            Close(world);
        }

        public void Close(World world)
        {
            Shop? closing = world.Characters.TryResolveNext(_trader, out int trader) ? ShopOf(world, world.Characters.Cold(trader)!) : null;
            _session.Active = false;
            _session = new();
            _customer = _trader = CharacterId.None;
            _customerFaction = -1;
            _open = false;
            _attributed = false;
            View = null;
            _authorization = null;
            _membershipRevision = 0;
            SessionId = 0;
            if (closing is not null) owner.SettleView(world, closing);
        }

        void Defer(World world, SimCommand command) => world.Defer(_authorization is { } grant
            ? command with { Issuer = playerId, ControlGrants = [grant], FactionGrantRevision = _membershipRevision } : command);

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
            if (!cc.IsPlayer || cc.Inventory is null) return false;
            var trader = table.Cold(t)!;
            if (trader.IsPlayer && (trader.SquadId == cc.SquadId || world.Squads.Find(trader.SquadId) is not { } own || !FactionShopOpen(world, own)))
                return false;
            if (!_open)
            {
                var tc = table.Cold(t)!;
                if (cc.Medical is { Dead: true } or { Incapacitated: true }) return false;
                if (tc.Medical is { Incapacitated: true } || tc.InCombat || tc.IsImprisoned || Carrying.CarrySystem.Asleep(table, t)) return false;
                if (AggroSystem.IsEnemy(Data.Relations, cc.Faction, tc.Faction)) return false;
            }

            shop = ShopOf(world, table.Cold(t)!);
            return shop is not null;
        }

        /// <summary>
        /// Item transfers use the original's horizontal pass range, even when the parties' windows remain open farther apart.
        /// </summary>
        bool InReach(World world, int c, int t)
        {
            var a = world.Characters.Next[c].Position;
            var b = world.Characters.Next[t].Position;
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return dx * dx + dz * dz <= Settings.PassRange * Settings.PassRange;
        }

        internal bool TradesWith(World world, Squad squad) => !_customer.IsNone && world.Characters.TryResolveNext(_trader, out int t) && world.Characters.Cold(t)!.SquadId == squad.Id;

        bool FollowSelection(World world)
        {
            var selection = Player(world).Selection;
            if (selection.Count == 0 || selection.Contains(_customer)) return true;
            if (!world.Characters.TryResolveNext(_trader, out int t)) return false;

            foreach (var id in selection)
                if (world.Characters.TryResolveNext(id, out int c) && InReach(world, c, t) && TradeWith(world, id)) return true;

            return false;
        }

        bool CustomerTrades(World world, CharacterId window, SimCommand command)
        {
            if (!window.IsNone) return TradeWith(world, window, command);
            if (FollowSelection(world)) return TradeWith(world, _customer, command);

            world.Post(playerId, "Out of trading range.");
            return false;
        }

        internal bool TradeWith(World world, CharacterId who, SimCommand? command = null)
        {
            if (!_open || !Resolve(world, out _, out int t, out _) || !world.Characters.TryResolveNext(who, out int c)
                || world.Characters.Cold(c) is not { IsPlayer: true, Inventory: not null } cold || cold.Faction != _customerFaction
                || cold.SquadId == world.Characters.Cold(t)!.SquadId || who != _customer && cold.Medical is { Dead: true }) return false;
            // The current customer's grant is checked by Resolve; choosing a different window needs its own control authorization.
            if (who != _customer && _attributed
                && (command is not null ? !world.MayOrder(command, who) : !world.Players.CanControl(playerId, who))) return false;
            if (!InReach(world, c, t)) { world.Post(playerId, "Out of trading range."); return false; }

            _customer = who;
            if (_attributed) _authorization = world.Players.ControlOf(who);
            return true;
        }

        static ItemInstance? Find(Inventory inventory, InventoryItemToken token)
        {
            var item = inventory.All().ElementAtOrDefault(token.Index);
            return item is not null && item.Record == token.Record && item.Section == token.Section && item.X == token.X && item.Y == token.Y && item.Quantity == token.Quantity
                && item.Quality == token.Quality && token.MatchesTag(item.ModTag) && (item.Contents is null || item.Quantity == 1) ? item : null;
        }

        // ------------------------------------------------------------------ the held item (PlayerSystem, Items/HeldItems.cs)

        /// <summary>The open trade's stock and its customer while the trade's windows show; null when no trade is open.</summary>
        internal Inventory? OpenStock(World world, out CharacterId buyer)
        {
            buyer = CharacterId.None;
            if (!_open || !Resolve(world, out _, out _, out var shop)) return null;
            owner.PrepareView(world, shop!);
            buyer = _customer;
            return shop!.Window;
        }

        /// <summary>
        /// How many of <paramref name="units"/> of a held item the payer can afford (the drop caps the quantity so, <c>FUN_140715560</c>); none: "You can't afford that." or "The shopkeeper can't
        /// afford that.".
        /// </summary>
        internal int AffordHeld(World world, ItemInstance item, int units, bool sell)
        {
            if (!Resolve(world, out int c, out int t, out var shop)) return 0;
            var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
            int n = Affordable(world, Market(world), item, Math.Clamp(units, 0, item.Quantity), sell, shop!.Terms, sell ? world.Characters.Cold(c)!.Faction : squad.Faction,
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
            var witness = _trader;
            world.Post(playerId, "Got caught selling stolen item.");
            world.CloseInventories(world.Characters.IdOf(c));
            if (Crimes is { } crimes && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, witness, Crime.CrimeSystem.MinTimer, viaWrapper: true))
                crimes.Book(world, c, squad.Faction, witness);
            return true;
        }

        /// <summary>
        /// The smuggling check of a sale (<c>FUN_140794e80</c>, <b>Observed</b>, run after the fence check): an item the culture of the trader's town's faction lists as illegal is refused by a trader that
        /// does not buy illegal goods, "Attempted smuggling!" (code 0xf), and the trader says THIEF_CAUGHT_STEALING_FROM_ME (8) at the seller unless it buys stolen goods. True when refused.
        /// </summary>
        bool Smuggling(World world, int seller, ItemInstance item, Trader terms)
        {
            if (owner.PlayersFaction(world, terms.Faction) || terms.BuysIllegal || (uint)terms.Site >= (uint)Data.SiteFactions.Count || Market(world).FactionCulture(Data.SiteFactions[terms.Site]) is not { } culture || !culture.Illegal.Contains(item.Record)) return false;
            var witness = _trader;
            var customer = world.Characters.IdOf(seller);
            world.Post(playerId, "Attempted smuggling!");
            world.CloseInventories(customer);
            if (!terms.BuysStolen) world.Defer(new Dialogue.DialogueEvent(witness, customer, 8) { Tick = world.Tick + 1 });
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
            var paid = Receipt(world, m, item, units, sell, shop!.Terms, holder);
            long price = Price(world, m, item, units, sell, shop.Terms, holder);
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
            if (!InventoryMatches(world, buy, shop!.Window)) return;
            var cold = world.Characters.Cold(c)!;
            var squad = world.Squads.Find(world.Characters.Cold(t)!.SquadId)!;
            if (Find(shop!.Window, buy.Item) is not { } item) return;
            if (item.Contents is { Items.Count: > 0 }) { world.Post(playerId, BagNotEmpty); return; }
            var m = Market(world);
            int units = Affordable(world, m, item, Math.Clamp(buy.Quantity, 1, item.Quantity), false, shop.Terms, squad.Faction, Budget(Funds(world).Money, squad.Money));
            if (units == 0)
            {
                world.Post(playerId, "You can't afford that.");
                return;
            }
            var receipt = Receipt(world, m, item, units, false, shop.Terms, squad.Faction);
            var bought = item.WithQuantity(units);
            bought.Buyback = receipt;
            long price = Price(world, m, item, units, false, shop.Terms, squad.Faction);
            int placed = PutBought(buy, shop.Window, item, bought, units, receipt, cold);
            if (placed == 0)
            {
                world.Post(playerId, "No room for that item.");
                return;
            }
            if (placed < units) price = Price(world, m, item, placed, false, shop.Terms, squad.Faction);
            if (item.Contents is null) Take(shop.Window, item, placed);
            Funds(world).Money -= price;
            squad.Money += price;
            world.Post(playerId, Loc.Tr("{1} bought {2} for c.{3}", cold.Name, Units(placed, item.Name), price));
        }

        /// <summary>
        /// Places purchased units in the receiving character's permitted grids. A live animal's closed main grid accepts nothing; its worn bag
        /// and bag slot remain available.
        /// </summary>
        int PutBought(BuyItem buy, Inventory stock, ItemInstance item, ItemInstance bought, int units, TradeReceipt? receipt, CharacterCold cold)
        {
            var into = cold.Inventory!;
            bool shut = InventoryCommands.MainClosed(cold);

            if (item.Contents is not null)
            {
                // An empty bag moves itself with its contents inventory; its receipt is replaced (none: this undid a sale).
                int moved = TransferContainer(stock, buy.Item, into, buy.Section, buy.X, buy.Y, buy.Equip, cold);
                if (moved > 0) item.Buyback = receipt;
                if (moved > 0 && InventoryCommands.IsEquipmentSection(item.Section)) Assembly.RefreshEquipment(cold);
                return moved;
            }

            if (buy.Section == "backpack_attach") return 0;
            if (buy.Section is { } section) return PlaceAt(into, bought, units, section, buy.X, buy.Y, shut);

            int inBag = buy.Equip && buy.IntoBag ? IntoWornBag(into, bought) : 0;
            if (inBag == units) return units;

            return inBag + PutRest(buy, into, bought, units - inBag, shut, cold);
        }

        /// <summary>
        /// Puts as many of the bought units as fit into the worn bag's grid (stacks of it first, then free cells, <see cref="Inventory.AddTo"/>); the
        /// units placed, the others staying in <paramref name="bought"/>.
        /// </summary>
        static int IntoWornBag(Inventory into, ItemInstance bought)
        {
            if (into.Backpack?.Contents is not { } bag) return 0;

            int units = bought.Quantity;
            return bag.AddTo("backpack_content", bought) ? units : units - bought.Quantity;
        }

        /// <summary>
        /// Puts the bought units not in the bag: with <see cref="BuyItem.Equip"/> one unit into the equipment slot that takes it when that is free,
        /// then wherever they fit; the units placed.
        /// </summary>
        int PutRest(BuyItem buy, Inventory into, ItemInstance bought, int units, bool shut, CharacterCold cold)
        {
            if (!buy.Equip || !InventoryCommands.TryEquip(into, bought.WithQuantity(1), cold)) return Place(into, bought, units, shut);

            // One unit is worn; the rest of a stack goes where it fits.
            Assembly.RefreshEquipment(cold);
            if (units == 1) return 1;

            var rest = bought.WithQuantity(units - 1);
            rest.Buyback = bought.Buyback?.Skip(1);
            return 1 + Place(into, rest, units - 1, shut);
        }

        /// <summary>
        /// Sells the units of the customer's (or the giving character's) item to the shop, as many as the shop can afford, after the fence and
        /// smuggling tests.
        /// </summary>
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
            int units = Affordable(world, m, item, Math.Clamp(sell.Quantity, 1, item.Quantity), true, shop!.Terms, cold.Faction, Budget(squad.Money, Funds(world).Money));
            if (units == 0)
            {
                world.Post(playerId, "The shopkeeper can't afford that.");
                return;
            }
            if (item.IsStolenFor(cold.Faction, Data) && Fences(item, squad, shop.Terms) && Caught(world, c, t, item, squad, shop.Terms))
            {
                var witness = _trader;
                world.Post(playerId, "Got caught selling stolen item.");
                world.CloseInventories(world.Characters.IdOf(c));
                if (Crimes is { } crimes && crimes.SetPending(world, c, Crime.CrimeCode.Fencing, squad.Faction, witness, Crime.CrimeSystem.MinTimer, viaWrapper: true))
                    crimes.Book(world, c, squad.Faction, witness);
                return;
            }
            if (Smuggling(world, c, item, shop.Terms)) return;
            var receipt = Receipt(world, m, item, units, true, shop.Terms, cold.Faction);
            var sold = item.WithQuantity(units);
            sold.Buyback = receipt;
            long price = Price(world, m, item, units, true, shop.Terms, cold.Faction);
            bool equipped = InventoryCommands.IsEquipmentSection(item.Section);
            int placed = item.Contents is not null
                ? TransferContainer(cold.Inventory!, sell.Item, shop.Window, sell.X is not null ? "main" : null, sell.X ?? 0, sell.Y, false, null)
                : sell.X is { } sx ? PlaceAt(shop.Window, sold, units, "main", sx, sell.Y) : Place(shop.Window, sold, units);
            if (placed == 0)
            {
                world.Post(playerId, "No room for that item.");
                return;
            }
            if (item.Contents is not null) item.Buyback = receipt;
            if (placed < units) price = Price(world, m, item, placed, true, shop.Terms, cold.Faction);
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
            if (owner.PlayersFaction(world, shopSquad.Faction)) return NeverCaught;
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
        int Affordable(World world, Market m, ItemInstance item, int wanted, bool sell, Trader terms, int holderFaction, long money)
        {
            for (int n = wanted; n > 0; n--)
                if (Price(world, m, item, n, sell, terms, holderFaction) <= money) return n;
            return 0;
        }

        // Containers move whole: stack copies intentionally omit Contents. Transfer validates the destination before changing either inventory.
        static int TransferContainer(Inventory from, InventoryItemToken token, Inventory into, string? section, int x, int y, bool equip, CharacterCold? wearer)
        {
            if (section is not (null or "main" or "backpack_attach")) return 0;
            if (section == "backpack_attach") return InventoryCommands.Transfer(from, token, into, section, 0, 0, wearer: wearer) ? 1 : 0;
            if (equip && InventoryCommands.Transfer(from, token, into, "backpack_attach", 0, 0, wearer: wearer)) return 1;
            if (InventoryCommands.MainClosed(wearer)) return 0;
            if (section == "main" && InventoryCommands.Transfer(from, token, into, section, x, y)) return 1;
            var item = Find(from, token);
            return item is not null && into.FindCell("main", item.Width, item.Height, out int cx, out int cy)
                && InventoryCommands.Transfer(from, token, into, "main", cx, cy) ? 1 : 0;
        }

        bool IsBuyback(ItemInstance item, bool sell) => item.Buyback is { } r && ReferenceEquals(r.Session, _session) && r.Sell == sell;

        long Price(World world, Market market, ItemInstance item, int units, bool sell, Trader terms, int holder) => IsBuyback(item, sell)
            ? item.Buyback!.Price(units) : Filtered(world, item, units, sell, market.StackPrice(item.WithQuantity(units), sell, terms, holder));

        /// <summary>The server's mod price filters for this interaction's customer and shop.</summary>
        long Filtered(World world, ItemInstance item, int units, bool sell, long price)
        {
            if (!world.TradePriceFilters.Active) return price;
            int shopSquad = world.Characters.TryResolveNext(_trader, out int t) ? world.Characters.Cold(t)?.SquadId ?? -1 : -1;
            return world.TradePriceFilters.Pass(world, new TradeDeal(world.Tick, item.Record, units, sell, _customer, shopSquad), price);
        }

        TradeReceipt? Receipt(World world, Market market, ItemInstance item, int units, bool sell, Trader terms, int holder)
        {
            if (IsBuyback(item, sell)) return null;
            var coins = new long[units];
            long previous = 0;
            for (int n = 1; n <= units; n++)
            {
                long total = Price(world, market, item, n, sell, terms, holder);
                coins[n - 1] = total - previous;
                previous = total;
            }
            return new(_session, !sell, coins);
        }

        /// <summary>
        /// Puts the units at the cell they were dropped on (the trade window's <c>placeItemFromMouse</c>): the cell of <paramref name="section"/> in <paramref name="root"/> (the backpack's
        /// contents for <c>backpack_content</c>) when the footprint is inside the grid, free and the stack within the limit; otherwise wherever they fit, as <see cref="Place"/>.
        /// </summary>
        static int PlaceAt(Inventory root, ItemInstance copy, int units, string section, int x, int y, bool mainShut = false)
        {
            var grid = section == "main" && !mainShut ? root : section == "backpack_content" ? root.Backpack?.Contents : null;
            if (grid is not null && x >= 0 && y >= 0 && x + copy.Width <= grid.Width && y + copy.Height <= grid.Height && units <= grid.LimitOf(copy)
                && grid.IsFree(section, x, y, copy.Width, copy.Height))
            {
                copy.Section = section;
                copy.X = x;
                copy.Y = y;
                grid.Items.Add(copy);
                return units;
            }
            return Place(root, copy, units, mainShut);
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
                long value = Filtered(world, item, 1, false, m.UnitPrice(item, false, shop.Terms, holder));
                long sellValue = Filtered(world, item, 1, true, m.UnitPrice(item, true, shop.Terms, holder));
                bool buyback = IsBuyback(item, sell);
                if (buyback)
                {
                    if (sell) sellValue = Price(world, m, item, 1, true, shop.Terms, holder);
                    else value = Price(world, m, item, 1, false, shop.Terms, holder);
                }
                bool stolen = item.IsStolenFor(holder, Data);
                float? odds = sell && stolen && Fences(item, squad, shop.Terms)
                    ? FenceOdds(world, skill ?? thievery, item, seller ?? c, t, squad, shop.Terms) : null;
                return new(index, item.Record, item.Name, item.Section, item.X, item.Y, item.Width, item.Height, item.Quantity,
                    item.Quality, Price(world, m, item, item.Quantity, sell, shop.Terms, holder), Price(world, m, item, 1, sell, shop.Terms, holder), stolen)
                {
                    Buyback = buyback,
                    Container = item.Contents is not null,
                    Unique = item.Unique,
                    BagItems = item.Contents?.Items.Count ?? -1,
                    Material = item.MaterialId,
                    Rotated = item.Rotated,
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
                    StolenOwner = stolen ? world.Characters.OwnerNameOf(item.Owner) : null,
                    ModTag = item.ModTag,
                    FenceChance = odds is { } shown ? Math.Clamp(shown, 0, 1) : null,
                    FenceOdds = odds is < 1 ? Math.Max(odds.Value, 0) : null,
                    Illegal = sell && !owner.PlayersFaction(world, squad.Faction) && (uint)shop.Terms.Site < (uint)Data.SiteFactions.Count && m.FactionCulture(Data.SiteFactions[shop.Terms.Site]) is { } culture && culture.Illegal.Contains(item.Record),
                };
            }
            // Only the stock grid's own items, at their All() indices (which tokens use): a bag's contents are not loose stock.
            var stock = new List<TradeItemView>(shop.Window.Items.Count);
            int at = 0;
            foreach (var item in shop.Window.Items)
            {
                stock.Add(View(item, at, false, squad.Faction));
                at += 1 + (item.Contents?.All().Count() ?? 0);
            }
            var goods = cold.Inventory!.All().Select((item, index) => (item, index)).Where(p => InventoryCommands.IsMovableSection(p.item.Section))
                .Select(p => View(p.item, p.index, true, cold.Faction)).ToArray();
            var goodsOf = new Dictionary<CharacterId, IReadOnlyList<TradeItemView>>();
            for (int candidate = 0; candidate < world.Characters.HighWater; candidate++)
            {
                if (!world.Characters.Next[candidate].Alive || world.Characters.Cold(candidate) is not { IsPlayer: true, Inventory: { } own, Medical: not { Dead: true } } seller
                    || seller.SquadId == squad.Id || seller.Faction != _customerFaction || _attributed && !world.Players.CanControl(playerId, world.Characters.IdOf(candidate)) || !InReach(world, candidate, t)) continue;
                goodsOf[world.Characters.IdOf(candidate)] = own.All().Select((item, index) => (item, index)).Where(p => InventoryCommands.IsMovableSection(p.item.Section))
                    .Select(p => View(p.item, p.index, true, seller.Faction, seller.Stats?[StatsEnumerated.Thieving] ?? 0, candidate)).ToArray();
            }
            var held = Player(world).Held switch
            {
                { From.Holder: ItemHolder.Stock } h => View(h.Item, -1, false, squad.Faction),
                { From.Holder: ItemHolder.Character } h when goodsOf.ContainsKey(h.From.Character) && world.Characters.TryResolveNext(h.From.Character, out int sellerSlot) => View(h.Item, -1, true, world.Characters.Cold(sellerSlot)!.Faction, world.Characters.Cold(sellerSlot)!.Stats?[StatsEnumerated.Thieving] ?? 0, sellerSlot),
                _ => null,
            };
            return new TradeView(_customer, _trader, world.Characters.Cold(t)!.Name, squad.Money, CanReadFunds(world) ? Funds(world).Money : 0, shop.Window.Width, shop.Window.Height, stock, goods, shop.Terms.BuysStolen, shop.Terms.BuysIllegal)
            {
                Held = held,
                GoodsOf = goodsOf,
                SessionId = SessionId,
                FundsVisible = CanReadFunds(world),
                CanSpendFunds = CanSpend(world),
                StockRevision = world.InventoryRevision(shop.Window),
                GoodsRevision = world.InventoryRevision(cold.Inventory!),
                GoodsRevisions = goodsOf.Keys.ToDictionary(id => id, id => world.InventoryRevision(world.Characters.Cold(id.Slot)!.Inventory!)),
            };
        }

        static bool Fences(ItemInstance item, Squad shopSquad, Trader terms) =>
                !terms.BuysStolen || item.OwnerSquad >= 0 && item.OwnerSquad == shopSquad.Id;
    }
}
