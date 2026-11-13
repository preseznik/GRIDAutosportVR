using System.Diagnostics;

namespace GRIDAutosportVR;

// GRID2VR's launcher layout (header, status, tabs of collapsible sections, notice, footer) with
// the Autosport actions: Steam game folder, normal VR play through the bridge, runtime checks.
internal sealed class MainForm : LauncherForm
{
    private const string HudHelp = "Follow view off keeps the race HUD in place ahead of your seated position. On moves it with your head, as the game's own HUD does. Loading screens stay in place in both modes. Applies to the next VR launch.";
    private string? gameFolder;
    private readonly string? probePath, rendererPath;
    private readonly SelectableStatusText state = new() { Name = "SessionStatus", AccessibleName = "Session status" },
        notice = new() { Name = "SessionNotice", AccessibleName = "Session message" };
    private readonly TextBox gamePath = new() { Name = "GameFolder", ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = "Steam game folder" };
    private readonly Button browse = Button("Choose game folder…", "BrowseGame"), query = Button("Check runtime", "CheckRuntime"),
        pattern = Button("Show eye markers", "EyeMarkers"), defaults = Button("Restore defaults", "LauncherDefaults");
    private readonly CheckBox hudFollow = new() { Name = "HudFollowView", AccessibleName = "HUD follows view", AccessibleDescription = HudHelp, Text = "On", AutoSize = true };
    private const string HudPartsHelp = "Show HUD and the HUD parts replace the game's own HUD options (Options → HUD Options) in VR sessions; your game settings stay as they are. Distance sets how far ahead of your seat the HUD appears; it keeps the same apparent size.";
    private readonly CheckBox hudShow = new() { Name = "HudShow", AccessibleName = "Show HUD", Text = "On", Checked = true, AutoSize = true },
        hudDial = HudPart("HudDial", "Dial"), hudMinimap = HudPart("HudMinimap", "Minimap"),
        hudCounters = HudPart("HudCounters", "Counters"), hudPositions = HudPart("HudPositions", "Position info");
    private readonly FlowLayoutPanel hudParts = new() { Name = "HudParts", AccessibleName = "HUD parts", AutoSize = true, WrapContents = true, Margin = Padding.Empty };
    private readonly DistanceSlider hudDistance = new("HudDistance", "HUD distance", DistanceSlider.HudMinimum, DistanceSlider.HudMaximum, 1m),
        screenDistance = new("ScreenDistance", "Screen distance", DistanceSlider.ScreenMinimum, DistanceSlider.ScreenMaximum, 3m);
    private readonly AppearanceSwitch modernInterface = new();
    private readonly RenderScaleSlider renderScale = new() { Name = "VrRenderScale", AccessibleName = "VR render scale" };
    private readonly Button refreshHeadset = Button("Refresh headset", "RefreshHeadset");
    private readonly Label vrSummary = TextLabel("");
    private HeadsetResolution? headsetResolution = HeadsetResolution.Load();
    private readonly Button recenter = Button("F10", "RecenterKey");
    private readonly Label recenterHint = TextLabel("");
    private readonly Button drivingBindings = Button("Driving controls…", "DrivingControls"), drivingLive = Button("Live input…", "DrivingLiveInputMain");
    private readonly Label drivingSummary = TextLabel("");
    private readonly CheckBox intro3d = new() { Name = "Intro3D", AccessibleName = "Show the pre-race intro in 3D", Text = "Show the pre-race intro in 3D", AutoSize = true };
    // Optional redrawn cockpit textures (CockpitTexturePack), shown when this build has a pack.
    private readonly CheckBox newCockpitTextures = new() { Name = "NewCockpitTextures", AccessibleName = "Use redrawn cockpit textures", Text = "Use redrawn cockpit textures", AutoSize = true };
    private readonly Button installCockpitTextures = Button("Download now", "InstallCockpitTextures"), removeCockpitTextures = Button("Remove", "RemoveCockpitTextures"),
        installCockpitFile = Button("Install from file…", "InstallCockpitTexturesFile");
    private readonly Label cockpitTextureStatus = TextLabel("");
    private bool cockpitTexturesInstalled, cockpitTextureBusy;
    private readonly CheckBox keepHorizonLevel = new() { Name = "KeepHorizonLevel", Text = "Keep horizon level when recentering", Checked = true, AutoSize = true };
    private int recenterKey = 121, recenterModifiers;
    // GRID2VR's controller / wheel recenter button (DeviceRecenter).
    private readonly Button bindRecenterDevice = Button("Bind controller / wheel…", "RecenterDevice"), clearRecenterDevice = Button("Clear", "ClearRecenterDevice");
    private readonly Label deviceRecenterHint = TextLabel("No controller / wheel button.");
    private DrivingBinding? recenterDevice;
    private bool capturingRecenter;
    private readonly Button desktop = Button("Launch", "LaunchDesktop"), vr = Button("Launch VR", "LaunchVR"),
        save = Button("Save settings", "SaveSettings"), recover = Button("Restore original files", "RestoreOriginalFiles"),
        logs = Button("Open logs", "OpenLogs"), end = Button("End session", "EndSession");
    private bool ending;
    // Launcher Practice / Race (RaceSession): the event the next Launch VR starts.
    private sealed record Option(string Id, string Label) { public override string ToString() => Label; }
    private readonly ComboBox launchMode = Choice("LaunchMode"), car = Choice("SessionCar"), track = Choice("SessionTrack"), opponentCars = Choice("OpponentCars");
    private readonly NumericUpDown laps = Count("Laps", RaceSession.MaxLaps), opponents = Count("Opponents", RaceSession.MaxOpponents);
    private readonly Label sessionSummary = TextLabel("");
    private readonly CheckBox vrCockpit = new() { Name = "VrCockpit", AccessibleName = "Start in the cockpit view", Text = "Start in the cockpit view", Checked = true, AutoSize = true };
    private ContentCatalog catalog = ContentCatalog.Empty;
    private string? catalogFolder;
    private string chosenCar = RaceSession.DefaultCar, chosenTrack = RaceSession.DefaultTrack;
    private bool applying;
    private readonly ToolTip tips = new() { InitialDelay = 450, ReshowDelay = 150, AutoPopDelay = 15000, ShowAlways = true };
    private readonly List<SectionHeader> sectionHeaders = [];
    private LauncherWindow window = new();
    private Choices saved = new();
    private bool windowLoaded, testing, supervising, recovering;
    private string? recoveryError;

    internal ModernTabs Tabs { get; }
    internal IReadOnlyList<SectionHeader> SectionHeaders => sectionHeaders;
    internal LauncherWindow WindowPreferences => window;

    public MainForm(string? folder, string? probe, string? renderer)
    {
        gameFolder = folder; probePath = probe; rendererPath = renderer;
        Text = Branding.Title + " — " + BuildInfo.Channel; Font = new Font("Segoe UI", 10);
        KeyPreview = true; AutoScaleMode = AutoScaleMode.Dpi; MinimumSize = new Size(560, 540);
        ClientSize = new Size(900, 720); StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var size in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.AutoSize })
            layout.RowStyles.Add(new(size, size == SizeType.Percent ? 100 : 0));
        var title = TextLabel(Branding.Title); title.Font = new Font(Font.FontFamily, 20, FontStyle.Bold);
        var channel = TextLabel(BuildInfo.Channel + " " + BuildInfo.Version); channel.Name = "Channel"; channel.Tag = "hint"; channel.Margin = new Padding(8, 0, 8, 0);
        var help = new Button { Text = "?", Name = "HelpAbout", AccessibleName = "Help / About", Size = new Size(36, 36), Anchor = AnchorStyles.Right };
        help.Click += (_, _) => ShowAbout();
        HelpRequested += (_, e) => { e.Handled = true; ShowAbout(); };
        layout.Controls.Add(new LauncherHeader(Branding.Logo(64), title, channel, help));
        state.Margin = new Padding(0, 8, 0, 16); layout.Controls.Add(state);
        Tabs = new ModernTabs { Name = "LauncherTabs", Dock = DockStyle.Fill };
        layout.Controls.Add(Tabs);

        var launcherColumns = Columns(Page(Tabs, "Launcher")); var launcher = launcherColumns.First;
        Section(launcher, "Session");
        launchMode.Items.AddRange([new Option("Normal", "Normal"), new Option("Practice", "Practice"), new Option("Race", "Race")]);
        opponentCars.Items.AddRange([new Option("same", "Same car"), new Option("class", "Same discipline"), new Option("mixed", "Mixed")]);
        Field(launcher, "Launch mode", launchMode);
        Field(launcher, "Car", car);
        Field(launcher, "Track", track);
        Field(launcher, "Laps", laps);
        Field(launcher, "Opponents", opponents);
        Field(launcher, "Opponent cars", opponentCars);
        Field(launcher, "Camera", vrCockpit);
        sessionSummary.Name = "SessionSummary"; sessionSummary.Tag = "hint";
        launcher.Controls.Add(sessionSummary);
        launcher.Controls.Add(TextLabel("Normal opens the game's own menus, where you choose cars and events. Practice starts the selected car and track on your own; Race adds 1–11 computer-controlled opponents. Practice and Race apply to Launch VR; the desktop Launch always opens the menus. They run on the game's benchmark mode, which has no pause menu: choose End session (beside Launch VR) to stop."));
        launcher.Controls.Add(TextLabel("Launch VR starts GRID Autosport through Steam with the VR renderer. The startup videos, loading screens and menus appear on a screen in front of your seat; races are in stereo with head tracking. Finish with Quit in the game: the launcher then restores the original game files. After an interrupted session, choose Restore original files."));
        launcher = launcherColumns.Second;
        Section(launcher, "Before you launch");
        launcher.Controls.Add(TextLabel("Connect your headset to its 32-bit OpenXR runtime, for example Virtual Desktop, before Launch VR. The launcher checks the runtime first and stops if no headset answers."));
        launcher.Controls.Add(TextLabel("Launch starts the unmodified game on the desktop through Steam."));

        var graphicsColumns = Columns(Page(Tabs, "Graphics")); var graphics = graphicsColumns.First;
        Section(graphics, "VR rendering");
        Field(graphics, "VR render scale", renderScale);
        graphics.Controls.Add(refreshHeadset);
        vrSummary.Name = "VrResolutionSummary"; vrSummary.Tag = "hint";
        Field(graphics, "Next VR launch", vrSummary);
        graphics.Controls.Add(TextLabel("100% renders each eye at the connected headset's recommended resolution. Lower values trade detail for performance; higher values sharpen the image and use more GPU time and memory. Headset output stays at 100%. The launcher reads the headset's resolution before every VR launch."));
        if (CockpitTexturePack.Current is { } pack)
        {
            Section(graphics, "Cockpit textures");
            Field(graphics, "Cockpit", newCockpitTextures);
            cockpitTextureStatus.Name = "CockpitTextureStatus"; cockpitTextureStatus.Tag = "hint";
            graphics.Controls.Add(cockpitTextureStatus);
            var cockpitActions = new FlowLayoutPanel { Name = "CockpitTextureActions", AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
            cockpitActions.Controls.AddRange([installCockpitTextures, installCockpitFile, removeCockpitTextures]);
            graphics.Controls.Add(cockpitActions);
            graphics.Controls.Add(TextLabel($"Sharper, AI-redrawn cockpit artwork for {pack.Cars} cars at 2048 x 2048 (the originals are 512 x 512). It is an optional {pack.SizeLabel} download from this project's GitHub releases, made the first time you launch VR with it on (or Download now); Install from file… takes a copy of {pack.Asset} instead. Markings and materials can differ slightly from the originals. Game files are not changed: the VR renderer swaps the textures in memory, and untick to use the originals again. Applies to the next VR launch."));
        }
        graphics = graphicsColumns.Second;
        Section(graphics, "VR HUD");
        Field(graphics, "Show HUD", hudShow);
        hudParts.Controls.AddRange([hudDial, hudMinimap, hudCounters, hudPositions]);
        Field(graphics, "HUD parts", hudParts);
        Field(graphics, "Distance", hudDistance);
        Field(graphics, "Follow view", hudFollow);
        graphics.Controls.Add(TextLabel(HudPartsHelp));
        graphics.Controls.Add(TextLabel(HudHelp));
        Section(graphics, "VR screen");
        Field(graphics, "Distance", screenDistance);
        Field(graphics, "Intro shots", intro3d);
        graphics.Controls.Add(TextLabel("Startup videos, loading screens, menus and the pause menu appear on a 16:9 screen fixed in front of your seat, up to 2.4 m wide; further back makes it smaller in view, and it stays in place until you recenter. The pre-race intro shots use it too, unless Intro shots is on: then they are shown in 3D, without the game's 2D info panel. Finish cameras and results always use the screen. Applies to the next VR launch."));

        // GRID2VR's Controls tab: VR now; Seat and Driving join it with their own launcher items.
        var controlPage = new TabPage("Controls") { Name = "ControlsPage", Padding = new Padding(8) };
        var controlTabs = new ModernTabs { Name = "ControlTabs", Dock = DockStyle.Fill };
        controlPage.Controls.Add(controlTabs); Tabs.TabPages.Add(controlPage);
        var controls = Page(controlTabs, "VR");
        Section(controls, "Keyboard shortcuts");
        Field(controls, "Recenter", recenter);
        recenterHint.Name = "RecenterHint"; controls.Controls.Add(recenterHint);
        controls.Controls.Add(TextLabel("Click the key above to change it before launching, then press the new key with any Ctrl, Alt or Shift. Press Escape to cancel. Save settings or launch to keep it; it applies to the next VR session. The game's own F11 recenter keeps working."));
        Section(controls, "Controller / wheel recenter");
        deviceRecenterHint.Name = "RecenterDeviceHint"; controls.Controls.Add(deviceRecenterHint);
        var recenterButtons = new FlowLayoutPanel { Name = "RecenterDeviceButtons", AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        recenterButtons.Controls.Add(bindRecenterDevice); recenterButtons.Controls.Add(clearRecenterDevice);
        controls.Controls.Add(recenterButtons);
        controls.Controls.Add(TextLabel("A button or hat direction on an Xbox controller or a wheel that recenters as the key does, alongside it. The launcher watches it while the game runs."));
        Section(controls, "Recenter alignment");
        controls.Controls.Add(keepHorizonLevel);
        controls.Controls.Add(TextLabel("Reset your position and forward direction without changing the horizon. Untick to align pitch and roll to your current head pose too. Applies on the next VR launch."));
        // GRID2VR's Driving page: bindings window (wizard, calibration, profiles) and live input.
        var driving = Page(controlTabs, "Driving");
        Section(driving, "Driving controls");
        drivingSummary.Name = "DrivingSummary"; drivingSummary.Tag = "hint";
        Field(driving, "Launcher bindings", drivingSummary);
        var drivingActions = new FlowLayoutPanel { Name = "DrivingActions", AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        drivingActions.Controls.AddRange([drivingBindings, drivingLive]);
        driving.Controls.Add(drivingActions);
        driving.Controls.Add(TextLabel(DrivingControls.RuntimeNotice));
        driving.Controls.Add(TextLabel("Driving controls… opens the bindings: the binding wizard, one action at a time with Key… or Device…, calibration (deadzone, saturation, direction), the Xbox preset and saved profiles. Live input… shows every connected controller, wheel and pedal set as you move it, with a rumble / force feedback test."));

        var settingColumns = Columns(Page(Tabs, "Settings")); var settings = settingColumns.First;
        Section(settings, "Paths and runtime");
        Field(settings, "Game folder", gamePath);
        settings.Controls.Add(browse);
        settings.Controls.Add(TextLabel("Uses your Steam installation of GRID Autosport (build with GRIDAutosport_avx.exe). The VR files are added only for a VR session and removed when it ends."));
        Section(settings, "Headset check");
        settings.Controls.Add(TextLabel("Check the 32-bit OpenXR runtime, or show a marker in each eye for up to 20 seconds. Connect your headset first."));
        var probeActions = new FlowLayoutPanel { Name = "HeadsetCheckActions", AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        probeActions.Controls.AddRange([query, pattern]);
        settings.Controls.Add(probeActions);
        if (probePath is null) settings.Controls.Add(TextLabel(@"Place AutosportXRProbe.exe in this package's probe folder to enable these checks."));
        settings = settingColumns.Second;
        Section(settings, "Launcher preferences");
        Field(settings, "Modern interface", modernInterface);
        settings.Controls.Add(TextLabel("Preview the red-accented Modern interface, or switch off for Classic. Both follow Windows light/dark and high-contrast settings. Save settings or launch to keep your choice."));
        settings.Controls.Add(defaults);
        settings.Controls.Add(TextLabel("Reset launcher choices. Save settings or launch to keep the reset. Game files and settings are unchanged."));

        notice.Margin = new Padding(0, 10, 0, 8); layout.Controls.Add(notice);
        end.Visible = false;
        layout.Controls.Add(new LauncherFooter(desktop, vr, save, recover, logs, end));
        Controls.Add(layout);
        void FitStatusLabels()
        {
            var width = Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal);
            state.FitWidth(width); notice.FitWidth(width);
        }
        layout.SizeChanged += (_, _) => FitStatusLabels(); FitStatusLabels();

        desktop.Click += (_, _) => RunDesktop();
        vr.Click += async (_, _) => await RunVrAsync();
        save.Click += (_, _) => SaveChoices(true);
        recover.Click += async (_, _) => await RecoverAsync();
        logs.Click += (_, _) => OpenLogs();
        end.Click += async (_, _) => await EndSessionAsync();
        browse.Click += (_, _) => ChooseGame();
        query.Click += async (_, _) => await RunProbeAsync(false);
        pattern.Click += async (_, _) => await RunProbeAsync(true);
        defaults.Click += (_, _) => { ApplyChoices(new Choices()); notice.Text = "Launcher defaults restored. Save settings or launch to keep them."; };
        hudFollow.CheckedChanged += (_, _) => ChoiceEdited();
        foreach (var hud in new[] { hudShow, hudDial, hudMinimap, hudCounters, hudPositions })
            hud.CheckedChanged += (_, _) => ChoiceEdited();
        hudDistance.ValueChanged += (_, _) => ChoiceEdited();
        screenDistance.ValueChanged += (_, _) => ChoiceEdited();
        intro3d.CheckedChanged += (_, _) => ChoiceEdited();
        newCockpitTextures.CheckedChanged += (_, _) => ChoiceEdited();
        installCockpitTextures.Click += async (_, _) => await EnsureCockpitTexturesAsync();
        installCockpitFile.Click += async (_, _) =>
        {
            using var open = new OpenFileDialog { Title = "Cockpit texture pack", Filter = $"{CockpitTexturePack.Current?.Asset}|{CockpitTexturePack.Current?.Asset}|ZIP files (*.zip)|*.zip" };
            if (open.ShowDialog(this) == DialogResult.OK) await EnsureCockpitTexturesAsync(open.FileName);
        };
        removeCockpitTextures.Click += async (_, _) => await RemoveCockpitTexturesAsync();
        drivingBindings.Click += (_, _) =>
        {
            try
            {
                using var dialog = new DrivingControlsForm();
                if (dialog.ShowDialog(this) == DialogResult.OK) notice.Text = "Driving controls saved. They apply on the next VR launch.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { notice.Text = "Driving controls: " + error.Message; }
            UpdateDrivingSummary();
        };
        drivingLive.Click += (_, _) =>
        {
            try
            {
                using var monitor = new DrivingInputMonitor(DrivingControls.Load().Bindings);
                monitor.ShowDialog(this);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { notice.Text = "Live input: " + error.Message; }
        };
        UpdateDrivingSummary();
        renderScale.ValueChanged += (_, _) => { UpdateVrSummary(); ChoiceEdited(); };
        refreshHeadset.Click += async (_, _) => await RunProbeAsync(false);
        recenter.Click += (_, _) => { capturingRecenter = true; recenter.Text = "Press a key…"; notice.Text = "Press the new recenter key, or Escape to cancel."; EnableActions(); };
        recenter.LostFocus += (_, _) => { if (capturingRecenter) { capturingRecenter = false; UpdateRecenterLabel(); EnableActions(); } };
        keepHorizonLevel.CheckedChanged += (_, _) => ChoiceEdited();
        bindRecenterDevice.Click += (_, _) =>
        {
            try
            {
                using var dialog = new DrivingBindingForm("Recenter", false, deviceButtonOnly: true);
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Bindings.FirstOrDefault() is not { } binding) return;
                DeviceRecenter.Validate(binding);
                recenterDevice = binding with { Action = "Recenter" }; UpdateDeviceRecenter(); ChoiceEdited();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { notice.Text = "Recenter button: " + error.Message; }
        };
        clearRecenterDevice.Click += (_, _) => { recenterDevice = null; UpdateDeviceRecenter(); ChoiceEdited(); };
        foreach (var combo in new[] { launchMode, car, track, opponentCars })
            combo.SelectedIndexChanged += (_, _) => SessionEdited();
        laps.ValueChanged += (_, _) => SessionEdited();
        vrCockpit.CheckedChanged += (_, _) => ChoiceEdited();
        opponents.ValueChanged += (_, _) => SessionEdited();
        modernInterface.CheckedChanged += (_, _) =>
        {
            LauncherAppearance.Modern = modernInterface.Checked;
            LauncherAppearance.Apply(this);
            ChoiceEdited();
        };
        ConfigureTooltips(help);
        LoadChoices();
        RefreshInstallation();
        Shown += async (_, _) => await RefreshCockpitTexturesAsync();
    }

    private Choices Current => new(hudFollow.Checked, modernInterface.Checked, renderScale.Value, recenterKey, recenterModifiers, keepHorizonLevel.Checked,
        (launchMode.SelectedItem as Option)?.Id ?? "Normal", chosenCar, chosenTrack, (int)laps.Value, (int)opponents.Value,
        (opponentCars.SelectedItem as Option)?.Id ?? "same", vrCockpit.Checked,
        hudShow.Checked, hudDial.Checked, hudMinimap.Checked, hudCounters.Checked, hudPositions.Checked, hudDistance.Value, screenDistance.Value, intro3d.Checked, recenterDevice,
        newCockpitTextures.Checked);

    // A car or track that is not installed (or before the catalog loads) stays chosen until replaced.
    private void SessionEdited()
    {
        if (applying) return;
        if (car.SelectedItem is CarItem c) chosenCar = c.Id;
        if (track.SelectedItem is TrackItem t) chosenTrack = t.Id;
        ChoiceEdited();
    }

    internal void UseCatalog(ContentCatalog value)
    {
        catalog = value;
        applying = true;
        try
        {
            car.Items.Clear(); car.Items.AddRange([.. catalog.Cars]);
            track.Items.Clear(); track.Items.AddRange([.. catalog.Tracks]);
            SelectSession();
        }
        finally { applying = false; }
        EnableActions();
    }

    private void SelectSession()
    {
        car.SelectedItem = catalog.Cars.FirstOrDefault(c => c.Id == chosenCar);
        track.SelectedItem = catalog.Tracks.FirstOrDefault(t => t.Id == chosenTrack);
    }

    private void LoadCatalog()
    {
        if (string.Equals(catalogFolder, gameFolder, StringComparison.OrdinalIgnoreCase) && catalog.Cars.Count > 0) return;
        catalogFolder = gameFolder;
        try { UseCatalog(Installation.GameExists(gameFolder) ? RaceSession.Load(gameFolder!) : ContentCatalog.Empty); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            UseCatalog(ContentCatalog.Empty);
            notice.Text = "Could not read the installed cars and tracks. " + error.Message;
        }
    }

    // Why Launch VR cannot start the chosen Practice / Race event, if it cannot.
    private string? SessionProblem()
    {
        var choices = Current;
        if (choices.LaunchMode == "Normal") return null;
        if (catalog.Cars.Count == 0 || catalog.Tracks.Count == 0) return "No installed cars or tracks were found for Practice or Race.";
        if (!catalog.Cars.Any(c => c.Id == choices.Car)) return $"Choose a car: {choices.Car} is not installed.";
        if (!catalog.Tracks.Any(t => t.Id == choices.Track)) return $"Choose a track: {choices.Track} is not installed.";
        return null;
    }

    private void LoadChoices()
    {
        try { saved = LauncherSettings.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            saved = new();
            notice.Text = "Could not read the launcher settings; defaults are shown. " + error.Message;
        }
        ApplyChoices(saved);
    }

    private void ApplyChoices(Choices choices)
    {
        hudFollow.Checked = choices.HudFollowView;
        hudShow.Checked = choices.HudShow; hudDial.Checked = choices.HudDial; hudMinimap.Checked = choices.HudMinimap;
        hudCounters.Checked = choices.HudCounters; hudPositions.Checked = choices.HudPositions; hudDistance.Value = choices.HudDistance;
        screenDistance.Value = choices.ScreenDistance; intro3d.Checked = choices.Intro3D;
        newCockpitTextures.Checked = choices.NewCockpitTextures;
        modernInterface.Checked = choices.ModernInterface;
        renderScale.Value = choices.VrRenderScale;
        recenterKey = choices.RecenterKey; recenterModifiers = choices.RecenterModifiers; UpdateRecenterLabel();
        recenterDevice = choices.RecenterDevice; UpdateDeviceRecenter();
        keepHorizonLevel.Checked = choices.KeepHorizonLevel;
        applying = true;
        try
        {
            launchMode.SelectedItem = launchMode.Items.Cast<Option>().First(o => o.Id == choices.LaunchMode);
            opponentCars.SelectedItem = opponentCars.Items.Cast<Option>().First(o => o.Id == choices.OpponentCars);
            laps.Value = choices.Laps; opponents.Value = choices.Opponents; vrCockpit.Checked = choices.VrCockpit;
            chosenCar = choices.Car; chosenTrack = choices.Track;
            SelectSession();
        }
        finally { applying = false; }
        LauncherAppearance.Modern = choices.ModernInterface;
        UpdateVrSummary();
        EnableActions();
    }

    private void ChoiceEdited()
    {
        if (Current != saved) notice.Text = "Unsaved changes. Save settings or launch to keep them.";
        EnableActions();
    }

    private bool SaveChoices(bool announce)
    {
        try
        {
            LauncherSettings.Save(Current);
            saved = Current;
            if (announce) notice.Text = "Settings saved for the next launch.";
            EnableActions();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            notice.Text = "Could not save the launcher settings. " + error.Message;
            return false;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        RestoreWindow();
        base.OnLoad(e);
    }

    private void RestoreWindow()
    {
        window = LauncherSettings.LoadWindow();
        var work = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(LauncherAppearance.Px(this, 560), work.Width), Math.Min(LauncherAppearance.Px(this, 540), work.Height));
        var available = new Size(work.Width - (Width - ClientSize.Width), work.Height - (Height - ClientSize.Height));
        ClientSize = window.ClientSizeAtDpi(DeviceDpi, available);
        if (StartPosition != FormStartPosition.Manual) Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
        var collapsed = window.CollapsedSections;
        window = LauncherWindow.FromClientSize(ClientSize, DeviceDpi) with { Maximized = window.Maximized, CollapsedSections = collapsed };
        ApplyCollapsedSections(collapsed);
        if (window.Maximized) WindowState = FormWindowState.Maximized;
        windowLoaded = true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (testing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            notice.Text = "Waiting for the OpenXR check to finish before closing.";
            return;
        }
        if (windowLoaded)
        {
            var maximized = WindowState == FormWindowState.Maximized;
            var size = maximized || WindowState == FormWindowState.Minimized ? RestoreBounds.Size - (Size - ClientSize) : ClientSize;
            window = LauncherWindow.FromClientSize(size, DeviceDpi) with { Maximized = maximized, CollapsedSections = window.CollapsedSections };
            try { LauncherSettings.SaveWindow(window); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        base.OnFormClosing(e);
    }

    internal void ApplyCollapsedSections(string? collapsedTitles)
    {
        var collapsed = (window with { CollapsedSections = collapsedTitles }).Collapsed();
        foreach (var header in sectionHeaders) header.Collapsed = collapsed.Contains(header.Title);
    }

    private void EnableActions()
    {
        gamePath.Text = gameFolder ?? "";
        var idle = !testing && !supervising && !recovering && !capturingRecenter;
        var valid = Installation.GameExists(gameFolder);
        var pending = Installation.SessionPending(gameFolder);
        var playLease = valid && File.Exists(Path.Combine(gameFolder!, Installation.PlayLease));
        desktop.Enabled = idle && valid && recoveryError is null && !pending;
        var problem = recoveryError ?? Installation.VrLaunchProblem(gameFolder, probePath, rendererPath) ?? SessionProblem();
        vr.Enabled = idle && problem is null;
        vr.AccessibleDescription = problem ?? "Start GRID Autosport in VR through Steam.";
        save.Enabled = idle && Current != saved;
        recover.Enabled = idle && playLease;
        end.Visible = supervising;
        end.Enabled = supervising && !ending;
        browse.Enabled = idle;
        query.Enabled = pattern.Enabled = refreshHeadset.Enabled = idle && File.Exists(probePath);
        defaults.Enabled = idle;
        var mode = (launchMode.SelectedItem as Option)?.Id ?? "Normal";
        launchMode.Enabled = idle;
        car.Enabled = track.Enabled = laps.Enabled = idle && mode != "Normal" && catalog.Cars.Count > 0;
        vrCockpit.Enabled = idle && mode != "Normal";
        hudParts.Enabled = hudShow.Checked;
        drivingBindings.Enabled = !supervising && !recovering;
        opponents.Enabled = opponentCars.Enabled = idle && mode == "Race" && catalog.Cars.Count > 0;
        sessionSummary.Text = RaceSession.Summary(Current, catalog) + (mode == "Race" && opponentCars.SelectedItem is Option { Id: "class" } && car.SelectedItem is CarItem c
            ? $" Discipline: {c.Class}{(catalog.Cars.Any(o => o.Id != c.Id && o.Class == c.Class && c.Class != "Other") ? "" : "; no other installed car shares it, so opponents use your car")}." : "");
        recenter.Enabled = !testing && !supervising && !recovering;
        bindRecenterDevice.Enabled = !testing && !supervising && !recovering && !capturingRecenter;
        clearRecenterDevice.Enabled = bindRecenterDevice.Enabled && recenterDevice is not null;
        installCockpitTextures.Enabled = installCockpitFile.Enabled = idle && !cockpitTextureBusy && !cockpitTexturesInstalled;
        removeCockpitTextures.Enabled = idle && !cockpitTextureBusy && cockpitTexturesInstalled;
        state.Text = supervising ? (Current.LaunchMode == "Normal"
                ? "VR session in progress. Quit the game normally, or choose End session; the launcher then restores the original game files."
                : $"{Current.LaunchMode} in progress. It has no pause menu: choose End session to stop; the launcher then restores the original game files.")
            : recoveryError is not null ? "Restore original files needs attention: " + recoveryError
            : playLease ? "A VR session is active or was interrupted. After the game exits, choose Restore original files."
            : pending ? "A VR benchmark session is pending. Restore it with bridge/run.py --restore before launching."
            : !valid ? "Choose your GRID Autosport Steam folder in Settings → Paths and runtime."
            : problem is not null ? problem
            : "Ready. Connect your headset, then choose Launch VR.";
    }

    private void RefreshInstallation()
    {
        recoveryError = null;
        LoadCatalog();
        var recovered = false;
        if (Installation.GameExists(gameFolder))
        {
            try { recovered = VrPlaySession.Recover(gameFolder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { recoveryError = error.Message; }
        }
        EnableActions();
        if (recovered) notice.Text = "Restored the game files from the interrupted VR session.";
    }

    private void ChooseGame()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select the GRID Autosport folder in your Steam library.", ShowNewFolderButton = false, UseDescriptionForTitle = true };
        if (gameFolder is not null) dialog.InitialDirectory = gameFolder;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!Installation.GameExists(dialog.SelectedPath))
        {
            notice.Text = "This folder does not match the GRID Autosport Steam manifest.";
            return;
        }
        gameFolder = dialog.SelectedPath;
        notice.Text = "Game folder selected.";
        RefreshInstallation();
    }

    private async Task RunVrAsync()
    {
        if (supervising || testing || gameFolder is null || !SaveChoices(false)) return;
        if (Current.NewCockpitTextures && CockpitTexturePack.Current is not null && !await EnsureCockpitTexturesAsync()) return;
        supervising = true;
        EnableActions();
        try
        {
            notice.Text = (Current.LaunchMode == "Normal" ? "" : RaceSession.Summary(Current, catalog) + " ") +
                $"Checking the headset, then Steam opens the game. Put on your headset. Press {RecenterShortcut.Label(recenterKey, recenterModifiers)} in the game to recenter.";
            using var worker = Process.Start(Installation.VrStartInfo(gameFolder)) ?? throw new IOException("The VR session supervisor did not start.");
            var errors = worker.StandardError.ReadToEndAsync();
            await worker.WaitForExitAsync();
            var detail = (await errors).Trim();
            if (!IsDisposed)
                notice.Text = worker.ExitCode == 0 ? "VR session ended. Original game files restored."
                    : detail.Length > 0 ? detail : "VR session exited with code " + worker.ExitCode + ".";
        }
        catch (Exception error) { if (!IsDisposed) notice.Text = error.Message; }
        finally
        {
            supervising = false;
            if (!IsDisposed) RefreshInstallation();
        }
    }

    // Downloads and installs the pack if it is missing. False when it is not installed afterwards.
    private async Task<bool> EnsureCockpitTexturesAsync(string? localZip = null)
    {
        if (CockpitTexturePack.Current is not { } pack || cockpitTextureBusy) return false;
        cockpitTextureBusy = true; EnableActions();
        try
        {
            cockpitTextureStatus.Text = "Checking the installed cockpit textures…";
            if (await Task.Run(() => CockpitTexturePack.Installed(pack))) return cockpitTexturesInstalled = true;
            using var dialog = new CockpitTextureForm(pack, localZip);
            var result = dialog.ShowDialog(this);
            cockpitTexturesInstalled = await Task.Run(() => CockpitTexturePack.Installed(pack));
            if (!IsDisposed && !cockpitTexturesInstalled)
                notice.Text = result == DialogResult.Cancel ? "Cockpit texture download cancelled. Launch again to retry, or untick Use redrawn cockpit textures to launch with the originals."
                    : "Cockpit textures: " + (dialog.Failure?.Message ?? "not installed.") + " Untick Use redrawn cockpit textures to launch with the originals.";
            else if (!IsDisposed) notice.Text = "Cockpit textures installed.";
            return cockpitTexturesInstalled;
        }
        finally
        {
            cockpitTextureBusy = false;
            if (!IsDisposed) { UpdateCockpitTextureStatus(); EnableActions(); }
        }
    }

    private async Task RemoveCockpitTexturesAsync()
    {
        if (CockpitTexturePack.Current is not { } pack || cockpitTextureBusy) return;
        cockpitTextureBusy = true; EnableActions();
        try
        {
            await Task.Run(() => CockpitTexturePack.Remove(pack));
            notice.Text = "Cockpit textures removed. VR launches use the original textures" + (newCockpitTextures.Checked ? " until they are downloaded again." : ".");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { notice.Text = "Cockpit textures: " + error.Message; }
        finally
        {
            cockpitTexturesInstalled = await Task.Run(() => CockpitTexturePack.Installed(pack));
            cockpitTextureBusy = false;
            if (!IsDisposed) { UpdateCockpitTextureStatus(); EnableActions(); }
        }
    }

    // Reads whether the pack is installed (hashing it) off the UI thread at startup.
    private async Task RefreshCockpitTexturesAsync()
    {
        if (CockpitTexturePack.Current is not { } pack) return;
        cockpitTextureStatus.Text = "Checking the installed cockpit textures…";
        cockpitTexturesInstalled = await Task.Run(() => CockpitTexturePack.Installed(pack));
        if (!IsDisposed) { UpdateCockpitTextureStatus(); EnableActions(); }
    }

    private void UpdateCockpitTextureStatus()
    {
        if (CockpitTexturePack.Current is not { } pack) return;
        cockpitTextureStatus.Text = cockpitTexturesInstalled ? $"Pack {pack.Version} installed ({pack.InstalledBytes / 1_000_000:0} MB)."
            : $"Pack {pack.Version} not installed: a {pack.SizeLabel} download.";
    }

    private async Task EndSessionAsync()
    {
        if (!supervising || ending || gameFolder is null) return;
        ending = true;
        EnableActions();
        try
        {
            notice.Text = "Closing GRID Autosport…";
            var folder = gameFolder;
            var closed = await Task.Run(() => VrPlaySession.EndSession(folder, TimeSpan.FromSeconds(15)));
            if (!IsDisposed && supervising)
                notice.Text = closed ? "GRID Autosport closed. Restoring the original game files…" : "GRID Autosport is not running yet; the session ends when Steam's start times out.";
        }
        catch (Exception error) { if (!IsDisposed) notice.Text = error.Message; }
        finally
        {
            ending = false;
            if (!IsDisposed) EnableActions();
        }
    }

    private void RunDesktop()
    {
        if (!SaveChoices(false)) return;
        try
        {
            Process.Start(Installation.DesktopStartInfo(gameFolder))?.Dispose();
            notice.Text = "Launch request sent to Steam.";
        }
        catch (Exception error) { notice.Text = error.Message; }
        EnableActions();
    }

    private async Task RecoverAsync()
    {
        if (gameFolder is null) return;
        recovering = true;
        EnableActions();
        try
        {
            var folder = gameFolder;
            var restored = await Task.Run(() => VrPlaySession.Recover(folder));
            recoveryError = null;
            notice.Text = restored ? "Original game files restored." : "No VR session needed restoring.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            recoveryError = error.Message;
            notice.Text = error.Message;
        }
        finally
        {
            recovering = false;
            EnableActions();
        }
    }

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(Installation.LogDirectory);
            Process.Start(new ProcessStartInfo(Installation.LogDirectory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) { notice.Text = error.Message; }
    }

    private async Task RunProbeAsync(bool stereo)
    {
        if (testing) return;
        testing = true;
        EnableActions();
        try
        {
            Directory.CreateDirectory(Installation.LogDirectory);
            var logFile = Path.Combine(Installation.LogDirectory, (stereo ? "eye-test-" : "openxr-") + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
            notice.Text = stereo ? "Running the headset eye test…" : "Checking the 32-bit OpenXR runtime…";
            using var worker = Process.Start(Installation.ProbeStartInfo(probePath, stereo, logFile)) ?? throw new IOException("The OpenXR probe did not start.");
            await worker.WaitForExitAsync();
            var failure = Installation.ProbeError(worker.ExitCode);
            if (failure is null && File.Exists(logFile) && HeadsetResolution.FromProbeLog(File.ReadLines(logFile)) is { } headset)
            {
                headsetResolution = headset;
                try { headset.Save(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                UpdateVrSummary();
            }
            notice.Text = (failure ?? (stereo ? "The probe showed its test pattern." : "Runtime check passed; headset resolution refreshed.")) +
                (File.Exists(logFile) ? Environment.NewLine + logFile : " The probe did not create its log.");
        }
        catch (Exception error) { notice.Text = error.Message; }
        finally
        {
            testing = false;
            EnableActions();
        }
    }

    private void UpdateDeviceRecenter()
    {
        deviceRecenterHint.Text = recenterDevice is null ? "No controller / wheel button." : DrivingInput.Description(recenterDevice);
        clearRecenterDevice.Enabled = bindRecenterDevice.Enabled && recenterDevice is not null;
    }

    private void UpdateRecenterLabel()
    {
        recenter.Text = RecenterShortcut.Label(recenterKey, recenterModifiers);
        recenterHint.Text = $"Press {recenter.Text} in GRID Autosport to recenter: sit comfortably and face forward. It also places the screen used for loading and menus in front of you.";
    }

    // Key capture as in GRID2VR: the next key press (with Ctrl, Alt, Shift) becomes the shortcut.
    protected override bool ProcessCmdKey(ref Message message, Keys keyData) =>
        capturingRecenter ? CaptureRecenterKey(keyData) : base.ProcessCmdKey(ref message, keyData);

    internal bool CaptureRecenterKey(Keys keyData)
    {
        if (!capturingRecenter) return false;
        var key = (int)(keyData & Keys.KeyCode);
        if (key == (int)Keys.Escape) { capturingRecenter = false; UpdateRecenterLabel(); notice.Text = "Recenter shortcut unchanged."; EnableActions(); return true; }
        if (key is (int)Keys.ControlKey or (int)Keys.Menu or (int)Keys.ShiftKey or (int)Keys.LWin or (int)Keys.RWin) return true;
        var modifiers = ((keyData & Keys.Control) != 0 ? 1 : 0) | ((keyData & Keys.Alt) != 0 ? 2 : 0) | ((keyData & Keys.Shift) != 0 ? 4 : 0);
        if (!RecenterShortcut.Valid(key, modifiers)) { notice.Text = "Choose another key; system shortcuts are reserved."; return true; }
        recenterKey = key; recenterModifiers = modifiers; capturingRecenter = false;
        UpdateRecenterLabel(); ChoiceEdited();
        notice.Text = "Recenter shortcut changed. Save settings or launch to keep it.";
        return true;
    }

    internal void UpdateDrivingSummary()
    {
        try
        {
            var controls = DrivingControls.Load();
            var devices = controls.Bindings.Select(b => b.Device).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            drivingSummary.Text = controls.Bindings.Count == 0 ? "None: the game's own controls are used."
                : $"{(controls.Enabled ? "On" : "Off")}: {controls.Bindings.Count} {(controls.Bindings.Count == 1 ? "binding" : "bindings")} on {string.Join(", ", devices)}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { drivingSummary.Text = "Could not read the driving controls: " + error.Message; }
    }

    internal void UpdateVrSummary() => vrSummary.Text = headsetResolution?.Summary(renderScale.Value) ??
        "Connect your headset and choose Refresh headset to see the render size. VR launches read it automatically.";

    internal void UseHeadset(HeadsetResolution? value) { headsetResolution = value; UpdateVrSummary(); }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            $"{Branding.Title} {BuildInfo.Version} ({BuildInfo.Channel})\nBuild: {BuildInfo.Built}, {BuildInfo.Revision}\n\n" +
            "Connect your headset, choose Launch VR, and finish with Quit in the game so the launcher can restore the original files.\n\n" +
            Branding.Repository,
            "Help / About", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ConfigureTooltips(Control help)
    {
        tips.SetToolTip(help, "Help / About. You can also press F1.");
        tips.SetToolTip(desktop, "Start the unmodified game on the desktop through Steam.");
        tips.SetToolTip(vr, "Check the headset, add the VR files and start the game through Steam. The files are removed when the game exits.");
        tips.SetToolTip(save, "Keep your launcher choices without launching.");
        tips.SetToolTip(recover, "Remove the VR files left by an interrupted session and restore the original game executable.");
        tips.SetToolTip(end, "Close this session's game (as its close button does) and restore the original game files. Practice and Race have no pause menu, so use this to stop early.");
        tips.SetToolTip(logs, "Open the folder with VR session and headset-check logs to attach to a report.");
        tips.SetToolTip(refreshHeadset, "Read the connected headset''s recommended resolution through the 32-bit OpenXR runtime.");
        tips.SetToolTip(launchMode, "Normal opens the game's menus. Practice starts the selected car and track on your own. Race adds computer-controlled opponents. Practice and Race apply to Launch VR.");
        tips.SetToolTip(car, "Your car for Practice or Race. The discipline after the name groups Same discipline opponents.");
        tips.SetToolTip(track, "The circuit or street route for Practice or Race.");
        tips.SetToolTip(laps, $"Laps for Practice or Race, 1–{RaceSession.MaxLaps}.");
        tips.SetToolTip(opponents, $"Race only: 1–{RaceSession.MaxOpponents} computer-controlled opponents. You start on pole.");
        tips.SetToolTip(vrCockpit, "Practice and Race in VR start in the driver's seat (the game's head camera) instead of the chase camera. The game's change-camera button still switches views. Normal races keep the camera you last used.");
        tips.SetToolTip(opponentCars, "Race only. Same car uses your car in its other liveries. Same discipline picks other installed cars of your car's discipline. Mixed picks any installed cars.");
        tips.SetToolTip(recenter, "Click, then press the key (with Ctrl, Alt or Shift) that recenters your view in VR.");
        tips.SetToolTip(hudShow, "Show the race HUD in VR, even if the game's HUD options or difficulty settings turn it off. Off hides it. Applies to the next VR launch.");
        tips.SetToolTip(hudDial, "The rev dial with speed and gear.");
        tips.SetToolTip(hudMinimap, "The track map.");
        tips.SetToolTip(hudCounters, "The lap and time counters.");
        tips.SetToolTip(hudPositions, "Your race position and the gaps to the cars around you.");
        tips.SetToolTip(intro3d, "On: the camera shots before a race starts are shown in 3D around you, without the game's 2D info panel and wipes. Off: they play on the screen ahead of you. Finish cameras and results always use the screen. Applies to the next VR launch.");
        tips.SetToolTip(screenDistance, "How far ahead of your seat the screen for videos, loading and menus appears, 1.5–6 m in 0.5 m steps. Default: 3 m. Applies to the next VR launch.");
        tips.SetToolTip(hudDistance, "How far ahead of your seat the HUD appears, 1–20 m in 0.5 m steps. Its apparent size stays the same. Default: 1 m. Applies to the next VR launch.");
    }
    private static CheckBox HudPart(string name, string text) => new() { Name = name, AccessibleName = text, Text = text, Checked = true, AutoSize = true, Margin = new Padding(0, 0, 16, 4) };

    private static ComboBox Choice(string name) => new() { Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, MaxDropDownItems = 20 };
    private static NumericUpDown Count(string name, int maximum) => new() { Name = name, Minimum = 1, Maximum = maximum, Value = 1, Width = 80 };
    private static Button Button(string text, string name) => new() { Text = text, Name = name, AutoSize = true, Padding = new Padding(5, 2, 5, 2) };
    private static Label TextLabel(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 4, 0, 10) };
    private static VerticalStack Page(TabControl tabs, string title)
    {
        var page = new TabPage(title) { Padding = new Padding(16), AutoScroll = true, BackColor = SystemColors.Control };
        var content = new VerticalStack { Name = title + "Content" };
        page.Controls.Add(content); tabs.TabPages.Add(page);
        return content;
    }
    private static ResponsiveColumns Columns(Control parent)
    {
        var columns = new ResponsiveColumns(); parent.Controls.Add(columns); return columns;
    }
    private void Section(Control content, string title)
    {
        var header = new SectionHeader(title) { Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 8) };
        header.CollapsedChanged += (_, _) => window = window.WithCollapsed(sectionHeaders.Where(h => h.Collapsed).Select(h => h.Title).Distinct());
        sectionHeaders.Add(header); content.Controls.Add(header);
    }
    private static void Field(Control content, string title, Control control) => content.Controls.Add(new SettingRow(title, control));
}
