// Help / About and update checks, ported from GRID2VR (launcher/Updates.cs, UpdatesPanel.cs,
// AboutForm.cs). Releases are this repository's GitHub releases tagged vX.Y.Z (a prerelease tag
// such as v0.2.0-beta.1 is Experimental) with the installer GRIDAutosportVR-X.Y.Z-Setup.exe; other
// tags (cockpit-textures-1.0.0) are not launcher releases. A download is checked against the
// release's size and SHA-256 digest before its installer runs, into this game folder.
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GRIDAutosportVR;

internal sealed record ReleaseVersion(int Major, int Minor, int Patch, string Pre) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion? Parse(string text)
    {
        var m = Regex.Match(text, @"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?\z");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var a) || !int.TryParse(m.Groups[2].Value, out var b) || !int.TryParse(m.Groups[3].Value, out var c)) return null;
        var pre = m.Groups[4].Value;
        if (pre.Split('.').Any(s => s.Length > 1 && s.All(char.IsAsciiDigit) && s[0] == '0')) return null;
        return new(a, b, c, pre);
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        foreach (var value in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) }) if (value != 0) return value;
        if (Pre == other.Pre) return 0;
        if (Pre.Length == 0 || other.Pre.Length == 0) return Pre.Length == 0 ? 1 : -1;
        var a = Pre.Split('.'); var b = other.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = a[i].All(char.IsAsciiDigit); var bn = b[i].All(char.IsAsciiDigit);
            var n = an && bn ? a[i].Length.CompareTo(b[i].Length) : an != bn ? an ? -1 : 1 : 0;
            if (n == 0) n = string.CompareOrdinal(a[i], b[i]);
            if (n != 0) return n;
        }
        return a.Length.CompareTo(b.Length);
    }
}

internal sealed record ReleaseInstaller(string Name, Uri Url, long Size, string Sha256);
internal sealed record AppRelease(string Tag, ReleaseVersion Version, Uri Page, ReleaseInstaller? Installer);

internal sealed class Updates(HttpClient http)
{
    public const string Feed = "https://api.github.com/repos/preseznik/GRIDAutosportVR/releases";
    public const long MaxInstaller = 512L * 1024 * 1024;
    public static string CurrentVersion => BuildInfo.Version;
    public static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromMinutes(15) };
    private static HttpRequestMessage Request(Uri url)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.UserAgent.ParseAdd("GRIDAutosportVR/" + CurrentVersion);
        if (url.Host == "api.github.com")
        {
            message.Headers.Accept.ParseAdd("application/vnd.github+json");
            message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        return message;
    }
    public static AppRelease? ReadRelease(JsonElement value, bool experimental)
    {
        if (value.GetProperty("draft").GetBoolean()) return null;
        var tag = value.GetProperty("tag_name").GetString() ?? "";
        var version = ReleaseVersion.Parse(tag);
        if (version is null || !experimental && (value.GetProperty("prerelease").GetBoolean() || version.Pre.Length != 0)) return null;
        var page = new Uri(Branding.Repository + "/releases/tag/" + Uri.EscapeDataString(tag));
        var expectedName = "GRIDAutosportVR-" + tag.TrimStart('v') + "-Setup.exe";
        ReleaseInstaller? installer = null;
        foreach (var asset in value.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != expectedName || asset.GetProperty("state").GetString() != "uploaded") continue;
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString()! : "";
            var url = asset.GetProperty("browser_download_url").GetString();
            var expected = Branding.Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(expectedName);
            if (size is <= 0 or > MaxInstaller || !Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z") || url != expected) continue;
            if (installer is not null) throw new IOException("Release contains duplicate installers.");
            installer = new(expectedName, new Uri(expected), size, digest[7..].ToLowerInvariant());
        }
        return new(tag, version, page, installer);
    }
    public async Task<AppRelease?> Check(bool experimental, CancellationToken cancellation, bool allowOlder = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        AppRelease? newest = null;
        var current = ReleaseVersion.Parse(CurrentVersion) ?? new(0, 0, 0, "");
        // Page URLs are built here; no server-provided link can change the repository.
        for (int page = 1; page <= 10; page++)
        {
            using var request = Request(new Uri(Feed + $"?per_page=100&page={page}"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new IOException("The GRID Autosport VR release list is not public yet. Check the repository's Releases page.");
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var data = new MemoryStream();
            var buffer = new byte[32768]; int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (data.Length + count > 2 * 1024 * 1024) throw new IOException("Release response exceeds its size limit.");
                data.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(data.ToArray());
            var rows = document.RootElement;
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 100) throw new IOException("Invalid release response.");
            foreach (var item in rows.EnumerateArray())
            {
                var release = ReadRelease(item, experimental);
                if (release is not null && (allowOlder || release.Version.CompareTo(current) > 0) && (newest is null || release.Version.CompareTo(newest.Version) > 0)) newest = release;
            }
            if (rows.GetArrayLength() < 100) return newest;
        }
        throw new IOException("Release listing exceeded its page limit. Open Releases to check manually.");
    }
    internal static void PlainPath(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if (Path.Exists(item) && File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked update path retained.");
    }
    public static string DownloadFolder => Path.Combine(LauncherSettings.DataDirectory, "updates");
    public async Task<string> Download(ReleaseInstaller asset, IProgress<int>? progress, CancellationToken cancellation)
    {
        if (asset.Size is <= 0 or > MaxInstaller || Path.GetFileName(asset.Name) != asset.Name || !Regex.IsMatch(asset.Sha256, @"\A[0-9a-f]{64}\z") ||
            !asset.Url.AbsoluteUri.StartsWith(Branding.Repository + "/releases/download/", StringComparison.Ordinal)) throw new IOException("Invalid download metadata.");
        var folder = DownloadFolder; PlainPath(folder); Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, asset.Name); PlainPath(target);
        var partial = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var request = Request(asset.Url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != asset.Size) throw new IOException("Download size differs from release metadata.");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536]; long total = 0; int count;
                while ((count = await source.ReadAsync(buffer, cancellation)) != 0)
                {
                    total += count; if (total > asset.Size) throw new IOException("Download exceeds its declared size.");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    progress?.Report((int)(100 * total / asset.Size));
                }
                if (total != asset.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != asset.Sha256) throw new IOException("Download verification failed; download discarded.");
                await output.FlushAsync(cancellation);
            }
            cancellation.ThrowIfCancellationRequested(); File.Move(partial, target, true); return target;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    // The game folder this launcher was installed into (<game>\GRIDAutosportVR), else the chosen one.
    public static string? InstallFolder(string? gameFolder)
    {
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))?.FullName;
        return Installation.GameExists(parent) ? parent : Installation.GameExists(gameFolder) ? gameFolder : null;
    }
    public static ProcessStartInfo InstallerCommand(string path, ReleaseInstaller asset, string gameFolder)
    {
        var expected = Path.GetFullPath(Path.Combine(DownloadFolder, asset.Name));
        if (!Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Installer is outside the download folder.");
        PlainPath(path);
        using var stream = File.OpenRead(path);
        if (stream.Length != asset.Size || Convert.ToHexStringLower(SHA256.HashData(stream)) != asset.Sha256) throw new IOException("Installer changed after download.");
        // Inno Setup: install into this game folder; launcher settings live in %LOCALAPPDATA% and are kept.
        var command = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        command.ArgumentList.Add("/DIR=" + Path.GetFullPath(gameFolder)); command.ArgumentList.Add("/NORESTART");
        return command;
    }
    public static void Install(string path, ReleaseInstaller asset, string? gameFolder)
    {
        var folder = InstallFolder(gameFolder) ?? throw new IOException("Choose your GRID Autosport folder before installing an update.");
        if (Installation.SessionPending(folder)) throw new IOException("Finish the VR session and restore the original files before installing an update.");
        foreach (var name in new[] { "GRIDAutosport", "GRIDAutosport_avx" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) throw new IOException("Close GRID Autosport before installing an update."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        using var installer = Process.Start(InstallerCommand(path, asset, folder)) ?? throw new IOException("Installer did not start.");
    }
}

internal sealed record UpdatePreferences(bool CheckAtStartup = true, bool Experimental = false)
{
    private static string FilePath => Path.Combine(LauncherSettings.DataDirectory, "update-preferences.json");
    public static UpdatePreferences Load()
    {
        var path = FilePath;
        if (!File.Exists(path)) return new(Experimental: BuildInfo.Channel == "Experimental");
        if (new FileInfo(path).Length > 4096) throw new IOException("Update settings exceed their size limit.");
        return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(path)) ?? new();
    }
    public void Save()
    {
        var path = FilePath;
        Updates.PlainPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(this)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class UpdatesPanel : UserControl
{
    private readonly CheckBox startup = new() { Name = "CheckUpdatesAtStartup", Text = "Check for updates at startup", AutoSize = true };
    private readonly CheckBox experimental = new() { Name = "IncludeExperimental", Text = "Include experimental releases", AutoSize = true };
    private readonly Label status = new() { Name = "UpdateStatus", AutoSize = true };
    private readonly Button check = new() { Name = "CheckUpdates", Text = "Check for updates", AutoSize = true };
    private readonly Button install = new() { Name = "InstallUpdate", Text = "Download and install…", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Text = "Cancel download", AutoSize = true, Visible = false };
    private readonly Button notes = new() { Text = "Release notes", AutoSize = true, Enabled = false };
    private readonly Button stable = new() { Text = "Return to stable…", AutoSize = true, Visible = BuildInfo.Channel == "Experimental" };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Top, Visible = false };
    private readonly HttpClient http = Updates.CreateClient();
    private readonly Func<bool, CancellationToken, Task<AppRelease?>> checkForRelease;
    private readonly string? gameFolder;
    private CancellationTokenSource? operation;
    private AppRelease? release;
    private bool sessionIdle, initializing, returningStable;
    public event Action<AppRelease?>? UpdateAvailable;
    public AppRelease? AvailableUpdate => returningStable ? null : release;

    public UpdatesPanel(string? gameFolder, Func<bool, CancellationToken, Task<AppRelease?>>? releaseCheck = null)
    {
        this.gameFolder = gameFolder;
        checkForRelease = releaseCheck ?? ((preview, token) => new Updates(http).Check(preview, token));
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top; Name = "Updates";
        var layout = new VerticalStack(); Controls.Add(layout);
        layout.Controls.Add(startup);
        layout.Controls.Add(experimental);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        actions.Controls.AddRange([check, install, notes, stable, cancel]); layout.Controls.Add(actions); layout.Controls.Add(status); layout.Controls.Add(progress);
        status.Text = "Check GitHub Releases for a newer version. No GitHub account is needed.\nClose the game before updating. Your settings are kept.";
        layout.SizeChanged += (_, _) => status.MaximumSize = new(Math.Max(1, layout.Width - 10), 0);
        check.Click += async (_, _) => await Check(); install.Click += async (_, _) => await Download();
        cancel.Click += (_, _) => operation?.Cancel();
        notes.Click += (_, _) => { if (release is not null) Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true }); };
        stable.Click += async (_, _) => await ReturnStable();
        startup.CheckedChanged += (_, _) => Save();
        experimental.CheckedChanged += async (_, _) => { if (initializing) return; SetRelease(null); Save(); await Check(); };
    }
    public async Task Start(bool forceCheck)
    {
        try
        {
            var prefs = UpdatePreferences.Load();
            initializing = true;
            try { startup.Checked = prefs.CheckAtStartup; experimental.Checked = prefs.Experimental; }
            finally { initializing = false; }
            if (forceCheck) await Check();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { if (!IsDisposed) status.Text = ex.Message; }
    }
    private void Save()
    {
        if (initializing) return;
        try { new UpdatePreferences(startup.Checked, experimental.Checked).Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = ex.Message; }
    }
    public void SetIdle(bool idle) { sessionIdle = idle; Enable(); }
    internal void SetRelease(AppRelease? value)
    {
        if (IsDisposed) return;
        release = value; returningStable = false;
        status.Text = release is null ? "You have the latest available release." : $"Update available: {release.Tag}" + (release.Installer is null ? ". No verified installer is attached; open GitHub Releases." : ". Ready to download.");
        Enable(); UpdateAvailable?.Invoke(release);
    }
    private void Enable()
    {
        check.Enabled = experimental.Enabled = operation is null;
        notes.Enabled = operation is null && release is not null;
        stable.Enabled = sessionIdle && operation is null;
        install.Enabled = sessionIdle && operation is null && release?.Installer is not null;
    }
    private async Task Check()
    {
        if (IsDisposed || operation is not null) return;
        operation = new(); Enable(); status.Text = "Checking GitHub…";
        var token = operation.Token;
        returningStable = false;
        try { var result = await checkForRelease(experimental.Checked, token); if (!IsDisposed && !token.IsCancellationRequested) SetRelease(result); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        { if (!IsDisposed) { SetRelease(null); status.Text = "Update check failed: " + ex.Message; } }
        finally { operation.Dispose(); operation = null; if (!IsDisposed) Enable(); }
    }
    private async Task ReturnStable()
    {
        if (operation is not null || !sessionIdle) return;
        operation = new(); Enable(); status.Text = "Finding the latest Stable release…";
        try
        {
            var candidate = await new Updates(http).Check(false, operation.Token, allowOlder: true);
            if (IsDisposed) return;
            if (candidate?.Installer is null) throw new IOException("No verified Stable installer is available yet.");
            if (MessageBox.Show(this, $"Install Stable {candidate.Tag}? This may be an older version. Your settings are kept.", "Return to stable", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            release = candidate; returningStable = true; status.Text = "Stable release selected. Choose Download and install to continue.";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or TaskCanceledException) { if (!IsDisposed) status.Text = ex.Message; }
        finally { operation.Dispose(); operation = null; if (!IsDisposed) Enable(); }
    }
    private async Task Download()
    {
        if (operation is not null || release?.Installer is not { } asset || !sessionIdle) return;
        operation = new(); Enable(); progress.Value = 0; progress.Visible = cancel.Visible = true; status.Text = "Downloading " + release.Tag + "…";
        try
        {
            var token = operation.Token;
            var reporting = new Progress<int>(p => { if (!IsDisposed) progress.Value = Math.Clamp(p, 0, 100); });
            var file = await Task.Run(() => new Updates(http).Download(asset, reporting, token), token);
            if (IsDisposed || token.IsCancellationRequested) return;
            if (!sessionIdle) throw new IOException("Download verified. Finish the session before installing.");
            var previous = UpdatePreferences.Load();
            if (returningStable) new UpdatePreferences(previous.CheckAtStartup, false).Save();
            try { Updates.Install(file, asset, gameFolder); }
            catch { if (returningStable) previous.Save(); throw; }
            // The installer replaces the launcher; closing it releases its files (the installer waits for it).
            Application.Exit();
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Download cancelled."; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { if (!IsDisposed) status.Text = ex.Message; }
        finally { operation.Dispose(); operation = null; if (!IsDisposed) { progress.Visible = cancel.Visible = false; Enable(); } }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { operation?.Cancel(); http.Dispose(); }
        base.Dispose(disposing);
    }
}

// Help / About (F1 or ? in the header), as GRID2VR: version and build, links, updates and the README.
internal sealed class AboutForm : LauncherForm
{
    private readonly UpdatesPanel updates;
    public AppRelease? AvailableUpdate => updates.AvailableUpdate;

    public AboutForm(string? gameFolder, bool sessionIdle, Func<bool, CancellationToken, Task<AppRelease?>>? releaseCheck = null, bool checkNow = true)
    {
        Text = Branding.Title + " — Help / About";
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 680); MinimumSize = new Size(560, 540);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false;

        var tabs = new ModernTabs { Name = "HelpTabs", Dock = DockStyle.Fill };
        var about = new TabPage("About") { AutoScroll = true };
        var instructions = new TabPage("Instructions") { Padding = new Padding(16) };
        tabs.TabPages.AddRange([about, instructions]);
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(20), ColumnCount = 1 };
        content.ColumnStyles.Add(new(SizeType.Percent, 100)); about.Controls.Add(content);
        AddText(content, Branding.Title).Font = new Font(Font.FontFamily, 20, FontStyle.Bold);
        AddText(content, $"Version {BuildInfo.Version} — {BuildInfo.Channel}").Name = "BuildVersion";
        AddText(content, $"Build: {BuildInfo.Revision} — {BuildInfo.Built}").Name = "BuildDetails";
        AddText(content, "Developed by Bohloney").Font = new Font(Font, FontStyle.Bold);
        AddText(content, "Native stereo VR with head tracking for GRID Autosport, with a desktop launcher for direct Practice and Race sessions, driving controls, seat positions and redrawn cockpit textures.");
        var links = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        var read = new Button { Text = "Instructions", Name = "ShowInstructions", AutoSize = true };
        read.Click += (_, _) => tabs.SelectedTab = instructions;
        links.Controls.Add(read);
        void Link(string text, string target)
        {
            var button = new Button { Text = text, AutoSize = true, Tag = target };
            button.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show(this, ex.Message, Branding.Title); }
            };
            links.Controls.Add(button);
        }
        Link("GitHub / report an issue", Branding.Repository + "/issues");
        Link("Releases", Branding.Repository + "/releases");
        content.Controls.Add(links);
        updates = new UpdatesPanel(gameFolder, releaseCheck); content.Controls.Add(updates);
        updates.SetIdle(sessionIdle);
        Shown += async (_, _) => await updates.Start(forceCheck: checkNow);

        var readme = Path.Combine(AppContext.BaseDirectory, "README.md");
        var body = new RichTextBox
        {
            Name = "InstructionText", AccessibleName = "Instructions", Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            DetectUrls = true, ScrollBars = RichTextBoxScrollBars.Vertical,
            Text = File.Exists(readme) ? File.ReadAllText(readme) : "README.md is not beside the launcher. See " + Branding.Repository + ".",
        };
        body.LinkClicked += (_, e) => { try { Process.Start(new ProcessStartInfo(e.LinkText!) { UseShellExecute = true })?.Dispose(); } catch (System.ComponentModel.Win32Exception) { } };
        instructions.Controls.Add(body);

        var close = new Button { Text = "Close", Name = "CloseAbout", AutoSize = true, DialogResult = DialogResult.Cancel };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        footer.Controls.Add(close); CancelButton = close;
        Controls.Add(tabs); Controls.Add(footer);
        content.SizeChanged += (_, _) =>
        {
            foreach (var label in content.Controls.OfType<Label>()) label.MaximumSize = new Size(Math.Max(1, content.ClientSize.Width - content.Padding.Horizontal - 16), 0);
        };
    }

    private static Label AddText(TableLayoutPanel content, string text)
    {
        var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(0, 0, 0, 12) };
        content.Controls.Add(label); return label;
    }
}
