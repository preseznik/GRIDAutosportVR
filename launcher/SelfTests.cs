using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GRIDAutosportVR;

// GRIDAutosportVR.Launcher.exe --self-test [OUTPUT]. No game, headset, Steam or worker is started and the
// player's own settings are untouched (all files go to OUTPUT\data). The window is shown off-screen
// without activation only to render tab previews to OUTPUT\launcher-*.png.
internal static class SelfTests
{
    private static int failures;

    public static int Run(string? output)
    {
        output = Path.GetFullPath(output ?? Path.Combine(AppContext.BaseDirectory, "selftest"));
        var data = Path.Combine(output, "data");
        if (Directory.Exists(data)) Directory.Delete(data, true);
        Directory.CreateDirectory(data);
        LauncherSettings.UseDataDirectory(data);
        try
        {
            Settings(data);
            Window();
            InstallationRules();
            RenderScale();
            ApplicationConfiguration.Initialize();
            Application.SetColorMode(SystemColorMode.System);
            Form(output);
        }
        catch (Exception error)
        {
            ++failures;
            Console.Error.WriteLine("FAIL (exception): " + error);
        }
        finally { LauncherSettings.UseDataDirectory(null); }
        Console.WriteLine(failures == 0 ? "Self-test passed." : $"Self-test failed: {failures} check(s).");
        return failures == 0 ? 0 : 1;
    }

    private static void Settings(string data)
    {
        var file = Path.Combine(data, "settings-test.json");
        Check(LauncherSettings.Load(file) == new Choices(false, true, 100)); // New installs: Modern interface, 100% VR scale.
        LauncherSettings.Save(new Choices(false, true, 175), file);
        Check(LauncherSettings.Load(file).VrRenderScale == 175);
        File.WriteAllText(file, "{\"vrRenderScale\": 40}");
        Check(Throws<JsonException>(() => LauncherSettings.Load(file)));
        LauncherSettings.Save(new Choices(true, false), file);
        Check(LauncherSettings.Load(file) == new Choices(true, false));
        File.WriteAllText(file, "{\"hudFollowView\": true, \"other\": 7}");
        Check(LauncherSettings.Load(file) == new Choices(true, false)); // Pre-launcher-port files start Classic.
        LauncherSettings.Save(new Choices(false, true), file);
        var saved = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Check(saved["other"]!.GetValue<int>() == 7 && !saved["hudFollowView"]!.GetValue<bool>() && saved["modernInterface"]!.GetValue<bool>());
        File.WriteAllText(file, "{\"hudFollowView\": \"yes\"}");
        Check(Throws<JsonException>(() => LauncherSettings.Load(file)));
        Check(!Directory.EnumerateFiles(data, "*.tmp").Any());
        File.Delete(file);
    }

    private static void Window()
    {
        var window = new LauncherWindow().WithCollapsed(["Headset check", "VR HUD", "Bad|title", ""]);
        Check(window.CollapsedSections == "Headset check|VR HUD");
        Check(window.Collapsed().SetEquals(["Headset check", "VR HUD"]));
        Check(window.WithCollapsed([]).CollapsedSections is null);
        Check(!new LauncherWindow(100, 100).Valid && new LauncherWindow().Valid);
        LauncherSettings.SaveWindow(window with { Width = 1000, Maximized = true });
        var loaded = LauncherSettings.LoadWindow();
        Check(loaded.Width == 1000 && loaded.Maximized && loaded.CollapsedSections == "Headset check|VR HUD");
        File.WriteAllText(LauncherSettings.WindowPath, "not json");
        Check(LauncherSettings.LoadWindow() == new LauncherWindow());
        File.Delete(LauncherSettings.WindowPath);
    }

    private static void InstallationRules()
    {
        Check(!Installation.GameExists(null) && !Installation.GameExists("relative\\folder") && !Installation.SessionPending(null));
        Check(Installation.VrLaunchProblem(null, null, null)!.Contains("Steam folder"));
        Check(Installation.ProbeError(0) is null && Installation.ProbeError(2)!.Contains("headset") && Installation.ProbeError(124)!.Contains("timed out"));
        var start = Installation.VrStartInfo(@"C:\Games\GRID Autosport");
        Check(start.ArgumentList.SequenceEqual(["--play-vr", "--game-dir", @"C:\Games\GRID Autosport"]) && start.FileName.EndsWith("GRIDAutosportVR.Launcher.exe"));
        Check(Throws<FileNotFoundException>(() => Installation.ProbeStartInfo(null, false, "x.log")));
    }

    private static readonly string[] QuestProbe =
    [
        "architecture=Win32 mode=query", "runtime=VirtualDesktopXR version=1.1.0",
        "headset=Meta Quest 3 position_tracking=1 orientation_tracking=1",
        "eye0=3072x3264 recommended_samples=1", "eye1=3072x3264 recommended_samples=1", "exit_code=0"
    ];
    internal static HeadsetResolution Quest3 => HeadsetResolution.FromProbeLog(QuestProbe)!;

    private static void RenderScale()
    {
        var quest = HeadsetResolution.FromProbeLog(QuestProbe);
        Check(quest == new HeadsetResolution("VirtualDesktopXR 1.1.0", "Meta Quest 3", 3072, 3264));
        Check(HeadsetResolution.FromProbeLog(QuestProbe.Where(l => !l.StartsWith("eye0"))) is null);
        Check(HeadsetResolution.FromProbeLog(["headset=X", "eye0=99999x100"]) is null);
        Check(quest!.LaunchSize(100) == (3072, 3264, 100) && quest.LaunchSize(50) == (1536, 1632, 50));
        // One D3D11 texture holds both eyes: 8192 per eye; 3264 x 2.51 = 8192 is the limit here.
        Check(quest.MaximumRenderScale == 251 && quest.LaunchSize(300) == (7710, 8192, 251));
        Check(quest.Summary(100).Contains("Render: 3072 × 3264 per eye at 100%") && quest.Summary(300).Contains("251% will be used"));
        quest.Save();
        Check(HeadsetResolution.Load() == quest);
        File.WriteAllText(HeadsetResolution.StorePath, "{}");
        Check(HeadsetResolution.Load() is null);
        File.Delete(HeadsetResolution.StorePath);
        Check(VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", true, (3072, 3264)).SequenceEqual(
            ["-applaunch", "255220", "-gasvrplay=0123456789abcdef0123456789abcdef", "-gasvrmode=openxr", "-gasvrhudfollow=1", "-gasvreyesize=3072x3264"]));
        Check(!VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", false, null).Any(a => a.StartsWith("-gasvreyesize")));
    }

    private static void Form(string output)
    {
        using var form = new MainForm(null, null, null) { ShowInactive = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        Check(form.Tabs.TabPages.Cast<TabPage>().Select(p => p.Text).SequenceEqual(["Launcher", "Graphics", "Settings"]));
        foreach (var name in new[] { "LaunchDesktop", "LaunchVR", "SaveSettings", "RestoreOriginalFiles", "OpenLogs", "HelpAbout", "SessionStatus", "SessionNotice" })
            Check(form.Controls.Find(name, true).Length == 1, name);
        Check(!Find<Button>(form, "LaunchVR").Enabled && !Find<Button>(form, "LaunchDesktop").Enabled && !Find<Button>(form, "SaveSettings").Enabled);
        Check(Find<SelectableStatusText>(form, "SessionStatus").Text.Contains("Steam folder"));
        var sections = form.SectionHeaders.Select(h => h.Title).ToArray();
        Check(sections.SequenceEqual(["Session", "Before you launch", "VR rendering", "VR HUD", "Paths and runtime", "Headset check", "Launcher preferences"]));
        Check(Find<Label>(form, "VrResolutionSummary").Text.Contains("Refresh headset"));
        form.UseHeadset(Quest3);
        Check(Find<Label>(form, "VrResolutionSummary").Text.Contains("Meta Quest 3"));
        form.Show();
        Application.DoEvents();
        try
        {
            // Editing a choice enables Save settings; saving writes it and disables Save again.
            var hud = Find<CheckBox>(form, "HudFollowView");
            hud.Checked = true;
            Check(Find<Button>(form, "SaveSettings").Enabled && Find<SelectableStatusText>(form, "SessionNotice").Text.Contains("Unsaved"));
            Find<Button>(form, "SaveSettings").PerformClick();
            Check(LauncherSettings.Load().HudFollowView && !Find<Button>(form, "SaveSettings").Enabled);
            Find<RenderScaleSlider>(form, "VrRenderScale").Value = 150;
            Check(Find<Button>(form, "SaveSettings").Enabled && Find<Label>(form, "VrResolutionSummary").Text.Contains("4608 × 4896 per eye at 150%"));
            Find<Button>(form, "SaveSettings").PerformClick();
            Check(LauncherSettings.Load().VrRenderScale == 150);
            // Collapsing a section is remembered by title and restored.
            var headset = form.SectionHeaders.Single(h => h.Title == "Headset check");
            form.Tabs.SelectedTab = form.Tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "Settings");
            Application.DoEvents();
            headset.PerformClick();
            Check(headset.Collapsed && form.WindowPreferences.CollapsedSections == "Headset check");
            Check(!Find<FlowLayoutPanel>(form, "HeadsetCheckActions").Visible);
            form.ApplyCollapsedSections(null);
            Check(!headset.Collapsed);
            foreach (var modern in new[] { true, false })
            {
                LauncherAppearance.Modern = modern; LauncherAppearance.Apply(form);
                foreach (var width in new[] { 900, 600 })
                {
                    form.ClientSize = new Size(LauncherAppearance.Px(form, width), LauncherAppearance.Px(form, 720));
                    foreach (TabPage page in form.Tabs.TabPages)
                    {
                        form.Tabs.SelectedTab = page;
                        form.PerformLayout(); Application.DoEvents();
                        using var preview = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
                        preview.Save(Path.Combine(output, $"launcher-{page.Text.ToLowerInvariant()}-{(modern ? "modern" : "classic")}-{width}.png"));
                    }
                }
            }
        }
        finally { form.Hide(); }
    }

    private static T Find<T>(Control root, string name) where T : Control => (T)root.Controls.Find(name, true).Single();

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Check(bool condition, string? detail = null, [CallerArgumentExpression(nameof(condition))] string? expression = null, [CallerLineNumber] int line = 0)
    {
        if (condition) return;
        ++failures;
        Console.Error.WriteLine($"FAIL line {line}: {expression}{(detail is null ? "" : " (" + detail + ")")}");
    }
}
