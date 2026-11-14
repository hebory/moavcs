#![allow(non_snake_case, clippy::missing_safety_doc)]
//! Minimal in-process Explorer overlay COM server. All VCS work is done in the
//! app's background worker; these callbacks only query the bounded local cache.

#[cfg(windows)]
mod win {
    use moavcs_core::overlay::{OverlayState, StatusCache};
    use std::{
        ffi::c_void,
        path::PathBuf,
        ptr,
        sync::{
            atomic::{AtomicI32, AtomicU32, Ordering},
            Mutex, OnceLock,
        },
    };
    use windows::{
        core::GUID,
        Win32::{
            Foundation::{GetLastError, HMODULE},
            System::LibraryLoader::{
                GetModuleFileNameW, GetModuleHandleExW, GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            },
        },
    };

    type HResult = i32;
    type ULong = u32;
    const S_OK: HResult = 0;
    const S_FALSE: HResult = 1;
    const E_NOINTERFACE: HResult = 0x80004002u32 as i32;
    const E_POINTER: HResult = 0x80004003u32 as i32;
    const E_INVALIDARG: HResult = 0x80070057u32 as i32;
    const CLASS_E_NOAGGREGATION: HResult = 0x80040110u32 as i32;
    const CLASS_E_CLASSNOTAVAILABLE: HResult = 0x80040111u32 as i32;
    const ISIOI_ICONFILE: u32 = 1;
    const ISIOI_ICONINDEX: u32 = 2;
    const CLSID_CONFLICT: GUID = GUID::from_u128(0xa25e0b81_8c4e_4c9b_9d3a_df43085c2b11);
    const CLSID_MODIFIED: GUID = GUID::from_u128(0xb7da0b46_1295_42b0_b3a7_3d55f6e83f22);
    const CLSID_UNTRACKED: GUID = GUID::from_u128(0xcf3e2a97_e680_49b8_a2c4_7a9f65c4d833);
    const IID_IUNKNOWN: GUID = GUID::from_u128(0x00000000_0000_0000_c000_000000000046);
    const IID_ICLASS_FACTORY: GUID = GUID::from_u128(0x00000001_0000_0000_c000_000000000046);
    const IID_OVERLAY: GUID = GUID::from_u128(0x0c6c4200_c589_11d0_999a_00c04fd655e1);

    type QueryInterface =
        unsafe extern "system" fn(*mut c_void, *const GUID, *mut *mut c_void) -> HResult;
    type AddRef = unsafe extern "system" fn(*mut c_void) -> ULong;
    type Release = unsafe extern "system" fn(*mut c_void) -> ULong;
    #[repr(C)]
    struct UnknownVtable {
        query: QueryInterface,
        add_ref: AddRef,
        release: Release,
    }
    #[repr(C)]
    struct OverlayVtable {
        base: UnknownVtable,
        is_member: unsafe extern "system" fn(*mut Overlay, LPCWSTR, u32) -> HResult,
        get_info:
            unsafe extern "system" fn(*mut Overlay, LPWSTR, i32, *mut i32, *mut u32) -> HResult,
        get_priority: unsafe extern "system" fn(*mut Overlay, *mut i32) -> HResult,
    }
    #[repr(C)]
    struct FactoryVtable {
        base: UnknownVtable,
        create: unsafe extern "system" fn(
            *mut Factory,
            *mut c_void,
            *const GUID,
            *mut *mut c_void,
        ) -> HResult,
        lock: unsafe extern "system" fn(*mut Factory, i32) -> HResult,
    }
    #[repr(C)]
    struct Overlay {
        vtable: *const OverlayVtable,
        refs: AtomicU32,
        state: OverlayState,
        index: i32,
    }
    #[repr(C)]
    struct Factory {
        vtable: *const FactoryVtable,
        refs: AtomicU32,
        state: OverlayState,
        index: i32,
    }
    type LPCWSTR = *const u16;
    type LPWSTR = *mut u16;
    static OBJECT_COUNT: AtomicI32 = AtomicI32::new(0);
    static SERVER_LOCKS: AtomicI32 = AtomicI32::new(0);
    static OVERLAY_VTABLE: OverlayVtable = OverlayVtable {
        base: UnknownVtable {
            query: overlay_query,
            add_ref: overlay_add_ref,
            release: overlay_release,
        },
        is_member: overlay_is_member,
        get_info: overlay_get_info,
        get_priority: overlay_get_priority,
    };
    static FACTORY_VTABLE: FactoryVtable = FactoryVtable {
        base: UnknownVtable {
            query: factory_query,
            add_ref: factory_add_ref,
            release: factory_release,
        },
        create: factory_create,
        lock: factory_lock,
    };
    static CACHE: OnceLock<Mutex<StatusCache>> = OnceLock::new();

    fn cache() -> &'static Mutex<StatusCache> {
        CACHE.get_or_init(|| Mutex::new(StatusCache::new(default_cache_path())))
    }
    fn default_cache_path() -> PathBuf {
        if let Some(local) = std::env::var_os("LOCALAPPDATA") {
            PathBuf::from(local)
                .join("MoaVcs")
                .join("overlays")
                .join("status.bin")
        } else {
            PathBuf::new()
        }
    }
    unsafe fn guid_eq(left: *const GUID, right: &GUID) -> bool {
        !left.is_null() && *left == *right
    }
    unsafe extern "system" fn overlay_query(
        this: *mut c_void,
        iid: *const GUID,
        out: *mut *mut c_void,
    ) -> HResult {
        if out.is_null() || this.is_null() {
            return E_POINTER;
        }
        *out = ptr::null_mut();
        if !guid_eq(iid, &IID_IUNKNOWN) && !guid_eq(iid, &IID_OVERLAY) {
            return E_NOINTERFACE;
        }
        *out = this;
        overlay_add_ref(this);
        S_OK
    }
    unsafe extern "system" fn overlay_add_ref(this: *mut c_void) -> ULong {
        if this.is_null() {
            return 0;
        }
        let obj = &*(this as *mut Overlay);
        obj.refs.fetch_add(1, Ordering::Relaxed) + 1
    }
    unsafe extern "system" fn overlay_release(this: *mut c_void) -> ULong {
        if this.is_null() {
            return 0;
        }
        let obj = &*(this as *mut Overlay);
        let left = obj.refs.fetch_sub(1, Ordering::AcqRel) - 1;
        if left == 0 {
            drop(Box::from_raw(this as *mut Overlay));
            OBJECT_COUNT.fetch_sub(1, Ordering::AcqRel);
        }
        left
    }
    unsafe extern "system" fn overlay_is_member(
        this: *mut Overlay,
        path: LPCWSTR,
        _flags: u32,
    ) -> HResult {
        if this.is_null() || path.is_null() || *path == 0 {
            return S_FALSE;
        }
        let mut len = 0usize;
        while len < 32768 && *path.add(len) != 0 {
            len += 1;
        }
        if len == 0 || len >= 32768 {
            return S_FALSE;
        }
        let text = String::from_utf16(std::slice::from_raw_parts(path, len));
        let Ok(text) = text else {
            return S_FALSE;
        };
        let Ok(mut cache) = cache().lock() else {
            return S_FALSE;
        };
        if cache.contains((*this).state, &text) {
            S_OK
        } else {
            S_FALSE
        }
    }
    unsafe extern "system" fn overlay_get_info(
        this: *mut Overlay,
        icon: LPWSTR,
        capacity: i32,
        index: *mut i32,
        flags: *mut u32,
    ) -> HResult {
        if this.is_null() || icon.is_null() || index.is_null() || flags.is_null() {
            return E_POINTER;
        }
        if capacity <= 0 {
            return E_INVALIDARG;
        }
        let mut module = HMODULE::default();
        let anchor = DllGetClassObject as *const () as *const u16;
        if let Err(e) = GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            windows::core::PCWSTR(anchor),
            &mut module,
        ) {
            return e.code().0;
        }
        let mut module_path = vec![0u16; capacity as usize];
        let written = GetModuleFileNameW(module, &mut module_path) as usize;
        if written == 0 {
            return GetLastError().0 as i32;
        }
        if written >= capacity as usize {
            *icon = 0;
            return 0x8007007au32 as i32;
        }
        ptr::copy_nonoverlapping(module_path.as_ptr(), icon, written + 1);
        *index = (*this).index;
        *flags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
        S_OK
    }
    unsafe extern "system" fn overlay_get_priority(this: *mut Overlay, out: *mut i32) -> HResult {
        if this.is_null() || out.is_null() {
            return E_POINTER;
        }
        *out = (*this).index;
        S_OK
    }

    unsafe extern "system" fn factory_query(
        this: *mut c_void,
        iid: *const GUID,
        out: *mut *mut c_void,
    ) -> HResult {
        if out.is_null() || this.is_null() {
            return E_POINTER;
        }
        *out = ptr::null_mut();
        if !guid_eq(iid, &IID_IUNKNOWN) && !guid_eq(iid, &IID_ICLASS_FACTORY) {
            return E_NOINTERFACE;
        }
        *out = this;
        factory_add_ref(this);
        S_OK
    }
    unsafe extern "system" fn factory_add_ref(this: *mut c_void) -> ULong {
        if this.is_null() {
            return 0;
        }
        (&*(this as *mut Factory))
            .refs
            .fetch_add(1, Ordering::Relaxed)
            + 1
    }
    unsafe extern "system" fn factory_release(this: *mut c_void) -> ULong {
        if this.is_null() {
            return 0;
        }
        let left = (&*(this as *mut Factory))
            .refs
            .fetch_sub(1, Ordering::AcqRel)
            - 1;
        if left == 0 {
            drop(Box::from_raw(this as *mut Factory));
            OBJECT_COUNT.fetch_sub(1, Ordering::AcqRel);
        }
        left
    }
    unsafe extern "system" fn factory_create(
        this: *mut Factory,
        outer: *mut c_void,
        iid: *const GUID,
        out: *mut *mut c_void,
    ) -> HResult {
        if out.is_null() || this.is_null() {
            return E_POINTER;
        }
        *out = ptr::null_mut();
        if !outer.is_null() {
            return CLASS_E_NOAGGREGATION;
        }
        let overlay = Box::new(Overlay {
            vtable: &OVERLAY_VTABLE,
            refs: AtomicU32::new(1),
            state: (*this).state,
            index: (*this).index,
        });
        OBJECT_COUNT.fetch_add(1, Ordering::AcqRel);
        let raw = Box::into_raw(overlay);
        let hr = overlay_query(raw as *mut c_void, iid, out);
        overlay_release(raw as *mut c_void);
        hr
    }
    unsafe extern "system" fn factory_lock(_this: *mut Factory, lock: i32) -> HResult {
        if lock != 0 {
            SERVER_LOCKS.fetch_add(1, Ordering::AcqRel);
        } else {
            SERVER_LOCKS.fetch_sub(1, Ordering::AcqRel);
        }
        S_OK
    }

    #[no_mangle]
    pub unsafe extern "system" fn DllGetClassObject(
        clsid: *const GUID,
        iid: *const GUID,
        out: *mut *mut c_void,
    ) -> HResult {
        if out.is_null() {
            return E_POINTER;
        }
        *out = ptr::null_mut();
        let (state, index) = if guid_eq(clsid, &CLSID_CONFLICT) {
            (OverlayState::Conflict, 0)
        } else if guid_eq(clsid, &CLSID_MODIFIED) {
            (OverlayState::Modified, 1)
        } else if guid_eq(clsid, &CLSID_UNTRACKED) {
            (OverlayState::Untracked, 2)
        } else {
            return CLASS_E_CLASSNOTAVAILABLE;
        };
        let factory = Box::new(Factory {
            vtable: &FACTORY_VTABLE,
            refs: AtomicU32::new(1),
            state,
            index,
        });
        OBJECT_COUNT.fetch_add(1, Ordering::AcqRel);
        let raw = Box::into_raw(factory);
        let hr = factory_query(raw as *mut c_void, iid, out);
        factory_release(raw as *mut c_void);
        hr
    }
    #[no_mangle]
    pub extern "system" fn DllCanUnloadNow() -> HResult {
        if OBJECT_COUNT.load(Ordering::Acquire) == 0 && SERVER_LOCKS.load(Ordering::Acquire) == 0 {
            S_OK
        } else {
            S_FALSE
        }
    }

    /// Test hook used by an ABI smoke host. It is intentionally outside the COM contract.
    #[no_mangle]
    pub unsafe extern "system" fn MoaVcsSetTestCachePath(path: LPCWSTR) {
        if path.is_null() {
            return;
        }
        let mut len = 0;
        while len < 32768 && *path.add(len) != 0 {
            len += 1;
        }
        if len >= 32768 {
            return;
        }
        if let Ok(text) = String::from_utf16(std::slice::from_raw_parts(path, len)) {
            if let Ok(mut cache) = cache().lock() {
                *cache = StatusCache::new(PathBuf::from(text));
            }
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        #[test]
        fn com_factory_and_overlay_vtables_follow_shell_abi() {
            unsafe {
                assert_eq!(DllCanUnloadNow(), S_OK);
                let mut out = ptr::null_mut();
                assert_eq!(
                    DllGetClassObject(&CLSID_CONFLICT, &IID_ICLASS_FACTORY, &mut out),
                    S_OK
                );
                assert!(!out.is_null());
                assert_eq!(DllCanUnloadNow(), S_FALSE);
                let factory = out as *mut Factory;
                let factory_vtable = (*factory).vtable;
                let mut unknown = ptr::null_mut();
                assert_eq!(
                    ((*factory_vtable).base.query)(out, &IID_IUNKNOWN, &mut unknown),
                    S_OK
                );
                assert_eq!(unknown, out);
                ((*factory_vtable).base.release)(unknown);
                assert_eq!(((*factory_vtable).lock)(factory, 1), S_OK);
                assert_eq!(DllCanUnloadNow(), S_FALSE);
                assert_eq!(((*factory_vtable).lock)(factory, 0), S_OK);
                let mut overlay_ptr = ptr::null_mut();
                assert_eq!(
                    ((*factory_vtable).create)(
                        factory,
                        ptr::null_mut(),
                        &IID_OVERLAY,
                        &mut overlay_ptr
                    ),
                    S_OK
                );
                assert!(!overlay_ptr.is_null());
                assert_eq!(
                    ((*factory_vtable).create)(
                        factory,
                        1usize as *mut c_void,
                        &IID_OVERLAY,
                        &mut unknown
                    ),
                    CLASS_E_NOAGGREGATION
                );
                assert!(unknown.is_null());
                let overlay = overlay_ptr as *mut Overlay;
                let overlay_vtable = (*overlay).vtable;
                let mut priority = -1;
                assert_eq!(
                    ((*overlay_vtable).get_priority)(overlay, &mut priority),
                    S_OK
                );
                assert_eq!(priority, 0);
                let mut icon = vec![0u16; 32768];
                let mut index = -1;
                let mut flags = 0;
                assert_eq!(
                    ((*overlay_vtable).get_info)(
                        overlay,
                        icon.as_mut_ptr(),
                        icon.len() as i32,
                        &mut index,
                        &mut flags
                    ),
                    S_OK
                );
                assert_eq!(index, 0);
                assert_eq!(flags, ISIOI_ICONFILE | ISIOI_ICONINDEX);
                assert_ne!(icon[0], 0);
                assert_eq!(
                    ((*overlay_vtable).get_info)(
                        overlay,
                        icon.as_mut_ptr(),
                        0,
                        &mut index,
                        &mut flags
                    ),
                    E_INVALIDARG
                );
                assert_eq!(
                    ((*overlay_vtable).get_info)(
                        overlay,
                        ptr::null_mut(),
                        icon.len() as i32,
                        &mut index,
                        &mut flags
                    ),
                    E_POINTER
                );
                let cache_path = std::env::temp_dir()
                    .join(format!("moavcs-com-cache-{}.bin", std::process::id()));
                let item_path = r"C:\moavcs-com-test\file.txt";
                let fixture = [moavcs_core::overlay::OverlayEntry {
                    state: OverlayState::Conflict,
                    path: item_path.into(),
                }];
                let mut fixture_bytes = Vec::new();
                assert!(moavcs_core::overlay::write_snapshot(
                    &mut fixture_bytes,
                    moavcs_core::overlay::file_time_now(),
                    &fixture
                )
                .is_ok());
                assert!(std::fs::write(&cache_path, fixture_bytes).is_ok());
                let cache_name = wide_for_test(&cache_path.to_string_lossy());
                MoaVcsSetTestCachePath(cache_name.as_ptr());
                let item = wide_for_test(item_path);
                assert_eq!(
                    ((*overlay_vtable).is_member)(overlay, item.as_ptr(), 0),
                    S_OK
                );
                let nonmember = wide_for_test(r"C:\moavcs-com-test\other.txt");
                assert_eq!(
                    ((*overlay_vtable).is_member)(overlay, nonmember.as_ptr(), 0),
                    S_FALSE
                );
                assert!(std::fs::write(&cache_path, b"bad cache").is_ok());
                let invalid = wide_for_test(&cache_path.to_string_lossy());
                MoaVcsSetTestCachePath(invalid.as_ptr());
                assert_eq!(
                    ((*overlay_vtable).is_member)(overlay, item.as_ptr(), 0),
                    S_FALSE
                );
                assert_eq!(
                    ((*overlay_vtable).is_member)(overlay, ptr::null(), 0),
                    S_FALSE
                );
                assert_eq!(
                    ((*overlay_vtable).base.query)(overlay_ptr, &IID_IUNKNOWN, &mut unknown),
                    S_OK
                );
                ((*overlay_vtable).base.release)(unknown);
                ((*overlay_vtable).base.release)(overlay_ptr);
                ((*factory_vtable).base.release)(out);
                assert_eq!(DllCanUnloadNow(), S_OK);
                let _ = std::fs::remove_file(cache_path);
            }
        }

        fn wide_for_test(value: &str) -> Vec<u16> {
            value.encode_utf16().chain(Some(0)).collect()
        }

        #[test]
        fn com_exports_reject_null_and_unknown_class_arguments() {
            unsafe {
                let mut out = ptr::null_mut();
                assert_eq!(
                    DllGetClassObject(ptr::null(), &IID_ICLASS_FACTORY, &mut out),
                    CLASS_E_CLASSNOTAVAILABLE
                );
                assert_eq!(
                    DllGetClassObject(&CLSID_CONFLICT, &IID_ICLASS_FACTORY, ptr::null_mut()),
                    E_POINTER
                );
                let mut bad = CLSID_CONFLICT;
                bad.data1 ^= 0x100;
                assert_eq!(
                    DllGetClassObject(&bad, &IID_ICLASS_FACTORY, &mut out),
                    CLASS_E_CLASSNOTAVAILABLE
                );
                assert_eq!(
                    DllGetClassObject(&CLSID_CONFLICT, &IID_OVERLAY, &mut out),
                    E_NOINTERFACE
                );
                assert!(out.is_null());
            }
        }
    }
}

#[cfg(windows)]
pub use win::{DllCanUnloadNow, DllGetClassObject};
