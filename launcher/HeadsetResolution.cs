using System.Text.Json;

namespace GRIDAutosportVR;

// The connected headset's OpenXR recommended per-eye size, read from the 32-bit probe's log
// (runtime=, headset=, eye0=WxH). VR render scale is relative to it: 100% renders each eye at
// the recommended size; the bridge reports the scaled size to the game (-gasvreyesize=WxH).
internal sealed record HeadsetResolution(string Runtime, string Headset, int Width, int Height)
{
    // The game packs both eyes side by side in one D3D11 texture (16384 px maximum).
    internal const int MaximumEyeWidth = 8192, MaximumEyeHeight = 8192, MinimumEye = 64;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string StorePath => Path.Combine(LauncherSettings.DataDirectory, "headset.json");

    internal static bool Supported(int width, int height) =>
        width is >= MinimumEye and <= MaximumEyeWidth && height is >= MinimumEye and <= MaximumEyeHeight;

    internal static int Scaled(int size, int scale) => (int)((long)size * scale / 100);

    internal (int Width, int Height) EyeSize(int scale) => (Scaled(Width, scale), Scaled(Height, scale));

    internal int MaximumRenderScale =>
        Enumerable.Range(RenderScaleSlider.MinimumScale, RenderScaleSlider.MaximumScale - RenderScaleSlider.MinimumScale + 1)
            .LastOrDefault(scale => Supported(Scaled(Width, scale), Scaled(Height, scale)), RenderScaleSlider.MinimumScale);

    // The size to request at a launch: the chosen scale, lowered to what one texture can hold.
    internal (int Width, int Height, int Scale) LaunchSize(int scale)
    {
        var used = Math.Clamp(scale, RenderScaleSlider.MinimumScale, MaximumRenderScale);
        var (width, height) = EyeSize(used);
        return (width, height, used);
    }

    internal string Summary(int scale)
    {
        var (width, height, used) = LaunchSize(scale);
        return $"{Headset} · {Runtime}\nOutput: {Width} × {Height} per eye (100% baseline).\nRender: {width} × {height} per eye at {used}%." +
            (used < scale ? $"\n{scale}% exceeds the game's texture limit at this resolution; {used}% will be used." : "");
    }

    internal static HeadsetResolution? FromProbeLog(IEnumerable<string> lines)
    {
        string runtime = "", headset = "";
        int width = 0, height = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("runtime=", StringComparison.Ordinal)) runtime = line[8..].Replace(" version=", " ", StringComparison.Ordinal).Trim();
            else if (line.StartsWith("headset=", StringComparison.Ordinal))
            {
                var value = line[8..];
                var tracking = value.IndexOf(" position_tracking=", StringComparison.Ordinal);
                headset = (tracking >= 0 ? value[..tracking] : value).Trim();
            }
            else if (line.StartsWith("eye0=", StringComparison.Ordinal))
            {
                var size = line[5..].Split(' ', 2)[0].Split('x');
                if (size.Length == 2) { int.TryParse(size[0], out width); int.TryParse(size[1], out height); }
            }
        }
        if (!Supported(width, height) || headset.Length is 0 or > 256 || runtime.Length > 128) return null;
        return new HeadsetResolution(runtime.Length == 0 ? "OpenXR" : runtime, headset, width, height);
    }

    internal static HeadsetResolution? Load()
    {
        try
        {
            if (!File.Exists(StorePath) || new FileInfo(StorePath).Length > 4096) return null;
            var value = JsonSerializer.Deserialize<HeadsetResolution>(File.ReadAllText(StorePath));
            return value is not null && Supported(value.Width, value.Height) && !string.IsNullOrWhiteSpace(value.Headset) ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal void Save()
    {
        Directory.CreateDirectory(LauncherSettings.DataDirectory);
        var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json)); File.Move(temporary, StorePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
