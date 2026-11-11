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
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Meitou.Simulation.Production;

internal sealed class JobVersions
{
    sealed class Stamp
    {
        public PermaJob[] Jobs = [];
        public ulong Revision;
    }
    readonly ConditionalWeakTable<CharacterCold, Stamp> _stamps = new();
    ulong _revision;

    public ulong Read(CharacterCold character)
    {
        var stamp = _stamps.GetOrCreateValue(character);
        if (stamp.Revision == 0 || stamp.Jobs.Length != character.Jobs.Count
            || stamp.Jobs.Where((job, i) => !ReferenceEquals(job, character.Jobs[i])).Any())
        {
            stamp.Jobs = character.Jobs.ToArray();
            stamp.Revision = _revision = checked(_revision + 1);
        }
        return stamp.Revision;
    }
}
