using System.Reflection;

namespace GRIDAutosportVR;

internal static class Branding
{
    public const string Title = "GRID Autosport VR";
    public const string Repository = "https://github.com/preseznik/GRIDAutosportVR";

    public static Icon LoadIcon()
    {
        using var stream = typeof(Branding).Assembly.GetManifestResourceStream("GRIDAutosportVR.logo.ico") ?? throw new IOException("Launcher icon is missing.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    public static Bitmap LoadLogo()
    {
        using var stream = typeof(Branding).Assembly.GetManifestResourceStream("GRIDAutosportVR.logo-display.png") ?? throw new IOException("Launcher logo is missing.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    public static PictureBox Logo(int size)
    {
        var box = new PictureBox { Name = "LauncherLogo", AccessibleName = Title + " logo", Image = LoadLogo(), Size = new Size(size, size), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 12, 4), TabStop = false };
        box.Disposed += (_, _) => box.Image?.Dispose();
        return box;
    }
}

// Version, channel and build stamp, set by packaging (Version, ReleaseChannel, BuildUtc, BuildRevision).
internal static class BuildInfo
{
    private static string Metadata(string key, string fallback) =>
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? fallback;
    public static string Version =>
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    public static string Channel => Metadata("ReleaseChannel", "Experimental");
    public static string Built => Metadata("BuildUtc", "Development build");
    public static string Revision => Metadata("BuildRevision", "local");
}
