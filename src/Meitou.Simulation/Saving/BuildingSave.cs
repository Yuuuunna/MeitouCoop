using System.Numerics;
using System.Text.Json;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The player's buildings and construction sites in a save (docs/formats/zones.md "Placement keys", docs/formats/save.md). Each is an instance of its zone file's <c>0-buildinglist</c>, id
/// <c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c>, with a GAMESTATE_BUILDING state <c>&lt;id&gt;-S23</c> in the keys of the original's writer (<c>FUN_1405547f0</c>, <b>Verified</b> by code; no player-built building
/// is in the sample saves, so a real file with one has not been compared): <c>name</c>, <c>owner faction ID</c>, <c>floornum</c>, <c>destroyed</c>, <c>flags</c>, <c>is complete</c>, <c>pause</c>,
/// <c>construction progress</c>, <c>&lt;i&gt;construction mats</c> (delivered of entry i), <c>world Y pos</c> (absolute) and the handles <c>handle</c>, <c>isInsideBuilding</c>, <c>town</c>, <c>residents</c>.
/// The instance's position is the absolute one (<b>Observed</b>: in the samples all 11405 game-made `-zoneX.Y-INGAME` buildings have an instance Y equal to <c>world Y pos</c>; the placements of the level data differ by a height above the terrain). Meitou's own key <c>meitou parent</c> (the parent's
/// raw instance id or zone-qualified key) carries a parent the original's handle cannot name, and wins on load. <c>town</c> is the handle of the player outpost the site belongs to (<see cref="WriteTowns"/>).
/// </summary>
public static class BuildingSave
{
    public const string MeitouParent = "meitou parent";
    public const string ManagedKey = "meitou constructed building";
    public const string OutpostKey = "meitou outpost";
    public const string TownNumberKey = "meitou town number";
    public const string AllocationKey = "meitou building allocation";
    sealed record ZoneAllocation(int X, int Y, int Last);
    sealed record Allocations(int Version, ZoneAllocation[] Zones);
    /// <summary>The container serial of every zone container (<b>Verified</b>, 1496 of 1496 handles).</summary>
    const int ContainerSerial = 11111;
    /// <summary>The <c>version</c> key of a building state (<b>Observed</b> 270 in every sample state).</summary>
    const int StateVersion = 270;
    const string StateSuffix = "-S23";   // the hex of GAMESTATE_BUILDING (35)

    static void RestoreAllocations(SaveGame save, BuildingRegistry registry)
    {
        if (registry.Count != 0 || registry.Towns.Count != 0) throw new InvalidOperationException("Building restoration requires an empty registry.");
        if (!save.Camera.Record.Strings.TryGetValue(AllocationKey, out var text)) throw new InvalidDataException("Missing building allocation state.");
        Allocations state;
        try { state = JsonSerializer.Deserialize<Allocations>(text, new JsonSerializerOptions { RespectRequiredConstructorParameters = true })
            ?? throw new InvalidDataException("Missing building allocation state."); }
        catch (JsonException e) { throw new InvalidDataException("Malformed building allocation state.", e); }
        if (state.Version != 1 || state.Zones is null) throw new InvalidDataException("Unsupported building allocation state.");
        var zones = new HashSet<ZoneCoordinate>();
        foreach (var entry in state.Zones)
            if (entry is null || entry.X < 0 || entry.Y < 0 || entry.X >= WorldLayout.ZoneCount || entry.Y >= WorldLayout.ZoneCount ||
                entry.Last < 0 || !zones.Add(new ZoneCoordinate(entry.X, entry.Y))) throw new InvalidDataException("Invalid building allocation counter.");
        foreach (var entry in state.Zones) registry.Reserve(BuildingRegistry.IdOf(entry.Last, new ZoneCoordinate(entry.X, entry.Y)));
    }

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Reserves the ids of every <c>-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c> building of the save (generated interiors share the pattern) and reads the player faction's into the world's registry, in zone and list
    /// order. Returns how many sites it made.
    /// </summary>
    public static int Read(SaveGame save, World world, GameDatabase db, string playerFactionId, int playerIndex)
        => ReadCore(save, world, db, id => id == playerFactionId ? playerIndex : -1, multiplayer: false);

    public static int ReadMultiplayer(SaveGame save, World world, PopulationData data)
        => ReadCore(save, world, data.Db, data.FactionIndex, multiplayer: true);

    static int ReadCore(SaveGame save, World world, GameDatabase db, Func<string, int> ownerOf, bool multiplayer)
    {
        var reg = world.Buildings;
        if (multiplayer) RestoreAllocations(save, reg);
        foreach (var zone in save.Zones.Values)
            if (zone.BuildingList is { } list)
                foreach (var i in list.Instances) reg.Reserve(i.Id);
        var states = HeldSave.BuildingStates(save);
        // A parent named by handle comes back as its zone-file id (player buildings' ids are unique: every id in the save is reserved above).
        var idOf = new Dictionary<FcsRecord, string>(ReferenceEqualityComparer.Instance);
        foreach (var zone in save.Zones.Values)
            if (zone.BuildingList is { } ids)
                foreach (var i in ids.Instances)
                    if (states.TryGetValue(BuildingKey.Of(zone.Coordinate, i.Id), out var record)) idOf[record] = i.Id;
        var handles = save.BuildHandles();
        var townOf = ReadTowns(save, world, ownerOf, multiplayer);
        int made = 0;
        foreach (var zone in save.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;
            foreach (var instance in list.Instances)
            {
                if (instance.IsRemoved || !states.TryGetValue(BuildingKey.Of(zone.Coordinate, instance.Id), out var state)) continue;
                int owner = ownerOf(state.Strings.GetValueOrDefault("owner faction ID", ""));
                if (multiplayer ? !state.Bools.GetValueOrDefault(ManagedKey) : owner < 0) continue;
                if (!BuildingRegistry.TryParseId(instance.Id, out _, out _) || state.Bools.GetValueOrDefault("meitou town construction"))
                {
                    if (multiplayer) throw new InvalidDataException("Invalid constructed building identity.");
                    continue;
                }
                if (owner < 0) throw new InvalidDataException("Constructed building has an unknown owner.");
                if (db.Find(instance.Target) is not { Type: FcsRecordType.BUILDING } record)
                {
                    if (multiplayer) throw new InvalidDataException("Constructed building content is missing.");
                    continue;
                }
                var data = BuildingBuildData.From(record);
                var materials = new MaterialSlot[data.Materials.Count];
                for (int k = 0; k < materials.Length; k++)
                    materials[k] = new MaterialSlot(data.Materials[k].Item, data.Materials[k].Required) { Delivered = state.Floats.GetValueOrDefault($"{k}construction mats") };
                float target = data.Target;
                bool complete = state.Bools.GetValueOrDefault("is complete", true);   // absent means complete
                string? parent = state.Strings.GetValueOrDefault(MeitouParent) is { Length: > 0 } own ? own
                    : state.Bools.GetValueOrDefault("is inside building") && handles.Building(Hand.Read(state, "isInsideBuilding")) is { } building ? idOf.GetValueOrDefault(building) : null;
                float y = state.Floats.TryGetValue("world Y pos", out float absolute) ? absolute : instance.Position.Y;
                var townHand = Hand.Read(state, "town");
                if (multiplayer && !townHand.IsNull && !townOf.ContainsKey((townHand.C, townHand.CS)))
                    throw new InvalidDataException("Constructed building names an unknown outpost.");
                reg.Add(new BuildSite
                {
                    Id = instance.Id,
                    Record = data.Id,
                    Name = multiplayer ? state.Strings.GetValueOrDefault("name", data.Name) : data.Name,
                    Position = new Vector3(instance.Position.X, y, instance.Position.Z),
                    Rotation = instance.Rotation,
                    Floor = state.Ints.GetValueOrDefault("floornum"),
                    Parent = parent,
                    Town = townHand.IsNull ? 0 : townOf.GetValueOrDefault((townHand.C, townHand.CS)),
                    Owner = owner,
                    Materials = materials,
                    Target = target,
                    Rate = data.Rate,
                    Progress = multiplayer ? Math.Clamp(state.Floats.GetValueOrDefault("construction progress", complete ? target : 0), 0, target)
                        : complete ? target : MathF.Min(state.Floats.GetValueOrDefault("construction progress"), target),
                    Complete = complete,
                    Paused = state.Bools.GetValueOrDefault("pause"),
                    Public = state.Bools.GetValueOrDefault("is public day", true),
                    Destroyed = state.Bools.GetValueOrDefault("destroyed"),
                    Upgrading = multiplayer && state.Bools.GetValueOrDefault("meitou upgrading"),
                    Ruined = multiplayer && state.Bools.GetValueOrDefault("meitou ruined"),
                });
                made++;
            }
        }
        // Wall subsection links are not saved; zone load rebuilds them (docs/game/buildings-production.md "Walls").
        var wallData = new Dictionary<string, BuildingBuildData?>(StringComparer.Ordinal);
        BuildingBuildData? DataOf(string id) =>
            wallData.TryGetValue(id, out var d) ? d : wallData[id] = db.Find(id) is { Type: FcsRecordType.BUILDING } r ? BuildingBuildData.From(r) : null;
        ConstructionSystem.AttachWalls(reg, DataOf, reg.All.ToList());
        // Engine choice: towns are reassessed on load (a save may hold a centre that was never moved, as Meitou's before it reassessed).
        ConstructionSystem.ReassessTowns(world);
        return made;
    }

    /// <summary>
    /// The player's outposts: the entries of <c>quick.save</c>'s town list whose GAMESTATE_TOWN is not a nest and is the player faction's, in list order. Returns their numbers by the
    /// handle's C and CS parts (how a building's <c>town</c> handle names them).
    /// </summary>
    static Dictionary<(int C, int CS), int> ReadTowns(SaveGame save, World world, Func<string, int> ownerOf, bool multiplayer)
    {
        var map = new Dictionary<(int, int), int>();
        if (save.TownInstanceList is not { } list) return map;
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in save.Quick.Records) byId[r.StringId] = r;
        var entries = multiplayer ? list.Instances.OrderBy(i => i.States.Count > 0 && byId.TryGetValue(i.States[0], out var s) ? s.Ints.GetValueOrDefault(TownNumberKey) : 0)
            : list.Instances.AsEnumerable();
        foreach (var instance in entries)
        {
            if (instance.IsRemoved || instance.States.Count == 0 || !byId.TryGetValue(instance.States[0], out var state)) continue;
            if (state.RecordType != FcsRecordType.GAMESTATE_TOWN || state.Bools.GetValueOrDefault("is nest")) continue;
            int owner = ownerOf(state.Strings.GetValueOrDefault("faction", ""));
            if (multiplayer ? !state.Bools.GetValueOrDefault(OutpostKey) : owner < 0) continue;
            if (owner < 0 || multiplayer && state.Ints.GetValueOrDefault(TownNumberKey) != world.Buildings.Towns.Count + 1)
                throw new InvalidDataException("Invalid outpost identity or owner.");
            string name = state.Name.StartsWith(TownStatePrefix, StringComparison.Ordinal) && state.Name.Length > TownStatePrefix.Length ? state.Name[TownStatePrefix.Length..] : PlayerTown.DefaultName;
            var town = world.Buildings.AddTown(instance.Position, owner, name);
            town.SaveInstance = instance.Id;
            town.Public = state.Bools.GetValueOrDefault("public");
            var hand = Hand.Read(state, "hand");
            if (!hand.IsNull)
            {
                town.SaveHandle = hand;
                if (!map.TryAdd((hand.C, hand.CS), town.Number) && multiplayer) throw new InvalidDataException("Duplicate outpost handle.");
            }
        }
        return map;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Writes the registry into the save: every site's instance and state in its zone file (a zone without a file gets one), and removes the player-built entries the world no longer has (a
    /// dismantled building). Everything else in the zone files is left as it was. Returns the town handle each player town (by number) was written with.
    /// </summary>
    public static IReadOnlyDictionary<int, Hand> Write(SaveGame game, World world, string playerFactionId, ulong seed = 0)
        => WriteCore(game, world, _ => playerFactionId, state => state.Strings.GetValueOrDefault("owner faction ID") == playerFactionId, seed, multiplayer: false);

    public static IReadOnlyDictionary<int, Hand> WriteMultiplayer(SaveGame game, World world, PopulationData data, ulong seed = 0, string? importedPlayerFaction = null)
        => WriteCore(game, world, owner => owner >= 0 && owner < data.Factions.Count ? data.Factions[owner].Id
            : throw new InvalidDataException("Constructed asset has an unknown faction."), state => state.Bools.GetValueOrDefault(ManagedKey) ||
                importedPlayerFaction is not null && state.Strings.GetValueOrDefault("owner faction ID") == importedPlayerFaction, seed, multiplayer: true);

    static IReadOnlyDictionary<int, Hand> WriteCore(SaveGame game, World world, Func<int, string> ownerId, Func<FcsRecord, bool> managed, ulong seed, bool multiplayer)
    {
        var reg = world.Buildings;
        if (multiplayer)
            game.Camera.Record.Strings[AllocationKey] = JsonSerializer.Serialize(new Allocations(1, reg.AllocationCounters
                .OrderBy(p => p.Key.X).ThenBy(p => p.Key.Y).Select(p => new ZoneAllocation(p.Key.X, p.Key.Y, p.Value)).ToArray()));
        var live = new HashSet<string>(reg.All.Select(s => s.Id), StringComparer.Ordinal);
        foreach (var zone in game.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;
            var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
            foreach (var r in zone.File.Records) byId[r.StringId] = r;
            foreach (var instance in list.Instances.ToList())
            {
                if (live.Contains(instance.Id)) continue;
                var state = instance.States.Select(id => byId.GetValueOrDefault(id)).FirstOrDefault(r => r?.RecordType == FcsRecordType.GAMESTATE_BUILDING);
                if (state is null || !BuildingRegistry.TryParseId(instance.Id, out _, out _) || state.Bools.GetValueOrDefault("meitou town construction") || !managed(state)) continue;
                list.Instances.Remove(instance);
                foreach (var inventory in instance.States.Select(id => byId.GetValueOrDefault(id)).Where(r => r?.RecordType == FcsRecordType.INVENTORY_STATE).ToArray())
                {
                    SaveInventory.Replace(zone.File, inventory!, [], n => $"{n}-zone{zone.Coordinate.X}.{zone.Coordinate.Y}-INGAME");
                    zone.File.Records.Remove(inventory!);
                }
                zone.File.Records.Remove(state!);
                if (Hand.Read(state!, "handle") is { IsNull: false } h && zone.File.SlotLists.Count > 0)
                    zone.File.SlotLists[0] = [.. zone.File.SlotLists[0].Where(slot => slot != h.I)];
            }
        }
        var townHands = WriteTowns(game, world, ownerId, seed, multiplayer);
        if (reg.Count == 0) return townHands;
        foreach (var site in reg.All.Where(s => s.LevelPlacement is null))
        {
            if (multiplayer && site.Town != 0 && !townHands.ContainsKey(site.Town)) throw new InvalidDataException("Constructed building has an unknown outpost.");
            WriteSite(game, site, ownerId(site.Owner), seed, townHands.GetValueOrDefault(site.Town, Hand.Null), world.Fixtures, multiplayer);
        }

        // Parents last: they may be sites written above.
        var states = HeldSave.BuildingStates(game);
        foreach (var site in reg.All.Where(s => s.LevelPlacement is null))
        {
            if (!states.TryGetValue(BuildingKey.Of(site.Zone, site.Id), out var state)) continue;
            if (site.Parent is { } parent)
            {
                state.Strings[MeitouParent] = parent;
                state.Bools["is inside building"] = true;
                ((states.TryGetValue(parent, out var p) || states.TryGetValue(BuildingKey.Of(site.Zone, parent), out p))
                    && Hand.IsComplete(p, "handle") ? Hand.Read(p, "handle") : Hand.Null).Write(state, "isInsideBuilding");
            }
            else
            {
                state.Strings.Remove(MeitouParent);
                state.Bools["is inside building"] = false;
                Hand.Null.Write(state, "isInsideBuilding");
            }
        }
        return townHands;
    }

    static void WriteSite(SaveGame game, BuildSite site, string playerFactionId, ulong seed, Hand town, Locks.FixtureRegistry fixtures, bool multiplayer)
    {
        var coordinate = site.Zone;
        if (!game.Zones.TryGetValue(coordinate, out var zone)) game.Zones[coordinate] = zone = new SaveZone(coordinate, new SaveFile());
        var file = zone.File;
        var list = zone.BuildingList;
        if (list is null)
        {
            list = new FcsRecord { Type = (int)FcsRecordType.INSTANCE_COLLECTION, Id = NextNumber(file), Name = "0", StringId = Meitou.Data.World.WorldLevelData.BuildingListId };
            file.Records.Add(list);
        }
        string stateId = site.Id + StateSuffix;
        var state = file.Records.FirstOrDefault(r => r.StringId == stateId && r.RecordType == FcsRecordType.GAMESTATE_BUILDING);
        if (state is null)
        {
            state = new FcsRecord { Type = (int)FcsRecordType.GAMESTATE_BUILDING, Id = NextNumber(file), Name = "0", StringId = stateId };
            file.Records.Add(state);
        }
        var at = site.Position;
        var instance = list.Instances.FirstOrDefault(i => i.Id == site.Id);
        if (instance is null)
        {
            instance = new FcsInstance { Id = site.Id };
            list.Instances.Add(instance);
        }
        instance.Target = site.Record;
        instance.Position = at;
        instance.Rotation = site.Rotation;
        instance.States.Clear();
        instance.States.Add(stateId);

        // The slot: kept while the state has one in this zone, else the next free one of the zone's slot list.
        if (file.SlotLists.Count == 0) file.SlotLists.Add([]);
        var hand = Hand.Read(state, "handle");
        if (hand.IsNull || hand.C != zone.ContainerId)
        {
            int slot = Math.Max(file.SlotLists[0].DefaultIfEmpty(0).Max(), file.Records.Where(r => r.RecordType == FcsRecordType.GAMESTATE_BUILDING)
                .Select(r => Hand.Read(r, "handle")).Where(h => !h.IsNull && h.C == zone.ContainerId).Select(h => h.I).DefaultIfEmpty(0).Max()) + 1;
            hand = new Hand((int)FcsRecordType.BUILDING, zone.ContainerId, ContainerSerial, slot, (int)Rng.Mix(Rng.StableHash(site.Id) ^ seed));
        }
        if (!file.SlotLists[0].Contains(hand.I)) file.SlotLists[0] = [.. file.SlotLists[0].Append(hand.I).Order()];

        state.Floats["world Y pos"] = site.Position.Y;
        state.Floats["player furniture Y offset"] = 0;
        state.Floats["construction progress"] = site.Progress;
        for (int i = 0; i < site.Materials.Length; i++) state.Floats[$"{i}construction mats"] = site.Materials[i].Delivered;
        state.Ints["version"] = StateVersion;
        state.Ints["flags"] = site.Parent is null ? 1 : 0;   // bit 0: is outside
        state.Ints["floornum"] = site.Floor;
        state.Ints["serialised interior"] = 1;
        hand.Write(state, "handle");
        town.Write(state, "town");
        Hand.Null.Write(state, "residents");
        state.Bools["foliage"] = false;
        state.Bools["pause"] = site.Paused;
        state.Bools["is public day"] = site.Public;
        state.Bools["is complete"] = site.Complete;
        state.Bools["is inside building"] = site.Parent is not null;
        state.Bools["destroyed"] = site.Destroyed;
        state.Strings["squad template"] = "";
        state.Strings["owner faction ID"] = playerFactionId;
        state.Strings["residents template"] = "";
        state.Strings["name"] = site.Name;
        if (multiplayer)
        {
            state.Bools[ManagedKey] = true;
            state.Bools["meitou upgrading"] = site.Upgrading;
            state.Bools["meitou ruined"] = site.Ruined;
        }
        // Its doors (a finished building has them registered): the same keys as a town building's (docs/game/locks.md "Save keys").
        fixtures.WriteBuilding(site.Id, state);
    }

    /// <summary>
    /// Writes the player's outposts into <c>quick.save</c> (docs/formats/save.md "Player outposts"; the original's town-list writer <c>FUN_14092a6c0</c> and <c>Town::vfunc_17/85</c>, <b>Verified</b> by
    /// code, no sample save has one): an entry of the GAMESTATE_TOWN_INSTANCE_LIST (made when the save has none) with id <c>&lt;n&gt;-quick.save-INGAME</c>, target
    /// <see cref="PlayerTown.Template"/>, the absolute position and a zero quaternion, and its GAMESTATE_TOWN <c>&lt;id&gt;-S5e</c> named "Town state &lt;name&gt;". An outpost read from a
    /// save keeps its entry, state and handle; a new one gets the next town container. Returns each outpost's handle by number, for the buildings' <c>town</c>.
    /// </summary>
    static Dictionary<int, Hand> WriteTowns(SaveGame game, World world, Func<int, string> ownerId, ulong seed, bool multiplayer)
    {
        var hands = new Dictionary<int, Hand>();
        var towns = world.Buildings.Towns;
        if (towns.Count == 0) return hands;
        var quick = game.Quick;
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in quick.Records) byId[r.StringId] = r;
        var list = game.TownInstanceList;
        if (list is null)
        {
            int n = NextQuick(quick);
            list = new FcsRecord { Type = (int)FcsRecordType.GAMESTATE_TOWN_INSTANCE_LIST, Id = n, Name = "0", StringId = QuickId(n) };
            quick.Records.Add(list);
            game.TownInstanceList = list;
        }
        int container = game.Towns.Select(t => t.Handle).Where(h => !h.IsNull).Select(h => h.C).DefaultIfEmpty(0).Max();
        foreach (var town in towns)
        {
            var instance = town.SaveInstance is { } known ? list.Instances.FirstOrDefault(i => i.Id == known) : null;
            var state = instance is { States.Count: > 0 } ? byId.GetValueOrDefault(instance.States[0]) : null;
            if (state is { RecordType: not FcsRecordType.GAMESTATE_TOWN }) state = null;
            if (instance is null)
            {
                instance = new FcsInstance { Id = QuickId(NextQuick(quick)) };
                list.Instances.Add(instance);
            }
            bool fresh = state is null;
            if (state is null)
            {
                int n = NextQuick(quick);
                state = new FcsRecord { Type = (int)FcsRecordType.GAMESTATE_TOWN, Id = n, StringId = instance.Id + TownStateSuffix };
                // With the other town states, before the list (the original's order).
                int at = quick.Records.IndexOf(list);
                quick.Records.Insert(at < 0 ? quick.Records.Count : at, state);
                byId[state.StringId] = state;
                game.Towns.Add(new SaveTown(state));
            }
            instance.Target = PlayerTown.Template;
            instance.Position = town.Position;
            instance.Rotation = new Quaternion(0, 0, 0, 0);
            instance.States.Clear();
            instance.States.Add(state.StringId);

            var hand = town.SaveHandle ?? (Hand.Read(state, "hand") is { IsNull: false } h ? h
                : new Hand((int)FcsRecordType.TOWN, ++container, (int)Rng.Mix(Rng.StableHash(instance.Id) ^ seed), 0, 0));
            hands[town.Number] = hand;

            state.Name = TownStatePrefix + town.Name;
            if (fresh)
            {
                // Town::vfunc_85 / TownBase's keys; FUN_140934e10 clears `public`. `discovered` and `explored` are Meitou's choice (the player made it).
                foreach (var k in new[] { "started", "recently discovered", "ded", "is nest", "public" }) state.Bools[k] = false;
                state.Bools["discovered"] = true;
                state.Bools["explored"] = true;
                foreach (var k in new[] { "pop", "pop2", "popd", "popd2", "tod" }) state.Floats[k] = 0;
                state.Ints["artifacts count"] = 0;
                state.Ints["plevel"] = 0;
                state.Ints["override"] = 0;
                state.Strings["building material"] = DefaultTownMaterial;
                state.Strings["replacementTown"] = "";
            }
            state.Bools["public"] = town.Public;   // Town +0x380, the panel's Open to public
            hand.Write(state, "hand");
            state.Strings["faction"] = ownerId(town.Owner);
            if (multiplayer) { state.Bools[OutpostKey] = true; state.Ints[TownNumberKey] = town.Number; }
            state.Strings["instance"] = instance.Id;
            var zone = Meitou.Data.World.WorldLayout.ZoneOf(town.Position.X, town.Position.Z);
            state.Ints["zzX0"] = zone.X;
            state.Ints["zzY0"] = zone.Y;
        }
        return hands;
    }

    const string TownStatePrefix = "Town state ";
    const string TownStateSuffix = "-S5e";   // the hex of GAMESTATE_TOWN (94)
    /// <summary>A town without a material of its own uses this one (docs/formats/zones.md "Materials"; the template has none).</summary>
    const string DefaultTownMaterial = "742-gamedata.base";

    static string QuickId(int n) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{n}-{SaveFolder.QuickSaveName}-INGAME");

    /// <summary>Takes the next number of <c>quick.save</c>'s id counter.</summary>
    static int NextQuick(SaveFile file)
    {
        int n = Math.Max(file.Data.NextId, file.Records.Count == 0 ? 0 : file.Records.Max(r => Math.Max(r.Id, SaveGame.NumberOf(r)))) + 1;
        file.Data.NextId = n;
        return n;
    }

    /// <summary>One more than the highest numeric id of the file's records.</summary>
    static int NextNumber(SaveFile file) => file.Records.Count == 0 ? Math.Max(file.Data.NextId, 0) + 1 : Math.Max(file.Data.NextId, file.Records.Max(r => r.Id)) + 1;
}
