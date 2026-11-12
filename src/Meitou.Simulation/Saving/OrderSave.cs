using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// One queued order as a save holds it: the row, the point, the target handle exactly as read (<see cref="Hand"/>: a character, a building or the
/// null handle), Meitou's own site id and detail code when it wrote them, and what a read could settle at once (<see cref="InstanceKey"/>, the
/// building a handle names; <see cref="Lost"/>, a handle that names nothing this save holds).
/// </summary>
public sealed record SavedOrder(TaskType Row, Vector3 Point, Hand Hand, string? Site, string? InstanceKey, int Kind, bool Lost, string? Authorization = null);

/// <summary>
/// A player character's order queue in its GAMESTATE_AI record <c>ai</c>, next to the perma-jobs (<see cref="PermaJobSave"/>; <b>Verified</b>,
/// <c>AITaskSytem</c> vfuncs 1 and 2, writer <c>FUN_1405090b0</c>, loader <c>FUN_1405092d0</c>; docs/game/ai-tasks.md "Perma-jobs",
/// docs/simulation.md "Order queue"): per order i from 0 the int <c>tjob&lt;i&gt;</c> (task type), the vec3 <c>tjobP&lt;i&gt;</c> (point) and the
/// handle <c>tjobT&lt;i&gt;</c> (target: a character or a building). The queue is written front first, started or not; nothing is saved of it
/// running. Meitou's own keys carry what the keys above cannot: the string <c>meitou tjob&lt;i&gt;</c> (the <see cref="OrderEntry.Site"/> id, which
/// wins on load) and the int <c>meitou tjobK&lt;i&gt;</c> (the owner's detail code, <see cref="IOrderOwner.SaveKind"/>; absent: 0). A load reads the
/// orders into <see cref="CharacterCold.SavedOrders"/>; the decision step makes them the queue (<see cref="Restore"/>) once the world they name
/// exists, and starts the front as any other.
/// </summary>
public static partial class OrderSave
{

    /// <summary>
    /// Writes the queue (or, for a character loaded and not decided yet, the orders it was loaded with) over the old keys. A target character's
    /// handle is given to <paramref name="target"/> to write once every character has one (the null handle is written first).
    /// </summary>
    public static void Write(
        FcsRecord ai, CharacterCold cold, World world, IReadOnlyDictionary<string, FcsRecord> buildings, Action<FcsRecord, string, CharacterId> target)
    {
        foreach (var key in ai.Ints.Keys
                     .Where(k => k.StartsWith(Type, StringComparison.Ordinal) || k.StartsWith(MeitouSite, StringComparison.Ordinal))
                     .ToList())
            ai.Ints.Remove(key);

        foreach (var key in ai.Strings.Keys.Where(k => k.StartsWith(MeitouSite, StringComparison.Ordinal) || k.StartsWith(AuthorizationKey, StringComparison.Ordinal)).ToList())
            ai.Strings.Remove(key);

        foreach (var key in ai.Vector3S.Keys.Where(k => k.StartsWith(Point, StringComparison.Ordinal)).ToList())
            ai.Vector3S.Remove(key);

        int i = 0;
        if (cold.SavedOrders is { } pending)
        {
            foreach (var o in pending)
            {
                Put(ai, i, o.Row, o.Point, o.Hand, o.Site, o.Kind);
                if (o.Authorization is { } authorization) ai.Strings[AuthorizationKey + i] = authorization;
                i++;
            }

            return;
        }

        foreach (var e in cold.OrderQueue)
        {
            if (e.SourceCommand?.Issuer is not null && (!world.Entities.TryResolve(cold.Entity, out var actor)
                || !world.ValidOrder(actor.Slot, e) || e.Owner.CanRun(world, actor.Slot, e).Verdict == OrderVerdict.Gone)) continue;
            int kind = e.Row == TaskType.None ? -1 : e.Owner.SaveKind(e);
            if (kind < 0) continue;

            Put(ai, i, SavedRow(e.Row), e.Point, e.Site is { } site ? SiteHand(world, site, buildings) : Hand.Null, e.Site, kind);
            if (!e.Target.IsNone) target(ai, Target + i, e.Target);
            if (CaptureAuthorization(e, world) is { } authorization) ai.Strings[AuthorizationKey + i] = authorization;
            i++;
        }
    }

    /// <summary>
    /// Writes order <paramref name="i"/> into the AI record; Meitou's site and detail keys only when set.
    /// </summary>
    static void Put(FcsRecord ai, int i, TaskType row, Vector3 point, Hand hand, string? site, int kind)
    {
        ai.Ints[Type + i] = (int)row;
        ai.Vector3S[Point + i] = point;
        hand.Write(ai, Target + i);
        if (site is not null) ai.Strings[MeitouSite + i] = site;
        if (kind != 0) ai.Ints[MeitouKind + i] = kind;
    }

    /// <summary>
    /// The row as the original numbers it: the Sleep order is 258 <c>USE_BED_ORDER</c> (Meitou's row 98 is the task it starts); the other rows are
    /// the same.
    /// </summary>
    static TaskType SavedRow(TaskType row) => row == TaskType.UseBed ? TaskType.UseBedOrder : row;

    /// <summary>
    /// The handle of the building a site or fixture is, when it is the building itself (a bed the player built too) and the save holds its state;
    /// else the null handle (the site id names it).
    /// </summary>
    static Hand SiteHand(World world, string id, IReadOnlyDictionary<string, FcsRecord> buildings)
    {
        string? key = world.Buildings.Find(id) is { } site
            ? site.LevelPlacement is null
                ? BuildingKey.Of(site.Zone, id)
                : id
            : world.Fixtures.Find(id) is { } fixture && (fixture.Id == fixture.Building || fixture is { Kind: Locks.FixtureKind.Bed, Site: < 0 })
                ? fixture.Building
                : null;
        return key is not null && buildings.TryGetValue(key, out var state) && Hand.IsComplete(state, "handle")
            ? Hand.Read(state, "handle")
            : Hand.Null;
    }

    /// <summary>
    /// Reads the orders as the original's loader does (every <c>tjob&lt;i&gt;</c> from 0 until one is missing); only a player character's are read. A
    /// building handle is turned into its instance key now (the way <see cref="PermaJobSave.Read"/> does); a character is found when the decision
    /// step restores the queue, after every character is loaded.
    /// </summary>
    public static void Read(FcsRecord ai, CharacterCold cold, SaveHandles handles, IReadOnlyDictionary<FcsRecord, string> instanceOf)
    {
        List<SavedOrder>? orders = null;
        for (int i = 0; ai.Ints.TryGetValue(Type + i, out int row); i++)
        {
            var hand = Hand.Read(ai, Target + i);
            var point = ai.Vector3S.GetValueOrDefault(Point + i);
            string? site = ai.Strings.TryGetValue(MeitouSite + i, out var id) && id.Length > 0 ? id : null;
            string? key = null;
            bool lost = false;
            if (site is null && !hand.IsNull && hand.Type != (int)FcsRecordType.Character)
            {
                if (hand.Type == (int)FcsRecordType.Building && handles.Building(hand) is { } b && instanceOf.TryGetValue(b, out var instance))
                    key = instance;
                else
                    lost = true;
            }

            (orders ??= []).Add(new SavedOrder(
                (TaskType)row, point, hand, site, key, ai.Ints.GetValueOrDefault(MeitouKind + i), lost, ai.Strings.GetValueOrDefault(AuthorizationKey + i)));
        }

        cold.SavedOrders = orders;
    }

    /// <summary>
    /// Makes the orders a load read the character's queue (serial phases, from the decision step or a queued order, <see cref="PlayerOrders.Give"/>): nothing is started, and the front is tried like any new order. An order whose row no system
    /// owns, whose target character or site is not in the world, or whose handle named nothing is left out, as the queue pops an order that is <see cref="OrderVerdict.Gone"/>.
    /// </summary>
    internal static void Restore(World world, int slot, CharacterCold cold)
    {
        var saved = cold.SavedOrders;
        cold.SavedOrders = null;
        if (saved is null || cold.OrderQueue.Count > 0) return;
        var table = world.Characters;
        var self = table.IdOf(slot);
        foreach (var o in saved)
        {
            var row = o.Row == TaskType.UseBedOrder ? TaskType.UseBed : o.Row;
            if (o.Lost || world.OrderOwnerOf(row) is not { } owner) continue;
            var target = CharacterId.None;
            if (o is { Hand: { IsNull: false, Type: (int)FcsRecordType.Character } } && (target = Find(table, o.Hand)).IsNone) continue;
            string? site = null;
            if ((o.Site ?? o.InstanceKey) is not null && (site = SiteOf(world, o)) is null) continue;
            var source = RestoreAuthorization(o.Authorization, world, o.Site);
            // Legacy native rows have no durable controller/grant evidence.
            if (world.Players.RequiresExplicitControl && cold.IsPlayer && source is null) continue;
            var entry = new OrderEntry(row, owner) { Target = target, Site = site, Point = o.Point, Detail = owner.LoadDetail(world, self, row, o.Kind, target), SourceCommand = source };
            if (world.ValidOrder(slot, entry)) cold.OrderQueue.Add(entry);
        }
        DecisionSystem.Request(cold);
    }

    /// <summary>
    /// The world character whose saved handle is <paramref name="hand"/>; <see cref="CharacterId.None"/> when none is.
    /// </summary>
    static CharacterId Find(CharacterTable table, Hand hand)
    {
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s)?.Save?.Source.Handle == hand) return table.IdOf(s);

        return CharacterId.None;
    }

    /// <summary>
    /// The site an order names: Meitou's id when the world has it (a build site, a fixture, a rock, a town dummy or turret), else the instance a building handle named, as a
    /// build site or a fixture.
    /// </summary>
    static string? SiteOf(World world, SavedOrder o)
    {
        if (o.Site is { } id)
        {
            bool known = world.Buildings.Find(id) is not null || world.Fixtures.Find(id) is not null || ProductionSystem.IsNatural(id)
                || Locks.FixtureRegistry.IsDummy(id);
            return known ? id : null;
        }

        var key = o.InstanceKey!;
        int colon = key.IndexOf(':');
        if (colon >= 0 && world.Buildings.Find(key[(colon + 1)..]) is not null) return key[(colon + 1)..];

        return world.Fixtures.Find(key) is not null ? key : null;
    }


    sealed class StableCharacterJson(World world) : JsonConverter<CharacterId>
    {
        public override CharacterId Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
        {
            ulong entity = reader.GetUInt64();
            if (entity == 0) return CharacterId.None;
            return world.Entities.TryResolve(new EntityId(entity), out var id) ? id : throw new JsonException("Queued-order role is not loaded.");
        }
        public override void Write(Utf8JsonWriter writer, CharacterId value, JsonSerializerOptions options)
        {
            if (value.IsNone) { writer.WriteNumberValue(0UL); return; }
            if (!world.Characters.TryResolveNext(value, out int slot) || world.Characters.Cold(slot) is not { Entity.IsNone: false } cold)
                throw new JsonException("Queued-order role has no stable identity.");
            writer.WriteNumberValue(cold.Entity.Value);
        }
    }


    sealed record Authorization(string Type, string Payload, PlayerId Issuer, CharacterControl[] Grants, ulong Membership, OrderAsset[]? Assets = null);


    static JsonSerializerOptions CommandJson(World world) => new()
    {
        IncludeFields = true,
        Converters = { new StableCharacterJson(world) },
    };


    static SimCommand? RestoreAuthorization(string? text, World world, string? site)
    {
        if (text is null) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<Authorization>(text, CommandJson(world));
            if (saved is null || saved.Issuer.IsNone || saved.Grants is null || saved.Grants.Length == 0
                || saved.Grants.Any(g => g.Player != saved.Issuer || !world.Players.Validate(g))) return null;
            // Resolve only concrete engine commands; save content cannot name an arbitrary CLR type.
            var type = typeof(SimCommand).Assembly.GetType(saved.Type, throwOnError: false);
            if (type is null || type.IsAbstract || !typeof(SimCommand).IsAssignableFrom(type)) return null;
            if (JsonSerializer.Deserialize(saved.Payload, type, CommandJson(world)) is not SimCommand command || !PlayerCommandRouting.Supports(command)) return null;
            if (DescribeAssets(command, world, site) is not { } assets || !assets.SequenceEqual(saved.Assets ?? [])) return null;
            command = RebindAssets(command, world, site);
            return command with { Issuer = saved.Issuer, ControlGrants = saved.Grants, FactionGrantRevision = saved.Membership, Tick = world.Tick };
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException) { return null; }
    }


    static string? CaptureAuthorization(OrderEntry entry, World world)
    {
        var source = entry.SourceCommand;
        if (source?.Issuer is not { } issuer || issuer.IsNone || !PlayerCommandRouting.Supports(source)) return null;
        try
        {
            if (DescribeAssets(source, world, entry.Site) is not { } assets) return null;
            var type = source.GetType();
            return JsonSerializer.Serialize(new Authorization(type.FullName!, JsonSerializer.Serialize(source, type, CommandJson(world)),
                issuer, source.ControlGrants.ToArray(), source.FactionGrantRevision, assets), CommandJson(world));
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    const string Type = "tjob", Point = "tjobP", Target = "tjobT", MeitouSite = "meitou tjob", MeitouKind = "meitou tjobK", AuthorizationKey = "meitou tjob auth";

}