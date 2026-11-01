using System.Runtime.CompilerServices;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

/// <summary>Inventory-affecting machine state, independent of production animation/progress and simulation tick.</summary>
internal sealed class MachineVersions
{
    readonly record struct SlotState(object Instance, string Item, int Count);
    sealed class Stamp
    {
        public SlotState[] Slots = [];
        public readonly Inventory Crafted = new();
        public ulong StorageRevision, CraftedRevision, Revision;
        public bool Closed;
        public CraftJob[] Queue = [];
        public ulong ControlRevision;
        public bool Repeat, PowerOn, GetsBattery, Burning;
    }
    readonly ConditionalWeakTable<Machine, Stamp> stamps = new();
    ulong revision;

    public ulong ReadControl(Machine machine)
    {
        var stamp = stamps.GetOrCreateValue(machine);
        CraftJob[] queue = machine.Craft?.Queue.ToArray() ?? [];
        bool repeat = machine.Craft?.Repeat ?? false;
        if (stamp.ControlRevision == 0 || stamp.Queue.Length != queue.Length || stamp.Queue.Where((job, i) => !ReferenceEquals(job, queue[i])).Any()
            || stamp.Repeat != repeat || stamp.PowerOn != machine.PowerOn || stamp.GetsBattery != machine.GetsBattery || stamp.Burning != machine.Burning)
        {
            stamp.Queue = queue; stamp.Repeat = repeat; stamp.PowerOn = machine.PowerOn; stamp.GetsBattery = machine.GetsBattery; stamp.Burning = machine.Burning;
            stamp.ControlRevision = revision = checked(revision + 1);
        }
        return stamp.ControlRevision;
    }

    public ulong Read(World world, Machine machine)
    {
        var stamp = stamps.GetOrCreateValue(machine);
        var slots = machine.Inputs.Select(i => new SlotState(i, i.Item, i.Count)).ToList();
        if (machine.Output is { } output) slots.Add(new(output, output.Recipe.Item, output.Count));
        ulong storage = machine.Storage is { } grid ? world.InventoryRevision(grid) : 0;
        stamp.Crafted.Items.Clear();
        if (machine.Craft is { } craft) stamp.Crafted.Items.AddRange(craft.Done);
        ulong crafted = world.InventoryRevision(stamp.Crafted);
        if (stamp.Revision == 0 || !stamp.Slots.SequenceEqual(slots) || stamp.StorageRevision != storage || stamp.CraftedRevision != crafted || stamp.Closed != machine.Closed)
        {
            stamp.Slots = slots.ToArray(); stamp.StorageRevision = storage; stamp.CraftedRevision = crafted; stamp.Closed = machine.Closed;
            stamp.Revision = revision = checked(revision + 1);
        }
        return stamp.Revision;
    }
}
