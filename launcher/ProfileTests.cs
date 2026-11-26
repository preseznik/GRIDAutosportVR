namespace GRIDAutosportVR;

// Career profiles (Profiles.cs), ported from GRID2VR's ProfileTests on fixture folders: no game,
// Steam or real save is touched.
internal static class ProfileTests
{
    internal static void Run(string fixture, Action<bool, string> check)
    {
        var data = Path.Combine(fixture, "profiles-fixture");
        var source = Path.Combine(fixture, "steam-career-fixture");
        Directory.CreateDirectory(source);
        var sourceSave = Path.Combine(source, "savegame@profile0#QKRHMYXE");
        byte[] original = [0, 1, 2, 255, 99];
        File.WriteAllBytes(sourceSave, original);
        File.WriteAllBytes(Path.Combine(source, "savegame@profile0#QKIGMYYI.LKR"), [3, 4]);
        var store = new ProfileStore(data, () => { }, () => source, () => "13216522");
        var selection = Path.Combine(data, "profile-selection.json");
        void Reject(Action action, string what)
        {
            try { action(); check(false, "accepted: " + what); }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException or System.Text.Json.JsonException) { }
        }

        check(store.SelectedId() == ProfileStore.CurrentCareer && store.StorageRequest() is null, "current career by default, no request");
        check(store.List().Single().Info!.FileCount == 2 && !Directory.Exists(store.Root), "current career listed from Steam's folder");
        var fresh = store.Create("Driver1", CareerStart.Fresh);
        check(fresh.Kind == "fresh" && fresh.FileCount == 0 && fresh.LastSavedUtc == null && fresh.AccountId == "13216522", "fresh profile");
        check(store.SelectedId() == fresh.Id && store.List().Count == 2 && Directory.Exists(Path.Combine(store.Root, fresh.Id, "remote")), "fresh selected");
        var request = store.StorageRequest()!.Split('\n');
        check(request.Length == 5 && request[0] == ProfileStore.RequestHeader && request[1] == Path.Combine(store.Root, fresh.Id, "remote") &&
              request[2] == fresh.Id && request[3] == "13216522" && request[4] == "", "request for the bridge");

        foreach (var invalid in new[] { "", " ", "two words", "../outside", "x/y", "x\\y", "é", new string('A', 25), "driver1" })
            Reject(() => store.Create(invalid, CareerStart.Fresh), "name " + invalid);
        foreach (var invalid in new[] { "../outside", fresh.Id.ToUpperInvariant(), "not-a-guid", Guid.NewGuid().ToString("N") })
        {
            Reject(() => store.Select(invalid), "select " + invalid);
            Reject(() => store.Delete(invalid), "delete " + invalid);
        }
        check(store.SelectedId() == fresh.Id && store.List().Count == 2, "invalid requests change nothing");

        var copied = store.Create("Copy1", CareerStart.CopyCurrent);
        check(copied.Kind == "copied" && copied.FileCount == 2 && copied.Bytes == 7 && store.SelectedId() == copied.Id, "copied profile");
        var copiedFile = Path.Combine(store.Root, copied.Id, "remote", Path.GetFileName(sourceSave));
        File.WriteAllBytes(copiedFile, [66]);
        check(File.ReadAllBytes(sourceSave).SequenceEqual(original), "a profile never writes the Steam career");
        Reject(() => store.Delete(copied.Id), "delete the selected profile");
        Reject(() => store.Delete(ProfileStore.CurrentCareer), "delete the current career");
        store.Select(fresh.Id);
        store.Delete(copied.Id);
        check(!Directory.Exists(Path.Combine(store.Root, copied.Id)) && File.ReadAllBytes(sourceSave).SequenceEqual(original), "delete");

        // The bridge's saves stay with the profile; its own temporary files are not saves.
        File.WriteAllBytes(Path.Combine(store.Root, fresh.Id, "remote", "savegame@profile0#QKRHMYXE"), [8, 9]);
        File.WriteAllBytes(Path.Combine(store.Root, fresh.Id, "remote", ".gasvr-write-123-1.tmp"), [99]);
        store.Select(ProfileStore.CurrentCareer); store.Select(fresh.Id);
        check(store.Read(fresh.Id).FileCount == 1 && store.Read(fresh.Id).Bytes == 2, "saves kept across switches");
        Directory.CreateDirectory(Path.Combine(store.Root, fresh.Id, "remote", "sub"));
        Reject(() => store.Read(fresh.Id), "a folder inside remote");
        Directory.Delete(Path.Combine(store.Root, fresh.Id, "remote", "sub"));

        var valid = File.ReadAllBytes(selection);
        foreach (var bad in new[] { "{", "{\"schema\":2,\"id\":\"current\"}", "{\"schema\":1,\"id\":\"missing\"}" })
        {
            File.WriteAllText(selection, bad);
            Reject(() => store.SelectedId(), "selection " + bad);
            Reject(() => store.Create("MustNotCreate", CareerStart.Fresh), "create over a broken selection");
        }
        File.WriteAllBytes(selection, valid);

        var otherAccount = new ProfileStore(data, () => { }, () => source, () => "4242");
        Reject(() => otherAccount.SelectedId(), "another Steam account's profile");
        var blocked = new ProfileStore(data, () => throw new IOException("Game is open."), () => source, () => "13216522");
        Reject(() => blocked.Create("Busy", CareerStart.Fresh), "create while the game runs");
        Reject(() => blocked.Select(ProfileStore.CurrentCareer), "switch while the game runs");
        check(store.SelectedId() == fresh.Id, "blocked changes change nothing");

        // Career details: the load menu's label, and reads that cannot start (no save, no game folder).
        var nine = CareerSaves.Parse("PLAYER ONE - GRID Autosport Career Races:  9, Online Races: 0");
        check(nine.CareerRaces == 9 && nine.OnlineRaces == 0 && nine.Text == "9 career races completed · 0 online", "career label");
        check(CareerSaves.Parse("PLAYER ONE - GRID Autosport Career Races:  1, Online Races: 12").Text == "1 career race completed · 12 online", "one race");
        check(CareerSaves.Parse("JOUEUR UN - GRID Autosport Courses : 3").Text == "JOUEUR UN - GRID Autosport Courses : 3", "unknown label shown as is");
        var empty = Directory.CreateDirectory(Path.Combine(fixture, "career-empty")).FullName;
        var reads = CareerSaves.Read([empty, source], null);
        check(reads[empty] is (null, null) && reads[source] is (null, { } noGame) && noGame.Contains("GRID Autosport folder"),
              "career reads without a game");

        check(ProfileService.RecentAccount("\"users\"\n{\n\t\"76561197973482250\"\n\t{\n\t\t\"AccountName\"\t\t\"x\"\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n}") == "13216522" &&
              ProfileService.RecentAccount("\"users\" { \"76561197973482250\" { \"MostRecent\" \"0\" } }") is null, "Steam's most recent login");
        store.Select(ProfileStore.CurrentCareer);
    }
}
