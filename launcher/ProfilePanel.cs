using System.Text.Json;

namespace GRIDAutosportVR;

// Settings → Career profiles, ported from GRID2VR's ProfilePanel.cs.

// Career selection is immediate and independent of graphics/control preferences.
internal sealed class ProfilePanel : VerticalStack
{
    readonly ProfileStore store;
    readonly Label used = Label("Used for all launches: Current game career");
    readonly Label message = Label("");
    readonly ListBox list = new() { Name = "CareerProfiles", AccessibleName = "Career profiles", Height = 150,
        IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed };
    readonly Button use = Button("Use this profile", "UseProfile"), refresh = Button("Refresh", "RefreshProfiles"),
        create = Button("Create new profile…", "CreateProfile"), delete = Button("Delete profile…", "DeleteProfile");
    readonly Label name = Label(""), status = Label(""), progress = Label(""), saved = Label(""), files = Label("");
    string selected = ProfileStore.CurrentCareer;
    bool idle;
    // Career details (CareerSaves.cs), read off the UI thread per save folder.
    internal Func<string?> GameFolder = () => null;
    readonly Dictionary<string, string> careers = [];
    int reading;
    sealed record Row(ProfileEntry Entry, bool Active)
    {
        public override string ToString() => (Entry.Info?.Name ?? (Entry.Id == ProfileStore.CurrentCareer ? "Current game career" : "Unavailable profile")) + (Active ? " — Active" : "");
    }

    internal ProfilePanel(ProfileStore profiles)
    {
        store = profiles; Name = "ProfilePanel";
        Controls.Add(used); Controls.Add(list);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 12) };
        buttons.Controls.AddRange([use, refresh, create, delete]); Controls.Add(buttons);
        Controls.Add(new SettingRow("Name", name)); Controls.Add(new SettingRow("Career", status));
        Controls.Add(new SettingRow("Progress", progress));
        Controls.Add(new SettingRow("Last saved", saved)); Controls.Add(new SettingRow("Save files", files));
        Controls.Add(Label("Each profile is a separate GRID Autosport career with its own saves; graphics settings and launcher controls are shared. The selected profile is used by Launch VR and by Launch on the desktop, which then starts through the launcher too. Your current Steam career stays as it is and is used again when you choose Current game career."));
        Controls.Add(Label("A fresh career starts at the game's Load Saved Data screen with no save: choose Start new game. Profiles stay on this PC; they do not sync to Steam Cloud. Close GRID Autosport before switching, creating or deleting profiles."));
        Controls.Add(message);
        list.SelectedIndexChanged += (_, _) => Details();
        list.DrawItem += DrawProfile;
        list.FontChanged += (_, _) => list.ItemHeight = list.Font.Height + LauncherAppearance.Px(list, 10);
        list.ItemHeight = list.Font.Height + 10;
        refresh.Click += (_, _) => Reload();
        use.Click += (_, _) => Change(() => store.Select(Current()!.Entry.Id));
        create.Click += (_, _) =>
        {
            using var dialog = new CreateProfileForm(store);
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK) Reload();
        };
        delete.Click += (_, _) =>
        {
            var row = Current(); if (row?.Entry.Info == null) return;
            if (MessageBox.Show(FindForm(), $"Delete profile {row.Entry.Info.Name} and its career saves? This cannot be undone.",
                "Delete profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                Change(() => store.Delete(row.Entry.Id));
        };
        SetIdle(false);
    }

    Row? Current() => list.SelectedItem as Row;
    internal void SetIdle(bool value) { idle = value; Actions(); }
    void Actions()
    {
        var row = Current(); create.Enabled = idle;
        use.Enabled = idle && row != null && (row.Entry.Info != null || row.Entry.Id == ProfileStore.CurrentCareer) && !row.Active;
        delete.Enabled = idle && row?.Entry.Info != null && !row.Active && row.Entry.Id != ProfileStore.CurrentCareer;
    }
    void Change(Action action)
    {
        try { action(); Reload(); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or JsonException)
        { message.Text = error.Message; }
    }
    internal void Reload()
    {
        var highlighted = Current()?.Entry.Id;
        try
        {
            string? selectionError = null;
            try { selected = store.SelectedId(); }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or JsonException)
            { selected = ""; selectionError = error.Message; }
            var entries = store.List();
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var entry in entries) list.Items.Add(new Row(entry, entry.Id == selected));
                list.SelectedItem = list.Items.Cast<Row>().FirstOrDefault(r => r.Entry.Id == highlighted)
                    ?? list.Items.Cast<Row>().FirstOrDefault(r => r.Active) ?? list.Items.Cast<Row>().FirstOrDefault();
            }
            finally { list.EndUpdate(); }
            var active = entries.FirstOrDefault(e => e.Id == selected);
            used.Text = "Used for all launches: " + (active?.Info?.Name ?? "Unavailable profile");
            message.Text = selectionError ?? active?.Error ?? "Profile changes apply immediately to the next launch.";
            Details();
            ReadCareers(entries);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or JsonException)
        { used.Text = "Profile selection unavailable"; message.Text = error.Message; list.Items.Clear(); Details(); }
    }
    void Details()
    {
        var row = Current(); var info = row?.Entry.Info;
        name.Text = info?.Name ?? ""; status.Text = info?.Status ?? row?.Entry.Error ?? "";
        saved.Text = info?.LastSavedUtc?.ToLocalTime().ToString("g") ?? (info == null ? "" : "Not saved yet");
        files.Text = info == null ? "" : $"{info.FileCount} files · {info.Bytes / 1024.0:N1} KiB";
        progress.Text = info == null ? "" : careers.GetValueOrDefault(row!.Entry.Id) ?? (info.FileCount == 0 ? "No races yet" : "Reading…");
        Actions();
    }

    // Races completed, from each career's save directory; the game folder supplies the save codec.
    void ReadCareers(IReadOnlyList<ProfileEntry> entries)
    {
        var folders = new Dictionary<string, string>();
        foreach (var entry in entries.Where(e => e.Info is { FileCount: > 0 }))
        {
            try { if (store.SaveFolder(entry.Id) is { } folder) folders[entry.Id] = folder; }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { careers[entry.Id] = error.Message; }
        }
        if (folders.Count == 0) return;
        var game = GameFolder();
        var ticket = ++reading;
        Task.Run(() => CareerSaves.Read(folders.Values, game)).ContinueWith(task =>
        {
            if (ticket != reading || IsDisposed || !IsHandleCreated) return;
            foreach (var (id, folder) in folders)
                careers[id] = task.IsFaulted ? "Career details unavailable: " + task.Exception!.GetBaseException().Message
                    : task.Result.TryGetValue(folder, out var read)
                        ? read.Summary?.Text ?? (read.Error is { } error ? "Career details unavailable: " + error : "No races yet")
                        : "Career details unavailable";
            Details();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
    void DrawProfile(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= list.Items.Count) return;
        var row = (Row)list.Items[e.Index]; var focused = (e.State & DrawItemState.Selected) != 0;
        var back = list.BackColor; var ink = list.ForeColor;
        if (focused) { back = SystemColors.Highlight; ink = SystemColors.HighlightText; }
        if (row.Active && !SystemInformation.HighContrast)
        { back = LauncherAppearance.Dark ? Color.FromArgb(83, 36, 39) : Color.FromArgb(255, 221, 219); ink = LauncherAppearance.Accent; }
        using var brush = new SolidBrush(back); e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, row.ToString(), e.Font, Rectangle.Inflate(e.Bounds, -8, 0), ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
    }
    static Label Label(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 4, 0, 10) };
    static Button Button(string text, string name) => new() { Text = text, Name = name, AutoSize = true, Padding = new Padding(5, 2, 5, 2) };
}

internal sealed class CreateProfileForm : LauncherForm
{
    internal CreateProfileForm(ProfileStore store)
    {
        Text = "Create new profile"; Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(440, 420); MinimumSize = new Size(400, 400);
        var body = new VerticalStack { Padding = new Padding(20) };
        var input = new TextBox { Name = "ProfileName", AccessibleName = "Profile name", MaxLength = 24 };
        var fresh = new RadioButton { Text = "Fresh career", AutoSize = true, Checked = true };
        var copy = new RadioButton { Text = "Copy current game career", AutoSize = true };
        var hint = new Label { AutoSize = true, Text = "1–24 letters (A–Z) or numbers (0–9). No spaces or symbols.", Margin = new Padding(0, 8, 0, 10) };
        var explanation = new Label { AutoSize = true, Text = "Creates a separate career and selects it for the next launch. A fresh career starts with no save: in the game choose Start new game. Copy current game career starts from a copy of your Steam career. Your existing saves are not changed.", Margin = new Padding(0, 12, 0, 12) };
        var error = new Label { AutoSize = true, UseMnemonic = false };
        var submit = new Button { Text = "Create", AutoSize = true, Enabled = false };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true }; buttons.Controls.AddRange([submit, cancel]);
        body.Controls.AddRange([new Label { Text = "Profile name", AutoSize = true }, input, hint]);
        body.Controls.AddRange([fresh, copy]);
        body.Controls.AddRange([explanation, buttons, error]);
        Controls.Add(body); AutoScroll = true; AcceptButton = submit; CancelButton = cancel;
        input.TextChanged += (_, _) => submit.Enabled = ProfileStore.IsValidNewName(input.Text);
        submit.Click += (_, _) =>
        {
            try
            {
                var start = copy.Checked ? CareerStart.CopyCurrent : CareerStart.Fresh;
                store.Create(input.Text, start);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception problem) when (problem is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or JsonException)
            { error.Text = problem.Message; }
        };
    }
}
