using System.Globalization;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

public static partial class MachineSave
{
    static bool HasNativeMachineState(SaveZone zone, FcsInstance instance) => instance.States.Any(id =>
        zone.File.Records.Any(record => record.StringId == id && (record.RecordType == FcsRecordType.InventoryState
            || record.RecordType == FcsRecordType.GamestateBuilding && (record.Bools.GetValueOrDefault("meitou machine state")
                || record.Bools.Keys.Any(key => key is "power on" or "gets battery" or "repeat")
                || record.Floats.Keys.Any(key => key is "resource mult" or "production amount" or "batterycharge" or "plant growth" or "num crafts"
                    || key.StartsWith("consumption amount-", StringComparison.Ordinal))))));

    // Only source DTOs awaiting the ordinary Ensure/Restoration path; there is no second machine registry or writer.
    sealed class LegacyTownMachines
    {
        const string Prefix = "meitou tm";
        readonly SortedDictionary<string, Row> _pending = new(StringComparer.Ordinal);

        internal LegacyTownMachines(SaveGame source)
        {
            var camera = source.Camera.Record;
            foreach (var pair in camera.Strings)
            {
                if (!Parts(pair.Key, out int index, out string suffix) || suffix.Length != 0) continue;
                if (pair.Key != Prefix + index.ToString(CultureInfo.InvariantCulture)) throw new InvalidDataException("Noncanonical legacy town machine row index.");
                string key = pair.Value;
                if (string.IsNullOrEmpty(key)) throw new InvalidDataException("A legacy town machine has no building identity.");
                int colon = key.IndexOf(':');
                var coordinates = colon > 0 ? key[..colon].Split('.') : [];
                if (coordinates.Length != 2 || !int.TryParse(coordinates[0], NumberStyles.None, CultureInfo.InvariantCulture, out int x)
                    || !int.TryParse(coordinates[1], NumberStyles.None, CultureInfo.InvariantCulture, out int y)
                    || !new ZoneCoordinate(x, y).IsInsideGrid || colon == key.Length - 1 || BuildingKey.Of(new(x, y), key[(colon + 1)..]) != key)
                    throw new InvalidDataException("A legacy town machine has an invalid canonical building identity.");
                if (source.Zones.TryGetValue(new ZoneCoordinate(x, y), out var zone)
                    && zone.BuildingList?.Instances.FirstOrDefault(instance => instance.Id == key[(colon + 1)..]) is { IsRemoved: true }) continue;
                var row = new Row(key);
                foreach (var item in camera.Ints)
                    if (Parts(item.Key, out int n, out string part) && n == index) row.Ints.Add(part, item.Value);
                foreach (var item in camera.Floats)
                    if (Parts(item.Key, out int n, out string part) && n == index)
                    {
                        if (!float.IsFinite(item.Value)) throw new InvalidDataException("A legacy town machine has non-finite progress.");
                        row.Floats.Add(part, item.Value);
                    }
                if (row.Ints.TryGetValue(" st", out int status) && (status < 0 || status > byte.MaxValue))
                    throw new InvalidDataException("A legacy town machine has an invalid status.");
                if (!_pending.TryAdd(key, row)) throw new InvalidDataException("Duplicate legacy town machine identity.");
            }
        }

        internal Row? Take(string key) => _pending.Remove(key, out var row) ? row : null;
        internal void Invalidate(string key) => _pending.Remove(key);

        internal void WritePending(FcsRecord camera, IEnumerable<string> invalidated)
        {
            foreach (string key in invalidated) _pending.Remove(key);
            foreach (string key in camera.Strings.Keys.Where(key => Parts(key, out _, out _)).ToArray()) camera.Strings.Remove(key);
            foreach (string key in camera.Ints.Keys.Where(key => Parts(key, out _, out _)).ToArray()) camera.Ints.Remove(key);
            foreach (string key in camera.Floats.Keys.Where(key => Parts(key, out _, out _)).ToArray()) camera.Floats.Remove(key);
            int index = 0;
            foreach (var row in _pending.Values)
            {
                string key = Prefix + (index++).ToString(CultureInfo.InvariantCulture);
                camera.Strings[key] = row.Key;
                foreach (var pair in row.Ints) camera.Ints[key + pair.Key] = pair.Value;
                foreach (var pair in row.Floats) camera.Floats[key + pair.Key] = pair.Value;
            }
        }

        static bool Parts(string key, out int index, out string suffix)
        {
            index = 0; suffix = "";
            if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            int end = Prefix.Length;
            while (end < key.Length && char.IsAsciiDigit(key[end])) end++;
            if (end == Prefix.Length || end < key.Length && key[end] != ' '
                || !int.TryParse(key.AsSpan(Prefix.Length, end - Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index)) return false;
            suffix = key[end..];
            return true;
        }

        internal sealed class Row(string key)
        {
            internal string Key { get; } = key;
            internal Dictionary<string, int> Ints { get; } = new(StringComparer.Ordinal);
            internal Dictionary<string, float> Floats { get; } = new(StringComparer.Ordinal);
            internal void Apply(Machine machine)
            {
                if (Ints.TryGetValue(" st", out int status)) machine.Status = (byte)status;
                if (machine.Output is { } output)
                {
                    output.Progress = Floats.GetValueOrDefault(" out");
                    output.Count = Math.Clamp(Ints.GetValueOrDefault(" outn"), 0, output.Recipe.Capacity);
                }
                for (int i = 0; i < machine.Inputs.Length; i++)
                {
                    string n = i.ToString(CultureInfo.InvariantCulture);
                    machine.Inputs[i].Count = Math.Clamp(Ints.GetValueOrDefault(" in" + n), 0, machine.Inputs[i].Recipe.Capacity);
                    machine.Inputs[i].Level = Floats.GetValueOrDefault(" lv" + n);
                }
            }
        }
    }
}