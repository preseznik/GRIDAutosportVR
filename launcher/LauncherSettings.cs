using System.Text.Json;
using System.Text.Json.Nodes;

namespace GRIDAutosportVR;

// Launcher choices saved by Save settings or a launch. Unknown keys are kept, so tools such as
// bridge/run.py can read hudFollowView from the same settings.json.
// RecenterKey is a Windows virtual-key code (121 = F10, as GRID2VR); RecenterModifiers: 1 Ctrl, 2 Alt, 4 Shift.
// LaunchMode Normal opens the game's menus; Practice and Race start the chosen car and track (RaceSession).
internal sealed record Choices(bool HudFollowView = false, bool ModernInterface = true, int VrRenderScale = 100,
    int RecenterKey = 121, int RecenterModifiers = 0, bool KeepHorizonLevel = true,
    string LaunchMode = "Normal", string Car = RaceSession.DefaultCar, string Track = RaceSession.DefaultTrack,
    int Laps = 3, int Opponents = 7, string OpponentCars = "same", bool VrCockpit = true,
    bool HudShow = true, bool HudDial = true, bool HudMinimap = true, bool HudCounters = true, bool HudPositions = true,
    decimal HudDistance = 1m, decimal ScreenDistance = 3m, bool Intro3D = false, DrivingBinding? RecenterDevice = null,
    bool NewCockpitTextures = false)
{
    // The bridge's -gasvrhudparts mask: 1 entire HUD, 2 dial, 4 minimap, 8 counters, 16 position info.
    internal int HudParts => (HudShow ? 1 : 0) | (HudDial ? 2 : 0) | (HudMinimap ? 4 : 0) | (HudCounters ? 8 : 0) | (HudPositions ? 16 : 0);
}

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

    private static string ReadText(JsonObject document, string key, string fallback, Func<string, bool> valid, string fileName)
    {
        if (!document.TryGetPropertyValue(key, out var node)) return fallback;
        if (node is JsonValue value && value.TryGetValue(out string? text) && valid(text)) return text;
        throw new JsonException(key + " has an unsupported value in " + fileName);
    }

    private static int ReadCount(JsonObject document, string key, int fallback, int maximum, string fileName)
    {
        if (!document.TryGetPropertyValue(key, out var node)) return fallback;
        if (node is JsonValue value && value.TryGetValue(out int number) && number >= 1 && number <= maximum) return number;
        throw new JsonException($"{key} must be a whole number from 1 to {maximum} in {fileName}");
    }

    private static decimal ReadDistance(JsonObject document, string key, decimal fallback, decimal minimum, decimal maximum, string fileName)
    {
        if (!document.TryGetPropertyValue(key, out var node)) return fallback;
        if (node is JsonValue value && value.TryGetValue(out decimal metres) && DistanceSlider.Valid(metres, minimum, maximum)) return metres;
        throw new JsonException($"{key} must be {minimum:0.#}–{maximum:0.#} metres in 0.5 m steps in {fileName}");
    }

    // A controller or wheel button that recenters too (DeviceRecenter); absent or null for none.
    private static DrivingBinding? ReadRecenterDevice(JsonObject document, string fileName)
    {
        if (!document.TryGetPropertyValue("recenterDevice", out var node) || node is null) return null;
        if (node is not JsonObject) throw new JsonException("recenterDevice must be an object in " + fileName);
        try { return DeviceRecenter.FromJson(node.ToJsonString()); }
        catch (JsonException error) { throw new JsonException(error.Message + " in " + fileName, error); }
    }

    internal static bool ValidCar(string text) => System.Text.RegularExpressions.Regex.IsMatch(text, @"\A[a-z0-9_]{1,32}\z");
    internal static bool ValidTrack(string text) => System.Text.RegularExpressions.Regex.IsMatch(text, @"\A(circuits|cities)/[a-z0-9_]{1,40}/route_\d{1,2}\z");

    public static Choices Load(string? fileName = null)
    {
        fileName ??= SettingsPath;
        var document = ReadDocument(fileName);
        // Settings saved before the Modern interface existed keep the look their users chose.
        var modernFallback = !File.Exists(fileName);
        var (key, modifiers) = ReadShortcut(document, fileName);
        return new Choices(ReadFlag(document, "hudFollowView", false, fileName), ReadFlag(document, "modernInterface", modernFallback, fileName),
            ReadScale(document, "vrRenderScale", 100, fileName), key, modifiers, ReadFlag(document, "keepHorizonLevel", true, fileName),
            ReadText(document, "launchMode", "Normal", RaceSession.Modes.Contains, fileName),
            ReadText(document, "car", RaceSession.DefaultCar, ValidCar, fileName),
            ReadText(document, "track", RaceSession.DefaultTrack, ValidTrack, fileName),
            ReadCount(document, "laps", 3, RaceSession.MaxLaps, fileName),
            ReadCount(document, "opponents", 7, RaceSession.MaxOpponents, fileName),
            ReadText(document, "opponentCars", "same", RaceSession.OpponentChoices.Contains, fileName),
            ReadFlag(document, "vrCockpit", true, fileName),
            ReadFlag(document, "hudShow", true, fileName), ReadFlag(document, "hudDial", true, fileName),
            ReadFlag(document, "hudMinimap", true, fileName), ReadFlag(document, "hudCounters", true, fileName),
            ReadFlag(document, "hudPositions", true, fileName),
            ReadDistance(document, "hudDistance", 1m, DistanceSlider.HudMinimum, DistanceSlider.HudMaximum, fileName),
            ReadDistance(document, "screenDistance", 3m, DistanceSlider.ScreenMinimum, DistanceSlider.ScreenMaximum, fileName),
            ReadFlag(document, "intro3d", false, fileName), ReadRecenterDevice(document, fileName),
            ReadFlag(document, "newCockpitTextures", false, fileName));
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
        if (!RaceSession.Modes.Contains(choices.LaunchMode) || !ValidCar(choices.Car) || !ValidTrack(choices.Track) ||
            choices.Laps is < 1 or > RaceSession.MaxLaps || choices.Opponents is < 1 or > RaceSession.MaxOpponents ||
            !RaceSession.OpponentChoices.Contains(choices.OpponentCars)) throw new ArgumentException("Unsupported session choice.");
        document["launchMode"] = choices.LaunchMode;
        document["car"] = choices.Car;
        document["track"] = choices.Track;
        document["laps"] = choices.Laps;
        document["opponents"] = choices.Opponents;
        document["opponentCars"] = choices.OpponentCars;
        document["vrCockpit"] = choices.VrCockpit;
        if (!DistanceSlider.ValidHud(choices.HudDistance)) throw new ArgumentException("Unsupported HUD distance.");
        document["hudShow"] = choices.HudShow;
        document["hudDial"] = choices.HudDial;
        document["hudMinimap"] = choices.HudMinimap;
        document["hudCounters"] = choices.HudCounters;
        document["hudPositions"] = choices.HudPositions;
        document["hudDistance"] = choices.HudDistance;
        if (!DistanceSlider.ValidScreen(choices.ScreenDistance)) throw new ArgumentException("Unsupported screen distance.");
        document["screenDistance"] = choices.ScreenDistance;
        document["intro3d"] = choices.Intro3D;
        document["newCockpitTextures"] = choices.NewCockpitTextures;
        if (choices.RecenterDevice is null) document.Remove("recenterDevice");
        else document["recenterDevice"] = JsonNode.Parse(DeviceRecenter.ToJson(choices.RecenterDevice));
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
