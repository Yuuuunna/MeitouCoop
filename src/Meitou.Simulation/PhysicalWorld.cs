using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation;

/// <summary>
/// A generation-checked character or the stable instance key of a loaded container.
/// </summary>
public readonly record struct PhysicalOwner(CharacterId Character, string? FixtureId)
{
    /// <summary>
    /// Addresses the character's native inventory.
    /// </summary>
    public static implicit operator PhysicalOwner(CharacterId character) => new(character, null);

    /// <summary>
    /// Addresses a fixture's contents or a storage machine's grid.
    /// </summary>
    public static implicit operator PhysicalOwner(string fixtureId) => new(CharacterId.None, fixtureId);
}

/// <summary>
/// Serial simulation operations on native inventories, ledgers, buildings and bodies.
/// </summary>
public sealed class PhysicalWorld
{
    #region Serial world access

    /// <summary>
    /// The authority owning these native records.
    /// </summary>
    readonly World _world;
    /// <summary>
    /// The innermost world tick on this thread. Parallel dispatch is checked separately for a single-thread world.
    /// </summary>
    [ThreadStatic] static PhysicalWorld? s_ticking;

    internal PhysicalWorld(World world) => _world = world;
    internal readonly ref struct TickScope(PhysicalWorld? previous)
    {
        public void Dispose() => s_ticking = previous;
    }

    internal TickScope EnterTick()
    {
        var previous = s_ticking;
        s_ticking = this;
        return new(previous);
    }

    /// <summary>
    /// Rejects off-thread calls and phases outside serial tick mutation.
    /// </summary>
    public void RequireSerial()
    {
        if (s_ticking != this || _world.Characters.Phase is TablePhase.Outside or TablePhase.Parallel)
            throw new InvalidOperationException("A serial simulation tick phase is required.");
    }

    PopulationData? Data => _world.SystemOf<PopulationSystem>()?.Data ?? _world.SystemOf<PlayerSystem>()?.Data;
    ItemFactory? Factory => _world.Fixtures.Items ?? Data?.Items ?? _world.SystemOf<FeedSystem>()?.Items;
    CharacterCold? Cold(CharacterId id) => _world.Characters.TryResolveNext(id, out int slot) ? _world.Characters.Cold(slot) : null;
    bool FactionExists(int faction) => faction >= 0 && (Data is { } data ? data.Relations.Contains(faction) : _world.Factions.Find(faction) is not null);

    #endregion

    #region Inventories

    /// <summary>
    /// Recalculates derived stats when a worn item is taken from a character.
    /// </summary>
    CharacterAssembly? _assembly;

    Inventory? Grid(PhysicalOwner owner) => owner.FixtureId is { } id
        ? _world.Fixtures.Find(id)?.Contents ?? _world.Machines.Find(id)?.Storage
        : Cold(owner.Character)?.Inventory;

    bool Put(PhysicalOwner owner, Inventory grid, ItemInstance item) => owner.FixtureId is not null
        ? grid.AddTo("main", item) : grid.Add(item, InventoryCommands.MainClosed(Cold(owner.Character)));

    /// <summary>
    /// Lists the current stack objects, including bag contents. Resolve again after a transfer, reload or world switch.
    /// </summary>
    public IReadOnlyList<ItemInstance> QueryItems(PhysicalOwner owner)
    {
        RequireSerial();
        return Grid(owner)?.All().ToArray() ?? [];
    }

    /// <summary>
    /// Revalidates a window's indexed stack token against the native inventory, including its tag or remote digest.
    /// </summary>
    public ItemInstance? ResolveItem(PhysicalOwner owner, InventoryItemToken token)
    {
        RequireSerial();
        var item = Grid(owner)?.All().ElementAtOrDefault(token.Index);
        return HeldItems.Matches(item, token) ? item : null;
    }

    /// <summary>
    /// Creates the entire quantity from the game's item database or leaves the inventory unchanged.
    /// </summary>
    public bool SpawnItem(PhysicalOwner owner, string templateId, string modTag, int count)
    {
        RequireSerial();
        if (count <= 0 || modTag is null || Grid(owner) is not { } grid || Factory?.Create(templateId, count) is not { } item) return false;
        item.ModTag = modTag;
        if (!Put(owner, grid.Copy(), item.Copy())) return false;
        return Put(owner, grid, item);
    }

    /// <summary>
    /// Changes the tag of a stack still held by this owner. Native inventory revisions and wire digests follow the new tag.
    /// </summary>
    public bool SetModTag(PhysicalOwner owner, ItemInstance item, string tag)
    {
        RequireSerial();
        if (tag is null || Grid(owner)?.All().Contains(item) != true) return false;
        item.ModTag = tag;
        return true;
    }

    static bool Removable(Inventory grid, ItemInstance item, int count) => count > 0 && count <= item.Quantity && !item.LockedOn
        && !item.Death && InventoryCommands.LimbSlotOf(item.Section) < 0 && (count == item.Quantity || item.Contents is null)
        && grid.All().Contains(item);

    static ItemInstance Take(Inventory grid, ItemInstance item, int count)
    {
        if (count == item.Quantity) grid.Remove(item);
        return HeldItems.Part(item, count);
    }

    void RefreshEquipment(PhysicalOwner owner, bool worn)
    {
        if (!worn || owner.FixtureId is not null || Cold(owner.Character) is not { } cold || Data is not { } data) return;
        (_assembly ??= new CharacterAssembly(data)).RefreshEquipment(cold);
    }

    /// <summary>
    /// Transfers exactly the requested quantity when the destination has room. Worn locks, fitted limbs and live death items stay attached.
    /// </summary>
    public bool TransferItem(PhysicalOwner from, PhysicalOwner to, ItemInstance item, int count)
    {
        RequireSerial();
        if (Grid(from) is not { } source || Grid(to) is not { } dest || source == dest || !Removable(source, item, count)) return false;
        var unit = count == item.Quantity ? item.Copy() : item.WithQuantity(count);
        if (!Put(to, dest.Copy(), unit)) return false;

        bool worn = InventoryCommands.IsEquipmentSection(item.Section);
        Put(to, dest, Take(source, item, count));
        RefreshEquipment(from, worn);
        return true;
    }

    /// <summary>
    /// Removes the specified units. A nonempty bag must be unloaded before being consumed.
    /// </summary>
    public bool RemoveItem(PhysicalOwner owner, ItemInstance item, int count)
    {
        RequireSerial();
        if (Grid(owner) is not { } grid || !Removable(grid, item, count) || item.Contents is { Items.Count: > 0 }) return false;
        bool worn = InventoryCommands.IsEquipmentSection(item.Section);
        Take(grid, item, count);
        RefreshEquipment(owner, worn);
        return true;
    }

    /// <summary>
    /// Consumes one sheet and inserts one tagged document as a single operation, including when the sheet occupies the last free cell.
    /// The caller verifies the sheet template and writing implements.
    /// </summary>
    public bool WriteOnPaper(PhysicalOwner owner, ItemInstance paper, string template, string tag)
    {
        RequireSerial();
        if (string.IsNullOrEmpty(tag) || Grid(owner) is not { } grid || !Removable(grid, paper, 1) || paper.Contents is not null
            || InventoryCommands.IsEquipmentSection(paper.Section) || Factory?.Create(template) is not { } document) return false;
        var trial = grid.Copy();
        int index = Array.IndexOf(grid.All().ToArray(), paper);
        Take(trial, trial.All().ElementAt(index), 1);
        document.ModTag = tag;
        if (!Put(owner, trial, document.Copy())) return false;

        Take(grid, paper, 1);
        return Put(owner, grid, document);
    }

    #endregion

    #region Buildings and encounter injuries

    /// <summary>
    /// The current owner of a loaded building, or -1 when absent or unowned.
    /// </summary>
    public int GetOwner(string buildingId)
    {
        RequireSerial();
        return _world.Buildings.Find(buildingId)?.Owner ?? _world.Fixtures.TransferredOwner(buildingId);
    }

    /// <summary>
    /// Transfers a building, including its registered fixtures and production assets, preserving their contents.
    /// </summary>
    public bool SetOwner(string buildingId, int faction)
    {
        RequireSerial();
        return FactionExists(faction) && _world.Fixtures.TransferTitle(_world, buildingId, faction);
    }

    /// <summary>
    /// Changes the native door locks and refreshes navigation.
    /// </summary>
    public bool SetLocked(string buildingId, bool locked)
    {
        RequireSerial();
        return _world.Fixtures.SetDoorsLocked(buildingId, locked);
    }

    /// <summary>
    /// Strikes the first intact gate leaf in registration order using the native door damage and break rules.
    /// </summary>
    public bool DamageGate(string buildingId, float damage)
    {
        RequireSerial();
        if (!float.IsFinite(damage) || damage <= 0) return false;
        var door = _world.Fixtures.OfBuilding(buildingId).FirstOrDefault(f => f.Kind == FixtureKind.Door && f.IsGate && !f.DoorBroken);
        if (door is null) return false;
        LockSystem.Strike(_world, door, damage);
        return true;
    }

    /// <summary>
    /// Applies native cut damage to a limb (left arm, right arm, left leg, right leg).
    /// </summary>
    public bool ApplyLimbDamage(CharacterId id, int limb, float damage)
    {
        RequireSerial();
        return (uint)limb < 4 && float.IsFinite(damage) && damage > 0 && Cold(id) is { } cold
            && _world.SystemOf<CombatSystem>()?.DamageLimb(_world, id.Slot, cold, limb, damage) == true;
    }

    /// <summary>
    /// Wounds the indexed body part from the actor's native anatomy, with normal bleeding, knockout and death callbacks.
    /// </summary>
    public bool ApplyBodyDamage(CharacterId id, int part, float damage)
    {
        RequireSerial();
        return float.IsFinite(damage) && damage > 0 && Cold(id) is { } cold
            && _world.SystemOf<CombatSystem>()?.DamageBodyPart(_world, id.Slot, cold, part, damage) == true;
    }

    #endregion

    #region Money and trade

    /// <summary>
    /// Transfers positive cats between two distinct native faction treasuries without overdraft or overflow.
    /// </summary>
    public bool TransferCats(int fromFaction, int toFaction, int amount)
    {
        RequireSerial();
        if (amount <= 0 || fromFaction == toFaction || _world.Factions.Find(fromFaction) is not { } source || !FactionExists(toFaction)
            || source.Money < amount || (_world.Factions.Find(toFaction)?.Money ?? 0) is < 0
            || (_world.Factions.Find(toFaction)?.Money ?? 0) > long.MaxValue - amount) return false;
        source.Money -= amount;
        _world.Factions.GetOrCreate(toFaction).Money += amount;
        return true;
    }

    /// <summary>
    /// Transfers a positive sum from the payer's funds to the payee. Player characters share their faction's treasury.
    /// </summary>
    public bool TransferCats(CharacterId from, CharacterId to, int amount)
    {
        RequireSerial();
        if (amount <= 0 || from == to || Cold(from) is not { } source || Cold(to) is not { } destination) return false;
        if (source.IsPlayer && destination.IsPlayer) return TransferCats(source.Faction, destination.Faction, amount);
        return PhysicalPayments.Transfer(Factory, PhysicalPayments.Purse.Of(_world, source), PhysicalPayments.Purse.Of(_world, destination), amount);
    }

    /// <summary>
    /// Pays an individual from a faction treasury using native physical denominations for NPC payees.
    /// </summary>
    public bool TransferCats(int fromFaction, CharacterId to, int amount)
    {
        RequireSerial();
        if (amount <= 0 || Cold(to) is not { } destination) return false;
        if (destination.IsPlayer) return TransferCats(fromFaction, destination.Faction, amount);
        return PhysicalPayments.Transfer(Factory, new(_world.Factions.Find(fromFaction), null), PhysicalPayments.Purse.Of(_world, destination), amount);
    }

    /// <summary>
    /// Pays a faction treasury from an individual.
    /// </summary>
    public bool TransferCats(CharacterId from, int toFaction, int amount)
    {
        RequireSerial();
        if (amount <= 0 || !FactionExists(toFaction) || Cold(from) is not { } source) return false;
        if (source.IsPlayer) return TransferCats(source.Faction, toFaction, amount);
        return PhysicalPayments.Transfer(Factory, PhysicalPayments.Purse.Of(_world, source), new(_world.Factions.GetOrCreate(toFaction), null), amount);
    }

    /// <summary>
    /// Exchanges goods for a positive price within five world metres. Both the item and all denominations, including any change, must fit.
    /// </summary>
    public bool Barter(CharacterId seller, CharacterId buyer, ItemInstance item, int quantity, int price)
    {
        RequireSerial();
        if (seller == buyer || price <= 0 || Cold(seller) is not { Inventory: { } stock } merchant
            || Cold(buyer) is not { Inventory: { } bag } customer || stock == bag
            || !Removable(stock, item, quantity) || !InventoryCommands.IsGridSection(item.Section)) return false;
        var units = _world.Characters.Next;
        float distance = System.Numerics.Vector3.DistanceSquared(units[seller.Slot].Position, units[buyer.Slot].Position);
        if (!float.IsFinite(distance) || distance > 50 * 50) return false;

        var previewStock = stock.Copy();
        var previewBag = bag.Copy();
        int index = Array.IndexOf(stock.All().ToArray(), item);
        var trialItem = Take(previewStock, previewStock.All().ElementAt(index), quantity);
        var debit = PhysicalPayments.Purse.Of(_world, customer, previewBag, preview: true);
        var credit = PhysicalPayments.Purse.Of(_world, merchant, previewStock, preview: true);
        if (!PhysicalPayments.Transfer(Factory, debit, credit, price) || !Put(buyer, previewBag, trialItem)) return false;

        var purchase = Take(stock, item, quantity);
        PhysicalPayments.Transfer(Factory, PhysicalPayments.Purse.Of(_world, customer), PhysicalPayments.Purse.Of(_world, merchant), price);
        return Put(buyer, bag, purchase);
    }

    /// <summary>
    /// Opens the native merchant window for the nearby customer's controller without issuing any movement or stance order.
    /// </summary>
    public bool OpenLocalTrade(CharacterId customer, CharacterId trader)
    {
        RequireSerial();
        if (customer == trader || Cold(customer) is not { IsPlayer: true } || Cold(trader) is null) return false;
        var positions = _world.Characters.Next;
        float distance = System.Numerics.Vector3.DistanceSquared(positions[customer.Slot].Position, positions[trader.Slot].Position);
        return float.IsFinite(distance) && distance <= 50 * 50
            && _world.SystemOf<Trading.TradeSystem>()?.OpenAt(_world, customer, trader, trusted: true, stand: false) == true;
    }

    /// <summary>
    /// Stops the recruit's or NPC's current fight and order immediately. A protected player avatar cannot be stopped by this operation.
    /// </summary>
    public bool StopAgentCombat(CharacterId actor)
    {
        RequireSerial();
        if (Cold(actor) is not { DiedTick: < 0 } cold || cold.Medical is { Dead: true } || _world.StarterAvatars.IsProtected(actor)) return false;
        _world.SystemOf<CombatSystem>()?.Disengage(_world.Characters, actor);
        PlayerOrders.Stop(_world, actor.Slot, cold);
        return true;
    }

    #endregion

    #region Factions, crime and recruitment

    /// <summary>
    /// Adjusts a faction's native bounty entry. Reductions down to zero remove the entry.
    /// </summary>
    public bool AddOrClearBounty(CharacterId id, int faction, int delta)
    {
        RequireSerial();
        if (delta == 0 || Cold(id) is not { } cold || !FactionExists(faction)) return false;
        if (Data is { } data && CrimeFactions.BookingKey(data.Relations, faction) is >= 0 and var legal) faction = legal;
        var prior = cold.Bounties?.Of(faction);
        long value = (long)(prior?.Amount ?? 0) + delta;
        if (value > int.MaxValue || (prior is null && value <= 0)
            || delta > 0 && (cold.Bounties?.Entries.Sum(e => (long)e.Amount) ?? 0) + delta > int.MaxValue) return false;
        var ledger = cold.Bounties ??= new BountyLedger();
        if (value <= 0)
        {
            ledger.Remove(prior!);
            if (ledger.Count == 0) cold.Bounties = null;
        }
        else
        {
            var entry = prior ?? ledger.Get(faction, _world.DescribeFaction(faction)?.Name ?? "");
            entry.Amount = (int)value;
            if (delta > 0) { entry.Claimed = false; entry.RaisedTick = _world.Tick; }
        }
        return true;
    }

    /// <summary>
    /// Changes a directed relation through the native threshold notification and observer path.
    /// </summary>
    public bool AdjustRelation(int from, int to, float delta)
    {
        RequireSerial();
        var relations = Data?.Relations ?? EventLog.Relations(_world);
        if (from == to || !float.IsFinite(delta) || relations is null || !relations.Contains(from) || !relations.Contains(to)) return false;
        PlayerRelations.Add(_world, relations, from, to, delta, new(RelationCauseType.Mod, CharacterId.None));
        return true;
    }

    /// <summary>
    /// Sets the faction catalogue's live display name.
    /// </summary>
    public bool SetDisplayName(int faction, string name)
    {
        RequireSerial();
        if (!FactionExists(faction) || string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)) return false;
        _world.Factions.GetOrCreate(faction).Name = name;
        return true;
    }

    /// <summary>
    /// Recruits through native dialogue eligibility, the payer's control grant, faction permissions and treasury.
    /// </summary>
    public bool Recruit(CharacterId recruit, CharacterId payer, int fee, bool contextualRecruit = false)
    {
        RequireSerial();
        return fee >= 0 && _world.SystemOf<DialogueSystem>()?.RecruitForPayment(_world, recruit, payer, fee, contextualRecruit) == true;
    }

    /// <summary>
    /// Transfers a free living NPC to a loaded NPC platoon, carrying its body, equipment and lasting identity into the destination squad.
    /// </summary>
    public bool JoinNpcPlatoon(CharacterId actor, int platoon, bool asLeader = false)
    {
        RequireSerial();
        if (_world.Platoons.Find(platoon) is not { State: PlatoonState.Loaded, Dead: false } destination
            || _world.Squads.Find(destination.SquadId) is not { Dead: false, PlayerManaged: false } squad || squad.PlatoonId != platoon
            || _world.IsPlayerFaction(squad.Faction) || Cold(actor) is not { IsPlayer: false, DiedTick: < 0, IsImprisoned: false } cold
            || cold.Medical?.Dead == true || !cold.CarriedBy.IsNone || _world.StarterAvatars.IsProtected(actor)
            || _world.Players.ControlOf(actor) is not null || Data is not { } data) return false;

        if (cold.SquadId != squad.Id)
        {
            if (squad.Members.Count >= data.Bodies.MaxSquadSize) return false;
            if (destination.Saved is not null && destination.Stored is null)
                destination.Stored = new StoredSquad { Name = squad.Name, Pinned = destination.Pinned, HomeBuilding = squad.HomeBuilding, RetainMemberRecords = true };
            var old = _world.Squads.Find(cold.SquadId);
            _world.ReleaseDuties(actor.Slot);
            PlayerOrders.Interrupt(cold);
            DialogueSystem.Leave(_world, actor, cold);
            cold.Save = null;
            if (old is not null && _world.Platoons.Find(old.PlatoonId) is { } former) former.Size = old.Members.Count;
            cold.Faction = squad.Faction;
            cold.RoadPreference = data.RoadPreferenceOf(squad.Faction);
            cold.SquadId = squad.Id;
            cold.Ai = null;
            cold.Role = (int)SquadRole.Squad1;
            squad.Members.Add(actor);
            destination.Size = squad.Members.Count;
        }
        if (asLeader || squad.Leader.IsNone)
        {
            if (Cold(squad.Leader) is { } previous) previous.Role = (int)SquadRole.Squad1;
            squad.Leader = actor;
            cold.Role = (int)SquadRole.Leader;
        }
        return true;
    }
    #endregion
}
