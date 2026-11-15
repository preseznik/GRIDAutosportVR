using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace GRIDAutosportVR;

// OpenXR layer recovery (VrApiLayers.cs, GitHub issue #1): GRID2VR's tests (launcher/VrLayerTests.cs
// and tests/Test-OpenXRLayers.ps1 there), with Autosport's probe log, supervisor and session file.
internal static class VrLayerTests
{
    private static Action<bool, string>? report;
    private static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null) => report!(value, "OpenXR layers: " + expression);
    private static bool Rejects(Action action) { try { action(); return false; } catch (IOException) { return true; } }
    private static void Click(Control control) => typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [EventArgs.Empty]);

    internal static void Run(string data, string output, Action<bool, string> check)
    {
        report = check;
        Records();
        var fixture = Path.Combine(data, "openxr-layers");
        Directory.CreateDirectory(fixture);
        Manifests(fixture);
        Launch(fixture);
        Preferences();
        Dialogs(output);
    }

    private static readonly VrLayerBypass Reshade = new("XR_APILAYER_reshade", @"C:\ReShade\reshade.json", new string('A', 64), @"C:\ReShade\ReShade64.dll", "DISABLE_XR_APILAYER_reshade");
    private static readonly VrLayerBypass Obs = new("XR_APILAYER_NOVENDOR_OBSMirror", @"C:\OBS\obs-mirror.json", new string('B', 64), @"C:\OBS\obs-mirror.dll", "DISABLE_XR_APILAYER_NOVENDOR_OBSMirror");

    private static void Records()
    {
        Check(Reshade.Valid && Obs.Valid && Reshade.DisplayName == "ReShade" && Obs.DisplayName == "OpenXR OBS Mirror" && Obs.ToString() == "OpenXR OBS Mirror");
        Check((Reshade with { LayerName = "XR_APILAYER_other" }).Consequence.StartsWith("Features provided by this OpenXR layer"));
        foreach (var valid in new[] { "DISABLE_XR_APILAYER_reshade", "DISABLE_OPENXR_x", "DISABLE_XR_" + new string('A', 180) }) Check(VrLayerBypass.SafeDisableVariable(valid));
        foreach (var invalid in new[] { "PATH", "XR_RUNTIME_JSON", "DISABLE_XR_", "DISABLE_XR_A-B", "disable_xr_a", "DISABLE_XR_A ", "DISABLE_XR_" + new string('A', 181), "DISABLE_XR_A\nPATH", null })
            Check(!VrLayerBypass.SafeDisableVariable(invalid));
        foreach (var invalid in new[] { Reshade with { DisableVariable = "PATH" }, Reshade with { DisableVariable = "XR_RUNTIME_JSON" }, Reshade with { DisableVariable = "DISABLE_XR_A;B" },
            Reshade with { LayerName = "reshade" }, Reshade with { ManifestHash = "abc" }, Reshade with { LibraryPath = "ReShade64.dll" }, Reshade with { ManifestPath = "" } })
            Check(!invalid.Valid && Rejects(() => VrApiLayers.Encode([invalid])));
        Check(Reshade.SameIdentity(Reshade with { ManifestPath = Reshade.ManifestPath.ToUpperInvariant() }) && !Reshade.SameIdentity(Reshade with { ManifestHash = new string('C', 64) }) &&
            !Reshade.SameIdentity(Reshade with { DisableVariable = "DISABLE_XR_APILAYER_RESHADE" }) && !Reshade.SameIdentity(null));

        // The supervisor's arguments: camelCase identity, no display text, bounded, round trip.
        using (var transport = JsonDocument.Parse(Convert.FromBase64String(VrApiLayers.Encode([Reshade, Obs]))))
        {
            var first = transport.RootElement[0];
            Check(transport.RootElement.GetArrayLength() == 2 && first.GetProperty("layerName").GetString() == Reshade.LayerName &&
                first.GetProperty("manifestHash").GetString() == Reshade.ManifestHash && first.GetProperty("disableVariable").GetString() == Reshade.DisableVariable &&
                !first.TryGetProperty("displayName", out _) && !first.TryGetProperty("consequence", out _) && !first.TryGetProperty("valid", out _));
        }
        Check(VrApiLayers.Decode(VrApiLayers.Encode([Reshade, Obs])) is [var a, var b] && a == Reshade && b == Obs);
        Check(Rejects(() => VrApiLayers.Encode(Enumerable.Repeat(Reshade, VrApiLayers.MaxLayers + 1).ToArray())));
        var pascal = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { Reshade })));
        Check(VrApiLayers.Decode(pascal).Single() == Reshade);
        foreach (var bad in new[] { new string('x', 16385), "not-base64!", Convert.ToBase64String(Encoding.UTF8.GetBytes("[{\"layerName\":\"XR_APILAYER_x\",\"disableVariable\":\"PATH\"}]")),
            Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")), Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Enumerable.Repeat(Reshade, 17)))) })
            Check(Rejects(() => VrApiLayers.Decode(bad)));
    }

    private static string Manifest(string name, string? library, string? disable) =>
        JsonSerializer.Serialize(new { file_format_version = "1.0.0", api_layer = new { name, library_path = library, api_version = "1.0", implementation_version = "1", description = "fixture", disable_environment = disable } });

    private static void Manifests(string fixture)
    {
        string Write(string name, string text, bool bom = false) { var path = Path.Combine(fixture, name); File.WriteAllText(path, text, new UTF8Encoding(bom)); return path; }
        // Manifests as ReShade and OBS Mirror register them; the BOM matches common installers.
        var reshade = Write("reshade.json", Manifest("XR_APILAYER_reshade", @".\ReShade64.dll", "DISABLE_XR_APILAYER_reshade"), true);
        var obs = Write("obs-mirror.json", Manifest("XR_APILAYER_NOVENDOR_OBSMirror", @".\obs-mirror.dll", "DISABLE_XR_APILAYER_NOVENDOR_OBSMirror"));
        List<VrLayerBypass> catalog = [VrApiLayers.ReadManifest(reshade)!, VrApiLayers.ReadManifest(obs)!];
        Check(catalog[0] is { LayerName: "XR_APILAYER_reshade", DisableVariable: "DISABLE_XR_APILAYER_reshade", Valid: true } first &&
            first.LibraryPath == Path.Combine(fixture, "ReShade64.dll") && first.ManifestPath == reshade && catalog[1].Valid);
        foreach (var (name, library, disable) in new[] { ("XR_APILAYER_bad", "x.dll", "PATH"), ("not_a_layer", "x.dll", "DISABLE_XR_A"), ("XR_APILAYER_empty", "", "DISABLE_XR_A"), ("XR_APILAYER_none", "x.dll", null) })
            Check(VrApiLayers.ReadManifest(Write("bad.json", Manifest(name, library, disable))) is null);
        Check(VrApiLayers.ReadManifest("relative.json") is null);
        Check(VrApiLayers.ReadManifest(Write("large.json", "{\"api_layer\":{}}" + new string(' ', 70000))) is null);
        Check(VrApiLayers.ReadManifest(Write("broken.json", "{")) is null);
        Check(VrApiLayers.Catalog() is { } installed && installed.All(layer => layer.Valid));

        // Loader 1.1.53 output with XR_LOADER_DEBUG=warn in the probe's log when every layer fails
        // to load (as GRID2VR captured from the real 32-bit loader with a 64-bit layer DLL), and
        // the probe's own lines as in issue #1.
        var log = $"""
            architecture=Win32 mode=query
            XR_RUNTIME_JSON=(unset; system loader discovery)
            XR_KHR_D3D11_enable=1
            Warning [GENERAL | xrCreateInstance | OpenXR-Loader] : ApiLayerInterface::LoadApiLayers skipping layer XR_APILAYER_reshade, failed to load with message "Failed to open dynamic library {fixture}\ReShade64.dll with error 193: 0xc1 (193): %1 is not a valid Win32 application.
            "
            Warning [GENERAL | xrCreateInstance | OpenXR-Loader] : ApiLayerInterface::LoadApiLayers skipping layer XR_APILAYER_NOVENDOR_OBSMirror, failed to load with message "Failed to open dynamic library {fixture}\obs-mirror.dll with error 126: 0x7e (126): The specified module could not be found.
            "
            Warning [GENERAL | xrCreateInstance | OpenXR-Loader] : ApiLayerInterface::LoadApiLayers skipping layer XR_APILAYER_reshade, failed to load with message "Failed to open dynamic library {fixture}\ReShade64.dll with error 193: 0xc1 (193): %1 is not a valid Win32 application.
            "
            Error [GENERAL | xrCreateInstance | OpenXR-Loader] : Failed loading layer information
            Error [GENERAL | xrCreateInstance | OpenXR-Loader] : xrCreateInstance failed
            xrCreateInstance: XrResult=-32
            error=xrCreateInstance
            exit_code=1
            """.ReplaceLineEndings("\r\n");
        var failures = VrApiLayers.Failures(log);
        Check(failures.Count == 2 && failures[0].LayerName == "XR_APILAYER_reshade" && failures[0].WindowsError == 193 && failures[1].WindowsError == 126);
        Check(VrApiLayers.Describe(failures) == "An OpenXR layer could not load: XR_APILAYER_reshade (Windows error 193); XR_APILAYER_NOVENDOR_OBSMirror (Windows error 126).");
        Check(VrApiLayers.Failures(log.Replace("XrResult=-32", "XrResult=-2")).Count == 0);
        Check(VrApiLayers.Failures(log.Replace("Failed loading layer information", "")).Count == 0);
        Check(VrApiLayers.Failures(log.Replace(fixture + @"\obs-mirror.dll", "obs-mirror.dll")).Count == 1);
        // Issue #1's log: without XR_LOADER_DEBUG=warn the loader names no layer.
        Check(VrApiLayers.Failures(string.Join("\r\n", log.Split("\r\n").Where(line => !line.StartsWith("Warning") && line != "\""))).Count == 0);

        var candidates = VrApiLayers.Resolve(failures, catalog);
        Check(candidates.Count == 2 && candidates[1].DisableVariable == "DISABLE_XR_APILAYER_NOVENDOR_OBSMirror");
        Check(VrApiLayers.Resolve([failures[0], failures[1] with { LibraryPath = @"C:\Other\obs-mirror.dll" }], catalog).Count == 0);
        Check(VrApiLayers.Resolve([new("XR_APILAYER_unknown", @"C:\x.dll", 126)], catalog).Count == 0);
        var shared = VrApiLayers.ReadManifest(Write("shared.json", Manifest("XR_APILAYER_other", "other.dll", "DISABLE_XR_APILAYER_reshade")))!;
        Check(VrApiLayers.Resolve(failures, [.. catalog, shared]).Count == 0);
        var twin = VrApiLayers.ReadManifest(Write("twin.json", Manifest("XR_APILAYER_reshade", "twin.dll", "DISABLE_XR_APILAYER_reshade_twin")))!;
        Check(VrApiLayers.Resolve(failures, [.. catalog, twin]).Count == 0);
        Check(VrApiLayers.Resolve(Enumerable.Repeat(failures[0], 17).ToList(), catalog).Count == 0);

        // Approvals apply only to an unchanged registration; duplicates collapse.
        Check(VrApiLayers.Select([.. candidates, candidates[0]], catalog).Count == 2);
        File.AppendAllText(obs, " ");
        List<VrLayerBypass> changed = [VrApiLayers.ReadManifest(reshade)!, VrApiLayers.ReadManifest(obs)!];
        Check(VrApiLayers.Select(candidates, changed).Single().LayerName == "XR_APILAYER_reshade");
        Check(VrApiLayers.Select([], catalog).Count == 0);

        // What the supervisor offers: named, registered layers, never one that still fails while disabled.
        Check(VrApiLayers.Recovery(log, catalog, []) is { Layers.Count: 2 } recovery && recovery.Message == VrApiLayers.Describe(failures) && recovery.Report == log);
        Check(VrApiLayers.Recovery(log, catalog, [candidates[1]]) is null);
        Check(VrApiLayers.Recovery(log.Replace("XrResult=-32", "XrResult=-2"), catalog, []) is null);
        Check(VrApiLayers.Recovery(log, [], []) is null);
    }

    private static void Launch(string fixture)
    {
        // The headset check: loader warnings on (a more verbose choice kept) and approved switches set.
        var probe = Path.Combine(fixture, "probe.exe");
        File.WriteAllBytes(probe, []);
        var start = Installation.ProbeStartInfo(probe, false, "x.log", OpenXrRuntimes.Default, [Reshade]);
        Check(start.Environment["XR_LOADER_DEBUG"] == "warn" && start.Environment["DISABLE_XR_APILAYER_reshade"] == "1");
        Check(!Installation.ProbeStartInfo(probe, false, "x.log").Environment.ContainsKey("DISABLE_XR_APILAYER_reshade"));
        var verbose = new ProcessStartInfo(probe);
        verbose.Environment["XR_LOADER_DEBUG"] = "all";
        VrApiLayers.ApplyToProbe(verbose, []);
        Check(verbose.Environment["XR_LOADER_DEBUG"] == "all");
        Check(Rejects(() => VrApiLayers.ApplyToProbe(new ProcessStartInfo(probe), [Reshade with { DisableVariable = "PATH" }])));

        // Launch VR's supervisor gets the prompt's approvals, and which of them to remember.
        var args = Installation.VrStartInfo(@"C:\Game", [Reshade, Obs], [Obs]).ArgumentList;
        Check(args[0] == "--play-vr" && args[2] == @"C:\Game" && VrApiLayers.Decode(args[args.IndexOf(Installation.XrLayerBypassArgument) + 1]).Count == 2 &&
            VrApiLayers.Decode(args[args.IndexOf(Installation.RememberXrLayersArgument) + 1]).Single() == Obs);
        Check(Installation.VrStartInfo(@"C:\Game").ArgumentList.Count == 3 && Installation.VrStartInfo(@"C:\Game", [], []).ArgumentList.Count == 3);

        // The session file for the VR renderer: one switch per line, none when nothing is disabled.
        var session = Path.Combine(fixture, "session");
        Directory.CreateDirectory(session);
        VrApiLayers.WriteSessionFile(session, [Reshade, Obs, Reshade]);
        Check(File.ReadAllText(Path.Combine(session, VrApiLayers.SessionFile)) == "DISABLE_XR_APILAYER_reshade\nDISABLE_XR_APILAYER_NOVENDOR_OBSMirror\n");
        VrApiLayers.WriteSessionFile(session, []);
        Check(!File.Exists(Path.Combine(session, VrApiLayers.SessionFile)));
        Check(Rejects(() => VrApiLayers.WriteSessionFile(session, [Reshade with { DisableVariable = "PATH" }])));

        // The supervisor's request for the launcher window.
        VrApiLayers.ClearRecovery();
        Check(VrApiLayers.ReadRecovery() is null);
        VrApiLayers.SaveRecovery(new("message", [Obs], "line 1\nline 2"));
        Check(VrApiLayers.ReadRecovery() is { Message: "message", Report: "line 1\r\nline 2" } read && read.Layers.Single() == Obs);
        foreach (var text in new[] { "{", "{\"version\":2}", "{\"version\":1,\"message\":\"m\",\"layers\":[],\"report\":\"\"}",
            "{\"version\":1,\"message\":\"m\",\"layers\":[{\"layerName\":\"XR_APILAYER_x\",\"disableVariable\":\"PATH\"}],\"report\":\"\"}" })
        { File.WriteAllText(VrApiLayers.RecoveryPath, text); Check(VrApiLayers.ReadRecovery() is null); }
        VrApiLayers.ClearRecovery();
        Check(!File.Exists(VrApiLayers.RecoveryPath));
    }

    private static void Preferences()
    {
        // Remembered choices: replaced by layer name, and never trusted when invalid.
        var path = VrApiLayers.PreferencesPath;
        if (File.Exists(path)) File.Delete(path);
        Check(VrApiLayers.Remembered().Count == 0);
        VrApiLayers.Remember([Reshade]); VrApiLayers.Remember([Obs, Reshade with { ManifestHash = new string('D', 64) }]);
        var remembered = VrApiLayers.Remembered();
        Check(remembered.Count == 2 && remembered.Any(Obs.SameIdentity) && remembered.Single(layer => layer.LayerName == Reshade.LayerName).ManifestHash == new string('D', 64));
        VrApiLayers.Reenable(Obs.LayerName);
        Check(VrApiLayers.Remembered().Single().LayerName == Reshade.LayerName);
        foreach (var text in new[] { "{", "[]", "{\"version\":2,\"bypasses\":[]}", "{\"version\":1,\"bypasses\":[{\"layerName\":\"XR_APILAYER_x\",\"disableVariable\":\"PATH\"}]}",
            "{\"version\":1,\"bypasses\":[]}" + new string(' ', 70000) })
        { File.WriteAllText(path, text); Check(VrApiLayers.Remembered().Count == 0); }
        File.Delete(path);
    }

    private static void Dialogs(string output)
    {
        // Cancel is the default, nothing is remembered unless chosen.
        using (var dialog = new VrLayerRecoveryForm(new("message", [Reshade, Obs], "report")))
        {
            var consequences = dialog.Controls.Find("LayerConsequences", true).Single().Text;
            Check(consequences.Contains("ReShade: ReShade OpenXR effects will be unavailable.") && consequences.Contains("OpenXR OBS Mirror: OBS Mirror's OpenXR capture will be unavailable."));
            Check(dialog.AcceptButton == dialog.CancelButton && ((Button)dialog.CancelButton!).Name == "CancelLayerRecovery" && !dialog.RememberChoice);
            Check(((Button)dialog.Controls.Find("RetryWithoutLayers", true).Single()).DialogResult == DialogResult.OK);
            Check(dialog.Controls.Find("LayerFailureDetails", true).Single().Text == "message\r\n\r\nreport");
            var show = dialog.Controls.Find("ShowLayerDetails", true).Single(); Click(show);
            Check(show.Text == "Hide details");
            Click(show);
            // Offscreen review image, rendered unattached; never shown.
            LauncherAppearance.Apply(dialog);
            var content = dialog.Content; dialog.Controls.Remove(content);
            try
            {
                content.Font = dialog.Font; content.Visible = true; content.Size = content.GetPreferredSize(Size.Empty); content.PerformLayout();
                Check(!dialog.Visible && content.Width > 300 && content.Height > 150);
                using var preview = new Bitmap(content.Width, content.Height);
                content.DrawToBitmap(preview, content.ClientRectangle);
                preview.Save(Path.Combine(output, "launcher-vr-layer-recovery.png"));
            }
            finally { dialog.Controls.Add(content); }
        }
        VrApiLayers.Remember([Obs]);
        using (var manage = new VrCompatibilityForm())
        {
            var list = (ListBox)manage.Controls.Find("RememberedLayerBypasses", true).Single();
            Check(list.Items.Count == 1 && list.Items[0] is VrLayerBypass item && item.SameIdentity(Obs) && list.Items[0].ToString() == "OpenXR OBS Mirror");
            Click(manage.Controls.Find("ReenableLayer", true).Single());
            Check(list.Items.Count == 0 && VrApiLayers.Remembered().Count == 0 && manage.Controls.Find("LayerBypassSummary", true).Single().Text.StartsWith("No OpenXR layers"));
        }
        VrApiLayers.Remember([Obs]);
        using (var form = new MainForm(null, null, null))
            Check(form.Controls.Find("VrCompatibilitySummary", true).Single().Text == "1 remembered OpenXR layer bypass." && form.Controls.Find("ManageVrCompatibility", true).Length == 1);
        File.Delete(VrApiLayers.PreferencesPath);
    }
}
