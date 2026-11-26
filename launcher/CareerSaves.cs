using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GRIDAutosportVR;

// Career details for the Profile tab, read from a career's save directory (savegame@profile0#QKRHMYXE).
// The directory is encrypted with the game's own save codec, so it is decoded by running that codec
// from the installed, unchanged GRIDAutosport_avx.exe under the Unicorn CPU emulator: no game code
// is shipped or reimplemented. Ported from GRID2VR's tools/career_profile/stock_save.py; the
// Autosport addresses are documented in tools/career_profile/stock_save.py.
internal sealed record CareerSummary(string Label, int? CareerRaces, int? OnlineRaces)
{
    public string Text => CareerRaces is { } career && OnlineRaces is { } online
        ? $"{career} career {(career == 1 ? "race" : "races")} completed · {online} online"
        : Label;
}

internal static partial class CareerSaves
{
    internal const string Directory = "savegame@profile0#QKRHMYXE";
    // Unicorn 2.1.4's unicorn.dll from the pinned win_amd64 wheel (tools/Stage-CareerRuntime.ps1).
    internal const string UnicornSha256 = "010d85dce84b8f6ffaec3d5420f1b5f7768f859eb831be574c7da23e656ee8f1";

    static readonly Dictionary<string, (long Length, DateTime Written, CareerSummary Summary)> cache = new(StringComparer.OrdinalIgnoreCase);

    internal static string? UnicornPath()
    {
        foreach (var candidate in new[] { Path.Combine(AppContext.BaseDirectory, "career", "unicorn.dll") })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    [GeneratedRegex(@"Career Races:\s*(\d{1,6}),\s*Online Races:\s*(\d{1,6})\s*$")]
    private static partial Regex Counts();

    internal static CareerSummary Parse(string label)
    {
        var match = Counts().Match(label);
        return match.Success
            ? new CareerSummary(label, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
            : new CareerSummary(label, null, null);
    }

    // One summary per save folder, or the reason it could not be read. Folders without a directory
    // (fresh careers) give null. Results are kept per file size and write time. Decoding runs in a
    // child copy of the launcher started with Control Flow Guard and CET shadow stacks off: Unicorn's
    // longjmp out of its translated code is neither a registered CFG target nor shadow-stack safe, so
    // in the launcher process itself (both on, as .NET builds it) it fails fast. The child only reads.
    internal static Dictionary<string, (CareerSummary? Summary, string? Error)> Read(IEnumerable<string> folders, string? gameFolder)
    {
        var results = new Dictionary<string, (CareerSummary?, string?)>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(string Folder, FileInfo File)>();
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var file = new FileInfo(Path.Combine(folder, Directory));
                if (!file.Exists) { results[folder] = (null, null); continue; }
                ProfileStore.NoLinks(file.FullName);
                lock (cache)
                    if (cache.TryGetValue(file.FullName, out var known) && known.Length == file.Length && known.Written == file.LastWriteTimeUtc)
                    { results[folder] = (known.Summary, null); continue; }
                pending.Add((folder, file));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { results[folder] = (null, error.Message); }
        }
        if (pending.Count == 0) return results;
        Dictionary<string, Label> labels;
        try
        {
            if (gameFolder == null) throw new IOException("Choose your GRID Autosport folder to read career details.");
            labels = RunChild(new Request(gameFolder, pending.Select(p => p.Folder).ToArray()));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            foreach (var (folder, _) in pending) results[folder] = (null, error.Message);
            return results;
        }
        foreach (var (folder, file) in pending)
        {
            if (!labels.TryGetValue(folder, out var label) || label.Text == null)
            { results[folder] = (null, label?.Error ?? "The career reader gave no answer."); continue; }
            var summary = Parse(label.Text);
            lock (cache) cache[file.FullName] = (file.Length, file.LastWriteTimeUtc, summary);
            results[folder] = (summary, null);
        }
        return results;
    }

    sealed record Request(string Game, string[] Folders);
    sealed record Label(string Folder, string? Text, string? Error);

    // The child: decodes each folder's directory and writes the labels beside the request.
    internal static int Child(string requestPath)
    {
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<Request>(File.ReadAllBytes(requestPath))
                ?? throw new InvalidDataException("Empty career request.");
            if (request.Folders is not { Length: > 0 and <= 100 }) throw new InvalidDataException("Invalid career request.");
            var labels = new List<Label>();
            StockSaveCodec? codec = null;
            string? setupError = null;
            try
            {
                foreach (var folder in request.Folders)
                {
                    try
                    {
                        var path = Path.Combine(folder, Directory);
                        ProfileStore.NoLinks(path);
                        var bytes = File.ReadAllBytes(path);
                        if (setupError == null)
                        {
                            try { codec ??= StockSaveCodec.Open(request.Game); }
                            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                            { setupError = error.Message; }
                        }
                        labels.Add(setupError != null ? new Label(folder, null, setupError) : new Label(folder, codec!.DirectoryLabel(bytes), null));
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                    { labels.Add(new Label(folder, null, error.Message)); }
                }
            }
            finally { codec?.Dispose(); }
            var temp = requestPath + ".tmp";
            File.WriteAllBytes(temp, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(labels));
            File.Move(temp, requestPath + ".result", true);
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return 1; }
    }

    // A completed career (CareerCompletion) written in place over the copied career in `folder`.
    internal static void Complete(string folder, string? gameFolder)
    {
        if (gameFolder == null) throw new IOException("Choose your GRID Autosport folder first.");
        var self = Environment.ProcessPath ?? throw new IOException("The career writer cannot find the launcher.");
        var path = Path.Combine(Path.GetTempPath(), $"gasvr-complete-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Request(gameFolder, [folder])));
            var code = ChildProcess.RunWithoutCfg(self, $"\"{self}\" --complete-career \"{path}\"", 120000);
            var result = File.Exists(path + ".result") ? File.ReadAllText(path + ".result") : null;
            if (code != 0 || result != "ok") throw new IOException(result ?? $"The career writer stopped (exit 0x{code:X}).");
        }
        finally
        {
            foreach (var file in new[] { path, path + ".tmp", path + ".result" })
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static int CompleteChild(string requestPath)
    {
        string result;
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<Request>(File.ReadAllBytes(requestPath))
                ?? throw new InvalidDataException("Empty career request.");
            if (request.Folders is not [var folder]) throw new InvalidDataException("Invalid career request.");
            ProfileStore.NoLinks(folder);
            using var codec = StockSaveCodec.Open(request.Game);
            CareerCompletion.CompleteFolder(codec, folder);
            result = "ok";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or System.Text.Json.JsonException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { result = error.Message; }
        try
        {
            File.WriteAllText(requestPath + ".tmp", result);
            File.Move(requestPath + ".tmp", requestPath + ".result", true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 2; }
        return result == "ok" ? 0 : 1;
    }

    static Dictionary<string, Label> RunChild(Request request)
    {
        var self = Environment.ProcessPath ?? throw new IOException("The career reader cannot find the launcher.");
        var path = Path.Combine(Path.GetTempPath(), $"gasvr-career-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request));
            var code = ChildProcess.RunWithoutCfg(self, $"\"{self}\" --read-careers \"{path}\"", 60000);
            if (code != 0 || !File.Exists(path + ".result")) throw new IOException($"The career reader stopped (exit 0x{code:X}).");
            var labels = System.Text.Json.JsonSerializer.Deserialize<Label[]>(File.ReadAllBytes(path + ".result")) ?? [];
            return labels.Where(l => l.Folder != null).GroupBy(l => l.Folder, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var file in new[] { path, path + ".tmp", path + ".result" })
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

// CreateProcess with CFG and shadow stacks always off (PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY).
internal static class ChildProcess
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo { public int cb; public IntPtr reserved, desktop, title; public int x, y, width, height, columns, rows, fill, flags; public short show, reserved2; public IntPtr reserved3, input, output, error; }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo info; public IntPtr attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public IntPtr process, thread; public int processId, threadId; }
    [DllImport("kernel32", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nint size);
    [DllImport("kernel32", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, nint attribute, ulong[] value, nint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, int flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32")] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);

    const nint MitigationPolicy = 0x20007;
    // CFG off, and user-mode shadow stacks (CET) off: the launcher image is marked CET compatible.
    const ulong CfgAlwaysOff = 2UL << 40, ShadowStacksAlwaysOff = 2UL << 28;
    const int ExtendedStartupInfo = 0x80000, NoWindow = 0x08000000;

    internal static uint RunWithoutCfg(string application, string command, uint timeout)
    {
        nint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new IOException("The career reader could not be prepared.");
            try
            {
                ulong[] policy = [CfgAlwaysOff, ShadowStacksAlwaysOff];
                if (!UpdateProcThreadAttribute(list, 0, MitigationPolicy, policy, 2 * sizeof(ulong), IntPtr.Zero, IntPtr.Zero))
                    throw new IOException("The career reader could not be prepared.");
                var startup = new StartupInfoEx { attributes = list };
                startup.info.cb = Marshal.SizeOf<StartupInfoEx>();
                if (!CreateProcessW(application, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfo | NoWindow,
                        IntPtr.Zero, null, ref startup, out var child))
                    throw new IOException($"The career reader could not start (error {Marshal.GetLastWin32Error()}).");
                try
                {
                    if (WaitForSingleObject(child.process, timeout) != 0)
                    {
                        TerminateProcess(child.process, 1);
                        WaitForSingleObject(child.process, 5000);
                        throw new IOException("The career reader timed out.");
                    }
                    return GetExitCodeProcess(child.process, out var code) ? code : uint.MaxValue;
                }
                finally { CloseHandle(child.thread); CloseHandle(child.process); }
            }
            finally { DeleteProcThreadAttributeList(list); }
        }
        finally { Marshal.FreeHGlobal(list); }
    }
}

// The game's archive descriptor, crypto provider and save service, emulated. Every Windows call the
// codec makes is a small stand-in; anything unexpected stops it. The game files are only read.
internal sealed class StockSaveCodec : IDisposable
{
    const ulong Owner = 0x20000000, Stack = 0x21017000, Stop = 0x22000000, Heap = 0x23000000, HeapEnd = 0x23080000, Source = 0x24000000;
    const int RegEax = 19, RegEbp = 20, RegEcx = 22, RegEip = 26, RegEsp = 30;
    internal const int Capacity = 262144;
    // GRIDAutosport_avx.exe (Steam 2019 build), stock or with the VR session's renamed d3d11 import.
    static readonly string[] Executables = [GameBuilds.Steam.StockSha256, GameBuilds.Steam.VrSha256];

    readonly Unicorn uc;
    readonly List<Unicorn.HookCode> hooks = [];
    ulong heap = Heap, service;
    IncrementalHash? hash;
    Exception? failure;
    // Encoding: the game's WriteFile calls land here, never in a file.
    readonly List<byte> output = [];
    int limit;
    byte[] label = [];
    ulong labelStubs;

    StockSaveCodec(Unicorn engine) { uc = engine; }

    internal static StockSaveCodec Open(string? gameFolder)
    {
        if (gameFolder == null) throw new IOException("Choose your GRID Autosport folder to read career details.");
        var executable = Path.Combine(gameFolder, GameBuilds.Steam.Executable);
        var image = File.ReadAllBytes(executable);
        if (!Executables.Contains(Convert.ToHexStringLower(SHA256.HashData(image))))
            throw new InvalidDataException("Career details need the Steam version's GRIDAutosport_avx.exe.");
        var library = CareerSaves.UnicornPath() ?? throw new IOException("The career reader (career\\unicorn.dll) is not installed.");
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(library))) != CareerSaves.UnicornSha256)
            throw new InvalidDataException("The career reader (career\\unicorn.dll) is damaged. Reinstall GRID Autosport VR.");
        var codec = new StockSaveCodec(new Unicorn(library));
        try { codec.Start(image); return codec; }
        catch { codec.Dispose(); throw; }
    }

    void Start(byte[] image)
    {
        var pe = BitConverter.ToInt32(image, 60);
        var size = (BitConverter.ToUInt32(image, pe + 24 + 56) + 0xfffu) & ~0xfffu;
        var first = pe + 24 + BitConverter.ToUInt16(image, pe + 20);
        uc.Map(0x400000, size);
        for (var i = 0; i < BitConverter.ToUInt16(image, pe + 6); i++)
        {
            var header = first + 40 * i;
            var rva = BitConverter.ToUInt32(image, header + 12);
            var length = BitConverter.ToInt32(image, header + 16);
            var offset = BitConverter.ToInt32(image, header + 20);
            if (length > 0) uc.Write(0x400000 + rva, image.AsSpan(offset, Math.Min(length, image.Length - offset)));
        }
        uc.Map(Owner, 0x20000); uc.Map(0x21000000, 0x20000); uc.Map(Stop, 0x1000);
        uc.Map(Heap, (uint)(HeapEnd - Heap)); uc.Map(Source, 0x40000);

        Bind(0xfc2028, 5, AcquireProvider);
        Bind(0xfc2020, 5, CreateHash);
        Bind(0xfc2024, 4, HashData);
        Bind(0xfc201c, 5, HashResult);
        Bind(0xfc202c, 1, a => a[0] == 2 ? 1u : 0u);
        Bind(0xfc2030, 2, a => a[0] == 1 && a[1] == 0 ? 1u : 0u);
        Bind(0xfc20ec, 5, WriteFile);
        Bind(0xfc22f8, 1, a => { uc.Write(a[0], BitConverter.GetBytes(123456789UL)); return 1; });
        Bind(0, 3, CopyBytes, address: 0xf68b0c, cdecl: true);
        var allocator = Owner + 0x1000;
        Put(allocator, (uint)(allocator + 0x100));
        Put(allocator + 0x108, (uint)Bind(0, 2, Allocate));
        Put(Owner + 4, (uint)allocator);
        Put(Owner + 0x5a0, 65536);
        var descriptor = Owner + 0xa0;
        if (Call(0x8a1dd0, descriptor, [(uint)allocator, 0]) != descriptor) throw Invalid("archive descriptor");
        if (Call(0x8a3670, descriptor, [0x106cb50, Word(0x107cf50), 0x1210000, Word(0x107cf54)]) != 0) throw Invalid("archive verification");
        Call(0x88c9b0, Owner, [], until: 0x88ca4f);
        var context = Word(Owner + 0x620);
        if (context == 0) throw Invalid("archive provider");
        service = Owner + 0x2000;
        Put(service + 4, (uint)allocator);
        Put(service + 0x10, context);
        if (Call(0xd74a60, service, []) != 0) throw Invalid("save service");
    }

    static InvalidDataException Invalid(string what) => new($"The game's save codec did not start ({what}).");

    // The load menu's label for a normal two-file career directory, after the game's own checksum.
    internal string DirectoryLabel(byte[] physical) => ReadDirectory(physical).Label;

    internal (string Label, (uint Id, uint Size, uint Checksum)[] Rows) ReadDirectory(byte[] physical)
    {
        if (physical.Length != 416) throw new InvalidDataException("This career's save directory has an unknown layout.");
        var raw = Decode(physical);
        var stored = BitConverter.ToUInt32(raw, 0x180);
        if (BitConverter.ToUInt32(raw, 0x184) != 2 || Checksum(raw[..0x180], raw[0x184..]) != stored)
            throw new InvalidDataException("This career's save directory is damaged.");
        var end = -1;
        for (var i = 0; i < 256; i += 2) if (raw[i] == 0 && raw[i + 1] == 0) { end = i; break; }
        if (end < 0) throw new InvalidDataException("This career's save directory has no label.");
        var rows = new (uint, uint, uint)[2];
        for (var i = 0; i < 2; i++)
            rows[i] = (BitConverter.ToUInt32(raw, 0x188 + 12 * i), BitConverter.ToUInt32(raw, 0x18c + 12 * i), BitConverter.ToUInt32(raw, 0x190 + 12 * i));
        return (Encoding.Unicode.GetString(raw, 0, end), rows);
    }

    internal byte[] Read(byte[] physical, int size)
    {
        if (size <= 0 || size > physical.Length) throw new InvalidDataException("A career file is shorter than its directory says.");
        return Decode(physical)[..size];
    }

    internal uint Checksum(byte[] data) => Checksum([data]);

    // The physical file for one logical career file (whole 16-byte blocks), and its directory checksum.
    internal (byte[] Physical, uint Checksum) Encode(byte[] source)
    {
        if (source.Length is <= 32 or >= Capacity || source.Length % 16 != 0) throw new InvalidDataException("Invalid career file size.");
        uc.Write(Source, source);
        output.Clear();
        limit = source.Length;
        var entry = Owner + 0x3000;
        ulong previous = entry + 0x100, used = entry + 0x104, record = entry + 0x200;
        uc.Write(entry, new byte[0x210]);
        Put(entry + 0x2c, (uint)source.Length);
        Put(record + 8, Call(0x879e70, 0, []));
        var status = Call(0xd753d0, service, [0, (uint)entry, (uint)record, (uint)previous, 0, 1, (uint)source.Length, (uint)Source, 0, (uint)used]);
        if (status != 0 || Word(used) != source.Length || output.Count != limit) throw new InvalidDataException("Incomplete career file write.");
        return (output.ToArray(), Word(record + 8));
    }

    uint WriteFile(uint[] a)
    {
        var (handle, data, length, used, overlapped) = (a[0], a[1], a[2], a[3], a[4]);
        if (handle != 1 || length > 65536 || output.Count + length > limit || Word(overlapped + 8) != output.Count)
            throw new InvalidDataException("Unexpected career file write.");
        var bytes = new byte[length]; uc.Read(data, bytes); output.AddRange(bytes);
        Put(used, length);
        return 1;
    }

    // The 416-byte directory for a label and the two files' (id, size, checksum), by the game's own writer.
    internal byte[] WriteDirectory(string text, (uint Id, uint Size, uint Checksum)[] rows)
    {
        label = Encoding.Unicode.GetBytes(text + "\0");
        if (label.Length > 256 || rows.Length != 2) throw new InvalidDataException("Career label too long.");
        ulong op = Owner + 0x5000, request = op + 0x100, profile = op + 0x200, vtable = op + 0x300, vector = op + 0x400, records = op + 0x500;
        if (labelStubs == 0)
        {
            // Autosport's string buffer is (capacity in characters, pointer); the profile is at request + 0x6c.
            labelStubs = Bind(0, 1, a =>
            {
                if (Word(a[0]) < label.Length / 2) throw new InvalidDataException("Career label too long.");
                uc.Write(Word(a[0] + 4), label);
                return 0;
            });
            Put(vtable + 0x38, (uint)Bind(0, 0, _ => 1));
        }
        Put(vtable + 0x34, (uint)labelStubs);
        Put(op, (uint)request); Put(request + 0x6c, (uint)profile); Put(profile, (uint)vtable);
        var entries = new byte[12 * rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            BitConverter.TryWriteBytes(entries.AsSpan(12 * i), rows[i].Id);
            BitConverter.TryWriteBytes(entries.AsSpan(12 * i + 4), rows[i].Size);
            BitConverter.TryWriteBytes(entries.AsSpan(12 * i + 8), rows[i].Checksum);
        }
        uc.Write(records, entries);
        Put(vector + 4, (uint)records); Put(vector + 8, (uint)(records + (ulong)entries.Length));
        Call(0xd7b240, service, [(uint)op, (uint)vector], until: 0xd7b520);
        if (Word(uc.Reg(RegEbp) - 8) != 416) throw new InvalidDataException("Unexpected career directory size.");
        var directory = new byte[416];
        uc.Read(Word(service + 0x338) + 0x8000, directory);
        return directory;
    }

    byte[] Decode(byte[] physical)
    {
        var decoded = new byte[physical.Length];
        var used = Owner + 0x1800; var dest = Source + 65536;
        for (var offset = 0; offset < physical.Length; offset += 65536)
        {
            var length = Math.Min(65536, physical.Length - offset);
            uc.Write(Source, physical.AsSpan(offset, length));
            if (Call(0xd74f20, service, [(uint)Source, (uint)length, (uint)dest, 65536, (uint)used]) != 0 || Word(used) != length)
                throw new InvalidDataException("Incomplete career save read.");
            uc.Read(dest, decoded.AsSpan(offset, length));
        }
        return decoded;
    }

    uint Checksum(params byte[][] parts)
    {
        var value = Call(0x879e70, 0, []);
        foreach (var part in parts)
        {
            uc.Write(Source, part);
            value = Call(0x879e80, 0, [value, (uint)Source, (uint)part.Length]);
        }
        return value;
    }

    uint Word(ulong address) { Span<byte> b = stackalloc byte[4]; uc.Read(address, b); return BitConverter.ToUInt32(b); }
    void Put(ulong address, uint value) => uc.Write(address, BitConverter.GetBytes(value));

    ulong Bind(ulong slot, int count, Func<uint[], uint> handler, ulong address = 0, bool cdecl = false)
    {
        if (address == 0) address = Stop + 16 * (ulong)(hooks.Count + 1);
        if (slot != 0) Put(slot, (uint)address);
        Unicorn.HookCode forward = (_, _, _, _) =>
        {
            try
            {
                var sp = uc.Reg(RegEsp);
                var frame = new byte[4 * (count + 1)];
                uc.Read(sp, frame);
                var args = new uint[count];
                for (var i = 0; i < count; i++) args[i] = BitConverter.ToUInt32(frame, 4 * (i + 1));
                uc.SetReg(RegEax, handler(args));
                uc.SetReg(RegEsp, sp + 4 + (cdecl ? 0u : 4u * (uint)count));
                uc.SetReg(RegEip, BitConverter.ToUInt32(frame, 0));
            }
            catch (Exception error) { failure ??= error; uc.StopSoon(); }
        };
        hooks.Add(forward);
        uc.AddCodeHook(forward, address);
        return address;
    }

    uint Call(ulong method, ulong owner, uint[] args, ulong until = Stop)
    {
        var frame = new byte[4 * (args.Length + 1)];
        BitConverter.TryWriteBytes(frame, (uint)Stop);
        for (var i = 0; i < args.Length; i++) BitConverter.TryWriteBytes(frame.AsSpan(4 * (i + 1)), args[i]);
        uc.Write(Stack, frame);
        uc.SetReg(RegEsp, Stack); uc.SetReg(RegEcx, owner);
        failure = null;
        uc.Run(method, until, 120_000_000);
        if (failure != null) throw failure as InvalidDataException ?? new InvalidDataException("The game's save codec failed.", failure);
        if (uc.Reg(RegEip) != until) throw new InvalidDataException("The game's save codec did not finish.");
        return (uint)uc.Reg(RegEax);
    }

    uint Allocate(uint[] a)
    {
        var (length, alignment) = (a[0], a[1]);
        var start = (heap + 15) & ~15UL;
        if (alignment is not (1 or 4 or 16) || length is 0 or > 65536 || start + length > HeapEnd)
            throw new InvalidDataException("Unexpected save codec allocation.");
        heap = start + length;
        return (uint)start;
    }

    uint CopyBytes(uint[] a)
    {
        var (dest, source, length) = (a[0], a[1], a[2]);
        if (length > 0x40000) throw new InvalidDataException("Unexpected save codec copy.");
        if (length > 0) { var data = new byte[length]; uc.Read(source, data); uc.Write(dest, data); }
        return dest;
    }

    uint AcquireProvider(uint[] a)
    {
        if (a[1] != 0 || a[3] != 24 || a[4] != 0xf0000000) throw new InvalidDataException("Expected the game's unkeyed hash provider.");
        Put(a[0], 1); return 1;
    }

    uint CreateHash(uint[] a)
    {
        if (a[0] != 1 || a[1] != 0x800c || a[2] != 0 || a[3] != 0) throw new InvalidDataException("Expected SHA-256.");
        hash?.Dispose(); hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Put(a[4], 2); return 1;
    }

    uint HashData(uint[] a)
    {
        if (a[0] != 2 || a[3] != 0 || a[2] > 0x400000 || hash == null) throw new InvalidDataException("Unexpected save codec hash input.");
        var data = new byte[a[2]]; uc.Read(a[1], data); hash.AppendData(data); return 1;
    }

    uint HashResult(uint[] a)
    {
        if (a[0] != 2 || a[1] != 2 || a[4] != 0 || Word(a[3]) < 32 || hash == null) throw new InvalidDataException("Unexpected save codec hash output.");
        uc.Write(a[2], hash.GetHashAndReset()); Put(a[3], 32); return 1;
    }

    public void Dispose() { hash?.Dispose(); uc.Dispose(); GC.KeepAlive(hooks); }
}

// The few Unicorn 2 C functions the codec needs, loaded from the given unicorn.dll.
internal sealed class Unicorn : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OpenFn(int arch, int mode, out IntPtr engine);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CloseFn(IntPtr engine);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int MapFn(IntPtr engine, ulong address, ulong size, uint perms);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] unsafe delegate int MemFn(IntPtr engine, ulong address, byte* bytes, ulong size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RegFn(IntPtr engine, int reg, ref ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StartFn(IntPtr engine, ulong begin, ulong until, ulong timeout, nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StopFn(IntPtr engine);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HookAddFn(IntPtr engine, out nuint hook, int type, IntPtr callback, IntPtr user, ulong begin, ulong end);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void HookCode(IntPtr engine, ulong address, uint size, IntPtr user);

    const int ArchX86 = 4, Mode32 = 4, ProtAll = 7, HookCodeType = 4;
    readonly IntPtr library, engine;
    readonly CloseFn close; readonly MapFn map; readonly MemFn write, read; readonly RegFn regRead, regWrite;
    readonly StartFn start; readonly StopFn stop; readonly HookAddFn hookAdd;

    internal Unicorn(string path)
    {
        library = NativeLibrary.Load(path);
        T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        try
        {
            close = Get<CloseFn>("uc_close"); map = Get<MapFn>("uc_mem_map");
            write = Get<MemFn>("uc_mem_write"); read = Get<MemFn>("uc_mem_read");
            regRead = Get<RegFn>("uc_reg_read"); regWrite = Get<RegFn>("uc_reg_write");
            start = Get<StartFn>("uc_emu_start"); stop = Get<StopFn>("uc_emu_stop"); hookAdd = Get<HookAddFn>("uc_hook_add");
            Check(Get<OpenFn>("uc_open")(ArchX86, Mode32, out engine));
        }
        catch { NativeLibrary.Free(library); throw; }
    }

    static void Check(int error)
    {
        if (error != 0) throw new InvalidDataException($"The career reader's emulator failed (Unicorn error {error}).");
    }

    internal void Map(ulong address, ulong size) => Check(map(engine, address, size, ProtAll));
    internal unsafe void Write(ulong address, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data) Check(write(engine, address, p, (ulong)data.Length));
    }
    internal unsafe void Read(ulong address, Span<byte> data)
    {
        fixed (byte* p = data) Check(read(engine, address, p, (ulong)data.Length));
    }
    internal ulong Reg(int reg) { ulong value = 0; Check(regRead(engine, reg, ref value)); return value & 0xffffffff; }
    internal void SetReg(int reg, ulong value) => Check(regWrite(engine, reg, ref value));
    internal void Run(ulong begin, ulong until, ulong count) => Check(start(engine, begin, until, 0, (nuint)count));
    internal void StopSoon() => stop(engine);
    internal void AddCodeHook(HookCode callback, ulong address) =>
        Check(hookAdd(engine, out _, HookCodeType, Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero, address, address));

    public void Dispose()
    {
        if (engine != IntPtr.Zero) close(engine);
        NativeLibrary.Free(library);
    }
}
