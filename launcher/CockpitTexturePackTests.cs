// Self-tests for the optional cockpit texture pack (CockpitTexturePack): catalog checks, install
// from a local ZIP into the test data folder, tamper detection, removal and the game argument.
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GRIDAutosportVR;

internal static class CockpitTexturePackTests
{
    public static void Run(string data, Action<bool, string?> check)
    {
        const string version = "9.9.9-test.1";
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["abc.dds"] = Encoding.ASCII.GetBytes("not a real texture, test only"),
            ["catalog.txt"] = Encoding.ASCII.GetBytes("abc " + new string('0', 64) + " " + new string('1', 64) + "\n"),
            ["README.txt"] = Encoding.ASCII.GetBytes("test pack\n"),
        };
        var zip = Path.Combine(data, "pack-test.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var (name, bytes) in files)
            {
                using var entry = archive.CreateEntry($"cockpit-textures/{version}/{name}").Open();
                entry.Write(bytes);
            }
        string Json(long zipBytes, string zipHash, string asset) => JsonSerializer.Serialize(new
        {
            schema = 1, version, asset, zipBytes, zipSha256 = zipHash, installedBytes = files.Values.Sum(b => (long)b.Length), cars = 1,
            files = files.ToDictionary(f => f.Key, f => Convert.ToHexStringLower(SHA256.HashData(f.Value))),
        });
        var zipHash = CockpitTexturePack.Sha256(zip);
        var asset = $"GRIDAutosportVR-CockpitTextures-{version}.zip";
        var catalog = CockpitPackCatalog.Parse(Json(new FileInfo(zip).Length, zipHash, asset));
        check(catalog.Folder == Path.Combine(data, "addons", "cockpit-textures", version), catalog.Folder);
        check(catalog.Download.AbsoluteUri == Branding.Repository + "/releases/download/cockpit-textures-9.9.9-test.1/" + asset, catalog.Download.AbsoluteUri);
        check(Throws(() => CockpitPackCatalog.Parse(Json(1, zipHash, "other.zip"))), "asset name");
        check(Throws(() => CockpitPackCatalog.Parse(Json(1, "abc", asset))), "zip hash");
        check(Throws(() => CockpitPackCatalog.Parse(Json(1, zipHash, asset).Replace("abc.dds", "../x.dds"))), "file name");

        check(!CockpitTexturePack.Installed(catalog), "not installed yet");
        using var http = new HttpClient();
        var progress = new Progress<TexturePackProgress>(_ => { });
        CockpitTexturePack.Install(catalog, http, progress, CancellationToken.None, zip).GetAwaiter().GetResult();
        check(CockpitTexturePack.Installed(catalog), "installed");
        check(File.Exists(zip) && !Directory.EnumerateFiles(Path.GetDirectoryName(catalog.Folder)!).Any(), "no download or staging left behind");
        check(VrPlaySession.CockpitPack(new Choices(NewCockpitTextures: false), out _) is null, "choice off");
        File.AppendAllText(Path.Combine(catalog.Folder, "README.txt"), "changed");
        check(!CockpitTexturePack.Installed(catalog), "a changed file fails verification");
        CockpitTexturePack.Install(catalog, http, progress, CancellationToken.None, zip).GetAwaiter().GetResult();
        check(CockpitTexturePack.Installed(catalog), "reinstalled over a changed pack");
        File.WriteAllText(Path.Combine(catalog.Folder, "extra.txt"), "x");
        check(!CockpitTexturePack.Installed(catalog), "an extra file fails verification");
        check(Throws(() => CockpitTexturePack.Remove(catalog)), "removal keeps a folder with foreign files");
        File.Delete(Path.Combine(catalog.Folder, "extra.txt"));
        CockpitTexturePack.Remove(catalog);
        check(!Directory.Exists(Path.Combine(data, "addons", "cockpit-textures")), "removed");

        var wrong = CockpitPackCatalog.Parse(Json(new FileInfo(zip).Length, new string('a', 64), asset));
        check(Throws(() => CockpitTexturePack.Install(wrong, http, progress, CancellationToken.None, zip).GetAwaiter().GetResult()), "hash mismatch refused");
        check(!Directory.Exists(wrong.Folder), "nothing installed after a mismatch");

        var arguments = VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", new Choices(), null, cockpitPack: "1.0.0");
        check(arguments.Contains("-gasvrcockpittextures=replace:1.0.0"), "game argument");
        check(!VrPlaySession.SteamArguments("0123456789abcdef0123456789abcdef", new Choices(), null).Any(a => a.StartsWith("-gasvrcockpittextures")), "no argument without a pack");
        if (CockpitTexturePack.Current is { } current)
            check(current.Asset == $"GRIDAutosportVR-CockpitTextures-{current.Version}.zip" && current.Files.ContainsKey("catalog.txt"), "embedded catalog");
        File.Delete(zip);
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return true; }
    }
}
