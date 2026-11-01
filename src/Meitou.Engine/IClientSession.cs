using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Simulation;

namespace Meitou.Engine;

/// <summary>The graphical host's control/presentation loop. Authoritative simulation is deliberately absent.</summary>
public interface IClientSession : IPlayerSession, IWorldPresentation
{
    new WorldSnapshot CurrentSnapshot { get; }
    FixedStepClock Ticks { get; }
    float Alpha { get; }
    bool IsPaused { get; }
    double PresentationTick { get; }
    double PresentationSeconds { get; }
    event Action<ActionState>? Ticked;
    int Advance(double realSeconds);
    ActionState Tick();
    CameraState CameraAt();
}
