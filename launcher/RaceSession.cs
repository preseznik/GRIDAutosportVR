using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace GRIDAutosportVR;

// Launcher Practice / Race (tools/practice_flow.py is the research behind it). The game's
// -benchmarkAttract mode loads car, track, laps and grid from a benchmark-style XML; the guarded
// flow sends its attract_mode_chosen decisions to the normal player start, so the player drives.
internal sealed record CarItem(string Id, string Label, string Class, IReadOnlyList<string> Liveries)
{
    public override string ToString() => Label + " · " + Class;
}

internal sealed record TrackItem(string Id, string Country, string Location, string Route, string Label)
{
    public override string ToString() => Label;
}

internal sealed record ContentCatalog(IReadOnlyList<CarItem> Cars, IReadOnlyList<TrackItem> Tracks)
{
    public static readonly ContentCatalog Empty = new([], []);
}

internal static partial class RaceSession
{
    public const string DefaultCar = "sls", DefaultTrack = "cities/san_francisco/route_0";
    public const int MaxLaps = 20, MaxOpponents = 11;
    public static readonly string[] Modes = ["Normal", "Practice", "Race"];
    public static readonly string[] OpponentChoices = ["same", "class", "mixed"];

    [GeneratedRegex(@"\A[a-z0-9_]+\z")] private static partial Regex Token();
    [GeneratedRegex(@"\Aroute_\d+\z")] private static partial Regex RouteFolder();

    // The shipped example_benchmark.xml grid: twelve cars with these driver presets.
    private static readonly (string Driver, string Driver0)[] Drivers =
    [
        ("AmericanaI_1_1", "AmericanaI_1_0"), ("AmericanaM_1_1", "AmericanaM_1_0"), ("ProfF_1_1", "ProfF_1_0"), ("AllN_1_1", "AllN_1_0"),
        ("MoneyH_1_1", "MoneyH_1_0"), ("ShowO_1_1", "ShowO_1_0"), ("UnderO_1_1", "UnderO_1_0"), ("MoneyJ_1_1", "MoneyJ_1_0"),
        ("AllB_1_1", "AllB_1_0"), ("UnderBBB_1_1", "UnderBBB_1_0"), ("UnderM_1_1", "UnderM_1_0"), ("AllH_1_1", "AllH_1_0"),
    ];

    // Launcher names and disciplines for the installed car folders (cars/models/<id>); a folder
    // without an entry is listed by its id in Other. Disciplines only group Race opponents.
    private static readonly Dictionary<string, (string Label, string Class)> CarNames = new()
    {
        ["177"] = ("Aston Martin V12 Vantage #177", "Endurance"), ["240"] = ("Nissan 240SX (Yoshihara)", "Tuner"),
        ["320"] = ("BMW 320 TC", "Touring"), ["35m"] = ("Nissan 350Z", "Tuner"), ["370"] = ("Nissan 370Z", "Tuner"),
        ["408"] = ("Peugeot 408", "Touring"), ["787"] = ("Mazda 787B", "Endurance"), ["a4p"] = ("Alfa Romeo 4C", "Street"),
        ["a8c"] = ("Alfa Romeo 8C Competizione", "Street"), ["at5"] = ("Ariel Atom 3", "Tuner"), ["av8"] = ("Ariel Atom V8", "Tuner"),
        ["b1m"] = ("BMW 135i", "Street"), ["bm3"] = ("BMW M3 GT2", "Endurance"), ["bom"] = ("Ford Mustang Boss 302 (modified)", "Tuner"),
        ["bos"] = ("Ford Mustang Boss 302", "Street"), ["brz"] = ("Subaru BRZ", "Tuner"), ["bug"] = ("Bugatti Veyron", "Street"),
        ["c63"] = ("Mercedes-Benz C63 AMG", "Street"), ["cap"] = ("Caparo T1", "Street"), ["cat"] = ("Caterham-Lola SP/300.R", "Endurance"),
        ["cha"] = ("Dodge Challenger SRT8", "Street"), ["chv"] = ("Chevrolet Sonic", "Street"), ["cit"] = ("Honda Civic BTCC", "Touring"),
        ["civ"] = ("Honda Civic (5Zigen)", "Tuner"), ["crt"] = ("Dodge Charger R/T 1970", "Street"), ["crz"] = ("Chevrolet Cruze", "Touring"),
        ["csm"] = ("Chevrolet Camaro (modified)", "Tuner"), ["css"] = ("Chevrolet Camaro SS", "Street"), ["df3"] = ("Dallara F312", "Open Wheel"),
        ["e30"] = ("BMW M3 E30", "Touring"), ["evx"] = ("Mitsubishi Lancer Evolution X", "Tuner"), ["foc"] = ("Ford Focus ST", "Street"),
        ["fot"] = ("Ford Focus TC", "Touring"), ["fur"] = ("Mazda Furai", "Endurance"), ["g40"] = ("Ford GT40", "Endurance"),
        ["gol"] = ("Volkswagen Golf", "Street"), ["gt5"] = ("Nismo GT500", "Endurance"), ["hsv"] = ("Honda HSV-010", "Endurance"),
        ["ind"] = ("Dallara IndyCar", "Open Wheel"), ["jcc"] = ("Mini Challenge", "Touring"), ["jcw"] = ("Mini John Cooper Works", "Street"),
        ["koe"] = ("Koenigsegg Agera", "Street"), ["ktm"] = ("KTM X-Bow", "Tuner"), ["l05"] = ("GP2 Dallara", "Open Wheel"),
        ["l12"] = ("Lola B12/80", "Endurance"), ["ldi"] = ("Lancia Delta Integrale", "Tuner"), ["mf1"] = ("McLaren F1 GT", "Endurance"),
        ["min"] = ("Mini Miglia 1962", "Touring"), ["mp1"] = ("McLaren P1", "Street"), ["mp4"] = ("McLaren MP4-12C", "Street"),
        ["mpg"] = ("McLaren MP4-12C GT3", "Endurance"), ["nsx"] = ("Honda NSX", "Street"), ["pag"] = ("Pagani Huayra", "Street"),
        ["ply"] = ("Plymouth Cuda", "Street"), ["r3b"] = ("Nissan Skyline R32", "Tuner"), ["r8l"] = ("Audi R8 LMS Ultra", "Endurance"),
        ["rs5"] = ("Audi RS 5", "Street"), ["rx1"] = ("Mazda RX-7 (stock)", "Tuner"), ["rx2"] = ("Mazda RX-7 (super)", "Tuner"),
        ["rx7"] = ("Mazda RX-7 (drift)", "Tuner"), ["s15"] = ("Nismo 200SX", "Tuner"), ["s1m"] = ("Nissan Silvia S15 (drift)", "Tuner"),
        ["s20"] = ("Honda S2000", "Tuner"), ["s2m"] = ("Honda S2000 (modified)", "Tuner"), ["sdc"] = ("Shelby Cobra Daytona Coupe", "Endurance"),
        ["sky"] = ("Nissan Skyline R34 Nismo", "Tuner"), ["sl6"] = ("Mercedes-Benz SL65 AMG", "Street"), ["sls"] = ("Mercedes-Benz SLS AMG GT3", "Endurance"),
        ["srs"] = ("Ford Sierra Cosworth", "Touring"), ["ssa"] = ("Audi RS 5 Superstars", "Touring"), ["ssm"] = ("Mercedes-Benz C63 AMG Superstars", "Touring"),
        ["stk"] = ("Jupiter Eagleray", "Tuner"), ["teg"] = ("Honda Integra Type R", "Touring"), ["utf"] = ("Ford Falcon Ute", "Touring"),
        ["uth"] = ("Holden Commodore Ute", "Touring"), ["v8f"] = ("Ford Falcon V8 Supercar", "Touring"), ["v8h"] = ("Holden Commodore V8 Supercar", "Touring"),
        ["van"] = ("Aston Martin V12 Vantage", "Street"), ["vip"] = ("SRT Viper GTS-R", "Endurance"), ["wrx"] = ("Subaru Impreza WRX", "Tuner"),
        ["xkr"] = ("Jaguar XKR GT", "Endurance"), ["zig"] = ("Aston Martin V12 Vantage GT3", "Endurance"), ["zon"] = ("Pagani Zonda R", "Street"),
    };

    private static readonly Dictionary<string, string> LocationNames = new()
    {
        ["austin"] = "Circuit of the Americas", ["bathurst"] = "Mount Panorama", ["hockenheim"] = "Hockenheimring",
        ["mont_tremblant"] = "Mont-Tremblant", ["okutama_gp"] = "Okutama", ["spa_francorchamps"] = "Spa-Francorchamps",
        ["washington"] = "Washington, D.C.",
    };

    private static string Title(string folder) => LocationNames.TryGetValue(folder, out var name) ? name
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(folder.Replace('_', ' '));

    private static IEnumerable<DirectoryInfo> Folders(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0 && Token().IsMatch(d.Name)).OrderBy(d => d.Name, StringComparer.Ordinal)
            : [];

    // Installed cars (with cameras.xml and a user livery) and complete track routes.
    public static ContentCatalog Load(string gameFolder)
    {
        var cars = new List<CarItem>();
        foreach (var folder in Folders(Path.Combine(gameFolder, "cars", "models")))
        {
            if (!File.Exists(Path.Combine(folder.FullName, "cameras.xml"))) continue;
            var liveries = Folders(folder.FullName).Where(d => d.Name.StartsWith("livery_", StringComparison.Ordinal) && d.Name.Length > 7)
                .Select(d => d.Name[7..]).ToArray();
            if (!liveries.Contains("user")) continue;
            var (label, group) = CarNames.TryGetValue(folder.Name, out var known) ? known : (folder.Name.ToUpperInvariant(), "Other");
            cars.Add(new(folder.Name, label, group, liveries));
        }
        var tracks = new List<TrackItem>();
        foreach (var country in new[] { "circuits", "cities" })
            foreach (var location in Folders(Path.Combine(gameFolder, "tracks", "locations", country)))
                foreach (var route in Folders(location.FullName).Where(d => RouteFolder().IsMatch(d.Name)).OrderBy(d => int.Parse(d.Name[6..], CultureInfo.InvariantCulture)))
                {
                    if (!new[] { "track.jpk", "route_objects.pssg", "progress_track.xml" }.All(f => File.Exists(Path.Combine(route.FullName, f)))) continue;
                    tracks.Add(new($"{country}/{location.Name}/{route.Name}", country, location.Name, route.Name,
                        $"{Title(location.Name)} — route {route.Name[6..]}{(country == "cities" ? " (street)" : "")}"));
                }
        return new(cars.OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase).ToArray(), tracks);
    }

    // The benchmark-style session file: the player's car on pole with the first driver preset,
    // then 0-11 opponents with the next presets (Practice is solo).
    public static byte[] SessionXml(Choices choices, ContentCatalog catalog, int seed)
    {
        if (choices.LaunchMode is not ("Practice" or "Race")) throw new ArgumentException("Only Practice and Race use a session file.");
        var car = catalog.Cars.FirstOrDefault(c => c.Id == choices.Car) ?? throw new IOException($"The selected car ({choices.Car}) is not installed.");
        var track = catalog.Tracks.FirstOrDefault(t => t.Id == choices.Track) ?? throw new IOException($"The selected track ({choices.Track}) is not installed.");
        if (choices.Laps is < 1 or > MaxLaps) throw new ArgumentException($"Laps must be 1 to {MaxLaps}.");
        if (choices.Opponents is < 1 or > MaxOpponents) throw new ArgumentException($"Opponents must be 1 to {MaxOpponents}.");
        if (!OpponentChoices.Contains(choices.OpponentCars)) throw new ArgumentException("Unknown opponent car choice.");
        var opponents = choices.LaunchMode == "Race" ? choices.Opponents : 0;
        var grid = new List<(string Car, string Livery)> { (car.Id, "user") };
        var random = new Random(seed);
        var pool = choices.OpponentCars switch
        {
            "mixed" => catalog.Cars.Where(c => c.Id != car.Id).ToList(),
            "class" => catalog.Cars.Where(c => c.Id != car.Id && c.Class == car.Class && c.Class != "Other").ToList(),
            _ => [],
        };
        var shuffled = pool.OrderBy(_ => random.Next()).ToList();
        // Same car (or an empty pool) cycles the car's own liveries so opponents stay distinguishable.
        var liveries = car.Liveries.Where(l => l != "user").DefaultIfEmpty("user").ToArray();
        for (var i = 0; i < opponents; ++i)
            grid.Add(shuffled.Count > 0 ? (shuffled[i % shuffled.Count].Id, "user") : (car.Id, liveries[i % liveries.Length]));

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("config");
            xml.WriteAttributeString("outputfilename", "GRIDAutosportVR_Session");
            xml.WriteAttributeString("hardwaresettings", "hardware_settings_config.xml");
            xml.WriteAttributeString("cycle_camera", "false");
            xml.WriteAttributeString("infinite_loop", "false");
            xml.WriteAttributeString("skipreplays", "true");
            xml.WriteStartElement("track");
            xml.WriteAttributeString("country", track.Country);
            xml.WriteAttributeString("name", track.Location);
            xml.WriteAttributeString("route", track.Route);
            xml.WriteAttributeString("lighting", "day");
            xml.WriteAttributeString("weather", "dry");
            xml.WriteAttributeString("laps", choices.Laps.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("number_cars", grid.Count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < grid.Count; ++i)
            {
                xml.WriteStartElement("car");
                xml.WriteAttributeString("name", grid[i].Car);
                xml.WriteAttributeString("livery", grid[i].Livery);
                xml.WriteAttributeString("ai_driver", Drivers[i].Driver);
                xml.WriteAttributeString("ai_driver0", Drivers[i].Driver0);
                xml.WriteAttributeString("grid", i.ToString(CultureInfo.InvariantCulture));
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        return buffer.ToArray();
    }

    public static string Summary(Choices choices, ContentCatalog catalog)
    {
        if (choices.LaunchMode == "Normal") return "Normal: choose your car and event in the game's menus.";
        var car = catalog.Cars.FirstOrDefault(c => c.Id == choices.Car)?.Label ?? choices.Car;
        var track = catalog.Tracks.FirstOrDefault(t => t.Id == choices.Track)?.Label ?? choices.Track;
        var laps = choices.Laps == 1 ? "1 lap" : $"{choices.Laps} laps";
        if (choices.LaunchMode == "Practice") return $"Practice: {car} at {track}, {laps}, solo.";
        var rivals = choices.OpponentCars switch { "mixed" => "mixed cars", "class" => "same discipline", _ => "same car" };
        return $"Race: {car} at {track}, {laps}, {choices.Opponents} AI {(choices.Opponents == 1 ? "opponent" : "opponents")} ({rivals}).";
    }
}

// The guarded system/flow.bin for player-driven -benchmarkAttract sessions, made on the player's
// PC from their own stock file. The EGO encoder's output for tools/practice_flow.py's three
// redirected decisions differs from the stock file only in these bytes (a swapped string pair and
// its references); both the input and the result are pinned, so any other file is refused.
internal static class PracticeFlow
{
    public const string StockSha256 = "b5f326eb2ad9ff1307c6bbf05ace2af99f21fcb9908704d66bd209882c30f20b";
    public const string GuardedSha256 = "fd8383f6b6332ec400f9035c1a89006d037ebd7e249c42f758f1fa005595ac88";
    public const int Length = 1021108;

    private static readonly (int Offset, string Before, string After)[] Patch =
    [
        (0x011E0B, "313837393536363035003639363938373739", "363936393837373900313837393536363035"),
        (0x0213C8, "fd", "fc"),
        (0x0D9C48, "72", "71"),
        (0x0D9C50, "71", "72"),
        (0x0D9D88, "7f", "71"),
        (0x0D9D98, "72", "71"),
        (0x0D9E10, "72", "71"),
        (0x0D9F28, "91", "93"),
        (0x0DA078, "71", "72"),
    ];

    internal static IReadOnlyList<(int Offset, string Before, string After)> Changes => Patch;

    public static byte[] Guarded(byte[] stock)
    {
        if (stock.Length != Length || Convert.ToHexStringLower(SHA256.HashData(stock)) != StockSha256)
            throw new IOException("system/flow.bin is not the supported stock file. Practice and Race need the original Steam file (verify the game files in Steam).");
        var result = (byte[])stock.Clone();
        foreach (var (offset, before, after) in Patch)
        {
            var from = Convert.FromHexString(before);
            if (!result.AsSpan(offset, from.Length).SequenceEqual(from)) throw new IOException("The flow patch does not match system/flow.bin.");
            Convert.FromHexString(after).CopyTo(result, offset);
        }
        if (Convert.ToHexStringLower(SHA256.HashData(result)) != GuardedSha256) throw new IOException("The Practice/Race flow verification failed.");
        return result;
    }
}
