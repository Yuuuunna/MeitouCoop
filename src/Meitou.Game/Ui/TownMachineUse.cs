using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Building;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Game.Ui;

/// <summary>
/// Picks level machines and their interior workbenches, then sends their canonical site and authoritative revision through the ordinary production
/// commands. Inspecting or taking their goods still goes through the server's ownership, distance and container-session checks.
/// </summary>
sealed class TownMachineUse(PlayerInterface player)
{
    /// <summary>
    /// A picked machine: its selected-building description, immutable functionality and distance along the view ray.
    /// </summary>
    internal sealed record MachineHit(BuildingSelection.SelectedBuilding Selection, MachineData Data, float T);

    /// <summary>
    /// The nearest standing level machine or interior workbench before the ground and visible floor.
    /// </summary>
    internal MachineHit? PickAt(Vector2 pixel)
    {
        if (player.Ray(pixel) is not { } ray || player.Build.Services is not { } host) return null;

        float limit = player.Picking.GroundHit(ray.Origin, ray.Direction) is { } ground ? Vector3.Distance(ray.Origin, ground) : 6000;
        MachineHit? best = null;
        foreach (var candidate in host.Placement.StaticAlong(ray.Origin, ray.Direction, limit))
        {
            var b = candidate.Placement;
            if (player.LevelEditorPanel.Services?.IsCleared(b.Key) == true) continue;
            var info = host.WorldInfo(b);
            if (info.Destroyed) continue;

            foreach (var piece in info.Pieces ?? [])
            {
                if (host.Machine(piece.RecordId) is not { Operable: true } data) continue;
                if (player.BuildingSelection.PieceRayT(ray, b.Key, piece, best?.T ?? limit) is not { } t) continue;

                var selection = new BuildingSelection.SelectedBuilding(host.ItemName(piece.RecordId), null, info.Owner, info.Town,
                    piece.Id, piece.Id, piece.Position, null, info.PublicDay, info.PublicNight, piece.RecordId);
                best = new(selection, data, t);
            }

            if (!info.Machine || host.Machine(b.BuildingId) is not { Operable: true } machine) continue;
            float y = b.WorldY ?? player.Ground(b.Position.X, b.Position.Z) + b.Position.Y;
            var hull = candidate.Footprint.Place(b.Position, b.Rotation);
            float? hit = candidate.Plot
                ? WorldPicking.PlotT(ray, hull, limit < 6000 ? limit : null)
                : player.Picking.BuildingRayT(ray, hull, candidate.Footprint.YMin, candidate.Footprint.YMax, b.BuildingId, b.Position, y, b.Rotation);
            if (hit is not { } distance || distance >= (best?.T ?? limit) || player.Hidden(ray, distance)) continue;

            var building = new BuildingSelection.SelectedBuilding(info.Name, info.Type, info.Owner, info.Town, b.Key, b.Key,
                new Vector3(b.Position.X, y, b.Position.Z), hull, info.PublicDay, info.PublicNight, b.BuildingId);
            best = new(building, machine, distance);
        }

        return best;
    }

    /// <summary>
    /// A quick click operates the target; holding opens its use and inventory verbs.
    /// </summary>
    internal bool Press(Vector2 pixel)
    {
        if (player.SelectedIds().Count == 0 || PickAt(pixel) is not { } hit) return false;

        ContextMenuWindow.ContextMenu? Menu(Vector2 at) => player.ContextMenuWindow.Menu(hit.Selection.Name, at, Verbs(hit));
        if (!player.ContextMenuWindow.ContextPressOn(pixel, Menu, () => Use(hit))) Use(hit);
        return true;
    }

    /// <summary>
    /// The pointer over a machine: the mine cursor for a mining function, the use cursor for other work.
    /// </summary>
    internal string? Pointer(Vector2 pixel) => PickAt(pixel) is { } hit
        ? hit.Data.Function is MachineData.MineFunction or MachineData.NaturalMineFunction ? "mine" : "use"
        : null;

    /// <summary>
    /// Use or auto-haul, followed by the machine's inventory window.
    /// </summary>
    List<(string Caption, Action Give)> Verbs(MachineHit hit) =>
    [
        (hit.Data.IsAutomatic ? "Auto-Haul" : "Use", () => Use(hit)),
        ("Loot", () => player.Build.OpenMachineWindow(hit.Selection.SiteId!, hit.Selection.Position, hit.Selection)),
    ];

    /// <summary>
    /// Submits only the revision supplied by the authority; no client-provided record or position creates a work target.
    /// </summary>
    void Use(MachineHit hit)
    {
        string site = hit.Selection.SiteId!;
        ulong revision = player.Production.WorkRevisionOf(site);
        var task = hit.Data.IsAutomatic ? TaskType.OperateAutomaticMachinery : TaskType.OperateMachinery;
        player.Say(player.SlowModifier
            ? new AddJob(player.SelectedIds(), task, site) { SiteRevision = revision, Tick = player.Session.CommandTick }
            : new OperateOrder(player.SelectedIds(), site) { SiteRevision = revision, Tick = player.Session.CommandTick });
    }
}
