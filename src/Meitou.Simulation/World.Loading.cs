using System.Numerics;
using Meitou.Simulation.Population;

namespace Meitou.Simulation;

/// <summary>
/// The world's loading step: the nearby population prepared without a tick.
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// Prepares nearby population at a frame boundary and publishes it without running AI, movement, combat or game time.
    /// </summary>
    public bool PreparePopulation(Vector3 cameraFocus)
    {
        RequireNoDurableMutation();
        if (Characters.Phase != TablePhase.Outside)
            throw new InvalidOperationException("population loading requires a tick boundary");

        var population = _systems.OfType<PopulationSystem>().FirstOrDefault();
        if (population is null) return true;

        bool ready;
        Characters.BeginTick();
        Characters.Phase = TablePhase.SlowWorld;
        try
        {
            ready = population.PrepareLoading(this, cameraFocus);
            Fixtures.Bind(this);
            foreach (var carrying in _systems.OfType<Carrying.CarrySystem>()) carrying.RestoreLinks(this);
        }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }

        if (Settings.PublishSnapshots)
        {
            Publish();
            KeepPrevious(Snapshot, _shownBuffer);
        }

        return ready;
    }
    internal void NoteStoredRolesRestored() => StateRevision = checked(StateRevision + 1);

    public Saving.StoredPlayerRoles? StoredPlayerRoles { get; internal set; }

}