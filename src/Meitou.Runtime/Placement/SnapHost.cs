using Meitou.Data.Gameplay.Construction;

namespace Meitou.Runtime.Placement;

/// <summary>
/// The building that carries a snap node (<c>is node</c> instance such as <c>Node Light Mount</c>): its placement id, the parent instance id a
/// placed attachment records, whether it is a level building, and the ownership flags <see cref="PlacementRules.MayBuildIn"/> reads.
/// </summary>
public readonly record struct SnapHost(string Id, string Parent, bool IsLevel, bool Owned, bool Destroyed, bool OwnerNotReal);
