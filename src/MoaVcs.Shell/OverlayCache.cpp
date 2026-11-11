#include "OverlayCache.h"

#include <ShlObj.h>
#include <algorithm>
#include <limits>
#include <memory>

namespace moavcs::shell {
namespace {
constexpr std::uint32_t Magic = 0x4D4F4156;
constexpr std::uint32_t Version = 1;
constexpr std::size_t MaxCacheBytes = 16u * 1024u * 1024u;
constexpr std::uint32_t MaxEntries = 100000;
constexpr std::uint32_t MaxPathUnits = 32767;
constexpr std::int64_t TicksPerSecond = 10000000;
constexpr std::int64_t MaxAgeTicks = 90 * TicksPerSecond;
constexpr std::int64_t FutureToleranceTicks = 5 * TicksPerSecond;
constexpr ULONGLONG ReloadIntervalMs = 2000;

bool ReadU32(const std::uint8_t* bytes, std::size_t length, std::size_t& offset, std::uint32_t& value) {
    if (offset > length || length - offset < sizeof(value)) return false;
    value = static_cast<std::uint32_t>(bytes[offset]) |
        (static_cast<std::uint32_t>(bytes[offset + 1]) << 8) |
        (static_cast<std::uint32_t>(bytes[offset + 2]) << 16) |
        (static_cast<std::uint32_t>(bytes[offset + 3]) << 24);
    offset += sizeof(value);
    return true;
}

bool ReadI64(const std::uint8_t* bytes, std::size_t length, std::size_t& offset, std::int64_t& value) {
    if (offset > length || length - offset < sizeof(value)) return false;
    std::uint64_t bits = 0;
    for (unsigned i = 0; i < 8; ++i) bits |= static_cast<std::uint64_t>(bytes[offset + i]) << (8 * i);
    value = static_cast<std::int64_t>(bits);
    offset += sizeof(value);
    return true;
}

bool IsAbsoluteWindowsPath(const std::wstring& path) noexcept {
    std::size_t componentStart = 0;
    if (path.size() >= 3 && ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) &&
        path[1] == L':' && path[2] == L'\\') {
        componentStart = 3;
    } else if (path.size() >= 5 && path[0] == L'\\' && path[1] == L'\\' && path[2] == L'?' && path[3] == L'\\' &&
        ((path[4] >= L'A' && path[4] <= L'Z') || (path[4] >= L'a' && path[4] <= L'z')) && path.size() >= 7 &&
        path[5] == L':' && path[6] == L'\\') {
        componentStart = 7;
    } else if (path.size() >= 11 && path.compare(0, 8, L"\\\\?\\UNC\\") == 0) {
        const auto serverEnd = path.find(L'\\', 8);
        if (serverEnd == std::wstring::npos || serverEnd == 8 || path.find(L':', 8) < serverEnd ||
            serverEnd + 1 >= path.size() || path[serverEnd + 1] == L'\\') return false;
        const auto shareEnd = path.find(L'\\', serverEnd + 1);
        if (shareEnd == serverEnd + 1) return false;
        componentStart = shareEnd == std::wstring::npos ? path.size() : shareEnd + 1;
    } else if (path.size() >= 5 && path[0] == L'\\' && path[1] == L'\\' && path[2] != L'\\' &&
        path[2] != L'.' && path[2] != L'?') {
        const auto serverEnd = path.find(L'\\', 2);
        if (serverEnd == std::wstring::npos || serverEnd == 2 || path.find(L':', 2) < serverEnd ||
            serverEnd + 1 >= path.size() || path[serverEnd + 1] == L'\\') return false;
        componentStart = serverEnd + 1;
    } else {
        return false;
    }

    if (path.find(L'/') != std::wstring::npos) return false;
    while (componentStart < path.size()) {
        const auto end = path.find(L'\\', componentStart);
        const auto length = (end == std::wstring::npos ? path.size() : end) - componentStart;
        if ((length == 1 && path[componentStart] == L'.') ||
            (length == 2 && path[componentStart] == L'.' && path[componentStart + 1] == L'.')) return false;
        if (path.find(L':', componentStart) != std::wstring::npos &&
            (end == std::wstring::npos || path.find(L':', componentStart) < end)) return false;
        if (end == std::wstring::npos) break;
        componentStart = end + 1;
    }
    return true;
}

std::wstring DefaultCachePath() {
    wchar_t localAppData[32768]{};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, localAppData))) return {};
    std::wstring result(localAppData);
    if (result.empty() || (result.size() >= 2 && result[0] == L'\\' && result[1] == L'\\')) return {};
    result += L"\\MoaVcs\\overlays\\status.bin";
    return result;
}

int ComparePaths(const std::wstring& left, const std::wstring& right) noexcept {
    return CompareStringOrdinal(left.c_str(), static_cast<int>(left.size()), right.c_str(),
        static_cast<int>(right.size()), TRUE);
}

bool ReadCacheFile(const std::wstring& path, std::vector<std::uint8_t>& bytes) noexcept {
    bytes.clear();
    if (path.empty()) return false;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size{};
    bool ok = GetFileSizeEx(file, &size) && size.QuadPart >= 20 &&
        static_cast<unsigned long long>(size.QuadPart) <= MaxCacheBytes;
    if (ok) {
        try { bytes.resize(static_cast<std::size_t>(size.QuadPart)); }
        catch (...) { ok = false; }
    }
    if (ok) {
        std::size_t offset = 0;
        while (offset < bytes.size()) {
            const DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(bytes.size() - offset, 1024u * 1024u));
            DWORD read = 0;
            if (!ReadFile(file, bytes.data() + offset, chunk, &read, nullptr) || read == 0) { ok = false; break; }
            offset += read;
        }
    }
    CloseHandle(file);
    if (!ok) bytes.clear();
    return ok;
}
} // namespace

bool DecodeStatusCache(const std::uint8_t* bytes, std::size_t length,
                       std::int64_t nowFileTime, std::vector<StatusEntry>& entries) {
    entries.clear();
    if (bytes == nullptr || length < 20 || length > MaxCacheBytes) return false;

    std::size_t offset = 0;
    std::uint32_t magic = 0, version = 0, count = 0;
    std::int64_t timestamp = 0;
    if (!ReadU32(bytes, length, offset, magic) || magic != Magic ||
        !ReadU32(bytes, length, offset, version) || version != Version ||
        !ReadI64(bytes, length, offset, timestamp) || timestamp <= 0 ||
        !ReadU32(bytes, length, offset, count) || count > MaxEntries) return false;
    if (timestamp > nowFileTime && timestamp - nowFileTime > FutureToleranceTicks) return false;
    if (nowFileTime > timestamp && nowFileTime - timestamp > MaxAgeTicks) return false;

    try {
        std::vector<StatusEntry> parsed;
        parsed.reserve(count);
        for (std::uint32_t i = 0; i < count; ++i) {
            std::uint32_t rawState = 0, pathUnits = 0;
            if (!ReadU32(bytes, length, offset, rawState) || rawState < 1 || rawState > 3 ||
                !ReadU32(bytes, length, offset, pathUnits) || pathUnits < 3 || pathUnits > MaxPathUnits ||
                offset > length || static_cast<std::size_t>(pathUnits) > (length - offset) / sizeof(wchar_t)) return false;

            std::wstring path;
            path.resize(pathUnits);
            for (std::uint32_t c = 0; c < pathUnits; ++c) {
                path[c] = static_cast<wchar_t>(bytes[offset] | (static_cast<std::uint16_t>(bytes[offset + 1]) << 8));
                if (path[c] == L'\0') return false;
                if (path[c] >= 0xD800 && path[c] <= 0xDBFF) {
                    if (c + 1 >= pathUnits) return false;
                    const auto next = static_cast<wchar_t>(bytes[offset + 2] | (static_cast<std::uint16_t>(bytes[offset + 3]) << 8));
                    if (next < 0xDC00 || next > 0xDFFF) return false;
                } else if (path[c] >= 0xDC00 && path[c] <= 0xDFFF &&
                           (c == 0 || path[c - 1] < 0xD800 || path[c - 1] > 0xDBFF)) {
                    return false;
                }
                offset += sizeof(wchar_t);
            }
            if (!IsAbsoluteWindowsPath(path)) return false;
            parsed.push_back({ static_cast<OverlayState>(rawState), std::move(path) });
        }
        if (offset != length) return false;
        std::sort(parsed.begin(), parsed.end(), [](const StatusEntry& left, const StatusEntry& right) {
            return ComparePaths(left.path, right.path) == CSTR_LESS_THAN;
        });
        for (std::size_t i = 1; i < parsed.size(); ++i) {
            if (ComparePaths(parsed[i - 1].path, parsed[i].path) == CSTR_EQUAL) return false;
        }
        entries.swap(parsed);
        return true;
    } catch (...) {
        entries.clear();
        return false;
    }
}

StatusCache::StatusCache() : path_(DefaultCachePath()) {}

StatusCache& StatusCache::Instance() {
    static StatusCache cache;
    return cache;
}

void StatusCache::ReloadIfNeeded() noexcept {
    const ULONGLONG nowTick = GetTickCount64();
    if (lastReloadTick_ != 0 && nowTick - lastReloadTick_ < ReloadIntervalMs) return;
    lastReloadTick_ = nowTick;
    entries_.clear(); // Missing, unreadable, stale or malformed data always removes old badges.

    std::vector<std::uint8_t> bytes;
    if (!ReadCacheFile(path_, bytes)) return;
    FILETIME now{};
    GetSystemTimeAsFileTime(&now);
    const std::int64_t nowFileTime = (static_cast<std::int64_t>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
    std::vector<StatusEntry> decoded;
    try {
        if (DecodeStatusCache(bytes.data(), bytes.size(), nowFileTime, decoded)) entries_.swap(decoded);
    } catch (...) {
        entries_.clear();
    }
}

bool StatusCache::Contains(OverlayState state, const wchar_t* path) noexcept {
    if (path == nullptr || *path == L'\0') return false;
    AcquireSRWLockExclusive(&lock_);
    bool found = false;
    try {
        ReloadIfNeeded();
        const auto match = std::lower_bound(entries_.begin(), entries_.end(), path,
            [](const StatusEntry& entry, const wchar_t* candidate) {
                return CompareStringOrdinal(entry.path.c_str(), static_cast<int>(entry.path.size()), candidate, -1, TRUE) == CSTR_LESS_THAN;
            });
        found = match != entries_.end() && match->state == state &&
            CompareStringOrdinal(match->path.c_str(), static_cast<int>(match->path.size()), path, -1, TRUE) == CSTR_EQUAL;
    } catch (...) { found = false; }
    ReleaseSRWLockExclusive(&lock_);
    return found;
}

#ifdef MOAVCS_SHELL_TEST
void StatusCache::SetPathForTest(const wchar_t* path) {
    AcquireSRWLockExclusive(&lock_);
    path_ = path == nullptr ? L"" : path;
    entries_.clear();
    lastReloadTick_ = 0;
    ReleaseSRWLockExclusive(&lock_);
}
#endif

} // namespace moavcs::shell
