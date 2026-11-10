using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GRIDAutosportVR;

// Steam client and library discovery (from DiRT3VR's launcher/Core.vb, via the VB launcher).
internal static partial class SteamLibrary
{
    private static List<string> ClientRoots()
    {
        var roots = new List<string>();
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (key?.GetValue("SteamPath") is string path && !string.IsNullOrWhiteSpace(path)) roots.Add(path.Replace('/', '\\'));
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var install = machine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (install?.GetValue("InstallPath") is string installPath && !string.IsNullOrWhiteSpace(installPath)) roots.Add(installPath);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Discovery is optional; the default install and manual selection remain available.
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return roots;
    }

    public static string? SteamClient()
    {
        foreach (var root in ClientRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(root)) continue;
            var executable = Path.Combine(root, "steam.exe");
            if (!File.Exists(executable)) continue;
            try
            {
                var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable);
                if (string.Equals(version.OriginalFilename, "steam.exe", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(version.CompanyName, "Valve Corporation", StringComparison.OrdinalIgnoreCase)) return executable;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // Continue with the next registered Steam installation.
            }
        }
        return null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();

    public static IEnumerable<string> SteamLibraries()
    {
        var roots = ClientRoots();
        foreach (var root in roots.ToArray())
        {
            var libraries = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(libraries))
                    foreach (Match match in LibraryPath().Matches(File.ReadAllText(libraries))) roots.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A protected library listing must not prevent selecting the game manually.
            }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
