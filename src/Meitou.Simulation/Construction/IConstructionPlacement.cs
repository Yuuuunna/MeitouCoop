using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Construction;

/// <summary>CPU geometry/resource validation at the world input boundary, shared by graphical and dedicated hosts.</summary>
public interface IConstructionPlacement
{
    /// <summary>
    /// Checks one ordinary plan against the current authoritative world.
    /// </summary>
    PlacementFailure Check(World world, PlayerId? actor, int faction, PlannedBuilding plan);

    /// <summary>
    /// Creates command-local geometry context for wall lines and previously accepted queued ghosts.
    /// </summary>
    IConstructionPlacementBatch? BeginBatch(World world, PlayerId? actor, int faction, PlaceBuildings command) => null;
}

/// <summary>
/// A placement command's geometry context, retaining only sites the authority accepted from that command.
/// </summary>
public interface IConstructionPlacementBatch
{
    /// <summary>
    /// Checks the plan at its original index.
    /// </summary>
    PlacementFailure Check(int index);

    /// <summary>
    /// Records a successfully created live site; a rejected plan never becomes a snap or collision exemption.
    /// </summary>
    void Accepted(int index, BuildSite site);
}
