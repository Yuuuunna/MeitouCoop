using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Crime;

namespace Meitou.Networking.VNext;

/// <summary>GameHours-anchored binary codecs for countdown fields (<see cref = "PrisonView"/> and <see cref = "BountyLine"/>)
/// so advancing <see cref = "PlayerWorldView.GameHours"/> updates remaining hours on replicas without per-tick re-transmission,
/// while preserving bit-exact IEEE754 values when <c>GameHours == BaseGameHours</c>.</summary>
internal static class GameHoursCountdownCodec
{
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    static GameHoursCountdownCodec()
    {
        string[] prisonFields = "Locked Sentence HoursLeft CageOwner CageId LockLevel".Split(' ');
        if (!prisonFields.Order().SequenceEqual(typeof(PrisonView).GetProperties().Select(p => p.Name).Order()))
            throw new InvalidOperationException("Prison wire schema coverage changed.");
        string[] bountyFields = "Faction Amount Crimes HoursLeft Local".Split(' ');
        if (!bountyFields.Order().SequenceEqual(typeof(BountyLine).GetProperties().Select(p => p.Name).Order()))
            throw new InvalidOperationException("Bounty wire schema coverage changed.");
    }

    public static byte[] EncodePrison(PrisonView? value, double baseGameHours)
    {
        if (value is not { } prison)
            return[0];
        if (!double.IsFinite(baseGameHours) || !float.IsFinite(prison.Sentence) || !double.IsFinite(prison.HoursLeft))
            throw new InvalidDataException("Invalid prison countdown values.");
        using var stream = new MemoryStream(64);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)(prison.Locked ? 1 : 0));
        writer.Write(prison.Sentence);
        writer.Write(baseGameHours);
        writer.Write(prison.HoursLeft);
        writer.Write(prison.CageOwner);
        writer.Write(prison.LockLevel);
        if (prison.CageId is null)
        {
            writer.Write((byte)0);
        }
        else
        {
            writer.Write((byte)1);
            WriteBoundedString(writer, prison.CageId, 1024);
        }

        return stream.ToArray();
    }

    public static (double BaseGameHours, PrisonView Value)? DecodePrison(byte[] bytes)
    {
        if (bytes.Length == 1 && bytes[0] == 0)
            return null;
        if (bytes.Length < 31 || bytes[0] != 1)
            throw new InvalidDataException("Invalid prison component.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        try
        {
            _ = reader.ReadByte();
            byte locked = reader.ReadByte();
            if (locked > 1)
                throw new InvalidDataException("Invalid prison lock flag.");
            float sentence = reader.ReadSingle();
            double baseGameHours = reader.ReadDouble();
            double hoursLeft = reader.ReadDouble();
            int cageOwner = reader.ReadInt32();
            int lockLevel = reader.ReadInt32();
            byte hasCage = reader.ReadByte();
            if (hasCage > 1 || !double.IsFinite(baseGameHours) || !float.IsFinite(sentence) || !double.IsFinite(hoursLeft))
                throw new InvalidDataException("Invalid prison component values.");
            string? cageId = hasCage == 1 ? ReadBoundedString(reader, 1024) : null;
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Trailing prison component bytes.");
            return (baseGameHours, new PrisonView(locked == 1, sentence, hoursLeft) { CageOwner = cageOwner, CageId = cageId, LockLevel = lockLevel, });
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Truncated prison component.", e);
        }
    }

    public static PrisonView? ProjectPrison((double BaseGameHours, PrisonView Value)? term, double currentGameHours)
    {
        if (term is not var (baseHours, prison))
            return null;
        double elapsed = currentGameHours == baseHours ? 0.0 : currentGameHours - baseHours;
        double hoursLeft = elapsed == 0.0 ? prison.HoursLeft : prison.HoursLeft - elapsed;
        if (!double.IsFinite(hoursLeft))
            throw new InvalidDataException("Non-finite projected prison hours.");
        return prison with
        {
            HoursLeft = hoursLeft
        };
    }

    public static byte[] EncodeBounties(IReadOnlyList<BountyLine>? value, double baseGameHours)
    {
        if (value is null)
            return[0];
        if (!double.IsFinite(baseGameHours) || value.Count > 4096)
            throw new InvalidDataException("Invalid bounty countdown collection.");
        if (value.Count == 0)
            return[1, 0];
        using var stream = new MemoryStream(32 + value.Count * 48);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write7BitEncodedInt(value.Count);
        writer.Write(baseGameHours);
        foreach (var line in value)
        {
            if (line.Faction is null || line.Crimes is null || !double.IsFinite(line.HoursLeft))
                throw new InvalidDataException("Invalid bounty line values.");
            WriteBoundedString(writer, line.Faction, 1024);
            writer.Write(line.Amount);
            WriteBoundedString(writer, line.Crimes, 4096);
            writer.Write(line.HoursLeft);
            writer.Write((byte)(line.Local ? 1 : 0));
        }

        return stream.ToArray();
    }

    public static (double BaseGameHours, BountyLine[]? Values) DecodeBounties(byte[] bytes)
    {
        if (bytes.Length == 1 && bytes[0] == 0)
            return (0.0, null);
        if (bytes.Length < 2 || bytes[0] != 1)
            throw new InvalidDataException("Invalid bounties component.");
        if (bytes.Length == 2 && bytes[1] == 0)
            return (0.0, []);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        try
        {
            _ = reader.ReadByte();
            int count = reader.Read7BitEncodedInt();
            if (count <= 0 || count > 4096)
                throw new InvalidDataException("Invalid bounty count.");
            double baseGameHours = reader.ReadDouble();
            if (!double.IsFinite(baseGameHours))
                throw new InvalidDataException("Invalid bounty clock.");
            var lines = new BountyLine[count];
            for (int i = 0; i < count; i++)
            {
                string faction = ReadBoundedString(reader, 1024);
                int amount = reader.ReadInt32();
                string crimes = ReadBoundedString(reader, 4096);
                double hoursLeft = reader.ReadDouble();
                byte local = reader.ReadByte();
                if (local > 1 || !double.IsFinite(hoursLeft))
                    throw new InvalidDataException("Invalid bounty line values.");
                lines[i] = new BountyLine(faction, amount, crimes, hoursLeft, local == 1);
            }

            if (stream.Position != stream.Length)
                throw new InvalidDataException("Trailing bounty component bytes.");
            return (baseGameHours, lines);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Truncated bounties component.", e);
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("Invalid bounties varint.", e);
        }
    }

    public static IReadOnlyList<BountyLine> ProjectBounties((double BaseGameHours, BountyLine[]? Values) terms, double currentGameHours)
    {
        if (terms.Values is null)
            return null!;
        if (terms.Values.Length == 0)
            return[];
        double elapsed = currentGameHours == terms.BaseGameHours ? 0.0 : currentGameHours - terms.BaseGameHours;
        if (elapsed == 0.0)
            return terms.Values;
        var projected = new BountyLine[terms.Values.Length];
        for (int i = 0; i < projected.Length; i++)
        {
            double hoursLeft = terms.Values[i].HoursLeft - elapsed;
            if (!double.IsFinite(hoursLeft))
                throw new InvalidDataException("Non-finite projected bounty hours.");
            projected[i] = terms.Values[i] with
            {
                HoursLeft = hoursLeft
            };
        }

        return projected;
    }

    static void WriteBoundedString(BinaryWriter writer, string value, int maxBytes)
    {
        int byteCount = StrictUtf8.GetByteCount(value);
        if (byteCount > maxBytes)
            throw new InvalidDataException("Component string exceeds wire bound.");
        writer.Write7BitEncodedInt(byteCount);
        if (byteCount > 0)
            writer.Write(StrictUtf8.GetBytes(value));
    }

    static string ReadBoundedString(BinaryReader reader, int maxBytes)
    {
        int length = reader.Read7BitEncodedInt();
        if (length < 0 || length > maxBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid component string bounds.");
        if (length == 0)
            return "";
        try
        {
            return StrictUtf8.GetString(reader.ReadBytes(length));
        }
        catch (DecoderFallbackException e)
        {
            throw new InvalidDataException("Invalid UTF-8 in component string.", e);
        }
    }
}