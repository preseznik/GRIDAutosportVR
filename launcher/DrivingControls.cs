// Ported from GRID2VR/launcher/DrivingControls.cs (2026-11-12). GRID Autosport uses GRID 2's
// input schema (input/*.xml: ActionMap / Action id / Axis id type deadzone saturation), so the
// binding model, validation, wizard and profiles carry over. The bindings reach the game as
// session-only edits of its own preset files (SessionInputMaps), so GRID2VR's hook-only options
// (transmission override, steering linearity, clutch threshold, device claim) are not here: the
// game's own Controls / Difficulty settings choose transmission and assists.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GRIDAutosportVR;

internal sealed record DrivingBinding
{
    public string Action { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string Device { get; init; } = "";
    public string DeviceType { get; init; } = "";
    public string Input { get; init; } = "";
    public string Calibration { get; init; } = "uniDirPos";
    public decimal DeadZone { get; init; }
    public decimal Saturation { get; init; } = 1;
    [JsonIgnore] public bool Keyboard => DeviceType == "keyboard";
    // Buttons, keys and hat directions are written without calibration, as the game's presets do.
    [JsonIgnore] public bool Axis => DrivingControls.XboxAxes.Contains(Input) || DrivingControls.DirectAxes.Contains(Input);
}

// GRID Autosport's action names and calibration vocabulary come from its shipped input/*.xml.
internal sealed class DrivingControls
{
    internal const string RuntimeNotice = "Enabled bindings apply on the next VR launch: for each bound device they replace the game's own preset for that action, for that session only. Keyboard, controller, wheel and shifter work together. Unassigned actions keep the game's bindings. Choose the transmission (automatic, manual, manual with clutch) in the game's own settings.";
    internal const int MaxDevices = 16;
    internal const int MaxFileBytes = 524288;
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; }
    public bool ListenAllDevices { get; set; } = true;
    public List<DrivingBinding> Bindings { get; set; } = [];
    public static readonly string[] Actions = ["Steer Left", "Steer Right", "Accelerate", "Brake", "Clutch", "Handbrake",
        "Gear Up", "Gear Down", "Gear 1", "Gear 2", "Gear 3", "Gear 4", "Gear 5", "Gear 6", "Gear 7", "Gear Reverse",
        "Change View", "Look Back", "Look Left", "Look Right", "Look Up", "Look Down", "Boost", "Pause", "Instant Replay",
        "Radio Car Health", "Radio Rival Update", "Radio Team Update", "Radio Split Time", "Radio TeamMate Push", "Radio TeamMate Drop",
        "Menu Start Button", "Menu Select", "Menu Back", "Menu Up", "Menu Down", "Menu Left", "Menu Right",
        "Replay Rewind", "Replay Fast Forward", "Replay Pause", "Replay Next Camera", "Replay Prev Camera", "Replay Jump In", "Replay Exit", "Replay UI On Off"];
    // The wizard binds Essentials, then asks before each later batch. Together the
    // batches cover every action exactly once.
    internal static readonly DrivingWizardBatch[] WizardBatches =
    [
        new("Essentials", "steering, pedals, handbrake, gear up/down, camera and pause",
            ["Steer Left", "Steer Right", "Accelerate", "Brake", "Clutch", "Handbrake", "Gear Up", "Gear Down", "Change View", "Look Back", "Pause"]),
        new("H-pattern shifter", "Gear 1–7 and Reverse", ["Gear 1", "Gear 2", "Gear 3", "Gear 4", "Gear 5", "Gear 6", "Gear 7", "Gear Reverse"]),
        new("Team radio", "car health, rival, team and split-time updates, team-mate push and drop",
            ["Radio Car Health", "Radio Rival Update", "Radio Team Update", "Radio Split Time", "Radio TeamMate Push", "Radio TeamMate Drop"]),
        new("Replay controls", "Instant Replay and the replay viewer",
            ["Instant Replay", "Replay Pause", "Replay Rewind", "Replay Fast Forward", "Replay Next Camera", "Replay Prev Camera", "Replay Jump In", "Replay Exit", "Replay UI On Off"]),
        new("Menu controls", "menu navigation, select, back and start",
            ["Menu Up", "Menu Down", "Menu Left", "Menu Right", "Menu Select", "Menu Back", "Menu Start Button"]),
        new("Look and boost", "look left/right/up/down and boost", ["Look Left", "Look Right", "Look Up", "Look Down", "Boost"])
    ];
    public static readonly string[] Calibrations = ["uniDirPos", "uniDirNeg", "biDirLower", "biDirUpper"];
    internal static readonly string[] XboxAxes = ["xinput_left_stick_x", "xinput_left_stick_y", "xinput_right_stick_x", "xinput_right_stick_y", "xinput_left_trigger", "xinput_right_trigger"];
    internal static readonly string[] DirectAxes = ["di_x_axis", "di_y_axis", "di_z_axis", "di_x_axis_rotation", "di_y_axis_rotation", "di_z_axis_rotation", "di_slider_0", "di_slider_1"];
    internal static readonly string[] XboxButtons = ["xinput_dpad_up", "xinput_dpad_down", "xinput_dpad_left", "xinput_dpad_right", "xinput_button_start", "xinput_button_back", "xinput_button_left_stick", "xinput_button_right_stick", "xinput_button_left_shoulder", "xinput_button_right_shoulder", "", "", "xinput_button_a", "xinput_button_b", "xinput_button_x", "xinput_button_y"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static string SettingsPath => Path.Combine(LauncherSettings.DataDirectory, "driving-controls.json");
    public static string ProfilesPath => Path.Combine(LauncherSettings.DataDirectory, "driving-profiles");
    public static DrivingControls Load() => File.Exists(SettingsPath) ? LoadFile(SettingsPath) : new();
    public static DrivingControls LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 1 or > MaxFileBytes) throw new IOException("Driving controls file is empty or too large.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Driving controls file changed while reading.");
        try
        {
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) json = json[3..];
            var value = JsonSerializer.Deserialize<DrivingControls>(json, Json) ?? throw new IOException("Invalid driving controls file.");
            value.Validate();
            value.Version = 2;
            return value;
        }
        catch (JsonException ex) { throw new IOException("Invalid driving controls JSON.", ex); }
    }
    public void Save() => SaveFile(SettingsPath);
    public void SaveFile(string path)
    {
        Validate();
        if (Enabled && Problems() is { Count: > 0 } problems) throw new IOException(string.Join(Environment.NewLine, problems));
        var value = new DrivingControls { Enabled = Enabled, ListenAllDevices = ListenAllDevices, Bindings = [.. Bindings] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxFileBytes) throw new IOException("Driving controls file is too large.");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Validate()
    {
        if (Version is not (1 or 2) || Bindings is null || Bindings.Count > Actions.Length * MaxDevices) throw new IOException("Invalid driving controls configuration.");
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Bindings)
        {
            if (b is null || !Actions.Contains(b.Action) || !Calibrations.Contains(b.Calibration) ||
                string.IsNullOrWhiteSpace(b.Device) || b.Device.Length > 260 || b.Device.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(b.DeviceId) || b.DeviceId.Length > 128 || b.DeviceId.Any(char.IsControl) ||
                b.DeadZone is < 0 or > .9m || b.Saturation is < .1m or > 1 || b.DeadZone >= b.Saturation || (float)b.DeadZone >= (float)b.Saturation || !ValidInput(b))
                throw new IOException("Invalid driving binding. Check device, input and calibration.");
            if (!slots.Add(b.DeviceId + "/" + b.Action)) throw new IOException("Duplicate binding for " + b.Action + " on " + b.Device + ".");
            if (devices.TryGetValue(b.DeviceId, out var family) && !StringComparer.OrdinalIgnoreCase.Equals(family, b.DeviceType))
                throw new IOException("A device has conflicting controller types.");
            devices[b.DeviceId] = b.DeviceType;
        }
        if (devices.Count > MaxDevices) throw new IOException($"Driving controls support up to {MaxDevices} devices per profile.");
    }
    private static bool ValidInput(DrivingBinding b)
    {
        if (b.Input is null || b.DeviceType is null) return false;
        if (b.Keyboard) return b.DeviceId == "keyboard" && Regex.IsMatch(b.Input, @"\Avk_code_0x[0-9A-F]{2}\z") &&
            int.TryParse(b.Input.AsSpan(10), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var key) && key is >= 8 and <= 254;
        if (b.DeviceType == "xinput_pad") return Regex.IsMatch(b.DeviceId, @"\Axinput:[0-3]\z") &&
            (XboxAxes.Contains(b.Input) || XboxButtons.Contains(b.Input) && b.Input.Length > 0);
        return b.DeviceType.Length == 38 && b.DeviceId.Length == 38 && Guid.TryParseExact(b.DeviceType, "B", out _) && Guid.TryParseExact(b.DeviceId, "B", out _) &&
            (DirectAxes.Contains(b.Input) || Regex.IsMatch(b.Input, @"\Adi_dpad_[0-3]_(up|down|left|right)\z") ||
             Regex.IsMatch(b.Input, @"\Adi_button_([0-9]|[1-9][0-9]|1[01][0-9]|12[0-7])\z"));
    }
    public List<string> Problems()
    {
        var errors = new List<string>();
        foreach (var device in Bindings.GroupBy(b => b.DeviceId, StringComparer.OrdinalIgnoreCase))
        {
            var left = device.FirstOrDefault(b => b.Action == "Steer Left");
            var right = device.FirstOrDefault(b => b.Action == "Steer Right");
            if (left is not null && right is not null && left.Input == right.Input && left.Calibration == right.Calibration)
                errors.Add("Left and right steering use the same direction. Rebind steering.");
        }
        foreach (var b in Bindings)
            if (b.DeviceType == "xinput_pad" && b.Input.EndsWith("_trigger", StringComparison.Ordinal) && b.Calibration != "uniDirPos")
                errors.Add(b.Action + ": Xbox triggers must increase when pressed.");
        return errors;
    }
    public void Assign(DrivingBinding binding)
    {
        Bindings.RemoveAll(b => b.Action == binding.Action && StringComparer.OrdinalIgnoreCase.Equals(b.DeviceId, binding.DeviceId));
        Bindings.Add(binding);
    }
    public void ApplyXboxPreset() { foreach (var binding in XboxPreset()) Assign(binding); }
    // GRID Autosport's own input/xinput_pad_0.xml, one input per action (the D-pad for menus).
    public static List<DrivingBinding> XboxPreset()
    {
        var result = new List<DrivingBinding>();
        void Add(string action, string input, string calibration = "uniDirPos", decimal dead = 0, decimal saturation = 1) => result.Add(new()
            { Action = action, Device = "Xbox controller", DeviceId = "xinput:0", DeviceType = "xinput_pad", Input = "xinput_" + input, Calibration = calibration, DeadZone = dead, Saturation = saturation });
        Add("Steer Left", "left_stick_x", "biDirLower", .2m); Add("Steer Right", "left_stick_x", "biDirUpper", .2m);
        Add("Accelerate", "right_trigger"); Add("Brake", "left_trigger"); Add("Handbrake", "button_b"); Add("Clutch", "button_left_shoulder");
        Add("Gear Up", "button_a"); Add("Gear Down", "button_x"); Add("Change View", "button_back");
        Add("Look Left", "right_stick_x", "biDirLower", .3m, .8m); Add("Look Right", "right_stick_x", "biDirUpper", .3m, .8m);
        Add("Look Down", "right_stick_y", "biDirLower", .3m, .8m); Add("Look Up", "right_stick_y", "biDirUpper", .3m, .8m);
        Add("Look Back", "button_right_stick"); Add("Pause", "button_start"); Add("Instant Replay", "button_y");
        Add("Radio Car Health", "dpad_left"); Add("Radio Rival Update", "dpad_down"); Add("Radio Team Update", "dpad_right"); Add("Radio Split Time", "dpad_up");
        Add("Radio TeamMate Push", "button_right_shoulder"); Add("Radio TeamMate Drop", "button_left_shoulder");
        Add("Menu Start Button", "button_start"); Add("Menu Select", "button_a"); Add("Menu Back", "button_b");
        foreach (var direction in new[] { "Up", "Down", "Left", "Right" }) Add("Menu " + direction, "dpad_" + direction.ToLowerInvariant());
        Add("Replay Rewind", "left_trigger"); Add("Replay Fast Forward", "right_trigger"); Add("Replay Pause", "button_a");
        Add("Replay Next Camera", "button_back"); Add("Replay Jump In", "button_x"); Add("Replay Exit", "button_b"); Add("Replay UI On Off", "button_left_shoulder");
        return result;
    }
    public static string? KeyInput(Keys key)
    {
        var value = (int)(key & Keys.KeyCode);
        if (value is < 8 or > 254 || key is Keys.LWin or Keys.RWin or Keys.Apps || (key & Keys.Modifiers) != 0) return null;
        return "vk_code_0x" + value.ToString("X2", CultureInfo.InvariantCulture);
    }
}

internal sealed record DrivingWizardBatch(string Name, string Summary, string[] Actions);

// Binding wizard progress, one batch at a time. Every batch after the first waits
// for Accept or Skip. Only finished batches are kept, so a skipped, cancelled or
// unfinished batch leaves that batch's current bindings untouched.
internal sealed class DrivingWizard
{
    private readonly List<DrivingBinding> kept = [], pending = [];
    public IReadOnlyList<DrivingWizardBatch> Batches { get; }
    public int BatchIndex { get; private set; }
    public int Step { get; private set; }
    public int FinishedBatches { get; private set; }
    public bool Asking { get; private set; }
    public bool Done { get; private set; }
    public DrivingWizardBatch Batch => Batches[BatchIndex];
    public string? Action => Asking || Done ? null : Batch.Actions[Step];
    public IReadOnlyList<DrivingBinding> Bindings => kept;
    public DrivingWizard(IReadOnlyList<DrivingWizardBatch> batches)
    {
        if (batches.Count == 0 || batches.Any(b => b.Actions.Length == 0)) throw new ArgumentException("The binding wizard needs actions.", nameof(batches));
        Batches = batches;
    }
    public void Record(DrivingBinding? binding)
    {
        var action = Action ?? throw new InvalidOperationException("The binding wizard is not capturing.");
        if (binding is not null && binding.Action != action) throw new InvalidOperationException("Binding does not match the wizard step.");
        if (binding is not null) pending.Add(binding);
        if (++Step < Batch.Actions.Length) return;
        kept.AddRange(pending); pending.Clear(); ++FinishedBatches; Advance();
    }
    public void Accept()
    {
        if (!Asking) throw new InvalidOperationException("The binding wizard is not asking.");
        Asking = false;
    }
    public void Skip()
    {
        if (!Asking) throw new InvalidOperationException("The binding wizard is not asking.");
        Advance();
    }
    public void Finish() { pending.Clear(); Asking = false; Done = true; }
    private void Advance()
    {
        Step = 0;
        if (BatchIndex + 1 < Batches.Count) { ++BatchIndex; Asking = true; }
        else { Asking = false; Done = true; }
    }
}
