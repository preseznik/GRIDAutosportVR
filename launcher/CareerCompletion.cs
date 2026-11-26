using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace GRIDAutosportVR;

// "100% complete career" (Profile tab): a copy of the current career with every cup and championship
// won (also with the Ravenwest team) and every discipline at the top XP level. Only fixed-size fields of the RaceHistory component
// change, in both career files; the files are then re-encoded and read back with the game's own save
// codec (StockSaveCodec). Layout and reference: tools/career_profile/complete_career.py.
internal static class CareerCompletion
{
    internal const string ProgressFile = "savegame@profile0#NXDTVPKI.LKR", SettingsFile = "savegame@profile0#QKIGMYYI.LKR";
    static readonly uint[] FileIds = [1838330113, 2210129629];
    const int MaxXp = 9_999_999;                    // career_xp_level 12
    static readonly int[] Disciplines = [2, 3, 4, 5, 6];
    // Base-game cups and championships (database cup and championship tables).
    internal static readonly HashSet<int> Cups = [1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 28,
        29, 30, 31, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44];
    internal static readonly HashSet<int> Championships = [.. Enumerable.Range(41, 45), 94, 95, 96];

    static byte[] Tag(string name)
    {
        uint value = 5381;
        foreach (var c in name.ToLowerInvariant()) value = 33 * value + c;
        return BitConverter.GetBytes(value);
    }

    // RaceHistory (from its tag): every cup and championship 1st, completed and won; disciplines at MaxXp.
    internal static byte[] CompleteHistory(byte[] history)
    {
        var data = (byte[])history.Clone();
        int Word(int at) => at + 4 <= data.Length ? BitConverter.ToInt32(data, at) : throw new InvalidDataException("Truncated career history.");
        if (data[4] != 5 || Word(5) != 1337 || Word(9) != 174 || Word(13) != 41) throw new InvalidDataException("Unsupported career history version.");
        var pos = 17 + (174 + 41) * 16 + 4 * 8 + 4 + 8 + 4 + 4;
        for (var i = 0; i < 3; i++)
        {
            var count = Word(pos);
            if (count is < 0 or >= 4096) throw new InvalidDataException("Unexpected career history list.");
            pos += 4 + 4 * count;
        }
        pos += 2;
        var keyed = Word(pos);
        if (keyed is < 0 or >= 4096) throw new InvalidDataException("Unexpected career history entries.");
        pos += 4 + 28 * keyed;
        if (Word(pos) != 7) throw new InvalidDataException("Unexpected career history packages.");
        var end = pos + 8 + Word(pos + 4);
        pos += 8;
        if (end > data.Length || Word(pos) != 5 || Word(pos + 4) != 1) throw new InvalidDataException("Unexpected base-game career history.");
        pos += 8;

        // ravenWord / seasonWord: Ravenwest wins and the season of the first, in words from the slot flag.
        void Awards(int slots, int record, HashSet<int> known, int ravenWord, int seasonWord)
        {
            var seen = new HashSet<int>();
            for (var i = 0; i < slots; i++)
            {
                var flag = Word(pos);
                if (flag == 0) { pos += 4; continue; }
                int id = Word(pos + 4), best = Word(pos + 8), completed = Word(pos + 12), wins = Word(pos + 16);
                if (flag != 1 || !known.Contains(id) || !seen.Add(id) || best < -1 || completed < 0 || wins < 0)
                    throw new InvalidDataException("Unexpected career award record.");
                BitConverter.TryWriteBytes(data.AsSpan(pos + 8), 0);
                BitConverter.TryWriteBytes(data.AsSpan(pos + 12), Math.Max(completed, 1));
                BitConverter.TryWriteBytes(data.AsSpan(pos + 16), Math.Max(wins, 1));
                // Wins with the Ravenwest team (platinum), and the season of the first one.
                var raven = Word(pos + 4 * ravenWord);
                var season = seasonWord == 0 ? 1 : Word(pos + 4 * seasonWord);
                if (raven < 0 || season < 0) throw new InvalidDataException("Unexpected career award record.");
                BitConverter.TryWriteBytes(data.AsSpan(pos + 4 * ravenWord), Math.Max(raven, 1));
                if (seasonWord != 0) BitConverter.TryWriteBytes(data.AsSpan(pos + 4 * seasonWord), season == 0 ? 1 : season);
                pos += 4 + record;
                if (pos > end) throw new InvalidDataException("Truncated career award table.");
            }
            if (!seen.SetEquals(known))
                throw new InvalidDataException("The current career is missing cups or championships. Start Career in GRID Autosport once and let it save, then try again.");
        }
        Awards(40, 104, Cups, 21, 0);
        Awards(50, 76, Championships, 16, 19);
        var styles = new HashSet<int>();
        while (pos + 12 <= end && Word(pos) == 1)
        {
            int style = Word(pos + 4), xp = Word(pos + 8);
            if (!styles.Add(style) || xp is < 0 or > MaxXp) throw new InvalidDataException("Unexpected discipline record.");
            if (Disciplines.Contains(style))
            {
                BitConverter.TryWriteBytes(data.AsSpan(pos + 8), MaxXp);
                // Seasons in which levels 10, 11 and 12 were reached (the milestone awards).
                for (var level = 0; level < 3; level++)
                {
                    var season = Word(pos + 128 + 4 * level);
                    if (season < 0) throw new InvalidDataException("Unexpected discipline record.");
                    BitConverter.TryWriteBytes(data.AsSpan(pos + 128 + 4 * level), season == 0 ? 1 : season);
                }
            }
            pos += 152;
        }
        if (!Disciplines.All(styles.Contains)) throw new InvalidDataException("The current career is missing a discipline.");
        return data;
    }

    internal static byte[] Complete(byte[] group)
    {
        var tag = Tag("RaceHistory");
        var start = group.AsSpan().IndexOf(tag);
        var end = group.AsSpan().IndexOf(Tag("career manager sdu name"));
        if (start < 0 || end <= start || group.AsSpan(start + 4).IndexOf(tag) >= 0) throw new InvalidDataException("Unrecognized GRID Autosport career layout.");
        return [.. group[..start], .. CompleteHistory(group[start..end]), .. group[end..]];
    }

    // The career file framing: one raw-deflate member, its SHA-256, and (settings file) 0xBE padding.
    internal static byte[] Unframe(byte[] logical, bool padded)
    {
        var limit = logical.Length;
        if (padded) while (limit > 0 && logical[limit - 1] == 0xBE) limit--;
        for (var end = limit; end <= Math.Min(logical.Length, limit + 32); end++)
        {
            if (end < 33) continue;
            var member = logical.AsSpan(0, end - 32);
            if (!SHA256.HashData(member).AsSpan().SequenceEqual(logical.AsSpan(end - 32, 32))) continue;
            if (!padded && end != logical.Length) break;
            using var input = new DeflateStream(new MemoryStream(member.ToArray()), CompressionMode.Decompress);
            using var group = new MemoryStream();
            input.CopyTo(group);
            if (group.Length is 0 or > 2 * 1024 * 1024) break;
            return group.ToArray();
        }
        throw new InvalidDataException("A career file's checksum or framing differs from the game's format.");
    }

    static byte[] Frame(byte[] member) => [.. member, .. SHA256.HashData(member)];

    static byte[] Deflate(ReadOnlySpan<byte> data, bool final)
    {
        var buffer = new MemoryStream();
        var stream = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true);
        stream.Write(data);
        if (final) { stream.Dispose(); return buffer.ToArray(); }
        stream.Flush(); // sync flush: ends on a byte boundary, without a final block
        var prefix = buffer.ToArray();
        stream.Dispose();
        return prefix;
    }

    // A member whose frame (member + SHA-256) fills whole 16-byte cipher blocks: a sync-flushed prefix
    // and the last bytes in one stored final block, sized to make up the difference.
    internal static byte[] ProgressMember(byte[] group)
    {
        var whole = Deflate(group, final: true);
        if ((whole.Length + 32) % 16 == 0) return whole;
        for (var tail = 1; tail < Math.Min(4096, group.Length); tail++)
        {
            var prefix = Deflate(group.AsSpan(0, group.Length - tail), final: false);
            var size = prefix.Length + 5 + tail;
            if ((size + 32) % 16 != 0) continue;
            var member = new byte[size];
            prefix.CopyTo(member, 0);
            member[prefix.Length] = 1; // BFINAL, stored
            BitConverter.TryWriteBytes(member.AsSpan(prefix.Length + 1), (ushort)tail);
            BitConverter.TryWriteBytes(member.AsSpan(prefix.Length + 3), (ushort)~tail);
            group.AsSpan(group.Length - tail).CopyTo(member.AsSpan(prefix.Length + 5));
            return member;
        }
        throw new InvalidDataException("Could not encode the career at a whole number of cipher blocks.");
    }

    // Reads the career in a folder, completes it, and writes the three files back after reading
    // them through the game's codec. Runs in the career child process (CareerSaves.Child).
    internal static void CompleteFolder(StockSaveCodec codec, string folder)
    {
        string Path_(string name) => Path.Combine(folder, name);
        var (label, rows) = codec.ReadDirectory(File.ReadAllBytes(Path_(CareerSaves.Directory)));
        if (rows[0].Id != FileIds[0] || rows[1].Id != FileIds[1] || rows[0].Size + rows[1].Size != StockSaveCodec.Capacity)
            throw new InvalidDataException("Unsupported career save directory.");
        var groups = new byte[2][];
        string[] names = [ProgressFile, SettingsFile];
        for (var i = 0; i < 2; i++)
        {
            var logical = codec.Read(File.ReadAllBytes(Path_(names[i])), (int)rows[i].Size);
            if (codec.Checksum(logical) != rows[i].Checksum) throw new InvalidDataException("A career file disagrees with its directory.");
            groups[i] = Complete(Unframe(logical, padded: i == 1));
        }
        var progress = Frame(ProgressMember(groups[0]));
        var settingsFrame = Frame(Deflate(groups[1], final: true));
        var settingsSize = StockSaveCodec.Capacity - progress.Length;
        if (settingsFrame.Length > settingsSize) throw new InvalidDataException("The completed career does not fit the game's save buffer.");
        var settings = new byte[settingsSize];
        Array.Fill(settings, (byte)0xBE);
        settingsFrame.CopyTo(settings, 0);
        byte[][] frames = [progress, settings];
        var files = new Dictionary<string, byte[]>();
        var newRows = new (uint, uint, uint)[2];
        for (var i = 0; i < 2; i++)
        {
            if (!Unframe(frames[i], padded: i == 1).AsSpan().SequenceEqual(groups[i])) throw new InvalidDataException("The completed career did not round-trip.");
            var (physical, checksum) = codec.Encode(frames[i]);
            if (!codec.Read(physical, frames[i].Length).AsSpan().SequenceEqual(frames[i]) || codec.Checksum(frames[i]) != checksum)
                throw new InvalidDataException("The completed career failed the game's own readback.");
            files[names[i]] = physical;
            newRows[i] = (FileIds[i], (uint)frames[i].Length, checksum);
        }
        var directory = codec.WriteDirectory(label, newRows);
        var check = codec.ReadDirectory(directory);
        if (check.Label != label || !check.Rows.SequenceEqual(newRows)) throw new InvalidDataException("The completed career's directory did not round-trip.");
        files[CareerSaves.Directory] = directory;
        foreach (var (name, data) in files)
        {
            var temp = Path_(name + ".gasvr-new");
            File.WriteAllBytes(temp, data);
            File.Move(temp, Path_(name), true);
        }
        foreach (var (name, data) in files)
            if (!File.ReadAllBytes(Path_(name)).AsSpan().SequenceEqual(data)) throw new IOException("The completed career could not be verified on disk.");
    }
}
