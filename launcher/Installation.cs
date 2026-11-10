using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace GRIDAutosportVR;

internal static class Installation
{
    public const string AppId = "255220";
    public const string GameName = "GRID Autosport";
    public const string PlayLease = "gasvr-play-session.json";
    public const string BenchmarkLease = "gasvr-benchmark-session.json";
    private const string ProbeName = "AutosportXRProbe.exe";
    private const string LauncherName = "GRIDAutosportVR.Launcher.exe";

    public static string LogDirectory => Path.Combine(LauncherSettings.DataDirectory, "logs");

    // Steam opens the registered application. Match the selected installation to its
    // library manifest before enabling launch, including when the user browses to it.
    public static bool GameExists(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return false;
        try
        {
            var game = new DirectoryInfo(folder);
            var common = game.Parent;
            if (common is null || !string.Equals(common.Name, "common", StringComparison.OrdinalIgnoreCase) || common.Parent is null) return false;
            if (!File.Exists(Path.Combine(game.FullName, "GRIDAutosport.exe"))) return false;
            var manifest = Path.Combine(common.Parent.FullName, "appmanifest_" + AppId + ".acf");
            if (!File.Exists(manifest)) return false;
            var fields = File.ReadAllText(manifest);
            return Regex.IsMatch(fields, "(?m)^\\s*\"appid\"\\s+\"" + AppId + "\"\\s*$") &&
                   Regex.IsMatch(fields, "(?im)^\\s*\"installdir\"\\s+\"" + Regex.Escape(game.Name) + "\"\\s*$");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static string? FindGame()
    {
        var own = new DirectoryInfo(AppContext.BaseDirectory);
        foreach (var folder in new[] { own.FullName, own.Parent?.FullName })
            if (GameExists(folder)) return folder;
        var conventional = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => Path.Combine(d.Name, "SteamLibrary"));
        foreach (var library in SteamLibrary.SteamLibraries().Concat(conventional).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.Combine(library, "steamapps", "common", GameName);
            if (GameExists(folder)) return folder;
        }
        return null;
    }

    public static ProcessStartInfo DesktopStartInfo(string? folder)
    {
        if (!GameExists(folder)) throw new IOException("Select the Steam installation of GRID Autosport.");
        var client = SteamLibrary.SteamClient();
        if (client is not null)
        {
            // The URI can stop at the standard/touch chooser. Ask Steam for its
            // default desktop entry explicitly, as the stock benchmark does.
            var start = new ProcessStartInfo(client) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(client) };
            start.ArgumentList.Add("-applaunch");
            start.ArgumentList.Add(AppId);
            return start;
        }
        return new ProcessStartInfo("steam://rungameid/" + AppId) { UseShellExecute = true };
    }

    public static string? FindProbe(string baseDirectory) =>
        new[] { Path.Combine(baseDirectory, "probe", ProbeName), Path.Combine(baseDirectory, ProbeName) }.FirstOrDefault(File.Exists);

    public static string? FindRenderer(string baseDirectory)
    {
        var dll = Path.Combine(baseDirectory, "renderer", "gasxr.dll");
        return File.Exists(dll) ? dll : null;
    }

    public static bool SessionPending(string? folder) => GameExists(folder) &&
        (File.Exists(Path.Combine(folder!, PlayLease)) || File.Exists(Path.Combine(folder!, BenchmarkLease)));

    public static string? VrLaunchProblem(string? folder, string? probe, string? renderer)
    {
        if (!GameExists(folder)) return "Choose your GRID Autosport Steam folder in Settings.";
        if (!File.Exists(renderer)) return @"The launcher package is missing renderer\gasxr.dll.";
        if (!File.Exists(probe)) return "The launcher package is missing the 32-bit OpenXR probe.";
        if (SteamLibrary.SteamClient() is null) return "The Steam client could not be located.";
        if (File.Exists(Path.Combine(folder!, BenchmarkLease))) return "Finish or restore the VR benchmark before launching the game.";
        if (File.Exists(Path.Combine(folder!, PlayLease))) return "A VR session is active or needs recovery. After the game exits, choose Restore original files.";
        if (File.Exists(Path.Combine(folder!, "gasxr.dll"))) return "A gasxr.dll is already installed in the game folder. Check that file before launching VR.";
        var executable = Path.Combine(folder!, "GRIDAutosport_avx.exe");
        if (!File.Exists(executable)) return "The Steam installation is missing GRIDAutosport_avx.exe.";
        try
        {
            VrPlaySession.EnsureIdle(folder);
            using var stream = File.OpenRead(executable);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(VrPlaySession.StockSha256, StringComparison.OrdinalIgnoreCase))
                return "This version of GRID Autosport is unsupported. Verify the game files in Steam.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return error.Message; }
        return null;
    }

    // The launcher re-runs itself as the session supervisor, so a closed launcher window
    // never strands staged game files.
    public static ProcessStartInfo VrStartInfo(string folder)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, LauncherName))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--play-vr");
        start.ArgumentList.Add("--game-dir");
        start.ArgumentList.Add(folder);
        return start;
    }

    public static string? ProbeError(int exitCode) => exitCode switch
    {
        0 => null,
        2 => "Connect your headset to the PC, then try again. The 32-bit OpenXR runtime could not find a headset.",
        124 => "The 32-bit OpenXR runtime timed out.",
        _ => "The 32-bit OpenXR probe failed with exit code " + exitCode + "."
    };

    public static ProcessStartInfo ProbeStartInfo(string? probe, bool stereo, string logFile)
    {
        if (string.IsNullOrEmpty(probe) || !File.Exists(probe)) throw new FileNotFoundException("The Win32 OpenXR probe is missing.");
        var start = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(probe) };
        if (stereo) { start.ArgumentList.Add("--session"); start.ArgumentList.Add("--seconds"); start.ArgumentList.Add("20"); }
        else start.ArgumentList.Add("--query");
        start.ArgumentList.Add("--log");
        start.ArgumentList.Add(logFile);
        return start;
    }
}
