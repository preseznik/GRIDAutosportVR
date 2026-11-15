using System.Diagnostics;
using System.Text.Json;

namespace GRIDAutosportVR;

internal static class Program
{
    private const string Usage = "Usage: GRIDAutosportVR.exe [--inspect] [--game-dir FOLDER] | --play-vr --game-dir FOLDER [--xr-layer-bypass B64] [--remember-xr-layers B64] | --restore-play --game-dir FOLDER | --self-test [OUTPUT]" +
        " | --discover OUTPUT | --end-session --game-dir FOLDER | --write-session --game-dir FOLDER [--settings FILE] OUTPUT | --write-input-maps --game-dir FOLDER --controls FILE OUTPUT | (--check-install | --check-uninstall) --game FOLDER --error-log FILE";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : null);
        if (args is ["--discover", var discovered]) return Discover(discovered);
        if (args is ["--end-session", "--game-dir", var endGame])
        {
            // The launcher's End session from a command line: close the game running in that folder.
            Console.WriteLine(VrPlaySession.EndSession(Path.TrimEndingDirectorySeparator(Path.GetFullPath(endGame)), TimeSpan.FromSeconds(15))
                ? "Closed GRID Autosport." : "GRID Autosport is not running from that folder.");
            return 0;
        }
        if (args is ["--write-session", "--game-dir", var sessionGame, var sessionOut]) return WriteSession(sessionGame, null, sessionOut);
        if (args is ["--write-session", "--game-dir", var testGame, "--settings", var testSettings, var testOut]) return WriteSession(testGame, testSettings, testOut);
        if (args is ["--write-input-maps", "--game-dir", var mapsGame, "--controls", var mapsControls, var mapsOut])
        {
            // The input presets the next VR launch would stage for these driving controls; nothing is staged.
            try
            {
                var changes = SessionInputMaps.Plan(Path.TrimEndingDirectorySeparator(Path.GetFullPath(mapsGame)), DrivingControls.LoadFile(mapsControls));
                foreach (var change in changes)
                {
                    var target = Path.Combine(Path.GetFullPath(mapsOut), change.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, change.Content);
                    Console.WriteLine(change.RelativePath);
                }
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(error.Message); return 1; }
        }
        if (args is ["--check-install" or "--check-uninstall", "--game", var game, "--error-log", var errorLog])
            return CheckForInstaller(args[0] == "--check-install", game, errorLog);
        var inspect = args.Length > 0 && args[0] == "--inspect";
        var play = args.Length > 0 && args[0] == "--play-vr";
        var restore = args.Length > 0 && args[0] == "--restore-play";
        var options = inspect || play || restore ? args[1..] : args;
        // Launch VR after the OpenXR layer prompt: the layers the player chose to disable (VrApiLayers.cs).
        IReadOnlyList<VrLayerBypass> approved = [], remember = [];
        try
        {
            while (play && options.Length >= 4 && options[^2] is Installation.XrLayerBypassArgument or Installation.RememberXrLayersArgument)
            {
                var layers = VrApiLayers.Decode(options[^1]);
                if (options[^2] == Installation.XrLayerBypassArgument && approved.Count == 0) approved = layers;
                else if (options[^2] == Installation.RememberXrLayersArgument && remember.Count == 0) remember = layers;
                else throw new IOException("Repeated OpenXR layer approval.");
                options = options[..^2];
            }
        }
        catch (IOException error) { Console.Error.WriteLine(error.Message); return 64; }
        if (((play || restore) && options.Length != 2) || (options.Length != 0 && (options.Length != 2 || options[0] != "--game-dir")))
        {
            Console.Error.WriteLine(Usage);
            return 64;
        }
        try
        {
            var folder = options.Length == 2 ? options[1] : Installation.FindGame();
            if (restore)
            {
                Console.WriteLine(VrPlaySession.Recover(folder) ? "Restored the stock game files." : "No ordinary VR play session to restore.");
                return 0;
            }
            var probe = Installation.FindProbe(AppContext.BaseDirectory);
            var renderer = Installation.FindRenderer(AppContext.BaseDirectory);
            if (play) return PlayVr(folder, probe, renderer, approved, remember);
            if (inspect) return Inspect(folder, probe, renderer);
            ApplicationConfiguration.Initialize();
            Application.SetColorMode(SystemColorMode.System);
            using var window = new MainForm(folder, probe, renderer);
            Application.Run(window);
            return 0;
        }
        catch (VrLayerBlockedException error) when (play)
        {
            Console.Error.WriteLine(error.Message);
            return VrLayerBlockedException.ExitCode;
        }
        catch (Exception error)
        {
            if (inspect || play || restore) Console.Error.WriteLine(error.Message);
            else MessageBox.Show(error.Message, Branding.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    // Installer helpers (installer/GRIDAutosportVR.iss): suggest the Steam game folder, then
    // refuse to install or uninstall while the game runs or a VR session's files are staged.
    private static int Discover(string output)
    {
        try
        {
            File.WriteAllText(output, Installation.FindGame() ?? "", new System.Text.UTF8Encoding(false));
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 1; }
    }

    // Diagnostics and the xr-sim harness (capture/xrsim_play.py --session): the Practice / Race
    // session file the next Launch VR would use, from the saved choices. Nothing is staged.
    private static int WriteSession(string game, string? settings, string output)
    {
        try
        {
            var choices = LauncherSettings.Load(settings);
            var catalog = RaceSession.Load(game);
            File.WriteAllBytes(output, RaceSession.SessionXml(choices, catalog, Random.Shared.Next()));
            Console.WriteLine(RaceSession.Summary(choices, catalog));
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    internal static string? InstallProblem(bool install, string game)
    {
        var valid = Installation.GameExists(game);
        if (install && !valid)
            return "This folder is not a Steam installation of GRID Autosport. Choose the folder that contains GRIDAutosport.exe.";
        if (!valid) return null; // The game was moved or removed: nothing of ours can be staged there.
        if (Installation.SessionPending(game) || File.Exists(Path.Combine(game, "gasxr.dll")))
            return "A GRID Autosport VR session is active or was interrupted. Close the game, open GRID Autosport VR and choose Restore original files first.";
        try { VrPlaySession.EnsureIdle(game); }
        catch (IOException error) { return error.Message; }
        return null;
    }

    private static int CheckForInstaller(bool install, string game, string errorLog)
    {
        var problem = InstallProblem(install, Path.TrimEndingDirectorySeparator(game));
        if (problem is null) return 0;
        try { File.WriteAllText(errorLog, problem, new System.Text.UTF8Encoding(false)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return 1;
    }

    // The session supervisor the launcher window starts for Launch VR.
    private static int PlayVr(string? folder, string? probe, string? renderer, IReadOnlyList<VrLayerBypass> approved, IReadOnlyList<VrLayerBypass> remember)
    {
        if (!Installation.GameExists(folder)) throw new IOException("Select the Steam installation of GRID Autosport.");
        VrPlaySession.Recover(folder);
        var problem = Installation.VrLaunchProblem(folder, probe, renderer) ?? OpenXrRuntimes.Problem(LauncherSettings.Load().Runtime);
        if (problem is not null) throw new IOException(problem);
        Directory.CreateDirectory(Installation.LogDirectory);
        var logFile = Path.Combine(Installation.LogDirectory, $"launch-openxr-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        // OpenXR add-on layers to disable for this session (VrApiLayers.cs): remembered choices and
        // the launcher prompt's, each still matching the installed registration.
        VrApiLayers.ClearRecovery();
        var catalog = VrApiLayers.Catalog();
        var layers = VrApiLayers.Select([.. VrApiLayers.Remembered(), .. approved, .. remember], catalog);
        using (var check = Process.Start(Installation.ProbeStartInfo(probe, false, logFile, LauncherSettings.Load().Runtime, layers)) ?? throw new IOException("The OpenXR probe did not start."))
        {
            check.WaitForExit();
            var failure = Installation.ProbeError(check.ExitCode);
            if (failure is not null)
            {
                var report = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
                if (VrApiLayers.Recovery(report, catalog, layers) is { } recovery)
                {
                    // The launcher window names the layers and offers to disable them, then launches again.
                    VrApiLayers.SaveRecovery(recovery);
                    throw new VrLayerBlockedException(recovery.Message + " Launch VR to review it." + Environment.NewLine + logFile);
                }
                if (VrApiLayers.Failures(report) is { Count: > 0 } failures)
                    failure = VrApiLayers.Describe(failures) + " No supported automatic layer bypass is available; disable or repair that OpenXR add-on.";
                throw new IOException(failure + Environment.NewLine + logFile);
            }
        }
        // Remembered only by choice, and only now that the headset check worked with them disabled.
        if (layers.Where(layer => remember.Any(layer.SameIdentity)).ToList() is { Count: > 0 } kept)
        {
            try { VrApiLayers.Remember(kept); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("The OpenXR layer bypass works for this session, but could not be remembered: " + error.Message); }
        }
        // Render each eye at the headset's recommended size times VR render scale. Without a
        // readable size the bridge keeps the game's legacy eye size.
        // Practice / Race: the session file from the installed content and the saved choices.
        static byte[]? Session(string game, Choices choices) =>
            choices.LaunchMode == "Normal" ? null : RaceSession.SessionXml(choices, RaceSession.Load(game), Random.Shared.Next());
        var headset = HeadsetResolution.FromProbeLog(File.ReadLines(logFile));
        if (headset is null) return VrPlaySession.Run(folder!, renderer!, sessionXml: c => Session(folder!, c), layers: layers);
        try { headset.Save(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var scale = LauncherSettings.Load().VrRenderScale;
        var (width, height, used) = headset.LaunchSize(scale);
        return VrPlaySession.Run(folder!, renderer!, (width, height),
            $"Render {width}x{height} per eye at {used}% (requested {scale}%) of {headset.Headset} {headset.Width}x{headset.Height} via {headset.Runtime}.",
            c => Session(folder!, c), layers);
    }

    private static int Inspect(string? folder, string? probe, string? renderer)
    {
        var valid = Installation.GameExists(folder);
        var launch = valid ? Installation.DesktopStartInfo(folder) : null;
        var vr = valid ? Installation.VrStartInfo(folder!) : null;
        var vrProblem = Installation.VrLaunchProblem(folder, probe, renderer);
        var eyeTest = probe is not null ? Installation.ProbeStartInfo(probe, true, "session.log").ArgumentList.ToArray() : null;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            gameFolder = folder, gameDetected = valid, desktopTarget = launch?.FileName, desktopArguments = launch?.ArgumentList.ToArray(),
            probePath = probe, eyeTestArguments = eyeTest, rendererPath = renderer, vrLaunchAvailable = vrProblem is null, vrLaunchProblem = vrProblem,
            vrTarget = vr?.FileName, vrArguments = vr?.ArgumentList.ToArray(), runtimeCheck = "on launch",
            settingsFile = LauncherSettings.SettingsPath, hudFollowView = LauncherSettings.Load().HudFollowView,
            launchMode = LauncherSettings.Load().LaunchMode, session = valid ? RaceSession.Summary(LauncherSettings.Load(), RaceSession.Load(folder!)) : null,
            installedCars = valid ? RaceSession.Load(folder!).Cars.Count : 0, installedTracks = valid ? RaceSession.Load(folder!).Tracks.Count : 0,
            version = BuildInfo.Version, channel = BuildInfo.Channel
        }));
        return valid ? 0 : 2;
    }
}
