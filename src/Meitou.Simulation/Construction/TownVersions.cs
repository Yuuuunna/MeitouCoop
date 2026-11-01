using System.Runtime.CompilerServices;

namespace Meitou.Simulation.Construction;

internal sealed class TownVersions
{
    sealed class Stamp
    {
        public ulong Revision;
        public string Name = "";
        public bool Public;
    }
    readonly ConditionalWeakTable<PlayerTown, Stamp> stamps = new();
    ulong revision;
    public ulong Read(PlayerTown town)
    {
        var stamp = stamps.GetOrCreateValue(town);
        if (stamp.Revision == 0 || stamp.Name != town.Name || stamp.Public != town.Public)
        {
            stamp.Name = town.Name; stamp.Public = town.Public; stamp.Revision = revision = checked(revision + 1);
        }
        return stamp.Revision;
    }
}
