using System.Buffers.Binary;
using Meitou.Data.Fcs;

namespace Meitou.Data.Save;
/// <summary>
/// One file of a save folder (<c>quick.save</c>, <c>*.platoon</c>, <c>*.zone</c>): an FCS file of type 15 followed by the slot lists
/// (docs/formats/save.md "Object handles and slot lists"). A list is an int32 count and that many ascending int32; <c>quick.save</c> has four, a zone
/// file one, a platoon file none. Strings are read losslessly (<see cref = "Fcs.LosslessUtf8"/>). The game writes every file as type 15, except that a
/// V17 header-carrying platoon file was seen in a quicksave folder (docs/formats/save.md), so any FCS file type is accepted. Reading and writing
/// again gives the same bytes.
/// </summary>
public sealed class SaveFile
{
    /// <summary>
    /// The FCS part: header and records.
    /// </summary>
    public FcsFile Data { get; init; } = new() { FileType = FcsFileType.V15 };

    /// <summary>
    /// The slot lists after the records, in file order.
    /// </summary>
    public List<int[]> SlotLists { get; } = [];

    /// <summary>
    /// The records of <see cref="Data"/>.
    /// </summary>
    public List<FcsRecord> Records => Data.Records;

    /// <summary>
    /// Reads the file at <paramref name="path"/>.
    /// </summary>
    public static SaveFile ReadFile(string path) => Read(File.ReadAllBytes(path));

    /// <summary>
    /// Reads a save file: its FCS part losslessly, then slot lists to the end; a list longer than the bytes left or stray bytes
    /// throw <see cref="FcsFormatException"/>.
    /// </summary>
    public static SaveFile Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var data = FcsReader.Read(stream, lossless: true);
        var file = new SaveFile { Data = data };
        long at = stream.Position;
        while (at < bytes.Length)
        {
            if (bytes.Length - at < 4) throw new FcsFormatException($"{bytes.Length - at} stray bytes after the records.", at);
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)at));
            if (count < 0 || (long)count * 4 > bytes.Length - at - 4)
                throw new FcsFormatException($"Slot list declares {count} entries but {bytes.Length - at - 4} bytes follow.", at);
            var list = new int[count];
            for (int i = 0; i < count; i++)
                list[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)at + 4 + 4 * i));
            file.SlotLists.Add(list);
            at += 4 + 4L * count;
        }
        return file;
    }

    /// <summary>
    /// Writes the FCS part (record sizes kept, strings lossless) and then each slot list as its count and entries.
    /// </summary>
    public void Write(Stream stream)
    {
        // Record sizes are written as held: the game's own files hold the child count there (docs/formats/save.md "byteSize").
        FcsWriter.Write(Data, stream, keepByteSizes: true, lossless: true);
        using var w = new BinaryWriter(stream, FcsReader.Encoding, leaveOpen: true);
        foreach (var list in SlotLists)
        {
            w.Write(list.Length);
            foreach (int v in list) w.Write(v);
        }
    }

    /// <summary>
    /// The file's bytes, as <see cref="Write"/> writes them.
    /// </summary>
    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        Write(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the file to <paramref name="path"/>.
    /// </summary>
    public void WriteFile(string path) => File.WriteAllBytes(path, ToBytes());
    /// <summary>Detached structural copy without a serialization/parse round trip.</summary>
    public SaveFile Clone()
    {
        var data = new FcsFile
        {
            FileType = Data.FileType,
            Version = Data.Version,
            Author = Data.Author,
            Description = Data.Description,
            NextId = Data.NextId,
            HeaderTail = Data.HeaderTail.ToArray(),
            Merge = Data.Merge is { } merge ? merge with
            {
                Merged = [..merge.Merged]
            }

            : null,
            DeleteRequests = Data.DeleteRequests?.Select(r => r with { Items = [..r.Items] }).ToList(),
        };
        data.Dependencies.AddRange(Data.Dependencies);
        data.References.AddRange(Data.References);
        foreach (var source in Records)
        {
            var record = new FcsRecord
            {
                ByteSize = source.ByteSize,
                Type = source.Type,
                Id = source.Id,
                Name = source.Name,
                StringId = source.StringId,
                Flags = source.Flags
            };
            Copy(source.Bools, record.Bools);
            Copy(source.Floats, record.Floats);
            Copy(source.Ints, record.Ints);
            Copy(source.Vector3S, record.Vector3S);
            Copy(source.Vector4S, record.Vector4S);
            Copy(source.Strings, record.Strings);
            Copy(source.Filenames, record.Filenames);
            foreach (var pair in source.References)
                record.References.Add(pair.Key, [..pair.Value]);
            foreach (var instance in source.Instances)
            {
                var clone = new FcsInstance
                {
                    Id = instance.Id,
                    Target = instance.Target,
                    Position = instance.Position,
                    Rotation = instance.Rotation
                };
                clone.States.AddRange(instance.States);
                record.Instances.Add(clone);
            }

            data.Records.Add(record);
        }

        var copy = new SaveFile
        {
            Data = data
        };
        copy.SlotLists.AddRange(SlotLists.Select(list => list.ToArray()));
        return copy;
        static void Copy<T>(OrderedDictionary<string, T> source, OrderedDictionary<string, T> target)
        {
            foreach (var pair in source)
                target.Add(pair.Key, pair.Value);
        }
    }
}
