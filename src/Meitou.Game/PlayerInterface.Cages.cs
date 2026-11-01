using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>
/// Cages under the pointer (docs/game/ui-input.md "Context menu verbs", the cage rows; the cages are published as <see cref="WorldSnapshot.Cages"/>): a quick right click on an empty cage
/// picks a locked one's lock, puts the carried captive in a free one (108) or gets the character in (107); held, the cage's menu. Engine choice: the picking magnet and the "Cage" title,
/// as for beds and containers (a cage has no pick mesh here).
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>How near the pointer a cage's projected middle must be, in pixels (an engine choice, as for beds).</summary>
    const float CagePickPixels = 30;

    /// <summary>The cage whose middle (half a unit above its spot) projects nearest the pixel within <see cref="CagePickPixels"/>, if any and not hidden behind a wall, with the ray distance.</summary>
    (CageSpot Cage, float T)? PickCageAt(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray) return null;
        (CageSpot, float)? best = null;
        float bestDistance = CagePickPixels;
        foreach (var c in session.CurrentSnapshot.Cages)
        {
            var middle = c.Position + new Vector3(0, 0.5f, 0);
            if (Project(middle) is not { } at) continue;
            float d = Vector2.Distance(at, pixel), t = Vector3.Distance(ray.Origin, middle);
            if (d < bestDistance && !Hidden(ray, t)) (best, bestDistance) = ((c, t), d);
        }
        return best;
    }

    /// <summary>
    /// The verbs of a cage (the original's cage rows): a locked one Pick Lock (76), Use Tools (285), Use Strength (286); a free, unlocked one Put in (108) when a selected character carries
    /// someone, else Get in (107); an occupied one Set Free (110). The player's own cage has the same, its Lock / Unlock (77, 78) being a Pick Lock stand-in while locked (the simulation has
    /// no cage lock order, engine choice).
    /// </summary>
    List<(string Caption, Action Give)> CageVerbs(CageSpot cage)
    {
        var verbs = new List<(string, Action)>();
        var selection = SelectedIds();
        if (selection.Count == 0) return verbs;
        bool occupied = !cage.Occupant.IsNone;
        if (cage.Locked)
        {
            verbs.Add(("Pick Lock", () => Say(new PickLockOrder(selection, cage.Occupant) { Cage = occupied ? null : cage.Id, Tick = session.CommandTick })));
            verbs.Add(("Use Tools", () => Say(new ForceLockOrder([], cage.Id, LockForce.Tools) { Tick = session.CommandTick })));
            verbs.Add(("Use Strength", () => Say(new ForceLockOrder([], cage.Id, LockForce.Strength) { Tick = session.CommandTick })));
        }
        if (occupied) verbs.Add(("Set Free", () => Say(new SetFreeOrder([], cage.Occupant) { Tick = session.CommandTick })));
        else if (!cage.Locked)
        {
            var carriers = Carriers();
            if (carriers.Length > 0) verbs.Add(("Put in", () => Say(new Meitou.Simulation.Crime.PutInCageOrder(carriers) { Cage = cage.Id, Tick = session.CommandTick })));
            else verbs.Add(("Get in", () => Say(new GetInCageOrder([], cage.Id) { Tick = session.CommandTick })));
        }
        return verbs;
    }

    /// <summary>A right press on a cage (true, with a selection): released quickly, the first of the cage's verbs while it is empty; held, its menu.</summary>
    bool CageClick(Vector2 p)
    {
        if (SelectedIds().Count == 0 || PickCageAt(p)?.Cage is not { } cage) return false;
        var verbs = CageVerbs(cage);
        Action? quick = cage.Occupant.IsNone && verbs.Count > 0 ? verbs[0].Give : null;
        return ContextPressOn(p, at => Menu(Loc.Tr("Cage"), at, CageVerbs(cage)), quick);
    }
}
