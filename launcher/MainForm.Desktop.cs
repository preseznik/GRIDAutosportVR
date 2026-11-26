namespace GRIDAutosportVR;

// Graphics → Desktop: VSync, anti-aliasing and the display mode for Launch on the desktop, as
// GRID2VR's Desktop graphics. VSync is on by default: without it the desktop game renders as fast as
// it can and tears. The launcher writes them into the game's own graphics settings just before a
// desktop Launch (Normal or Practice / Race), so they stay as if chosen in the game's menu. Launch VR
// stages its own copy with VSync off (the headset sets the frame rate) and restores the player's.
internal sealed partial class MainForm
{
    private readonly CheckBox desktopVSync = new() { Name = "DesktopVSync", AccessibleName = "Desktop VSync", Text = "On", AutoSize = true, Checked = true };
    private readonly ComboBox desktopMsaa = Choice("DesktopMsaa"), desktopDisplay = Choice("DesktopDisplay");

    private void InitializeDesktopGraphics(Control parent)
    {
        Section(parent, "Desktop");
        Field(parent, "VSync", desktopVSync);
        desktopMsaa.Items.AddRange(GraphicsCatalog.VrMsaa.Select(m => (object)new Option(m.Id, m.Label)).ToArray());
        Field(parent, "Anti-aliasing", desktopMsaa);
        desktopDisplay.Items.AddRange(GraphicsCatalog.Displays.Select(d => (object)new Option(d.Id, d.Label)).ToArray());
        Field(parent, "Display", desktopDisplay);
        parent.Controls.Add(TextLabel("For Launch on the desktop, Normal and Practice / Race. VSync On (default) holds the frame rate to your monitor's refresh rate, without tearing. The launcher sets these in the game's own graphics settings just before the desktop Launch, so they stay as if chosen in the game's menu. Launch VR always runs without VSync, so the headset sets the frame rate, and puts your settings back afterwards."));
        ShowDesktopGraphics(new Choices());
        desktopVSync.CheckedChanged += (_, _) => ChoiceEdited();
        foreach (var combo in new[] { desktopMsaa, desktopDisplay }) combo.SelectedIndexChanged += (_, _) => ChoiceEdited();
        tips.SetToolTip(desktopVSync, "Desktop Launch only. On (default) syncs frames to the monitor's refresh rate. Launch VR always turns VSync off so the headset sets the frame rate.");
        tips.SetToolTip(desktopMsaa, "Multisample anti-aliasing for desktop Launch. Game setting keeps the game's own choice.");
        tips.SetToolTip(desktopDisplay, "Fullscreen or a window for desktop Launch, at the resolution the game has. Game setting keeps the game's own choice.");
    }

    private void ShowDesktopGraphics(Choices choices)
    {
        desktopVSync.Checked = choices.DesktopVSync;
        desktopMsaa.SelectedItem = desktopMsaa.Items.Cast<Option>().FirstOrDefault(o => o.Id == choices.DesktopMsaa) ?? desktopMsaa.Items[0];
        desktopDisplay.SelectedItem = desktopDisplay.Items.Cast<Option>().FirstOrDefault(o => o.Id == choices.DesktopDisplay) ?? desktopDisplay.Items[0];
    }

    private Choices WithDesktopGraphics(Choices choices) => choices with
    {
        DesktopVSync = desktopVSync.Checked,
        DesktopMsaa = (desktopMsaa.SelectedItem as Option)?.Id ?? GraphicsCatalog.GameSettings,
        DesktopDisplay = (desktopDisplay.SelectedItem as Option)?.Id ?? GraphicsCatalog.GameSettings,
    };

    // Before a desktop Launch: the chosen VSync, anti-aliasing and display mode in the game's graphics
    // settings. Returns false (with the reason shown) when they cannot be written; a game that has
    // not saved its settings yet keeps its first-start defaults.
    private bool ApplyDesktopGraphics()
    {
        try
        {
            var path = GraphicsCatalog.ConfigPath;
            if (!File.Exists(path)) return true;
            if (new FileInfo(path).Length > GraphicsCatalog.MaximumConfigBytes) throw new IOException("The game's graphics settings file is unexpectedly large.");
            var choices = Current;
            var original = File.ReadAllBytes(path);
            var updated = GraphicsCatalog.ApplyDesktop(original, choices.DesktopVSync, choices.DesktopMsaa, choices.DesktopDisplay);
            if (ReferenceEquals(updated, original)) return true;
            var temporary = path + ".gasvr-desktop.tmp";
            File.WriteAllBytes(temporary, updated);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            notice.Text = "Desktop graphics could not be set: " + error.Message;
            return false;
        }
    }
}
