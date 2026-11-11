#include "ShellOverlay.h"
#include "OverlayCache.h"

#include <ShlObj.h>
#include <Windows.h>
#include <algorithm>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

namespace {
constexpr std::uint32_t Magic = 0x4D4F4156;
constexpr std::uint32_t Version = 1;
using GetClassObjectFn = HRESULT(__stdcall*)(REFCLSID, REFIID, void**);
using CanUnloadNowFn = HRESULT(__stdcall*)();
using SetCachePathFn = void(__stdcall*)(const wchar_t*);
GetClassObjectFn g_getClassObject = nullptr;
CanUnloadNowFn g_canUnloadNow = nullptr;
SetCachePathFn g_setCachePath = nullptr;

void AddU32(std::vector<std::uint8_t>& bytes, std::uint32_t value) {
    for (unsigned i = 0; i < 4; ++i) bytes.push_back(static_cast<std::uint8_t>(value >> (8 * i)));
}

void AddI64(std::vector<std::uint8_t>& bytes, std::int64_t value) {
    const auto bits = static_cast<std::uint64_t>(value);
    for (unsigned i = 0; i < 8; ++i) bytes.push_back(static_cast<std::uint8_t>(bits >> (8 * i)));
}

std::int64_t NowFileTime() {
    FILETIME time{};
    GetSystemTimeAsFileTime(&time);
    return (static_cast<std::int64_t>(time.dwHighDateTime) << 32) | time.dwLowDateTime;
}

std::vector<std::uint8_t> MakeCache(std::int64_t timestamp, const std::vector<moavcs::shell::StatusEntry>& entries) {
    std::vector<std::uint8_t> bytes;
    AddU32(bytes, Magic);
    AddU32(bytes, Version);
    AddI64(bytes, timestamp);
    AddU32(bytes, static_cast<std::uint32_t>(entries.size()));
    for (const auto& entry : entries) {
        AddU32(bytes, static_cast<std::uint32_t>(entry.state));
        AddU32(bytes, static_cast<std::uint32_t>(entry.path.size()));
        for (const wchar_t ch : entry.path) {
            const auto code = static_cast<std::uint16_t>(ch);
            bytes.push_back(static_cast<std::uint8_t>(code));
            bytes.push_back(static_cast<std::uint8_t>(code >> 8));
        }
    }
    return bytes;
}

void Check(bool condition, const char* description) {
    if (!condition) throw std::runtime_error(description);
}

void WriteFile(const std::filesystem::path& path, const std::vector<std::uint8_t>& bytes) {
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    output.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    if (!output) throw std::runtime_error("failed to write fixture cache");
}

void TestDecoder() {
    using moavcs::shell::DecodeStatusCache;
    using moavcs::shell::OverlayState;
    using moavcs::shell::StatusEntry;
    const auto now = NowFileTime();
    const std::vector<StatusEntry> source{
        { OverlayState::Conflict, L"C:\\moa fixture\\충돌.txt" },
        { OverlayState::Modified, L"C:\\moa fixture\\수정.txt" },
        { OverlayState::Untracked, L"C:\\moa fixture\\새 파일.txt" },
    };
    auto bytes = MakeCache(now, source);
    std::vector<StatusEntry> decoded;
    Check(DecodeStatusCache(bytes.data(), bytes.size(), now, decoded), "valid Unicode cache rejected");
    Check(decoded.size() == 3 && std::any_of(decoded.begin(), decoded.end(), [&](const StatusEntry& item) {
        return item.path == source[2].path && item.state == OverlayState::Untracked;
    }), "decoded cache contents differ");
    Check(!DecodeStatusCache(bytes.data(), bytes.size(), now + 91ll * 10000000, decoded) && decoded.empty(), "stale cache accepted or retained entries");
    Check(!DecodeStatusCache(bytes.data(), bytes.size(), now - 6ll * 10000000, decoded) && decoded.empty(), "future cache accepted");
    bytes.pop_back();
    Check(!DecodeStatusCache(bytes.data(), bytes.size(), now, decoded) && decoded.empty(), "truncated cache accepted or retained entries");
    bytes = MakeCache(now, source);
    bytes[0] ^= 0x80;
    Check(!DecodeStatusCache(bytes.data(), bytes.size(), now, decoded) && decoded.empty(), "bad magic accepted");
    const std::vector<StatusEntry> duplicates{
        { OverlayState::Modified, L"C:\\moa fixture\\duplicate.txt" },
        { OverlayState::Untracked, L"c:\\MOA FIXTURE\\DUPLICATE.TXT" },
    };
    bytes = MakeCache(now, duplicates);
    Check(!DecodeStatusCache(bytes.data(), bytes.size(), now, decoded) && decoded.empty(), "duplicate case-insensitive paths accepted");
    const std::vector<StatusEntry> invalidUtf16{
        { OverlayState::Modified, std::wstring(L"C:\\bad\\") + static_cast<wchar_t>(0xD800) },
    };
    bytes = MakeCache(now, invalidUtf16);
    Check(!DecodeStatusCache(bytes.data(), bytes.size(), now, decoded) && decoded.empty(), "unpaired UTF-16 surrogate accepted");
}

void TestComAndCache(HMODULE testServer) {
    using moavcs::shell::OverlayState;
    using moavcs::shell::StatusEntry;
    wchar_t tempDirectory[MAX_PATH]{};
    Check(GetTempPathW(MAX_PATH, tempDirectory) > 0, "GetTempPathW failed");
    const auto cachePath = std::filesystem::path(tempDirectory) / L"moavcs-overlay-smoke-status.bin";
    const std::wstring itemPath = L"C:\\moa fixture\\한글 파일.txt";
    const std::vector<StatusEntry> entries{ { OverlayState::Modified, itemPath } };
    WriteFile(cachePath, MakeCache(NowFileTime(), entries));
    g_setCachePath(cachePath.c_str());

    struct Case { const GUID* clsid; int index; };
    const Case cases[] = {
        { &CLSID_MoaVcsOverlayConflict, 0 },
        { &CLSID_MoaVcsOverlayModified, 1 },
        { &CLSID_MoaVcsOverlayUntracked, 2 },
    };
    for (const auto& item : cases) {
        IClassFactory* factory = nullptr;
        Check(SUCCEEDED(g_getClassObject(*item.clsid, IID_IClassFactory, reinterpret_cast<void**>(&factory))) && factory != nullptr,
            "DllGetClassObject failed");
        IShellIconOverlayIdentifier* handler = nullptr;
        Check(SUCCEEDED(factory->CreateInstance(nullptr, __uuidof(IShellIconOverlayIdentifier), reinterpret_cast<void**>(&handler))) && handler != nullptr,
            "class factory CreateInstance failed");
        wchar_t iconFile[32768]{};
        int iconIndex = -1;
        DWORD flags = 0;
        Check(SUCCEEDED(handler->GetOverlayInfo(iconFile, static_cast<int>(std::size(iconFile)), &iconIndex, &flags)), "GetOverlayInfo failed");
        Check(iconIndex == item.index && (flags & (ISIOI_ICONFILE | ISIOI_ICONINDEX)) == (ISIOI_ICONFILE | ISIOI_ICONINDEX), "overlay index/flags mismatch");
        wchar_t expectedModule[32768]{};
        Check(GetModuleFileNameW(testServer, expectedModule, static_cast<DWORD>(std::size(expectedModule))) > 0 &&
            CompareStringOrdinal(iconFile, -1, expectedModule, -1, TRUE) == CSTR_EQUAL, "GetOverlayInfo returned a different module");
        int priority = -1;
        Check(SUCCEEDED(handler->GetPriority(&priority)) && priority == item.index, "GetPriority mismatch");
        Check(handler->GetPriority(nullptr) == E_POINTER, "GetPriority null pointer not rejected");
        Check(handler->IsMemberOf(itemPath.c_str(), 0) == (item.index == 1 ? S_OK : S_FALSE), "overlay state lookup mismatch");
        std::wstring caseVariant = itemPath;
        for (auto& ch : caseVariant) if (ch >= L'a' && ch <= L'z') ch -= L'a' - L'A';
        Check(handler->IsMemberOf(caseVariant.c_str(), 0) == (item.index == 1 ? S_OK : S_FALSE), "case-insensitive path lookup failed");
        Check(handler->IsMemberOf(L"C:\\moa fixture\\", 0) == S_FALSE, "unlisted parent path inherited a badge");
        Check(handler->IsMemberOf(nullptr, 0) == S_FALSE, "null shell path not handled");
        handler->Release();
        factory->Release();
    }

    IClassFactory* unavailable = nullptr;
    const GUID unknown{ 0xFEFEFEFE, 0x1, 0x2, { 0, 1, 2, 3, 4, 5, 6, 7 } };
    Check(g_getClassObject(unknown, IID_IClassFactory, reinterpret_cast<void**>(&unavailable)) == CLASS_E_CLASSNOTAVAILABLE,
        "unknown CLSID was accepted");
    Check(g_canUnloadNow() == S_OK, "COM object lifetime did not return to zero");

    DeleteFileW(cachePath.c_str());
    g_setCachePath(cachePath.c_str());
    IClassFactory* factory = nullptr;
    Check(SUCCEEDED(g_getClassObject(CLSID_MoaVcsOverlayModified, IID_IClassFactory, reinterpret_cast<void**>(&factory))), "cache test factory failed");
    IShellIconOverlayIdentifier* handler = nullptr;
    Check(SUCCEEDED(factory->CreateInstance(nullptr, __uuidof(IShellIconOverlayIdentifier), reinterpret_cast<void**>(&handler))), "cache test handler failed");
    Check(handler->IsMemberOf(itemPath.c_str(), 0) == S_FALSE, "missing cache retained stale badge");
    handler->Release();
    factory->Release();
    DeleteFileW(cachePath.c_str());
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    HMODULE testServer = nullptr;
    try {
        Check(argc == 2, "pass the test-only shell overlay DLL path");
        testServer = LoadLibraryW(argv[1]);
        Check(testServer != nullptr, "could not LoadLibrary the shell COM server");
        g_getClassObject = reinterpret_cast<GetClassObjectFn>(GetProcAddress(testServer, "DllGetClassObject"));
        g_canUnloadNow = reinterpret_cast<CanUnloadNowFn>(GetProcAddress(testServer, "DllCanUnloadNow"));
        g_setCachePath = reinterpret_cast<SetCachePathFn>(GetProcAddress(testServer, "MoaVcsSetTestCachePath"));
        Check(g_getClassObject && g_canUnloadNow && g_setCachePath, "required COM/test exports are missing");
        const HRESULT comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        Check(SUCCEEDED(comResult), "COM initialization failed");
        TestDecoder();
        TestComAndCache(testServer);
        CoUninitialize();
        FreeLibrary(testServer);
        std::wcout << L"MoaVcs shell overlay smoke tests passed.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "MoaVcs shell overlay smoke tests failed: " << error.what() << '\n';
        CoUninitialize();
        if (testServer != nullptr) FreeLibrary(testServer);
        return 1;
    }
}
