// OpenXR runtime choice (Settings → Paths and runtime → VR runtime), as GRID2VR
// (tools/OpenXR-Runtime.ps1 there): Default uses this PC's 32-bit OpenXR runtime; VDXR and SteamVR
// use that runtime's own manifest for the headset check and the game session only. The system's
// default runtime is never changed. The game is started by Steam, which does not pass the
// launcher's environment on, so the choice goes to the session folder (openxr-runtime.txt) and the
// VR renderer selects it in the game process (bridge/xr_bridge.cpp).
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace GRIDAutosportVR;

internal static class OpenXrRuntimes
{
    public const string Default = "Default", Vdxr = "VDXR", SteamVr = "SteamVR";
    public static readonly string[] Choices = [Default, Vdxr, SteamVr];
    public const string SessionFile = "openxr-runtime.txt";
    // How the launcher shows Default, as GRID2VR.
    public const string DefaultLabel = "OpenXR (system Default)";
    public static bool Valid(string choice) => Choices.Contains(choice);

    // The manifest a choice uses, or null for Default (the loader then finds the system runtime).
    // Release checks only (tools/release_check.py): the session supervisor may be given a stand-in
    // VDXR manifest, named as Virtual Desktop's, so a launch through Steam runs on xr-sim. The
    // launcher never sets it; the VR renderer accepts only a manifest with that file name.
    internal const string TestVdxrManifest = "GASVR_TEST_VDXR_MANIFEST";
    private static string? TestVdxr => Environment.GetEnvironmentVariable(TestVdxrManifest) is { Length: > 0 } path &&
        Path.IsPathFullyQualified(path) && string.Equals(Path.GetFileName(path), "virtualdesktop-openxr-32.json", StringComparison.OrdinalIgnoreCase) ? path : null;

    public static string? Manifest(string choice) => choice switch
    {
        Vdxr when TestVdxr is { } test => test,
        Vdxr => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer", "OpenXR", "virtualdesktop-openxr-32.json"),
        SteamVr => SteamLibrary.SteamLibraries().Select(root => Path.Combine(root, "steamapps", "common", "SteamVR", "steamxr_win32.json"))
                       .FirstOrDefault(File.Exists),
        _ => null,
    };

    // The 32-bit system runtime (what Default uses), for display.
    public static string? SystemManifest()
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = registry.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
            return key?.GetValue("ActiveRuntime") is string path ? Environment.ExpandEnvironmentVariables(path.Trim()) : null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    // Why a choice cannot be used, or null when it can.
    public static string? Problem(string choice)
    {
        if (!Valid(choice)) return "Choose OpenXR (system Default), VDXR or SteamVR as the VR runtime.";
        if (choice == Default) return null;
        var manifest = Manifest(choice);
        if (manifest is null || !File.Exists(manifest))
            return choice == Vdxr ? "VDXR is not installed: Virtual Desktop Streamer's 32-bit OpenXR runtime was not found. Install Virtual Desktop Streamer or choose another VR runtime."
                                  : "SteamVR is not installed in a Steam library. Install SteamVR or choose another VR runtime.";
        try
        {
            var file = new FileInfo(manifest);
            if (file.Length > 65536 || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return $"The {choice} runtime manifest is not usable: {manifest}";
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            if (root.GetProperty("file_format_version").GetString() != "1.0.0") return $"The {choice} runtime manifest has an unknown format: {manifest}";
            var library = root.GetProperty("runtime").GetProperty("library_path").GetString() ?? "";
            if (!Path.IsPathRooted(library)) library = Path.Combine(file.DirectoryName!, library);
            if (!File.Exists(library)) return $"The {choice} runtime is incomplete; its library is missing: {library}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return $"The {choice} runtime manifest could not be read: {error.Message}"; }
        return null;
    }

    // A short description for the launcher.
    public static string Describe(string choice) => choice switch
    {
        Default => SystemManifest() is { } system ? $"This PC's 32-bit OpenXR runtime: {Path.GetFileName(system)}." : "No 32-bit OpenXR runtime is set as this PC's default.",
        _ => Problem(choice) ?? $"{choice}: {Manifest(choice)}",
    };

    // The headset check (probe) runs with the chosen runtime.
    public static void Apply(ProcessStartInfo start, string choice)
    {
        if (Manifest(choice) is { } manifest && Problem(choice) is null) start.Environment["XR_RUNTIME_JSON"] = manifest;
    }

    // The game session's choice for the VR renderer; nothing for Default.
    public static void WriteSessionChoice(string sessionFolder, string choice)
    {
        var path = Path.Combine(sessionFolder, SessionFile);
        if (choice == Default) { if (File.Exists(path)) File.Delete(path); return; }
        if (Problem(choice) is { } problem) throw new IOException(problem);
        File.WriteAllText(path, choice + "\n" + Manifest(choice) + "\n", new UTF8Encoding(false));
    }
}
