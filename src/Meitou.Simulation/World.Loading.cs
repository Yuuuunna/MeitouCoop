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

    /// <summary>
    /// One frame of the loading pause at a tick boundary (<see cref="LoadingPause"/>), with the camera focus last sent (a queued
    /// <see cref="FocusCommand"/>, else the population's): whether the world is held after it, given whether it was and whether it is paused
    /// otherwise, and whether the camera's load is under way (<see cref="PopulationSystem.CameraLoading"/>). Explicit-authority worlds never use
    /// a client camera to pause or activate the shared world. Without a population or a focus, neither.
    /// </summary>
    public (bool Held, bool Loading) StepLoadingPause(bool held, bool paused)
    {
        if (Players.RequiresExplicitControl) return (false, false);
        RequireNoDurableMutation();
        if (Characters.Phase != TablePhase.Outside)
            throw new InvalidOperationException("the loading pause requires a tick boundary");

        var population = _systems.OfType<PopulationSystem>().FirstOrDefault();
        if (population is null || (Commands.LastDue<FocusCommand>(Tick)?.Position ?? population.Focus) is not { } focus) return (false, false);

        held = LoadingPause.Step(this, population, focus, held, paused);
        return (held, population.CameraLoading);
    }
}