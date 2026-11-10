// Build as a Win32 console program to exercise the launcher's session marker.
#include "../bridge/play_session.h"
#include <cassert>
#include <filesystem>
#include <fstream>
#include <string>

int wmain() {
    namespace fs = std::filesystem;
    constexpr wchar_t token[] = L"0123456789abcdef0123456789abcdef";
    const fs::path root = fs::temp_directory_path() /
        (L"GASVR marker test " + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    assert(fs::create_directory(root));
    const fs::path session = root / L"GRIDAutosportVR" / L"sessions" / token;
    fs::create_directories(session);
    const fs::path marker = session / L"session.id";
    wchar_t selected[MAX_PATH]{};
    assert(!AutosportPlayDirectory(token, root.c_str(), selected));
    auto write = [&](const char* content) { std::ofstream(marker, std::ios::binary) << content; };
    write("0123456789abcdef0123456789abcdef\n");
    assert(!AutosportPlayDirectory(token, root.c_str(), selected));
    write("0123456789abcdef0123456789abcdee");
    assert(!AutosportPlayDirectory(token, root.c_str(), selected));
    write("0123456789abcdef0123456789abcdef");
    assert(AutosportPlayDirectory(token, root.c_str(), selected) && fs::path(selected) == session);
    assert(!AutosportPlayDirectory(L"..", root.c_str(), selected));
    assert(!AutosportPlayDirectory(L"0123456789ABCDEF0123456789ABCDEF", root.c_str(), selected));
    fs::remove(marker);
    fs::remove(session);
    fs::remove(session.parent_path());
    fs::remove(session.parent_path().parent_path());
    fs::remove(root);
    std::puts("PASS ordinary play token, home path with spaces, wrong token, missing and oversized marker");
}
