using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace GRIDAutosportVR;

// Career profiles, ported from GRID2VR's Profiles.cs: separate careers the launcher keeps under
// %LOCALAPPDATA%\GRIDAutosportVR\profiles\<id>\remote. While one is selected, every launch through the
// launcher (VR, and the desktop Launch, which then runs through the launcher too) has the VR renderer
// keep the game's Steam Cloud saves (ISteamRemoteStorage) in that folder instead (bridge
// profile_storage.cpp). "Current game career" is the game's normal Steam save, never written here.
internal sealed record ProfileInfo(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("createdUtc")] DateTime CreatedUtc)
{
    [JsonPropertyName("accountId")] public string? AccountId { get; init; }
    [JsonIgnore] public int FileCount { get; init; }
    [JsonIgnore] public long Bytes { get; init; }
    [JsonIgnore] public DateTime? LastSavedUtc { get; init; }
    [JsonIgnore] public string Status { get; init; } = "";
}

internal sealed record ProfileEntry(string Id, ProfileInfo? Info, string? Error);
internal enum CareerStart { Fresh, CopyCurrent }

internal sealed class ProfileStore(string dataRoot, Action requireClosed, Func<string?> currentSaveRoot, Func<string?>? currentAccountId = null)
{
    internal const string CurrentCareer = "current";
    internal const int MaxFiles = 64;
    internal const long MaxBytes = 64L * 1024 * 1024;
    // The bridge's profile-storage.request, written into a session folder for a managed profile.
    internal const string RequestHeader = "GASVR_PROFILE_STORAGE_V1";
    public string Root { get; } = Path.Combine(Path.GetFullPath(dataRoot), "profiles");
    readonly string selection = Path.Combine(Path.GetFullPath(dataRoot), "profile-selection.json");
    sealed record Selection([property: JsonPropertyName("schema")] int Schema, [property: JsonPropertyName("id")] string Id);
    sealed record SaveFile(string Path, string Name, long Bytes, DateTime LastWriteUtc);

    internal static bool IsValidNewName(string? name) => name is { Length: >= 1 and <= 24 } &&
        name.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');

    static void ValidId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var guid) || guid.ToString("N") != id)
            throw new InvalidDataException("Invalid profile identifier.");
    }

    internal static bool ValidAccountId(string? id) => id is { Length: >= 1 and <= 10 } && id.All(c => c is >= '0' and <= '9') &&
        uint.TryParse(id, out var account) && account > 0 && account.ToString(System.Globalization.CultureInfo.InvariantCulture) == id;

    string? AccountId()
    {
        var id = currentAccountId?.Invoke();
        if (id != null && !ValidAccountId(id)) throw new InvalidDataException("The active Steam account identifier is invalid.");
        return id;
    }

    internal static void NoLinks(string path)
    {
        for (string? item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(item); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Profile storage and source saves cannot use linked files or folders.");
            if ((attributes & FileAttributes.Directory) == 0)
            {
                using var handle = File.OpenHandle(item, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!GetFileInformationByHandle(handle, out var information))
                    throw new IOException("Could not inspect profile file ownership.", Marshal.GetLastWin32Error());
                if (information.NumberOfLinks != 1)
                    throw new IOException("Profile storage and source saves cannot use hard-linked files.");
            }
        }
    }

    internal string ProfileRoot(string id)
    {
        ValidId(id);
        var path = Path.Combine(Root, id);
        NoLinks(path);
        return path;
    }

    static T ReadJson<T>(string path)
    {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 8192) throw new InvalidDataException("Profile metadata is too large.");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Profile metadata is empty.");
    }

    static void NewJson<T>(string path, T value)
    {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value);
        stream.Flush(true);
    }

    static void AtomicJson<T>(string path, T value)
    {
        NoLinks(path);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        NoLinks(parent);
        var temporary = Path.Combine(parent, ".profile-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { NewJson(temporary, value); NoLinks(path); File.Move(temporary, path, true); }
        finally { NoLinks(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // The save files of a remote folder: no subfolders, at most 64 files and 64 MiB. The bridge's own
    // temporary files (.gasvr-write-*.tmp) are not saves.
    static IReadOnlyList<SaveFile> ScanRemote(string path)
    {
        NoLinks(path);
        if (!Directory.Exists(path)) throw new IOException("This profile's save folder is missing.");
        var files = new List<SaveFile>();
        long bytes = 0;
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            NoLinks(child);
            var name = Path.GetFileName(child);
            if ((File.GetAttributes(child) & FileAttributes.Directory) != 0 || !ValidSaveName(name))
                throw new InvalidDataException("The save folder contains an unsupported file or folder.");
            if (Temporary(name)) continue;
            var file = new FileInfo(child);
            bytes = checked(bytes + file.Length);
            if (files.Count >= MaxFiles || bytes > MaxBytes)
                throw new InvalidDataException("Profile saves exceed the 64-file / 64 MiB limit.");
            files.Add(new SaveFile(child, name, file.Length, file.LastWriteTimeUtc));
        }
        return files;
    }

    internal static bool Temporary(string name) => name.StartsWith(".gasvr-write-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal);

    internal static bool ValidSaveName(string name) => name.Length is >= 1 and <= 180 &&
        name != "." && name != ".." && !name.EndsWith('.') && !name.EndsWith(' ') &&
        !name.Any(c => c < 32 || c > 126 || "<>:\"/\\|?*".Contains(c)) &&
        !IsDeviceName(name.Split('.')[0]);

    static bool IsDeviceName(string name) => name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PRN", StringComparison.OrdinalIgnoreCase) || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
        (name.Length == 4 && name[3] is >= '1' and <= '9' &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));

    ProfileInfo ReadInfo(string id)
    {
        var root = ProfileRoot(id);
        var info = ReadJson<ProfileInfo>(Path.Combine(root, "profile.json"));
        if (info.Schema != 1 || info.Id != id || !IsValidNewName(info.Name) ||
            info.Kind is not ("fresh" or "copied") || info.CreatedUtc.Kind != DateTimeKind.Utc || info.CreatedUtc == default ||
            (info.AccountId != null && !ValidAccountId(info.AccountId)))
            throw new InvalidDataException("This profile has incompatible or damaged metadata.");
        foreach (var child in Directory.EnumerateFileSystemEntries(root))
        {
            NoLinks(child);
            if (Path.GetFileName(child) is not ("profile.json" or "remote"))
                throw new InvalidDataException("This profile contains an unexpected file or folder.");
        }
        return info;
    }

    static ProfileInfo WithDetails(ProfileInfo info, IReadOnlyList<SaveFile> files) => info with
    {
        FileCount = files.Count,
        Bytes = files.Sum(file => file.Bytes),
        LastSavedUtc = files.Count == 0 ? null : files.Max(file => file.LastWriteUtc),
        Status = files.Count == 0 ? "Fresh career: GRID Autosport creates it when you first start Normal play with it" : "Saved career",
    };

    public ProfileInfo Read(string id)
    {
        if (id != CurrentCareer)
        {
            var info = ReadInfo(id);
            if (info.AccountId != null && info.AccountId != AccountId())
                throw new IOException("This profile belongs to a different Steam account. Sign in to its original account or choose another profile.");
            return WithDetails(info, ScanRemote(Path.Combine(ProfileRoot(id), "remote")));
        }
        var current = new ProfileInfo(1, CurrentCareer, "Current game career", "current", DateTime.UnixEpoch);
        var source = currentSaveRoot();
        if (source == null) return current with { Status = "The game's normal Steam career: no local save found" };
        return WithDetails(current, ScanRemote(source)) with { Status = "The game's normal Steam career, synced by Steam Cloud" };
    }

    // The folder holding a career's save files (Steam's for the current career), for career details.
    public string? SaveFolder(string id) => id == CurrentCareer ? currentSaveRoot() : Path.Combine(ProfileRoot(id), "remote");

    public string SelectedId()
    {
        NoLinks(selection);
        if (!File.Exists(selection))
        {
            if (Directory.Exists(selection)) throw new InvalidDataException("Profile selection is not a file.");
            return CurrentCareer;
        }
        var selected = ReadJson<Selection>(selection);
        if (selected.Schema != 1) throw new InvalidDataException("Unsupported profile selection version.");
        if (selected.Id != CurrentCareer) Read(selected.Id);
        return selected.Id;
    }

    public IReadOnlyList<ProfileEntry> List()
    {
        NoLinks(Root);
        var entries = new List<ProfileEntry> { Entry(CurrentCareer) };
        if (Directory.Exists(Root))
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                var id = Path.GetFileName(directory);
                if (Guid.TryParseExact(id, "N", out var guid) && guid.ToString("N") == id) entries.Add(Entry(id));
            }
        return entries.Take(1).Concat(entries.Skip(1).OrderBy(entry => entry.Info?.Name ?? entry.Id, StringComparer.CurrentCultureIgnoreCase)).ToArray();
    }

    ProfileEntry Entry(string id)
    {
        try { return new ProfileEntry(id, Read(id), null); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        { return new ProfileEntry(id, null, error.Message); }
    }

    public void Select(string id)
    {
        using var guard = new EditGuard(requireClosed);
        if (id != CurrentCareer) Read(id);
        AtomicJson(selection, new Selection(1, id));
    }

    public ProfileInfo Create(string name, CareerStart start)
    {
        using var guard = new EditGuard(requireClosed);
        if (!IsValidNewName(name)) throw new ArgumentException("Use 1–24 letters (A–Z) or numbers (0–9), without spaces or symbols.");
        SelectedId(); // Never replace a broken selection without an explicit switch.
        var accountId = AccountId();
        if (currentAccountId != null && accountId == null)
            throw new IOException("The active Steam account could not be identified. Start Steam and sign in before creating a profile.");
        NoLinks(Root);
        Directory.CreateDirectory(Root);
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var existingId = Path.GetFileName(directory);
            if (!Guid.TryParseExact(existingId, "N", out _)) continue;
            var existing = ReadInfo(existingId);
            if (existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A profile with that name already exists.");
        }
        var source = start switch
        {
            CareerStart.Fresh => null,
            CareerStart.CopyCurrent => currentSaveRoot() ?? throw new IOException("No local GRID Autosport Steam career was found to copy."),
            _ => throw new ArgumentOutOfRangeException(nameof(start)),
        };
        var files = source == null ? [] : ScanRemote(source);
        if (start == CareerStart.CopyCurrent && files.Count == 0) throw new IOException("The current game career has no save files to copy.");
        var id = Guid.NewGuid().ToString("N");
        var info = new ProfileInfo(1, id, name, source == null ? "fresh" : "copied", DateTime.UtcNow) { AccountId = accountId };
        var staging = Path.Combine(Root, ".creating-" + id);
        var destination = ProfileRoot(id);
        var owned = new List<string>();
        var registered = false;
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "remote"));
            NoLinks(staging);
            foreach (var file in files)
            {
                NoLinks(file.Path);
                var target = Path.Combine(staging, "remote", file.Name);
                owned.Add(target);
                using (var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (input.Length != file.Bytes || File.GetLastWriteTimeUtc(file.Path) != file.LastWriteUtc)
                        throw new IOException("The source save changed while the profile was being created. Try again after GRID Autosport and Steam Cloud finish saving.");
                    input.CopyTo(output);
                    output.Flush(true);
                }
                File.SetLastWriteTimeUtc(target, file.LastWriteUtc);
            }
            if (AccountId() != accountId || (source != null && !string.Equals(currentSaveRoot(), source, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The active player changed while the profile was being created. Try again.");
            var metadata = Path.Combine(staging, "profile.json");
            owned.Add(metadata); NewJson(metadata, info);
            Directory.Move(staging, destination);
            owned = owned.Select(path => Path.Combine(destination, Path.GetRelativePath(staging, path))).ToList();
            staging = destination;
            AtomicJson(selection, new Selection(1, id));
            registered = true;
            return Read(id);
        }
        finally
        {
            if (!registered)
            {
                // Only files created by this attempt; never recursively remove a save directory.
                foreach (var path in owned) { NoLinks(path); if (File.Exists(path)) File.Delete(path); }
                var remote = Path.Combine(staging, "remote");
                NoLinks(remote); if (Directory.Exists(remote)) Directory.Delete(remote);
                NoLinks(staging); if (Directory.Exists(staging)) Directory.Delete(staging);
            }
        }
    }

    public void Delete(string id)
    {
        using var guard = new EditGuard(requireClosed);
        if (id == CurrentCareer) throw new IOException("The current game career cannot be deleted here.");
        if (id == SelectedId()) throw new IOException("Use another profile before deleting this one.");
        ReadInfo(id);
        var root = ProfileRoot(id);
        var remote = Path.Combine(root, "remote");
        var saves = Directory.Exists(remote) ? Directory.EnumerateFiles(remote).ToArray() : [];
        foreach (var path in saves) if (!ValidSaveName(Path.GetFileName(path))) throw new InvalidDataException("The save folder contains an unsupported file. Nothing was deleted.");
        var files = saves.Append(Path.Combine(root, "profile.json")).ToArray();
        foreach (var path in files.Append(remote).Append(root))
        {
            NoLinks(path);
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new IOException("A profile file is read-only. Nothing was deleted.");
        }
        foreach (var path in saves) { NoLinks(path); File.Delete(path); }
        NoLinks(remote); if (Directory.Exists(remote)) Directory.Delete(remote);
        var metadata = Path.Combine(root, "profile.json");
        NoLinks(metadata); File.Delete(metadata);
        NoLinks(root); Directory.Delete(root);
    }

    // The request a session folder carries for the bridge, or null for the current game career.
    internal string? StorageRequest()
    {
        var id = SelectedId();
        if (id == CurrentCareer) return null;
        var info = ReadInfo(id);
        var account = AccountId() ?? info.AccountId;
        if (account == null) throw new IOException("Sign in to Steam to play a career profile.");
        if (info.AccountId != null && info.AccountId != account)
            throw new IOException("The selected career profile belongs to a different Steam account. Choose another profile.");
        var remote = Path.Combine(ProfileRoot(id), "remote");
        ScanRemote(remote);
        return RequestHeader + "\n" + remote + "\n" + id + "\n" + account + "\n";
    }

    sealed class EditGuard : IDisposable
    {
        readonly Mutex mutex = new(false, Installation.SessionMutex);
        bool held;
        internal EditGuard(Action requireClosed)
        {
            try
            {
                try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("Close the active GRID Autosport VR session before changing career profiles.");
                requireClosed();
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (held) { mutex.ReleaseMutex(); held = false; }
            mutex.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}

// The player's Steam account and save folder, and the checks before a profile change.
internal static class ProfileService
{
    internal static ProfileStore Open() => new(LauncherSettings.DataDirectory, RequireClosed, CurrentSaveRoot, AccountId);

    internal static string? AccountId()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
        if (key?.GetValue("ActiveUser") is int account && account != 0)
            return unchecked((uint)account).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Some Steam sessions leave ActiveUser at zero while signed in: its most recent login, while it runs.
        var processes = Process.GetProcessesByName("steam");
        try
        {
            if (processes.Length == 0) return null;
            var path = Path.Combine(SteamRoot(), "config", "loginusers.vdf");
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) return null;
            return RecentAccount(File.ReadAllText(path));
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    // loginusers.vdf: "users" { "<SteamID64>" { ... "MostRecent" "1" ... } }; the account is its low 32 bits.
    internal static string? RecentAccount(string vdf)
    {
        var block = System.Text.RegularExpressions.Regex.Matches(vdf, "\"(7656\\d{13})\"\\s*\\{([^{}]*)\\}");
        foreach (System.Text.RegularExpressions.Match user in block)
            if (System.Text.RegularExpressions.Regex.IsMatch(user.Groups[2].Value, "\"MostRecent\"\\s*\"1\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                ulong.TryParse(user.Groups[1].Value, out var id64) && (uint)id64 != 0)
                return ((uint)id64).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    internal static string SteamRoot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return Path.GetFullPath(key?.GetValue("SteamPath") as string ?? @"C:\Program Files (x86)\Steam");
    }

    internal static string? CurrentSaveRoot()
    {
        var account = AccountId();
        if (account == null) throw new IOException("Sign in to Steam before using career profiles.");
        var path = Path.Combine(SteamRoot(), "userdata", account, Installation.AppId, "remote");
        ProfileStore.NoLinks(path);
        return Directory.Exists(path) ? path : null;
    }

    static void RequireClosed()
    {
        foreach (var name in new[] { "GRIDAutosport", "GRIDAutosport_avx" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) throw new IOException("Close GRID Autosport before changing career profiles.");
    }
}
