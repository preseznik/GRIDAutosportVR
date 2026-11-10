#pragma once
#include <windows.h>
#include <cwchar>

// The normal Steam launch carries a token. Derive the directory ourselves and
// require the launcher's per-session marker before activating the renderer.
inline bool AutosportPlayDirectory(const wchar_t* token, const wchar_t* localData,
                                   wchar_t (&directory)[MAX_PATH]) {
    constexpr size_t length = 32;
    constexpr wchar_t suffix[] = L"\\GRIDAutosportVR\\sessions\\";
    if (!localData || !*localData || wcslen(token) != length ||
        wcsspn(token, L"0123456789abcdef") != length ||
        wcslen(localData) + wcslen(suffix) + length + wcslen(L"\\session.id") >= MAX_PATH)
        return false;
    wchar_t session[MAX_PATH], marker[MAX_PATH];
    swprintf_s(session, L"%s%s%s", localData, suffix, token);
    swprintf_s(marker, L"%s\\session.id", session);
    HANDLE file = CreateFileW(marker, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    char bytes[length + 1];
    DWORD read{};
    bool match = ReadFile(file, bytes, sizeof(bytes), &read, nullptr) && read == length;
    CloseHandle(file);
    for (size_t i = 0; match && i < length; ++i) match = bytes[i] == token[i];
    if (match) wcscpy_s(directory, session);
    return match;
}
