// Ported from GRID2VR/launcher/DeviceRecenter.cs (2026-11-13): recenter from a controller or
// wheel button. GRID2VR runs a hidden helper process; here the launcher itself already waits for
// the game for the whole session (VrPlaySession.Run), so the watcher is a thread of it.
using System.Text.Json;

namespace GRIDAutosportVR;

// One request per press: a release must be seen first, and only while the device is connected.
internal sealed class RecenterEdge
{
    private bool armed;
    public bool Update(bool connected, bool down)
    {
        if (!connected) { armed = false; return false; }
        if (!down) { armed = true; return false; }
        if (!armed) return false;
        armed = false; return true;
    }
}

internal static class DeviceRecenter
{
    // The bridge (game_device.cpp) consumes this file from the play session folder and recenters
    // exactly as the keyboard shortcut does.
    internal const string RequestFile = "recenter.request";

    public static void Validate(DrivingBinding binding)
    {
        // Device validation is shared with the driving bindings; no game binding is made.
        new DrivingControls { Bindings = [binding with { Action = "Change View" }] }.Validate();
        if (binding.Action != "Recenter" || binding.Keyboard || DrivingInput.IsAxis(binding))
            throw new IOException("Recenter needs a controller or wheel button or hat direction.");
    }

    public static string ToJson(DrivingBinding binding) { Validate(binding); return JsonSerializer.Serialize(binding); }

    public static DrivingBinding FromJson(string json)
    {
        if (json.Length > 4096) throw new JsonException("recenterDevice exceeds its size limit.");
        var binding = JsonSerializer.Deserialize<DrivingBinding>(json) ?? throw new JsonException("recenterDevice is empty.");
        try { Validate(binding); }
        catch (IOException error) { throw new JsonException("recenterDevice: " + error.Message, error); }
        return binding;
    }

    public static bool Down(DrivingBinding binding, DrivingInput.Sample sample)
    {
        if (sample.Connected == 0) return false;
        if (binding.DeviceType == "xinput_pad") return sample.Buttons[Array.IndexOf(DrivingControls.XboxButtons, binding.Input)] != 0;
        if (binding.Input.StartsWith("di_button_", StringComparison.Ordinal)) return sample.Buttons[int.Parse(binding.Input[10..])] != 0;
        var parts = binding.Input.Split('_');
        return DrivingInput.PovDirection(sample.Pov[int.Parse(parts[2])]) == Array.IndexOf(new[] { "up", "right", "down", "left" }, parts[3]);
    }

    // Polls the bound device every 25 ms until disposed and writes one request file per press
    // (none while the bridge has not consumed the previous one). Read-only, nonexclusive input.
    public static IDisposable Watch(string sessionFolder, DrivingBinding binding, Action<string> record)
    {
        Validate(binding);
        var stop = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            int requests = 0;
            try
            {
                using var window = new Form { ShowInTaskbar = false }; // DirectInput needs a window handle; never shown.
                var handle = window.Handle;
                DrivingInput? input = null; DrivingInput.Device? device = null;
                var edge = new RecenterEdge(); long refreshAt = 0;
                try
                {
                    while (!stop.IsSet)
                    {
                        var now = Environment.TickCount64;
                        if (device is null && now >= refreshAt)
                        {
                            input?.Dispose(); input = new DrivingInput(handle);
                            device = input.Devices.FirstOrDefault(d => d.Id.Equals(binding.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                                                                       d.Product.Equals(binding.DeviceType, StringComparison.OrdinalIgnoreCase));
                            refreshAt = now + 2000;
                        }
                        var sample = device is null ? DrivingInput.Sample.Empty() : input!.Read(device);
                        if (edge.Update(sample.Connected != 0, Down(binding, sample)))
                        {
                            try { using var file = new FileStream(Path.Combine(sessionFolder, RequestFile), FileMode.CreateNew, FileAccess.Write, FileShare.Read); requests++; }
                            catch (IOException) { } // The bridge has not taken the previous request yet.
                        }
                        if (sample.Connected == 0) device = null;
                        Application.DoEvents();
                        stop.Wait(25);
                    }
                }
                finally { input?.Dispose(); }
                record($"Controller recenter: {requests} request(s) from {DrivingInput.Description(binding)}.");
            }
            catch (Exception error) { record("Controller recenter stopped: " + error.Message); }
        }) { IsBackground = true, Name = "Controller recenter" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return new Stopper(() => { stop.Set(); thread.Join(2000); });
    }

    private sealed class Stopper(Action stop) : IDisposable
    {
        private Action? pending = stop;
        public void Dispose() { Interlocked.Exchange(ref pending, null)?.Invoke(); }
    }
}
