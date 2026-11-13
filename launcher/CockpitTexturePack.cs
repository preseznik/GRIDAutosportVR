// Optional redrawn cockpit textures, ported from GRID2VR (launcher/CockpitTexturePack.cs and
// tools/Cockpit-TexturePack.ps1). The pack is a ZIP published on this repository's releases under
// the tag cockpit-textures-<version>; its size, SHA-256 and every file's SHA-256 are embedded in
// the launcher at build time (assets/cockpit-texture-pack.json, written by
// tools/build_cockpit_texture_pack.py), so a build only ever installs the pack it was made with.
// It installs into the launcher's data folder, never the game folder:
// %LOCALAPPDATA%\GRIDAutosportVR\addons\cockpit-textures\<version>. The VR renderer swaps the
// textures in memory (bridge/cockpit_textures.cpp, -gasvrcockpittextures=replace:<version>).
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GRIDAutosportVR;

internal sealed record TexturePackProgress(string Message, int Percent = 0, bool CanCancel = true);

internal sealed record CockpitPackCatalog(string Version, string Asset, long ZipBytes, string ZipSha256, long InstalledBytes, int Cars,
    IReadOnlyDictionary<string, string> Files)
{
    private static readonly Regex VersionPattern = new(@"\A\d+\.\d+\.\d+(-[a-z]+\.\d+)?\z");
    private static readonly Regex NamePattern = new(@"\A([a-z0-9]{3}\.dds|catalog\.txt|README\.txt)\z");
    private static readonly Regex HashPattern = new(@"\A[0-9a-f]{64}\z");

    public static CockpitPackCatalog Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1) throw new JsonException("Unsupported cockpit texture catalog.");
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in root.GetProperty("files").EnumerateObject()) files[file.Name] = file.Value.GetString() ?? "";
        var catalog = new CockpitPackCatalog(root.GetProperty("version").GetString() ?? "", root.GetProperty("asset").GetString() ?? "",
            root.GetProperty("zipBytes").GetInt64(), root.GetProperty("zipSha256").GetString() ?? "",
            root.GetProperty("installedBytes").GetInt64(), root.GetProperty("cars").GetInt32(), files);
        if (!VersionPattern.IsMatch(catalog.Version) || catalog.Asset != $"GRIDAutosportVR-CockpitTextures-{catalog.Version}.zip" ||
            !HashPattern.IsMatch(catalog.ZipSha256) || catalog.ZipBytes <= 0 || catalog.ZipBytes > 2_000_000_000 ||
            files.Count is < 3 or > 300 || !files.ContainsKey("catalog.txt") ||
            files.Any(f => !NamePattern.IsMatch(f.Key) || !HashPattern.IsMatch(f.Value)))
            throw new JsonException("Invalid cockpit texture catalog.");
        return catalog;
    }

    public string Folder => Path.Combine(LauncherSettings.DataDirectory, "addons", "cockpit-textures", Version);
    public Uri Download => new(Branding.Repository + "/releases/download/cockpit-textures-" + Version + "/" + Asset);
    public string SizeLabel => $"{Math.Ceiling(ZipBytes / 1_000_000d):0} MB";
}

internal static class CockpitTexturePack
{
    // The pack this build installs; null when the build has none (the option is then hidden).
    internal static readonly CockpitPackCatalog? Current = ReadEmbedded();

    private static CockpitPackCatalog? ReadEmbedded()
    {
        using var stream = typeof(CockpitTexturePack).Assembly.GetManifestResourceStream("GRIDAutosportVR.CockpitPack.json");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return CockpitPackCatalog.Parse(reader.ReadToEnd());
    }

    // Every listed file present with its hash, and nothing else in the folder.
    public static bool Installed(CockpitPackCatalog catalog)
    {
        try
        {
            var folder = new DirectoryInfo(catalog.Folder);
            if (!folder.Exists || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            var entries = folder.GetFileSystemInfos();
            if (entries.Length != catalog.Files.Count) return false;
            foreach (var entry in entries)
                if (entry is not FileInfo file || !catalog.Files.TryGetValue(file.Name, out var hash) || Sha256(file.FullName) != hash) return false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // Downloads (or, for tests and offline installs, copies a local ZIP), checks it against the
    // catalog, extracts beside the target and moves the verified folder into place.
    public static async Task Install(CockpitPackCatalog catalog, HttpClient http, IProgress<TexturePackProgress> progress,
        CancellationToken cancellation, string? localZip = null)
    {
        if (Installed(catalog)) { progress.Report(new("Cockpit textures are installed.", 100, false)); return; }
        var parent = Path.GetDirectoryName(catalog.Folder)!;
        Directory.CreateDirectory(parent);
        var zip = Path.Combine(parent, catalog.Asset + ".download");
        var staging = Path.Combine(parent, ".install-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (localZip is not null) File.Copy(localZip, zip, true);
            else await DownloadZip(catalog, http, zip, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            progress.Report(new("Checking the download…", 100, false));
            if (new FileInfo(zip).Length != catalog.ZipBytes || Sha256(zip) != catalog.ZipSha256)
                throw new IOException("The downloaded cockpit textures do not match this launcher's pack (size or SHA-256). Nothing was installed.");
            progress.Report(new("Installing cockpit textures…", 100, false));
            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var prefix = "cockpit-textures/" + catalog.Version + "/";
                if (archive.Entries.Count != catalog.Files.Count) throw new IOException("Unexpected cockpit texture archive contents.");
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.StartsWith(prefix, StringComparison.Ordinal) ? entry.FullName[prefix.Length..] : "";
                    if (!catalog.Files.ContainsKey(name) || entry.Length > 64_000_000) throw new IOException("Unexpected cockpit texture archive entry: " + entry.FullName);
                    entry.ExtractToFile(Path.Combine(staging, name), false);
                    if (Sha256(Path.Combine(staging, name)) != catalog.Files[name]) throw new IOException("A cockpit texture file failed its SHA-256 check: " + name);
                }
            }
            if (Directory.Exists(catalog.Folder)) DeleteOwned(catalog.Folder);
            Directory.Move(staging, catalog.Folder);
            if (!Installed(catalog)) throw new IOException("The installed cockpit textures failed verification.");
            RemoveOtherVersions(catalog);
            progress.Report(new("Cockpit textures installed.", 100, false));
        }
        finally
        {
            try { if (Directory.Exists(staging)) DeleteOwned(staging); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { File.Delete(zip); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task DownloadZip(CockpitPackCatalog catalog, HttpClient http, string zip, IProgress<TexturePackProgress> progress, CancellationToken cancellation)
    {
        progress.Report(new($"Downloading cockpit textures ({catalog.SizeLabel})…"));
        using var response = await http.GetAsync(catalog.Download, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"The cockpit texture download failed ({(int)response.StatusCode} {response.ReasonPhrase}): {catalog.Download}");
        await using var source = await response.Content.ReadAsStreamAsync(cancellation);
        await using var target = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        var buffer = new byte[1 << 16]; long total = 0; int last = -1;
        for (int read; (read = await source.ReadAsync(buffer, cancellation)) > 0;)
        {
            total += read;
            if (total > catalog.ZipBytes) throw new IOException("The cockpit texture download is larger than expected.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
            var percent = (int)(total * 100 / catalog.ZipBytes);
            if (percent != last) { last = percent; progress.Report(new($"Downloading cockpit textures… {percent}% of {catalog.SizeLabel}", percent)); }
        }
    }

    public static void Remove(CockpitPackCatalog catalog)
    {
        if (Directory.Exists(catalog.Folder)) DeleteOwned(catalog.Folder);
        var parent = Path.GetDirectoryName(catalog.Folder)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
    }

    // Earlier pack versions left by an older launcher: folders named like a version holding only pack files.
    private static void RemoveOtherVersions(CockpitPackCatalog catalog)
    {
        foreach (var folder in Directory.EnumerateDirectories(Path.GetDirectoryName(catalog.Folder)!))
        {
            var name = Path.GetFileName(folder);
            if (name == catalog.Version || !Regex.IsMatch(name, @"\A\d+\.\d+\.\d+(-[a-z]+\.\d+)?\z")) continue;
            try { DeleteOwned(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Deletes a pack folder only when it holds nothing but pack files (no subfolders, links or other files).
    private static void DeleteOwned(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unexpected link in the cockpit texture folder: " + folder);
        var entries = info.GetFileSystemInfos();
        if (entries.Any(e => e is not FileInfo || !Regex.IsMatch(e.Name, @"\A([a-z0-9]{3}\.dds|catalog\.txt|README\.txt)\z")))
            throw new IOException("The cockpit texture folder holds other files; it was left in place: " + folder);
        foreach (var entry in entries) entry.Delete();
        info.Delete();
    }

    internal static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

// Progress window for the download, as GRID2VR's CockpitTextureForm.
internal sealed class CockpitTextureForm : Form
{
    private readonly Label status = new() { Dock = DockStyle.Top, Height = 60, Text = "Preparing cockpit textures…" };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 24 };
    private readonly Button cancel = new() { Text = "Cancel", Dock = DockStyle.Bottom, Height = 32 };
    private readonly CancellationTokenSource cancellation = new();
    private bool finished;
    internal Exception? Failure { get; private set; }

    // localZip: a copy of the pack's ZIP to install instead of downloading it (checked the same way).
    internal CockpitTextureForm(CockpitPackCatalog catalog, string? localZip = null)
    {
        Text = "GRID Autosport VR — Cockpit textures";
        ClientSize = new Size(520, 180);
        Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) };
        content.Controls.Add(progress); content.Controls.Add(status); content.Controls.Add(cancel); Controls.Add(content);
        cancel.Click += (_, _) => CancelDownload();
        FormClosing += (_, e) => { if (!finished) { e.Cancel = true; if (cancel.Enabled) CancelDownload(); } };
        Shown += async (_, _) =>
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GRIDAutosportVR-Launcher");
            try
            {
                await CockpitTexturePack.Install(catalog, http, new Progress<TexturePackProgress>(Display), cancellation.Token, localZip);
                DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException) { DialogResult = DialogResult.Cancel; }
            catch (Exception error) { Failure = error; DialogResult = DialogResult.Abort; }
            finally { finished = true; Close(); }
        };
    }

    private void Display(TexturePackProgress update)
    {
        if (finished || IsDisposed) return;
        status.Text = update.Message; progress.Value = Math.Clamp(update.Percent, 0, 100);
        cancel.Enabled = update.CanCancel && !cancellation.IsCancellationRequested;
        progress.Style = update.CanCancel ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
    }

    private void CancelDownload() { cancellation.Cancel(); cancel.Enabled = false; status.Text = "Cancelling download…"; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) cancellation.Dispose();
        base.Dispose(disposing);
    }
}
