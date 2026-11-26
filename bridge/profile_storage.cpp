// Career profiles (launcher Profile tab, Profiles.cs), ported from GRID2VR's profile_storage.cpp.
//
// GRID Autosport saves its career through Steam Cloud: ISteamRemoteStorage (version 010, from the
// game's steam_api.dll, the same file GRID 2 ships) writes savegame@profile0#... files into
// Steam\userdata\<account>\255220\remote. When the session folder holds profile-storage.request,
// the game's import of steam_api.dll!SteamRemoteStorage is routed, before the game's entry point
// runs, to a local object whose file methods use the profile's own folder
// (%LOCALAPPDATA%\GRIDAutosportVR\profiles\<id>\remote). Steam's copy, and its cloud, are never read
// or written while a profile is in use. Methods that are not about this player's files (quota, cloud
// enabled, UGC) go to Steam's own object unchanged and are counted. A request that does not check
// out, or a Steam account other than the profile's, stops the game before it reads any save
// (receipt phase "refused"), so a profile's career and the normal one can never mix.
//
// Request (UTF-8, LF): GASVR_PROFILE_STORAGE_V1 / <remote folder> / <profile id> / <Steam account>.
// Receipt: profile-storage.json in the session folder, read by the launcher after the game exits.
#include "bridge_api.h"
#include <windows.h>
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <charconv>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#pragma comment(lib, "bcrypt.lib")

namespace {
constexpr std::uint64_t maxTotal = 64ull * 1024 * 1024;
constexpr std::uint32_t maxFile = 32u * 1024 * 1024;
constexpr std::size_t maxFiles = 64;
constexpr char steamApiSha256[] = "abfedd473b3f4a9597bbdc90d20f4b6f696bb2ebb937a03177461df695430ad6";
constexpr unsigned slotCount = 64, ownSlots = 17; // ISteamRemoteStorage010: 0-16 are about files

bool StartsWith(std::string_view text, std::string_view prefix) { return text.substr(0, prefix.size()) == prefix; }
bool EndsWith(std::string_view text, std::string_view suffix) { return text.size() >= suffix.size() && text.substr(text.size() - suffix.size()) == suffix; }
bool Temporary(std::string_view name) { return StartsWith(name, ".gasvr-write-") && EndsWith(name, ".tmp"); }

bool ValidFileName(std::string_view value) {
    if (value.empty() || value.size() > 180 || value == "." || value == ".." || value.back() == '.' || value.back() == ' ') return false;
    for (const unsigned char c : value) if (c < 32 || c > 126 || std::strchr("\\/:*?\"<>|", c)) return false;
    auto stem = std::string(value.substr(0, value.find('.')));
    for (auto& c : stem) if (c >= 'a' && c <= 'z') c -= 32;
    if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return false;
    if (stem.size() == 4 && (StartsWith(stem, "COM") || StartsWith(stem, "LPT")) && stem[3] >= '1' && stem[3] <= '9') return false;
    return true;
}

struct Handle {
    HANDLE value{INVALID_HANDLE_VALUE};
    Handle() = default;
    explicit Handle(HANDLE v) : value(v) {}
    ~Handle() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value(other.value) { other.value = INVALID_HANDLE_VALUE; }
    bool Valid() const { return value != INVALID_HANDLE_VALUE; }
};
bool Info(HANDLE handle, BY_HANDLE_FILE_INFORMATION& info, bool directory) {
    return GetFileInformationByHandle(handle, &info) && !(info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) &&
           bool(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == directory && (directory || info.nNumberOfLinks == 1);
}
Handle OpenFile(const std::filesystem::path& path, DWORD access) {
    return Handle(CreateFileW(path.c_str(), access, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
}

// The profile's remote folder: flat, at most 64 files and 64 MiB, no links.
class Store {
public:
    bool Open(const std::filesystem::path& folder) {
        root = folder;
        std::filesystem::path prefix = folder.root_path();
        if (!Pin(prefix)) return false;
        for (const auto& component : folder.relative_path()) { prefix /= component; if (!Pin(prefix)) return false; }
        std::size_t files{}; std::uint64_t bytes{};
        return ready = Totals(files, bytes);
    }
    bool Ready() const { return ready; }
    bool Write(const char* name, const void* data, std::int32_t bytes) {
        std::lock_guard lock(mutex);
        if (!ready) return Fail("store_unavailable");
        if (bytes < 0 || static_cast<std::uint32_t>(bytes) > maxFile || (!data && bytes)) return Fail("invalid_write_arguments");
        try {
            const auto path = Path(name); if (path.empty()) return Fail("invalid_filename");
            std::size_t files{}; std::uint64_t used{};
            if (!Totals(files, used)) return Fail("unsafe_profile_folder");
            std::uint64_t previous{};
            if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES) {
                auto file = OpenFile(path, FILE_READ_ATTRIBUTES); BY_HANDLE_FILE_INFORMATION info{};
                if (!file.Valid() || !Info(file.value, info, false) || info.nFileSizeHigh) return Fail("unsafe_target", GetLastError());
                previous = info.nFileSizeLow;
            } else if (files >= maxFiles) return Fail("file_count_limit");
            if (used - previous + bytes > maxTotal || used + bytes > maxTotal) return Fail("byte_limit");
            const auto temp = root / (L".gasvr-write-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(++serial) + L".tmp");
            const char* problem{}; DWORD error{};
            {
                Handle file(CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr));
                DWORD written{};
                if (!file.Valid()) problem = "temporary_create_failed";
                else if (!WriteFile(file.value, data, static_cast<DWORD>(bytes), &written, nullptr)) problem = "temporary_write_failed";
                else if (written != static_cast<DWORD>(bytes)) { problem = "short_write"; SetLastError(ERROR_WRITE_FAULT); }
                else if (!FlushFileBuffers(file.value)) problem = "temporary_flush_failed";
                if (problem) error = GetLastError();
            }
            if (!problem && !MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
                problem = "atomic_replace_failed"; error = GetLastError();
            }
            if (problem) { DeleteFileW(temp.c_str()); return Fail(problem, error); }
            return true;
        } catch (...) { return Fail("write_exception"); }
    }
    std::int32_t Read(const char* name, void* data, std::int32_t bytes) {
        if (!ready || bytes < 0 || (!data && bytes)) return 0;
        std::lock_guard lock(mutex);
        try {
            const auto path = Path(name); if (path.empty()) return 0;
            auto file = OpenFile(path, GENERIC_READ); BY_HANDLE_FILE_INFORMATION info{}; DWORD read{};
            if (!file.Valid() || !Info(file.value, info, false) || info.nFileSizeHigh || info.nFileSizeLow > maxFile) return 0;
            return ReadFile(file.value, data, static_cast<DWORD>(bytes), &read, nullptr) ? static_cast<std::int32_t>(read) : 0;
        } catch (...) { return 0; }
    }
    bool Delete(const char* name) {
        if (!ready) return false;
        std::lock_guard lock(mutex);
        try {
            const auto path = Path(name); if (path.empty()) return false;
            auto file = OpenFile(path, DELETE | FILE_READ_ATTRIBUTES); BY_HANDLE_FILE_INFORMATION info{}; FILE_DISPOSITION_INFO dispose{TRUE};
            return file.Valid() && Info(file.value, info, false) && SetFileInformationByHandle(file.value, FileDispositionInfo, &dispose, sizeof(dispose));
        } catch (...) { return false; }
    }
    bool Attributes(const char* name, BY_HANDLE_FILE_INFORMATION& info) {
        if (!ready) return false;
        std::lock_guard lock(mutex);
        try {
            const auto path = Path(name); if (path.empty()) return false;
            auto file = OpenFile(path, FILE_READ_ATTRIBUTES);
            return file.Valid() && Info(file.value, info, false) && !info.nFileSizeHigh && info.nFileSizeLow <= maxFile;
        } catch (...) { return false; }
    }
    std::int32_t Count() {
        if (!ready) return 0;
        std::lock_guard lock(mutex);
        try { return static_cast<std::int32_t>(Files().size()); } catch (...) { return 0; }
    }
    const char* Name(std::int32_t index, std::int32_t* size) {
        if (size) *size = 0;
        if (!ready || index < 0) return nullptr;
        std::lock_guard lock(mutex);
        try {
            const auto files = Files();
            if (static_cast<std::size_t>(index) >= files.size()) return nullptr;
            enumerated = files[index].first;
            if (size) *size = files[index].second;
            return enumerated.c_str();
        } catch (...) { return nullptr; }
    }
    unsigned failures{};
    char failure[48]{};
    DWORD failureError{};
private:
    std::filesystem::path root;
    std::vector<Handle> leases;
    std::mutex mutex;
    bool ready{};
    unsigned serial{};
    std::string enumerated;
    bool Fail(const char* category, DWORD error = 0) {
        ++failures; strcpy_s(failure, category); failureError = error;
        AutosportLog("Career profile: a save could not be written (%s, error %lu)", category, error);
        return false;
    }
    bool Pin(const std::filesystem::path& directory) {
        Handle handle(CreateFileW(directory.c_str(), FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                  OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        BY_HANDLE_FILE_INFORMATION info{};
        if (!handle.Valid() || !Info(handle.value, info, true)) return false;
        leases.push_back(std::move(handle));
        return true;
    }
    std::filesystem::path Path(const char* name) const {
        if (!name || strnlen_s(name, 181) > 180 || !ValidFileName(name) || Temporary(name)) return {};
        return root / name;
    }
    bool Totals(std::size_t& files, std::uint64_t& bytes) {
        for (const auto& entry : std::filesystem::directory_iterator(root)) {
            const auto attrs = GetFileAttributesW(entry.path().c_str());
            if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))) return false;
            if (!ValidFileName(entry.path().filename().string())) return false;
            auto file = OpenFile(entry.path(), FILE_READ_ATTRIBUTES); BY_HANDLE_FILE_INFORMATION info{};
            if (!file.Valid() || !Info(file.value, info, false) || ++files > maxFiles || info.nFileSizeHigh) return false;
            bytes += info.nFileSizeLow;
            if (bytes > maxTotal) return false;
        }
        return true;
    }
    std::vector<std::pair<std::string, std::int32_t>> Files() {
        std::vector<std::pair<std::string, std::int32_t>> result;
        for (const auto& entry : std::filesystem::directory_iterator(root)) {
            const auto name = entry.path().filename().string();
            if (Temporary(name)) continue;
            if (!ValidFileName(name)) throw std::runtime_error("profile filename");
            auto file = OpenFile(entry.path(), FILE_READ_ATTRIBUTES); BY_HANDLE_FILE_INFORMATION info{};
            if (!file.Valid() || !Info(file.value, info, false) || info.nFileSizeHigh || info.nFileSizeLow > maxFile || result.size() >= maxFiles)
                throw std::runtime_error("profile entry");
            result.emplace_back(name, static_cast<std::int32_t>(info.nFileSizeLow));
        }
        std::sort(result.begin(), result.end());
        return result;
    }
};

// POD only before the game runs: attach happens under the loader lock.
wchar_t sessionFolder[MAX_PATH]{}, requestPath[MAX_PATH + 32]{};
using GetterFn = void* (__cdecl*)();
GetterFn steamStorage{}, steamUser{};
HMODULE steamModule{};
void* volatile realStorage{};
void* storageSlots[slotCount]{};
struct Object { void** slots; } storageObject{storageSlots};
BYTE* forwarders{};
volatile LONG forwarded[slotCount]{};
volatile LONG writes{}, reads{}, deletes{}, lists{}, unsupported{};
char profileId[33]{};
Store* store{};
INIT_ONCE once = INIT_ONCE_STATIC_INIT;

void Receipt(const char* phase, const char* reason = "") {
    wchar_t path[MAX_PATH + 32]{};
    if (swprintf_s(path, L"%s\\profile-storage.json", sessionFolder) < 0) return;
    const unsigned failures = store ? store->failures : 0;
    if (!reason[0] && failures) reason = store->failure;
    std::string calls;
    for (unsigned i = ownSlots; i < slotCount; ++i)
        if (forwarded[i]) calls += (calls.empty() ? "" : ",") + std::string("\"") + std::to_string(i) + "\":" + std::to_string(forwarded[i]);
    char text[2048]{};
    const int size = sprintf_s(text, "{\"schema\":1,\"phase\":\"%s\",\"reason\":\"%s\",\"profileId\":\"%s\",\"writes\":%ld,\"reads\":%ld,\"deletes\":%ld,"
        "\"listCalls\":%ld,\"unsupportedCalls\":%ld,\"writeFailures\":%u,\"lastWin32Error\":%lu,\"forwardedToSteam\":{%s}}\n",
        phase, reason, profileId, writes, reads, deletes, lists, unsupported, failures, store ? store->failureError : 0ul, calls.c_str());
    HANDLE file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) { DWORD done{}; WriteFile(file, text, size > 0 ? size : 0, &done, nullptr); CloseHandle(file); }
}
[[noreturn]] void Refuse(const char* reason) {
    AutosportLog("Career profile refused (%s): GRID Autosport is closed before it reads or writes any save", reason);
    Receipt("refused", reason);
    TerminateProcess(GetCurrentProcess(), 0x47415350);
    __fastfail(FAST_FAIL_FATAL_APP_EXIT);
}

std::string HashFile(const wchar_t* path) {
    std::ifstream input(path, std::ios::binary);
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{};
    if (!input || BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    if (BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) < 0) { BCryptCloseAlgorithmProvider(algorithm, 0); return {}; }
    std::vector<char> block(65536); bool valid = true;
    while (input) {
        input.read(block.data(), block.size());
        if (BCryptHashData(hash, reinterpret_cast<PUCHAR>(block.data()), static_cast<ULONG>(input.gcount()), 0) < 0) valid = false;
    }
    std::array<unsigned char, 32> digest{};
    valid = valid && !input.bad() && BCryptFinishHash(hash, digest.data(), 32, 0) >= 0;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
    char text[65]{};
    if (valid) for (unsigned i = 0; i < 32; ++i) sprintf_s(text + i * 2, 3, "%02x", digest[i]);
    return text;
}

// The request's folder must be this PC's own profile folder for its id.
bool ParseRequest(const char* data, DWORD size, std::filesystem::path& remote, std::uint32_t& account) {
    std::string_view text(data, size);
    std::array<std::string_view, 4> lines{};
    for (auto& line : lines) {
        const auto end = text.find('\n');
        line = text.substr(0, end);
        if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        text = end == std::string_view::npos ? std::string_view{} : text.substr(end + 1);
    }
    if (!text.empty() || lines[0] != "GASVR_PROFILE_STORAGE_V1" || lines[2].size() != 32 ||
        !std::all_of(lines[2].begin(), lines[2].end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); })) return false;
    const auto parsed = std::from_chars(lines[3].data(), lines[3].data() + lines[3].size(), account);
    if (parsed.ec != std::errc{} || parsed.ptr != lines[3].data() + lines[3].size() || !account) return false;
    wchar_t localData[MAX_PATH]{};
    const DWORD count = GetEnvironmentVariableW(L"LOCALAPPDATA", localData, MAX_PATH);
    if (!count || count >= MAX_PATH) return false;
    try {
        wchar_t wide[MAX_PATH]{};
        if (lines[1].empty() || lines[1].size() >= MAX_PATH ||
            !MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, lines[1].data(), static_cast<int>(lines[1].size()), wide, MAX_PATH - 1)) return false;
        const auto requested = std::filesystem::path(wide).lexically_normal();
        const auto expected = (std::filesystem::path(localData) / L"GRIDAutosportVR" / L"profiles" /
                               std::filesystem::path(std::string(lines[2])) / L"remote").lexically_normal();
        if (_wcsicmp(requested.c_str(), expected.c_str())) return false;
        remote = expected;
    } catch (...) { return false; }
    memcpy(profileId, lines[2].data(), 32);
    return true;
}

// ISteamUser::GetSteamID (slot 2, returning the 64-bit id through a hidden buffer, as GRID2VR).
std::uint32_t Account() {
    void* user = steamUser ? steamUser() : nullptr;
    if (!user) return 0;
    auto slots = *static_cast<void***>(user);
    std::uint64_t id{};
    const auto getId = reinterpret_cast<std::uint64_t* (__thiscall*)(void*, std::uint64_t*)>(slots[2]);
    if (getId(user, &id) != &id) return 0;
    return static_cast<std::uint32_t>(id);
}

// ISteamRemoteStorage010 file methods on the profile's folder (__fastcall stands in for __thiscall).
bool __fastcall FileWrite(void*, void*, const char* name, const void* data, std::int32_t bytes) {
    const bool ok = store->Write(name, data, bytes);
    if (ok) InterlockedIncrement(&writes);
    Receipt("active");
    return ok;
}
std::int32_t __fastcall FileRead(void*, void*, const char* name, void* data, std::int32_t bytes) {
    InterlockedIncrement(&reads);
    const auto result = store->Read(name, data, bytes);
    Receipt("active");
    return result;
}
bool __fastcall FileForget(void*, void*, const char*) { return true; } // Nothing here is in Steam Cloud.
bool __fastcall FileDelete(void*, void*, const char* name) {
    const bool ok = store->Delete(name);
    if (ok) InterlockedIncrement(&deletes);
    Receipt("active");
    return ok;
}
std::uint64_t __fastcall FileShare(void*, void*, const char*) { InterlockedIncrement(&unsupported); return 0; }
bool __fastcall SetSyncPlatforms(void*, void*, const char*, int) { return true; }
std::uint64_t __fastcall StreamOpen(void*, void*, const char*) { InterlockedIncrement(&unsupported); Receipt("active"); return ~0ull; }
bool __fastcall StreamChunk(void*, void*, std::uint64_t, const void*, std::int32_t) { InterlockedIncrement(&unsupported); return false; }
bool __fastcall StreamHandle(void*, void*, std::uint64_t) { InterlockedIncrement(&unsupported); return false; }
bool __fastcall FileExists(void*, void*, const char* name) { BY_HANDLE_FILE_INFORMATION info{}; return store->Attributes(name, info); }
std::int32_t __fastcall GetFileSize(void*, void*, const char* name) {
    BY_HANDLE_FILE_INFORMATION info{};
    return store->Attributes(name, info) ? static_cast<std::int32_t>(info.nFileSizeLow) : 0;
}
std::int64_t __fastcall GetFileTimestamp(void*, void*, const char* name) {
    BY_HANDLE_FILE_INFORMATION info{};
    if (!store->Attributes(name, info)) return 0;
    const std::uint64_t ticks = (std::uint64_t(info.ftLastWriteTime.dwHighDateTime) << 32) | info.ftLastWriteTime.dwLowDateTime;
    return static_cast<std::int64_t>(ticks / 10000000ull - 11644473600ull); // Unix seconds, as Steam's
}
int __fastcall GetSyncPlatforms(void*, void*, const char*) { return -1; } // k_ERemoteStoragePlatformAll
std::int32_t __fastcall GetFileCount(void*, void*) { InterlockedIncrement(&lists); const auto count = store->Count(); Receipt("active"); return count; }
const char* __fastcall GetFileNameAndSize(void*, void*, std::int32_t index, std::int32_t* size) { InterlockedIncrement(&lists); return store->Name(index, size); }

// Slots 17 and up (quota, cloud enabled, UGC...): Steam's own object, with `this` swapped. Each
// forwarder: lock inc [count]; mov ecx,[realStorage]; mov eax,[ecx]; jmp [eax+slot*4].
bool BuildForwarders() {
    constexpr unsigned size = 24;
    forwarders = static_cast<BYTE*>(VirtualAlloc(nullptr, slotCount * size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!forwarders) return false;
    for (unsigned i = ownSlots; i < slotCount; ++i) {
        BYTE* at = forwarders + i * size;
        const auto counter = reinterpret_cast<std::uint32_t>(&forwarded[i]);
        const auto object = reinterpret_cast<std::uint32_t>(&realStorage);
        const std::uint32_t offset = i * 4;
        at[0] = 0xF0; at[1] = 0xFF; at[2] = 0x05; memcpy(at + 3, &counter, 4);  // lock inc dword ptr [counter]
        at[7] = 0x8B; at[8] = 0x0D; memcpy(at + 9, &object, 4);                // mov ecx, [realStorage]
        at[13] = 0x8B; at[14] = 0x01;                                           // mov eax, [ecx]
        at[15] = 0xFF; at[16] = 0xA0; memcpy(at + 17, &offset, 4);              // jmp [eax+offset]
        storageSlots[i] = at;
    }
    DWORD old{};
    return VirtualProtect(forwarders, slotCount * size, PAGE_EXECUTE_READ, &old) &&
           FlushInstructionCache(GetCurrentProcess(), forwarders, slotCount * size);
}

BOOL CALLBACK Initialize(PINIT_ONCE, void*, void**) {
    try {
        wchar_t steamPath[MAX_PATH]{};
        if (!GetModuleFileNameW(steamModule, steamPath, MAX_PATH) || HashFile(steamPath) != steamApiSha256) Refuse("unsupported_steam_api");
        char text[4096]{}; DWORD size{};
        HANDLE file = CreateFileW(requestPath, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (file == INVALID_HANDLE_VALUE) Refuse("request_unreadable");
        const BOOL read = ReadFile(file, text, sizeof(text) - 1, &size, nullptr);
        CloseHandle(file);
        std::filesystem::path remote; std::uint32_t account{};
        if (!read || !ParseRequest(text, size, remote, account)) Refuse("invalid_request");
        const auto actual = Account();
        if (!actual) Refuse("steam_account_unavailable");
        if (actual != account) Refuse("steam_account_mismatch");
        store = new Store();
        if (!store->Open(remote)) Refuse("unsafe_profile_folder");
        storageSlots[0] = reinterpret_cast<void*>(FileWrite);
        storageSlots[1] = reinterpret_cast<void*>(FileRead);
        storageSlots[2] = reinterpret_cast<void*>(FileForget);
        storageSlots[3] = reinterpret_cast<void*>(FileDelete);
        storageSlots[4] = reinterpret_cast<void*>(FileShare);
        storageSlots[5] = reinterpret_cast<void*>(SetSyncPlatforms);
        storageSlots[6] = reinterpret_cast<void*>(StreamOpen);
        storageSlots[7] = reinterpret_cast<void*>(StreamChunk);
        storageSlots[8] = reinterpret_cast<void*>(StreamHandle);
        storageSlots[9] = reinterpret_cast<void*>(StreamHandle);
        storageSlots[10] = reinterpret_cast<void*>(FileExists);
        storageSlots[11] = reinterpret_cast<void*>(FileExists); // FilePersisted
        storageSlots[12] = reinterpret_cast<void*>(GetFileSize);
        storageSlots[13] = reinterpret_cast<void*>(GetFileTimestamp);
        storageSlots[14] = reinterpret_cast<void*>(GetSyncPlatforms);
        storageSlots[15] = reinterpret_cast<void*>(GetFileCount);
        storageSlots[16] = reinterpret_cast<void*>(GetFileNameAndSize);
        if (!BuildForwarders()) Refuse("forwarders_unavailable");
        AutosportLog("Career profile %s: the game's Steam Cloud saves stay in the profile's folder (%d files)", profileId, store->Count());
        Receipt("active");
        return TRUE;
    } catch (...) { Refuse("initialization_exception"); }
}

void* __cdecl StorageGetter() {
    if (!InitOnceExecuteOnce(&once, Initialize, nullptr, nullptr)) Refuse("initialization_failed");
    if (!realStorage) realStorage = steamStorage(); // Steam's own object, once Steam is up, for the forwarded methods
    return &storageObject;
}

// The executable's import of steam_api.dll!name: its IAT slot, found through the import table.
void** ImportSlot(const BYTE* image, const char* name) {
    const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
    const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(image + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC) return nullptr;
    const auto& directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress) return nullptr;
    for (auto descriptor = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR*>(image + directory.VirtualAddress); descriptor->Name; ++descriptor) {
        if (_stricmp(reinterpret_cast<const char*>(image + descriptor->Name), "steam_api.dll") || !descriptor->OriginalFirstThunk) continue;
        auto names = reinterpret_cast<const IMAGE_THUNK_DATA32*>(image + descriptor->OriginalFirstThunk);
        auto slots = reinterpret_cast<IMAGE_THUNK_DATA32*>(const_cast<BYTE*>(image) + descriptor->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++slots) {
            if (IMAGE_SNAP_BY_ORDINAL32(names->u1.Ordinal)) continue;
            const auto byName = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(image + names->u1.AddressOfData);
            if (!strcmp(reinterpret_cast<const char*>(byName->Name), name)) return reinterpret_cast<void**>(&slots->u1.Function);
        }
    }
    return nullptr;
}
}

// From DllMain, once the session folder is known and before the game's entry point runs. Returns
// false when a request exists but cannot be honoured; the caller then stops the game.
bool AutosportAttachProfileStorage(const BYTE* image, const wchar_t* folder) {
    if (!folder || !*folder || wcscpy_s(sessionFolder, folder) || swprintf_s(requestPath, L"%s\\profile-storage.request", folder) < 0) return true;
    const DWORD attrs = GetFileAttributesW(requestPath);
    if (attrs == INVALID_FILE_ATTRIBUTES) return true; // Current game career: Steam keeps the saves.
    if (attrs & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) { Receipt("refused", "invalid_request_file"); return false; }
    steamModule = GetModuleHandleW(L"steam_api.dll");
    if (!steamModule) { Receipt("refused", "steam_api_not_loaded"); return false; }
    steamStorage = reinterpret_cast<GetterFn>(GetProcAddress(steamModule, "SteamRemoteStorage"));
    steamUser = reinterpret_cast<GetterFn>(GetProcAddress(steamModule, "SteamUser"));
    void** slot = ImportSlot(image, "SteamRemoteStorage");
    if (!steamStorage || !steamUser || !slot) { Receipt("refused", "storage_import_missing"); return false; }
    if (*slot != reinterpret_cast<void*>(steamStorage)) { Receipt("refused", "foreign_storage_hook"); return false; }
    DWORD old{};
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old)) { Receipt("refused", "storage_import_protected"); return false; }
    const bool replaced = InterlockedCompareExchangePointer(slot, reinterpret_cast<void*>(StorageGetter), reinterpret_cast<void*>(steamStorage)) ==
                          reinterpret_cast<void*>(steamStorage);
    VirtualProtect(slot, sizeof(void*), old, &old);
    if (!replaced) { Receipt("refused", "storage_import_patch_failed"); return false; }
    AutosportLog("Career profile requested: the game's Steam Cloud saves are routed to the profile's folder");
    Receipt("armed");
    return true;
}
