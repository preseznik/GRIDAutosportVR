using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace GRIDAutosportVR;

// Normal VR play through Steam: stage the verified VR executable and bridge under a durable
// lease, supervise the game, then restore the stock files. Ported from the VB launcher.
internal static partial class VrPlaySession
{
    internal static string StockSha256 => GameBuilds.Steam.StockSha256;
    private const string BridgeName = "gasxr.dll";
    private const string SessionMutex = Installation.SessionMutex;

    private sealed class Lease
    {
        public string? Id { get; set; }
        public string? GameFolder { get; set; }
        public string? BridgeHash { get; set; }
        // The bridge's file name in the game folder: gasxr.dll (null in older leases) or d3d11.dll (2014 build).
        public string? BridgeFile { get; set; }
        // The game build staged (GameBuilds); null in leases from before the 2014 build: Steam.
        public string? Build { get; set; }
        // Practice / Race: the session file in the game folder and the swapped system/flow.bin.
        public string? SessionHash { get; set; }
        public bool Flow { get; set; }
        // Driving bindings: game input presets changed (or added) for this session (SessionInputMaps).
        public List<InputFile>? Inputs { get; set; }
        // Graphics preset: the player's hardware_settings_config.xml in Documents (GraphicsCatalog).
        public InputFile? Graphics { get; set; }
    }
    private sealed class InputFile
    {
        public string? Path { get; set; }
        public string? OriginalHash { get; set; } // null: a preset added for the session.
        public string? StagedHash { get; set; }
    }

    private static string SessionFile(string id) => "GASVR_" + id + ".xml";
    private static string GraphicsBackup(string saved) => Path.Combine(saved, "original-hardware-settings.xml");

    // The player's graphics settings with the launcher's preset applied, or null when the game's
    // own settings are used (Game settings, or a preset that matches them already).
    internal sealed record GraphicsPlan(string Path, byte[] Original, byte[] Staged, string Description);

    // A VR session's graphics: the chosen quality and anti-aliasing, and always a window instead of
    // exclusive fullscreen (GraphicsCatalog.Apply), so a focus change cannot drop the eye resolution.
    internal static GraphicsPlan? PlanGraphics(Choices choices, string? path = null)
    {
        var gameQuality = choices.GraphicsQuality.Length == 0 && choices.VrMsaa == GraphicsCatalog.GameSettings;
        path ??= GraphicsCatalog.ConfigPath;
        if (!File.Exists(path))
        {
            if (gameQuality) return null; // The game makes the file on its first start, as it chooses.
            throw new IOException("GRID Autosport has not saved its graphics settings yet. Start it once with Launch, or set Graphics → Graphics preset to Game settings.");
        }
        var original = File.ReadAllBytes(path);
        var quality = GraphicsCatalog.Apply(original, choices.GraphicsQuality, choices.VrMsaa);
        var staged = GraphicsCatalog.Apply(original, choices.GraphicsQuality, choices.VrMsaa, windowed: true);
        if (staged.AsSpan().SequenceEqual(original)) return null;
        var description = GraphicsCatalog.Describe(choices.GraphicsQuality, choices.VrMsaa) +
            (staged.AsSpan().SequenceEqual(quality) ? "" : "; the game runs in a window of its chosen size instead of fullscreen");
        return new GraphicsPlan(path, original, staged, description);
    }
    private static string FlowFile(string folder) => Path.Combine(folder, "system", "flow.bin");

    private static string SessionFolder(string id) => Path.Combine(LauncherSettings.DataDirectory, "sessions", id);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string FileHash(string fileName)
    {
        using var input = File.OpenRead(fileName);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
    private static void WriteNew(string fileName, byte[] bytes)
    {
        using var output = new FileStream(fileName, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
    }
    private static void Record(string id, string message) =>
        File.AppendAllText(Path.Combine(SessionFolder(id), "supervisor.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);

    private static T UnderLock<T>(Func<T> action)
    {
        using var gate = new Mutex(false, SessionMutex);
        try
        {
            if (!gate.WaitOne(0)) throw new IOException("A GRID Autosport VR session is already running.");
        }
        catch (AbandonedMutexException)
        {
            // The former supervisor exited. Its durable lease still describes the installation.
        }
        try { return action(); }
        finally { gate.ReleaseMutex(); }
    }

    internal static void EnsureIdle(string? folder = null)
    {
        foreach (var processName in new[] { "GRIDAutosport", "GRIDAutosport_avx" })
            foreach (var entry in Process.GetProcessesByName(processName))
                using (entry)
                {
                    try
                    {
                        if (entry.HasExited) continue;
                        var imageName = entry.MainModule?.FileName;
                        if (folder is not null && !string.IsNullOrEmpty(imageName) &&
                            !string.Equals(Path.GetDirectoryName(imageName), folder, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    catch (InvalidOperationException) when (entry.HasExited) { continue; }
                    catch (Win32Exception error)
                    {
                        throw new IOException("Cannot verify the running game's installation. Close GRID Autosport before continuing.", error);
                    }
                    throw new IOException("Close GRID Autosport before changing its VR installation.");
                }
    }

    // End session: ask the running game in this folder to close (as its window's close button
    // does), then end the process if it has not exited within the grace period. The session
    // supervisor sees the exit and restores the original files. Returns false if none was running.
    public static bool EndSession(string folder, TimeSpan grace)
    {
        var games = new List<Process>();
        foreach (var name in new[] { "GRIDAutosport_avx", "GRIDAutosport" })
            foreach (var entry in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!entry.HasExited && string.Equals(Path.GetDirectoryName(entry.MainModule?.FileName), folder, StringComparison.OrdinalIgnoreCase))
                    {
                        games.Add(entry);
                        continue;
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
                entry.Dispose();
            }
        try
        {
            foreach (var game in games) game.CloseMainWindow();
            var deadline = DateTime.UtcNow + grace;
            foreach (var game in games)
            {
                var left = deadline - DateTime.UtcNow;
                if (!game.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero)) game.Kill();
            }
            return games.Count > 0;
        }
        finally { foreach (var game in games) game.Dispose(); }
    }

    private static string CheckedFolder(string? folder)
    {
        if (!Installation.GameExists(folder)) throw new IOException("Select your GRID Autosport installation.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder!));
    }

    // Steam arguments for the game process. eyeSize is the per-eye render size reported to the game.
    // A Practice / Race session adds -benchmarkAttract with its session file, and the devices the
    // bridge may make the player's controller (-gasvrdevice, DeviceClaim) in place of Press Start.
    // A Normal launch with driving bindings passes its bound devices (NormalClaim) too: the save
    // profile's controls replace the session preset after Press Start, so the bridge binds it again
    // after each loading screen and keeps the profile's own controls in its saves.
    internal static string[] SteamArguments(string id, Choices choices, (int Width, int Height)? eyeSize, bool session = false,
        IReadOnlyList<string>? claim = null, string? cockpitPack = null) =>
        ["-applaunch", Installation.AppId, "-gasvrplay=" + id, "-gasvrmode=openxr", "-gasvrhudfollow=" + (choices.HudFollowView ? "1" : "0"),
         "-gasvrrecenterkey=" + choices.RecenterKey, "-gasvrrecentermods=" + choices.RecenterModifiers, "-gasvrkeephorizon=" + (choices.KeepHorizonLevel ? "1" : "0"),
         .. eyeSize is { } eye ? new[] { $"-gasvreyesize={eye.Width}x{eye.Height}" } : [],
         .. choices.VrCockpit ? new[] { "-gasvrcockpit=1" } : [],
         "-gasvrhudparts=" + choices.HudParts, "-gasvrhuddistance=" + (int)(choices.HudDistance * 100m),
         "-gasvrscreendistance=" + (int)(choices.ScreenDistance * 100m),
         .. choices.Intro3D ? new[] { "-gasvrintro3d=1" } : [],
         .. choices.SkipIntro ? new[] { "-gasvrskipintro=1" } : [],
         .. choices.DiagnosticLogging ? new[] { "-gasvrdiagnostics=1" } : [],
         .. choices.PerformanceDisplay ? new[] { "-gasvrperformance=1" } : [],
         .. choices.ClassicColor ? new[] { "-gasvrclassiccolor=1" } : [],
         .. choices.NightCockpitLight ? new[] { "-gasvrnightcockpit=1" } : [],
         .. TextureFilters.Argument(choices.TextureFiltering) is { } filter ? new[] { filter } : [],
         .. choices.ForceFeedbackArgument is { } ffb ? new[] { ffb } : [],
         .. cockpitPack is not null ? new[] { "-gasvrcockpittextures=replace:" + cockpitPack } : [],
         "-gasvrseatkeys=" + choices.SeatKeys,
         .. session && Difficulty.Argument(choices) is { } difficulty ? new[] { difficulty } : [],
         .. session ? new[] { "-benchmarkAttract", SessionFile(id) } : [],
         .. claim is { Count: > 0 } ? new[] { "-gasvrdevice=" + string.Join(',', claim) } : []];

    // Desktop Launch with Practice / Race: the session file and the controller claim, while the game
    // renders on the desktop as it always does (-gasvrmode=desktop). The force feedback override
    // applies too; the VR-only choices (HUD, cockpit, seat, graphics preset) do not.
    internal static string[] DesktopArguments(string id, Choices choices, IReadOnlyList<string>? claim = null) =>
        ["-applaunch", Installation.AppId, "-gasvrplay=" + id, "-gasvrmode=desktop",
         .. choices.ForceFeedbackArgument is { } ffb ? new[] { ffb } : [],
         .. Difficulty.Argument(choices) is { } difficulty ? new[] { difficulty } : [],
         "-benchmarkAttract", SessionFile(id),
         .. claim is { Count: > 0 } ? new[] { "-gasvrdevice=" + string.Join(',', claim) } : []];

    // Practice / Race never shows the Press Start screen, where the game makes the device that
    // pressed Start the player's controller; without it only the keyboard drives. The bridge makes
    // the same native bind for the first of these that is connected: the devices the launcher's
    // steering is bound to, in order, then an Xbox controller (GRID2VR's "Auto"). Other DirectInput
    // devices (shifters, handbrakes, button boxes) are never guessed.
    internal static List<string> DeviceClaim(DrivingControls controls)
    {
        var families = new List<string>();
        if (controls.Enabled)
            foreach (var binding in controls.Bindings.Where(b => !b.Keyboard && b.Action is "Steer Left" or "Steer Right"))
            {
                var family = SessionInputMaps.Family(binding);
                if (System.Text.RegularExpressions.Regex.IsMatch(family, @"\A(xinput_pad|\{[0-9A-F]{8}(-[0-9A-F]{4}){3}-[0-9A-F]{12}\})\z") &&
                    !families.Contains(family, StringComparer.OrdinalIgnoreCase)) families.Add(family);
            }
        if (!families.Contains("xinput_pad")) families.Add("xinput_pad");
        return families.Take(8).ToList();
    }

    // Normal play: every controller the launcher binds (steering devices first). The player chose
    // their controller at Press Start, so no controller is guessed, and a keyboard is never claimed.
    internal static List<string> NormalClaim(DrivingControls controls)
    {
        var families = new List<string>();
        if (!controls.Enabled) return families;
        foreach (var binding in controls.Bindings.Where(b => !b.Keyboard).OrderBy(b => b.Action is "Steer Left" or "Steer Right" ? 0 : 1))
        {
            var family = SessionInputMaps.Family(binding);
            if (System.Text.RegularExpressions.Regex.IsMatch(family, @"\A(xinput_pad|\{[0-9A-F]{8}(-[0-9A-F]{4}){3}-[0-9A-F]{12}\})\z") &&
                !families.Contains(family, StringComparer.OrdinalIgnoreCase)) families.Add(family);
        }
        return families.Take(8).ToList();
    }

    // The names the in-game seat panel shows for each car (bridge/seat.cpp): "car<TAB>name" lines.
    internal static void WriteCarNames()
    {
        try
        {
            var path = Path.Combine(LauncherSettings.DataDirectory, "car-names.txt");
            Directory.CreateDirectory(LauncherSettings.DataDirectory);
            File.WriteAllText(path, string.Concat(RaceSession.CarLabels.Select(c => c.Key + "\t" + c.Value + "\n")), new System.Text.UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // The panel then shows the car's ID.
    }

    // The installed, verified cockpit texture pack's version when the choice is on (else stock textures).
    internal static string? CockpitPack(Choices choices, out string? note)
    {
        note = null;
        if (!choices.NewCockpitTextures) return null;
        if (CockpitTexturePack.Current is not { } pack) { note = "Cockpit textures: this build has no pack; original textures."; return null; }
        if (!CockpitTexturePack.Installed(pack)) { note = $"Cockpit textures {pack.Version}: not installed or failed verification; original textures."; return null; }
        note = $"Cockpit textures {pack.Version}: verified, {pack.Cars} cars.";
        return pack.Version;
    }

    // Settings → Diagnostics → Diagnostic logging: this PC and the launcher's choices, for bug reports.
    internal static string SystemSummary(Choices choices, IReadOnlyList<VrLayerBypass> disabled)
    {
        var gpu = GraphicsRecommendation.DetectGpu();
        var system = OpenXrRuntimes.SystemManifest();
        var layers = VrApiLayers.Catalog().Select(layer => layer.LayerName).ToList();
        return "Diagnostics: GRID Autosport VR " + Updates.CurrentVersion + " on " + Environment.OSVersion.VersionString +
               (Environment.Is64BitOperatingSystem ? " 64-bit" : "") + "; GPU " + (gpu is null ? "unknown" : $"{gpu.Name} ({gpu.DedicatedMb} MB)") +
               "; system OpenXR runtime " + (system ?? "none") + "; OpenXR add-on layers " + (layers.Count == 0 ? "none" : string.Join(", ", layers)) +
               (disabled.Count > 0 ? " (" + disabled.Count + " disabled for this session)" : "") +
               $"; render scale {choices.VrRenderScale}%, HUD {(choices.HudFollowView ? "follows view" : "fixed")} at {choices.HudDistance.ToString(System.Globalization.CultureInfo.InvariantCulture)} m, " +
               $"seated screen {choices.ScreenDistance.ToString(System.Globalization.CultureInfo.InvariantCulture)} m, performance display {(choices.PerformanceDisplay ? "on" : "off")}, {(choices.ClassicColor ? "classic" : "accurate")} colors, texture filtering {choices.TextureFiltering}, cockpit light at night {(choices.NightCockpitLight ? "on" : "off")}, Practice / Race difficulty: {Difficulty.Summary(choices)}.";
    }

    // sessionXml builds the Practice / Race session file from the saved choices (null for Normal).
    // layers: OpenXR add-on layers the VR renderer disables for this session (VrApiLayers.cs).
    // desktop: a desktop Practice / Race session (DesktopArguments), without the headset.
    public static int Run(string gameFolder, string bridgePath, (int Width, int Height)? eyeSize = null, string? renderNote = null, Func<Choices, byte[]?>? sessionXml = null,
        IReadOnlyList<VrLayerBypass>? layers = null, bool desktop = false)
    {
        var folder = CheckedFolder(gameFolder);
        var build = GameBuilds.Detect(folder) ?? throw new IOException("This version of GRID Autosport is unsupported. Verify the game files in Steam.");
        string? client = null;
        if (build.ThroughSteam)
        {
            client = SteamLibrary.SteamClient() ?? throw new IOException("Steam.exe was not found. Start Steam, then try again.");
            var library = Directory.GetParent(folder)!.Parent!.Parent!.FullName;
            if (!SteamLibrary.SteamLibraries().Any(root => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), library, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The selected installation is outside Steam's registered libraries.");
        }
        var choices = LauncherSettings.Load();
        return UnderLock(() =>
        {
            if (File.Exists(Path.Combine(folder, Installation.BenchmarkLease))) throw new IOException("Restore the outstanding benchmark session first.");
            EnsureIdle(folder);
            RestoreLease(folder);
            var id = Guid.NewGuid().ToString("N");
            VrLaunchStatus.Report(VrLaunchStatus.Preparing, "Preparing the game files…", id);
            var session = sessionXml?.Invoke(choices);
            if (desktop && session is null) throw new IOException("Launch on the desktop through the launcher needs Practice or Race.");
            var controls = DrivingControls.Load();
            var inputs = SessionInputMaps.Plan(folder, controls);
            var claim = session is not null ? DeviceClaim(controls) : inputs.Count > 0 ? NormalClaim(controls) : null;
            string? cockpitNote = null;
            var cockpitPack = desktop ? null : CockpitPack(choices, out cockpitNote);
            var graphics = desktop ? null : PlanGraphics(choices);
            WriteCarNames();
            try
            {
                Stage(folder, Installation.RendererFor(build, bridgePath), id, session, inputs, graphics, build);
                if (desktop) Record(id, "Desktop session: the game renders on the desktop; no headset.");
                else
                {
                    OpenXrRuntimes.WriteSessionChoice(SessionFolder(id), choices.Runtime);
                    Record(id, "VR runtime: " + (choices.Runtime == OpenXrRuntimes.Default ? "this PC's default 32-bit OpenXR runtime." : choices.Runtime + " (" + OpenXrRuntimes.Manifest(choices.Runtime) + ")."));
                    VrApiLayers.WriteSessionFile(SessionFolder(id), layers ?? []);
                }
                if (!desktop && layers is { Count: > 0 })
                    Record(id, "OpenXR layers disabled for this session: " + string.Join(", ", layers.Select(layer => layer.LayerName + " (" + layer.DisableVariable + ")")) + ".");
                // The Steam build starts through Steam (-applaunch); the 2014 build starts directly.
                var start = build.ThroughSteam
                    ? new ProcessStartInfo(client!) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(client) }
                    : new ProcessStartInfo(Path.Combine(folder, build.Executable)) { UseShellExecute = false, WorkingDirectory = folder };
                var arguments = desktop ? DesktopArguments(id, choices, claim) : SteamArguments(id, choices, eyeSize, session is not null, claim, cockpitPack);
                foreach (var arg in build.Arguments.Concat(arguments.Skip(build.ThroughSteam ? 0 : 2)))
                    start.ArgumentList.Add(arg);
                if (cockpitNote is not null) Record(id, cockpitNote);
                if (claim is not null) Record(id, "Practice / Race controller: the first connected of " + string.Join(", ", claim) + ".");
                if (renderNote is not null) Record(id, renderNote);
                if (choices.DiagnosticLogging && !desktop) Record(id, SystemSummary(choices, layers ?? []));
                if (session is not null) Record(id, choices.LaunchMode + " session: " + Encoding.UTF8.GetString(session).ReplaceLineEndings(" "));
                Record(id, build.ThroughSteam ? "Requesting normal play through Steam." : "Starting the " + build.Label + " directly.");
                VrLaunchStatus.Report(VrLaunchStatus.Steam, build.ThroughSteam ? "Steam is starting GRID Autosport…" : "Starting GRID Autosport…", id);
                var requested = DateTime.UtcNow;
                using (var steam = Process.Start(start))
                    if (steam is null) throw new IOException((build.ThroughSteam ? "Steam" : "Windows") + " could not start GRID Autosport.");
                using var recenterWatch = !desktop && choices.RecenterDevice is { } recenterDevice
                    ? DeviceRecenter.Watch(SessionFolder(id), recenterDevice, message => Record(id, message)) : null;
                var exitCode = WaitForGame(folder, id, requested, build, desktop);
                if (desktop) return exitCode;
                try
                {
                    if (VrPerformance.Capture(SessionFolder(id), choices, eyeSize) is { } performance)
                        Record(id, "Frame rate while driving: " + performance.Describe());
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    Record(id, "Frame rate summary not kept: " + error.Message);
                }
                return exitCode;
            }
            finally
            {
                if (File.Exists(Path.Combine(folder, Installation.PlayLease))) RestoreLease(folder);
            }
        });
    }

    public static bool Recover(string? gameFolder)
    {
        var folder = CheckedFolder(gameFolder);
        if (!File.Exists(Path.Combine(folder, Installation.PlayLease))) return false;
        return UnderLock(() => RestoreLease(folder));
    }

    // The marker and stock backup are committed before the lease. The lease is
    // committed before either game file is changed, including on an interrupted launch.
    internal static void Stage(string folder, string bridgePath, string id, byte[]? session = null, IReadOnlyList<SessionInputMaps.Change>? inputs = null,
        GraphicsPlan? graphics = null, GameBuild? build = null)
    {
        inputs ??= [];
        build ??= GameBuilds.Steam;
        var executable = Path.Combine(folder, build.Executable);
        var dll = Path.Combine(folder, build.BridgeFile);
        if (File.Exists(dll) || File.Exists(Path.Combine(folder, BridgeName)) || File.Exists(Path.Combine(folder, Installation.PlayLease)))
            throw new IOException("A VR bridge or play session already exists in the game folder. Existing files were preserved.");
        var stockFlow = session is null ? null : File.ReadAllBytes(FlowFile(folder));
        var guardedFlow = stockFlow is null ? null : PracticeFlow.Guarded(stockFlow);
        var stock = File.ReadAllBytes(executable);
        if (Hash(stock) != build.StockSha256 || !stock.AsSpan(build.ImportNameOffset, 10).SequenceEqual("d3d11.dll\0"u8))
            throw new IOException("This " + build.Executable + " is not the supported " + build.Label + ".");
        var candidate = (byte[])stock.Clone();
        Encoding.ASCII.GetBytes(BridgeName).CopyTo(candidate, build.ImportNameOffset);
        if (Hash(candidate) != build.VrSha256) throw new IOException("The VR executable verification failed.");
        var payload = File.ReadAllBytes(bridgePath);
        var inputOriginals = inputs.Select(change =>
        {
            if (!SessionInputMaps.ValidPath(change.RelativePath)) throw new IOException("Unexpected game input file " + change.RelativePath);
            var target = Path.Combine(folder, change.RelativePath);
            return File.Exists(target) ? File.ReadAllBytes(target) : null;
        }).ToList();
        var info = new Lease { Id = id, GameFolder = folder, BridgeHash = Hash(payload), BridgeFile = build.ProxyBridge ? build.BridgeFile : null, Build = build == GameBuilds.Steam ? null : build.Id, SessionHash = session is null ? null : Hash(session), Flow = session is not null,
            Inputs = inputs.Count == 0 ? null : inputs.Select((change, i) => new InputFile { Path = change.RelativePath,
                OriginalHash = inputOriginals[i] is { } original ? Hash(original) : null, StagedHash = Hash(change.Content) }).ToList(),
            Graphics = graphics is null ? null : new InputFile { Path = graphics.Path, OriginalHash = Hash(graphics.Original), StagedHash = Hash(graphics.Staged) } };
        var saved = SessionFolder(id);
        Directory.CreateDirectory(saved);
        WriteNew(Path.Combine(saved, "session.id"), Encoding.ASCII.GetBytes(id));
        WriteNew(Path.Combine(saved, build.BackupName), stock);
        if (FileHash(Path.Combine(saved, build.BackupName)) != build.StockSha256) throw new IOException("The stock backup verification failed.");
        if (stockFlow is not null)
        {
            WriteNew(Path.Combine(saved, "original-flow.bin"), stockFlow);
            if (FileHash(Path.Combine(saved, "original-flow.bin")) != PracticeFlow.StockSha256) throw new IOException("The flow backup verification failed.");
        }
        for (var i = 0; i < inputs.Count; ++i)
            if (inputOriginals[i] is { } original)
            {
                WriteNew(InputBackup(saved, i), original);
                if (FileHash(InputBackup(saved, i)) != info.Inputs![i].OriginalHash) throw new IOException("The input preset backup verification failed.");
            }
        if (graphics is not null)
        {
            WriteNew(GraphicsBackup(saved), graphics.Original);
            if (FileHash(GraphicsBackup(saved)) != info.Graphics!.OriginalHash) throw new IOException("The graphics settings backup verification failed.");
        }
        var prefix = Path.Combine(folder, "gasvr-play-" + id);
        WriteNew(prefix + ".lease.tmp", JsonSerializer.SerializeToUtf8Bytes(info));
        File.Move(prefix + ".lease.tmp", Path.Combine(folder, Installation.PlayLease));
        WriteNew(prefix + ".dll.tmp", payload);
        if (FileHash(prefix + ".dll.tmp") != info.BridgeHash) throw new IOException("The staged bridge verification failed.");
        File.Move(prefix + ".dll.tmp", dll);
        if (session is not null)
        {
            WriteNew(prefix + ".xml.tmp", session);
            if (FileHash(prefix + ".xml.tmp") != info.SessionHash) throw new IOException("The session file verification failed.");
            File.Move(prefix + ".xml.tmp", Path.Combine(folder, SessionFile(id)));
            WriteNew(prefix + ".flow.tmp", guardedFlow!);
            EnsureIdle(folder);
            if (FileHash(FlowFile(folder)) != PracticeFlow.StockSha256 || FileHash(prefix + ".flow.tmp") != PracticeFlow.GuardedSha256)
                throw new IOException("system/flow.bin changed during staging.");
            File.Replace(prefix + ".flow.tmp", FlowFile(folder), null);
            if (FileHash(FlowFile(folder)) != PracticeFlow.GuardedSha256) throw new IOException("The installed Practice/Race flow verification failed.");
            Record(id, "Staged session file " + info.SessionHash + " and guarded flow " + PracticeFlow.GuardedSha256 + ".");
        }
        for (var i = 0; i < inputs.Count; ++i)
        {
            var entry = info.Inputs![i];
            var target = Path.Combine(folder, inputs[i].RelativePath);
            var temporary = prefix + ".input-" + i + ".tmp";
            WriteNew(temporary, inputs[i].Content);
            if (FileHash(temporary) != entry.StagedHash) throw new IOException("The staged input preset verification failed.");
            EnsureIdle(folder);
            if (entry.OriginalHash is null)
            {
                if (File.Exists(target)) throw new IOException("A game input preset appeared during staging: " + inputs[i].RelativePath);
                File.Move(temporary, target);
            }
            else
            {
                if (FileHash(target) != entry.OriginalHash) throw new IOException("A game input preset changed during staging: " + inputs[i].RelativePath);
                File.Replace(temporary, target, null);
            }
            if (FileHash(target) != entry.StagedHash) throw new IOException("The installed input preset verification failed: " + inputs[i].RelativePath);
        }
        if (inputs.Count > 0) Record(id, "Staged driving bindings in " + string.Join(", ", inputs.Select(c => c.RelativePath)) + ".");
        if (graphics is not null)
        {
            var temporary = graphics.Path + ".gasvr-" + id + ".tmp";
            WriteNew(temporary, graphics.Staged);
            if (FileHash(temporary) != info.Graphics!.StagedHash) throw new IOException("The staged graphics settings verification failed.");
            EnsureIdle(folder);
            if (FileHash(graphics.Path) != info.Graphics.OriginalHash) throw new IOException("The game's graphics settings changed during staging.");
            File.Replace(temporary, graphics.Path, null);
            if (FileHash(graphics.Path) != info.Graphics.StagedHash) throw new IOException("The installed graphics settings verification failed.");
            Record(id, "Graphics for this session: " + graphics.Description + " (" + graphics.Path + "; the player's own file is restored afterwards).");
        }
        if (build.ProxyBridge)
        {
            EnsureIdle(folder);
            if (FileHash(executable) != build.StockSha256) throw new IOException("The game files changed during staging.");
            Record(id, "Staged verified bridge " + info.BridgeHash + " as " + build.BridgeFile + " beside the unchanged " + build.Executable + " (" + build.Label + ").");
            return;
        }
        WriteNew(prefix + ".exe.tmp", candidate);
        EnsureIdle(folder);
        if (FileHash(executable) != build.StockSha256 || FileHash(prefix + ".exe.tmp") != build.VrSha256) throw new IOException("The game files changed during staging.");
        File.Replace(prefix + ".exe.tmp", executable, null);
        if (FileHash(executable) != build.VrSha256) throw new IOException("The installed VR executable verification failed.");
        Record(id, "Staged verified " + build.Executable + " (" + build.Label + ") and bridge " + info.BridgeHash + ".");
    }

    private static void RemoveMatching(string fileName, string expected, string id)
    {
        var timer = Stopwatch.StartNew();
        var retried = false;
        while (true)
        {
            try
            {
                // Hash the actual contents for each attempt. Do not allow writers
                // between verification and delete; allow our open handle to be deleted.
                using (var input = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (Convert.ToHexStringLower(SHA256.HashData(input)) != expected) throw new IOException("Preserving independently changed file: " + fileName);
                    File.Delete(fileName);
                }
                if (retried) Record(id, $"Released staged bridge after {timer.ElapsedMilliseconds} ms.");
                return;
            }
            catch (FileNotFoundException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && (error.HResult & 0xFFFF) is 5 or 32 or 33)
            {
                var entry = new FileInfo(fileName);
                if (entry.Exists && entry.IsReadOnly) throw new IOException("The staged VR bridge is read-only. Preserving the file and recovery lease: " + fileName, error);
                if (timer.Elapsed >= TimeSpan.FromSeconds(8)) throw new IOException("Windows is still denying removal of the staged VR bridge. The recovery lease was kept: " + fileName, error);
                if (!retried) Record(id, "Waiting for Windows to release the verified staged bridge.");
                retried = true;
                Thread.Sleep(200);
            }
        }
    }

    private static void RemoveStageTemp(string fileName, string expected, string id)
    {
        if (!File.Exists(fileName)) return;
        if (FileHash(fileName) == expected) File.Delete(fileName);
        else Record(id, "Kept incomplete or independently changed staging file " + fileName);
    }

    [GeneratedRegex(@"\A[0-9a-f]{32}\z")] private static partial Regex SessionId();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z")] private static partial Regex Sha256Text();

    private static bool RestoreLease(string folder)
    {
        var leaseFile = Path.Combine(folder, Installation.PlayLease);
        if (!File.Exists(leaseFile)) return false;
        if (File.Exists(Path.Combine(folder, Installation.BenchmarkLease))) throw new IOException("A benchmark lease also exists. Preserving both sessions.");
        EnsureIdle(folder);
        var info = JsonSerializer.Deserialize<Lease>(File.ReadAllBytes(leaseFile));
        if (info is null || !SessionId().IsMatch(info.Id ?? "") || !Sha256Text().IsMatch(info.BridgeHash ?? "") ||
            (info.SessionHash is not null && !Sha256Text().IsMatch(info.SessionHash)) || info.BridgeFile is not (null or "d3d11.dll") ||
            !string.Equals(info.GameFolder, folder, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid VR play lease. The game files were preserved.");
        var id = info.Id!;
        var saved = SessionFolder(id);
        if (!File.ReadAllBytes(Path.Combine(saved, "session.id")).SequenceEqual(Encoding.ASCII.GetBytes(id))) throw new IOException("Session identity mismatch. The game files were preserved.");
        var build = GameBuilds.ById(info.Build);
        var backup = Path.Combine(saved, build.BackupName);
        if (FileHash(backup) != build.StockSha256) throw new IOException("The original game executable backup failed verification. The game files were preserved.");
        var prefix = Path.Combine(folder, "gasvr-play-" + id);
        var executable = Path.Combine(folder, build.Executable);
        var current = FileHash(executable);
        if (current == build.VrSha256)
        {
            var restoreTemp = prefix + ".restore.tmp";
            if (File.Exists(restoreTemp) && FileHash(restoreTemp) != build.StockSha256) restoreTemp = prefix + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            if (!File.Exists(restoreTemp)) WriteNew(restoreTemp, File.ReadAllBytes(backup));
            if (FileHash(restoreTemp) != build.StockSha256) throw new IOException("The restore copy failed verification.");
            File.Replace(restoreTemp, executable, null);
        }
        else if (current != build.StockSha256) // Stock: staging stopped before the swap, or recovery already restored it.
            throw new IOException("The installed game executable changed independently. The current file and backup were preserved.");
        if (FileHash(executable) != build.StockSha256) throw new IOException("Stock restoration could not be verified.");
        if (info.Flow) RestoreFlow(folder, saved, prefix, id);
        if (info.Inputs is not null) RestoreInputs(folder, saved, prefix, id, info.Inputs);
        if (info.Graphics is not null) RestoreGraphics(saved, id, info.Graphics);
        RemoveMatching(Path.Combine(folder, info.BridgeFile ?? BridgeName), info.BridgeHash!, id);
        if (info.SessionHash is not null)
        {
            RemoveMatching(Path.Combine(folder, SessionFile(id)), info.SessionHash, id);
            RemoveStageTemp(prefix + ".xml.tmp", info.SessionHash, id);
        }
        RemoveStageTemp(prefix + ".exe.tmp", build.VrSha256, id);
        RemoveStageTemp(prefix + ".dll.tmp", info.BridgeHash!, id);
        RemoveStageTemp(prefix + ".restore.tmp", build.StockSha256, id);
        Record(id, "Restored and verified stock " + build.Executable + " " + build.StockSha256 + ".");
        File.Delete(leaseFile);
        File.Delete(Path.Combine(saved, "session.id"));
        File.Delete(backup);
        if (info.Flow) File.Delete(Path.Combine(saved, "original-flow.bin"));
        for (var i = 0; i < (info.Inputs?.Count ?? 0); ++i) File.Delete(InputBackup(saved, i));
        if (info.Graphics is not null) File.Delete(GraphicsBackup(saved));
        return true;
    }

    // The player's own graphics settings come back after every session. Changes made in the game
    // during the session last for that session only (as GRID2VR); its final file is kept in the
    // session folder.
    private static void RestoreGraphics(string saved, string id, InputFile entry)
    {
        if (entry.Path is null || !string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(GraphicsCatalog.ConfigPath), StringComparison.OrdinalIgnoreCase) ||
            !Sha256Text().IsMatch(entry.OriginalHash ?? "") || !Sha256Text().IsMatch(entry.StagedHash ?? ""))
            throw new IOException("Invalid graphics settings entry in the VR play lease. The game files were preserved.");
        var target = entry.Path;
        var current = File.Exists(target) ? FileHash(target) : null;
        if (current != entry.OriginalHash)
        {
            var backup = GraphicsBackup(saved);
            if (FileHash(backup) != entry.OriginalHash) throw new IOException("The graphics settings backup failed verification. Your settings file was preserved: " + target);
            if (current is not null && current != entry.StagedHash)
            {
                File.Copy(target, Path.Combine(saved, "session-hardware-settings.xml"), true);
                Record(id, "The game changed its graphics settings during the session; that copy is session-hardware-settings.xml.");
            }
            var restoreTemp = target + ".gasvr-" + id + "-restore.tmp";
            if (File.Exists(restoreTemp)) File.Delete(restoreTemp);
            WriteNew(restoreTemp, File.ReadAllBytes(backup));
            if (FileHash(restoreTemp) != entry.OriginalHash) throw new IOException("The graphics settings restore copy failed verification.");
            if (current is null) File.Move(restoreTemp, target); else File.Replace(restoreTemp, target, null);
        }
        if (FileHash(target) != entry.OriginalHash) throw new IOException("Graphics settings restoration could not be verified: " + target);
        var stageTemp = target + ".gasvr-" + id + ".tmp";
        if (File.Exists(stageTemp) && FileHash(stageTemp) == entry.StagedHash) File.Delete(stageTemp);
        Record(id, "Restored and verified the player's graphics settings.");
    }

    private static string InputBackup(string saved, int index) => Path.Combine(saved, "original-input-" + index + ".xml");

    // Each preset goes back to its original (or, if the session added it, is removed). One that
    // changed independently is preserved with its backup, and the lease stays.
    private static void RestoreInputs(string folder, string saved, string prefix, string id, List<InputFile> inputs)
    {
        for (var i = 0; i < inputs.Count; ++i)
        {
            var entry = inputs[i];
            if (entry.Path is null || !SessionInputMaps.ValidPath(entry.Path) || !Sha256Text().IsMatch(entry.StagedHash ?? "") ||
                (entry.OriginalHash is not null && !Sha256Text().IsMatch(entry.OriginalHash))) throw new IOException("Invalid input preset entry in the VR play lease. The game files were preserved.");
            var target = Path.Combine(folder, entry.Path);
            var current = File.Exists(target) ? FileHash(target) : null;
            if (current == entry.StagedHash)
            {
                if (entry.OriginalHash is null) RemoveMatching(target, entry.StagedHash!, id);
                else
                {
                    var backup = InputBackup(saved, i);
                    if (FileHash(backup) != entry.OriginalHash) throw new IOException("The input preset backup failed verification. The game files were preserved: " + entry.Path);
                    var restoreTemp = prefix + ".input-restore-" + i + ".tmp";
                    if (File.Exists(restoreTemp) && FileHash(restoreTemp) != entry.OriginalHash) restoreTemp = prefix + ".input-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
                    if (!File.Exists(restoreTemp)) WriteNew(restoreTemp, File.ReadAllBytes(backup));
                    if (FileHash(restoreTemp) != entry.OriginalHash) throw new IOException("The input preset restore copy failed verification.");
                    File.Replace(restoreTemp, target, null);
                }
            }
            else if (current != entry.OriginalHash)
                throw new IOException("A game input preset changed independently. The current file and backup were preserved: " + entry.Path);
            if ((File.Exists(target) ? FileHash(target) : null) != entry.OriginalHash) throw new IOException("Input preset restoration could not be verified: " + entry.Path);
            RemoveStageTemp(prefix + ".input-" + i + ".tmp", entry.StagedHash!, id);
            if (entry.OriginalHash is not null) RemoveStageTemp(prefix + ".input-restore-" + i + ".tmp", entry.OriginalHash, id);
        }
        Record(id, "Restored and verified the game's input presets.");
    }

    private static void RestoreFlow(string folder, string saved, string prefix, string id)
    {
        var flow = FlowFile(folder);
        var backup = Path.Combine(saved, "original-flow.bin");
        switch (FileHash(flow))
        {
            case PracticeFlow.GuardedSha256:
                if (FileHash(backup) != PracticeFlow.StockSha256) throw new IOException("The original flow.bin backup failed verification. The game files were preserved.");
                var restoreTemp = prefix + ".flow-restore.tmp";
                if (File.Exists(restoreTemp) && FileHash(restoreTemp) != PracticeFlow.StockSha256) restoreTemp = prefix + ".flow-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
                if (!File.Exists(restoreTemp)) WriteNew(restoreTemp, File.ReadAllBytes(backup));
                if (FileHash(restoreTemp) != PracticeFlow.StockSha256) throw new IOException("The flow.bin restore copy failed verification.");
                File.Replace(restoreTemp, flow, null);
                break;
            case PracticeFlow.StockSha256:
                break; // Staging stopped before the swap, or recovery already restored it.
            default:
                throw new IOException("system/flow.bin changed independently. The current file and backup were preserved.");
        }
        if (FileHash(flow) != PracticeFlow.StockSha256) throw new IOException("Stock flow.bin restoration could not be verified.");
        RemoveStageTemp(prefix + ".flow.tmp", PracticeFlow.GuardedSha256, id);
        RemoveStageTemp(prefix + ".flow-restore.tmp", PracticeFlow.StockSha256, id);
        Record(id, "Restored and verified stock flow.bin " + PracticeFlow.StockSha256 + ".");
    }

    // Windows returns the command line as a counted UNICODE_STRING in our
    // process, including when this x64 supervisor queries the x86 renderer.
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(SafeProcessHandle handle, int informationClass, IntPtr buffer, int size, out int used);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string command, out int count);
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr address);

    private static bool IsOurGame(Process proc, string folder, string id)
    {
        var imageName = proc.MainModule?.FileName;
        if (string.IsNullOrEmpty(imageName) || !string.Equals(Path.GetDirectoryName(imageName), folder, StringComparison.OrdinalIgnoreCase)) return false;
        NtQueryInformationProcess(proc.SafeHandle, 60, IntPtr.Zero, 0, out var size);
        if (size <= IntPtr.Size * 2 || size > 131072) return false;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(proc.SafeHandle, 60, buffer, size, out var used) != 0) return false;
            var length = (ushort)Marshal.ReadInt16(buffer);
            var content = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            var offset = content.ToInt64() - buffer.ToInt64();
            if (length % 2 != 0 || offset < IntPtr.Size * 2 || offset + length > used) return false;
            var command = Marshal.PtrToStringUni(content, length / 2);
            var args = CommandLineToArgvW(command, out var count);
            if (args == IntPtr.Zero) return false;
            try
            {
                for (var i = 1; i < count; ++i)
                    if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, i * IntPtr.Size)) == "-gasvrplay=" + id) return true;
                return false;
            }
            finally { LocalFree(args); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // desktop: the renderer has no VR renderer to configure; its device creation in a desktop session counts.
    private static bool Activated(string id, DateTime requested, bool desktop = false)
    {
        var log = Path.Combine(SessionFolder(id), "bridge.log");
        if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) < requested.AddSeconds(-2)) return false;
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var lines = new StreamReader(stream);
        while (lines.ReadLine() is { } line)
            if (desktop ? line.Contains("] D3D11 creation hr=00000000 ", StringComparison.Ordinal) && line.Contains(" desktop=1 ", StringComparison.Ordinal)
                        : line.Contains("native renderer configured:", StringComparison.Ordinal)) return true;
        return false;
    }

    // The launch step the renderer's log shows, reported when it changes (VrLaunchStatus).
    private static void ReportProgress(string id, ref (long Length, string Stage) last)
    {
        var log = new FileInfo(Path.Combine(SessionFolder(id), "bridge.log"));
        if (!log.Exists || log.Length == last.Length || last.Stage is VrLaunchStatus.Running or VrLaunchStatus.Failed) return;
        last.Length = log.Length;
        try
        {
            using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            var (stage, message) = VrLaunchStatus.FromBridgeLog(lines);
            if (stage == last.Stage) return;
            last.Stage = stage;
            VrLaunchStatus.Report(stage, message, id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static int WaitForGame(string folder, string id, DateTime requested, GameBuild build, bool desktop = false)
    {
        var watched = new Dictionary<int, Process>();
        Process? renderer = null;
        var clock = Stopwatch.StartNew();
        TimeSpan? exitedBeforeObservation = null;
        (long Length, string Stage) progress = (-1, "");
        try
        {
            while (true)
            {
                foreach (var name in new[] { "GRIDAutosport", "GRIDAutosport_avx" })
                    foreach (var found in Process.GetProcessesByName(name))
                    {
                        Process? proc = found;
                        try
                        {
                            if (watched.ContainsKey(proc.Id) || proc.StartTime.ToUniversalTime() < requested.AddSeconds(-2) || !IsOurGame(proc, folder, id)) continue;
                            watched.Add(proc.Id, proc);
                            if (name == build.ProcessName)
                            {
                                renderer = proc;
                                VrLaunchStatus.Report(desktop ? VrLaunchStatus.Running : VrLaunchStatus.Loading, desktop ? "GRID Autosport is running on the desktop." : "GRID Autosport is loading…", id);
                            }
                            Record(id, "Watching " + name + " PID " + proc.Id + ".");
                            proc = null; // The dictionary now owns its process handle.
                        }
                        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                        {
                            // A short-lived Steam wrapper may disappear during enumeration.
                        }
                        finally { proc?.Dispose(); }
                    }
                if (renderer is not null && !desktop) ReportProgress(id, ref progress);
                if (renderer is not null && watched.Values.All(p => p.HasExited))
                {
                    foreach (var p in watched.Values) Record(id, $"PID {p.Id} exited 0x{p.ExitCode:X8}.");
                    if (renderer.ExitCode == 0 && !Activated(id, requested, desktop))
                        throw new IOException("GRID Autosport exited without " + (desktop ? "starting its Practice / Race session" : "activating its VR renderer") + ". See " + SessionFolder(id));
                    return renderer.ExitCode;
                }
                if (renderer is null && watched.Values.All(p => p.HasExited) && File.Exists(Path.Combine(SessionFolder(id), "bridge.log")))
                {
                    exitedBeforeObservation ??= clock.Elapsed;
                    if (clock.Elapsed - exitedBeforeObservation.Value > TimeSpan.FromSeconds(3))
                        throw new IOException("The VR renderer started and exited before its exit code could be observed. See " + SessionFolder(id));
                }
                else exitedBeforeObservation = null;
                if (renderer is null && clock.Elapsed >= TimeSpan.FromSeconds(90))
                    throw new IOException((build.ThroughSteam ? "Steam did not start" : "Windows did not start") + " a matching GRID Autosport VR session within 90 seconds. Close any open game before recovery.");
                Thread.Sleep(200);
            }
        }
        finally { foreach (var p in watched.Values) p.Dispose(); }
    }
}
