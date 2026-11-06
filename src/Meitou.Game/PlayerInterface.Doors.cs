using System.Numerics;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>
/// Doors under the pointer (docs/game/ui-input.md "Building hover and click"): a quick right click sends the nearest selected character to open a closed door or close an open one;
/// another faction's locked door takes Alt to pick its lock (with Shift held, else its menu opens on the press). Pointers: <c>door</c>, <c>lock</c> over the player's own locked door, <c>lockpick</c> over another faction's.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>How much of the way to the leaf a building's own collision may take before the door counts as hidden (the leaf sits in its building's wall).</summary>
    const float DoorPickSlack = 10;

    /// <summary>The door's collision triangles by its BUILDING record and index, in that building's frame with the leaf closed (the host sets it; null: no door is picked).</summary>
    public Func<string, int, IReadOnlyList<(Vector3[] Vertices, int[] Indices)>>? DoorShape { get; set; }

    /// <summary>
    /// The door whose leaf's collision the ray meets nearest, where the leaf has slid to, if any and not hidden behind another wall; locked or not (the original's pick hits a door's
    /// collision, group 5, docs/game/ui-input.md "Building picking and selection"). One without collision cannot be picked. A broken door counts only when it is the player's (it
    /// takes the repair order).
    /// </summary>
    (DoorPick Door, bool Locked)? PickDoor(Vector2 pixel) => PickDoorAt(pixel)?.Pick;

    /// <summary><see cref="PickDoor"/> with the ray distance to the hit.</summary>
    ((DoorPick Door, bool Locked) Pick, float T)? PickDoorAt(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray || DoorShape is not { } shapeOf) return null;
        var snapshot = session.CurrentSnapshot;
        ((DoorPick, bool), float)? best = null;
        float bestT = float.MaxValue;
        foreach (var door in snapshot.DoorPicks)
        {
            // A door missing from the published table is closed and unlocked. The leaf is where it has slid to (linear in the open amount: exact at closed and open).
            var state = snapshot.Doors.GetValueOrDefault((door.Building, door.Door));
            if (state.Broken && door.Owner != session.ActiveFaction) continue;
            var mesh = shapeOf(door.HostRecord, door.Door);
            if (mesh.Count == 0) continue;
            var at = door.Origin + door.Slide * state.Open;
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, door.Yaw);
            if (RayNears(ray, mesh, at, turn) && RayMesh(ray, mesh, at, turn) is { } t && t < bestT && !Hidden(ray, t - DoorPickSlack))
                (best, bestT) = (((door, state.Locked), t), t);
        }
        return best;
    }

    /// <summary>Whether the door is broken (its progress ran out, <see cref="Meitou.Simulation.Locks.Fixture.DoorBroken"/>): the player's own takes the repair order (95).</summary>
    bool DoorBroken(DoorPick door) => session.CurrentSnapshot.Doors.GetValueOrDefault((door.Building, door.Door)).Broken;

    /// <summary>The pointer over a door: <c>spanner</c> for a broken one (<c>FUN_1407fc6e0</c>), <c>lock</c> for the player's own locked one, <c>lockpick</c> for another faction's, else <c>door</c>.</summary>
    string DoorPointer((DoorPick Door, bool Locked) pick) => DoorBroken(pick.Door) ? "spanner" : !pick.Locked ? "door" : pick.Door.Owner == session.ActiveFaction ? "lock" : "lockpick";

    /// <summary>
    /// A right press on a door (true, with a selection): another faction's locked door opens its context menu (<see cref="DoorVerbs"/>) at once unless Shift is held (<c>FUN_140800250</c>,
    /// <b>Verified</b>); any other door, released quickly, gives the door order, where Alt picks another faction's locked lock, held, the menu (titled "Door": the door's own name is not
    /// published, engine choice).
    /// </summary>
    bool DoorClick(Vector2 p, bool alt)
    {
        if (SelectedIds().Count == 0 || PickDoor(p) is not { } pick) return false;
        if (!pressShift && pick.Locked && pick.Door.Owner != session.ActiveFaction && Menu(Loc.Tr("Door"), p, DoorVerbs(pick)) is { } now) { contextMenu = now; return true; }
        void Quick() => Say(DoorBroken(pick.Door) ? new Meitou.Simulation.Construction.BuildOrder([], pick.Door.Id) { SiteRevision = pick.Door.Revision, Tick = session.CommandTick } : new DoorOrder(SelectedIds().ToArray(), pick.Door.Id, alt) { Tick = session.CommandTick, ExpectedFixtureRevision = pick.Door.Revision });
        if (!ContextPressOn(p, at => Menu(Loc.Tr("Door"), at, DoorVerbs(pick)), Quick)) Quick();
        return true;
    }
}
