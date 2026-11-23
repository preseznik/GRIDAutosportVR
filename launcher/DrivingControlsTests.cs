using System.Text;
using System.Xml.Linq;

namespace GRIDAutosportVR;

// Driving controls: the GRID2VR model, capture and wizard (ported tests), and the session input
// presets that deliver the bindings to GRID Autosport (SessionInputMaps / VrPlaySession).
internal static class DrivingControlsTests
{
    private const string Wheel = "{C24F046D-0000-0000-0000-504944564944}"; // A G29: no stock preset.
    private const string Known = "{B66E044F-0000-0000-0000-504944564944}"; // tm_t300_rs's device_type_0.

    internal static void Run(string data, Action<bool, string> check)
    {
        DrivingCaptureTests.Run();
        DrivingWizardTests.Run();
        Model(data, check);
        Presets(data, check);
    }

    private static DrivingBinding Pad(string action, string input, string calibration = "uniDirPos") =>
        new() { Action = action, Device = "Xbox controller", DeviceId = "xinput:0", DeviceType = "xinput_pad", Input = input, Calibration = calibration };
    private static DrivingBinding Direct(string action, string type, string input, string calibration = "uniDirPos", decimal dead = 0, decimal saturation = 1) =>
        new() { Action = action, Device = "Test wheel", DeviceId = "{11111111-2222-3333-4444-555555555555}".Replace("1", type[1..2]), DeviceType = type,
            Input = input, Calibration = calibration, DeadZone = dead, Saturation = saturation };

    private static void Model(string data, Action<bool, string> check)
    {
        var preset = DrivingControls.XboxPreset();
        var controls = new DrivingControls { Enabled = true };
        controls.ApplyXboxPreset();
        controls.Validate();
        check(controls.Problems().Count == 0, "Xbox preset is valid");
        check(preset.All(b => DrivingControls.Actions.Contains(b.Action)) && preset.Select(b => b.Action).Distinct().Count() == preset.Count, "Xbox preset: known actions, once each");
        check(preset.Single(b => b.Action == "Gear Up").Input == "xinput_button_a" && preset.Single(b => b.Action == "Gear Down").Input == "xinput_button_x",
            "Xbox preset follows the game's own pad map (A up, X down)");
        check(!DrivingControls.Actions.Contains("Horn") && DrivingControls.Actions.Contains("Radio Car Health"), "Autosport actions (team radio, no horn)");
        var file = Path.Combine(data, "driving-test.json");
        controls.SaveFile(file);
        var loaded = DrivingControls.LoadFile(file);
        check(loaded.Enabled && loaded.Bindings.SequenceEqual(controls.Bindings), "driving controls round-trip");
        File.WriteAllText(file, "{\"bindings\":[{\"action\":\"Horn\",\"deviceId\":\"keyboard\",\"device\":\"Keyboard\",\"deviceType\":\"keyboard\",\"input\":\"vk_code_0x48\"}]}");
        check(Throws(() => DrivingControls.LoadFile(file)), "unknown action rejected");
        var bad = new DrivingControls { Enabled = true, Bindings = [Pad("Steer Left", "xinput_left_stick_x", "biDirLower"), Pad("Steer Right", "xinput_left_stick_x", "biDirLower")] };
        check(bad.Problems().Count == 1 && Throws(() => bad.SaveFile(file)), "same steering direction refused");
        check(new DrivingControls().Bindings.Count == 0 && !new DrivingControls().Enabled, "off and empty by default");
        File.Delete(file);
        Resolve(check);
    }

    // A wheel Windows re-registered keeps its bindings; a missing or ambiguous one is reported (GRID2VR 9f1715a).
    private static void Resolve(Action<bool, string> check)
    {
        const string pedals = "{B6771DD2-0000-0000-0000-504944564944}", pad = "{0E8F0003-0000-0000-0000-504944564944}";
        const string oldWheel = "{267D2D20-E1EA-11F0-8005-444553540000}", newWheel = "{9A0C1E40-F1B2-11F0-8001-444553540000}";
        const string pedalId = "{11111111-0000-11F0-8000-444553540000}", twin = "{22222222-0000-11F0-8000-444553540000}";
        static DrivingControls Bound() => new() { Enabled = true, Bindings = [
            new() { Action = "Steer Left", DeviceId = oldWheel, Device = "G29", DeviceType = Wheel, Input = "di_x_axis", Calibration = "biDirLower" },
            new() { Action = "Steer Right", DeviceId = oldWheel, Device = "G29", DeviceType = Wheel, Input = "di_x_axis", Calibration = "biDirUpper" },
            new() { Action = "Brake", DeviceId = pedalId, Device = "Pedals", DeviceType = pedals, Input = "di_y_axis" },
            new() { Action = "Pause", DeviceId = "keyboard", Device = "Keyboard", DeviceType = "keyboard", Input = "vk_code_0x1B" },
            Pad("Accelerate", "xinput_right_trigger")] };
        var moved = Bound(); var (retargeted, missing) = moved.ResolveDevices([(newWheel, Wheel, "G29 Racing Wheel"), (pedalId, pedals, "Pedals")]);
        check(retargeted.SequenceEqual(["G29 Racing Wheel"]) && missing.Count == 0 &&
              moved.Bindings.Where(b => b.DeviceType == Wheel).All(b => b.DeviceId == newWheel && b.Device == "G29 Racing Wheel") &&
              moved.Bindings.Single(b => b.Action == "Brake").DeviceId == pedalId && moved.Bindings.Single(b => b.Action == "Accelerate").DeviceId == "xinput:0",
            "a re-registered wheel keeps its bindings; pedals, keyboard and pad untouched");
        moved.Validate();
        var same = Bound(); (retargeted, missing) = same.ResolveDevices([(oldWheel.ToLowerInvariant(), Wheel, "G29"), (pedalId, pedals, "Pedals")]);
        check(retargeted.Count == 0 && missing.Count == 0 && same.Bindings.SequenceEqual(Bound().Bindings), "connected devices leave the bindings as they are");
        var absent = Bound(); (retargeted, missing) = absent.ResolveDevices([(pedalId, pedals, "Pedals")]);
        check(retargeted.Count == 0 && missing.SequenceEqual(["G29"]) && absent.Bindings.SequenceEqual(Bound().Bindings), "an unplugged wheel is reported");
        var twins = Bound(); (retargeted, missing) = twins.ResolveDevices([(newWheel, Wheel, "G29"), (twin, Wheel, "G29"), (pedalId, pedals, "Pedals")]);
        check(retargeted.Count == 0 && missing.SequenceEqual(["G29"]) && twins.Bindings.SequenceEqual(Bound().Bindings), "two wheels of one product: reported, nothing moves");
        // A compatibility-mode switch: the same instance now reports another product.
        var other = Bound(); (retargeted, missing) = other.ResolveDevices([(oldWheel, pad, "Gamepad"), (pedalId, pedals, "Pedals")]);
        check(retargeted.Count == 0 && missing.SequenceEqual(["G29"]) && other.Bindings.SequenceEqual(Bound().Bindings), "another product never takes over a wheel's bindings");
    }

    private static void Presets(string data, Action<bool, string> check)
    {
        var game = Path.Combine(data, "input-game");
        var input = Path.Combine(game, "input");
        Directory.CreateDirectory(input);
        static string Map(string header, params string[] actions) =>
            "<!-- test map -->\r\n" + header + "\r\n" + string.Concat(actions.Select(a => $"  <Action id=\"{a}\">\r\n    <Axis id=\"\" />\r\n  </Action>\r\n")) + "</ActionMap>\r\n";
        // The game's look defaults (dinput_default.xml): Look Up on Rz, Look Left on Z, Look Back on a
        // button, and a menu action sharing Rz; the keyboard's Look Back on P.
        static string With(string map, string actions) => map.Replace("</ActionMap>", actions + "</ActionMap>");
        static string Action(string id, params string[] entries) =>
            $"  <Action id=\"{id}\">\r\n" + string.Concat(entries.Select(e => "    " + e + "\r\n")) + "  </Action>\r\n";
        const string rz = "<Axis id=\"di_z_axis_rotation\" type=\"biDirLower\" deadzone=\"0.3\" saturation=\"0.8\" />";
        File.WriteAllText(Path.Combine(input, "keyboard.xml"), With(Map("<ActionMap name=\"keyboard_default\" device_type=\"keyboard\" priority=\"11\">", "Accelerate", "Pause"),
            Action("Look Back", "<Axis id=\"vk_code_0x50\" />") + Action("Look Left", "<Axis id=\"vk_code_0x4A\" />")));
        File.WriteAllText(Path.Combine(input, "xinput_pad_0.xml"), Map("<ActionMap name=\"xinput_pad\" device_type=\"xinput_pad\" priority=\"5\">", "Accelerate", "Pause"));
        File.WriteAllText(Path.Combine(input, "dinput_default.xml"), With(Map("<ActionMap name=\"di_default\" device_type=\"di_default\" priority=\"16\">", "Accelerate", "Steer Left"),
            Action("Look Up", rz) + Action("Look Left", "<Axis id=\"di_z_axis\" type=\"biDirLower\" deadzone=\"0.3\" saturation=\"0.8\" />") +
            Action("Look Back", "<Axis id=\"di_button_11\" />") + Action("Look Down", "<Axis id=\"di_button_5\" />") + Action("Fe Menu Up", rz)));
        const string pedals = "{B660044F-0000-0000-0000-504944564944}";
        File.WriteAllText(Path.Combine(input, "tm_t300rs.xml"), With(Map($"<ActionMap name=\"tm_t300_rs\" device_type_0=\"{Known}\" device_type_1=\"{pedals}\" priority=\"0\" shouldbindalldevicetypes=\"true\">"),
            Action("Accelerate", $"<Axis id=\"di_y_axis\" type=\"uniDirNeg\" deadzone=\"0.0\" saturation=\"1.0\" restricted_device=\"{Known}\" />",
                   $"<Axis id=\"di_x_axis_rotation\" type=\"uniDirNeg\" deadzone=\"0.0\" saturation=\"1.0\" restricted_device=\"{pedals}\" />") +
            Action("Look Up", $"<Axis id=\"di_y_axis_rotation\" type=\"biDirLower\" deadzone=\"0.1\" saturation=\"1.0\" restricted_device=\"{pedals}\" />",
                   "<Axis id=\"di_button_3\" />")));
        var paths = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<dataset>\r\n" + string.Concat(new[] { "dinput_default", "keyboard", "tm_t300rs", "xinput_pad_0" }.Select(f =>
            $"  <xmlreader processor=\"Input\" filename=\"input/{f}.xml\" map=\"UPDATE\" pool=\"UPDATE_TEMPORARY\" userdata=\"presets\" />\r\n")) +
            "  <xmlreader processor=\"InputStrings\" filename=\"input/strings.xml\" map=\"UPDATE\" pool=\"UPDATE_TEMPORARY\" userdata=\"strings\" />\r\n</dataset>\r\n";
        File.WriteAllText(Path.Combine(input, "paths.xml"), paths);

        var controls = new DrivingControls { Enabled = false, Bindings = [Pad("Accelerate", "xinput_button_a")] };
        check(SessionInputMaps.Plan(game, controls).Count == 0, "disabled bindings change nothing");
        controls.Enabled = true;
        controls.Bindings = [Pad("Accelerate", "xinput_right_trigger"), Pad("Pause", "xinput_button_y"),
            new() { Action = "Pause", Device = "Keyboard", DeviceId = "keyboard", DeviceType = "keyboard", Input = "vk_code_0x50" },
            Direct("Steer Left", Wheel, "di_x_axis", "biDirLower", .05m, .9m), Direct("Gear 1", Wheel, "di_button_12"),
            Direct("Brake", Wheel, "di_z_axis_rotation", "uniDirNeg"), Direct("Gear Up", Wheel, "di_button_11"),
            Direct("Accelerate", Known, "di_y_axis", "uniDirNeg")];
        var changes = SessionInputMaps.Plan(game, controls);
        check(changes.Select(c => c.RelativePath).Order(StringComparer.Ordinal).SequenceEqual(
            ["input/gasvr_device_0.xml", "input/keyboard.xml", "input/paths.xml", "input/tm_t300rs.xml", "input/xinput_pad_0.xml"]), "one change per bound family, plus the new preset");
        XElement Root(string path) => XDocument.Parse(Encoding.UTF8.GetString(changes.Single(c => c.RelativePath == path).Content)).Root!;
        XElement[] Axes(XElement map, string action) => map.Elements("Action").Single(a => (string?)a.Attribute("id") == action).Elements("Axis").ToArray();
        var pad = Root("input/xinput_pad_0.xml");
        check(Axes(pad, "Accelerate") is [var trigger] && (string?)trigger.Attribute("id") == "xinput_right_trigger" && (string?)trigger.Attribute("type") == "uniDirPos" &&
              (string?)trigger.Attribute("deadzone") == "0.0" && (string?)trigger.Attribute("saturation") == "1.0", "pad axis written with calibration");
        check(Axes(pad, "Pause") is [var button] && (string?)button.Attribute("id") == "xinput_button_y" && button.Attribute("type") is null, "pad button written without calibration");
        check((string?)pad.Attribute("priority") == "5" && (string?)pad.Attribute("name") == "xinput_pad", "the stock map keeps its name and priority");
        check(Axes(Root("input/keyboard.xml"), "Pause") is [var key] && (string?)key.Attribute("id") == "vk_code_0x50", "keyboard map");
        var t300 = Root("input/tm_t300rs.xml");
        check(Axes(t300, "Accelerate") is [var other, var pedal] && (string?)other.Attribute("restricted_device") == pedals &&
              (string?)pedal.Attribute("id") == "di_y_axis" && (string?)pedal.Attribute("type") == "uniDirNeg" && (string?)pedal.Attribute("restricted_device") == Known,
              "known wheel uses its own preset (device_type_0), restricted to its device, the pedals' input kept");
        check(Axes(t300, "Look Up") is [var otherLook, var lookButton] && (string?)otherLook.Attribute("restricted_device") == pedals && (string?)lookButton.Attribute("id") == "di_button_3",
              "a two-device preset: the other device's look axis and the look button stay");
        var keys = Root("input/keyboard.xml");
        check(Axes(keys, "Look Back") is [var freedKey] && (string?)freedKey.Attribute("id") == "" &&
              Axes(keys, "Look Left") is [var lookKey] && (string?)lookKey.Attribute("id") == "vk_code_0x4A", "keyboard: a look key bound to Pause is cleared, other look keys stay");
        var added = Root("input/gasvr_device_0.xml");
        check((string?)added.Attribute("device_type") == Wheel && (string?)added.Attribute("priority") == "0" && (string?)added.Attribute("name") == "gasvr_device_0" &&
              added.Attribute("shouldbindalldevicetypes") is null, "unknown wheel: new preset from di_default");
        check(Axes(added, "Steer Left") is [var steer] && (string?)steer.Attribute("deadzone") == "0.05" && (string?)steer.Attribute("saturation") == "0.9" &&
              Axes(added, "Gear 1") is [var gear] && (string?)gear.Attribute("id") == "di_button_12" && Axes(added, "Accelerate") is [var unbound] && (string?)unbound.Attribute("id") == "",
              "new preset: bound actions written, an added action appended, the rest unbound");
        // The reported skew: Brake on Rz while the game's Look Up stayed on Rz.
        check(Axes(added, "Look Up") is [var lookUp] && (string?)lookUp.Attribute("id") == "" && Axes(added, "Look Left") is [var lookLeft] && (string?)lookLeft.Attribute("id") == "" &&
              Axes(added, "Look Back") is [var lookBack] && (string?)lookBack.Attribute("id") == "" &&
              Axes(added, "Look Down") is [var lookDown] && (string?)lookDown.Attribute("id") == "di_button_5", "wheel: look axes and the look button bound to Gear Up cleared, other look buttons kept");
        check(Axes(added, "Fe Menu Up") is [var menu] && (string?)menu.Attribute("id") == "di_z_axis_rotation", "menu actions keep their shared inputs");
        check(!added.Elements("Action").Where(a => SessionInputMaps.LookAction((string)a.Attribute("id")!)).SelectMany(a => a.Elements("Axis"))
                  .Any(a => (string?)a.Attribute("id") == "di_z_axis_rotation"), "no look action left on the brake axis");
        var newPaths = Encoding.ASCII.GetString(changes.Single(c => c.RelativePath == "input/paths.xml").Content);
        var lines = newPaths.Split("\r\n");
        check(newPaths.Replace("  <xmlreader processor=\"Input\" filename=\"input/gasvr_device_0.xml\" map=\"UPDATE\" pool=\"UPDATE_TEMPORARY\" userdata=\"presets\" />\r\n", "") == paths &&
              Array.FindIndex(lines, l => l.Contains("gasvr_device_0")) == Array.FindIndex(lines, l => l.Contains("xinput_pad_0")) + 1, "paths.xml gains one line after the last preset");
        check(SessionInputMaps.PresetFiles(newPaths).Contains("input/gasvr_device_0.xml"), "the added preset is listed");
        check(!SessionInputMaps.ValidPath("input/../x.xml") && !SessionInputMaps.ValidPath("input/a b.xml") && SessionInputMaps.ValidPath("input/lg_g27.xml"), "preset paths are confined to input/");
        Directory.Delete(game, true);
    }

    // With GASVR_TEST_GAME: plan against the real input folder, then stage and recover on a copy.
    internal static void Installed(string real, string game, string bridge, Action<bool, string> check)
    {
        var input = Path.Combine(game, "input");
        Directory.CreateDirectory(input);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(real, "input"), "*.xml")) File.Copy(file, Path.Combine(input, Path.GetFileName(file)));
        var before = Directory.EnumerateFiles(input).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
        var controls = new DrivingControls { Enabled = true };
        controls.ApplyXboxPreset();
        controls.Assign(Direct("Steer Left", Wheel, "di_x_axis", "biDirLower"));
        controls.Assign(Direct("Steer Right", Wheel, "di_x_axis", "biDirUpper"));
        controls.Assign(new() { Action = "Look Back", Device = "Keyboard", DeviceId = "keyboard", DeviceType = "keyboard", Input = "vk_code_0x42" });
        var changes = SessionInputMaps.Plan(game, controls);
        check(changes.Any(c => c.RelativePath == "input/xinput_pad_0.xml") && changes.Any(c => c.RelativePath == "input/xinput_pad_1.xml") &&
              changes.Any(c => c.RelativePath == "input/keyboard.xml") && changes.Any(c => c.RelativePath == "input/keyboard_azerty.xml") &&
              changes.Any(c => c.RelativePath == "input/gasvr_device_0.xml") && changes.Any(c => c.RelativePath == "input/paths.xml"), "real presets: pad, both keyboards, a new wheel preset");
        const string id = "abcdefabcdefabcdefabcdefabcdef12";
        VrPlaySession.Stage(game, bridge, id, null, changes);
        foreach (var change in changes) check(File.ReadAllBytes(Path.Combine(game, change.RelativePath)).SequenceEqual(change.Content), "staged " + change.RelativePath);
        check(VrPlaySession.Recover(game), "recover a session with input presets");
        var after = Directory.EnumerateFiles(input).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
        check(after.Count == before.Count && before.All(p => after.TryGetValue(p.Key, out var bytes) && bytes.SequenceEqual(p.Value)), "input presets restored byte for byte, the added one removed");
        // A preset changed during the session is preserved with its backup and the lease.
        VrPlaySession.Stage(game, bridge, id.Replace('a', '9'), null, changes);
        File.WriteAllText(Path.Combine(input, "keyboard.xml"), "changed");
        check(Throws(() => VrPlaySession.Recover(game)) && File.Exists(Path.Combine(game, Installation.PlayLease)), "independently changed preset keeps the lease");
        File.WriteAllBytes(Path.Combine(input, "keyboard.xml"), changes.Single(c => c.RelativePath == "input/keyboard.xml").Content);
        check(VrPlaySession.Recover(game) && File.ReadAllBytes(Path.Combine(input, "keyboard.xml")).SequenceEqual(before["keyboard.xml"]), "recover after putting it back");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (IOException) { return true; }
    }
}
