using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Ui;

namespace Meitou.Game;

/// <summary>
/// Loose items under the pointer (docs/simulation.md "Ground items"): a right click on one sends the nearest selected character to pick it up (pointer <c>pick_item</c>).
/// While the Highlight key is held each one near the focus shows its name label (docs/game/ui-input.md "Ground item labels"), which picks like the item.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>How near the pointer an item's projected middle must be, in pixels (an engine choice: the items have no pick mesh).</summary>
    const float GroundItemPickPixels = 24;
    public Func<int, bool>? GroundItemHidden { get; set; }

    /// <summary>The camera's focus while the Highlight key is held (the host sets it; null otherwise): the item labels show round it.</summary>
    public Vector3? HighlightFocus;

    /// <summary>The labels drawn last frame, front to back, for <see cref="PickGroundItem"/>.</summary>
    readonly List<(int Id, UiRect Rect)> itemLabels = [];

    /// <summary>
    /// The ground item whose label is under the pixel, else whose middle (half a unit above where it lies) projects nearest the pixel within
    /// <see cref="GroundItemPickPixels"/>, if any; one inside or behind a building seen from outside does not count.
    /// </summary>
    int? PickGroundItem(Vector2 pixel) => PickGroundItemAt(pixel)?.Id;

    /// <summary><see cref="PickGroundItem"/> with the ray distance to the item's middle (0 for a label: it is drawn over everything).</summary>
    (int Id, float T)? PickGroundItemAt(Vector2 pixel)
    {
        if (HighlightFocus is not null)
            foreach (var (id, rect) in itemLabels)
                if (rect.Contains(pixel.X, pixel.Y)) return (id, 0);
        if (Ray(pixel) is not { } ray) return null;
        (int, float)? best = null;
        float bestDistance = GroundItemPickPixels;
        foreach (var g in session.CurrentSnapshot.GroundItems)
        {
            if (GroundItemHidden?.Invoke(g.Id) == true) continue;
            var middle = g.Position + new Vector3(0, 0.5f, 0);
            if (Project(middle) is not { } at) continue;
            float d = Vector2.Distance(at, pixel), t = Vector3.Distance(ray.Origin, middle);
            if (d < bestDistance && !Hidden(ray, t)) (best, bestDistance) = ((g.Id, t), d);
        }
        return best;
    }

    /// <summary>
    /// A right click on a ground item, with a selection (true): the pick-up order, or held its menu with the one verb, "Steal" for another faction's item else "Pick Up"
    /// (<c>FUN_14079b2d0</c>; docs/game/ui-input.md "Ground item labels").
    /// </summary>
    bool GroundItemClick(Vector2 p)
    {
        if (SelectedIds().Count == 0 || PickGroundItem(p) is not { } item) return false;
        void Quick() => Say(new Meitou.Simulation.Items.PickUpItemOrder([], item) { Tick = session.CommandTick });
        if (session.CurrentSnapshot.GroundItems.FirstOrDefault(g => g.Id == item) is not { Name: { } name } g
            || !ContextPressOn(p, at => Menu(name, at, [(Stealing(g) ? "Steal" : "Pick Up", Quick)]), Quick)) Quick();
        return true;
    }

    /// <summary>
    /// The name label over each ground item within <see cref="HighlightRadius"/> of the focus while the Highlight key is held: the original's <c>Kenshi_ItemDropLabel</c>
    /// (<b>Verified</b>), a <c>Kenshi_GenericTextBoxFlatSmallSkin</c> 0.0259 of the screen high and fitted to the name, centred in Kenshi_StandardFont_Medium; plate and text
    /// in the item's <see cref="ItemLabelColour"/>, both green (<c>...Selected</c>, #66ff66) for the one the pointer picks, the plate red for a stolen one. Labels that would overlap the nearer
    /// one's are lifted above it, and hidden ones are skipped (engine choices).
    /// </summary>
    void AddItemLabels(List<UiControl> controls)
    {
        itemLabels.Clear();
        if (HighlightFocus is not { } focus || uiPainter is not { } painter || EditorOpen) return;
        float scale = Height / 1080f, height = Height * 0.0259259f, em = 20 * 50f / 72f * scale;
        var near = new List<(Meitou.Simulation.Items.GroundItemSnapshot Item, float Distance, Vector2 At)>();
        foreach (var g in session.CurrentSnapshot.GroundItems)
        {
            if (GroundItemHidden?.Invoke(g.Id) == true) continue;
            if (g.Name.Length == 0 || Vector3.DistanceSquared(focus, g.Position) > HighlightRadius * HighlightRadius) continue;
            var middle = g.Position + new Vector3(0, 0.5f, 0);
            if (Project(middle) is not { } at || Ray(at) is not { } ray) continue;
            float distance = Vector3.Distance(ray.Origin, middle);
            if (!Hidden(ray, distance)) near.Add((g, distance, at));
        }
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var placed = new List<(int Id, UiRect Rect)>();
        foreach (var (g, _, at) in near)
        {
            float width = painter.MeasureStandard(g.Name, em) + em;
            var rect = new UiRect(at.X - width / 2, at.Y - 6 * scale - height, width, height);
            for (bool moved = true; moved;)
            {
                moved = false;
                foreach (var (_, other) in placed)
                    if (rect.Intersect(other) is { Width: > 0, Height: > 0 }) { rect = rect with { Y = other.Y - height - 1 }; moved = true; }
            }
            placed.Add((g.Id, rect));
        }
        itemLabels.AddRange(placed);
        int? hovered = PickGroundItem(mouse);
        // Drawn far to near so the nearer labels lie on top.
        for (int i = placed.Count - 1; i >= 0; i--)
        {
            var (id, rect) = placed[i];
            bool stealing = Stealing(near[i].Item);
            string item = ItemLabelColour(near[i].Item), selected = stealing ? "1 0.4 0.4" : "0.4 1 0.4";
            string back = stealing ? "1 0.4 0.4" : id == hovered ? selected : item, text = id == hovered ? selected : item;
            controls.Add(new($"itemlabel-{i}", "Widget", rect, "", "", "Kenshi_GenericTextBoxFlatSmallSkin", true,
                new Dictionary<string, string> { ["Colour"] = back }, Vector4.Zero, Pickable: false));
            controls.Add(new($"itemlabel-text-{i}", "TextBox", rect, near[i].Item.Name, "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Center", ["TextColour"] = text }, Vector4.Zero, Pickable: false, TextScale: 1));
        }
    }

    /// <summary>Picking the item up would be stealing it: its owner's faction (<see cref="Meitou.Simulation.Items.GroundItem.OwnedBy"/>) is not the player's.</summary>
    bool Stealing(Meitou.Simulation.Items.GroundItemSnapshot g) => g.OwnedBy >= 0 && g.OwnedBy != session.ActiveFaction;

    /// <summary>Whether an ITEM record is an artifact (its <c>artifact</c> field); the host sets it.</summary>
    public Func<string, bool>? ArtifactOf { get; set; }

    /// <summary>
    /// A label's own colour (docs/game/ui-input.md "Ground item labels"): a plain ITEM's <c>ItemArtifact</c> or <c>ItemStandard</c>; a weapon, armour, backpack, nest item, map item,
    /// crossbow or limb its <c>ItemGrade</c> by level (armour by its quality) below 16/36/51/66/81 (grades 4 and 5 share the yellow); anything else <c>DropItemLabelBackDefault</c>.
    /// </summary>
    string ItemLabelColour(Meitou.Simulation.Items.GroundItemSnapshot g)
    {
        int level = g.Type == FcsRecordType.ARMOUR ? (int)g.Quality : g.Level;
        return g.Type switch
        {
            FcsRecordType.ITEM => TipHex(ArtifactOf?.Invoke(g.Record) == true ? "0dfceb" : "e0cccc"),
            FcsRecordType.WEAPON or FcsRecordType.ARMOUR or FcsRecordType.CONTAINER or FcsRecordType.NEST_ITEM or FcsRecordType.MAP_ITEM or FcsRecordType.CROSSBOW
                or FcsRecordType.LIMB_REPLACEMENT => TipHex(level < 16 ? "666666" : level < 36 ? "d9ccbf" : level < 51 ? "4099b3" : level < 66 ? "66fc80" : "fceb0d"),
            _ => "1 1 1",
        };
    }
}
