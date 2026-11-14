using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Data.Save;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation.Locks;

/// <summary>
/// A container's contents in a save (docs/game/locks.md "Containers"): Meitou's own GAMESTATE_BUILDING string <c>meitou container &lt;layout
/// index&gt;</c> (the index of <see cref="Fixture.Id"/>, whose interior ids the original's keys cannot name), a JSON list of the items with their
/// cell, quality, level, material, maker, charge, colour, turn and nested bag contents. Optional fields keep crafted and uniform labels, item
/// function, stolen faction, death and unique flags, shackle locks and prosthetic condition. Present only while the container holds something;
/// older lists without the optional fields retain their original defaults. A shared interior machine grid is saved here only once.
/// </summary>
static class ContainerSave
{
    /// <summary>
    /// The key prefix of a container's contents (followed by its layout index).
    /// </summary>
    public const string KeyPrefix = "meitou container ";

    /// <summary>
    /// One saved item, with optional metadata after the original fields. Theft names a stable faction record, never a runtime faction or character
    /// slot; the container format does not retain the robbed character's native handle.
    /// </summary>
    sealed record Entry(string R, int Q, int X, int Y, float Ql, int Lv, string M, string C, float Ch, string S, List<Entry>? I,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool U = false, string? Co = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool T = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? Crafter = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? Uniform = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int? ItemFunction = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? StealSource = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] SaveItemLock? Lock = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float Damage = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float Wear = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float Stun = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Death = false);

    /// <summary>
    /// The key of the container <paramref name="f"/> of <paramref name="building"/>, or null when it is not an interior item of it.
    /// </summary>
    public static string? KeyOf(Fixture f, string building) =>
        f.Id.Length > building.Length + 1 && f.Id[building.Length] == '/' && f.Id.StartsWith(building, StringComparison.Ordinal) ? KeyPrefix
                + f.Id[(building.Length + 1)..] : null;

    /// <summary>
    /// The contents as text; null when the grid is empty.
    /// </summary>
    public static string? Write(Inventory grid) => grid.Items.Count == 0 ? null : JsonSerializer.Serialize(Entries(grid));

    /// <summary>
    /// The saved entries of a grid's items, a bag's contents nested.
    /// </summary>
    static List<Entry> Entries(Inventory grid)
            => [.. grid.Items.Select(i
                    => new Entry(i.Record, i.Quantity, i.X, i.Y, i.Quality, i.Level, i.MaterialId, i.CompanyId, i.Charges, i.Section,
        i.Contents is { Items.Count: > 0 } bag ? Entries(bag) : null, i.Unique, i.ColourId.Length > 0 ? i.ColourId : null, i.Rotated,
        i.Crafter.Length > 0 ? i.Crafter : null, i.Uniform.Length > 0 ? i.Uniform : null, i.ItemFunction,
        (uint)i.StolenFrom < (uint)(SavedKit.Factions?.Count ?? 0) ? SavedKit.Factions![i.StolenFrom].Id : null,
        i.Lock is { } l ? new SaveItemLock(l.Level, l.EffectivelyLocked, l.Broken, l.Hardness) : null,
        i.LimbDamage, i.LimbWear, i.LimbStun, i.Death))];

    /// <summary>
    /// Puts the saved items into <paramref name="grid"/>; one whose cell is no longer free (a changed grid) goes to the first that is, dropped when
    /// none is or when the data lacks its record.
    /// </summary>
    public static void Read(string text, Inventory grid, ItemFactory factory, PopulationData? data = null)
    {
        List<Entry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<Entry>>(text);
        }
        catch (JsonException)
        {
            return;
        }

        if (entries is not null) Fill(entries, grid, factory, data, topLevel: true);
    }

    /// <summary>
    /// Puts saved entries into a grid (a bag's into the bag), each in its cell else the first free one; dropped when none is free or the data lacks
    /// its record.
    /// </summary>
    static void Fill(List<Entry> entries, Inventory grid, ItemFactory factory, PopulationData? data, bool topLevel)
    {
        foreach (var e in entries)
        {
            var saved = new SaveItem
            {
                BaseId = e.R,
                Quantity = e.Q,
                X = e.X,
                Y = e.Y,
                Quality = e.Ql,
                Level = e.Lv,
                MaterialId = e.M ?? "",
                CompanyId = e.C ?? "",
                Crafter = e.Crafter ?? "",
                Uniform = e.Uniform ?? "",
                Charges = e.Ch,
                ColourId = e.Co ?? "",
                Section = topLevel ? "main" : e.S ?? "main",
                Unique = e.U,
                Rotated = e.T,
                ItemFunction = e.ItemFunction ?? 0,
                StolenFrom = e.StealSource,
                Lock = e.Lock,
                Damage = e.Damage,
                Wear = e.Wear,
                Stun = e.Stun,
                Death = e.Death
            };
            if (SavedKit.ItemOf(saved, factory) is not { } item) continue;
            if (e.ItemFunction is { } function) item.ItemFunction = function;
            if (e.StealSource is { Length: > 0 } source && data is not null) item.StolenFrom = data.FactionIndex(source);
            if (e.I is { } inner && item.Contents is { } bag) Fill(inner, bag, factory, data, topLevel: false);
            if (topLevel && !grid.IsFree("main", item.X, item.Y, item.Width, item.Height))
            {
                grid.AddTo("main", item);
                continue;
            }

            grid.Items.Add(item);
        }
    }
}
