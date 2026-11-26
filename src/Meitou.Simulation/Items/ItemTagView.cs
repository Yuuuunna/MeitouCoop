namespace Meitou.Simulation.Items;

/// <summary>
/// One exact UTF-16 mod tag visible to this recipient, shared by every item carrying the same digest.
/// </summary>
public sealed record ItemTagView(string Digest, string Value);
