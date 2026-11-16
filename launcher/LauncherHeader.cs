namespace GRIDAutosportVR;

// GRID2VR's header order: logo, title, then (right-aligned) channel, the Simple / Advanced picker
// and Help / About.
internal sealed class LauncherHeader : TableLayoutPanel
{
    public LauncherHeader(Control brand, Label caption, Control channel, Control picker, Control about)
    {
        Name = "LauncherHeader"; ColumnCount = 5; RowCount = 1; Dock = DockStyle.Fill; AutoSize = true;
        ColumnStyles.Add(new(SizeType.AutoSize)); ColumnStyles.Add(new(SizeType.Percent, 100));
        ColumnStyles.Add(new(SizeType.AutoSize)); ColumnStyles.Add(new(SizeType.AutoSize)); ColumnStyles.Add(new(SizeType.AutoSize));
        caption.AutoSize = false; caption.AutoEllipsis = true; caption.Dock = DockStyle.Fill;
        caption.TextAlign = ContentAlignment.MiddleLeft; caption.MinimumSize = new Size(1, 44);
        channel.Anchor = AnchorStyles.Right; channel.Margin = new Padding(8, 0, 8, 0);
        Controls.Add(brand, 0, 0); Controls.Add(caption, 1, 0); Controls.Add(channel, 2, 0); Controls.Add(picker, 3, 0); Controls.Add(about, 4, 0);
    }
}
