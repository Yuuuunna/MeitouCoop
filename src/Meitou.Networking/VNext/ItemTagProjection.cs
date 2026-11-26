using System.Runtime.CompilerServices;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Meitou.Networking.VNext;

/// <summary>
/// Splits recipient-visible tag definitions from changing item fields and joins them only at the replica's presentation boundary.
/// </summary>
public sealed class ItemTagProjection
{
    /// <summary>
    /// Cached immutable subtrees are valid only for this exact definition table; withdrawal invalidates every hydrated value.
    /// </summary>
    IReadOnlyList<ItemTagView>? _definitions;

    /// <summary>
    /// Exact digest-to-value lookup for the current recipient and publication state.
    /// </summary>
    IReadOnlyDictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Weak keys avoid retaining old inventory and machine snapshots after their owning publication is released.
    /// </summary>
    ConditionalWeakTable<object, object> _joined = new();

    /// <summary>
    /// Collects definitions exclusively from the already authorized view, including its current loot, trade and machine projections.
    /// </summary>
    public static PlayerWorldView Capture(PlayerWorldView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Snapshot is null) throw new InvalidDataException("Missing item tag source view.");
        Validate(view.Snapshot.ItemTags);
        var known = view.Snapshot.ItemTags.ToDictionary(t => t.Digest, StringComparer.Ordinal);
        var tags = new Dictionary<string, ItemTagView>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var (value, digest) in VisibleTags(view.Snapshot))
        {
            ItemTagView? tag = value is { Length: > 0 } ? new(ItemTagIdentity.Digest(value), value)
                : digest is { Length: > 0 } ? known.GetValueOrDefault(digest) : null;
            if (tag is null || tags.ContainsKey(tag.Digest)) continue;
            ItemTagCodec.Validate(tag);
            bytes += ItemTagCodec.HeaderBytes + 2L * tag.Value.Length;
            if (tags.Count >= 65536 || bytes > WorldViewCodec.MaximumBytes)
                throw new InvalidDataException("Item tag definitions exceed the view bound.");
            tags.Add(tag.Digest, tag);
        }
        return WithDefinitions(view, tags.Values.OrderBy(t => t.Digest, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Definitions not referenced by this view never enter its table, even when a caller supplies a larger known-definition cache.
    /// </summary>
    static IEnumerable<(string Value, string Digest)> VisibleTags(WorldSnapshot snapshot)
    {
        foreach (var character in snapshot.Characters)
            foreach (var item in character.ItemGrid?.Items ?? []) yield return (item.ModTag, item.ModTagDigest);
        foreach (var machine in snapshot.Machines)
            foreach (var tag in MachineTags(machine)) yield return tag;
        if (snapshot.InspectedTownMachine is { } inspected)
            foreach (var tag in MachineTags(inspected)) yield return tag;
        foreach (var options in snapshot.Crafts.Values)
            foreach (var option in options)
                if (option.Product is { } item) yield return (item.ModTag, item.ModTagDigest);
        foreach (var ground in snapshot.GroundItems) yield return (ground.ModTag ?? "", ground.ModTagDigest ?? "");
        if (snapshot.Held is { } held) yield return (held.ModTag, held.ModTagDigest);
        if (snapshot.Trade is { } trade)
        {
            foreach (var item in trade.Stock.Concat(trade.Goods).Concat(trade.GoodsOf.Values.SelectMany(v => v)))
                yield return (item.ModTag, item.ModTagDigest);
            if (trade.Held is { } heldTrade) yield return (heldTrade.ModTag, heldTrade.ModTagDigest);
        }
        if (snapshot.PlayerTrade is { } barter)
            foreach (var goods in barter.Own.Items.Concat(barter.Other.Items).Concat(barter.Available))
                yield return (goods.Item.ModTag, goods.Item.ModTagDigest);
    }

    /// <summary>
    /// Every item-bearing machine surface shares the same definition collection.
    /// </summary>
    static IEnumerable<(string Value, string Digest)> MachineTags(MachineSnapshot machine)
    {
        foreach (var item in machine.Storage?.Items ?? []) yield return (item.ModTag, item.ModTagDigest);
        foreach (var slot in machine.Inputs)
            if (slot.Held is { } item) yield return (item.ModTag, item.ModTagDigest);
        if (machine.Output?.Held is { } output) yield return (output.ModTag, output.ModTagDigest);
        foreach (var item in machine.Craft?.Done ?? []) yield return (item.ModTag, item.ModTagDigest);
    }

    /// <summary>
    /// Keeps unchanged definition-table identity stable so presentation caches survive unrelated item quantities and charges.
    /// </summary>
    internal static PlayerWorldView WithDefinitions(PlayerWorldView view, IReadOnlyList<ItemTagView> definitions) =>
        view.Snapshot.ItemTags.SequenceEqual(definitions) ? view : view with { Snapshot = view.Snapshot with { ItemTags = definitions } };

    /// <summary>
    /// Bounds typed and decoded tables and rejects duplicate identities before constructing their lookup dictionary.
    /// </summary>
    internal static void Validate(IReadOnlyList<ItemTagView> definitions)
    {
        if (definitions is null || definitions.Count > 65536) throw new InvalidDataException("Invalid item tag table.");
        long bytes = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in definitions)
        {
            if (tag is null) throw new InvalidDataException("Missing item tag definition.");
            ItemTagCodec.Validate(tag);
            bytes += ItemTagCodec.HeaderBytes + 2L * tag.Value.Length;
            if (!seen.Add(tag.Digest) || bytes > WorldViewCodec.MaximumBytes)
                throw new InvalidDataException("Item tag definitions exceed the view bound.");
        }
    }

    /// <summary>
    /// Hydrates a separate UI view; an absent definition always produces empty text, never a previous item's tag.
    /// </summary>
    public PlayerWorldView Join(PlayerWorldView view)
    {
        if (!ReferenceEquals(_definitions, view.Snapshot.ItemTags))
        {
            Validate(view.Snapshot.ItemTags);
            _definitions = view.Snapshot.ItemTags;
            _values = _definitions.ToDictionary(t => t.Digest, t => t.Value, StringComparer.Ordinal);
            _joined = new();
        }
        return Cached(view, source => source with { Snapshot = JoinSnapshot(source.Snapshot) });
    }

    /// <summary>
    /// Resolves only explicit wire digests. A default struct or an omitted empty JSON field may contain null; both mean an untagged item.
    /// </summary>
    string Value(string? value, string? digest) => digest is { Length: > 0 } ? _values.GetValueOrDefault(digest, "") : value ?? "";

    /// <summary>
    /// Reuses immutable hydrated subtrees while their raw input and definition table remain unchanged.
    /// </summary>
    T Cached<T>(T value, Func<T, T> join) where T : class => (T)_joined.GetValue(value, key => join((T)key));

    /// <summary>
    /// Rewrites each item-bearing snapshot surface while preserving all authoritative revisions and permissions.
    /// </summary>
    WorldSnapshot JoinSnapshot(WorldSnapshot snapshot) => snapshot with
    {
        Characters = snapshot.Characters.Select(c => c.ItemGrid is null ? c : c with { ItemGrid = JoinInventory(c.ItemGrid) }).ToArray(),
        Machines = snapshot.Machines.Select(JoinMachine).ToArray(),
        InspectedTownMachine = snapshot.InspectedTownMachine is { } inspected ? JoinMachine(inspected) : null,
        Crafts = snapshot.Crafts.ToDictionary(p => p.Key,
            p => (IReadOnlyList<CraftOptionView>)p.Value.Select(c => c.Product is null ? c : c with { Product = JoinItem(c.Product) }).ToArray()),
        GroundItems = snapshot.GroundItems.Select(i => i with
        {
            ModTag = Value(i.ModTag, i.ModTagDigest), ModTagDigest = i.ModTagDigest ?? ""
        }).ToArray(),
        Held = snapshot.Held is { } held ? held with { ModTag = Value(held.ModTag, held.ModTagDigest) } : null,
        Trade = snapshot.Trade is { } trade ? JoinTrade(trade) : null,
        PlayerTrade = snapshot.PlayerTrade is { } barter ? JoinBarter(barter) : null,
    };

    /// <summary>
    /// Inventory grids are usually unchanged between presentation frames.
    /// </summary>
    InventoryView JoinInventory(InventoryView inventory) => Cached(inventory, source => source with
    {
        Items = source.Items.Select(JoinItem).ToArray()
    });

    /// <summary>
    /// A definition changes display text without changing the item token's digest.
    /// </summary>
    InventoryItemView JoinItem(InventoryItemView item) => Cached(item, source => source with
    {
        ModTag = Value(source.ModTag, source.ModTagDigest)
    });

    /// <summary>
    /// Storage, input, output and completed crafting slots have the same text identity semantics.
    /// </summary>
    MachineSnapshot JoinMachine(MachineSnapshot machine) => Cached(machine, source => source with
    {
        Storage = source.Storage is { } storage ? JoinInventory(storage) : null,
        Inputs = source.Inputs.Select(s => s.Held is { } item ? s with { Held = JoinItem(item) } : s).ToArray(),
        Output = source.Output is { Held: { } output } slot ? slot with { Held = JoinItem(output) } : source.Output,
        Craft = source.Craft is { } craft ? craft with { Done = craft.Done.Select(JoinItem).ToArray() } : null,
    });

    /// <summary>
    /// The independent stock, goods, per-character and held trade projections all remain authoritative.
    /// </summary>
    TradeView JoinTrade(TradeView trade) => Cached(trade, source => source with
    {
        Stock = source.Stock.Select(JoinTradeItem).ToArray(),
        Goods = source.Goods.Select(JoinTradeItem).ToArray(),
        GoodsOf = source.GoodsOf.ToDictionary(p => p.Key, p => (IReadOnlyList<TradeItemView>)p.Value.Select(JoinTradeItem).ToArray()),
        Held = source.Held is { } held ? JoinTradeItem(held) : null,
    });

    /// <summary>
    /// A trade row's token continues to echo only its digest when the row is hydrated.
    /// </summary>
    TradeItemView JoinTradeItem(TradeItemView item) => Cached(item, source => source with
    {
        ModTag = Value(source.ModTag, source.ModTagDigest)
    });

    /// <summary>
    /// Barter has no separate item DTO, so its displayed token is hydrated while command binding removes the text again.
    /// </summary>
    PlayerTradeView JoinBarter(PlayerTradeView trade) => Cached(trade, source => source with
    {
        Own = source.Own with { Items = source.Own.Items.Select(JoinGoods).ToArray() },
        Other = source.Other with { Items = source.Other.Items.Select(JoinGoods).ToArray() },
        Available = source.Available.Select(JoinGoods).ToArray(),
    });

    /// <summary>
    /// Resolves the participant-visible tag without changing the quoted item identity.
    /// </summary>
    PlayerTradeGoods JoinGoods(PlayerTradeGoods goods) => goods with
    {
        Item = goods.Item with { ModTag = Value(goods.Item.ModTag, goods.Item.ModTagDigest) }
    };
}
