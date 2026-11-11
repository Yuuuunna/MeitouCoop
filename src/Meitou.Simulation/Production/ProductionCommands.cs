using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Population;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Simulation.Player;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

/// <summary>Sends characters (the selection when empty) to work a complete player building: operate a well, harvest a farm, research at a bench.</summary>
public sealed record OperateOrder(IReadOnlyList<CharacterId> Characters, string Site) : SiteWorkCommand;

public abstract record SiteWorkCommand : OrderCommand
{
    public ulong SiteRevision { get; init; }
}

/// <summary>
/// Gives characters (the selection when empty) the job an order of <paramref name="Type"/> on a player building becomes, at the end of their job lists (the original's Shift with an order;
/// <see cref="PermaJobs.Add"/>).
/// </summary>
public sealed record AddJob(IReadOnlyList<CharacterId> Characters, Meitou.Data.Gameplay.Ai.TaskType Type, string Site) : SiteWorkCommand { public ulong RootRevision { get; init; } }

/// <summary>
/// Gives characters (the selection when empty) the job an order of <paramref name="Ordered"/> on a character becomes, untargeted, at the end of their job lists (Shift with First Aid,
/// Splint Injuries, an attack or a loot; <see cref="PermaJobs.Add"/>): 25 → 58 Medic (60 → 57 Robotics, which a first aid on a robot <paramref name="Target"/> is), 249 → 250 Splint
/// Rigging, 4 / 5 / 61 → 16 Attacking enemies, 26 → 238 Foraging animals.
/// </summary>
public sealed record AddOrderJob(IReadOnlyList<CharacterId> Characters, Meitou.Data.Gameplay.Ai.TaskType Ordered, CharacterId Target) : SimCommand;

/// <summary>
/// The original's order 146 USE_TURRET on a complete turret of the player's (function 12): the nearest of the characters (the selection when empty) that can act operates it, standing at
/// its gunner spot (<see cref="Ai.AiSystem.TurretSpot"/>); the combat system fires it (docs/simulation.md "Production", "Combat").
/// </summary>
public sealed record UseTurretOrder(IReadOnlyList<CharacterId> Characters, string Site) : SiteWorkCommand;

/// <summary>
/// The original's order 136 FEED_CORPSE_INTO_MACHINE without Shift (Corpse Disposal on a corpse machine): each of the characters carrying a dead body takes it to an incinerator as the
/// corpse disposal job does. Condition 99 is taken as carrying a dead body; the runtime binds the explicitly selected site and rechecks its authority until arrival.
/// </summary>
public sealed record DisposeCorpseOrder(IReadOnlyList<CharacterId> Characters, string Site) : SiteWorkCommand
{
    /// <summary>Expected carried bodies, in the same order as Characters. Required for attributed requests.</summary>
    public IReadOnlyList<CharacterId> Bodies { get; init; } = [];
}

/// <summary>Takes the job at <paramref name="Index"/> off a character's job list (the list's X button).</summary>
public sealed record RemoveJob(CharacterId Character, int Index) : JobListCommand;

/// <summary>Moves a character's job from <paramref name="From"/> to <paramref name="To"/> in its list (the list's rows are dragged to reorder, the original's tutorial).</summary>
public sealed record MoveJob(CharacterId Character, int From, int To) : JobListCommand;

public abstract record JobListCommand : SimCommand
{
    public ulong JobsRevision { get; init; }
}

/// <summary>
/// A character puts an item of its own into a machine: <paramref name="Slot"/> k into recipe input k (as many units as the input still takes), -1 into the storage grid at
/// (<paramref name="X"/>, <paramref name="Y"/>), or with X below 0 where it first fits (stacks of it first, <see cref="Inventory.Add"/>). <paramref name="Count"/> above 0 moves at most that
/// many units (a right click moves one), 0 the whole stack. The character must stand within <see cref="ProductionSettings.TransferReach"/> of the building.
/// </summary>
public sealed record PutInMachine(CharacterId Character, InventoryItemToken Item, string Site, int Slot, int X = 0, int Y = 0, int Count = 0) : StorageTransferCommand;

/// <summary>Both the character inventory and the displayed storage state must still match; containers also require their open-session id.</summary>
public abstract record StorageTransferCommand : InventoryCommand
{
    public ulong StorageRevision { get; init; }
    public ulong ContainerSessionId { get; init; }
}

/// <summary>
/// A character takes from a machine: <paramref name="Slot"/> -2 the output's items, k the items of input k, -1 the storage item at <paramref name="Index"/>; into its inventory where they first
/// fit (a storage item as the right click's move, <see cref="InventoryCommands.MoveInto"/>: its open bag first with <see cref="BagFirst"/>). <paramref name="Count"/> above 0 takes at most that
/// many units, 0 all. Dragging goes through the held item (<see cref="LiftItem"/>).
/// </summary>
public sealed record TakeFromMachine(CharacterId Character, string Site, int Slot, int Index = 0, int Count = 0) : StorageTransferCommand
{
    /// <summary>
    /// A storage item goes into the character's open bag first (the right click's move).
    /// </summary>
    public bool BagFirst { get; init; }
}

/// <summary>Queues a research (paying its cost from the research benches when it was never paid).</summary>
public sealed record QueueResearch(string Research) : FactionCommand;

/// <summary>Takes a research off the queue; its progress is kept for when it is queued again.</summary>
public sealed record CancelResearch(string Research) : FactionCommand;

/// <summary>Queues a craft at a crafting bench: an option's key (<see cref="Meitou.Data.Gameplay.Construction.CraftOption.Key"/>), <paramref name="Count"/> times.</summary>
public sealed record QueueCraft(string Site, string Option, int Count = 1) : MachineControlCommand;

/// <summary>Changes the displayed machine configuration or craft queue, independently of production progress.</summary>
public abstract record MachineControlCommand : SimCommand
{
    public ulong ControlRevision { get; init; }
}

/// <summary>Takes the craft at <paramref name="Index"/> off a bench's queue (its progress is lost, as the original).</summary>
public sealed record CancelCraft(string Site, int Index) : MachineControlCommand;

/// <summary>Sets a bench's repeat flag: a finished craft goes back to the end of the queue.</summary>
public sealed record RepeatCrafts(string Site, bool Repeat) : MachineControlCommand;

/// <summary>A machine's Power toggle (<c>power on</c>) or, for a consumer, its Battery toggle (<c>gets battery</c>); null leaves one as it is.</summary>
public sealed record SetMachinePower(string Site, bool? PowerOn = null, bool? GetsBattery = null) : MachineControlCommand;

/// <summary>An item furnace's Active button: starts burning its grid, or stops it (<c>FurnaceBuilding::incinerate</c>).</summary>
public sealed record IncinerateFurnace(string Site) : MachineControlCommand;
