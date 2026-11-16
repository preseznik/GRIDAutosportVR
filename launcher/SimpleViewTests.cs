using System.Runtime.CompilerServices;

namespace GRIDAutosportVR;

// The Simple / Advanced view (MainForm.Views.cs), as GRID2VR's SimpleViewFollowupTests.
internal static class SimpleViewTests
{
    private static Action<bool, string>? report;
    private static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null) => report!(value, "Simple view: " + expression);

    internal static void Run(string output, Action<bool, string> check)
    {
        report = check;
        File.Delete(LauncherSettings.SettingsPath); File.Delete(LauncherSettings.WindowPath);
        // New installations start in Simple; saved settings without a view preference keep Advanced.
        Check(!MainForm.UseAdvancedView(new()));
        LauncherSettings.Save(new Choices());
        Check(MainForm.UseAdvancedView(new()) && !MainForm.UseAdvancedView(new(AdvancedView: false)));
        File.Delete(LauncherSettings.SettingsPath);
        Check(MainForm.UseAdvancedView(new(AdvancedView: true)));

        var confirm = false; var asked = 0;
        using var form = new MainForm(null, null, null) { ShowInactive = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        form.ConfirmAdvancedView = () => { ++asked; return confirm; };
        form.UseCatalog(new([new("sls", "Mercedes-Benz SLS AMG GT3", "Endurance", ["user", "03"])],
            [new("circuits/bathurst/route_0", "circuits", "bathurst", "route_0", "Mount Panorama — route 0")]));
        form.Show();
        Application.DoEvents();
        T Find<T>(string name) where T : Control => (T)form.Controls.Find(name, true).Single();
        bool Shown(string name) => form.Controls.Find(name + "Row", true).Single().Visible;
        try
        {
            Check(!form.AdvancedView && form.Tabs.TabPages.Cast<TabPage>().Select(p => p.Text).SequenceEqual(["Quick start"]));
            foreach (var name in new[] { "VrRuntime", "LaunchMode", "GraphicsPreset", "RecommendGraphics", "VrMsaa", "LastVrPerformance", "VrRenderScale", "RecenterKey", "DrivingControls", "HudShow", "SimpleSeat", "LaunchVR" })
                Check(form.Controls.Find(name, true).Length == 1, name);
            Check(form.Controls.Find("Shadows", true).Length == 0 && form.Controls.Find("ForceFeedbackOverride", true).Length == 0);
            Check(form.SectionHeaders.Where(h => h.Parent is not null && h.FindForm() == form).Select(h => h.Title).SequenceEqual(["Play", "Performance", "Controls & comfort"]));
            // Only the rows that apply to the launch mode are shown.
            Check(!Shown("SessionCar") && !Shown("Opponents") && Shown("LaunchMode"));
            Find<ComboBox>("LaunchMode").SelectedIndex = 1;
            Check(Shown("SessionCar") && Shown("Laps") && Shown("VrCockpit") && !Shown("Opponents") && Find<Label>("SessionSummary").Visible);
            Find<ComboBox>("LaunchMode").SelectedIndex = 2;
            Check(Shown("Opponents") && Shown("OpponentCars"));
            // Edits in Simple are the same choices as in Advanced.
            Find<RenderScaleSlider>("VrRenderScale").Value = 120;
            Check(Find<Button>("SaveSettings").Enabled);
            // Advanced asks first; Cancel keeps Simple and the edit.
            form.SelectView(true);
            Check(asked == 1 && !form.AdvancedView && form.Tabs.TabPages.Count == 1 && Find<RenderScaleSlider>("VrRenderScale").Value == 120);
            confirm = true;
            form.SelectView(true);
            Check(asked == 2 && form.AdvancedView && form.Tabs.TabPages.Cast<TabPage>().Select(p => p.Text).SequenceEqual(["Launcher", "Graphics", "Controls", "Settings"]));
            Check(LauncherSettings.LoadWindow().AdvancedView == true);
            // Every control is back in its Advanced place and order, shown, with the edit kept.
            var scaleRow = form.Controls.Find("VrRenderScaleRow", true).Single();
            Check(scaleRow.Parent!.Name == "GraphicsContent" || scaleRow.Parent is VerticalStack && scaleRow.FindForm() == form);
            Check(scaleRow.Parent!.Controls.GetChildIndex(scaleRow) < scaleRow.Parent.Controls.GetChildIndex(Find<Button>("RefreshHeadset")));
            Check(Find<ComboBox>("GraphicsPreset").Parent!.Parent!.Controls.GetChildIndex(Find<ComboBox>("GraphicsPreset").Parent!) <
                  Find<ComboBox>("GraphicsPreset").Parent!.Parent!.Controls.GetChildIndex(form.Controls.Find("GraphicsPresetActions", true).Single()));
            Check(Shown("SessionCar") && Find<RenderScaleSlider>("VrRenderScale").Value == 120 && form.Controls.Find("Shadows", true).Length == 1);
            Check(Find<ComboBox>("LaunchMode").Parent!.Parent == form.Controls.Find("SessionCarRow", true).Single().Parent);
            // Back to Simple needs no confirmation; Seat adjustment… opens Controls → Seat in Advanced.
            form.SelectView(false);
            Check(asked == 2 && !form.AdvancedView && LauncherSettings.LoadWindow().AdvancedView == false);
            form.Refresh();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(output, "launcher-simple-view.png"));
            }
            Find<Button>("SimpleSeat").PerformClick();
            Check(form.AdvancedView && form.Tabs.SelectedTab?.Name == "ControlsPage" &&
                  ((TabControl)form.Controls.Find("ControlTabs", true).Single()).SelectedTab?.Text == "Seat");
        }
        finally { form.Close(); }
        File.Delete(LauncherSettings.SettingsPath); File.Delete(LauncherSettings.WindowPath);
    }
}
