using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Immutable inputs identified by a stable token; the builder runs off the serialized world owner.
/// </summary>
public sealed record GateRegionBuild(object Identity, Func<CancellationToken, IGateRegions> Build);