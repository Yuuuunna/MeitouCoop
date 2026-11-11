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
    PlacementFailure Check(World world, PlayerId? actor, int faction, PlannedBuilding plan);
}
