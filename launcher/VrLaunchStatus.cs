using System.Text.Json;

namespace GRIDAutosportVR;

// Where a VR launch is, for the launcher's progress window and status line (GRID2VR's
// LaunchProgress). The session supervisor (--play-vr) writes vr-launch.json at each step and
// follows the VR renderer's bridge.log once the game runs; the launcher reads it while its
// supervisor runs, and only when it names that supervisor's process.
internal sealed record VrLaunchStatus(int SupervisorId, DateTime SupervisorStartedUtc, string Stage, string Message, string? SessionId = null)
{
    internal const string Checking = "checking", Preparing = "preparing", Steam = "steam", Loading = "loading", Headset = "headset",
        Running = "running", Failed = "failed";
    private static readonly string[] Stages = [Checking, Preparing, Steam, Loading, Headset, Running, Failed];

    internal static string StorePath => Path.Combine(LauncherSettings.DataDirectory, "vr-launch.json");

    internal bool Valid => Stages.Contains(Stage) && Message.Length is > 0 and <= 600 && (SessionId is null || SessionId.Length == 32 && SessionId.All(Uri.IsHexDigit));

    // Written by the supervisor; a failure to write never stops a launch.
    internal static void Report(string stage, string message, string? sessionId = null)
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var value = new VrLaunchStatus(Environment.ProcessId, self.StartTime.ToUniversalTime(), stage, message, sessionId);
            Directory.CreateDirectory(LauncherSettings.DataDirectory);
            var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, StorePath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // The status of the supervisor process with this id and start time, or null.
    internal static VrLaunchStatus? Read(int supervisorId, DateTime supervisorStartedUtc)
    {
        try
        {
            if (!File.Exists(StorePath) || new FileInfo(StorePath).Length > 4096) return null;
            var value = JsonSerializer.Deserialize<VrLaunchStatus>(File.ReadAllText(StorePath));
            return value is { Valid: true } && value.SupervisorId == supervisorId &&
                Math.Abs((value.SupervisorStartedUtc - supervisorStartedUtc).TotalSeconds) < 1 ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // The step a VR renderer log shows, furthest first.
    internal static (string Stage, string Message) FromBridgeLog(IEnumerable<string> lines)
    {
        string? failure = null;
        bool renderer = false, runtime = false, waiting = false, focused = false;
        foreach (var line in lines)
        {
            if (line.Contains("] VR failed: ", StringComparison.Ordinal)) failure ??= line[(line.IndexOf("] VR failed: ", StringComparison.Ordinal) + 13)..].Trim();
            else if (line.Contains("XR session state=FOCUSED", StringComparison.Ordinal)) focused = true;
            else if (line.Contains("XR session state=", StringComparison.Ordinal) || line.Contains(": waiting_for_session_ready", StringComparison.Ordinal)) waiting = true;
            else if (line.Contains("] OpenXR runtime ", StringComparison.Ordinal)) runtime = true;
            else if (line.Contains("native renderer configured:", StringComparison.Ordinal)) renderer = true;
        }
        if (failure is not null)
            return (Failed, "VR could not start (" + (failure.Length > 200 ? failure[..200] : failure) + "). Quit the game, then open Logs for details.");
        if (focused) return (Running, "VR is running.");
        if (waiting) return (Headset, "Waiting for the headset. Put on your headset…");
        if (runtime) return (Headset, "Connecting to the headset…");
        if (renderer) return (Loading, "Starting the VR renderer…");
        return (Loading, "GRID Autosport is loading…");
    }
}

internal sealed record LaunchProgress(string Message, bool Finished = false, bool Failed = false);

// GRID2VR's launch progress window: the current step and elapsed time, shown without taking focus
// from the game. It closes when VR is running; Hide lets the launch continue.
internal sealed class LaunchProgressForm : LauncherForm
{
    private readonly Label step = new() { Name = "LaunchStep", Dock = DockStyle.Top, Height = 92, Text = "Saving launch settings…", AutoEllipsis = true };
    private readonly ProgressBar progress = new() { Name = "LaunchProgressBar", Dock = DockStyle.Top, Height = 16, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
    private readonly Label elapsed = new() { Name = "LaunchElapsed", Dock = DockStyle.Top, Height = 32, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label hint = new() { Name = "LaunchHint", Dock = DockStyle.Top, Height = 48 };
    private readonly Button dismiss = new() { Name = "HideLaunchProgress", Text = "Hide", Dock = DockStyle.Right, Width = 96, DialogResult = DialogResult.Cancel };
    private readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    internal Panel Content { get; } = new() { Dock = DockStyle.Fill, Padding = new Padding(20) };
    internal string Step => step.Text;
    internal bool Stopped { get; private set; }
    protected override bool ShowWithoutActivation => true;

    internal LaunchProgressForm()
    {
        Text = "Launching GRID Autosport in VR";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(480, 290); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        hint.Text = "This closes when VR starts. You can hide it and let loading continue.";
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        buttons.Controls.Add(dismiss);
        Content.Controls.Add(hint); Content.Controls.Add(elapsed); Content.Controls.Add(progress); Content.Controls.Add(step); Content.Controls.Add(buttons);
        Controls.Add(Content); CancelButton = dismiss;
        dismiss.Click += (_, _) => Close();
        timer.Tick += (_, _) => UpdateElapsed();
        Shown += (_, _) => timer.Start();
        UpdateElapsed();
    }

    internal void DisplayProgress(LaunchProgress update)
    {
        if (IsDisposed || Stopped) return;
        if (update.Finished) { Close(); return; }
        step.Text = update.Message;
        if (!update.Failed) return;
        Stopped = true;
        timer.Stop(); watch.Stop(); UpdateElapsed();
        Text = "GRID Autosport VR launch stopped";
        progress.Style = ProgressBarStyle.Continuous; progress.Value = 0;
        hint.Text = "Close this message to return to the launcher."; dismiss.Text = "Close";
    }

    private void UpdateElapsed() => elapsed.Text = $"Elapsed: {(int)watch.Elapsed.TotalMinutes}:{watch.Elapsed.Seconds:00}";
    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
