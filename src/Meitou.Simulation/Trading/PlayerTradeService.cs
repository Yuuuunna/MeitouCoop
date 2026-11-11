using System.Numerics;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Trading;

/// <summary>Serialized authoritative barter. Offers never remove or reserve live items; all refusal occurs before commit.</summary>
public sealed class PlayerTradeService(Func<PlayerId, bool> isActive)
{
    sealed class Side(PlayerId player, CharacterId actor)
    {
        public readonly PlayerId Player = player;
        public readonly CharacterId Actor = actor;
        public CharacterControl Grant;
        public int Faction = -1;
        public ulong Membership, InventoryRevision;
        public long Money;
        public bool Accepted, Confirmed;
        public Inventory? Inventory;
        public List<Offered> Items = [];
    }
    sealed record Offered(ItemInstance Item, Inventory Holder, InventoryItemToken Token, int Count);
    sealed class Session(ulong id, Side first, Side second)
    {
        public readonly ulong Id = id;
        public readonly Side First = first, Second = second;
        public ulong Revision = 1;
    }
    readonly Dictionary<ulong, Session> _sessions = [];
    ulong _serial;
    public const float Range = 30;
    public const int MaximumOfferItems = 128;

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        Prune(world);
        foreach (var command in commands)
        {
            if (command.Issuer is not { } player || !isActive(player)) continue;
            if (command is OpenPlayerTrade open) { Open(world, player, open); continue; }
            if (command is not PlayerTradeCommand request || !_sessions.TryGetValue(request.SessionId, out var session)
                || request.Revision != session.Revision) continue;
            var own = session.First.Player == player ? session.First : session.Second.Player == player ? session.Second : null;
            if (own is null) continue;
            if (command is CancelPlayerTrade) { _sessions.Remove(session.Id); continue; }
            if (!Valid(world, session)) { _sessions.Remove(session.Id); continue; }
            if (command is AcceptPlayerTrade accept)
            {
                if (own.Accepted || own.Actor != accept.Actor || !world.MayOrder(accept, accept.Actor)
                    || !MembershipMatches(world, player, accept.PaymentFaction, accept.FactionGrantRevision)
                    || !Bind(world, own, accept.PaymentFaction)) continue;
                Change(session);
            }
            else if (command is RevisePlayerTrade revise)
            {
                if (!own.Accepted || revise.Money < 0 || revise.Items is null || revise.Items.Count > MaximumOfferItems
                    || own.Inventory is not { } inventory || revise.InventoryRevision == 0
                    || world.InventoryRevision(inventory) != revise.InventoryRevision) continue;
                var offered = new List<Offered>();
                var seen = new HashSet<ItemInstance>();
                bool invalid = false;
                foreach (var row in revise.Items)
                {
                    if (row?.Item is not { } token || inventory.All().ElementAtOrDefault(token.Index) is not { } item
                        || !Matches(item, token) || !Movable(inventory, item, out var holder) || !seen.Add(item)
                        || row.Count <= 0 || row.Count > item.Quantity || (item.Contents is not null || item.Lock is not null) && row.Count != item.Quantity)
                    { invalid = true; break; }
                    offered.Add(new(item, holder!, token, row.Count));
                }
                if (invalid) continue;
                own.Items = offered;
                own.Money = revise.Money;
                own.InventoryRevision = revise.InventoryRevision;
                Change(session);
            }
            else if (command is ConfirmPlayerTrade && session.First.Accepted && session.Second.Accepted)
            {
                if (!InventoryValid(world, session.First) || !InventoryValid(world, session.Second))
                { InvalidateOffers(world, session); continue; }
                own.Confirmed = true;
                if (session.First.Confirmed && session.Second.Confirmed)
                {
                    if (Commit(world, session)) _sessions.Remove(session.Id);
                    else { Change(session); world.Post(player, "Trade could not complete: check funds and inventory space."); }
                }
            }
        }
    }

    void Open(World world, PlayerId player, OpenPlayerTrade command)
    {
        if (player == command.Counterparty || command.Counterparty.IsNone || !isActive(command.Counterparty)
            || _sessions.Values.Any(s => s.First.Player == player || s.Second.Player == player || s.First.Player == command.Counterparty || s.Second.Player == command.Counterparty)
            || !world.MayOrder(command, command.Actor)
            || !MembershipMatches(world, player, command.PaymentFaction, command.FactionGrantRevision)
            || world.Players.ControlOf(command.CounterpartyActor) is not { } target || target.Player != command.Counterparty) return;
        var first = new Side(player, command.Actor);
        if (!Bind(world, first, command.PaymentFaction)) return;
        var second = new Side(command.Counterparty, command.CounterpartyActor) { Grant = target };
        var session = new Session(checked(++_serial), first, second);
        if (Valid(world, session)) _sessions.Add(session.Id, session);
    }

    static bool MembershipMatches(World world, PlayerId player, int faction, ulong revision) =>
        revision != 0 && world.Players.Find(player)?.MembershipRevision(faction) == revision;

    static bool Bind(World world, Side side, int faction)
    {
        if (world.Players.Find(side.Player) is not { } player || !player.HasPermission(faction, FactionPermission.SpendFunds)
            || world.Factions.Find(faction) is null || world.Players.ControlOf(side.Actor) is not { } grant || grant.Player != side.Player
            || !world.Characters.TryResolveNext(side.Actor, out int slot) || world.Characters.Cold(slot)?.Inventory is not { } inventory) return false;
        side.Grant = grant; side.Faction = faction; side.Membership = player.MembershipRevision(faction);
        side.Inventory = inventory; side.InventoryRevision = world.InventoryRevision(inventory); side.Accepted = true;
        return true;
    }

    bool Valid(World world, Session session)
    {
        foreach (var side in new[] { session.First, session.Second })
        {
            if (!isActive(side.Player) || !world.Players.Validate(side.Grant) || side.Grant.Player != side.Player
                || !world.Characters.TryResolveNext(side.Actor, out int slot) || world.Characters.Cold(slot) is not { DiedTick: < 0 } cold
                || cold.Medical?.Incapacitated == true || cold.Save is { CarryPartner.IsNull: false }) return false;
            if (side.Accepted && (!ReferenceEquals(cold.Inventory, side.Inventory)
                || world.Players.Find(side.Player) is not { } player || !player.HasPermission(side.Faction, FactionPermission.SpendFunds)
                || player.MembershipRevision(side.Faction) != side.Membership)) return false;
        }
        world.Characters.TryResolveNext(session.First.Actor, out int a);
        world.Characters.TryResolveNext(session.Second.Actor, out int b);
        float distance = Vector3.DistanceSquared(world.Characters.Next[a].Position, world.Characters.Next[b].Position);
        return float.IsFinite(distance) && distance <= Range * Range;
    }

    static bool Matches(ItemInstance item, InventoryItemToken token) => item.Record == token.Record && item.Section == token.Section
        && item.X == token.X && item.Y == token.Y && item.Quantity == token.Quantity && item.Quality == token.Quality;
    static bool Movable(Inventory root, ItemInstance item, out Inventory? holder)
    {
        holder = root.Items.Contains(item) && item.Section == "main" ? root
            : root.Backpack?.Contents is { } bag && bag.Items.Contains(item) && item.Section == "backpack_content" ? bag : null;
        return holder is not null && !item.Death && !item.LockedOn && item.Quantity > 0 && item.Width > 0 && item.Height > 0;
    }
    static bool InventoryValid(World world, Side side) => side.Inventory is { } inventory && world.InventoryRevision(inventory) == side.InventoryRevision
        && side.Items.All(row => row.Holder.Items.Contains(row.Item) && Matches(row.Item, row.Token) && Movable(inventory, row.Item, out var holder)
            && ReferenceEquals(holder, row.Holder) && row.Count > 0 && row.Count <= row.Item.Quantity);
    static void Change(Session session)
    {
        session.Revision = checked(session.Revision + 1);
        session.First.Confirmed = session.Second.Confirmed = false;
    }
    static void InvalidateOffers(World world, Session session)
    {
        foreach (var side in new[] { session.First, session.Second })
            if (side.Accepted && !InventoryValid(world, side))
            { side.Items.Clear(); side.Money = 0; side.InventoryRevision = world.InventoryRevision(side.Inventory!); }
        Change(session);
    }
    public void Prune(World world)
    {
        foreach (var session in _sessions.Values.ToArray())
            if (!Valid(world, session)) _sessions.Remove(session.Id);
            else if (session.First.Accepted && !InventoryValid(world, session.First) || session.Second.Accepted && !InventoryValid(world, session.Second)) InvalidateOffers(world, session);
    }
    public void Disconnect(PlayerId player)
    {
        foreach (var session in _sessions.Values.Where(s => s.First.Player == player || s.Second.Player == player).ToArray()) _sessions.Remove(session.Id);
    }
    public void Clear() => _sessions.Clear();

    public PlayerTradeView? Publish(World world, PlayerId player)
    {
        var session = _sessions.Values.FirstOrDefault(s => s.First.Player == player || s.Second.Player == player);
        if (session is null || !Valid(world, session)) return null;
        var own = session.First.Player == player ? session.First : session.Second;
        var other = ReferenceEquals(own, session.First) ? session.Second : session.First;
        PlayerTradeSideView View(Side side) => new(side.Player, side.Actor, side.Faction, side.Accepted, side.Confirmed, side.Money, side.InventoryRevision,
            Array.AsReadOnly(side.Items.Select(row => new PlayerTradeGoods(row.Token, row.Item.Name, row.Count)).ToArray()));
        var inventory = own.Inventory;
        if (inventory is null && world.Characters.TryResolveNext(own.Actor, out int slot)) inventory = world.Characters.Cold(slot)?.Inventory;
        PlayerTradeGoods[] available = inventory is null ? [] : inventory.All().Select((item, index) => (item, index)).Where(p => Movable(inventory, p.item, out _))
            .Select(p => new PlayerTradeGoods(new(p.index, p.item.Record, p.item.Section, p.item.X, p.item.Y, p.item.Quantity, p.item.Quality), p.item.Name, p.item.Quantity)).ToArray();
        var factions = world.Players.Find(player)?.Memberships.Where(p => (p.Value & FactionPermission.SpendFunds) != 0).Select(p => p.Key).ToArray() ?? [];
        return new(session.Id, session.Revision, View(own), View(other), Array.AsReadOnly(available), Array.AsReadOnly(factions));
    }

    sealed record Placement(Inventory Grid, ItemInstance Item, int X, int Y, string Section);
    sealed class Plan
    {
        public readonly List<Placement> Places = [];
        public readonly Dictionary<ItemInstance, int> Quantities = [];
    }
    static bool PlanIncoming(Side receiver, Side sender, Plan plan)
    {
        var root = receiver.Inventory!;
        var grids = new List<(Inventory Grid, string Section)> { (root, "main") };
        if (root.Backpack?.Contents is { } bag) grids.Add((bag, "backpack_content"));
        foreach (var outgoing in receiver.Items) plan.Quantities[outgoing.Item] = outgoing.Item.Quantity - outgoing.Count;
        foreach (var offered in sender.Items)
        {
            int remaining = offered.Count;
            foreach (var (grid, section) in grids)
            {
                if (ReferenceEquals(offered.Item.Contents, grid)
                    || InventoryCommands.SlotRefusal(section, offered.Item.Type, offered.Item.EquipmentSection, offered.Item.Contents) is not null) continue;
                int limit = grid.LimitOf(offered.Item);
                if (offered.Item.Contents is null)
                    foreach (var existing in grid.Items.Where(i => i.Section == section && CanStack(i, offered.Item)))
                    {
                        int quantity = plan.Quantities.GetValueOrDefault(existing, existing.Quantity);
                        if (quantity <= 0) continue;
                        int add = Math.Min(remaining, Math.Max(0, limit - quantity));
                        plan.Quantities[existing] = quantity + add; remaining -= add;
                        if (remaining == 0) break;
                    }
                if (offered.Item.Contents is null && remaining > 0)
                    foreach (var placed in plan.Places.Where(p => ReferenceEquals(p.Grid, grid) && CanStack(p.Item, offered.Item)))
                    {
                        int quantity = plan.Quantities.GetValueOrDefault(placed.Item, placed.Item.Quantity);
                        int add = Math.Min(remaining, Math.Max(0, limit - quantity));
                        plan.Quantities[placed.Item] = quantity + add; remaining -= add;
                        if (remaining == 0) break;
                    }
                while (remaining > 0)
                {
                    if (offered.Item.Contents is not null && remaining > limit) break;
                    int x = 0, y = 0;
                    bool found = false;
                    for (int cy = 0; cy <= grid.Height - offered.Item.Height && !found; cy++)
                        for (int cx = 0; cx <= grid.Width - offered.Item.Width && !found; cx++)
                            if (grid.InRegion(cx, cy, offered.Item.Width, offered.Item.Height)
                                && !grid.Items.Any(i => i.Section == section && plan.Quantities.GetValueOrDefault(i, i.Quantity) > 0 && Overlap(cx, cy, offered.Item, i.X, i.Y, i))
                                && !plan.Places.Any(p => ReferenceEquals(p.Grid, grid) && Overlap(cx, cy, offered.Item, p.X, p.Y, p.Item)))
                            { x = cx; y = cy; found = true; }
                    if (!found) break;
                    int count = Math.Min(remaining, limit);
                    if (offered.Item.Lock is not null && count != offered.Item.Quantity) return false;
                    var unit = offered.Item.Contents is not null || remaining == offered.Count && count == remaining && offered.Count == offered.Item.Quantity
                        ? offered.Item : offered.Item.WithQuantity(count);
                    if (!ReferenceEquals(unit, offered.Item) && unit.MaxCharges > 0
                        && !(offered.Count == offered.Item.Quantity && remaining == count)) unit.Charges = unit.MaxCharges;
                    plan.Places.Add(new(grid, unit, x, y, section)); remaining -= count;
                }
                if (remaining == 0) break;
            }
            if (remaining != 0) return false;
        }
        return true;
    }
    // Preserve manufacturer/model/charge state; partly consumed stacks stay separate rather than losing their partial unit.
    static bool CanStack(ItemInstance a, ItemInstance b) => InventoryCommands.Stackable(a, b)
        && a.Lock is null && b.Lock is null && a.CompanyId == b.CompanyId && a.Level == b.Level && a.ColourId == b.ColourId
        && a.Crafter == b.Crafter && a.Uniform == b.Uniform && a.ItemFunction == b.ItemFunction
        && a.Charges == b.Charges && a.MaxCharges == b.MaxCharges && (a.MaxCharges <= 0 || a.Charges == a.MaxCharges)
        && a.LimbDamage == b.LimbDamage && a.LimbWear == b.LimbWear && a.LimbStun == b.LimbStun;
    static bool Overlap(int x, int y, ItemInstance a, int bx, int by, ItemInstance b) => x < (long)bx + b.Width && bx < (long)x + a.Width && y < (long)by + b.Height && by < (long)y + a.Height;
    bool Commit(World world, Session session)
    {
        var a = session.First; var b = session.Second;
        if (!Valid(world, session) || !InventoryValid(world, a) || !InventoryValid(world, b)
            || ReferenceEquals(a.Inventory, b.Inventory) || world.Factions.Find(a.Faction) is not { } accountA || world.Factions.Find(b.Faction) is not { } accountB
            || accountA.Money < a.Money || accountB.Money < b.Money || accountA.Money < 0 || accountB.Money < 0) return false;
        long nextA = accountA.Money, nextB = accountB.Money;
        if (!ReferenceEquals(accountA, accountB))
        {
            long debitA = accountA.Money - a.Money, debitB = accountB.Money - b.Money;
            if (debitA > long.MaxValue - b.Money || debitB > long.MaxValue - a.Money) return false;
            nextA = debitA + b.Money; nextB = debitB + a.Money;
        }
        var planA = new Plan(); var planB = new Plan();
        if (!PlanIncoming(a, b, planA) || !PlanIncoming(b, a, planB)) return false;
        // All allocations and gameplay refusals precede mutations. No external callbacks run during commit.
        foreach (var placement in planA.Places.Concat(planB.Places).GroupBy(p => p.Grid)) placement.Key.Items.EnsureCapacity(placement.Key.Items.Count + placement.Count());
        foreach (var side in new[] { a, b })
            foreach (var row in side.Items)
                if (row.Count == row.Item.Quantity) row.Holder.Items.Remove(row.Item);
        foreach (var plan in new[] { planA, planB })
        {
            foreach (var (item, count) in plan.Quantities) if (count > 0) item.Quantity = count;
            foreach (var p in plan.Places)
            {
                p.Item.Section = p.Section; p.Item.X = p.X; p.Item.Y = p.Y; p.Item.Buyback = null;
                p.Grid.Items.Add(p.Item);
            }
        }
        accountA.Money = nextA; accountB.Money = nextB;
        world.InventoryRevision(a.Inventory!); world.InventoryRevision(b.Inventory!);
        return true;
    }
}