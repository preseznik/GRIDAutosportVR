// Self-tests for Help / About and update checks (Updates.cs): versions, release parsing and the
// Help / About window with a stubbed release check (no network).
using System.Text.Json;

namespace GRIDAutosportVR;

internal static class UpdateTests
{
    public static void Run(string output, Action<bool, string?> check)
    {
        check(ReleaseVersion.Parse("v0.2.0") is { Major: 0, Minor: 2, Patch: 0, Pre: "" }, "parse");
        check(ReleaseVersion.Parse("cockpit-textures-1.0.0") is null && ReleaseVersion.Parse("v01.2.3") is null, "non-release tags");
        check(ReleaseVersion.Parse("v1.0.0")!.CompareTo(ReleaseVersion.Parse("v1.0.0-beta.2")) > 0 &&
              ReleaseVersion.Parse("v1.0.0-beta.10")!.CompareTo(ReleaseVersion.Parse("v1.0.0-beta.2")) > 0 &&
              ReleaseVersion.Parse("v0.10.0")!.CompareTo(ReleaseVersion.Parse("v0.9.9")) > 0, "ordering");

        string Release(string tag, bool prerelease, string assetName, string digest, string? url = null) => JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease,
            assets = new[] { new { name = assetName, state = "uploaded", size = 1234L, digest,
                browser_download_url = url ?? Branding.Repository + "/releases/download/" + tag + "/" + assetName } },
        });
        var hash = "sha256:" + new string('a', 64);
        JsonElement Parse(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
        var stable = Updates.ReadRelease(Parse(Release("v0.2.0", false, "GRIDAutosportVR-0.2.0-Setup.exe", hash)), false);
        check(stable is { Tag: "v0.2.0", Installer: { Size: 1234 } } && stable.Installer.Sha256 == new string('a', 64), "stable release");
        check(Updates.ReadRelease(Parse(Release("v0.3.0-beta.1", true, "GRIDAutosportVR-0.3.0-beta.1-Setup.exe", hash)), false) is null, "prerelease hidden from Stable");
        check(Updates.ReadRelease(Parse(Release("v0.3.0-beta.1", true, "GRIDAutosportVR-0.3.0-beta.1-Setup.exe", hash)), true) is { Installer: not null }, "prerelease for Experimental");
        check(Updates.ReadRelease(Parse(Release("cockpit-textures-1.0.0", false, "GRIDAutosportVR-CockpitTextures-1.0.0.zip", hash)), true) is null, "pack release ignored");
        check(Updates.ReadRelease(Parse(Release("v0.2.0", false, "GRIDAutosportVR-0.2.0-Setup.exe", "")), false) is { Installer: null }, "no digest, no installer");
        check(Updates.ReadRelease(Parse(Release("v0.2.0", false, "GRIDAutosportVR-0.2.0-Setup.exe", hash, "https://example.com/x.exe")), false) is { Installer: null }, "foreign URL refused");
        var outside = Path.Combine(output, "GRIDAutosportVR-0.2.0-Setup.exe");
        File.WriteAllText(outside, "x");
        check(Throws(() => Updates.InstallerCommand(outside, stable!.Installer!, output)), "installer outside the download folder");

        // Help / About with a stubbed check (an available update with an installer).
        using var about = new AboutForm(null, true, (_, _) => Task.FromResult<AppRelease?>(stable), checkNow: true)
        { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        about.Show(); Application.DoEvents();
        for (var i = 0; i < 50 && about.AvailableUpdate is null; ++i) { Thread.Sleep(20); Application.DoEvents(); }
        check(about.AvailableUpdate?.Tag == "v0.2.0", "About shows the stubbed update");
        check(Find<Label>(about, "UpdateStatus")?.Text.StartsWith("Update available: v0.2.0") == true && Find<Button>(about, "InstallUpdate")?.Enabled == true, "install offered");
        check(Find<Label>(about, "BuildVersion")?.Text.Contains(BuildInfo.Version) == true, "version shown");
        using (var image = new Bitmap(about.Width, about.Height))
        {
            about.DrawToBitmap(image, new Rectangle(Point.Empty, about.Size));
            image.Save(Path.Combine(output, "help-about.png"));
        }
        about.Close();
    }

    private static T? Find<T>(Control root, string name) where T : Control =>
        root.Controls.Find(name, true).OfType<T>().FirstOrDefault();

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (IOException) { return true; }
    }
}
