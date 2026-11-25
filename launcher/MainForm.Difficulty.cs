namespace GRIDAutosportVR;

// Launcher -> Difficulty: where Practice and Race start (Difficulty, bridge/difficulty.cpp).
internal sealed partial class MainForm
{
    private readonly CheckBox launcherDifficulty = new()
    {
        Name = "LauncherDifficulty", AccessibleName = "Set the difficulty for Practice and Race", Text = "Set the difficulty for Practice and Race", AutoSize = true,
    };
    private readonly ComboBox transmission = Choice("Transmission"), aiDifficulty = Choice("AiDifficulty"), damage = Choice("Damage"),
        flashbacks = Choice("Flashbacks");
    private readonly CheckBox abs = Assist("Abs", "ABS"), tractionControl = Assist("TractionControl", "Traction control"),
        stabilityControl = Assist("StabilityControl", "Stability control"), corneringAssist = Assist("CorneringAssist", "Cornering assist"),
        steeringAssist = Assist("SteeringAssist", "Steering assist");
    private readonly FlowLayoutPanel assists = new() { Name = "Assists", AutoSize = true, WrapContents = true, Margin = Padding.Empty };

    private static CheckBox Assist(string name, string text) => new() { Name = name, AccessibleName = text, Text = text, AutoSize = true };

    private void InitializeDifficulty(Control page)
    {
        Section(page, "Practice and Race");
        page.Controls.Add(launcherDifficulty);
        transmission.Items.AddRange([.. Difficulty.Transmissions.Select(t => new Option(t.Id, t.Label))]);
        aiDifficulty.Items.AddRange([.. Difficulty.AiLevels.Select((label, level) => new Option(level.ToString(System.Globalization.CultureInfo.InvariantCulture), label))]);
        damage.Items.AddRange([new Option("full", "Full"), new Option("visual", "Visual only")]);
        flashbacks.Items.AddRange([.. Enumerable.Range(0, Difficulty.MaxFlashbacks + 1).Select(n => new Option(n.ToString(System.Globalization.CultureInfo.InvariantCulture), Difficulty.FlashbackLabel(n)))]);
        Field(page, "Transmission", transmission);
        Field(page, "AI difficulty", aiDifficulty);
        Field(page, "Damage", damage);
        Field(page, "Flashbacks", flashbacks);
        foreach (var assist in new[] { abs, tractionControl, stabilityControl, corneringAssist, steeringAssist }) assists.Controls.Add(assist);
        Field(page, "Assists", assists);
        page.Controls.Add(TextLabel("Practice and Race start without your save profile, so they use the game's default difficulty: Medium AI, automatic transmission, ABS and traction control on, full damage and 5 flashbacks. Turn this on to start them with your own choices instead. The game's Difficulty screen in the pause menu still works during the session: a setting you change there applies from then on. Normal launches keep the difficulty from your save profile."));
        page.Controls.Add(TextLabel("A manual H-pattern transmission needs a shifter bound to Gear 1–7 and Reverse in Controls → Driving; a transmission with clutch needs Clutch bound."));
        launcherDifficulty.CheckedChanged += (_, _) => { EnableDifficulty(); ChoiceEdited(); };
        foreach (var combo in new[] { transmission, aiDifficulty, damage, flashbacks }) combo.SelectedIndexChanged += (_, _) => ChoiceEdited();
        foreach (var assist in new[] { abs, tractionControl, stabilityControl, corneringAssist, steeringAssist }) assist.CheckedChanged += (_, _) => ChoiceEdited();
        ShowDifficulty(new Choices());
    }

    private void EnableDifficulty()
    {
        foreach (Control editor in new Control[] { transmission, aiDifficulty, damage, flashbacks, assists }) editor.Enabled = launcherDifficulty.Checked;
    }

    private void ShowDifficulty(Choices choices)
    {
        launcherDifficulty.Checked = choices.LauncherDifficulty;
        static void Select(ComboBox combo, string id) =>
            combo.SelectedItem = combo.Items.Cast<Option>().FirstOrDefault(o => o.Id == id) ?? combo.Items.Cast<Option>().FirstOrDefault();
        Select(transmission, choices.Transmission);
        Select(aiDifficulty, choices.AiDifficulty.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Select(damage, choices.FullDamage ? "full" : "visual");
        Select(flashbacks, choices.Flashbacks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        abs.Checked = choices.Abs; tractionControl.Checked = choices.TractionControl; stabilityControl.Checked = choices.StabilityControl;
        corneringAssist.Checked = choices.CorneringAssist; steeringAssist.Checked = choices.SteeringAssist;
        EnableDifficulty();
    }

    private Choices WithDifficulty(Choices choices) => choices with
    {
        LauncherDifficulty = launcherDifficulty.Checked,
        Transmission = (transmission.SelectedItem as Option)?.Id ?? "automatic",
        AiDifficulty = int.TryParse((aiDifficulty.SelectedItem as Option)?.Id, out var ai) ? ai : 2,
        FullDamage = (damage.SelectedItem as Option)?.Id != "visual",
        Flashbacks = int.TryParse((flashbacks.SelectedItem as Option)?.Id, out var count) ? count : 5,
        Abs = abs.Checked, TractionControl = tractionControl.Checked, StabilityControl = stabilityControl.Checked,
        CorneringAssist = corneringAssist.Checked, SteeringAssist = steeringAssist.Checked,
    };
}
