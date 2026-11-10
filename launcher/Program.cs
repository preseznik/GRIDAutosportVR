using System.Diagnostics;
using System.Text.Json;

namespace GRIDAutosportVR;

internal static class Program
{
    private const string Usage = "Usage: GRIDAutosportVR.Launcher.exe [--inspect] [--game-dir FOLDER] | (--play-vr | --restore-play) --game-dir FOLDER | --self-test [OUTPUT]";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : null);
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
        return VrPlaySession.Run(folder!, renderer!);
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
            version = BuildInfo.Version, channel = BuildInfo.Channel
        }));
        return valid ? 0 : 2;
    }
}
