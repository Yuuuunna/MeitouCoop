using System.Numerics;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>
/// Containers of a building's interior (docs/game/locks.md "Containers"): a right click on one sends the nearest selected character to open it (pointer <c>search</c>); its inventory
/// then shows in the storage window the research bench uses (<c>PlayerInterface.Production.cs</c>), beside the character's.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>Whether a place is above the floor shown in a building the player is in, where nothing is drawn (the host sets it; null: never).</summary>
    public Func<Vector3, bool>? AboveViewedFloor { get; set; }

    ContainerSpot? PickContainer(Vector2 pixel) => PickContainerAt(pixel)?.Container;

    /// <summary>
    /// The container under the pixel with the ray distance, when not hidden behind a wall: the nearest the ray meets on its record's collision (hull prism, then triangles), as a building is
    /// picked (docs/game/ui-input.md "Building picking and selection"); one without collision, or above the shown floor of the building the player is in, cannot be picked. The
    /// collision is read on workers; until then it is not picked.
    /// </summary>
    (ContainerSpot Container, float T)? PickContainerAt(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray || buildHost?.Placement is not { } placement) return null;
        var containers = session.CurrentSnapshot.Containers;
        (ContainerSpot, float)? hit = null;
        float bestT = float.MaxValue;
        foreach (var c in containers)
        {
            if (placement.Footprint(c.Record) is not { } footprint) continue;
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, c.Yaw);
            if (BuildingRayT(ray, footprint.Place(c.Position, yaw), footprint.YMin, footprint.YMax, c.Record, c.Position, c.Position.Y, yaw) is { } t && t < bestT && AboveViewedFloor?.Invoke(c.Position) != true && !Hidden(ray, t))
                (hit, bestT) = ((c, t), t);
        }
        return hit;
    }

    /// <summary>
    /// A right press on a container (true, with a selection): released quickly, the open order, where Alt picks another faction's locked one (the sim gives the hint without it); held,
    /// its context menu (<see cref="ContainerVerbs"/>).
    /// </summary>
    bool ContainerClick(Vector2 p, bool alt)
    {
        if (SelectedIds().Count == 0 || PickContainer(p) is not { } container) return false;
        string name = buildHost?.ItemName(container.Record) ?? container.Record;
        if (!ContextPressOn(p, at => Menu(name, at, ContainerVerbs(container)), () => OpenContainer(container.Id, alt))) OpenContainer(container.Id, alt);
        return true;
    }

    /// <summary>
    /// The pointer over a container (docs/game/ui-input.md "Building hover and click", StorageBuilding): <c>search</c> for the player's own or an unowned one, else <c>lockpick</c> while
    /// locked and <c>scavenge</c> when not.
    /// </summary>
    string ContainerPointer(ContainerSpot container) =>
        container.Owner < 0 || container.Owner == session.ActiveFaction ? "search" : container.Locked ? "lockpick" : "scavenge";
}
