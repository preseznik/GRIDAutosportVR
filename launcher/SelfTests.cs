using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

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
            Sessions();
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
        Check(LauncherSettings.Load(file) == new Choices(false, true, 100, 121, 0, true)); // New installs: Modern, 100% VR scale, F10, level horizon.
        LauncherSettings.Save(new Choices(RecenterKey: 120, RecenterModifiers: 1, KeepHorizonLevel: false), file);
        Check(LauncherSettings.Load(file) is { RecenterKey: 120, RecenterModifiers: 1, KeepHorizonLevel: false });
        File.WriteAllText(file, "{\"recenterKey\": 115, \"recenterModifiers\": 2}"); // Alt+F4 stays the system's.
        Check(Throws<JsonException>(() => LauncherSettings.Load(file)));
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
        // Practice / Race choices: Normal by default, validated on load and save.
        Check(new Choices() is { LaunchMode: "Normal", Car: "sls", Track: "cities/san_francisco/route_0", Laps: 3, Opponents: 7, OpponentCars: "same" });
        var race = new Choices(LaunchMode: "Race", Car: "pag", Track: "circuits/bathurst/route_1", Laps: 20, Opponents: 11, OpponentCars: "class");
        LauncherSettings.Save(race, file);
        Check(LauncherSettings.Load(file) == race);
        // VR HUD: everything shown at 1 m by default; parts and distance round-trip and are validated.
        Check(new Choices() is { HudShow: true, HudDial: true, HudMinimap: true, HudCounters: true, HudPositions: true, HudDistance: 1m, ScreenDistance: 3m } && new Choices().HudParts == 31);
        var hudChoices = new Choices(HudShow: true, HudDial: false, HudMinimap: true, HudCounters: false, HudPositions: true, HudDistance: 7.5m, ScreenDistance: 5.5m, Intro3D: true);
        Check(hudChoices.HudParts == 1 + 4 + 16 && (new Choices(HudShow: false)).HudParts == 30);
        LauncherSettings.Save(hudChoices, file);
        Check(LauncherSettings.Load(file) == hudChoices);
        Check(Throws<ArgumentException>(() => LauncherSettings.Save(new Choices(HudDistance: 1.25m), file)));
        Check(Throws<ArgumentException>(() => LauncherSettings.Save(new Choices(ScreenDistance: 7m), file)));
        foreach (var bad in new[] { "{\"hudDistance\": 0.5}", "{\"hudDistance\": 20.5}", "{\"hudDistance\": 2.25}", "{\"hudDistance\": \"2\"}", "{\"hudShow\": 1}", "{\"screenDistance\": 1}", "{\"screenDistance\": 6.5}", "{\"screenDistance\": 2.2}", "{\"intro3d\": \"yes\"}" })
        {
            File.WriteAllText(file, bad);
            Check(Throws<JsonException>(() => LauncherSettings.Load(file)));
        }
        Check(new Choices().VrCockpit); // Practice / Race start in the cockpit by default.
        LauncherSettings.Save(race with { VrCockpit = false }, file);
        Check(!LauncherSettings.Load(file).VrCockpit);
        foreach (var bad in new[] { "{\"vrCockpit\": 1}", "{\"launchMode\": \"Online\"}", "{\"car\": \"../x\"}", "{\"track\": \"p2p/okutama/route_0\"}", "{\"laps\": 0}",
                                    "{\"opponents\": 12}", "{\"opponentCars\": \"any\"}", "{\"track\": \"cities/chicago/route_0 -benchmark x\"}" })
        {
            File.WriteAllText(file, bad);
            Check(Throws<JsonException>(() => LauncherSettings.Load(file)), bad);
        }
        Check(Throws<ArgumentException>(() => LauncherSettings.Save(new Choices(Car: "a b"), file)));
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
        Check(start.ArgumentList.SequenceEqual(["--play-vr", "--game-dir", @"C:\Games\GRID Autosport"]) && start.FileName.EndsWith("GRIDAutosportVR.exe"));
        Check(Throws<FileNotFoundException>(() => Installation.ProbeStartInfo(null, false, "x.log")));
        // Installer checks on a fake Steam library: install needs the game; a staged session blocks both.
        var library = Path.Combine(LauncherSettings.DataDirectory, "fake-library");
        var game = Path.Combine(library, "steamapps", "common", "GRID Autosport");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_255220.acf"), "\"appid\" \"255220\"\n\"installdir\" \"GRID Autosport\"\n");
        Check(Program.InstallProblem(true, library)!.Contains("not a Steam installation") && Program.InstallProblem(false, library) is null);
        File.WriteAllBytes(Path.Combine(game, "GRIDAutosport.exe"), [0]);
        Check(Program.InstallProblem(true, game) is null && Program.InstallProblem(false, game) is null);
        File.WriteAllText(Path.Combine(game, Installation.PlayLease), "{}");
        Check(Program.InstallProblem(true, game)!.Contains("Restore original files") && Program.InstallProblem(false, game)!.Contains("Restore original files"));
        Directory.Delete(library, true);
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
        Check(VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", new Choices(HudFollowView: true), (3072, 3264)).SequenceEqual(
            ["-applaunch", "255220", "-gasvrplay=0123456789abcdef0123456789abcdef", "-gasvrmode=openxr", "-gasvrhudfollow=1",
             "-gasvrrecenterkey=121", "-gasvrrecentermods=0", "-gasvrkeephorizon=1", "-gasvreyesize=3072x3264", "-gasvrcockpit=1",
             "-gasvrhudparts=31", "-gasvrhuddistance=100", "-gasvrscreendistance=300"]));
        var hudArguments = VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", new Choices(HudShow: false, HudMinimap: false, HudDistance: 12.5m), null);
        Check(hudArguments.Contains("-gasvrhudparts=26") && hudArguments.Contains("-gasvrhuddistance=1250"));
        Check(VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", new Choices(ScreenDistance: 4.5m), null).Contains("-gasvrscreendistance=450"));
        Check(!new Choices().Intro3D && !VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", new Choices(), null).Contains("-gasvrintro3d=1"));
        Check(VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", new Choices(Intro3D: true), null).Contains("-gasvrintro3d=1"));
        Check(!VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", new Choices(VrCockpit: false), null).Contains("-gasvrcockpit=1"));
        var shortcut = VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", new Choices(RecenterKey: 120, RecenterModifiers: 5, KeepHorizonLevel: false), null);
        Check(shortcut.Contains("-gasvrrecenterkey=120") && shortcut.Contains("-gasvrrecentermods=5") && shortcut.Contains("-gasvrkeephorizon=0") && !shortcut.Any(a => a.StartsWith("-gasvreyesize")));
        Check(RecenterShortcut.Label(121, 0) == "F10" && RecenterShortcut.Label(120, 5) == "Ctrl+Shift+F9" && !RecenterShortcut.Valid(115, 2) && !RecenterShortcut.Valid(9, 2));
    }

    // A fake Steam library with a few cars and routes (and some incomplete ones that must be skipped).
    private static string FakeGame(string name)
    {
        var library = Path.Combine(LauncherSettings.DataDirectory, name);
        var game = Path.Combine(library, "steamapps", "common", "GRID Autosport");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_255220.acf"), "\"appid\" \"255220\"\n\"installdir\" \"GRID Autosport\"\n");
        File.WriteAllBytes(Path.Combine(game, "GRIDAutosport.exe"), [0]);
        foreach (var (id, liveries) in new[] { ("sls", new[] { "user", "03", "04" }), ("r8l", new[] { "user" }), ("pag", new[] { "user" }), ("zzz", new[] { "user" }), ("nouser", new[] { "01" }) })
        {
            var folder = Path.Combine(game, "cars", "models", id);
            foreach (var livery in liveries) Directory.CreateDirectory(Path.Combine(folder, "livery_" + livery));
            File.WriteAllText(Path.Combine(folder, "cameras.xml"), "<x/>");
        }
        Directory.CreateDirectory(Path.Combine(game, "cars", "models", "nocam", "livery_user"));
        foreach (var route in new[] { "circuits/bathurst/route_1", "circuits/bathurst/route_0", "cities/san_francisco/route_0", "circuits/spa_francorchamps/route_10" })
        {
            var folder = Path.Combine(game, "tracks", "locations", route);
            Directory.CreateDirectory(folder);
            foreach (var file in new[] { "track.jpk", "route_objects.pssg", "progress_track.xml" }) File.WriteAllBytes(Path.Combine(folder, file), [0]);
        }
        Directory.CreateDirectory(Path.Combine(game, "tracks", "locations", "cities", "chicago", "route_0")); // Incomplete route.
        Directory.CreateDirectory(Path.Combine(game, "tracks", "locations", "frontend", "frontend_garage", "route_0"));
        return game;
    }

    private static void Sessions()
    {
        var game = FakeGame("session-library");
        var catalog = RaceSession.Load(game);
        Check(catalog.Cars.Select(c => c.Id).Order().SequenceEqual(["pag", "r8l", "sls", "zzz"]));
        Check(catalog.Cars.Single(c => c.Id == "sls") is { Label: "Mercedes-Benz SLS AMG GT3", Class: "Endurance" } sls && sls.ToString() == "Mercedes-Benz SLS AMG GT3 · Endurance");
        Check(catalog.Cars.Single(c => c.Id == "zzz") is { Label: "ZZZ", Class: "Other" });
        Check(catalog.Tracks.Select(t => t.Id).SequenceEqual(["circuits/bathurst/route_0", "circuits/bathurst/route_1", "circuits/spa_francorchamps/route_10", "cities/san_francisco/route_0"]));
        Check(catalog.Tracks[0].Label == "Mount Panorama — route 0" && catalog.Tracks[3].Label == "San Francisco — route 0 (street)");

        XmlElement Parse(byte[] bytes) { var doc = new XmlDocument(); doc.LoadXml(System.Text.Encoding.UTF8.GetString(bytes)); return doc.DocumentElement!; }
        var practice = Parse(RaceSession.SessionXml(new Choices(LaunchMode: "Practice", Laps: 5), catalog, 1));
        var route = (XmlElement)practice.SelectSingleNode("track")!;
        Check(practice.GetAttribute("infinite_loop") == "false" && practice.GetAttribute("skipreplays") == "true" && practice.GetAttribute("cycle_camera") == "false");
        Check(route.GetAttribute("country") == "cities" && route.GetAttribute("name") == "san_francisco" && route.GetAttribute("route") == "route_0" &&
              route.GetAttribute("laps") == "5" && route.GetAttribute("number_cars") == "1");
        var player = (XmlElement)route.SelectSingleNode("car")!;
        Check(player.GetAttribute("name") == "sls" && player.GetAttribute("livery") == "user" && player.GetAttribute("grid") == "0" && player.GetAttribute("ai_driver") == "AmericanaI_1_1");
        // Same car: opponents use the car's other liveries in turn.
        var same = ((XmlElement)Parse(RaceSession.SessionXml(new Choices(LaunchMode: "Race", Opponents: 3), catalog, 1)).SelectSingleNode("track")!);
        var cars = same.SelectNodes("car")!.Cast<XmlElement>().ToArray();
        Check(same.GetAttribute("number_cars") == "4" && cars.Select(c => c.GetAttribute("grid")).SequenceEqual(["0", "1", "2", "3"]));
        Check(cars.All(c => c.GetAttribute("name") == "sls") && cars.Select(c => c.GetAttribute("livery")).SequenceEqual(["user", "03", "04", "03"]));
        Check(cars.Select(c => c.GetAttribute("ai_driver")).Distinct().Count() == 4);
        // Same discipline: other Endurance cars (r8l), repeated; Mixed: every other installed car.
        var sameClass = RaceSession.SessionXml(new Choices(LaunchMode: "Race", Opponents: 4, OpponentCars: "class"), catalog, 7);
        Check(Parse(sameClass).SelectNodes("track/car")!.Cast<XmlElement>().Skip(1).All(c => c.GetAttribute("name") == "r8l"));
        var mixed = Parse(RaceSession.SessionXml(new Choices(LaunchMode: "Race", Opponents: 11, OpponentCars: "mixed"), catalog, 7)).SelectNodes("track/car")!.Cast<XmlElement>().ToArray();
        Check(mixed.Length == 12 && mixed.Skip(1).Select(c => c.GetAttribute("name")).ToHashSet().SetEquals(["pag", "r8l", "zzz"]));
        Check(RaceSession.SessionXml(new Choices(LaunchMode: "Race", OpponentCars: "mixed"), catalog, 7).SequenceEqual(RaceSession.SessionXml(new Choices(LaunchMode: "Race", OpponentCars: "mixed"), catalog, 7)));
        // Other ("zzz") has no discipline: Same discipline falls back to the player's car.
        Check(Parse(RaceSession.SessionXml(new Choices(LaunchMode: "Race", Car: "zzz", Opponents: 2, OpponentCars: "class"), catalog, 1)).SelectNodes("track/car")!.Cast<XmlElement>().All(c => c.GetAttribute("name") == "zzz"));
        Check(Throws<IOException>(() => RaceSession.SessionXml(new Choices(LaunchMode: "Practice", Car: "foc"), catalog, 1)));
        Check(Throws<IOException>(() => RaceSession.SessionXml(new Choices(LaunchMode: "Practice", Track: "cities/chicago/route_0"), catalog, 1)));
        Check(Throws<ArgumentException>(() => RaceSession.SessionXml(new Choices(), catalog, 1)));
        Check(RaceSession.Summary(new Choices(), catalog).StartsWith("Normal"));
        Check(RaceSession.Summary(new Choices(LaunchMode: "Practice", Laps: 1), catalog) == "Practice: Mercedes-Benz SLS AMG GT3 at San Francisco — route 0 (street), 1 lap, solo.");
        Check(RaceSession.Summary(new Choices(LaunchMode: "Race", Opponents: 1, OpponentCars: "class"), catalog).EndsWith("1 AI opponent (same discipline)."));

        const string id = "0123456789abcdef0123456789abcdef";
        var args = VrPlaySession.SteamArguments(id, new Choices(LaunchMode: "Practice"), null, true);
        Check(args[^2..].SequenceEqual(["-benchmarkAttract", "GASVR_" + id + ".xml"]) && args.Contains("-gasvrplay=" + id));
        Check(!VrPlaySession.SteamArguments(id, new Choices(), null).Contains("-benchmarkAttract"));
        Check(!VrPlaySession.EndSession(game, TimeSpan.Zero)); // No game runs from the fake folder.

        // The flow patch: only the pinned stock file is accepted.
        Check(Throws<IOException>(() => PracticeFlow.Guarded(new byte[PracticeFlow.Length])));
        Check(PracticeFlow.Changes.Zip(PracticeFlow.Changes.Skip(1)).All(p => p.First.Offset + p.First.Before.Length / 2 <= p.Second.Offset));
        Check(PracticeFlow.Changes.All(c => c.Before.Length == c.After.Length && c.Before != c.After));
        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(game)))!, true);
        InstalledGame(Environment.GetEnvironmentVariable("GASVR_TEST_GAME"));
    }

    // With GASVR_TEST_GAME set to a real installation (read only): the real catalog and flow patch,
    // then a full Practice stage and restore on a temporary copy of the AVX and flow.bin.
    private static void InstalledGame(string? real)
    {
        if (string.IsNullOrEmpty(real)) { Console.WriteLine("Installed-game checks skipped (set GASVR_TEST_GAME)."); return; }
        var installed = RaceSession.Load(real);
        Console.WriteLine($"Installed: {installed.Cars.Count} cars, {installed.Tracks.Count} routes.");
        Check(installed.Cars.Count >= 80 && installed.Tracks.Count >= 100 && installed.Cars.All(c => c.Class != "Other"));
        var stockFlow = File.ReadAllBytes(Path.Combine(real, "system", "flow.bin"));
        var guarded = PracticeFlow.Guarded(stockFlow);
        Check(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(guarded)) == PracticeFlow.GuardedSha256);

        var game = FakeGame("stage-library");
        File.Copy(Path.Combine(real, "GRIDAutosport_avx.exe"), Path.Combine(game, "GRIDAutosport_avx.exe"));
        Directory.CreateDirectory(Path.Combine(game, "system"));
        File.WriteAllBytes(Path.Combine(game, "system", "flow.bin"), stockFlow);
        var bridge = Path.Combine(LauncherSettings.DataDirectory, "bridge-test.dll");
        File.WriteAllBytes(bridge, [1, 2, 3]);
        const string id = "fedcba9876543210fedcba9876543210";
        var session = RaceSession.SessionXml(new Choices(LaunchMode: "Practice"), RaceSession.Load(game), 1);
        VrPlaySession.Stage(game, bridge, id, session);
        Check(File.ReadAllBytes(Path.Combine(game, "GASVR_" + id + ".xml")).SequenceEqual(session));
        Check(File.ReadAllBytes(Path.Combine(game, "system", "flow.bin")).SequenceEqual(guarded));
        Check(File.Exists(Path.Combine(game, "gasxr.dll")) && File.Exists(Path.Combine(game, Installation.PlayLease)));
        Check(Program.InstallProblem(false, game)!.Contains("Restore original files"));
        Check(VrPlaySession.Recover(game));
        Check(File.ReadAllBytes(Path.Combine(game, "system", "flow.bin")).SequenceEqual(stockFlow));
        Check(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(game, "GRIDAutosport_avx.exe")))) == VrPlaySession.StockSha256);
        Check(Directory.EnumerateFiles(game).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(["GRIDAutosport.exe", "GRIDAutosport_avx.exe"]), string.Join(",", Directory.EnumerateFiles(game)));
        Check(!Directory.EnumerateFiles(Path.Combine(LauncherSettings.DataDirectory, "sessions", id)).Any(f => f.EndsWith(".bin") || f.EndsWith(".exe")));
        // A flow.bin changed during the session is preserved with its backup and lease.
        VrPlaySession.Stage(game, bridge, id.Replace('f', 'e'), session);
        File.WriteAllBytes(Path.Combine(game, "system", "flow.bin"), [9]);
        Check(Throws<IOException>(() => VrPlaySession.Recover(game)) && File.Exists(Path.Combine(game, Installation.PlayLease)));
        File.WriteAllBytes(Path.Combine(game, "system", "flow.bin"), guarded);
        Check(VrPlaySession.Recover(game) && File.ReadAllBytes(Path.Combine(game, "system", "flow.bin")).SequenceEqual(stockFlow));
        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(game)))!, true);
    }

    private static void Form(string output)
    {
        using var form = new MainForm(null, null, null) { ShowInactive = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        Check(form.Tabs.TabPages.Cast<TabPage>().Select(p => p.Text).SequenceEqual(["Launcher", "Graphics", "Controls", "Settings"]));
        foreach (var name in new[] { "LaunchDesktop", "LaunchVR", "SaveSettings", "RestoreOriginalFiles", "OpenLogs", "HelpAbout", "SessionStatus", "SessionNotice" })
            Check(form.Controls.Find(name, true).Length == 1, name);
        Check(!Find<Button>(form, "LaunchVR").Enabled && !Find<Button>(form, "LaunchDesktop").Enabled && !Find<Button>(form, "SaveSettings").Enabled);
        Check(!Find<Button>(form, "EndSession").Visible); // Only while a VR session runs.
        Check(Find<SelectableStatusText>(form, "SessionStatus").Text.Contains("Steam folder"));
        var sections = form.SectionHeaders.Select(h => h.Title).ToArray();
        Check(sections.SequenceEqual(["Session", "Before you launch", "VR rendering", "VR HUD", "VR screen", "Keyboard shortcuts", "Recenter alignment", "Paths and runtime", "Headset check", "Launcher preferences"]));
        Check(Find<Button>(form, "RecenterKey").Text == "F10" && Find<Label>(form, "RecenterHint").Text.Contains("Press F10"));
        Check(Find<Label>(form, "VrResolutionSummary").Text.Contains("Refresh headset"));
        form.UseHeadset(Quest3);
        Check(Find<Label>(form, "VrResolutionSummary").Text.Contains("Meta Quest 3"));
        // Session: Normal by default; Practice enables car, track and laps; Race also the opponents.
        Check(Find<ComboBox>(form, "LaunchMode").Text == "Normal" && !Find<ComboBox>(form, "SessionCar").Enabled && !Find<NumericUpDown>(form, "Opponents").Enabled);
        Check(!Find<CheckBox>(form, "VrCockpit").Enabled); // Practice / Race only.
        form.UseCatalog(new([new("sls", "Mercedes-Benz SLS AMG GT3", "Endurance", ["user", "03"]), new("r8l", "Audi R8 LMS Ultra", "Endurance", ["user"])],
            [new("cities/san_francisco/route_0", "cities", "san_francisco", "route_0", "San Francisco — route 0 (street)"), new("circuits/bathurst/route_0", "circuits", "bathurst", "route_0", "Mount Panorama — route 0")]));
        Check(Find<ComboBox>(form, "SessionCar").Text.StartsWith("Mercedes") && Find<ComboBox>(form, "SessionTrack").Text.StartsWith("San Francisco"));
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
            // VR HUD: Show HUD off disables the parts; parts and distance are saved.
            Check(Find<FlowLayoutPanel>(form, "HudParts").Enabled && Find<DistanceSlider>(form, "HudDistance").Value == 1m);
            Find<CheckBox>(form, "HudShow").Checked = false;
            Check(!Find<FlowLayoutPanel>(form, "HudParts").Enabled && Find<Button>(form, "SaveSettings").Enabled);
            Find<CheckBox>(form, "HudShow").Checked = true;
            Find<CheckBox>(form, "HudMinimap").Checked = false;
            Find<DistanceSlider>(form, "HudDistance").Value = 3.5m;
            Find<DistanceSlider>(form, "ScreenDistance").Value = 4m;
            Find<CheckBox>(form, "Intro3D").Checked = true;
            Find<Button>(form, "SaveSettings").PerformClick();
            Check(LauncherSettings.Load() is { HudShow: true, HudMinimap: false, HudDial: true, HudDistance: 3.5m, ScreenDistance: 4m, Intro3D: true });
            var mode = Find<ComboBox>(form, "LaunchMode");
            mode.SelectedIndex = 1;
            Check(Find<ComboBox>(form, "SessionCar").Enabled && Find<NumericUpDown>(form, "Laps").Enabled && !Find<NumericUpDown>(form, "Opponents").Enabled);
            Check(Find<CheckBox>(form, "VrCockpit").Enabled && Find<CheckBox>(form, "VrCockpit").Checked);
            Check(Find<Label>(form, "SessionSummary").Text.StartsWith("Practice: Mercedes-Benz SLS AMG GT3 at San Francisco"));
            mode.SelectedIndex = 2;
            Find<ComboBox>(form, "SessionTrack").SelectedIndex = 1;
            Find<ComboBox>(form, "OpponentCars").SelectedIndex = 1;
            Find<NumericUpDown>(form, "Opponents").Value = 4;
            Check(Find<NumericUpDown>(form, "Opponents").Enabled && Find<Label>(form, "SessionSummary").Text.Contains("Mount Panorama") &&
                  Find<Label>(form, "SessionSummary").Text.Contains("4 AI opponents (same discipline). Discipline: Endurance."));
            Find<Button>(form, "SaveSettings").PerformClick();
            Check(LauncherSettings.Load() is { LaunchMode: "Race", Car: "sls", Track: "circuits/bathurst/route_0", Opponents: 4, OpponentCars: "class" });
            // A saved car that is no longer installed stays chosen and is named in the summary.
            form.UseCatalog(new([new("r8l", "Audi R8 LMS Ultra", "Endurance", ["user"])], [new("circuits/bathurst/route_0", "circuits", "bathurst", "route_0", "Mount Panorama — route 0")]));
            Check(Find<ComboBox>(form, "SessionCar").SelectedItem is null && !Find<Button>(form, "SaveSettings").Enabled);
            Check(Find<Label>(form, "SessionSummary").Text.StartsWith("Race: sls at Mount Panorama"));
            // Changing the recenter key: click, then press it. System shortcuts are refused.
            var key = Find<Button>(form, "RecenterKey");
            form.Tabs.SelectedTab = form.Tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "Controls");
            Application.DoEvents();
            key.PerformClick();
            Check(key.Text.StartsWith("Press") && !Find<Button>(form, "LaunchVR").Enabled);
            Check(form.CaptureRecenterKey(Keys.F4 | Keys.Alt) && key.Text.StartsWith("Press") && Find<SelectableStatusText>(form, "SessionNotice").Text.Contains("reserved"));
            Check(form.CaptureRecenterKey(Keys.F9 | Keys.Control) && key.Text == "Ctrl+F9" && Find<Button>(form, "SaveSettings").Enabled);
            Find<CheckBox>(form, "KeepHorizonLevel").Checked = false;
            Find<Button>(form, "SaveSettings").PerformClick();
            Check(LauncherSettings.Load() is { RecenterKey: 120, RecenterModifiers: 1, KeepHorizonLevel: false, LaunchMode: "Race", Car: "sls" });
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
