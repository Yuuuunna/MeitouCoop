namespace Meitou.Simulation;

/// <summary>
/// The source of a command's faction revision, shared by authority and client binding.
/// </summary>
public enum CommandFactionSource { None, Explicit, Character, ConstructionRoot, Container, WorkSite }
public readonly record struct CommandFactionScope(CommandFactionSource Source, int Faction = -1, CharacterId Character = default, string? Site = null);