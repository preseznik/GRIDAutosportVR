using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace GRIDAutosportVR;

internal sealed record GraphicsChoice(string Id, string Label, Dictionary<string, string> Attributes);
internal sealed record GraphicsCategory(string Field, string Label, GraphicsChoice[] Choices);
internal sealed record GraphicsPreset(string Id, string Label, Dictionary<string, string> Values);
internal sealed record MultisamplingChoice(string Id, string Value);

// Graphics → Graphics preset, ported from GRID2VR. data/graphics-options.json is generated from the
// game's own system/hardware_settings_options.xml (tools/build_graphics_catalog.py): the quality
// categories of its Graphics menu, each choice's hardware_settings_config.xml attributes, and its
// five overall presets. "Game settings" changes nothing; any other preset is written into the
// player's hardware_settings_config.xml for one VR session and the player's own file is restored
// afterwards (VrPlaySession).
internal static class GraphicsCatalog
{
    internal const string GameSettings = "game", Balanced = "vrbalanced", Potato = "potato", Custom = "custom";
    internal const int PotatoScale = 70;
    // VR anti-aliasing: the game's own choice, or MSAA the VR renderer resolves.
    internal static readonly (string Id, string Label)[] VrMsaa =
        [(GameSettings, "Game setting"), ("off", "Off"), ("2xmsaa", "2× MSAA"), ("4xmsaa", "4× MSAA"), ("8xmsaa", "8× MSAA")];
    private const string MultisamplingTarget = "graphics_card/resolution@multisampling";
    private const string FullscreenTarget = "graphics_card/resolution@fullscreen";
    private const string VSyncTarget = "graphics_card/resolution@vsync";
    // Graphics → Desktop → Display: the game's own choice, exclusive fullscreen or a window of the
    // game's chosen size.
    internal static readonly (string Id, string Label)[] Displays = [(GameSettings, "Game setting"), ("fullscreen", "Fullscreen"), ("window", "Window")];
    internal static bool ValidDisplay(string id) => Displays.Any(d => d.Id == id);

    private sealed record Catalog(int Schema, string SourceOptionsSha256, GraphicsCategory[] Categories, GraphicsPreset[] Presets, MultisamplingChoice[] Multisampling);
    private static readonly Catalog catalog = Load();
    internal static IReadOnlyList<GraphicsCategory> Categories => catalog.Categories;
    internal static IReadOnlyList<GraphicsPreset> Presets => catalog.Presets;

    private static Catalog Load()
    {
        using var stream = typeof(GraphicsCatalog).Assembly.GetManifestResourceStream("GRIDAutosportVR.GraphicsOptions.json")
            ?? throw new IOException("Graphics catalog is missing.");
        var value = JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IOException("Graphics catalog is invalid.");
        if (value.Schema != 1 || value.Categories.Length != 23 || value.Presets.Length != 5 ||
            value.Categories.Select(c => c.Field).Distinct().Count() != value.Categories.Length ||
            !VrMsaa.Skip(1).All(m => value.Multisampling.Any(c => c.Id == m.Id && c.Value == m.Id)))
            throw new IOException("Graphics catalog has an unsupported schema.");
        foreach (var category in value.Categories)
            if (category.Choices.Length == 0 || category.Choices.Select(c => c.Id).Distinct().Count() != category.Choices.Length ||
                category.Choices.Any(c => c.Attributes.Count == 0 || c.Attributes.Keys.Any(k => !ValidTarget(k))))
                throw new IOException("Invalid graphics category: " + category.Field);
        foreach (var preset in value.Presets)
            if (preset.Values.Count != value.Categories.Length || value.Categories.Any(c =>
                !preset.Values.TryGetValue(c.Field, out var choice) || !c.Choices.Any(o => o.Id == choice)))
                throw new IOException("Invalid graphics preset: " + preset.Id);
        return value;
    }

    private static bool ValidTarget(string target) =>
        System.Text.RegularExpressions.Regex.IsMatch(target, @"\A[a-z_]+(/[a-z_]+)?@[A-Za-z_]+\z");

    internal static GraphicsPreset Preset(string id) => Presets.SingleOrDefault(p => p.Id == id) ?? throw new ArgumentException("Unknown graphics preset.");

    // VR Balanced, as GRID2VR's: the game's High with Medium crowd, and VR MSAA Off.
    private static Dictionary<string, string> BalancedValues() => new(Preset("high").Values) { ["Crowd"] = "medium" };

    // Quality is stored as "Field=choice,…" in catalog order ("" for Game settings), so launcher
    // choices stay comparable records.
    internal static string Encode(IReadOnlyDictionary<string, string> values)
    {
        var parts = new List<string>();
        foreach (var category in Categories)
        {
            if (!values.TryGetValue(category.Field, out var choice) || !category.Choices.Any(c => c.Id == choice))
                throw new ArgumentException("Choose a supported option for " + category.Label + ".");
            parts.Add(category.Field + "=" + choice);
        }
        if (values.Count != Categories.Count) throw new ArgumentException("Unknown graphics setting.");
        return string.Join(',', parts);
    }

    internal static Dictionary<string, string>? Decode(string text)
    {
        if (text.Length == 0) return null;
        var values = new Dictionary<string, string>();
        foreach (var part in text.Split(','))
        {
            var pair = part.Split('=');
            if (pair.Length != 2 || !values.TryAdd(pair[0], pair[1])) throw new ArgumentException("Invalid graphics quality.");
        }
        Encode(values); // validates
        return values;
    }

    internal static bool ValidQuality(string text)
    {
        try { Decode(text); return true; }
        catch (ArgumentException) { return false; }
    }

    internal static bool ValidMsaa(string id) => VrMsaa.Any(m => m.Id == id);

    internal static string PresetId(string quality, string msaa, int scale)
    {
        if (quality.Length == 0) return msaa == GameSettings ? GameSettings : Custom;
        if (msaa == "off" && scale == PotatoScale && quality == Encode(Preset("ultralow").Values)) return Potato;
        if (msaa == "off" && quality == Encode(BalancedValues())) return Balanced;
        return Presets.FirstOrDefault(p => quality == Encode(p.Values))?.Id ?? Custom;
    }

    // The quality, VR MSAA and render scale a preset sets. Game presets keep MSAA and scale;
    // Potato also sets 70% render scale (GRID2VR's Potato / Low-End).
    internal static (string Quality, string Msaa, int Scale) ApplyPreset(string id, string msaa, int scale) => id switch
    {
        GameSettings => ("", GameSettings, scale),
        Potato => (Encode(Preset("ultralow").Values), "off", PotatoScale),
        Balanced => (Encode(BalancedValues()), "off", scale),
        _ => (Encode(Preset(id).Values), msaa, scale),
    };

    private static string? configOverride;
    internal static string ConfigPath => configOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games", "GRID Autosport", "hardwaresettings", "hardware_settings_config.xml");
    // Self-tests use a fixture instead of the player's file.
    internal static void UseConfig(string? path) => configOverride = path;
    internal const int MaximumConfigBytes = 65536;

    private static XAttribute? Attribute(XElement root, string target)
    {
        var (path, name) = (target[..target.IndexOf('@')], target[(target.IndexOf('@') + 1)..]);
        var element = path.Split('/').Aggregate((XElement?)root, (node, part) => node?.Element(part));
        return element?.Attribute(name);
    }

    private static XElement Root(byte[] config)
    {
        if (config.Length > MaximumConfigBytes) throw new IOException("The game's graphics settings file is unexpectedly large.");
        try
        {
            var root = XDocument.Parse(Encoding.UTF8.GetString(config), LoadOptions.PreserveWhitespace).Root;
            return root?.Name.LocalName == "hardware_settings_config" ? root : throw new IOException("The game's graphics settings file is not a hardware settings file.");
        }
        catch (System.Xml.XmlException error) { throw new IOException("The game's graphics settings file could not be read: " + error.Message, error); }
    }

    // Each category's choice in a hardware_settings_config.xml, or null when no choice matches.
    // A choice matches when every attribute the file holds agrees (the game does not save Trees
    // Ultra's meshes_in_all_passes back).
    internal static Dictionary<string, string?> Read(byte[] config, out string? msaa)
    {
        var root = Root(config);
        msaa = Attribute(root, MultisamplingTarget)?.Value;
        var result = new Dictionary<string, string?>();
        foreach (var category in Categories)
            result[category.Field] = category.Choices.FirstOrDefault(choice =>
            {
                var present = choice.Attributes.Where(a => Attribute(root, a.Key) is not null).ToList();
                return present.Count > 0 && present.All(a => Attribute(root, a.Key)!.Value == a.Value) &&
                    category.Choices.SelectMany(c => c.Attributes.Keys).Distinct().Except(choice.Attributes.Keys).All(k => Attribute(root, k) is null);
            })?.Id;
        return result;
    }

    // The player's file with the chosen quality and VR MSAA; everything else is kept as it is.
    // windowed: a VR session runs the game in a window of its chosen size (FullscreenTarget). In
    // exclusive fullscreen any focus change mid-race (another window, the VR streamer, Alt+Tab)
    // makes Windows drop the game out of fullscreen, and the game then falls back to its 800x600
    // window: the bridge's eye image follows the game's back buffer, so the headset view went to
    // about a fifth of its resolution for the rest of the session (23 Nov, 3840x2160 fullscreen).
    // A window of the same size keeps it, focus or not; the headset never shows the monitor.
    internal static byte[] Apply(byte[] config, string quality, string msaa, bool windowed = false)
    {
        var values = Decode(quality);
        if (values is null && msaa == GameSettings && !windowed) return config;
        if (!ValidMsaa(msaa)) throw new ArgumentException("Unsupported VR anti-aliasing.");
        var root = Root(config);
        void Set(string target, string value)
        {
            var path = target[..target.IndexOf('@')];
            var element = path.Split('/').Aggregate((XElement?)root, (node, part) => node?.Element(part))
                ?? throw new IOException("The game's graphics settings file has no " + path + " entry. Start GRID Autosport once on the desktop, or choose Game settings.");
            element.SetAttributeValue(target[(target.IndexOf('@') + 1)..], value);
        }
        if (values is not null)
            foreach (var category in Categories)
            {
                var choice = category.Choices.Single(c => c.Id == values[category.Field]);
                foreach (var (target, value) in choice.Attributes) Set(target, value);
                foreach (var other in category.Choices.SelectMany(c => c.Attributes.Keys).Distinct().Except(choice.Attributes.Keys))
                    Attribute(root, other)?.Remove();
            }
        if (msaa != GameSettings) Set(MultisamplingTarget, msaa);
        var windowChanged = false;
        if (windowed && Attribute(root, FullscreenTarget) is { } fullscreen && !fullscreen.Value.Equals("false", StringComparison.OrdinalIgnoreCase))
        { fullscreen.Value = "false"; windowChanged = true; }
        // A VR session also runs without VSync, so the headset, not the monitor, sets the frame rate
        // (Graphics → Desktop → VSync is on by default and is the player's own setting).
        if (windowed && Attribute(root, VSyncTarget) is { } vsync && vsync.Value != "0")
        { vsync.Value = "0"; windowChanged = true; }
        if (!windowChanged && values is null && msaa == GameSettings) return config;
        return Serialize(config, root);
    }

    private static byte[] Serialize(byte[] config, XElement root)
    {
        var text = Encoding.UTF8.GetString(config);
        var declaration = text.StartsWith("<?xml", StringComparison.Ordinal) ? text[..(text.IndexOf("?>", StringComparison.Ordinal) + 2)] : "";
        var body = root.ToString(SaveOptions.DisableFormatting);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return new UTF8Encoding(false).GetBytes(declaration + (declaration.Length > 0 ? newline : "") + body + (text.EndsWith('\n') ? newline : ""));
    }

    // Graphics → Desktop: the player's file with VSync, anti-aliasing and the display mode set for
    // Launch on the desktop (Normal and Practice / Race). It is the game's own setting, kept after
    // the session as if chosen in the game's menu; VR sessions stage their own copy and restore it.
    internal static byte[] ApplyDesktop(byte[] config, bool vsync, string msaa, string display)
    {
        if (!ValidMsaa(msaa)) throw new ArgumentException("Unsupported desktop anti-aliasing.");
        if (!ValidDisplay(display)) throw new ArgumentException("Unsupported desktop display mode.");
        var root = Root(config);
        var changed = false;
        void Set(string target, string value)
        {
            var attribute = Attribute(root, target)
                ?? throw new IOException("The game's graphics settings file has no " + target + " entry. Start GRID Autosport once from Steam.");
            if (attribute.Value == value) return;
            attribute.Value = value;
            changed = true;
        }
        Set(VSyncTarget, vsync ? "1" : "0");
        if (msaa != GameSettings) Set(MultisamplingTarget, msaa);
        if (display != GameSettings) Set(FullscreenTarget, display == "fullscreen" ? "true" : "false");
        return changed ? Serialize(config, root) : config;
    }

    // "Ultra, VR MSAA Off" for a session log or summary.
    internal static string Describe(string quality, string msaa)
    {
        var preset = Presets.FirstOrDefault(p => quality == Encode(p.Values));
        var name = quality.Length == 0 ? "the game's own quality settings"
            : preset is not null ? preset.Label + " quality"
            : quality == Encode(BalancedValues()) ? "VR Balanced quality" : "custom quality";
        return name + (msaa == GameSettings ? "" : ", VR anti-aliasing " + VrMsaa.Single(m => m.Id == msaa).Label);
    }
}
