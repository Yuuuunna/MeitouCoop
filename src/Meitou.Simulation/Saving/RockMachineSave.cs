using System.Globalization;
using Meitou.Data.Fcs;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The machines of worked rocks on the CAMERA record (docs/formats/save.md "Rock machines"): a rock has no building state in the zone files, so
/// its machine is Meitou-only keys: <c>meitou rock&lt;i&gt;</c> (the rock's natural site id), bool <c>meitou rock&lt;i&gt; own</c> (the player's
/// faction has claimed it), int <c>meitou rock&lt;i&gt; town</c> (its player town's number, absent: none) and the status, output and inputs.
/// Multiplayer ownership comes from <see cref="NaturalClaimsSave"/>'s stable faction IDs; the bool remains a legacy single-player fallback.
/// The original ignores these keys (they are read by name).
/// </summary>
public static class RockMachineSave
{
    /// <summary>
    /// The prefix of every rock machine key.
    /// </summary>
    const string Prefix = "meitou rock";

    /// <summary>
    /// The key of entry <paramref name="i"/>.
    /// </summary>
    static string Key(int i) => Prefix + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the rocks' machines over the camera record's entries (the old ones removed first).
    /// </summary>
    public static void Write(FcsRecord camera, World world, ProductionSystem production)
    {
        Clear(camera);

        int n = 0;
        foreach (var m in world.Machines.All)
        {
            if (!ProductionSystem.IsNatural(m.Site)) continue;

            var (owner, town) = production.RockClaim(m.Site);
            camera.Strings[Key(n)] = m.Site;
            if (owner >= 0 && owner == world.Player.Faction) camera.Bools[Key(n) + " own"] = true;
            if (town != 0) camera.Ints[Key(n) + " town"] = town;
            WriteState(camera, Key(n), m);
            n++;
        }
    }

    /// <summary>
    /// Makes the saved rock machines before foliage placement. A stable multiplayer claim overrides the legacy player-faction bool.
    /// </summary>
    public static void Read(FcsRecord camera, World world, ProductionSystem production)
    {
        if (!camera.Strings.ContainsKey(Key(0))) return;
        for (int i = 0; camera.Strings.TryGetValue(Key(i), out var id); i++)
        {
            int owner = camera.Bools.GetValueOrDefault(Key(i) + " own") ? world.Player.Faction : -1;
            int town = camera.Ints.GetValueOrDefault(Key(i) + " town");
            if (production.RestoreRock(world, id, owner, town) is not { } m) continue;
            ReadState(camera, Key(i), m);
        }

        world.Machines.Touch();
    }

    /// <summary>
    /// Removes the entries of the keys <see cref="Prefix"/>&lt;i&gt; (each with its parts) from the camera record.
    /// </summary>
    internal static void Clear(FcsRecord camera)
    {
        for (int i = 0; camera.Strings.ContainsKey(Prefix + i.ToString(CultureInfo.InvariantCulture)); i++)
        {
            string entry = Prefix + i.ToString(CultureInfo.InvariantCulture);
            var keys = camera.Strings.Keys.Concat(camera.Floats.Keys).Concat(camera.Ints.Keys).Concat(camera.Bools.Keys)
                .Where(k => k.StartsWith(entry, StringComparison.Ordinal) && (k.Length == entry.Length || k[entry.Length] == ' '))
                .ToList();
            foreach (var k in keys)
            {
                camera.Strings.Remove(k);
                camera.Floats.Remove(k);
                camera.Ints.Remove(k);
                camera.Bools.Remove(k);
            }
        }
    }

    /// <summary>
    /// Writes a machine's status, output and inputs as the parts of the entry <paramref name="entry"/>.
    /// </summary>
    static void WriteState(FcsRecord camera, string entry, Machine m)
    {
        camera.Ints[entry + " st"] = m.Status;
        if (m.Output is { } o)
        {
            camera.Floats[entry + " out"] = o.Progress;
            camera.Ints[entry + " outn"] = o.Count;
        }

        for (int k = 0; k < m.Inputs.Length; k++)
        {
            string part = k.ToString(CultureInfo.InvariantCulture);
            camera.Ints[entry + " in" + part] = m.Inputs[k].Count;
            camera.Floats[entry + " lv" + part] = m.Inputs[k].Level;
        }
    }

    /// <summary>
    /// Reads a machine's status, output and inputs from the parts of the entry <paramref name="entry"/> (counts clamped to the slots' capacities).
    /// </summary>
    static void ReadState(FcsRecord camera, string entry, Machine m)
    {
        m.Status = (byte)camera.Ints.GetValueOrDefault(entry + " st", m.Status);
        if (m.Output is { } o)
        {
            o.Progress = camera.Floats.GetValueOrDefault(entry + " out");
            o.Count = Math.Clamp(camera.Ints.GetValueOrDefault(entry + " outn"), 0, o.Recipe.Capacity);
        }

        for (int k = 0; k < m.Inputs.Length; k++)
        {
            string part = k.ToString(CultureInfo.InvariantCulture);
            m.Inputs[k].Count = Math.Clamp(camera.Ints.GetValueOrDefault(entry + " in" + part), 0, m.Inputs[k].Recipe.Capacity);
            m.Inputs[k].Level = camera.Floats.GetValueOrDefault(entry + " lv" + part);
        }
    }
}
