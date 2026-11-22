using System.Runtime.CompilerServices;

namespace GRIDAutosportVR;

// The launch progress window and its status feed (VrLaunchStatus.cs), as GRID2VR's LaunchProgressTests.
internal static class LaunchProgressTests
{
    private static Action<bool, string>? report;
    private static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null) => report!(value, "Launch progress: " + expression);

    // A VR renderer log from an xr-sim race, in order.
    private static readonly string[] Log =
    [
        "[450682953] D3D11 creation hr=00000000 nativeAdapter=1 device=0506AEC4",
        "[450683953] native renderer configured: 800x600 device=28E1069C context=28C809C0",
        "[450684828] OpenXR runtime xr-sim version 281474976710656",
        "[450684859] begin frame 1: waiting_for_session_ready",
        "[450685078] XR session state=READY frame=41 gameForeground=0",
        "[450685093] XR session state=FOCUSED frame=44 gameForeground=0",
    ];

    internal static void Run(string output, Action<bool, string> check)
    {
        report = check;
        Check(VrLaunchStatus.FromBridgeLog([]) == (VrLaunchStatus.Loading, "GRID Autosport is loading…"));
        Check(VrLaunchStatus.FromBridgeLog(Log[..2]) == (VrLaunchStatus.Loading, "Starting the VR renderer…"));
        Check(VrLaunchStatus.FromBridgeLog(Log[..3]) == (VrLaunchStatus.Headset, "Connecting to the headset…"));
        Check(VrLaunchStatus.FromBridgeLog(Log[..4]).Message == "Waiting for the headset. Put on your headset…");
        Check(VrLaunchStatus.FromBridgeLog(Log[..5]).Stage == VrLaunchStatus.Headset);
        Check(VrLaunchStatus.FromBridgeLog(Log) == (VrLaunchStatus.Running, "VR is running."));
        var failed = VrLaunchStatus.FromBridgeLog([.. Log[..3], "[450684843] VR failed: xrCreateInstance(XrResult=-32)", "[450684843] begin frame 300: reinitialize_bridge_after_failure"]);
        Check(failed.Stage == VrLaunchStatus.Failed && failed.Message.StartsWith("VR could not start (xrCreateInstance(XrResult=-32))."), failed.Message);
        // A failure wins over a later focus, and a long reason is shortened.
        Check(VrLaunchStatus.FromBridgeLog(["[1] VR failed: " + new string('x', 400), .. Log]).Message.Length < 300);

        // The supervisor's file names its own process; another process's is ignored.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var started = self.StartTime.ToUniversalTime();
        Check(VrLaunchStatus.Read(Environment.ProcessId, started) is null);
        VrLaunchStatus.Report(VrLaunchStatus.Steam, "Steam is starting GRID Autosport…", new string('a', 32));
        Check(VrLaunchStatus.Read(Environment.ProcessId, started) is { Stage: VrLaunchStatus.Steam, SessionId: { Length: 32 } });
        Check(VrLaunchStatus.Read(Environment.ProcessId + 1, started) is null && VrLaunchStatus.Read(Environment.ProcessId, started.AddMinutes(-1)) is null);
        File.WriteAllText(VrLaunchStatus.StorePath, File.ReadAllText(VrLaunchStatus.StorePath).Replace("\"steam\"", "\"done\""));
        Check(VrLaunchStatus.Read(Environment.ProcessId, started) is null);
        VrLaunchStatus.Report(VrLaunchStatus.Loading, "x", "../not-an-id");
        Check(VrLaunchStatus.Read(Environment.ProcessId, started) is null);
        File.Delete(VrLaunchStatus.StorePath);

        using (var form = new LaunchProgressForm { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) })
        {
            form.Show();
            form.DisplayProgress(new("Waiting for the headset. Put on your headset…"));
            Check(form.Step == "Waiting for the headset. Put on your headset…" && !form.Stopped);
            form.DisplayProgress(new("VR could not start (xrCreateInstance(XrResult=-32)). Quit the game, then open Logs for details.", Failed: true));
            Check(form.Stopped && form.Text == "GRID Autosport VR launch stopped" && form.Controls.Find("HideLaunchProgress", true).Single().Text == "Close");
            form.DisplayProgress(new("Later steps do not replace a failure."));
            Check(form.Step.StartsWith("VR could not start"));
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, "launcher-launch-progress.png"));
        }
        using (var form = new LaunchProgressForm { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) })
        {
            form.Show();
            var shown = form.Icon!;
            form.DisplayProgress(new("VR is running.", Finished: true));
            Check(!form.Visible && form.IsDisposed);
            // The window closes as VR starts, when the game's display change can still send it
            // WM_DPICHANGED: the icon it showed must stay usable.
            Check(shown.Handle != IntPtr.Zero && ReferenceEquals(shown, Branding.Icon));
        }
    }
}
