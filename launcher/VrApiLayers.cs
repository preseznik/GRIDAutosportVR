// OpenXR add-on layers that stop VR from starting (GitHub issue #1), ported from GRID2VR
// (launcher/VrApiLayers.cs and tools/OpenXR-Layers.ps1 there, 2026-11-15), itself DiRT2VR's recovery.
//
// GRID Autosport is a 32-bit game. An implicit OpenXR API layer registered for every OpenXR app
// (ReShade, OpenXR OBS Mirror, ...) that has no working 32-bit library makes the 32-bit loader
// fail xrCreateInstance with -32 (XR_ERROR_FILE_ACCESS_ERROR, "Failed loading layer information").
// When the loader names such a layer and the layer's own installed registration declares a disable
// switch, Launch VR names it and offers to disable it for GRID Autosport VR only, then retries.
// Cancel is the default. A choice is remembered only on request and after the retry's headset
// check works, and can be undone under Settings → VR compatibility. Approvals are revalidated
// against the installed registration before every use. Nothing outside our own processes changes:
// the probe gets the switch in its environment, and Steam starts the game without the launcher's
// environment, so the session folder's openxr-layer-bypass.txt lists the switches and the VR
// renderer sets them only around xrCreateInstance (bridge/xr_bridge.cpp).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GRIDAutosportVR;

internal sealed record VrLayerBypass(string LayerName, string ManifestPath, string ManifestHash, string LibraryPath, string DisableVariable)
{
    [JsonIgnore] public string DisplayName => LayerName switch
    {
        "XR_APILAYER_reshade" => "ReShade",
        "XR_APILAYER_NOVENDOR_OBSMirror" => "OpenXR OBS Mirror",
        _ => LayerName
    };
    [JsonIgnore] public string Consequence => LayerName switch
    {
        "XR_APILAYER_reshade" => "ReShade OpenXR effects will be unavailable.",
        "XR_APILAYER_NOVENDOR_OBSMirror" => "OBS Mirror's OpenXR capture will be unavailable.",
        _ => "Features provided by this OpenXR layer will be unavailable."
    };
    public override string ToString() => DisplayName;
    internal bool SameIdentity(VrLayerBypass? other) => other is not null && LayerName == other.LayerName && ManifestHash == other.ManifestHash &&
        DisableVariable == other.DisableVariable && string.Equals(ManifestPath, other.ManifestPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(LibraryPath, other.LibraryPath, StringComparison.OrdinalIgnoreCase);
    // Never let a manifest turn a bypass into PATH, runtime, or other process configuration.
    internal static bool SafeDisableVariable(string? value) => value is not null && Regex.IsMatch(value, @"\ADISABLE_(?:XR_|OPENXR_)[A-Za-z0-9_]{1,180}\z");
    [JsonIgnore] internal bool Valid => LayerName is not null && Regex.IsMatch(LayerName, @"\AXR_APILAYER_[A-Za-z0-9_]{1,200}\z") && SafeDisableVariable(DisableVariable) &&
        ManifestHash is not null && Regex.IsMatch(ManifestHash, @"\A[0-9A-F]{64}\z") &&
        ManifestPath is not null && Path.IsPathFullyQualified(ManifestPath) && LibraryPath is not null && Path.IsPathFullyQualified(LibraryPath);
}

// A layer the loader could not load, from its XR_LOADER_DEBUG=warn output.
internal sealed record VrLayerFailure(string LayerName, string LibraryPath, int WindowsError);
internal sealed record VrLayerRecovery(string Message, IReadOnlyList<VrLayerBypass> Layers, string Report);
internal sealed record VrLayerRecoveryChoice(bool Retry, bool Remember);

// Launch VR's supervisor exits with this when a layer it may offer to disable blocked the headset check.
internal sealed class VrLayerBlockedException(string message) : IOException(message)
{
    public const int ExitCode = 3;
}

internal static class VrApiLayers
{
    internal const int MaxLayers = 16;
    internal const string ImplicitLayersKey = @"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit";
    // The session folder's list of switches for the VR renderer, one per line.
    internal const string SessionFile = "openxr-layer-bypass.txt";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    internal static string PreferencesPath => Path.Combine(LauncherSettings.DataDirectory, "vr-layer-bypasses.json");
    internal static string RecoveryPath => Path.Combine(LauncherSettings.DataDirectory, "openxr-layer-recovery.json");

    // One installed manifest, or null when it is unreadable or offers no safe disable switch.
    internal static VrLayerBypass? ReadManifest(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 64 * 1024) return null;
            var bytes = File.ReadAllBytes(file.FullName);
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
            if (!document.RootElement.TryGetProperty("api_layer", out var layer) || layer.ValueKind != JsonValueKind.Object) return null;
            string? Text(string name) => layer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var (name, library, variable) = (Text("name"), Text("library_path"), Text("disable_environment"));
            if (name is null || !Regex.IsMatch(name, @"\AXR_APILAYER_[A-Za-z0-9_]{1,200}\z") || string.IsNullOrWhiteSpace(library) || !VrLayerBypass.SafeDisableVariable(variable)) return null;
            var full = Path.GetFullPath(path);
            return new(name, full, Convert.ToHexString(SHA256.HashData(bytes)), Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, library)), variable!);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    // The enabled implicit layers a 32-bit process sees: the 32-bit view of both hives, as the
    // probe's and the game's loader read them. An incomplete inventory authorizes no bypass.
    internal static List<VrLayerBypass> Catalog()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
                using var key = root.OpenSubKey(ImplicitLayersKey);
                if (key is null) continue;
                foreach (var path in key.GetValueNames())
                    if (key.GetValueKind(path) == RegistryValueKind.DWord && key.GetValue(path) is int enabled && enabled == 0) paths.Add(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
        }
        return [.. paths.Select(ReadManifest).OfType<VrLayerBypass>()];
    }

    // The layers the loader names as unloadable when xrCreateInstance failed with -32 for its layers.
    // Loader text is from the pinned SDK; numeric Windows errors are matched, not localized descriptions.
    internal static List<VrLayerFailure> Failures(string report)
    {
        var result = new List<VrLayerFailure>();
        if (!Regex.IsMatch(report, @"(?m)^xrCreateInstance: XrResult=-32\r?$") || !report.Contains("Failed loading layer information", StringComparison.Ordinal)) return result;
        foreach (Match match in Regex.Matches(report, "ApiLayerInterface::LoadApiLayers skipping layer (?<name>XR_APILAYER_[A-Za-z0-9_]+), failed to load with message \"Failed to open dynamic library (?<path>[^\r\n]+?) with error (?<error>[0-9]+):"))
        {
            if (!int.TryParse(match.Groups["error"].Value, out var code) || !Path.IsPathFullyQualified(match.Groups["path"].Value)) continue;
            string path;
            try { path = Path.GetFullPath(match.Groups["path"].Value); } catch (ArgumentException) { continue; }
            var name = match.Groups["name"].Value;
            if (result.Any(failure => failure.LayerName == name && string.Equals(failure.LibraryPath, path, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new(name, path, code));
        }
        return result;
    }

    internal static string Describe(IEnumerable<VrLayerFailure> failures) =>
        "An OpenXR layer could not load: " + string.Join("; ", failures.Select(failure => $"{failure.LayerName} (Windows error {failure.WindowsError})")) + ".";

    // Every named layer must be registered once, with the same library and its own switch; otherwise none.
    internal static List<VrLayerBypass> Resolve(IReadOnlyList<VrLayerFailure> failures, IReadOnlyList<VrLayerBypass> catalog)
    {
        if (failures.Count == 0 || failures.Count > MaxLayers) return [];
        var result = new List<VrLayerBypass>();
        foreach (var failure in failures)
        {
            var found = catalog.Where(layer => layer.LayerName == failure.LayerName).ToList();
            if (found.Count != 1 || !string.Equals(found[0].LibraryPath, failure.LibraryPath, StringComparison.OrdinalIgnoreCase) || !VrLayerBypass.SafeDisableVariable(found[0].DisableVariable)) return [];
            // A shared switch would also disable a layer the failure did not name.
            if (catalog.Any(layer => string.Equals(layer.DisableVariable, found[0].DisableVariable, StringComparison.OrdinalIgnoreCase) && layer.LayerName != found[0].LayerName)) return [];
            if (!result.Any(found[0].SameIdentity)) result.Add(found[0]);
        }
        return result;
    }

    // Approved bypasses whose registration is unchanged since approval; duplicates collapse.
    internal static List<VrLayerBypass> Select(IEnumerable<VrLayerBypass> approved, IReadOnlyList<VrLayerBypass> catalog)
    {
        var result = new List<VrLayerBypass>();
        foreach (var entry in approved)
        {
            var resolved = Resolve([new(entry.LayerName, entry.LibraryPath, 0)], catalog);
            if (resolved.Count == 1 && resolved[0].SameIdentity(entry) && !result.Any(entry.SameIdentity)) result.Add(resolved[0]);
        }
        return result;
    }

    // What Launch VR may offer after a failed headset check, or null. A layer that still fails
    // while disabled has no further automatic recovery.
    internal static VrLayerRecovery? Recovery(string report, IReadOnlyList<VrLayerBypass> catalog, IReadOnlyList<VrLayerBypass> applied)
    {
        var failures = Failures(report);
        var candidates = Resolve(failures, catalog);
        if (candidates.Count == 0 || candidates.Any(candidate => applied.Any(candidate.SameIdentity))) return null;
        return new(Describe(failures), candidates, report);
    }

    // The headset check: loader warnings name a layer that failed to load (a player's more verbose
    // choice is kept), and approved switches disable their layers for the probe only.
    internal static void ApplyToProbe(ProcessStartInfo start, IReadOnlyList<VrLayerBypass> layers)
    {
        if (!start.Environment.TryGetValue("XR_LOADER_DEBUG", out var debug) || debug is not ("warn" or "info" or "verbose" or "all")) start.Environment["XR_LOADER_DEBUG"] = "warn";
        foreach (var layer in layers)
        {
            if (!VrLayerBypass.SafeDisableVariable(layer.DisableVariable)) throw new IOException("Invalid OpenXR layer disable switch.");
            start.Environment[layer.DisableVariable] = "1";
        }
    }

    // The game session's switches for the VR renderer; no file when none apply.
    internal static void WriteSessionFile(string sessionFolder, IReadOnlyList<VrLayerBypass> layers)
    {
        var path = Path.Combine(sessionFolder, SessionFile);
        if (layers.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        if (layers.Count > MaxLayers || layers.Any(layer => !layer.Valid)) throw new IOException("Invalid OpenXR layer bypass.");
        File.WriteAllText(path, string.Concat(layers.Select(layer => layer.DisableVariable + "\n").Distinct(StringComparer.OrdinalIgnoreCase)), new UTF8Encoding(false));
    }

    internal static string Encode(IReadOnlyList<VrLayerBypass> layers)
    {
        if (layers.Count > MaxLayers || layers.Any(layer => !layer.Valid)) throw new IOException("Invalid OpenXR layer bypass.");
        var text = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(layers, Json));
        return text.Length <= 16384 ? text : throw new IOException("Too many OpenXR layer bypasses. Re-enable some under Settings → VR compatibility.");
    }

    internal static List<VrLayerBypass> Decode(string text)
    {
        if (text.Length > 16384) throw new IOException("OpenXR layer approvals exceed their size limit.");
        try
        {
            var layers = JsonSerializer.Deserialize<List<VrLayerBypass>>(Convert.FromBase64String(text), Json);
            if (layers is null || layers.Count > MaxLayers || layers.Any(layer => layer is null || !layer.Valid)) throw new IOException("Invalid OpenXR layer approval.");
            return layers;
        }
        catch (Exception error) when (error is FormatException or JsonException or NotSupportedException) { throw new IOException("Invalid OpenXR layer approval.", error); }
    }

    // The supervisor's recovery request for the launcher window (exit code 3).
    internal static void SaveRecovery(VrLayerRecovery recovery)
    {
        Directory.CreateDirectory(LauncherSettings.DataDirectory);
        WriteAtomically(RecoveryPath, JsonSerializer.Serialize(new { version = 1, message = recovery.Message, layers = recovery.Layers, report = recovery.Report }, Json));
    }

    internal static void ClearRecovery() { if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath); }

    internal static VrLayerRecovery? ReadRecovery()
    {
        try
        {
            if (!File.Exists(RecoveryPath) || new FileInfo(RecoveryPath).Length > 1024 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(RecoveryPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1) return null;
            var layers = root.GetProperty("layers").Deserialize<List<VrLayerBypass>>(Json);
            if (layers is null || layers.Count == 0 || layers.Count > MaxLayers || layers.Any(layer => layer is null || !layer.Valid)) return null;
            return new(root.GetProperty("message").GetString() ?? "", layers, (root.GetProperty("report").GetString() ?? "").ReplaceLineEndings("\r\n"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }

    // Invalid or unreadable preferences grant no permission to bypass a layer.
    internal static List<VrLayerBypass> Remembered()
    {
        try
        {
            var path = PreferencesPath;
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1) return [];
            var layers = root.GetProperty("bypasses").Deserialize<List<VrLayerBypass>>(Json);
            return layers is null || layers.Count > MaxLayers || layers.Any(layer => layer is null || !layer.Valid) ? [] : layers;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return []; }
    }

    internal static void Remember(IEnumerable<VrLayerBypass> layers)
    {
        var saved = Remembered();
        foreach (var layer in layers) { saved.RemoveAll(item => item.LayerName == layer.LayerName); saved.Add(layer); }
        Save(saved);
    }

    internal static void Reenable(string layerName)
    {
        var saved = Remembered();
        saved.RemoveAll(item => item.LayerName == layerName);
        Save(saved);
    }

    private static void Save(List<VrLayerBypass> layers)
    {
        if (layers.Count > MaxLayers || layers.Any(layer => !layer.Valid)) throw new IOException("Invalid OpenXR layer bypass.");
        Directory.CreateDirectory(LauncherSettings.DataDirectory);
        WriteAtomically(PreferencesPath, JsonSerializer.Serialize(new { version = 1, bypasses = layers }, Json));
    }

    private static void WriteAtomically(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
