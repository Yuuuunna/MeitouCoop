using System.Runtime.CompilerServices;

namespace Meitou.Simulation.Construction;

/// <summary>Instance/ownership/shape changes invalidate displayed work targets; construction progress does not.</summary>
internal sealed class SiteVersions
{
    sealed class Stamp
    {
        public ulong Revision;
        public int Owner;
        public string Record = "";
        public bool Complete, Destroyed, Upgrading;
        public bool Paused, Public;
        public string? Parent, WallParent;
    }
    readonly ConditionalWeakTable<BuildSite, Stamp> stamps = new();
    ulong revision;
    public ulong Read(BuildSite site)
    {
        var stamp = stamps.GetOrCreateValue(site);
        if (stamp.Revision == 0 || stamp.Owner != site.Owner || stamp.Record != site.Record || stamp.Complete != site.Complete
            || stamp.Destroyed != site.Destroyed || stamp.Upgrading != site.Upgrading || stamp.Paused != site.Paused || stamp.Public != site.Public
            || stamp.Parent != site.Parent || stamp.WallParent != site.WallParent)
        {
            stamp.Owner = site.Owner; stamp.Record = site.Record; stamp.Complete = site.Complete; stamp.Destroyed = site.Destroyed; stamp.Upgrading = site.Upgrading;
            stamp.Paused = site.Paused; stamp.Public = site.Public; stamp.Parent = site.Parent; stamp.WallParent = site.WallParent;
            stamp.Revision = revision = checked(revision + 1);
        }
        return stamp.Revision;
    }
}
