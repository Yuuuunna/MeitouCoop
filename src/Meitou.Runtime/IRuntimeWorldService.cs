using Meitou.Simulation;

namespace Meitou.Runtime;

/// <summary>
/// External world state owned by the active runtime. Candidate worlds cannot publish it during setup.
/// </summary>
public interface IRuntimeWorldService : ITickSystem
{
    void Synchronize(World world);
    /// <summary>
    /// Idempotent cleanup, including after a partially failed Synchronize or Suspend.
    /// </summary>
    void Suspend(World world);
}