using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GRIDAutosportVR;

// The launcher's driving bindings reach GRID Autosport as session-only edits of the game's own
// input presets (input/*.xml, listed in input/paths.xml; the schema GRID 2 uses). For each bound
// device family the matching stock preset is rewritten with the bound actions replaced, so no
// competing map has to win a priority tie: keyboard.xml and keyboard_azerty.xml for the keyboard,
// xinput_pad_0/1.xml for an Xbox pad, the wheel's own preset for a DirectInput device the game
// knows. A DirectInput device without a preset (G29, G920, Fanatec DD...) gets a new preset made
// from dinput_default.xml, listed after the last preset in paths.xml. VrPlaySession stages the
// changed files under its recovery lease and restores the originals after the session, as DiRT3VR
// does with its session action maps.
internal static class SessionInputMaps
{
    internal sealed record Change(string RelativePath, byte[] Content);
    internal const string PathsFile = "input/paths.xml";
    private const string NewPresetPrefix = "input/gasvr_device_";

    public static List<Change> Plan(string gameFolder, DrivingControls controls)
    {
        var changes = new List<Change>();
        if (!controls.Enabled || controls.Bindings.Count == 0) return changes;
        controls.Validate();
        var pathsText = File.ReadAllText(Path.Combine(gameFolder, "input", "paths.xml"));
        // The shipped tilttouch.xml (touch screens, never bound here) has a comment that is not
        // well-formed XML; a preset the game parses leniently is skipped, never rewritten.
        var maps = PresetFiles(pathsText).Select(file => (File: file, Document: TryLoad(Path.Combine(gameFolder, file))))
            .Where(map => map.Document is not null).Select(map => (map.File, Document: map.Document!)).ToList();
        var added = new List<string>();
        foreach (var family in Effective(controls.Bindings).GroupBy(Family, StringComparer.OrdinalIgnoreCase))
        {
            var targets = maps.Where(map => DeviceTypes(map.Document.Root!).Contains(family.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            if (targets.Count == 0)
            {
                if (family.Key is "keyboard" or "xinput_pad") throw new IOException("The game's " + family.Key + " input preset is missing from input/paths.xml.");
                var template = maps.FirstOrDefault(map => DeviceTypes(map.Document.Root!).Contains("di_default"));
                if (template.Document is null) throw new IOException("The game's input/dinput_default.xml preset is missing; cannot add a preset for " + family.First().Device + ".");
                var document = new XDocument(template.Document);
                var root = document.Root!;
                foreach (var attribute in root.Attributes().Where(a => a.Name.LocalName.StartsWith("device_type", StringComparison.Ordinal) ||
                         a.Name.LocalName is "shouldbindalldevicetypes" or "name" or "priority").ToList()) attribute.Remove();
                var file = NewPresetPrefix + added.Count.ToString(CultureInfo.InvariantCulture) + ".xml";
                var kept = root.Attributes().ToList();
                root.RemoveAttributes();
                root.Add(new XAttribute("name", Path.GetFileNameWithoutExtension(file)), new XAttribute("device_type", family.Key), new XAttribute("priority", "0"), kept);
                document.AddFirst(new XComment(" GRID Autosport VR: session preset for " + Sanitize(family.First().Device) + " "));
                Apply(document, family);
                changes.Add(new(file, Bytes(document)));
                added.Add(file);
                continue;
            }
            foreach (var target in targets)
            {
                var document = new XDocument(target.Document);
                Apply(document, family);
                changes.Add(new(target.File, Bytes(document)));
            }
        }
        if (added.Count > 0) changes.Add(new(PathsFile, Encoding.ASCII.GetBytes(InsertPresets(pathsText, added))));
        // One file per path: a preset matched by two families (it cannot be today) would be ambiguous.
        if (changes.GroupBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new IOException("Two bound devices map to the same game input preset. Bind one of them only.");
        return changes;
    }

    // The game's device_type for a binding: keyboard, xinput_pad, or the DirectInput product GUID.
    internal static string Family(DrivingBinding binding) =>
        binding.Keyboard ? "keyboard" : binding.DeviceType == "xinput_pad" ? "xinput_pad" : binding.DeviceType.ToUpperInvariant();

    internal static IEnumerable<string> DeviceTypes(XElement map) =>
        map.Attributes().Where(a => a.Name.LocalName == "device_type" || a.Name.LocalName.StartsWith("device_type_", StringComparison.Ordinal)).Select(a => a.Value);

    internal static List<string> PresetFiles(string pathsText)
    {
        var document = XDocument.Parse(pathsText);
        var files = new List<string>();
        foreach (var reader in document.Root!.Elements("xmlreader"))
        {
            if ((string?)reader.Attribute("processor") != "Input" || (string?)reader.Attribute("userdata") != "presets") continue;
            var file = ((string?)reader.Attribute("filename") ?? "").Replace('\\', '/');
            if (!ValidPath(file)) throw new IOException("Unexpected input preset path in input/paths.xml: " + file);
            files.Add(file);
        }
        return files;
    }

    internal static bool ValidPath(string relative) =>
        System.Text.RegularExpressions.Regex.IsMatch(relative, @"\Ainput/[A-Za-z0-9_]{1,64}\.xml\z") || relative == PathsFile;

    // Every bound action of the family replaces that action's inputs in the map; several bindings
    // for one action (two pads, say) are all kept, as the game's own maps do. In a preset for two
    // devices (a wheel base and its pedals: device_type_0 / device_type_1) the bindings are
    // restricted to this family's device, and the other device's inputs for the action are kept.
    //
    // The in-race actions the launcher does not bind are then cleared of clashing inputs, as the
    // game's own remap screen does when an input moves to another action. A preset copied from
    // dinput_default.xml keeps the gamepad's Radio TeamMate Drop / Push and Clutch on DirectInput
    // buttons 4 and 5, a G29's or G920's shift paddles, so with Gear Up / Down bound there every
    // shift also radioed the team-mate ("I've asked your teammate...") or pressed the clutch.
    // Look actions go further. The game's defaults put
    // Look Left/Right and Up/Down on the Z and Rz axes (a gamepad's right stick; dinput_default.xml
    // and the presets copied from it), and some wheel presets on their pedal axes, so with Brake on
    // Rz the view skewed at rest and swung while braking. Look actions lose any input this family
    // binds; on a DirectInput device (a wheel, pedals) they also lose every axis, since its Z and Rz
    // are pedals more often than a stick, and head tracking does the looking in VR. Buttons stay
    // (Look Back on a wheel button). Menu, replay, spectator and frontend actions share inputs with
    // driving by design and are left alone.
    // Settings saved before 0.2.5 can bind one input to a driving action and to a team radio or
    // Instant Replay action (the Xbox preset's radio on RB / LB, then Gear Up / Down moved there):
    // the session preset keeps the driving action only.
    internal static List<DrivingBinding> Effective(IEnumerable<DrivingBinding> bindings)
    {
        var all = bindings.ToList();
        return all.Where(b => !DrivingControls.Secondary(b.Action) || !all.Any(other => !DrivingControls.Secondary(other.Action) &&
            StringComparer.OrdinalIgnoreCase.Equals(other.DeviceId, b.DeviceId) && DrivingControls.Clash(other, b))).ToList();
    }

    private static void Apply(XDocument document, IGrouping<string, DrivingBinding> family)
    {
        var root = document.Root!;
        var shared = DeviceTypes(root).Count() > 1;
        var directInput = family.Key is not ("keyboard" or "xinput_pad");
        bool Ours(XElement entry) => (string?)entry.Attribute("restricted_device") is not { Length: > 0 } device ||
                                     string.Equals(device, family.Key, StringComparison.OrdinalIgnoreCase);
        foreach (var action in family.GroupBy(b => b.Action))
        {
            var element = root.Elements("Action").FirstOrDefault(e => (string?)e.Attribute("id") == action.Key);
            if (element is null) root.Add(element = new XElement("Action", new XAttribute("id", action.Key)));
            element.Elements().Where(Ours).ToList().Remove();
            foreach (var binding in action.DistinctBy(b => (b.Input, b.Calibration)))
                element.Add(new XElement("Axis", new XAttribute("id", binding.Input),
                    binding.Axis ? new object[] { new XAttribute("type", binding.Calibration), new XAttribute("deadzone", Number(binding.DeadZone)),
                                                  new XAttribute("saturation", Number(binding.Saturation)) } : [],
                    shared ? new XAttribute("restricted_device", family.Key) : null));
        }
        var actions = family.Select(b => b.Action).ToHashSet(StringComparer.Ordinal);
        var inputs = family.Select(b => b.Input).Where(input => input.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var element in root.Elements("Action").Where(e => (string?)e.Attribute("id") is { } id && RaceAction(id) && !actions.Contains(id)).ToList())
        {
            var look = LookAction((string)element.Attribute("id")!);
            var stale = element.Elements("Axis").Where(entry => Ours(entry) && (string?)entry.Attribute("id") is { Length: > 0 } input &&
                (inputs.Contains(input) || look && directInput && entry.Attribute("type") is not null)).ToList();
            if (stale.Count == 0) continue;
            stale.Remove();
            if (!element.Elements("Axis").Any()) element.Add(new XElement("Axis", new XAttribute("id", "")));
        }
    }

    // The in-race look actions and the frontend's car view tweak (the same axes in the game's maps).
    internal static bool LookAction(string id) =>
        id.StartsWith("Look ", StringComparison.Ordinal) || id.StartsWith("Fe View Tweak ", StringComparison.Ordinal);

    // Actions that act while driving: everything but the menus, replays, spectating and YouTube
    // uploads, whose inputs the game's own maps share with driving. The frontend's car view tweak
    // counts with the look actions.
    internal static bool RaceAction(string id) =>
        LookAction(id) || !(id.StartsWith("Menu ", StringComparison.Ordinal) || id.StartsWith("Fe ", StringComparison.Ordinal) ||
                            id.StartsWith("Replay ", StringComparison.Ordinal) || id.StartsWith("Spectator ", StringComparison.Ordinal) ||
                            id.StartsWith("Youtube ", StringComparison.Ordinal));

    private static string Number(decimal value) => value.ToString("0.0###", CultureInfo.InvariantCulture);
    private static string Sanitize(string text) => new(text.Where(c => c >= ' ' && c < 127 && c != '-').ToArray());

    private static XDocument? TryLoad(string fileName)
    {
        var info = new FileInfo(fileName);
        if (!info.Exists) throw new IOException("A game input preset listed in input/paths.xml is missing: " + fileName);
        if (info.Length > 1 << 20) throw new IOException("Unexpectedly large game input preset: " + fileName);
        XDocument document;
        try { document = XDocument.Load(fileName); }
        catch (XmlException) { return null; }
        return document.Root?.Name.LocalName == "ActionMap" ? document : null;
    }

    private static byte[] Bytes(XDocument document)
    {
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings)) document.Save(writer);
        stream.Write("\r\n"u8);
        return stream.ToArray();
    }

    // After the last preset entry, keeping the stock file's text (and its preset order) intact.
    internal static string InsertPresets(string pathsText, IReadOnlyList<string> files)
    {
        var lines = pathsText.Split('\n').ToList();
        var last = lines.FindLastIndex(line => line.Contains("processor=\"Input\"", StringComparison.Ordinal) && line.Contains("userdata=\"presets\"", StringComparison.Ordinal));
        if (last < 0) throw new IOException("input/paths.xml lists no input presets.");
        lines.InsertRange(last + 1, files.Select(file =>
            $"  <xmlreader processor=\"Input\" filename=\"{file}\" map=\"UPDATE\" pool=\"UPDATE_TEMPORARY\" userdata=\"presets\" />\r"));
        return string.Join('\n', lines);
    }
}
