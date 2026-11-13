// Per-car VR seat adjustment (GRID2VR's Controls -> Seat). The offsets move your head from the
// game's own cockpit (head-cam) position: millimetres right / up / forward, at most 50 cm each way.
// One file is shared with the VR renderer, which reads it at the start of a session and writes it
// when you save in the in-game seat panel (Tab in the cockpit; bridge/seat.cpp):
// %LOCALAPPDATA%\GRIDAutosportVR\seat-adjustments.txt, one "car right up forward" line per car.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GRIDAutosportVR;

internal readonly record struct SeatOffset(int Right, int Up, int Forward)
{
    public const int Limit = 500; // millimetres
    public bool IsZero => Right == 0 && Up == 0 && Forward == 0;
    public bool Valid => Math.Abs(Right) <= Limit && Math.Abs(Up) <= Limit && Math.Abs(Forward) <= Limit;
}

internal static class SeatAdjustments
{
    private static readonly Regex CarId = new(@"\A[a-z0-9]{3}\z");
    public static string FilePath => Path.Combine(LauncherSettings.DataDirectory, "seat-adjustments.txt");

    // Unreadable lines are skipped, as the renderer does; values are clamped to the limit.
    public static SortedDictionary<string, SeatOffset> Load(string? file = null)
    {
        var seats = new SortedDictionary<string, SeatOffset>(StringComparer.Ordinal);
        file ??= FilePath;
        if (!File.Exists(file)) return seats;
        foreach (var line in File.ReadLines(file).Take(512))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || line.StartsWith('#') || !CarId.IsMatch(parts[0])) continue;
            if (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var right) ||
                !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var up) ||
                !int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var forward)) continue;
            seats[parts[0]] = new(Clamp(right), Clamp(up), Clamp(forward));
        }
        return seats;
    }

    public static void Save(IReadOnlyDictionary<string, SeatOffset> seats, string? file = null)
    {
        file ??= FilePath;
        var text = new StringBuilder("# GRID Autosport VR seat adjustments: car right up forward (millimetres)\n");
        foreach (var (car, offset) in seats.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (!CarId.IsMatch(car) || !offset.Valid) throw new ArgumentException("Unsupported seat adjustment for " + car + ".");
            if (!offset.IsZero) text.Append(CultureInfo.InvariantCulture, $"{car} {offset.Right} {offset.Up} {offset.Forward}\n");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
        File.Move(temporary, file, true);
    }

    public static void Set(string car, SeatOffset offset, string? file = null)
    {
        var seats = Load(file);
        if (offset.IsZero) seats.Remove(car); else seats[car] = offset;
        Save(seats, file);
    }

    private static int Clamp(int value) => Math.Clamp(value, -SeatOffset.Limit, SeatOffset.Limit);
}
