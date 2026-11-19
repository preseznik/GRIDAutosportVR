using System.Security.Cryptography;

namespace GRIDAutosportVR;

// The GRID Autosport builds GRID Autosport VR supports (bridge/game_build.h has the bridge's side).
// Steam: the 2019 update, which runs GRIDAutosport_avx.exe through Steam. Classic 2014:
// GRIDAutosport.exe 1.0.103.1840 linked 2014-11-03 (17,478,656 bytes), the same game version
// built earlier; it starts directly, in any folder, with -noavx (otherwise, on an AVX processor, it
// hands over to a GRIDAutosport_avx.exe beside it). A VR session stages the build's executable
// with its d3d11.dll import renamed to gasxr.dll, and restores it afterwards.
internal sealed record GameBuild(string Id, string Label, string Executable, string StockSha256, string VrSha256, int ImportNameOffset,
    bool ThroughSteam, string BackupName, string[] Arguments)
{
    public string ProcessName => Path.GetFileNameWithoutExtension(Executable);
}

internal static class GameBuilds
{
    public static readonly GameBuild Steam = new("steam", "Steam version", "GRIDAutosport_avx.exe",
        "c88929ba8ed5327799fb6f57dc7889b2f5e8baec47b92832038af913ff54fc48", "9d3e8f9a36571e20707187f1da86967201ab74e72f9f72e8ec446f01dbe59856",
        0xE0C23C, true, "original-avx.exe", []);
    public static readonly GameBuild Classic2014 = new("2014", "2014 version (GRIDAutosport.exe)", "GRIDAutosport.exe",
        "23d863f970a454ba52d66f7c22a296428bd0047781ecede1baa9f0d9211616f9", "e4e3983a33b2fa1ed277d48dc61f2672f6e2460600612aced1672a0161afce9e",
        0xF3CE1A, false, "original-game.exe", ["-noavx"]);
    public static readonly GameBuild[] All = [Steam, Classic2014];

    // Leases written before builds were recorded are Steam sessions.
    public static GameBuild ById(string? id) => All.FirstOrDefault(b => b.Id == id) ?? Steam;

    // The supported build installed in a folder (its stock or staged VR executable), or null.
    public static GameBuild? Detect(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        foreach (var build in All)
        {
            var hash = CachedHash(Path.Combine(folder, build.Executable));
            if (hash == build.StockSha256 || hash == build.VrSha256) return build;
        }
        return null;
    }

    // Executables are hashed once per size and write time: the launcher asks often.
    private static readonly Dictionary<string, (long Length, DateTime Written, string Hash)> hashes = new(StringComparer.OrdinalIgnoreCase);
    internal static string? CachedHash(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            lock (hashes)
                if (hashes.TryGetValue(file.FullName, out var known) && known.Length == file.Length && known.Written == file.LastWriteTimeUtc) return known.Hash;
            string hash;
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            lock (hashes) hashes[file.FullName] = (file.Length, file.LastWriteTimeUtc, hash);
            return hash;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
