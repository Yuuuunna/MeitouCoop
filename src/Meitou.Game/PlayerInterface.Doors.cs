using System.Numerics;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>
/// Doors under the pointer (docs/game/ui-input.md "Building hover and click"): a quick right click sends the nearest selected character to open a closed door or close an open one;
/// another faction's locked door takes Alt to pick its lock. Pointers: <c>door</c>, <c>lock</c> over the player's own locked door, <c>lockpick</c> over another faction's.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>How near the pointer a door leaf's projected centre must be, in pixels (an engine choice: the doors are picked by their centre, not their collision).</summary>
    const float DoorPickPixels = 40;
    /// <summary>How much of the way to the leaf a building's own collision may take before the door counts as hidden (the leaf sits in its building's wall).</summary>
    const float DoorPickSlack = 10;

    /// <summary>The unbroken door whose leaf centre projects nearest the pixel within <see cref="DoorPickPixels"/>, if any, and is not hidden behind another wall; locked or not.</summary>
    (DoorPick Door, bool Locked)? PickDoor(Vector2 pixel) => PickDoorAt(pixel)?.Pick;

    /// <summary><see cref="PickDoor"/> with the ray distance to the leaf's centre.</summary>
    ((DoorPick Door, bool Locked) Pick, float T)? PickDoorAt(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray) return null;
        var snapshot = session.CurrentSnapshot;
        ((DoorPick, bool), float)? best = null;
        float bestDistance = DoorPickPixels;
        foreach (var door in snapshot.DoorPicks)
        {
            // A door missing from the published table is closed and unlocked. The leaf is where it has slid to (linear in the open amount: exact at closed and open).
            var state = snapshot.Doors.GetValueOrDefault((door.Building, door.Door));
            var leaf = door.Position + door.Slide * state.Open;
            if (Project(leaf) is not { } at) continue;
            float d = Vector2.Distance(at, pixel);
            if (d >= bestDistance) continue;
            float t = Vector3.Distance(ray.Origin, leaf);
            if (state.Broken || Hidden(ray, t - DoorPickSlack)) continue;
            (best, bestDistance) = (((door, state.Locked), t), d);
        }
        return best;
    }

    /// <summary>The pointer over a door: <c>lock</c> for the player's own locked one, <c>lockpick</c> for another faction's, else <c>door</c>.</summary>
    string DoorPointer((DoorPick Door, bool Locked) pick) => !pick.Locked ? "door" : pick.Door.Owner == session.ActiveFaction ? "lock" : "lockpick";

    /// <summary>
    /// A right press on a door (true, with a selection): released quickly, the door order, where Alt picks another faction's locked lock; held, its context menu (<see cref="DoorVerbs"/>,
    /// titled "Door": the door's own name is not published, engine choice).
    /// </summary>
    bool DoorClick(Vector2 p, bool alt)
    {
        if (SelectedIds().Count == 0 || PickDoor(p) is not { } pick) return false;
        void Quick() => Say(new DoorOrder(SelectedIds().ToArray(), pick.Door.Id, alt) { Tick = session.CommandTick, ExpectedFixtureRevision = pick.Door.Revision });
        if (!ContextPressOn(p, at => Menu(Loc.Tr("Door"), at, DoorVerbs(pick)), Quick)) Quick();
        return true;
    }
}
