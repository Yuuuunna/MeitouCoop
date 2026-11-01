using Meitou.Simulation;

namespace Meitou.Engine;

/// <summary>Rendering inputs, independent of who advances the authoritative world.</summary>
public interface IWorldPresentation
{
    WorldSnapshot CurrentSnapshot { get; }
    WorldSnapshot PreviousSnapshot { get; }
    float SimulationAlpha { get; }
}
