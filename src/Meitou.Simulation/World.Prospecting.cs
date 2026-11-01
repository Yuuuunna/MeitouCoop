using Meitou.Simulation.Production;

namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>The local player's last completed survey. Remote players have independent PlayerState reports.</summary>
    public ProspectReport? Prospected { get => Player.Prospected; internal set => Player.Prospected = value; }
}
