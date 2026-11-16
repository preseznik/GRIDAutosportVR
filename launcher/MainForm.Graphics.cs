namespace GRIDAutosportVR;

// Graphics → Graphics preset and Quality settings, ported from GRID2VR's MainForm.Graphics.cs.
internal sealed partial class MainForm
{
    private readonly ComboBox graphicsPreset = Choice("GraphicsPreset"), vrMsaa = Choice("VrMsaa");
    private readonly Button recommendGraphics = Button("Recommend for this PC", "RecommendGraphics"), undoGraphics = Button("Undo preset", "UndoGraphicsPreset");
    // Recommendation reasons are worth quoting, so the hint is selectable text.
    private readonly SelectableStatusText presetHint = new() { Name = "GraphicsPresetHint", AccessibleName = "Graphics preset explanation", Margin = new Padding(0, 4, 0, 10) };
    private readonly Label lastPerformance = TextLabel("");
    private readonly Dictionary<string, ComboBox> qualityEditors = [];
    // "" while the game's own quality settings are used (Game settings).
    private string graphicsQuality = "";
    private (string Quality, string Msaa, int Scale)? graphicsUndo;
    private VrPerformance? measuredPerformance;
    private GpuInfo? detectedGpu;
    private bool gpuDetected, updatingGraphics;

    private string MsaaChoice => (vrMsaa.SelectedItem as Option)?.Id ?? GraphicsCatalog.GameSettings;

    private void InitializeGraphicsPreset(Control parent)
    {
        Section(parent, "Graphics preset");
        graphicsPreset.Items.AddRange([new Option(GraphicsCatalog.GameSettings, "Game settings"), new Option(GraphicsCatalog.Balanced, "VR Balanced"),
            new Option(GraphicsCatalog.Potato, "Potato / Low-End"), .. GraphicsCatalog.Presets.Select(p => new Option(p.Id, p.Label)),
            new Option(GraphicsCatalog.Custom, "Custom")]);
        Field(parent, "Quality preset", graphicsPreset);
        var actions = new FlowLayoutPanel { Name = "GraphicsPresetActions", AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 4) };
        actions.Controls.AddRange([recommendGraphics, undoGraphics]);
        parent.Controls.Add(actions);
        parent.Controls.Add(presetHint);
        vrMsaa.Items.AddRange(GraphicsCatalog.VrMsaa.Select(m => (object)new Option(m.Id, m.Label)).ToArray());
        Field(parent, "VR anti-aliasing", vrMsaa);
        lastPerformance.Name = "LastVrPerformance"; lastPerformance.Tag = "hint";
        Field(parent, "Last VR session", lastPerformance);
        parent.Controls.Add(TextLabel("Game settings uses the quality you chose in GRID Autosport. Any other preset applies to VR launches only: the launcher sets it in the game's graphics settings for the session and puts yours back afterwards. Changes made inside the game during a VR session last for that session only."));
        undoGraphics.Enabled = false;
        graphicsPreset.SelectionChangeCommitted += (_, _) => SelectGraphicsPreset((graphicsPreset.SelectedItem as Option)?.Id ?? GraphicsCatalog.Custom);
        vrMsaa.SelectedIndexChanged += (_, _) => GraphicsEdited();
        recommendGraphics.Click += (_, _) => RecommendGraphics();
        undoGraphics.Click += (_, _) =>
        {
            if (graphicsUndo is not { } previous) return;
            graphicsUndo = null;
            ShowGraphics(previous.Quality, previous.Msaa, previous.Scale);
            ChoiceEdited();
            notice.Text = "Previous graphics restored. Save settings or launch to keep them.";
        };
        tips.SetToolTip(graphicsPreset, "Quality presets mirror the game's own Graphics menu presets. VR Balanced is High with Medium crowd and VR anti-aliasing Off; Potato is Ultra Low with anti-aliasing Off and 70% render scale.");
        tips.SetToolTip(recommendGraphics, "Suggest a preset and VR render scale. Uses your last measured VR session when it used the same quality and anti-aliasing as now; otherwise estimates from this PC's graphics card and the headset resolution. Nothing is saved until you Save or launch; Undo preset restores your previous choices.");
        tips.SetToolTip(vrMsaa, "Multisample anti-aliasing for the VR eyes. Each step costs GPU time at headset resolution; Off with a higher render scale often looks sharper for the same cost. Game setting keeps the game's own choice.");
        tips.SetToolTip(lastPerformance, "Measured by the VR renderer while the race is in stereo: frame rate in one-second windows against the headset's display rate. Menus, the seated screen, pauses and loading are excluded.");
        UpdateLastPerformance();
    }

    private void InitializeQualityEditors(Control parent)
    {
        Section(parent, "Quality settings");
        foreach (var category in GraphicsCatalog.Categories)
        {
            var editor = Choice(category.Field);
            editor.Items.AddRange(category.Choices.Select(c => (object)new Option(c.Id, c.Label)).ToArray());
            editor.SelectedIndexChanged += (_, _) => QualityEdited();
            qualityEditors.Add(category.Field, editor);
            Field(parent, category.Label, editor);
        }
        tips.SetToolTip(qualityEditors["IntelAoit"], "Requires Intel-specific graphics extensions, so it stays Off.");
        parent.Controls.Add(TextLabel("The game's own Graphics menu settings. With Game settings selected they show your current choices; changing one switches to Custom. Resolution, window and VSync stay as the game has them."));
    }

    // Fills the preset controls from saved launcher choices.
    private void ShowGraphics(string quality, string msaa, int scale)
    {
        updatingGraphics = true;
        try
        {
            graphicsQuality = quality;
            vrMsaa.SelectedItem = vrMsaa.Items.Cast<Option>().FirstOrDefault(o => o.Id == msaa) ?? vrMsaa.Items[0];
            renderScale.Value = scale;
            var values = GraphicsCatalog.Decode(quality) ?? GameQuality();
            foreach (var (field, editor) in qualityEditors)
                editor.SelectedItem = values.TryGetValue(field, out var id) ? editor.Items.Cast<Option>().FirstOrDefault(o => o.Id == id) : null;
        }
        finally { updatingGraphics = false; }
        undoGraphics.Enabled = graphicsUndo is not null;
        UpdateVrSummary();
        UpdateGraphicsPreset();
    }

    // The quality in the player's hardware_settings_config.xml; a setting no choice matches is left out.
    private static Dictionary<string, string> GameQuality()
    {
        try
        {
            var path = GraphicsCatalog.ConfigPath;
            if (!File.Exists(path) || new FileInfo(path).Length > GraphicsCatalog.MaximumConfigBytes) return [];
            return GraphicsCatalog.Read(File.ReadAllBytes(path), out _).Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    // A quality edit makes the settings the launcher's own (Custom); a setting the game's file did
    // not match starts from the game's High.
    private void QualityEdited()
    {
        if (updatingGraphics) return;
        graphicsQuality = GraphicsCatalog.Encode(qualityEditors.ToDictionary(p => p.Key,
            p => (p.Value.SelectedItem as Option)?.Id ?? GraphicsCatalog.Preset("high").Values[p.Key]));
        GraphicsEdited();
    }

    private void GraphicsEdited()
    {
        if (updatingGraphics) return;
        // An old whole-selection snapshot must never discard later manual edits.
        graphicsUndo = null; undoGraphics.Enabled = false;
        UpdateGraphicsPreset();
        ChoiceEdited();
    }

    private void SelectGraphicsPreset(string id)
    {
        if (id == GraphicsCatalog.Custom) { UpdateGraphicsPreset(); return; }
        var previous = (graphicsQuality, MsaaChoice, renderScale.Value);
        var next = GraphicsCatalog.ApplyPreset(id, MsaaChoice, renderScale.Value);
        if (next != previous) graphicsUndo = previous;
        ShowGraphics(next.Quality, next.Msaa, next.Scale);
        ChoiceEdited();
        notice.Text = id switch
        {
            GraphicsCatalog.GameSettings => "Game settings selected: VR uses the quality and anti-aliasing you chose in GRID Autosport. Save settings or launch to keep it.",
            GraphicsCatalog.Potato => "Potato selected: Ultra Low, VR anti-aliasing Off, 70% render scale. Save settings or launch to keep it.",
            GraphicsCatalog.Balanced => "VR Balanced selected: High quality, Medium crowd, VR anti-aliasing Off. Render scale is unchanged. Save settings or launch to keep it.",
            _ => "Graphics preset selected. Save settings or launch to keep it. Render scale and anti-aliasing are unchanged.",
        };
    }

    private void RecommendGraphics()
    {
        if (!gpuDetected) { detectedGpu = GraphicsRecommendation.DetectGpu(); gpuDetected = true; }
        var current = Current;
        var recommendation = GraphicsRecommendation.Recommend(headsetResolution, detectedGpu, measuredPerformance, current);
        var next = GraphicsRecommendation.Apply(current, recommendation);
        var previous = (current.GraphicsQuality, current.VrMsaa, current.VrRenderScale);
        var changed = (next.GraphicsQuality, next.VrMsaa, next.VrRenderScale) != previous;
        if (changed) graphicsUndo = previous;
        ShowGraphics(next.GraphicsQuality, next.VrMsaa, next.VrRenderScale);
        ChoiceEdited();
        presetHint.Text = recommendation.Reason;
        var preset = recommendation.Preset switch { GraphicsCatalog.Potato => "Potato", GraphicsRecommendation.Keep => "your current quality", _ => "VR Balanced" };
        notice.Text = !changed ? "Your current graphics settings are already the recommendation."
            : $"Recommended {preset} at {next.VrRenderScale}% VR render scale. Save settings or launch to keep it, or select Undo preset.";
    }

    private void UpdateGraphicsPreset()
    {
        if (qualityEditors.Count != GraphicsCatalog.Categories.Count) return;
        var id = GraphicsCatalog.PresetId(graphicsQuality, MsaaChoice, renderScale.Value);
        graphicsPreset.SelectedItem = graphicsPreset.Items.Cast<Option>().First(o => o.Id == id);
        presetHint.Text = id switch
        {
            GraphicsCatalog.GameSettings => "The quality and anti-aliasing you chose in GRID Autosport. The launcher does not change them.",
            GraphicsCatalog.Potato => "Potato: Ultra Low quality · VR anti-aliasing Off · 70% VR render scale (49% of the pixels at 100%).",
            GraphicsCatalog.Balanced => "VR Balanced: High quality · Medium crowd · VR anti-aliasing Off. Recommend for this PC also picks a VR render scale.",
            GraphicsCatalog.Custom => "Custom quality settings for VR launches. Your settings in the game stay as they are.",
            _ => "The game's own quality preset, for VR launches. Render scale and anti-aliasing have their own controls.",
        };
    }

    internal void UpdateLastPerformance()
    {
        measuredPerformance = VrPerformance.Load();
        lastPerformance.Text = measuredPerformance is null ? "None measured yet. Launch VR and drive for at least 10 seconds."
            : measuredPerformance.Describe() + " " + measuredPerformance.At.ToLocalTime().ToString("g");
    }

    private void EnableGraphicsEditors(bool enabled)
    {
        graphicsPreset.Enabled = vrMsaa.Enabled = recommendGraphics.Enabled = enabled;
        undoGraphics.Enabled = enabled && graphicsUndo is not null;
        foreach (var (field, editor) in qualityEditors) editor.Enabled = enabled && field != "IntelAoit";
    }
}
