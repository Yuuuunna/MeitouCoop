namespace Meitou.Simulation;

/// <summary>
/// Receives authorized manual orders on the serial simulation owner. Deferred AI and mod orders do not invoke this hook.
/// </summary>
public interface IPlayerOrderObserver : ITickSystem
{
    /// <summary>
    /// A native player order addressed to one controlled character. An observer may cancel its own conflicting automation immediately.
    /// </summary>
    void OnPlayerOrder(World world, PlayerId issuer, CharacterId character, SimCommand command);
}
