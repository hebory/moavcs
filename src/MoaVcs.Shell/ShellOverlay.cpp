#include "ShellOverlay.h"
#include "OverlayCache.h"

#include <ShlObj.h>
#include <atomic>
#include <new>
#include <string>

namespace {
using moavcs::shell::OverlayState;
std::atomic<long> g_objectCount{ 0 };

HRESULT CopyModulePath(wchar_t* target, int capacity) noexcept {
    if (target == nullptr || capacity <= 0) return E_INVALIDARG;
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&DllGetClassObject), &module)) return HRESULT_FROM_WIN32(GetLastError());
    const DWORD length = GetModuleFileNameW(module, target, static_cast<DWORD>(capacity));
    if (length == 0) return HRESULT_FROM_WIN32(GetLastError());
    if (length >= static_cast<DWORD>(capacity)) {
        target[0] = L'\0';
        return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    }
    return S_OK;
}

class Overlay final : public IShellIconOverlayIdentifier {
public:
    Overlay(OverlayState state, int index) noexcept : state_(state), index_(index) {
        g_objectCount.fetch_add(1, std::memory_order_relaxed);
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (IsEqualIID(iid, IID_IUnknown) || IsEqualIID(iid, __uuidof(IShellIconOverlayIdentifier))) {
            *object = static_cast<IShellIconOverlayIdentifier*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override {
        return static_cast<ULONG>(references_.fetch_add(1, std::memory_order_relaxed) + 1);
    }

    ULONG STDMETHODCALLTYPE Release() override {
        const long remaining = references_.fetch_sub(1, std::memory_order_acq_rel) - 1;
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }

    HRESULT STDMETHODCALLTYPE IsMemberOf(LPCWSTR path, DWORD) override {
        if (path == nullptr || *path == L'\0') return S_FALSE;
        try {
            return moavcs::shell::StatusCache::Instance().Contains(state_, path) ? S_OK : S_FALSE;
        } catch (...) { return S_FALSE; }
    }

    HRESULT STDMETHODCALLTYPE GetOverlayInfo(LPWSTR iconFile, int capacity, int* index, DWORD* flags) override {
        if (iconFile == nullptr || capacity <= 0 || index == nullptr || flags == nullptr) return E_INVALIDARG;
        const HRESULT result = CopyModulePath(iconFile, capacity);
        if (FAILED(result)) return result;
        *index = index_;
        *flags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetPriority(int* priority) override {
        if (priority == nullptr) return E_POINTER;
        *priority = index_;
        return S_OK;
    }

private:
    ~Overlay() { g_objectCount.fetch_sub(1, std::memory_order_relaxed); }
    std::atomic<long> references_{ 1 };
    OverlayState state_;
    int index_;
};

class ClassFactory final : public IClassFactory {
public:
    ClassFactory(OverlayState state, int index) noexcept : state_(state), index_(index) {
        g_objectCount.fetch_add(1, std::memory_order_relaxed);
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (IsEqualIID(iid, IID_IUnknown) || IsEqualIID(iid, IID_IClassFactory)) {
            *object = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override {
        return static_cast<ULONG>(references_.fetch_add(1, std::memory_order_relaxed) + 1);
    }

    ULONG STDMETHODCALLTYPE Release() override {
        const long remaining = references_.fetch_sub(1, std::memory_order_acq_rel) - 1;
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** object) override {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        try {
            auto instance = new Overlay(state_, index_);
            const HRESULT result = instance->QueryInterface(iid, object);
            instance->Release();
            return result;
        } catch (...) { return E_OUTOFMEMORY; }
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock) g_objectCount.fetch_add(1, std::memory_order_relaxed);
        else g_objectCount.fetch_sub(1, std::memory_order_relaxed);
        return S_OK;
    }

private:
    ~ClassFactory() { g_objectCount.fetch_sub(1, std::memory_order_relaxed); }
    std::atomic<long> references_{ 1 };
    OverlayState state_;
    int index_;
};

bool FindClass(REFCLSID clsid, OverlayState& state, int& index) noexcept {
    if (IsEqualCLSID(clsid, CLSID_MoaVcsOverlayConflict)) { state = OverlayState::Conflict; index = 0; return true; }
    if (IsEqualCLSID(clsid, CLSID_MoaVcsOverlayModified)) { state = OverlayState::Modified; index = 1; return true; }
    if (IsEqualCLSID(clsid, CLSID_MoaVcsOverlayUntracked)) { state = OverlayState::Untracked; index = 2; return true; }
    return false;
}
} // namespace

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID classId, REFIID interfaceId, void** object) {
    if (object == nullptr) return E_POINTER;
    *object = nullptr;
    OverlayState state{};
    int index = -1;
    if (!FindClass(classId, state, index)) return CLASS_E_CLASSNOTAVAILABLE;
    try {
        auto factory = new ClassFactory(state, index);
        const HRESULT result = factory->QueryInterface(interfaceId, object);
        factory->Release();
        return result;
    } catch (...) { return E_OUTOFMEMORY; }
}

extern "C" HRESULT __stdcall DllCanUnloadNow() {
    return g_objectCount.load(std::memory_order_acquire) == 0 ? S_OK : S_FALSE;
}

#ifdef MOAVCS_SHELL_TEST
extern "C" void __stdcall MoaVcsSetTestCachePath(const wchar_t* path) {
    moavcs::shell::StatusCache::Instance().SetPathForTest(path);
}
#endif
