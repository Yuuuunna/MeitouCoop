using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
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
    readonly ConditionalWeakTable<BuildSite, Stamp> _stamps = new();
    ulong _revision;
    public ulong Read(BuildSite site)
    {
        var stamp = _stamps.GetOrCreateValue(site);
        if (stamp.Revision == 0 || stamp.Owner != site.Owner || stamp.Record != site.Record || stamp.Complete != site.Complete
            || stamp.Destroyed != site.Destroyed || stamp.Upgrading != site.Upgrading || stamp.Paused != site.Paused || stamp.Public != site.Public
            || stamp.Parent != site.Parent || stamp.WallParent != site.WallParent)
        {
            stamp.Owner = site.Owner; stamp.Record = site.Record; stamp.Complete = site.Complete; stamp.Destroyed = site.Destroyed; stamp.Upgrading = site.Upgrading;
            stamp.Paused = site.Paused; stamp.Public = site.Public; stamp.Parent = site.Parent; stamp.WallParent = site.WallParent;
            stamp.Revision = _revision = checked(_revision + 1);
        }
        return stamp.Revision;
    }
}
