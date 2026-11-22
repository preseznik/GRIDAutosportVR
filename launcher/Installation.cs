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
    internal const string LauncherName = "GRIDAutosportVR.exe";
    // Held by the VR session supervisor; the installer refuses to run while it exists.
    internal const string SessionMutex = @"Local\GRIDAutosportVR-255220";

    public static string LogDirectory => Path.Combine(LauncherSettings.DataDirectory, "logs");

    // A usable installation: the Steam version, matched to its library manifest (Steam opens the
    // registered application), or the 2014 GRIDAutosport.exe in any folder (GameBuilds).
    public static bool GameExists(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return false;
        return SteamGame(folder) || GameBuilds.Detect(folder) == GameBuilds.Classic2014;
    }

    private static bool SteamGame(string folder)
    {
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

    // Why a folder the player chose is not a usable installation, in words they can act on.
    public static string FolderProblem(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            return "Choose your GRID Autosport folder.";
        if (!File.Exists(Path.Combine(folder, "GRIDAutosport.exe")))
            return "This folder has no GRIDAutosport.exe. Choose your GRID Autosport folder.";
        if (!File.Exists(Path.Combine(folder, "GRIDAutosport_avx.exe")))
            return "This GRIDAutosport.exe is a version GRID Autosport VR does not support. It supports the Steam version and GRIDAutosport.exe 1.0.103.1840 from 2014.";
        return "This folder is not a GRID Autosport installation that Steam knows about (no matching appmanifest_255220.acf in its Steam library). " +
               "Choose the folder Steam installed the game in (steamapps\\common\\GRID Autosport).";
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
        if (GameBuilds.Detect(folder) is { ThroughSteam: false } direct)
        {
            var start = new ProcessStartInfo(Path.Combine(folder!, direct.Executable)) { UseShellExecute = false, WorkingDirectory = folder };
            foreach (var arg in direct.Arguments) start.ArgumentList.Add(arg);
            return start;
        }
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

    // The renderer a build stages, beside renderer\gasxr.dll (GameBuild.RendererFile).
    public static string RendererFor(GameBuild build, string renderer) => Path.Combine(Path.GetDirectoryName(renderer)!, build.RendererFile);

    public static bool SessionPending(string? folder) => GameExists(folder) &&
        (File.Exists(Path.Combine(folder!, PlayLease)) || File.Exists(Path.Combine(folder!, BenchmarkLease)));

    // desktop: a desktop Practice / Race session, which needs no OpenXR probe.
    public static string? VrLaunchProblem(string? folder, string? probe, string? renderer, bool desktop = false)
    {
        if (!GameExists(folder)) return folder is null ? "Choose your GRID Autosport Steam folder in Settings." : FolderProblem(folder);
        if (!File.Exists(renderer)) return @"The launcher package is missing renderer\gasxr.dll.";
        if (!desktop && !File.Exists(probe)) return "The launcher package is missing the 32-bit OpenXR probe.";
        var build = GameBuilds.Detect(folder);
        if (build?.ThroughSteam != false && SteamLibrary.SteamClient() is null) return "The Steam client could not be located.";
        if (File.Exists(Path.Combine(folder!, BenchmarkLease))) return "Finish or restore the VR benchmark before launching the game.";
        if (File.Exists(Path.Combine(folder!, PlayLease))) return "A VR session is active or needs recovery. After the game exits, choose Restore original files.";
        if (File.Exists(Path.Combine(folder!, "gasxr.dll"))) return "A gasxr.dll is already installed in the game folder. Check that file before launching VR.";
        if (build is null)
            return File.Exists(Path.Combine(folder!, GameBuilds.Steam.Executable))
                ? "This version of GRID Autosport is unsupported. Verify the game files in Steam."
                : "The Steam installation is missing GRIDAutosport_avx.exe. Update GRID Autosport in Steam, or verify its files.";
        if (build.ProxyBridge)
        {
            if (!File.Exists(RendererFor(build, renderer!))) return @"The launcher package is missing renderer\" + build.RendererFile + ".";
            if (File.Exists(Path.Combine(folder!, build.BridgeFile)))
                return "A d3d11.dll is already in the game folder, perhaps from another mod such as ReShade. GRID Autosport VR puts its own there for each session: move that file elsewhere before launching.";
        }
        try
        {
            VrPlaySession.EnsureIdle(folder);
            if (GameBuilds.CachedHash(Path.Combine(folder!, build.Executable)) != build.StockSha256)
                return build.ThroughSteam ? "This version of GRID Autosport is unsupported. Verify the game files in Steam."
                    : build.Executable + " is not the original file. Put the original back before launching VR.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return error.Message; }
        return null;
    }

    // The launcher re-runs itself as the session supervisor, so a closed launcher window
    // never strands staged game files.
    // approved: OpenXR layers the player just chose to disable (VrApiLayers.cs); remember: those of
    // them to remember once the headset check works. Remembered ones the supervisor reads itself.
    public static ProcessStartInfo VrStartInfo(string folder, IReadOnlyList<VrLayerBypass>? approved = null, IReadOnlyList<VrLayerBypass>? remember = null)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, LauncherName))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--play-vr");
        start.ArgumentList.Add("--game-dir");
        start.ArgumentList.Add(folder);
        if (approved is { Count: > 0 }) { start.ArgumentList.Add(XrLayerBypassArgument); start.ArgumentList.Add(VrApiLayers.Encode(approved)); }
        if (remember is { Count: > 0 }) { start.ArgumentList.Add(RememberXrLayersArgument); start.ArgumentList.Add(VrApiLayers.Encode(remember)); }
        return start;
    }

    // The session supervisor for desktop Launch with Practice / Race (Program.PlayDesktop).
    public static ProcessStartInfo DesktopSessionStartInfo(string folder)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, LauncherName))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--play-desktop");
        start.ArgumentList.Add("--game-dir");
        start.ArgumentList.Add(folder);
        return start;
    }

    public const string XrLayerBypassArgument = "--xr-layer-bypass", RememberXrLayersArgument = "--remember-xr-layers";

    public static string? ProbeError(int exitCode) => exitCode switch
    {
        0 => null,
        2 => "Connect your headset to the PC, then try again. The 32-bit OpenXR runtime could not find a headset.",
        124 => "The 32-bit OpenXR runtime timed out.",
        _ => "The 32-bit OpenXR probe failed with exit code " + exitCode + "."
    };

    // layers: approved, revalidated OpenXR layer bypasses (VrApiLayers.Select).
    public static ProcessStartInfo ProbeStartInfo(string? probe, bool stereo, string logFile, string runtime = OpenXrRuntimes.Default, IReadOnlyList<VrLayerBypass>? layers = null)
    {
        if (string.IsNullOrEmpty(probe) || !File.Exists(probe)) throw new FileNotFoundException("The Win32 OpenXR probe is missing.");
        var start = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(probe) };
        OpenXrRuntimes.Apply(start, runtime);
        VrApiLayers.ApplyToProbe(start, layers ?? []);
        if (stereo) { start.ArgumentList.Add("--session"); start.ArgumentList.Add("--seconds"); start.ArgumentList.Add("20"); }
        else start.ArgumentList.Add("--query");
        start.ArgumentList.Add("--log");
        start.ArgumentList.Add(logFile);
        return start;
    }
}
