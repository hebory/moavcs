#pragma once

#include <Windows.h>
#include <cstdint>
#include <string>
#include <vector>

namespace moavcs::shell {

enum class OverlayState : std::uint32_t {
    Conflict = 1,
    Modified = 2,
    Untracked = 3,
};

struct StatusEntry {
    OverlayState state;
    std::wstring path;
};

// Decodes the on-disk little-endian protocol. A false result always leaves entries empty.
bool DecodeStatusCache(const std::uint8_t* bytes, std::size_t length,
                       std::int64_t nowFileTime, std::vector<StatusEntry>& entries);

class StatusCache final {
public:
    static StatusCache& Instance();
    bool Contains(OverlayState state, const wchar_t* path) noexcept;

#ifdef MOAVCS_SHELL_TEST
    void SetPathForTest(const wchar_t* path);
#endif

private:
    StatusCache();
    void ReloadIfNeeded() noexcept;

    SRWLOCK lock_ = SRWLOCK_INIT;
    std::wstring path_;
    std::vector<StatusEntry> entries_;
    ULONGLONG lastReloadTick_ = 0;
};

} // namespace moavcs::shell
