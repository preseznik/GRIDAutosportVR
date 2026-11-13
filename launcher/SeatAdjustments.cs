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

// The in-game seat panel's five keys (Windows virtual-key codes): open / close, up, down, back,
// forward. Shift turns back / forward into left / right; Enter, Esc and Backspace are the panel's own.
internal static class SeatKeys
{
    public const string Default = "9,38,40,37,39"; // Tab and the arrow keys, as GRID2VR.
    public static readonly string[] Actions = ["Open / close panel", "Seat up", "Seat down", "Seat back", "Seat forward"];
    private static readonly int[] Reserved = [(int)Keys.Return, (int)Keys.Escape, (int)Keys.Back, (int)Keys.ShiftKey,
        (int)Keys.ControlKey, (int)Keys.Menu, (int)Keys.LWin, (int)Keys.RWin, (int)Keys.LShiftKey, (int)Keys.RShiftKey];

    public static int[] Parse(string value)
    {
        var parts = value.Split(',');
        if (parts.Length != Actions.Length) throw new FormatException("Seat keys need five keys.");
        var keys = parts.Select(p => int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
        if (!Valid(keys)) throw new FormatException("Unsupported seat keys.");
        return keys;
    }
    public static bool TryParse(string value, out int[] keys)
    {
        try { keys = Parse(value); return true; }
        catch (FormatException) { keys = []; return false; }
        catch (OverflowException) { keys = []; return false; }
    }
    public static bool Valid(int[] keys) => keys.Length == Actions.Length && keys.All(ValidKey) && keys.Distinct().Count() == keys.Length;
    public static bool ValidKey(int key) => RecenterShortcut.ValidKey(key) && !Reserved.Contains(key);
    public static string Format(int[] keys) => string.Join(',', keys.Select(k => k.ToString(CultureInfo.InvariantCulture)));
    public static string Label(int key) => key switch
    {
        (int)Keys.Prior => "Page Up", (int)Keys.Next => "Page Down", (int)Keys.Up => "Up arrow", (int)Keys.Down => "Down arrow",
        (int)Keys.Left => "Left arrow", (int)Keys.Right => "Right arrow", _ => ((Keys)key).ToString(),
    };
}
