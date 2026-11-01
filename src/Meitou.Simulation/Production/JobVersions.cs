using System.Runtime.CompilerServices;

namespace Meitou.Simulation.Production;

internal sealed class JobVersions
{
    sealed class Stamp
    {
        public PermaJob[] Jobs = [];
        public ulong Revision;
    }
    readonly ConditionalWeakTable<CharacterCold, Stamp> stamps = new();
    ulong revision;

    public ulong Read(CharacterCold character)
    {
        var stamp = stamps.GetOrCreateValue(character);
        if (stamp.Revision == 0 || stamp.Jobs.Length != character.Jobs.Count
            || stamp.Jobs.Where((job, i) => !ReferenceEquals(job, character.Jobs[i])).Any())
        {
            stamp.Jobs = character.Jobs.ToArray();
            stamp.Revision = revision = checked(revision + 1);
        }
        return stamp.Revision;
    }
}
