using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace GRIDAutosportVR;

// Normal VR play through Steam: stage the verified VR executable and bridge under a durable
// lease, supervise the game, then restore the stock files. Ported from the VB launcher.
internal static partial class VrPlaySession
{
    internal const string StockSha256 = "c88929ba8ed5327799fb6f57dc7889b2f5e8baec47b92832038af913ff54fc48";
    private const string VrSha256 = "9d3e8f9a36571e20707187f1da86967201ab74e72f9f72e8ec446f01dbe59856";
    private const int ImportNameOffset = 0xE0C23C;
    private const string Avx = "GRIDAutosport_avx.exe";
    private const string BridgeName = "gasxr.dll";
    private const string SessionMutex = @"Local\GRIDAutosportVR-255220";

    private sealed class Lease
    {
        public string? Id { get; set; }
        public string? GameFolder { get; set; }
        public string? BridgeHash { get; set; }
    }

    private static string SessionFolder(string id) => Path.Combine(LauncherSettings.DataDirectory, "sessions", id);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string FileHash(string fileName)
    {
        using var input = File.OpenRead(fileName);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
    private static void WriteNew(string fileName, byte[] bytes)
    {
        using var output = new FileStream(fileName, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
    }
    private static void Record(string id, string message) =>
        File.AppendAllText(Path.Combine(SessionFolder(id), "supervisor.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);

    private static T UnderLock<T>(Func<T> action)
    {
        using var gate = new Mutex(false, SessionMutex);
        try
        {
            if (!gate.WaitOne(0)) throw new IOException("A GRID Autosport VR session is already running.");
        }
        catch (AbandonedMutexException)
        {
            // The former supervisor exited. Its durable lease still describes the installation.
        }
        try { return action(); }
        finally { gate.ReleaseMutex(); }
    }

    internal static void EnsureIdle(string? folder = null)
    {
        foreach (var processName in new[] { "GRIDAutosport", "GRIDAutosport_avx" })
            foreach (var entry in Process.GetProcessesByName(processName))
                using (entry)
                {
                    try
                    {
                        if (entry.HasExited) continue;
                        var imageName = entry.MainModule?.FileName;
                        if (folder is not null && !string.IsNullOrEmpty(imageName) &&
                            !string.Equals(Path.GetDirectoryName(imageName), folder, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    catch (InvalidOperationException) when (entry.HasExited) { continue; }
                    catch (Win32Exception error)
                    {
                        throw new IOException("Cannot verify the running game's installation. Close GRID Autosport before continuing.", error);
                    }
                    throw new IOException("Close GRID Autosport before changing its VR installation.");
                }
    }

    private static string CheckedFolder(string? folder)
    {
        if (!Installation.GameExists(folder)) throw new IOException("Select the registered Steam installation of GRID Autosport.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder!));
    }

    // Steam arguments for the game process. eyeSize is the per-eye render size reported to the game.
    internal static string[] SteamArguments(string id, bool hudFollow, (int Width, int Height)? eyeSize) =>
        ["-applaunch", Installation.AppId, "-gasvrplay=" + id, "-gasvrmode=openxr", "-gasvrhudfollow=" + (hudFollow ? "1" : "0"),
         .. eyeSize is { } eye ? new[] { $"-gasvreyesize={eye.Width}x{eye.Height}" } : []];

    public static int Run(string gameFolder, string bridgePath, (int Width, int Height)? eyeSize = null, string? renderNote = null)
    {
        var folder = CheckedFolder(gameFolder);
        var client = SteamLibrary.SteamClient() ?? throw new IOException("Steam.exe was not found. Start Steam, then try again.");
        var library = Directory.GetParent(folder)!.Parent!.Parent!.FullName;
        if (!SteamLibrary.SteamLibraries().Any(root => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), library, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The selected installation is outside Steam's registered libraries.");
        var hudFollow = LauncherSettings.Load().HudFollowView;
        return UnderLock(() =>
        {
            if (File.Exists(Path.Combine(folder, Installation.BenchmarkLease))) throw new IOException("Restore the outstanding benchmark session first.");
            EnsureIdle(folder);
            RestoreLease(folder);
            var id = Guid.NewGuid().ToString("N");
            try
            {
                Stage(folder, bridgePath, id);
                var start = new ProcessStartInfo(client) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(client) };
                foreach (var arg in SteamArguments(id, hudFollow, eyeSize)) start.ArgumentList.Add(arg);
                if (renderNote is not null) Record(id, renderNote);
                Record(id, "Requesting normal play through Steam.");
                var requested = DateTime.UtcNow;
                using (var steam = Process.Start(start))
                    if (steam is null) throw new IOException("Steam could not start GRID Autosport.");
                return WaitForGame(folder, id, requested);
            }
            finally
            {
                if (File.Exists(Path.Combine(folder, Installation.PlayLease))) RestoreLease(folder);
            }
        });
    }

    public static bool Recover(string? gameFolder)
    {
        var folder = CheckedFolder(gameFolder);
        if (!File.Exists(Path.Combine(folder, Installation.PlayLease))) return false;
        return UnderLock(() => RestoreLease(folder));
    }

    // The marker and stock backup are committed before the lease. The lease is
    // committed before either game file is changed, including on an interrupted launch.
    private static void Stage(string folder, string bridgePath, string id)
    {
        var executable = Path.Combine(folder, Avx);
        var dll = Path.Combine(folder, BridgeName);
        if (File.Exists(dll) || File.Exists(Path.Combine(folder, Installation.PlayLease)))
            throw new IOException("A VR bridge or play session already exists in the game folder. Existing files were preserved.");
        var stock = File.ReadAllBytes(executable);
        if (Hash(stock) != StockSha256 || !stock.AsSpan(ImportNameOffset, 10).SequenceEqual("d3d11.dll\0"u8))
            throw new IOException("This GRID Autosport AVX executable is not the supported Steam build.");
        var candidate = (byte[])stock.Clone();
        Encoding.ASCII.GetBytes(BridgeName).CopyTo(candidate, ImportNameOffset);
        if (Hash(candidate) != VrSha256) throw new IOException("The VR executable verification failed.");
        var payload = File.ReadAllBytes(bridgePath);
        var info = new Lease { Id = id, GameFolder = folder, BridgeHash = Hash(payload) };
        var saved = SessionFolder(id);
        Directory.CreateDirectory(saved);
        WriteNew(Path.Combine(saved, "session.id"), Encoding.ASCII.GetBytes(id));
        WriteNew(Path.Combine(saved, "original-avx.exe"), stock);
        if (FileHash(Path.Combine(saved, "original-avx.exe")) != StockSha256) throw new IOException("The stock backup verification failed.");
        var prefix = Path.Combine(folder, "gasvr-play-" + id);
        WriteNew(prefix + ".lease.tmp", JsonSerializer.SerializeToUtf8Bytes(info));
        File.Move(prefix + ".lease.tmp", Path.Combine(folder, Installation.PlayLease));
        WriteNew(prefix + ".dll.tmp", payload);
        if (FileHash(prefix + ".dll.tmp") != info.BridgeHash) throw new IOException("The staged bridge verification failed.");
        File.Move(prefix + ".dll.tmp", dll);
        WriteNew(prefix + ".exe.tmp", candidate);
        EnsureIdle(folder);
        if (FileHash(executable) != StockSha256 || FileHash(prefix + ".exe.tmp") != VrSha256) throw new IOException("The game files changed during staging.");
        File.Replace(prefix + ".exe.tmp", executable, null);
        if (FileHash(executable) != VrSha256) throw new IOException("The installed VR executable verification failed.");
        Record(id, "Staged verified AVX executable and bridge " + info.BridgeHash + ".");
    }

    private static void RemoveMatching(string fileName, string expected, string id)
    {
        var timer = Stopwatch.StartNew();
        var retried = false;
        while (true)
        {
            try
            {
                // Hash the actual contents for each attempt. Do not allow writers
                // between verification and delete; allow our open handle to be deleted.
                using (var input = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (Convert.ToHexStringLower(SHA256.HashData(input)) != expected) throw new IOException("Preserving independently changed file: " + fileName);
                    File.Delete(fileName);
                }
                if (retried) Record(id, $"Released staged bridge after {timer.ElapsedMilliseconds} ms.");
                return;
            }
            catch (FileNotFoundException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && (error.HResult & 0xFFFF) is 5 or 32 or 33)
            {
                var entry = new FileInfo(fileName);
                if (entry.Exists && entry.IsReadOnly) throw new IOException("The staged VR bridge is read-only. Preserving the file and recovery lease: " + fileName, error);
                if (timer.Elapsed >= TimeSpan.FromSeconds(8)) throw new IOException("Windows is still denying removal of the staged VR bridge. The recovery lease was kept: " + fileName, error);
                if (!retried) Record(id, "Waiting for Windows to release the verified staged bridge.");
                retried = true;
                Thread.Sleep(200);
            }
        }
    }

    private static void RemoveStageTemp(string fileName, string expected, string id)
    {
        if (!File.Exists(fileName)) return;
        if (FileHash(fileName) == expected) File.Delete(fileName);
        else Record(id, "Kept incomplete or independently changed staging file " + fileName);
    }

    [GeneratedRegex(@"\A[0-9a-f]{32}\z")] private static partial Regex SessionId();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z")] private static partial Regex Sha256Text();

    private static bool RestoreLease(string folder)
    {
        var leaseFile = Path.Combine(folder, Installation.PlayLease);
        if (!File.Exists(leaseFile)) return false;
        if (File.Exists(Path.Combine(folder, Installation.BenchmarkLease))) throw new IOException("A benchmark lease also exists. Preserving both sessions.");
        EnsureIdle(folder);
        var info = JsonSerializer.Deserialize<Lease>(File.ReadAllBytes(leaseFile));
        if (info is null || !SessionId().IsMatch(info.Id ?? "") || !Sha256Text().IsMatch(info.BridgeHash ?? "") ||
            !string.Equals(info.GameFolder, folder, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid VR play lease. The game files were preserved.");
        var id = info.Id!;
        var saved = SessionFolder(id);
        if (!File.ReadAllBytes(Path.Combine(saved, "session.id")).SequenceEqual(Encoding.ASCII.GetBytes(id))) throw new IOException("Session identity mismatch. The game files were preserved.");
        var backup = Path.Combine(saved, "original-avx.exe");
        if (FileHash(backup) != StockSha256) throw new IOException("The original AVX backup failed verification. The game files were preserved.");
        var prefix = Path.Combine(folder, "gasvr-play-" + id);
        var executable = Path.Combine(folder, Avx);
        switch (FileHash(executable))
        {
            case VrSha256:
                var restoreTemp = prefix + ".restore.tmp";
                if (File.Exists(restoreTemp) && FileHash(restoreTemp) != StockSha256) restoreTemp = prefix + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
                if (!File.Exists(restoreTemp)) WriteNew(restoreTemp, File.ReadAllBytes(backup));
                if (FileHash(restoreTemp) != StockSha256) throw new IOException("The restore copy failed verification.");
                File.Replace(restoreTemp, executable, null);
                break;
            case StockSha256:
                break; // Staging stopped before the swap, or recovery already restored the executable.
            default:
                throw new IOException("The installed game executable changed independently. The current file and backup were preserved.");
        }
        if (FileHash(executable) != StockSha256) throw new IOException("Stock restoration could not be verified.");
        RemoveMatching(Path.Combine(folder, BridgeName), info.BridgeHash!, id);
        RemoveStageTemp(prefix + ".exe.tmp", VrSha256, id);
        RemoveStageTemp(prefix + ".dll.tmp", info.BridgeHash!, id);
        RemoveStageTemp(prefix + ".restore.tmp", StockSha256, id);
        Record(id, "Restored and verified stock AVX " + StockSha256 + ".");
        File.Delete(leaseFile);
        File.Delete(Path.Combine(saved, "session.id"));
        File.Delete(backup);
        return true;
    }

    // Windows returns the command line as a counted UNICODE_STRING in our
    // process, including when this x64 supervisor queries the x86 renderer.
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(SafeProcessHandle handle, int informationClass, IntPtr buffer, int size, out int used);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string command, out int count);
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr address);

    private static bool IsOurGame(Process proc, string folder, string id)
    {
        var imageName = proc.MainModule?.FileName;
        if (string.IsNullOrEmpty(imageName) || !string.Equals(Path.GetDirectoryName(imageName), folder, StringComparison.OrdinalIgnoreCase)) return false;
        NtQueryInformationProcess(proc.SafeHandle, 60, IntPtr.Zero, 0, out var size);
        if (size <= IntPtr.Size * 2 || size > 131072) return false;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(proc.SafeHandle, 60, buffer, size, out var used) != 0) return false;
            var length = (ushort)Marshal.ReadInt16(buffer);
            var content = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            var offset = content.ToInt64() - buffer.ToInt64();
            if (length % 2 != 0 || offset < IntPtr.Size * 2 || offset + length > used) return false;
            var command = Marshal.PtrToStringUni(content, length / 2);
            var args = CommandLineToArgvW(command, out var count);
            if (args == IntPtr.Zero) return false;
            try
            {
                for (var i = 1; i < count; ++i)
                    if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, i * IntPtr.Size)) == "-gasvrplay=" + id) return true;
                return false;
            }
            finally { LocalFree(args); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool Activated(string id, DateTime requested)
    {
        var log = Path.Combine(SessionFolder(id), "bridge.log");
        if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) < requested.AddSeconds(-2)) return false;
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var lines = new StreamReader(stream);
        while (lines.ReadLine() is { } line)
            if (line.Contains("native renderer configured:", StringComparison.Ordinal)) return true;
        return false;
    }

    private static int WaitForGame(string folder, string id, DateTime requested)
    {
        var watched = new Dictionary<int, Process>();
        Process? renderer = null;
        var clock = Stopwatch.StartNew();
        TimeSpan? exitedBeforeObservation = null;
        try
        {
            while (true)
            {
                foreach (var name in new[] { "GRIDAutosport", "GRIDAutosport_avx" })
                    foreach (var found in Process.GetProcessesByName(name))
                    {
                        Process? proc = found;
                        try
                        {
                            if (watched.ContainsKey(proc.Id) || proc.StartTime.ToUniversalTime() < requested.AddSeconds(-2) || !IsOurGame(proc, folder, id)) continue;
                            watched.Add(proc.Id, proc);
                            if (name == "GRIDAutosport_avx") renderer = proc;
                            Record(id, "Watching " + name + " PID " + proc.Id + ".");
                            proc = null; // The dictionary now owns its process handle.
                        }
                        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                        {
                            // A short-lived Steam wrapper may disappear during enumeration.
                        }
                        finally { proc?.Dispose(); }
                    }
                if (renderer is not null && watched.Values.All(p => p.HasExited))
                {
                    foreach (var p in watched.Values) Record(id, $"PID {p.Id} exited 0x{p.ExitCode:X8}.");
                    if (renderer.ExitCode == 0 && !Activated(id, requested))
                        throw new IOException("GRID Autosport exited without activating its VR renderer. See " + SessionFolder(id));
                    return renderer.ExitCode;
                }
                if (renderer is null && watched.Values.All(p => p.HasExited) && File.Exists(Path.Combine(SessionFolder(id), "bridge.log")))
                {
                    exitedBeforeObservation ??= clock.Elapsed;
                    if (clock.Elapsed - exitedBeforeObservation.Value > TimeSpan.FromSeconds(3))
                        throw new IOException("The VR renderer started and exited before its exit code could be observed. See " + SessionFolder(id));
                }
                else exitedBeforeObservation = null;
                if (renderer is null && clock.Elapsed >= TimeSpan.FromSeconds(90))
                    throw new IOException("Steam did not start a matching GRID Autosport AVX VR session within 90 seconds. Close any open game before recovery.");
                Thread.Sleep(200);
            }
        }
        finally { foreach (var p in watched.Values) p.Dispose(); }
    }
}
