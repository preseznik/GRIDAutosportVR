using System.Text.Json;
using System.Text.Json.Nodes;

namespace GRIDAutosportVR;

// Launcher choices saved by Save settings or a launch. Unknown keys are kept, so tools such as
// bridge/run.py can read hudFollowView from the same settings.json.
// RecenterKey is a Windows virtual-key code (121 = F10, as GRID2VR); RecenterModifiers: 1 Ctrl, 2 Alt, 4 Shift.
internal sealed record Choices(bool HudFollowView = false, bool ModernInterface = true, int VrRenderScale = 100,
    int RecenterKey = 121, int RecenterModifiers = 0, bool KeepHorizonLevel = true);

internal static class LauncherSettings
{
    private static string? dataOverride;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // %LOCALAPPDATA%\GRIDAutosportVR, shared with the bridge's session folders.
    public static string DataDirectory
    {
        get
        {
            if (dataOverride is not null) return dataOverride;
            var userData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(userData)) userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(userData, "GRIDAutosportVR");
        }
    }
    // Self-tests point every file at a temporary folder.
    internal static void UseDataDirectory(string? folder) => dataOverride = folder;

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string WindowPath => Path.Combine(DataDirectory, "launcher-window.json");
    public static bool HasSavedSettings => File.Exists(SettingsPath);

    private static JsonObject ReadDocument(string fileName)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(fileName)) as JsonObject ?? throw new JsonException("Expected a JSON object in " + fileName);
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static bool ReadFlag(JsonObject document, string key, bool fallback, string fileName)
    {
        if (!document.TryGetPropertyValue(key, out var node)) return fallback;
        if (node is JsonValue value && value.TryGetValue(out bool flag)) return flag;
        throw new JsonException(key + " must be true or false in " + fileName);
    }

    private static int ReadScale(JsonObject document, string key, int fallback, string fileName)
    {
        if (!document.TryGetPropertyValue(key, out var node)) return fallback;
        if (node is JsonValue value && value.TryGetValue(out int scale) && scale is >= RenderScaleSlider.MinimumScale and <= RenderScaleSlider.MaximumScale) return scale;
        throw new JsonException($"{key} must be a whole number from {RenderScaleSlider.MinimumScale} to {RenderScaleSlider.MaximumScale} in {fileName}");
    }

    private static (int Key, int Modifiers) ReadShortcut(JsonObject document, string fileName)
    {
        int Read(string key, int fallback)
        {
            if (!document.TryGetPropertyValue(key, out var node)) return fallback;
            if (node is JsonValue value && value.TryGetValue(out int number)) return number;
            throw new JsonException(key + " must be a whole number in " + fileName);
        }
        var shortcut = (Read("recenterKey", 121), Read("recenterModifiers", 0));
        if (!RecenterShortcut.Valid(shortcut.Item1, shortcut.Item2)) throw new JsonException("recenterKey/recenterModifiers is not an allowed recenter shortcut in " + fileName);
        return shortcut;
    }

    public static Choices Load(string? fileName = null)
    {
        fileName ??= SettingsPath;
        var document = ReadDocument(fileName);
        // Settings saved before the Modern interface existed keep the look their users chose.
        var modernFallback = !File.Exists(fileName);
        var (key, modifiers) = ReadShortcut(document, fileName);
        return new Choices(ReadFlag(document, "hudFollowView", false, fileName), ReadFlag(document, "modernInterface", modernFallback, fileName),
            ReadScale(document, "vrRenderScale", 100, fileName), key, modifiers, ReadFlag(document, "keepHorizonLevel", true, fileName));
    }

    public static void Save(Choices choices, string? fileName = null)
    {
        fileName ??= SettingsPath;
        var document = ReadDocument(fileName);
        document["hudFollowView"] = choices.HudFollowView;
        document["modernInterface"] = choices.ModernInterface;
        document["vrRenderScale"] = choices.VrRenderScale;
        document["recenterKey"] = choices.RecenterKey;
        document["recenterModifiers"] = choices.RecenterModifiers;
        document["keepHorizonLevel"] = choices.KeepHorizonLevel;
        WriteAtomically(fileName, document.ToJsonString(Indented) + Environment.NewLine);
    }

    public static LauncherWindow LoadWindow()
    {
        try
        {
            if (!File.Exists(WindowPath) || new FileInfo(WindowPath).Length > 4096) return new();
            var value = JsonSerializer.Deserialize<LauncherWindow>(File.ReadAllText(WindowPath));
            return value is { Valid: true } ? value : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public static void SaveWindow(LauncherWindow value)
    {
        if (!value.Valid) return;
        WriteAtomically(WindowPath, JsonSerializer.Serialize(value, Indented));
    }

    private static void WriteAtomically(string fileName, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
        var temporary = fileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, fileName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
