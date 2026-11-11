#pragma once

#include <Windows.h>
#include <guiddef.h>

inline constexpr GUID CLSID_MoaVcsOverlayConflict =
    { 0xA25E0B81, 0x8C4E, 0x4C9B, { 0x9D, 0x3A, 0xDF, 0x43, 0x08, 0x5C, 0x2B, 0x11 } };
inline constexpr GUID CLSID_MoaVcsOverlayModified =
    { 0xB7DA0B46, 0x1295, 0x42B0, { 0xB3, 0xA7, 0x3D, 0x55, 0xF6, 0xE8, 0x3F, 0x22 } };
inline constexpr GUID CLSID_MoaVcsOverlayUntracked =
    { 0xCF3E2A97, 0xE680, 0x49B8, { 0xA2, 0xC4, 0x7A, 0x9F, 0x65, 0xC4, 0xD8, 0x33 } };

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID classId, REFIID interfaceId, void** object);
extern "C" HRESULT __stdcall DllCanUnloadNow();

#ifdef MOAVCS_SHELL_TEST
extern "C" void __stdcall MoaVcsSetTestCachePath(const wchar_t* path);
#endif
