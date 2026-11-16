using System.Globalization;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The machines of the player's buildings in a save (docs/simulation.md "Production"): the production and farm keys of each building's
/// GAMESTATE_BUILDING (docs/game/buildings-production.md "Production", "Farming" and "Power": <c>production amount</c>, <c>resource mult</c>,
/// <c>power on</c>, <c>gets battery</c>, <c>batterycharge</c>, <c>consumption amount-&lt;k&gt;</c>, <c>plant growth</c>, <c>plant death</c>, <c>plant
/// start</c>, <c>plants harvested</c>, <c>save time</c>), and its items in an INVENTORY_STATE named first by the building's instance states
/// (docs/formats/save.md "Machine inventory sections"). The items' sections are the original's: <c>in1</c>, <c>in2</c>, ... for the inputs,
/// <c>out</c> for the output and <c>backpack_content</c> for a storage grid (a furnace's grid <c>in1</c>); an inventory older Meitou wrote numbers its
/// inputs from <c>in0</c> and names its grid <c>main</c>, and is read as such. A loaded farm's plants all take the farm's growth (the harvested ones
/// -10): the stagger between plants is not saved.
/// </summary>
public static partial class MachineSave
{
    /// <summary>
    /// The suffix of a building's INVENTORY_STATE id.
    /// </summary>
    const string InventorySuffix = "-inventory";

    /// <summary>
    /// The item furnace's input section (<b>Verified</b>, <c>FurnaceBuilding::setupFromData</c>), Meitou's storage grid of a furnace.
    /// </summary>
    const string FurnaceSection = "in1";
    /// <summary>
    /// The section of a building's storage grid (<b>Verified</b>, <c>FUN_140554530</c>: every building with <c>has inventory</c>), Meitou's
    /// <c>main</c>.
    /// </summary>
    const string StorageSection = "backpack_content";
    /// <summary>
    /// The Meitou bool on a player building's INVENTORY_STATE (<c>&lt;id&gt;-inventory</c>) that says its sections are the original's: one Meitou
    /// wrote without it numbers the inputs from <c>in0</c> (the original ignores it: keys are read by name).
    /// </summary>
    public const string OriginalSectionsKey = "meitou original sections";

    /// <summary>
    /// The key prefix of a remembered ingredient's string id (followed by its number).
    /// </summary>
    const string PartialSid = "craft partial item sid ";
    /// <summary>
    /// The key prefix of a remembered ingredient's level (followed by its number).
    /// </summary>
    const string PartialVal = "craft partial item val ";

    /// <summary>
    /// Meitou references on a layout's parent and machine state; they never create a second rendered furniture placement.
    /// </summary>
    const string InteriorPrefix = "meitou interior machine ", InteriorInventory = "meitou machine inventory";

    /// <summary>
    /// Writes every machine into its building's state (after <see cref="BuildingSave.Write"/>).
    /// </summary>
    public static void Write(SaveGame game, World world)
    {
        ClearResetMachines(game, world);
        world.SavedMachines?.WritePending(game.Camera.Record, world);
        if (world.Machines.Count == 0) return;
        foreach (var m in world.Machines.All)
        {
            if (world.Buildings.Find(m.Site) is not { } site) continue;
            if (IsInteriorMachine(site))
            {
                WriteInterior(game, world, site, m);
                continue;
            }
            if (!game.Zones.TryGetValue(site.Zone, out var zone) || zone.BuildingList is not { } list)
            {
                world.SavedMachines?.RequireNativeCapture(m.Site);
                continue;
            }
            var file = zone.File;
            string rawId = site.LevelPlacement?.InstanceId ?? site.Id;
            var instance = list.Instances.FirstOrDefault(i => i.Id == rawId);
            var state = instance?.States.Select(id => file.Records.FirstOrDefault(r => r.StringId == id && r.RecordType == FcsRecordType.GamestateBuilding)).FirstOrDefault(r => r is not null);
            if (state is null || instance is null)
            {
                world.SavedMachines?.RequireNativeCapture(m.Site);
                continue;
            }
            WriteKeys(state, m, world.GameHours);
            if (WriteInventory(file, site.Zone, instance, null, Items(m)) is { } inventory)
                inventory.Bools[OriginalSectionsKey] = true;
        }
    }

    /// <summary>
    /// A layout machine belongs to its parent; its stable runtime id is not a zone placement id.
    /// </summary>
    static bool IsInteriorMachine(BuildSite site) => site.LevelMachine && site.Parent is not null;

    /// <summary>
    /// Finds a machine's existing parent placement and its native building state in the save.
    /// </summary>
    static (SaveZone Zone, FcsRecord State)? InteriorParent(SaveGame game, BuildSite site)
    {
        if (site.Parent is not { } parent || !game.Zones.TryGetValue(site.Zone, out var zone) || zone.BuildingList is not { } list) return null;
        string prefix = $"{site.Zone.X}.{site.Zone.Y}:";
        string raw = parent.StartsWith(prefix, StringComparison.Ordinal) ? parent[prefix.Length..] : parent;
        var instance = list.Instances.FirstOrDefault(i => i.Id == raw && !i.IsRemoved);
        var state = instance?.States.Select(id => zone.File.Records.FirstOrDefault(r => r.StringId == id
            && r.RecordType == FcsRecordType.GamestateBuilding)).FirstOrDefault(r => r is not null);
        return state is null ? null : (zone, state);
    }

    /// <summary>
    /// Saves a layout machine's native building and inventory records, referenced by its parent rather than added to the placement list.
    /// </summary>
    static void WriteInterior(SaveGame game, World world, BuildSite site, Machine machine)
    {
        if (InteriorParent(game, site) is not { } parent)
            throw new InvalidOperationException("Capturing an interior machine requires its parent building's native zone state.");

        var file = parent.Zone.File;
        string reference = InteriorPrefix + site.Id;
        string stateId = parent.State.Strings.GetValueOrDefault(reference) ?? site.Id + "-machine-S23";
        var state = file.Records.FirstOrDefault(r => r.StringId == stateId && r.RecordType == FcsRecordType.GamestateBuilding);
        if (state is null)
        {
            state = new FcsRecord { Type = (int)FcsRecordType.GamestateBuilding, Id = SaveBuilder.NextNumber(file), StringId = stateId, Name = site.Name };
            file.Records.Add(state);
        }
        parent.State.Strings[reference] = stateId;
        state.Strings["meitou machine site"] = site.Id;
        state.Strings["meitou machine record"] = site.Record;
        WriteKeys(state, machine, world.GameHours);

        var references = InteriorReferences(state);
        WriteInventory(file, site, references, Items(machine, !SharedStorage(world, site, machine)));
        string? inventory = references.States.FirstOrDefault(id => id != state.StringId);
        if (inventory is null) state.Strings.Remove(InteriorInventory);
        else state.Strings[InteriorInventory] = inventory;
    }

    /// <summary>
    /// The common native reader's record references, without adding a synthetic placement to the world's geometry.
    /// </summary>
    static FcsInstance InteriorReferences(FcsRecord state)
    {
        var references = new FcsInstance { Id = state.Strings.GetValueOrDefault("meitou machine site", "") };
        references.States.Add(state.StringId);
        if (state.Strings.GetValueOrDefault(InteriorInventory) is { Length: > 0 } inventory) references.States.Add(inventory);
        return references;
    }

    /// <summary>
    /// A layout container's grid has one save owner, ContainerSave, even when a machine uses the same inventory.
    /// </summary>
    static bool SharedStorage(World world, BuildSite site, Machine machine) => IsInteriorMachine(site)
        && machine.Storage is { } grid && ReferenceEquals(grid, world.Fixtures.Find(site.Id)?.Contents);

    /// <summary>
    /// Replaces the building's inventory record with its items, or removes the record when it holds none.
    /// </summary>
    static void WriteInventory(SaveFile file, BuildSite site, FcsInstance instance, List<SaveItem> saveItems)
    {
        if (WriteInventory(file, site.Zone, instance, site.Id + InventorySuffix, saveItems) is { } inventory)
            inventory.Bools[OriginalSectionsKey] = true;
    }

    /// <summary>
    /// Replaces a building instance's inventory record with its items, or removes the record when it holds none: the record
    /// <paramref name="inventoryId"/>, or with null the INVENTORY_STATE the instance lists (a level building's,
    /// <c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c> when it has none yet, as the original names it). Returns the record written, null when removed.
    /// </summary>
    internal static FcsRecord? WriteInventory(SaveFile file, ZoneCoordinate zone, FcsInstance instance, string? inventoryId, List<SaveItem> saveItems)
    {
        string IdOf(int n) => string.Create(CultureInfo.InvariantCulture, $"{n}-zone{zone.X}.{zone.Y}-INGAME");

        var inventory = inventoryId is null
            ? ListedInventory(file, instance)
            : file.Records.FirstOrDefault(r => r.StringId == inventoryId && r.RecordType == FcsRecordType.InventoryState);
        instance.States.Remove(inventory?.StringId ?? inventoryId ?? "");

        foreach (var id in instance.States.ToArray())
        {
            var duplicate = file.Records.FirstOrDefault(r => r.StringId == id && r.RecordType == FcsRecordType.InventoryState);
            if (duplicate is null) continue;
            SaveInventory.Replace(file, duplicate, [], IdOf);
            file.Records.Remove(duplicate);
            instance.States.Remove(id);
        }

        if (saveItems.Count == 0)
        {
            if (inventory is null) return null;
            SaveInventory.Replace(file, inventory, [], IdOf);
            file.Records.Remove(inventory);
            return null;
        }

        if (inventory is null)
        {
            int n = SaveBuilder.NextNumber(file);
            inventory = new FcsRecord { Type = (int)FcsRecordType.InventoryState, Id = n, Name = "0", StringId = inventoryId ?? IdOf(n) };
            file.Records.Add(inventory);
        }

        SaveInventory.Replace(file, inventory, saveItems, IdOf);
        instance.States.Insert(0, inventory.StringId);
        return inventory;
    }

    /// <summary>
    /// The INVENTORY_STATE a building instance lists among its states (the first), null for none.
    /// </summary>
    internal static FcsRecord? ListedInventory(SaveFile file, FcsInstance instance) =>
        instance.States
            .Select(id => file.Records.FirstOrDefault(r => r.StringId == id && r.RecordType == FcsRecordType.InventoryState))
            .FirstOrDefault(r => r is not null);

    /// <summary>
    /// Writes a machine's production, power, battery, input and farm keys into its building state.
    /// </summary>
    static void WriteKeys(FcsRecord state, Machine m, double gameHours)
    {
        state.Bools["meitou machine state"] = true;
        state.Ints["meitou machine status"] = m.Status;
        state.Floats["resource mult"] = m.Yield;

        // The power keys (Observed, vfunc_17/18 of the building classes FUN_1402a14b0 / FUN_1402a1750).
        state.Bools["power on"] = m.PowerOn;
        state.Bools["gets battery"] = m.GetsBattery;
        state.Bools["broken"] = m.Broken;
        if (m.Data.IsBattery) state.Floats["batterycharge"] = m.Charge;

        WriteProduction(state, m, gameHours);
    }

    /// <summary>
    /// Writes a machine's production keys into its building state: <c>resource mult</c>, <c>production amount</c>,
    /// <c>consumption amount-&lt;k&gt;</c>, a bench's crafting keys and a farm's keys (<c>FUN_1402a1fa0</c>, the farm's and the bench's writers after it).
    /// </summary>
    internal static void WriteProduction(FcsRecord state, Machine m, double gameHours)
    {
        state.Floats["resource mult"] = m.Yield;
        if (m.Output is { } o) state.Floats["production amount"] = o.Progress;
        for (int k = 0; k < m.Inputs.Length; k++)
            state.Floats["consumption amount-" + k.ToString(CultureInfo.InvariantCulture)] = m.Inputs[k].Level;

        if (m.Data.IsFurnace) state.Floats["consumption amount-0"] = m.BurnLevel;   // its one input, the burn level
        if (m.Craft is { } c) WriteCraft(state, m, c);
        if (m.Farm is not { } f) return;

        state.Floats["plant growth"] = f.Growth;
        state.Floats["plant death"] = f.Death;
        state.Floats["plant start"] = f.Start;
        state.Floats["save time"] = (float)(double.IsNaN(f.LiveHours) ? gameHours : f.LiveHours);
        state.Ints["plants harvested"] = f.Harvested;
    }

    /// <summary>The crafting keys (<b>Observed</b>, <c>CraftingBuilding::vfunc_17</c>): <c>num crafts</c> (a float), <c>repeat</c>, and per queued craft k <c>current&lt;k&gt;</c> (the item),
    /// <c>currentExtraData&lt;k&gt;</c> (a weapon's model, else empty), <c>current prog&lt;k&gt;</c> and <c>crit&lt;k&gt;</c>, then the partly drawn items (<b>Verified</b>, <c>FUN_1402b8440</c>): every remembered level above 0, with the sections' own levels stored first, as
    /// strings <c>craft partial item sid &lt;k&gt;</c> (the item), floats <c>craft partial item val &lt;k&gt;</c> and the int <c>craft partial item size</c> (how many).</summary>
    static void WriteCraft(FcsRecord state, Machine m, CraftState c)
    {
        foreach (var key in state.Strings.Keys.Where(k => k.StartsWith("current", StringComparison.Ordinal)).ToList()) state.Strings.Remove(key);
        foreach (var key in state.Floats.Keys.Where(k => k.StartsWith("current prog", StringComparison.Ordinal)).ToList()) state.Floats.Remove(key);
        foreach (var key in state.Ints.Keys.Where(k => k.StartsWith("crit", StringComparison.Ordinal)).ToList()) state.Ints.Remove(key);
        foreach (var key in state.Strings.Keys.Where(k => k.StartsWith(PartialSid, StringComparison.Ordinal)).ToList()) state.Strings.Remove(key);
        foreach (var key in state.Floats.Keys.Where(k => k.StartsWith(PartialVal, StringComparison.Ordinal)).ToList()) state.Floats.Remove(key);
        state.Floats["num crafts"] = c.Queue.Count;
        state.Bools["repeat"] = c.Repeat;
        for (int k = 0; k < c.Queue.Count; k++)
        {
            string n = k.ToString(CultureInfo.InvariantCulture);
            state.Strings["current" + n] = c.Queue[k].Option.Item;
            state.Strings["currentExtraData" + n] = c.Queue[k].Option.Model ?? "";
            state.Floats["current prog" + n] = c.Queue[k].Progress;
            state.Ints["crit" + n] = c.Queue[k].Critical;
        }
        WritePartial(state, m, c);
    }

    /// <summary>
    /// The partly drawn items of a craft: the sections' levels above 0 over the remembered ones, then the remembered ones still above 0.
    /// </summary>
    static void WritePartial(FcsRecord state, Machine m, CraftState c)
    {
        var partial = new Dictionary<string, float>(c.Partial, StringComparer.Ordinal);
        foreach (var input in m.Inputs)
        {
            if (input.Level > 0)
                partial[input.Item] = input.Level;
            else
                partial.Remove(input.Item);
        }

        int written = 0;
        foreach (var (item, level) in partial)
        {
            if (level <= 0) continue;

            string n = written++.ToString(CultureInfo.InvariantCulture);
            state.Strings[PartialSid + n] = item;
            state.Floats[PartialVal + n] = level;
        }

        state.Ints["craft partial item size"] = written;
    }

    /// <summary>
    /// The machine's items as saved, in the original's sections (docs/formats/save.md "Machine inventory sections"): each input k under
    /// <c>in&lt;k + 1&gt;</c>, the output under <c>out</c>, then the crafting bench's finished items and the storage grid's items (a furnace's under
    /// <c>in1</c>, any other's <c>main</c> under <c>backpack_content</c>).
    /// </summary>
    internal static List<SaveItem> Items(Machine m, bool includeStorage = true)
    {
        var list = new List<SaveItem>();
        for (int k = 0; k < m.Inputs.Length; k++)
        {
            if (m.Inputs[k].Count > 0)
            {
                string section = "in" + (k + 1).ToString(CultureInfo.InvariantCulture);
                list.Add(new SaveItem { BaseId = m.Inputs[k].Item, Section = section, Quantity = m.Inputs[k].Count });
            }
        }

        if (m.Output is { Count: > 0 } o) list.Add(new SaveItem { BaseId = o.Recipe.Item, Section = "out", Quantity = o.Count });
        if (m.Craft is { } c) list.AddRange(c.Done.Select(SavedKit.FromItem));
        if (includeStorage && m.Storage is { } s)
            list.AddRange(m.Data.IsFurnace ? s.Items.Select(i => SavedKit.FromItem(i, FurnaceSection)) : StorageItems(s));

        return list;
    }

    /// <summary>
    /// A storage grid's saved items with Meitou's <c>main</c> named as the original's grid section (<see cref="SavedKit.FromInventory"/>).
    /// </summary>
    static IEnumerable<SaveItem> StorageItems(Inventory s) =>
        s.Items
            .Where(i => !InventoryCommands.IsLimbSection(i.Section))
            .Select(StorageItem);

    /// <summary>
    /// A storage grid's saved item, <c>main</c> as <c>backpack_content</c>.
    /// </summary>
    static SaveItem StorageItem(ItemInstance item) => SavedKit.FromItem(item, item.Section == "main" ? StorageSection : item.Section);

    /// <summary>
    /// Read existing machines after buildings; the binding also handles subsequently created town and level-house machines.
    /// </summary>
    public static void Read(SaveGame save, World world, ProductionSystem production, ItemFactory factory, bool allOwners = false)
    {
        Bind(save, world, factory);
        foreach (var site in world.Buildings.All)
        {
            if ((!allOwners && site.Owner != world.Player.Faction) || production.Data(site.Record) is null) continue;
            if (production.Ensure(world, site) is { } machine) world.SavedMachines!.Apply(world, production, site, machine);
        }
    }

    /// <summary>
    /// Puts a building's saved items into its machine: the outputs, the inputs and the storage grid (<see cref="Items"/>). The inputs are numbered
    /// from <c>in1</c>, or from <c>in0</c> as older Meitou wrote them when <paramref name="fromZero"/> is set or an item lies in <c>in0</c> (the
    /// original never names one, docs/formats/save.md "Machine inventory sections").
    /// </summary>
    internal static void PutItems(Machine m, List<SaveItem> items, ItemFactory factory, bool fromZero = false, bool includeStorage = true)
    {
        int firstInput = fromZero || items.Any(i => i.Section == "in0") ? 0 : 1;
        var storage = new List<SaveItem>();
        foreach (var item in items)
        {
            int k = InputOf(item.Section) - firstInput;
            if (item.Section == "out" && m.Craft is { } craft)
            {
                if (SavedKit.ItemOf(item, factory) is { } done) craft.Done.Add(done);
            }
            else if (item.Section == "out" && m.Output is { } o && o.Recipe.Item == item.BaseId)
            {
                o.Count = Math.Min(o.Recipe.Capacity, o.Count + item.Quantity);
            }
            else if (k >= 0 && k < m.Inputs.Length && (m.Inputs[k].Item == item.BaseId || m.Craft is not null && m.Inputs[k].Count == 0))
            {
                // A crafting bench's sections hold its last craft's ingredients, also with nothing queued.
                m.Inputs[k].Item = item.BaseId;
                m.Inputs[k].Count = Math.Min(m.Inputs[k].Recipe.Capacity, m.Inputs[k].Count + item.Quantity);
            }
            else
            {
                storage.Add(item);
            }
        }

        if (includeStorage && m.Storage is { } grid && storage.Count > 0)
        {
            foreach (var item in SavedKit.ToGrid(storage, factory, grid.Width, grid.Height).Items)
            {
                // A furnace's in1 and any other building's backpack_content are Meitou's storage grid main.
                if (m.Data.IsFurnace || item.Section == StorageSection) item.Section = "main";

                // A grid that is now smaller (a storage sized from its one item) gets an item that no longer fits at the next free cell, as the
                // original's loader does.
                bool fits = item.X >= 0 && item.Y >= 0 && item.X + item.Width <= grid.Width && item.Y + item.Height <= grid.Height
                    && grid.IsFree(item.Section, item.X, item.Y, item.Width, item.Height);
                if (fits || grid.Add(item) is false && item.Quantity > 0) grid.Items.Add(item);
            }
        }
    }

    /// <summary>
    /// The number of an input section <c>in&lt;n&gt;</c>, -1 for any other section.
    /// </summary>
    static int InputOf(string section) =>
        section.StartsWith("in", StringComparison.Ordinal) && int.TryParse(section.AsSpan(2), CultureInfo.InvariantCulture, out int n) ? n : -1;

    /// <summary>
    /// Reads a crafting bench's repeat flag, remembered ingredient levels and finished crafts.
    /// </summary>
    static void ReadCraft(FcsRecord state, Machine m, ProductionSystem production)
    {
        m.Craft!.Repeat = state.Bools.GetValueOrDefault("repeat");

        // The remembered ingredient levels, read before any bind so the sections keep their saved levels (consumption amount-k).
        for (int k = 0, size = state.Ints.GetValueOrDefault("craft partial item size"); k < size; k++)
        {
            string p = k.ToString(CultureInfo.InvariantCulture);
            if (state.Strings.TryGetValue(PartialSid + p, out var sid))
                production.RestorePartial(m, sid, state.Floats.GetValueOrDefault(PartialVal + p));
        }

        int count = (int)state.Floats.GetValueOrDefault("num crafts");
        for (int k = 0; k < count; k++)
        {
            string n = k.ToString(CultureInfo.InvariantCulture);
            if (!state.Strings.TryGetValue("current" + n, out var item)) continue;

            string? model = state.Strings.GetValueOrDefault("currentExtraData" + n) is { Length: > 0 } x ? x : null;
            byte critical = (byte)Math.Clamp(state.Ints.GetValueOrDefault("crit" + n, 2), 0, 2);
            production.RestoreCraft(m, item, model, state.Floats.GetValueOrDefault("current prog" + n), critical);
        }
    }

    /// <summary>
    /// Reads a machine's production, power, battery, input and farm keys from its building state (a missing key leaves the default).
    /// </summary>
    static void ReadKeys(FcsRecord state, Machine m)
    {
        if (state.Ints.TryGetValue("meitou machine status", out int status)) m.Status = (byte)Math.Clamp(status, 0, byte.MaxValue);
        if (state.Floats.TryGetValue("resource mult", out float y)) m.Yield = y;

        m.PowerOn = state.Bools.GetValueOrDefault("power on", true);
        m.GetsBattery = state.Bools.GetValueOrDefault("gets battery", true);
        m.Broken = state.Bools.GetValueOrDefault("broken");
        if (m.Data.IsBattery) m.Charge = Math.Max(0, state.Floats.GetValueOrDefault("batterycharge"));

        ReadProduction(state, m);
    }

    /// <summary>
    /// Reads a machine's production keys from its building state (<see cref="WriteProduction"/>; a missing key leaves the default, a farm without
    /// <c>plant growth</c> keeps its plants).
    /// </summary>
    internal static void ReadProduction(FcsRecord state, Machine m)
    {
        if (state.Floats.TryGetValue("resource mult", out float y)) m.Yield = y;
        if (m.Output is { } o) o.Progress = state.Floats.GetValueOrDefault("production amount");
        for (int k = 0; k < m.Inputs.Length; k++)
            m.Inputs[k].Level = state.Floats.GetValueOrDefault("consumption amount-" + k.ToString(CultureInfo.InvariantCulture));

        if (m.Data.IsFurnace) m.BurnLevel = state.Floats.GetValueOrDefault("consumption amount-0");
        if (m.Farm is not { } f || !state.Floats.ContainsKey("plant growth")) return;

        f.Growth = state.Floats.GetValueOrDefault("plant growth");
        f.Death = state.Floats.GetValueOrDefault("plant death");
        f.Start = state.Floats.GetValueOrDefault("plant start");
        f.Harvested = Math.Clamp(state.Ints.GetValueOrDefault("plants harvested"), 0, f.Plants.Length);
        if (state.Floats.TryGetValue("save time", out float saved)) f.LiveHours = saved;
        for (int i = 0; i < f.Plants.Length; i++) f.Plants[i] = i < f.Harvested ? -10 : f.Growth;
    }


    internal sealed class Restoration(SaveGame source, ItemFactory factory)
    {
        public SaveGame Source { get; } = source;
        readonly Dictionary<string, string> _consumed = new(StringComparer.Ordinal);
        readonly LegacyTownMachines _legacy = new(source);
        readonly HashSet<string> _importedLegacy = new(StringComparer.Ordinal);
        internal void RequireNativeCapture(string site)
        {
            if (_importedLegacy.Contains(site)) throw new InvalidOperationException("Capturing a migrated town machine requires its native zone state; supply the world's level data.");
        }
        internal void Invalidate(string key) { _consumed.TryAdd(key, key); _legacy.Invalidate(key); }
        internal void WritePending(FcsRecord camera, World world) => _legacy.WritePending(camera, world.Fixtures.ResetBuildings);
        internal IEnumerable<string> Retired(World world) => _consumed.Where(p => world.Machines.Find(p.Value) is null).Select(p => p.Key);

        internal void Apply(World world, ProductionSystem production, BuildSite site, Machine machine)
        {
            if (IsInteriorMachine(site))
            {
                if (!_consumed.TryAdd(site.Id, site.Id) || InteriorParent(Source, site) is not { } parent) return;
                string? stateId = parent.State.Strings.GetValueOrDefault(InteriorPrefix + site.Id);
                var state = parent.Zone.File.Records.FirstOrDefault(r => r.StringId == stateId && r.RecordType == FcsRecordType.GamestateBuilding);
                if (state is null || state.Strings.GetValueOrDefault("meitou machine site") != site.Id
                    || state.Strings.GetValueOrDefault("meitou machine record") != site.Record) return;
                RestoreSite(parent.Zone, InteriorReferences(state), machine, production, factory, SharedStorage(world, site, machine));
                world.Machines.Touch();
                return;
            }
            string rawId = site.LevelPlacement?.InstanceId ?? site.Id;
            string key = BuildingKey.Of(site.Zone, rawId);
            if (!_consumed.TryAdd(key, site.Id)) return;
            var row = _legacy.Take(key);
            Source.Zones.TryGetValue(site.Zone, out var zone);
            var instance = zone?.BuildingList?.Instances.FirstOrDefault(i => i.Id == rawId);
            if (instance is { IsRemoved: true }) return;
            if (instance is not null && instance.Target != site.Record)
                throw new InvalidDataException("A saved machine's building record differs from the restored site.");
            bool restoreNative = instance is not null && zone is not null
                && (site.LevelPlacement is null || world.Fixtures.Saved?.ContainsKey(key) == true);
            // Preserve the original native reader even for a state with only a lock/broken flag.
            if (restoreNative) RestoreSite(zone!, instance!, machine, production, factory);
            // Payload detection only decides precedence over the obsolete CAMERA row, never whether native fields may load.
            bool nativePayload = restoreNative && HasNativeMachineState(zone!, instance!);
            bool importLegacy = row is not null && !nativePayload && site.LevelPlacement is not null && site.Id == key;
            if (importLegacy)
            {
                row!.Apply(machine);
                _importedLegacy.Add(site.Id);
            }
            if (restoreNative || importLegacy) world.Machines.Touch();
        }
    }


    /// <summary>
    /// Bind a checkpoint once. Late-created machines use the same restoration path as those loaded immediately.
    /// </summary>
    internal static void Bind(SaveGame save, World world, ItemFactory factory)
    {
        if (world.SavedMachines is { } existing)
        {
            if (!ReferenceEquals(existing.Source, save)) throw new InvalidOperationException("A world's machine source cannot be replaced.");
            return;
        }
        world.SavedMachines = new Restoration(save, factory);
        world.Fixtures.Saved ??= HeldSave.BuildingStates(save);
    }


    static void ClearResetMachines(SaveGame game, World world)
    {
        var reset = world.Fixtures.ResetBuildings.ToHashSet(StringComparer.Ordinal);
        if (world.SavedMachines is { } restored) reset.UnionWith(restored.Retired(world));
        if (reset.Count == 0) return;
        ClearInteriorMachines(game, reset);
        foreach (var zone in game.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;
            foreach (var instance in list.Instances)
            {
                if (!reset.Contains(BuildingKey.Of(zone.Coordinate, instance.Id))) continue;
                foreach (var id in instance.States.ToArray())
                {
                    var record = zone.File.Records.FirstOrDefault(r => r.StringId == id);
                    if (record?.RecordType == FcsRecordType.InventoryState)
                    {
                        SaveInventory.Replace(zone.File, record, [], n => $"{n}-zone{zone.Coordinate.X}.{zone.Coordinate.Y}-INGAME");
                        zone.File.Records.Remove(record);
                        instance.States.Remove(id);
                    }
                    else if (record?.RecordType == FcsRecordType.GamestateBuilding)
                    {
                        foreach (var key in record.Floats.Keys.Where(k => k is "resource mult" or "production amount" or "batterycharge" or "plant growth" or "plant death" or "plant start" or "save time" or "num crafts"
                            || Numbered(k, "consumption amount-") || Numbered(k, "current prog")).ToArray()) record.Floats.Remove(key);
                        foreach (var key in new[] { "power on", "gets battery", "broken", "repeat", "meitou machine state" }) record.Bools.Remove(key);
                        foreach (var key in record.Ints.Keys.Where(k => k is "plants harvested" or "meitou machine status" || Numbered(k, "crit")).ToArray()) record.Ints.Remove(key);
                        foreach (var key in record.Strings.Keys.Where(k => Numbered(k, "current") || Numbered(k, "currentExtraData")).ToArray()) record.Strings.Remove(key);
                    }
                }
            }
        }
        static bool Numbered(string key, string prefix) => key.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }


    /// <summary>
    /// A replaced parent layout or retired machine discards its saved furniture state and inventory through the same reset as placed machines.
    /// </summary>
    static void ClearInteriorMachines(SaveGame game, HashSet<string> reset)
    {
        foreach (var zone in game.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;
            foreach (var instance in list.Instances)
            {
                bool parentReset = reset.Contains(BuildingKey.Of(zone.Coordinate, instance.Id));
                var parent = instance.States.Select(id => zone.File.Records.FirstOrDefault(r => r.StringId == id
                    && r.RecordType == FcsRecordType.GamestateBuilding)).FirstOrDefault(r => r is not null);
                if (parent is null) continue;
                foreach (var reference in parent.Strings.Where(p => p.Key.StartsWith(InteriorPrefix, StringComparison.Ordinal)).ToArray())
                {
                    if (!parentReset && !reset.Contains(reference.Key[InteriorPrefix.Length..])) continue;
                    var state = zone.File.Records.FirstOrDefault(r => r.StringId == reference.Value && r.RecordType == FcsRecordType.GamestateBuilding);
                    if (state?.Strings.GetValueOrDefault(InteriorInventory) is { } inventoryId
                        && zone.File.Records.FirstOrDefault(r => r.StringId == inventoryId && r.RecordType == FcsRecordType.InventoryState) is { } inventory)
                    {
                        SaveInventory.Replace(zone.File, inventory, [], n => $"{n}-zone{zone.Coordinate.X}.{zone.Coordinate.Y}-INGAME");
                        zone.File.Records.Remove(inventory);
                    }
                    if (state is not null) zone.File.Records.Remove(state);
                    parent.Strings.Remove(reference.Key);
                }
            }
        }
    }

    static void RestoreSite(SaveZone zone, FcsInstance instance, Machine m, ProductionSystem production, ItemFactory factory, bool sharedStorage = false)
    {
        var state = instance.States.Select(id => zone.File.Records.FirstOrDefault(r => r.StringId == id &&
            r.RecordType == FcsRecordType.GamestateBuilding)).FirstOrDefault(r => r is not null);
        if (state is not null)
        {
            ReadKeys(state, m);
            if (m.Craft is not null) ReadCraft(state, m, production);
        }
        var inventory = instance.States.Select(id => zone.File.Records.FirstOrDefault(r => r.StringId == id && r.RecordType == FcsRecordType.InventoryState)).FirstOrDefault(r => r is not null);
        if (inventory is null) return;
        bool fromZero = !inventory.Bools.GetValueOrDefault(OriginalSectionsKey)
            && (inventory.StringId == instance.Id + InventorySuffix || state?.Bools.GetValueOrDefault("meitou machine state") == true);
        PutItems(m, SaveInventory.Read(zone.File, inventory), factory, fromZero, includeStorage: !sharedStorage);
    }

}
