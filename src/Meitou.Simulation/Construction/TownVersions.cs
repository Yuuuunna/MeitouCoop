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

internal sealed class TownVersions
{
    sealed class Stamp
    {
        public ulong Revision;
        public string Name = "";
        public bool Public;
    }
    readonly ConditionalWeakTable<PlayerTown, Stamp> _stamps = new();
    ulong _revision;
    public ulong Read(PlayerTown town)
    {
        var stamp = _stamps.GetOrCreateValue(town);
        if (stamp.Revision == 0 || stamp.Name != town.Name || stamp.Public != town.Public)
        {
            stamp.Name = town.Name; stamp.Public = town.Public; stamp.Revision = _revision = checked(_revision + 1);
        }
        return stamp.Revision;
    }
}
