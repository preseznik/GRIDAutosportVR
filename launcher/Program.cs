using System.Diagnostics;
using System.Text.Json;

namespace GRIDAutosportVR;

internal static class Program
{
    private const string Usage = "Usage: GRIDAutosportVR.exe [--inspect] [--game-dir FOLDER] | (--play-vr | --restore-play) --game-dir FOLDER | --self-test [OUTPUT]" +
        " | --discover OUTPUT | --write-session --game-dir FOLDER [--settings FILE] OUTPUT | (--check-install | --check-uninstall) --game FOLDER --error-log FILE";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : null);
        if (args is ["--discover", var discovered]) return Discover(discovered);
        if (args is ["--write-session", "--game-dir", var sessionGame, var sessionOut]) return WriteSession(sessionGame, null, sessionOut);
        if (args is ["--write-session", "--game-dir", var testGame, "--settings", var testSettings, var testOut]) return WriteSession(testGame, testSettings, testOut);
        if (args is ["--check-install" or "--check-uninstall", "--game", var game, "--error-log", var errorLog])
            return CheckForInstaller(args[0] == "--check-install", game, errorLog);
        var inspect = args.Length > 0 && args[0] == "--inspect";
        var play = args.Length > 0 && args[0] == "--play-vr";
        var restore = args.Length > 0 && args[0] == "--restore-play";
        var options = inspect || play || restore ? args[1..] : args;
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
            if (play) return PlayVr(folder, probe, renderer);
            if (inspect) return Inspect(folder, probe, renderer);
            ApplicationConfiguration.Initialize();
            Application.SetColorMode(SystemColorMode.System);
            using var window = new MainForm(folder, probe, renderer);
            Application.Run(window);
            return 0;
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
    private static int PlayVr(string? folder, string? probe, string? renderer)
    {
        if (!Installation.GameExists(folder)) throw new IOException("Select the Steam installation of GRID Autosport.");
        VrPlaySession.Recover(folder);
        var problem = Installation.VrLaunchProblem(folder, probe, renderer);
        if (problem is not null) throw new IOException(problem);
        Directory.CreateDirectory(Installation.LogDirectory);
        var logFile = Path.Combine(Installation.LogDirectory, $"launch-openxr-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        using (var check = Process.Start(Installation.ProbeStartInfo(probe, false, logFile)) ?? throw new IOException("The OpenXR probe did not start."))
        {
            check.WaitForExit();
            var failure = Installation.ProbeError(check.ExitCode);
            if (failure is not null) throw new IOException(failure + Environment.NewLine + logFile);
        }
        // Render each eye at the headset's recommended size times VR render scale. Without a
        // readable size the bridge keeps the game's legacy eye size.
        // Practice / Race: the session file from the installed content and the saved choices.
        static byte[]? Session(string game, Choices choices) =>
            choices.LaunchMode == "Normal" ? null : RaceSession.SessionXml(choices, RaceSession.Load(game), Random.Shared.Next());
        var headset = HeadsetResolution.FromProbeLog(File.ReadLines(logFile));
        if (headset is null) return VrPlaySession.Run(folder!, renderer!, sessionXml: c => Session(folder!, c));
        try { headset.Save(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var scale = LauncherSettings.Load().VrRenderScale;
        var (width, height, used) = headset.LaunchSize(scale);
        return VrPlaySession.Run(folder!, renderer!, (width, height),
            $"Render {width}x{height} per eye at {used}% (requested {scale}%) of {headset.Headset} {headset.Width}x{headset.Height} via {headset.Runtime}.",
            c => Session(folder!, c));
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
