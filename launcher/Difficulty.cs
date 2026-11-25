namespace GRIDAutosportVR;

// Launcher -> Difficulty: where Practice and Race start (bridge/difficulty.cpp, -gasvrdifficulty).
// Those sessions load no save profile, so the game's own defaults applied (Medium, automatic). The
// values are the game's: transmission_type 0-4, ai_difficulty 0-4, flashbacks 0-5 and 6 for unlimited.
internal static class Difficulty
{
    public static readonly (string Id, string Label)[] Transmissions =
    [
        ("automatic", "Automatic"), ("manual", "Manual"), ("manual-h", "Manual, H-pattern shifter"),
        ("clutch-h", "Manual with clutch, H-pattern shifter"), ("clutch", "Manual with clutch"),
    ];
    public static readonly string[] AiLevels = ["Very easy", "Easy", "Medium", "Hard", "Very hard"];
    public const int MaxAi = 4, MaxFlashbacks = 6; // 6 flashbacks is unlimited.

    public static bool ValidTransmission(string id) => Transmissions.Any(t => t.Id == id);
    public static bool Valid(Choices c) => ValidTransmission(c.Transmission) && c.AiDifficulty is >= 0 and <= MaxAi &&
                                           c.Flashbacks is >= 0 and <= MaxFlashbacks;
    public static string FlashbackLabel(int count) => count == MaxFlashbacks ? "Unlimited" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ABS, traction control, stability control, cornering assist, steering assist, damage (1 full),
    // AI level, transmission and flashbacks; null when the game's own settings apply.
    public static string? Argument(Choices c) =>
        !c.LauncherDifficulty || !Valid(c) ? null
            : $"-gasvrdifficulty={Bit(c.Abs)},{Bit(c.TractionControl)},{Bit(c.StabilityControl)},{Bit(c.CorneringAssist)},{Bit(c.SteeringAssist)}," +
              $"{Bit(c.FullDamage)},{c.AiDifficulty},{Array.FindIndex(Transmissions, t => t.Id == c.Transmission)},{c.Flashbacks}";

    public static string Summary(Choices c) =>
        !c.LauncherDifficulty || !Valid(c) ? "the game's difficulty"
            : $"{Transmissions.First(t => t.Id == c.Transmission).Label.ToLowerInvariant()}, AI {AiLevels[c.AiDifficulty].ToLowerInvariant()}";

    private static char Bit(bool value) => value ? '1' : '0';
}
