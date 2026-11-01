using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Construction;

/// <summary>CPU geometry/resource validation at the world input boundary, shared by graphical and dedicated hosts.</summary>
public interface IConstructionPlacement
{
    PlacementFailure Check(World world, PlayerId? actor, int faction, PlannedBuilding plan);
}
