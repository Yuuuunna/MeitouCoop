using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Simulation.Session;
using Meitou.Simulation;

namespace Meitou.Game.Host;

/// <summary>
/// The graphical host's control/presentation loop. Authoritative simulation is deliberately absent.
/// </summary>
public interface IClientSession : IPlayerSession, IWorldPresentation
{
    new WorldSnapshot CurrentSnapshot { get; }

    FixedStepClock Ticks { get; }

    float Alpha { get; }
    new bool IsPaused { get; }

    double PresentationTick { get; }

    double PresentationSeconds { get; }

    event Action<ActionState>? Ticked;
    int Advance(double realSeconds);
    ActionState Tick();
    CameraState CameraAt();
}