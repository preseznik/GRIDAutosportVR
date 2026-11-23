using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace GRIDAutosportVR;

// Graphics presets (GraphicsCatalog, GraphicsRecommendation, VrPerformance, MainForm.Graphics):
// GRID2VR's GraphicsPresetTests and GraphicsRecommendationTests, with Autosport's settings file.
internal static class GraphicsPresetTests
{
    private static Action<bool, string>? report;
    private static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null) => report!(value, "Graphics: " + expression);
    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

    // A player's hardware_settings_config.xml as GRID Autosport writes it (Ultra, soft ambient
    // occlusion on, 8× MSAA, 800 × 600 window).
    internal const string Fixture = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <hardware_settings_config version="21" deviceId="0x2702">
        	<cpu>
        		<threadStrategy workerMapFile="system/workerMap8Core.xml" forceFeedbackProcessor="6" dvdStorageProcessor="7" dataSetMonitorProcessor="4" renderProcessor="0" updateProcessor="2" fileStreamProcessor="5" />
        	</cpu>
        	<audio_card>
        		<dynamic_range value="high" />
        		<eq value="flat" />
        		<ai_count value="medium" />
        	</audio_card>
        	<graphics_card firstBoot="true" rating="2">
        		<directx forcedx10="false" />
        		<advanced forward="true" global_illumination="true" />
        		<intel tree_aoit="false" particle_avsm="false" />
        		<resolution width="800" height="600" aspect="auto" fullscreen="false" vsync="0" multisampling="8xmsaa">
        			<refreshRate rate="120" />
        		</resolution>
        		<gamma level="0.8" />
        		<avsm enabled="true" />
        	</graphics_card>
        	<shadows enabled="true" size="4096" maskQuality="2" particles="true" />
        	<advanced_fog enabled="true" />
        	<particles enabled="true" wind="true" dynamicRes="false" lowResScalar="2" nearFadeBias="0" />
        	<crowd enabled="true" detail="4" />
        	<cloth enabled="true" />
        	<groundcover mode="ultra" clutter="true" />
        	<objects lod="1.5" maxlod="0" />
        	<trees lod="3.0" maxlod="0" meshes="true" />
        	<vehicles characterQuality="4" lodQuality="3" />
        	<track quality="2" />
        	<envmap faces="6" size="1024" drawallobjects="true" scaleminsize="0.1" mindistance="200.0" />
        	<water update="true" detail="3" scale="2" drawallobjects="true" tessellation="true" />
        	<mirrors enabled="false" width="256" height="64" car_maxlod="4" />
        	<skidmarks enabled="true" />
        	<dynamic_ambient_occ enabled="true" quality="3" />
        	<dynamic_ambient_occ_soft enabled="true" />
        	<night_lighting volumes="true" lights="0" shadows="true" onlyHeadlights="false" shadowSize="2048" />
        	<textures resolution="2" />
        	<shaders quality="2" low_quality="false" />
        	<anisotropic min="4" max="16" />
        	<physics environmentalDamage="true" vehicleDamage="true" />
        </hardware_settings_config>

        """;

    internal static void UseFixture(string data)
    {
        var path = Path.Combine(data, "hardwaresettings", "hardware_settings_config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Fixture.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
        GraphicsCatalog.UseConfig(path);
    }

    internal static void Run(string data, string output, Action<bool, string> check)
    {
        report = check;
        Catalog();
        SettingsFile();
        Choices();
        Recommendations();
        Performance(data);
        Form(output);
    }

    private static void Catalog()
    {
        Check(GraphicsCatalog.Categories.Count == 23 && GraphicsCatalog.Presets.Select(p => p.Id).SequenceEqual(["ultralow", "low", "medium", "high", "ultra"]));
        Check(GraphicsCatalog.Categories.Single(c => c.Field == "IntelAoit").Choices.Select(c => c.Id).SequenceEqual(["off"]));
        Check(GraphicsCatalog.Preset("ultra").Values["Shadows"] == "ultra" && GraphicsCatalog.Preset("ultra").Values["GroundCover"] == "ultra"); // DirectX 11 cases.
        foreach (var id in new[] { GraphicsCatalog.GameSettings, GraphicsCatalog.Balanced, GraphicsCatalog.Potato, "ultralow", "low", "medium", "high", "ultra" })
        {
            var (quality, msaa, scale) = GraphicsCatalog.ApplyPreset(id, "4xmsaa", 120);
            Check(GraphicsCatalog.PresetId(quality, msaa, scale) == id, id);
        }
        Check(GraphicsCatalog.ApplyPreset(GraphicsCatalog.Potato, "4xmsaa", 120) is { Msaa: "off", Scale: 70 });
        Check(GraphicsCatalog.ApplyPreset(GraphicsCatalog.Balanced, "4xmsaa", 120) is { Msaa: "off", Scale: 120 });
        Check(GraphicsCatalog.ApplyPreset("high", "4xmsaa", 120) is { Msaa: "4xmsaa", Scale: 120 });
        Check(GraphicsCatalog.ApplyPreset(GraphicsCatalog.GameSettings, "4xmsaa", 120) is { Quality: "", Msaa: "game", Scale: 120 });
        var custom = GraphicsCatalog.Decode(GraphicsCatalog.ApplyPreset("high", "off", 100).Quality)!;
        custom["Shadows"] = "low";
        Check(GraphicsCatalog.PresetId(GraphicsCatalog.Encode(custom), "off", 100) == GraphicsCatalog.Custom);
        Check(GraphicsCatalog.PresetId("", "off", 100) == GraphicsCatalog.Custom);
        // Ultra Low quality without Potato's anti-aliasing and scale is the game's Ultra Low.
        Check(GraphicsCatalog.PresetId(GraphicsCatalog.ApplyPreset(GraphicsCatalog.Potato, "off", 70).Quality, "off", 100) == "ultralow");
        foreach (var bad in new[] { "Shadows=ultra", "Shadows=nope", custom.Count + "=x", GraphicsCatalog.Encode(custom) + ",Shadows=low", GraphicsCatalog.Encode(custom).Replace("Shadows=low", "Shadows=") })
            Check(!GraphicsCatalog.ValidQuality(bad), bad);
        Check(GraphicsCatalog.ValidQuality("") && GraphicsCatalog.ValidMsaa("8xmsaa") && !GraphicsCatalog.ValidMsaa("16xcsaa") && !GraphicsCatalog.ValidMsaa("cmaa"));
    }

    private static void SettingsFile()
    {
        var original = File.ReadAllBytes(GraphicsCatalog.ConfigPath);
        var read = GraphicsCatalog.Read(original, out var msaa);
        Check(msaa == "8xmsaa" && read["Shadows"] == "ultra" && read["Crowd"] == "ultra" && read["Trees"] == "ultra" && read["SoftAmbientOcclusion"] == "on" && read["Avsm"] == "on");
        Check(read.Values.All(v => v is not null), string.Join(",", read.Where(p => p.Value is null).Select(p => p.Key)));
        Check(GraphicsCatalog.Apply(original, "", GraphicsCatalog.GameSettings).SequenceEqual(original));

        var low = GraphicsCatalog.Apply(original, GraphicsCatalog.ApplyPreset(GraphicsCatalog.Potato, "off", 100).Quality, "off");
        var lowRead = GraphicsCatalog.Read(low, out var lowMsaa);
        Check(lowMsaa == "off" && GraphicsCatalog.Preset("ultralow").Values.All(p => lowRead[p.Key] == p.Value));
        var lowText = Encoding.UTF8.GetString(low); var originalText = Encoding.UTF8.GetString(original);
        Check(lowText.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\r\n<hardware_settings_config version=\"21\"") && lowText.EndsWith("</hardware_settings_config>\r\n"));
        Check(low[0] == (byte)'<'); // No byte order mark.
        foreach (var kept in new[] { "<resolution width=\"800\" height=\"600\" aspect=\"auto\" fullscreen=\"false\" vsync=\"0\" multisampling=\"off\">", "workerMapFile=\"system/workerMap8Core.xml\"",
                     "<mirrors enabled=\"false\" width=\"256\" height=\"64\" car_maxlod=\"4\" />", "<physics environmentalDamage=\"true\" vehicleDamage=\"true\" />", "\t<gamma level=\"0.8\" />" })
            Check(lowText.Contains(kept, StringComparison.Ordinal), kept);
        Check(lowText.Contains("<trees lod=\"1.0\" maxlod=\"0\" meshes=\"false\" />") && lowText.Split('\n').Length == originalText.Split('\n').Length);
        // Trees Ultra's extra attribute comes and goes with the choice.
        var ultra = GraphicsCatalog.Apply(low, GraphicsCatalog.ApplyPreset("ultra", "off", 100).Quality, GraphicsCatalog.GameSettings);
        Check(Encoding.UTF8.GetString(ultra).Contains("meshes_in_all_passes=\"true\"") && GraphicsCatalog.Read(ultra, out var kept8)["Trees"] == "ultra" && kept8 == "off");
        Check(!Encoding.UTF8.GetString(GraphicsCatalog.Apply(ultra, GraphicsCatalog.ApplyPreset("high", "off", 100).Quality, "off")).Contains("meshes_in_all_passes"));
        Check(Throws<IOException>(() => GraphicsCatalog.Read("<hardware_settings_config"u8.ToArray(), out _)));
        Check(Throws<IOException>(() => GraphicsCatalog.Read("<other />"u8.ToArray(), out _)));
        Check(Throws<IOException>(() => GraphicsCatalog.Apply("<hardware_settings_config />"u8.ToArray(), GraphicsCatalog.ApplyPreset("high", "off", 100).Quality, "off")));
        Check(Throws<IOException>(() => GraphicsCatalog.Read(new byte[GraphicsCatalog.MaximumConfigBytes + 1], out _)));

        // The session plan: none for Game settings or when nothing changes; the file must exist.
        Check(VrPlaySession.PlanGraphics(new Choices()) is null);
        // The file's own choices change nothing but Trees Ultra's attribute, which the game does not save.
        var same = VrPlaySession.PlanGraphics(new Choices(GraphicsQuality: GraphicsCatalog.Encode(read!.ToDictionary(p => p.Key, p => p.Value!)), VrMsaa: "8xmsaa"));
        Check(same is not null && Encoding.UTF8.GetString(same.Staged) == originalText.Replace("meshes=\"true\" />", "meshes=\"true\" meshes_in_all_passes=\"true\" />"));
        var plan = VrPlaySession.PlanGraphics(new Choices(VrMsaa: "off"));
        Check(plan is not null && plan.Original.SequenceEqual(original) && GraphicsCatalog.Read(plan.Staged, out var planMsaa)["Shadows"] == "ultra" && planMsaa == "off" &&
            plan.Description == "the game's own quality settings, VR anti-aliasing Off");
        Check(Throws<IOException>(() => VrPlaySession.PlanGraphics(new Choices(VrMsaa: "off"), GraphicsCatalog.ConfigPath + ".missing")));
        Check(VrPlaySession.PlanGraphics(new Choices(), GraphicsCatalog.ConfigPath + ".missing") is null);
        // Exclusive fullscreen becomes a window of the same size for the session, with Game settings too.
        var fullscreenPath = GraphicsCatalog.ConfigPath + ".fullscreen.xml";
        File.WriteAllText(fullscreenPath, originalText.Replace("<resolution width=\"800\" height=\"600\" aspect=\"auto\" fullscreen=\"false\"",
            "<resolution width=\"3840\" height=\"2160\" aspect=\"auto\" fullscreen=\"true\""));
        var windowed = VrPlaySession.PlanGraphics(new Choices(), fullscreenPath);
        Check(windowed is not null && Encoding.UTF8.GetString(windowed.Staged) == File.ReadAllText(fullscreenPath).Replace("fullscreen=\"true\"", "fullscreen=\"false\"") &&
              windowed.Description == "the game's own quality settings; the game runs in a window of its chosen size instead of fullscreen", windowed?.Description ?? "no plan");
        var windowedLow = VrPlaySession.PlanGraphics(new Choices(GraphicsQuality: GraphicsCatalog.ApplyPreset(GraphicsCatalog.Potato, "off", 100).Quality, VrMsaa: "off"), fullscreenPath);
        Check(windowedLow is not null && Encoding.UTF8.GetString(windowedLow.Staged).Contains("width=\"3840\" height=\"2160\" aspect=\"auto\" fullscreen=\"false\" vsync=\"0\" multisampling=\"off\"") &&
              GraphicsCatalog.Read(windowedLow.Staged, out _)["Shadows"] == GraphicsCatalog.Preset("ultralow").Values["Shadows"]);
        File.Delete(fullscreenPath);
        Check(GraphicsCatalog.Describe(GraphicsCatalog.ApplyPreset(GraphicsCatalog.Balanced, "off", 100).Quality, "off") == "VR Balanced quality, VR anti-aliasing Off");
        Check(GraphicsCatalog.Describe(GraphicsCatalog.ApplyPreset("ultra", "game", 100).Quality, "game") == "Ultra quality");
    }

    private static void Choices()
    {
        var path = Path.Combine(LauncherSettings.DataDirectory, "graphics-settings.json");
        var balanced = GraphicsCatalog.ApplyPreset(GraphicsCatalog.Balanced, "game", 90);
        var value = new Choices(VrRenderScale: 90, GraphicsQuality: balanced.Quality, VrMsaa: balanced.Msaa);
        LauncherSettings.Save(value, path);
        var text = File.ReadAllText(path);
        Check(LauncherSettings.Load(path) == value && text.Contains("\"graphicsQuality\": {") && text.Contains("\"Crowd\": \"medium\"") && text.Contains("\"vrMsaa\": \"off\""));
        LauncherSettings.Save(new Choices(), path);
        Check(!File.ReadAllText(path).Contains("graphicsQuality") && LauncherSettings.Load(path) == new Choices());
        Check(Throws<ArgumentException>(() => LauncherSettings.Save(new Choices(VrMsaa: "cmaa"), path)));
        Check(Throws<ArgumentException>(() => LauncherSettings.Save(new Choices(GraphicsQuality: "Shadows=ultra"), path)));
        foreach (var bad in new[] { "{\"graphicsQuality\": {\"Shadows\": \"ultra\"}}", "{\"graphicsQuality\": \"ultra\"}", "{\"vrMsaa\": \"16xcsaa\"}",
                     "{\"graphicsQuality\": " + System.Text.Json.JsonSerializer.Serialize(GraphicsCatalog.Decode(balanced.Quality)!.ToDictionary(p => p.Key, p => p.Key == "Crowd" ? "huge" : p.Value)) + "}" })
        {
            File.WriteAllText(path, bad);
            Check(Throws<System.Text.Json.JsonException>(() => LauncherSettings.Load(path)), bad);
        }
        File.Delete(path);
    }

    private static readonly HeadsetResolution Quest3 = new("VDXR", "Quest 3", 3072, 3264);
    private static VrPerformance Measured(double median, double low, double dips, string quality, string msaa = "off", int scale = 100) =>
        new(DateTime.UtcNow, 90, 120, median, low, dips, quality, msaa, scale, Quest3.EyeSize(scale).Item1, Quest3.EyeSize(scale).Item2);

    private static void Recommendations()
    {
        Check(GraphicsRecommendation.GpuIndex("NVIDIA GeForce RTX 4080 SUPER") == 1.03 && GraphicsRecommendation.GpuIndex("NVIDIA GeForce RTX 4070 Laptop GPU") == .6 * .6);
        Check(GraphicsRecommendation.GpuIndex("Microsoft Basic Render Driver") is null);
        var balanced = GraphicsCatalog.ApplyPreset(GraphicsCatalog.Balanced, "game", 100);
        var current = new Choices(GraphicsQuality: balanced.Quality, VrMsaa: balanced.Msaa);
        var gpu = new GpuInfo("NVIDIA GeForce RTX 4080 SUPER", 0x10DE, 0x2702, 16000);
        // Estimates: a 4080 SUPER at Quest 3 resolution gets VR Balanced; a weak GPU gets Potato.
        var estimate = GraphicsRecommendation.Recommend(Quest3, gpu, null, new Choices());
        Check(estimate.Preset == GraphicsCatalog.Balanced && estimate.Scale is >= 80 and <= 100 && estimate.Reason.Contains("RTX 4080 SUPER at 3072 × 3264"), estimate.Reason);
        Check(GraphicsRecommendation.Recommend(Quest3, gpu with { Name = "NVIDIA GeForce RTX 3050 Laptop GPU" }, null, new Choices()).Preset == GraphicsCatalog.Potato);
        Check(GraphicsRecommendation.Recommend(Quest3, null, null, new Choices()) is { Preset: GraphicsCatalog.Balanced, Scale: 80 });
        Check(GraphicsRecommendation.Recommend(null, gpu, null, new Choices()).Reason.Contains("Headset resolution unknown"));
        // Measured: held the rate → keep; dips → lower scale; Ultra short of the rate → VR Balanced.
        Check(GraphicsRecommendation.Recommend(Quest3, gpu, Measured(90, 89, 0, balanced.Quality), current) is { Preset: GraphicsRecommendation.Keep, Scale: 100 });
        // Drops in about a tenth of the seconds happen at every setting: keep, and say so.
        var some = GraphicsRecommendation.Recommend(Quest3, gpu, Measured(90, 78.7, .106, balanced.Quality), current);
        Check(some is { Preset: GraphicsRecommendation.Keep, Scale: 100 } && some.Reason.Contains("Short drops in 11% of seconds"), some.Reason);
        var dips = GraphicsRecommendation.Recommend(Quest3, gpu, Measured(89.5, 72, .4, balanced.Quality), current);
        Check(dips.Preset == GraphicsRecommendation.Keep && dips.Scale is >= 80 and <= 95 && dips.Reason.Contains("40% of seconds dropped"), dips.Reason);
        var slow = GraphicsRecommendation.Recommend(Quest3, gpu, Measured(45, 40, 1, balanced.Quality), current);
        Check(slow is { Preset: GraphicsRecommendation.Keep, Scale: 65 } && slow.Reason.Contains("ran at 45 of 90 FPS"), slow.Reason);
        Check(GraphicsRecommendation.Recommend(Quest3, gpu, Measured(30, 25, 1, balanced.Quality), current).Preset == GraphicsCatalog.Potato);
        var ultra = GraphicsCatalog.ApplyPreset("ultra", "8xmsaa", 100);
        var heavy = GraphicsRecommendation.Recommend(Quest3, gpu, Measured(70, 60, .9, ultra.Quality, "8xmsaa"), new Choices(GraphicsQuality: ultra.Quality, VrMsaa: "8xmsaa"));
        Check(heavy.Preset == GraphicsCatalog.Balanced && heavy.Scale == 100);
        var applied = GraphicsRecommendation.Apply(new Choices(GraphicsQuality: ultra.Quality, VrMsaa: "8xmsaa"), heavy);
        Check(GraphicsCatalog.PresetId(applied.GraphicsQuality, applied.VrMsaa, applied.VrRenderScale) == GraphicsCatalog.Balanced && applied.VrRenderScale == 100);
        // A measurement with other settings, another headset or too short a drive is not used.
        Check(GraphicsRecommendation.Recommend(Quest3, gpu, Measured(45, 40, 1, ultra.Quality, "8xmsaa"), current).Reason.Contains("different quality"));
        Check(GraphicsRecommendation.Recommend(Quest3 with { Width = 2000 }, gpu, Measured(45, 40, 1, balanced.Quality), current).Reason.StartsWith("Estimated"));
        Check(GraphicsRecommendation.Recommend(Quest3, gpu, Measured(45, 40, 1, balanced.Quality) with { Windows = 3 }, current).Reason.StartsWith("Estimated"));
    }

    private static void Performance(string data)
    {
        var session = Path.Combine(data, "performance-session");
        Directory.CreateDirectory(session);
        Check(VrPerformance.Capture(session, new Choices(), null) is null && VrPerformance.Load() is null);
        File.WriteAllText(Path.Combine(session, "performance.json"),
            "{\"schema\":1,\"displayHz\":90.00,\"windows\":42,\"fps\":{\"p50\":89.9,\"p5\":71.5,\"min\":60.0},\"dips\":{\"share\":0.071}}\n");
        var ultra = GraphicsCatalog.ApplyPreset("ultra", "off", 100);
        var captured = VrPerformance.Capture(session, new Choices(GraphicsQuality: ultra.Quality, VrMsaa: "off"), (3072, 3264));
        Check(captured is { Usable: true, Dips: false, SomeDips: true, HeldRate: true, Windows: 42, EyeWidth: 3072 } && VrPerformance.Load() == captured);
        Check(captured!.Describe() == "90 FPS median, 72 low (headset 90 Hz) · mostly holding the headset rate, 7% of seconds dropped · 100% render scale · Ultra quality, VR anti-aliasing Off.", captured.Describe());
        File.WriteAllText(Path.Combine(session, "performance.json"), "{\"schema\":2}");
        Check(VrPerformance.Capture(session, new Choices(), null) is null && VrPerformance.Load() == captured);
        File.WriteAllText(VrPerformance.StorePath, "{\"Quality\":\"Shadows=ultra\"}");
        Check(VrPerformance.Load() is null);
        File.Delete(VrPerformance.StorePath);
    }

    private static void Click(Control control) => typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [EventArgs.Empty]);
    private static void Commit(ComboBox combo, string label)
    {
        combo.SelectedItem = combo.Items.Cast<object>().First(o => o.ToString() == label);
        typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(combo, [EventArgs.Empty]);
    }

    private static void Form(string output)
    {
        File.Delete(LauncherSettings.SettingsPath);
        LauncherSettings.SaveWindow(new LauncherWindow(AdvancedView: true));
        using var form = new MainForm(null, null, null) { ShowInactive = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        form.Show();
        T Find<T>(string name) where T : Control => (T)form.Controls.Find(name, true).Single();
        var preset = Find<ComboBox>("GraphicsPreset"); var msaa = Find<ComboBox>("VrMsaa"); var shadows = Find<ComboBox>("Shadows");
        var scale = Find<RenderScaleSlider>("VrRenderScale"); var undo = Find<Button>("UndoGraphicsPreset");
        // Game settings shows the player's file and changes nothing.
        Check(preset.Text == "Game settings" && msaa.Text == "Game setting" && shadows.Text == "Ultra" && Find<ComboBox>("SoftAmbientOcclusion").Text == "On");
        Check(Find<Label>("LastVrPerformance").Text.StartsWith("None measured yet"));
        Commit(preset, "Potato / Low-End");
        Check(shadows.Text == "Ultra Low" && msaa.Text == "Off" && scale.Value == 70 && preset.Text == "Potato / Low-End" && undo.Enabled);
        Click(undo);
        Check(preset.Text == "Game settings" && shadows.Text == "Ultra" && scale.Value == 100 && !undo.Enabled);
        Commit(preset, "High");
        Check(preset.Text == "High" && shadows.Text == "High" && msaa.Text == "Game setting" && Find<ComboBox>("Crowd").Text == "High");
        Find<ComboBox>("Crowd").SelectedItem = Find<ComboBox>("Crowd").Items.Cast<object>().First(o => o.ToString() == "Medium");
        msaa.SelectedItem = msaa.Items.Cast<object>().First(o => o.ToString() == "Off");
        Check(preset.Text == "VR Balanced" && !undo.Enabled);
        shadows.SelectedItem = shadows.Items.Cast<object>().First(o => o.ToString() == "Low");
        Check(preset.Text == "Custom");
        Click(Find<Button>("SaveSettings"));
        var saved = LauncherSettings.Load();
        Check(saved.VrMsaa == "off" && GraphicsCatalog.Decode(saved.GraphicsQuality)!["Shadows"] == "low" && GraphicsCatalog.Decode(saved.GraphicsQuality)!["Crowd"] == "medium");
        // Editing from Game settings starts the launcher's own quality from the file's choices.
        Commit(preset, "Game settings");
        Find<ComboBox>("Water").SelectedItem = Find<ComboBox>("Water").Items.Cast<object>().First(o => o.ToString() == "Low");
        Check(preset.Text == "Custom" && shadows.Text == "Ultra" && Find<ComboBox>("Water").Text == "Low");
        Click(Find<Button>("RecommendGraphics"));
        Check(Find<SelectableStatusText>("GraphicsPresetHint").Text.Length > 20 && preset.Text is "VR Balanced" or "Potato / Low-End");
        var graphicsPage = form.Tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "Graphics");
        form.Tabs.SelectedTab = graphicsPage;
        form.Refresh();
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, "launcher-graphics-presets.png"));
        }
        form.Close();
        File.Delete(LauncherSettings.SettingsPath);
    }
}
