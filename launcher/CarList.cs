using System.Text;

namespace GRIDAutosportVR;

// CARS.txt in the release package: every car's name, the code the game files use for it, its
// discipline and its texture in the optional cockpit texture pack, so a car can be found for
// editing (GRID2VR's pack has the same list). Written by tools/Package.ps1 through --write-car-list.
internal static class CarList
{
    internal const string FileName = "CARS.txt";

    internal static string Text()
    {
        var pack = CockpitTexturePack.Current;
        var cars = RaceSession.Cars.OrderBy(c => c.Value.Label, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        string Texture(string code) => pack is null ? "" : pack.Files.ContainsKey(code + ".dds") ? code + ".dds" : "(original kept)";
        var name = Math.Max("Car".Length, cars.Max(c => c.Value.Label.Length));
        var group = Math.Max("Discipline".Length, cars.Max(c => c.Value.Class.Length));
        var text = new StringBuilder();
        text.Append("GRID Autosport VR car list: ").Append(cars.Count).Append(" cars\r\n\r\n");
        text.Append("Code is the car's folder in the game, cars\\models\\<code>. Its cockpit texture is <code>_cabin.tga\r\n");
        text.Append("inside cars\\models\\<code>\\livery_user\\textures_low\\<code>_tex_low_user.pssg.\r\n");
        if (pack is not null)
            text.Append("Cockpit texture is the car's file in the optional cockpit texture pack ").Append(pack.Version)
                .Append(" (Graphics -> Cockpit textures),\r\nin %LOCALAPPDATA%\\GRIDAutosportVR\\addons\\cockpit-textures\\").Append(pack.Version).Append(".\r\n");
        text.Append("\r\n").Append("Car".PadRight(name)).Append("  Code  ").Append("Discipline".PadRight(group)).Append(pack is null ? "" : "  Cockpit texture").Append("\r\n");
        foreach (var (code, (label, discipline)) in cars)
            text.Append((label.PadRight(name) + "  " + code + "   " + discipline.PadRight(group) + (pack is null ? "" : "  " + Texture(code))).TrimEnd()).Append("\r\n");
        return text.ToString();
    }

    internal static void Write(string path) => File.WriteAllText(path, Text(), new UTF8Encoding(false));
}
