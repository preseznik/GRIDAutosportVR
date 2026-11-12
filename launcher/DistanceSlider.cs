// Ported from GRID2VR/launcher/HudDistanceSlider.cs (2026-11-12), with its range as parameters:
// a distance in metres in 0.5 m steps (VR HUD 1–20 m, seated screen 1.5–6 m).
using System.ComponentModel;

namespace GRIDAutosportVR;

internal sealed class DistanceSlider : UserControl
{
    internal const decimal HudMinimum = 1m, HudMaximum = 20m, ScreenMinimum = 1.5m, ScreenMaximum = 6m;
    private readonly decimal minimum, maximum;
    private readonly TrackBar track;
    private readonly Label valueText;
    public event EventHandler? ValueChanged;

    public DistanceSlider(string name, string label, decimal minimum, decimal maximum, decimal value)
    {
        this.minimum = minimum; this.maximum = maximum;
        track = new() { Name = name + "Slider", AccessibleName = label, Minimum = (int)(minimum * 2m), Maximum = (int)(maximum * 2m),
            Value = (int)(value * 2m), SmallChange = 1, LargeChange = 2, TickFrequency = 2, Dock = DockStyle.Fill, AutoSize = false, Margin = Padding.Empty };
        valueText = new() { Name = name + "Value", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(8, 0, 0, 0) };
        Name = name; AccessibleName = label; Dock = DockStyle.Top; Height = 42; Width = 280; MinimumSize = new Size(150, 42); TabStop = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(track); layout.Controls.Add(valueText); Controls.Add(layout);
        track.ValueChanged += (_, _) => { RefreshValue(); ValueChanged?.Invoke(this, EventArgs.Empty); };
        RefreshValue();
    }

    internal static bool Valid(decimal metres, decimal minimum, decimal maximum) =>
        metres >= minimum && metres <= maximum && metres * 2m == decimal.Truncate(metres * 2m);
    internal static bool ValidHud(decimal metres) => Valid(metres, HudMinimum, HudMaximum);
    internal static bool ValidScreen(decimal metres) => Valid(metres, ScreenMinimum, ScreenMaximum);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Value
    {
        get => track.Value / 2m;
        set
        {
            if (!Valid(value, minimum, maximum)) throw new ArgumentOutOfRangeException(nameof(value), $"{minimum:0.#}–{maximum:0.#} m in 0.5 m steps.");
            track.Value = (int)(value * 2m);
        }
    }

    private void RefreshValue()
    {
        valueText.Text = $"{Value:0.#} m";
        track.AccessibleDescription = AccessibleDescription = $"{Value:0.#} metres; {minimum:0.#}–{maximum:0.#} metres in 0.5 m steps";
    }
}
