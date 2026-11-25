namespace GRIDAutosportVR;

// GRID2VR's Simple / Advanced view (MainForm.Views.cs there). Simple shows one Quick start tab with
// the essentials (Play, Performance, Controls & comfort); Advanced shows every tab. The essential
// controls themselves move between the two, so both views edit the same choices and switching never
// changes or saves a setting. New installations start in Simple; existing settings open in Advanced.
internal sealed partial class MainForm
{
    private readonly LauncherViewPicker viewPicker = new();
    private bool restoringViewPicker;
    private readonly List<ViewPlacement> viewPlacements = [];
    private readonly List<(VerticalStack Stack, List<Control> Order)> quickOrder = [];
    private readonly List<Control> simpleEventRows = [], simpleRaceRows = [];
    private TabPage[] fullPages = [];
    private TabPage? quickPage, lastFullPage;
    private readonly Button simpleSeat = Button("Seat adjustment…", "SimpleSeat");
    private sealed record ViewPlacement(Control Control, Control FullParent, int FullIndex);
    private sealed class QuickStartColumns : ResponsiveColumns { protected override int WideBreakpoint => 740; }

    // Self-tests answer the Advanced view confirmation themselves.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<bool>? ConfirmAdvancedView { get; set; }
    internal bool AdvancedView => viewPicker.Advanced;
    internal void SelectView(bool advanced) => viewPicker.Advanced = advanced;

    // The view a window preference asks for: saved settings without a preference keep Advanced.
    internal static bool UseAdvancedView(LauncherWindow window) => window.AdvancedView ?? LauncherSettings.HasSavedSettings;

    private void InitializeViews()
    {
        fullPages = Tabs.TabPages.Cast<TabPage>().ToArray(); lastFullPage = fullPages[0];
        quickPage = new TabPage("Quick start") { Name = "QuickStart", Padding = new Padding(16), AutoScroll = true, BackColor = SystemColors.Control };
        var root = new VerticalStack { Name = "QuickStartContent" }; quickPage.Controls.Add(root);
        var columns = new QuickStartColumns(); root.Controls.Add(columns);
        var play = Stack(columns.First); var performance = Stack(columns.Second);

        QuickSection(play, "Play");
        Row(play, runtime); Row(play, launchMode);
        foreach (var editor in new Control[] { car, track, timeOfDay, laps, vrCockpit }) simpleEventRows.Add(Row(play, editor));
        foreach (var editor in new Control[] { opponents, opponentCars }) simpleRaceRows.Add(Row(play, editor));
        simpleEventRows.Add(Move(play, sessionSummary));
        Note(play, "Normal opens the game's own menus. Practice starts the chosen car and track on your own; Race adds computer-controlled opponents. Connect your headset to its 32-bit OpenXR runtime, then Launch VR.");

        QuickSection(performance, "Performance");
        Row(performance, graphicsPreset); Move(performance, Named("GraphicsPresetActions")); Move(performance, presetHint);
        Row(performance, vrMsaa); Row(performance, lastPerformance);
        Row(performance, renderScale); Move(performance, refreshHeadset); Row(performance, vrSummary);
        Note(performance, "Recommend for this PC suggests a preset and VR render scale from your last VR session, or from your graphics card and headset. Game settings keeps the quality you chose in GRID Autosport. Save or launch to keep your choices.");
        QuickSection(performance, "Controls & comfort");
        Row(performance, recenter); Move(performance, Named("DrivingActions")); Row(performance, hudShow);
        var comfort = new FlowLayoutPanel { Name = "SimpleComfortActions", AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        comfort.Controls.Add(simpleSeat); Fixed(performance, comfort);
        Note(performance, "Advanced shows every graphics, VR, controls and runtime option. Switching views keeps every choice, including ones Simple does not show.");

        simpleSeat.Click += (_, _) =>
        {
            if (!EnterAdvancedView()) return;
            Tabs.SelectedTab = fullPages.Single(p => p.Name == "ControlsPage");
            var controlTabs = (TabControl)Named("ControlTabs");
            controlTabs.SelectedTab = controlTabs.TabPages.Cast<TabPage>().Single(p => p.Text == "Seat");
        };
        viewPicker.ViewChanged += (_, _) =>
        {
            if (restoringViewPicker) return;
            if (windowLoaded && viewPicker.Advanced && Tabs.TabPages.Contains(quickPage) && !(ConfirmAdvancedView ?? ConfirmAdvanced)())
            {
                restoringViewPicker = true;
                try { viewPicker.Advanced = false; }
                finally { restoringViewPicker = false; }
                return; // Cancel leaves the tabs, edits and saved view untouched.
            }
            ApplyView(viewPicker.Advanced);
            if (!windowLoaded) return;
            window = window with { AdvancedView = viewPicker.Advanced };
            try { LauncherSettings.SaveWindow(window); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { notice.Text = "View preference could not be saved: " + error.Message; }
        };
        tips.SetToolTip(viewPicker, "Simple shows the essential setup on one page. Advanced shows every option. Switching views never changes or saves a setting.");
        Disposed += (_, _) => { foreach (var page in fullPages.Append(quickPage)) if (page.Parent is null) page.Dispose(); };

        VerticalStack Stack(VerticalStack stack) { quickOrder.Add((stack, [])); return stack; }
        List<Control> Order(VerticalStack stack) => quickOrder.Single(o => o.Stack == stack).Order;
        void QuickSection(VerticalStack stack, string title) { Section(stack, title); Order(stack).Add(stack.Controls[^1]); }
        void Fixed(VerticalStack stack, Control control) { stack.Controls.Add(control); Order(stack).Add(control); }
        void Note(VerticalStack stack, string text) => Fixed(stack, TextLabel(text));
        Control Row(VerticalStack stack, Control editor) =>
            Move(stack, editor.Parent as SettingRow ?? throw new InvalidOperationException("Missing settings row: " + editor.Name));
        Control Move(VerticalStack stack, Control control)
        {
            // Recorded only: controls change parents when Simple is chosen.
            viewPlacements.Add(new(control, control.Parent!, control.Parent!.Controls.GetChildIndex(control)));
            Order(stack).Add(control);
            return control;
        }
        Control Named(string name) => Controls.Find(name, true).Single();
    }

    private bool EnterAdvancedView()
    {
        viewPicker.Advanced = true;
        return viewPicker.Advanced;
    }

    private bool ConfirmAdvanced()
    {
        using var dialog = new AdvancedViewConfirmationForm();
        LauncherAppearance.Apply(dialog);
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private void ApplyView(bool advanced)
    {
        if (quickPage is null) return;
        var showingQuick = Tabs.TabPages.Contains(quickPage);
        if (advanced != showingQuick) return; // Already showing it.
        Tabs.SuspendLayout();
        try
        {
            if (!advanced)
            {
                if (Tabs.SelectedTab is { } selected && fullPages.Contains(selected)) lastFullPage = selected;
                foreach (var (stack, order) in quickOrder)
                    for (var i = 0; i < order.Count; ++i) { stack.Controls.Add(order[i]); stack.Controls.SetChildIndex(order[i], i); }
                Tabs.TabPages.Clear(); Tabs.TabPages.Add(quickPage);
                LauncherAppearance.Apply(quickPage);
            }
            else
            {
                foreach (var group in viewPlacements.GroupBy(p => p.FullParent))
                    foreach (var placement in group.OrderBy(p => p.FullIndex))
                    {
                        placement.FullParent.Controls.Add(placement.Control);
                        placement.FullParent.Controls.SetChildIndex(placement.Control, placement.FullIndex);
                        VerticalStack.SetShown(placement.Control, true);
                    }
                Tabs.TabPages.Clear(); Tabs.TabPages.AddRange(fullPages);
                Tabs.SelectedTab = lastFullPage ?? fullPages[0];
            }
        }
        finally { Tabs.ResumeLayout(true); }
        EnableActions();
    }

    // In Simple view only the rows that apply to the launch mode are shown.
    private void UpdateSimpleContext()
    {
        simpleSeat.Enabled = !testing && !supervising && !recovering && catalog.Cars.Count > 0;
        if (viewPicker.Advanced || quickPage is null || !Tabs.TabPages.Contains(quickPage)) return;
        var mode = (launchMode.SelectedItem as Option)?.Id ?? "Normal";
        foreach (var row in simpleEventRows) VerticalStack.SetShown(row, mode != "Normal");
        foreach (var row in simpleRaceRows) VerticalStack.SetShown(row, mode == "Race");
    }
}

internal sealed class LauncherViewPicker : FlowLayoutPanel
{
    private readonly RadioButton simple = new() { Name = "SimpleView", Text = "Simple", Appearance = Appearance.Button, AutoSize = true, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleCenter };
    private readonly RadioButton advanced = new() { Name = "AdvancedView", Text = "Advanced", Appearance = Appearance.Button, AutoSize = true, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleCenter, Checked = true };
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Advanced { get => advanced.Checked; set { if (value) advanced.Checked = true; else simple.Checked = true; } }
    public event EventHandler? ViewChanged;
    public LauncherViewPicker()
    {
        Name = "LauncherView"; AccessibleName = "Simple or Advanced launcher view";
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; WrapContents = false;
        Anchor = AnchorStyles.Right; Margin = new Padding(8, 0, 8, 0);
        Controls.AddRange([simple, advanced]);
        advanced.CheckedChanged += (_, _) => ViewChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class AdvancedViewConfirmationForm : LauncherForm
{
    internal Control Content { get; }

    internal AdvancedViewConfirmationForm()
    {
        Text = "Switch to Advanced view"; Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi; FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false; ClientSize = new Size(420, 165);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 2 };
        body.ColumnStyles.Add(new(SizeType.Percent, 100));
        body.RowStyles.Add(new(SizeType.Percent, 100)); body.RowStyles.Add(new(SizeType.AutoSize));
        var message = new Label { Name = "AdvancedViewMessage", Text = "This shows every launcher option.\nYour settings stay unchanged.",
            AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false };
        var actions = new FlowLayoutPanel { Name = "AdvancedViewConfirmationActions", AutoSize = true, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        var cancel = new Button { Name = "CancelAdvancedView", Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        var approve = new Button { Name = "ApproveAdvancedView", Text = "Switch", DialogResult = DialogResult.OK, AutoSize = true, Padding = cancel.Padding };
        actions.Controls.AddRange([cancel, approve]); body.Controls.Add(message, 0, 0); body.Controls.Add(actions, 0, 1);
        Controls.Add(body); Content = body; AcceptButton = approve; CancelButton = cancel;
        ActiveControl = cancel;
    }
}
