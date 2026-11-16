using System.Text.Json;

namespace GRIDAutosportVR;

// Graphics → Last VR session: the VR renderer's performance.json from the last session (one-second
// frame-rate windows while the race was in stereo, and the headset's display rate), with the
// launcher settings that session used. Recommend for this PC tunes from it only while the quality
// and VR anti-aliasing are still the same.
internal sealed record VrPerformance(DateTime At, double DisplayHz, int Windows, double FpsMedian, double FpsLow, double DipShare,
    string Quality, string Msaa, int Scale, int EyeWidth, int EyeHeight)
{
    internal const int MinimumWindows = 10;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string StorePath => Path.Combine(LauncherSettings.DataDirectory, "last-vr-performance.json");
    internal bool Usable => Windows >= MinimumWindows && DisplayHz is > 20 and < 1000 && FpsMedian > 0;
    // At least a quarter of the seconds below 95% of the headset rate. A race on an RTX 4080 SUPER at
    // 90 Hz with 11 opponents dropped in about 11% of seconds at Potato (100%) and at VR Balanced
    // (150%) alike, so drops below that share do not come from the GPU settings.
    internal bool Dips => DipShare >= .25;
    internal bool SomeDips => DipShare >= .05;
    internal bool HeldRate => FpsMedian >= DisplayHz * .97;
    internal bool SameSettings(Choices current) => Quality == current.GraphicsQuality && Msaa == current.VrMsaa;

    internal string Describe()
    {
        if (!Usable) return "Too short to measure. Drive for at least 10 seconds in VR.";
        var state = !HeldRate ? "below the headset rate" : SomeDips ? $"mostly holding the headset rate, {DipShare * 100:0}% of seconds dropped" : "holding the headset rate";
        return $"{FpsMedian:0} FPS median, {FpsLow:0} low (headset {DisplayHz:0} Hz) · {state} · {Scale}% render scale · {GraphicsCatalog.Describe(Quality, Msaa)}.";
    }

    // Reads the renderer's summary in a session folder and keeps it as the last session's.
    internal static VrPerformance? Capture(string sessionFolder, Choices choices, (int Width, int Height)? eye)
    {
        var path = Path.Combine(sessionFolder, "performance.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (Number(root, "schema") != 1) return null;
        var fps = root.TryGetProperty("fps", out var f) ? f : default;
        var dips = root.TryGetProperty("dips", out var d) ? d : default;
        var value = new VrPerformance(DateTime.UtcNow, Number(root, "displayHz") ?? 0, (int)(Number(root, "windows") ?? 0),
            Number(fps, "p50") ?? 0, Number(fps, "p5") ?? 0, Number(dips, "share") ?? 0,
            choices.GraphicsQuality, choices.VrMsaa, choices.VrRenderScale, eye?.Width ?? 0, eye?.Height ?? 0);
        if (value.Windows == 0) return null;
        Directory.CreateDirectory(LauncherSettings.DataDirectory);
        var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, StorePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return value;
    }

    internal static VrPerformance? Load()
    {
        try
        {
            if (!File.Exists(StorePath) || new FileInfo(StorePath).Length > 4096) return null;
            var value = JsonSerializer.Deserialize<VrPerformance>(File.ReadAllText(StorePath));
            return value is not null && GraphicsCatalog.ValidQuality(value.Quality) && GraphicsCatalog.ValidMsaa(value.Msaa) &&
                value.Scale is >= RenderScaleSlider.MinimumScale and <= RenderScaleSlider.MaximumScale ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static double? Number(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
