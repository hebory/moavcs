//! Windows shell integration and the per-user status-cache worker.
//! Explorer's in-process COM callbacks never launch VCS tools; this process owns all scans.

use crate::launch::LaunchOptions;
use moavcs_core::{
    overlay::{self, OverlayEntry, OverlayState},
    settings::{AppSettings, SettingsStore},
    CancelToken,
};
use moavcs_core::{Result, VcsError};
use std::{
    path::{Path, PathBuf},
    process::Command,
    time::Duration,
};

#[cfg(not(windows))]
pub fn install_context_menu(_: &AppSettings) -> Result<()> {
    Err("탐색기 통합은 Windows에서만 사용할 수 있습니다.".into())
}
#[cfg(not(windows))]
pub fn uninstall_context_menu() -> Result<()> {
    Err("탐색기 통합은 Windows에서만 사용할 수 있습니다.".into())
}
#[cfg(not(windows))]
pub fn configure_overlays(_: bool, _: bool) -> Result<()> {
    Err("탐색기 상태 배지는 Windows에서만 사용할 수 있습니다.".into())
}
#[cfg(not(windows))]
pub fn handle_startup(_: &LaunchOptions) -> Result<bool> {
    Ok(false)
}
#[cfg(not(windows))]
pub fn maybe_start_worker(_: &AppSettings) -> Result<()> {
    Ok(())
}

#[cfg(windows)]
mod win {
    use super::*;
    use moavcs_core::create_provider;
    use std::{collections::BTreeMap, ffi::OsStr, os::windows::ffi::OsStrExt, sync::mpsc};
    use windows::{
        core::PCWSTR,
        Win32::{
            Foundation::{CloseHandle, GetLastError, HANDLE},
            Security::{GetTokenInformation, TokenUser, TOKEN_QUERY, TOKEN_USER},
            Storage::FileSystem::{
                GetDriveTypeW, MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
            },
            System::{Registry::*, Threading::*},
            UI::Shell::{
                SHChangeNotify, ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHCNE_ASSOCCHANGED,
                SHCNE_UPDATEITEM, SHCNF_IDLIST, SHCNF_PATHW, SHELLEXECUTEINFOW,
            },
        },
    };

    const OWNER: &str = "MoaVcs.ExplorerMenu.v1";
    const RUN_KEY: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    const RUN_NAME: &str = "MoaVcsStatusCache";
    const SCOPES: [&str; 3] = ["Directory", "Directory\\Background", "*"];
    fn wide(s: &str) -> Vec<u16> {
        OsStr::new(s).encode_wide().chain(Some(0)).collect()
    }
    fn app_exe() -> Result<PathBuf> {
        std::env::current_exe().map_err(Into::into)
    }
    fn reg_error(code: windows::Win32::Foundation::WIN32_ERROR) -> Result<()> {
        if code.0 == 0 {
            Ok(())
        } else {
            Err(VcsError(format!("Windows 레지스트리 오류: {}", code.0)))
        }
    }
    unsafe fn open_or_create(path: &str) -> Result<HKEY> {
        let key = wide(path);
        let mut out = HKEY::default();
        reg_error(RegCreateKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(key.as_ptr()),
            0,
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_READ | KEY_WRITE,
            None,
            &mut out,
            None,
        ))?;
        Ok(out)
    }
    unsafe fn open(path: &str, access: REG_SAM_FLAGS) -> Result<HKEY> {
        let key = wide(path);
        let mut out = HKEY::default();
        reg_error(RegOpenKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(key.as_ptr()),
            0,
            access,
            &mut out,
        ))?;
        Ok(out)
    }
    unsafe fn close(key: HKEY) {
        let _ = RegCloseKey(key);
    }
    unsafe fn set(key: HKEY, name: &str, value: &str) -> Result<()> {
        let n = wide(name);
        let data = wide(value);
        reg_error(RegSetValueExW(
            key,
            PCWSTR(n.as_ptr()),
            0,
            REG_SZ,
            Some(std::slice::from_raw_parts(
                data.as_ptr() as *const u8,
                data.len() * 2,
            )),
        ))
    }
    unsafe fn set_dword(key: HKEY, name: &str, value: u32) -> Result<()> {
        let n = wide(name);
        reg_error(RegSetValueExW(
            key,
            PCWSTR(n.as_ptr()),
            0,
            REG_DWORD,
            Some(value.to_le_bytes().as_slice()),
        ))
    }
    unsafe fn read(key: HKEY, name: &str) -> Option<String> {
        let n = wide(name);
        let mut ty = REG_VALUE_TYPE(0);
        let mut bytes = 0u32;
        if RegQueryValueExW(
            key,
            PCWSTR(n.as_ptr()),
            None,
            Some(&mut ty),
            None,
            Some(&mut bytes),
        )
        .0 != 0
            || ty != REG_SZ
            || bytes < 2
            || bytes > 32768
        {
            return None;
        }
        let mut data = vec![0u16; (bytes as usize + 1) / 2];
        if RegQueryValueExW(
            key,
            PCWSTR(n.as_ptr()),
            None,
            Some(&mut ty),
            Some(data.as_mut_ptr() as *mut u8),
            Some(&mut bytes),
        )
        .0 != 0
        {
            return None;
        }
        Some(String::from_utf16_lossy(
            &data[..data.iter().position(|x| *x == 0).unwrap_or(data.len())],
        ))
    }
    fn check_owner_or_free(path: &str) -> Result<()> {
        unsafe {
            let Ok(k) = open(path, KEY_READ) else {
                return Ok(());
            };
            let owner = read(k, "MoaVcsOwner");
            close(k);
            if owner.as_deref().is_some_and(|v| v == OWNER) {
                Ok(())
            } else {
                Err(format!("탐색기 메뉴 위치가 다른 앱에서 사용 중입니다: {path}").into())
            }
        }
    }
    fn delete_owned(path: &str, expected_exe: Option<&str>) -> Result<()> {
        unsafe {
            let Ok(k) = open(path, KEY_READ) else {
                return Ok(());
            };
            let owned = read(k, "MoaVcsOwner").as_deref() == Some(OWNER);
            let exe = read(k, "MoaVcsExecutable");
            close(k);
            if owned
                && expected_exe
                    .is_none_or(|e| exe.as_deref().is_some_and(|p| p.eq_ignore_ascii_case(e)))
            {
                let p = wide(path);
                let code = RegDeleteTreeW(HKEY_CURRENT_USER, PCWSTR(p.as_ptr()));
                if code.0 != 0 && code.0 != 2 {
                    return reg_error(code);
                }
            }
            Ok(())
        }
    }
    fn executable_marker_matches(target: Option<&str>, expected_exe: &str) -> bool {
        target.is_some_and(|target| {
            overlay::compare_paths(target, expected_exe) == std::cmp::Ordering::Equal
        })
    }
    fn legacy_command_matches(command: Option<&str>, expected_exe: &str) -> bool {
        command.is_some_and(|command| {
            let (actual_exe, remainder) = if quote(expected_exe).starts_with('"') {
                let Some(quoted) = command.strip_prefix('"') else {
                    return false;
                };
                let Some(end) = quoted.find('"') else {
                    return false;
                };
                (&quoted[..end], &quoted[end + 1..])
            } else {
                let Some(end) = command.find(' ') else {
                    return false;
                };
                (&command[..end], &command[end..])
            };
            remainder.starts_with(' ')
                && overlay::compare_paths(actual_exe, expected_exe) == std::cmp::Ordering::Equal
        })
    }
    fn scope_targets_executable(
        target: Option<&str>,
        legacy_command: Option<&str>,
        expected_exe: &str,
    ) -> bool {
        match target {
            Some(target) => executable_marker_matches(Some(target), expected_exe),
            None => legacy_command_matches(legacy_command, expected_exe),
        }
    }
    fn read_value(path: &str, name: &str) -> Option<String> {
        unsafe {
            let Ok(key) = open(path, KEY_READ) else {
                return None;
            };
            let value = read(key, name);
            close(key);
            value
        }
    }
    fn quote(s: &str) -> String {
        if !s.chars().any(char::is_whitespace) && !s.contains('"') {
            return s.into();
        }
        let mut out = String::from("\"");
        let mut slashes = 0;
        for ch in s.chars() {
            if ch == '\\' {
                slashes += 1;
                continue;
            }
            if ch == '"' {
                out.push_str(&"\\".repeat(slashes * 2 + 1));
                out.push('"');
                slashes = 0;
                continue;
            }
            out.push_str(&"\\".repeat(slashes));
            out.push(ch);
            slashes = 0
        }
        out.push_str(&"\\".repeat(slashes * 2));
        out.push('"');
        out
    }
    fn menu_paths(scope: &str) -> (String, String) {
        let command = match scope {
            "Directory\\Background" => "Directory\\ContextMenus\\MoaVcs.BackgroundCommands",
            "*" => "*\\ContextMenus\\MoaVcs.FileCommands",
            _ => "Directory\\ContextMenus\\MoaVcs.Commands",
        };
        (
            format!("Software\\Classes\\{scope}\\shell\\MoaVcs"),
            format!("Software\\Classes\\{command}"),
        )
    }
    fn menu_entries(file: bool) -> Vec<(&'static str, &'static str, &'static str, bool)> {
        if file {
            vec![
                ("10Commit", "커밋…", "commit", false),
                ("20Diff", "차이 비교 (Diff)", "diff", true),
                ("30Log", "로그 보기", "log", true),
                ("40Blame", "행별 이력 (Blame)", "blame", false),
                ("50Status", "변경 사항 확인", "status", false),
                ("60Open", "MoaVCS에서 열기", "open", true),
                ("85Remote", "원격 주소…", "remote", false),
                ("90Settings", "설정…", "settings", true),
            ]
        } else {
            vec![
                ("10Update", "업데이트 / Pull…", "update", false),
                ("20Push", "푸시…", "push", false),
                ("30Commit", "커밋…", "commit", false),
                ("40Log", "로그 보기", "log", true),
                ("50Status", "변경 사항 확인", "status", false),
                ("60Open", "MoaVCS에서 열기", "open", true),
                ("70Clone", "복제 / 체크아웃…", "clone", false),
                ("80Init", "여기에 저장소 만들기…", "init", false),
                ("85Remote", "원격 주소…", "remote", false),
                ("90Settings", "설정…", "settings", true),
            ]
        }
    }
    fn set_menu_value(path: &str, name: &str, value: &str) -> Result<()> {
        unsafe {
            let k = open_or_create(path)?;
            let r = set(k, name, value);
            close(k);
            r
        }
    }
    fn set_menu_dword(path: &str, name: &str, value: u32) -> Result<()> {
        unsafe {
            let k = open_or_create(path)?;
            let r = set_dword(k, name, value);
            close(k);
            r
        }
    }
    fn remove_owned_menus(exe: Option<&str>) -> Result<()> {
        for scope in SCOPES {
            for action in if scope == "*" {
                vec!["commit"]
            } else {
                vec!["commit", "update"]
            } {
                let name = if action == "commit" {
                    "Commit"
                } else {
                    "Update"
                };
                delete_owned(
                    &format!("Software\\Classes\\{scope}\\shell\\MoaVcs.{name}"),
                    exe,
                )?;
            }
        }
        let scopes: Vec<_> = SCOPES
            .iter()
            .copied()
            .filter(|scope| exe.is_none_or(|exe| command_targets_executable(scope, exe)))
            .collect();
        for scope in scopes {
            let (menu, commands) = menu_paths(scope);
            // The parent menu has no executable marker. Select the whole scope
            // using its command-root marker, then delete only MoaVCS-owned keys.
            delete_owned(&menu, None)?;
            delete_owned(&commands, None)?;
        }
        Ok(())
    }
    fn command_targets_executable(scope: &str, expected_exe: &str) -> bool {
        let (_, commands) = menu_paths(scope);
        let target = read_value(&commands, "MoaVcsExecutable");
        let legacy_command = format!("{commands}\\Shell\\Open\\command");
        let legacy = if target.is_none() {
            read_value(&legacy_command, "")
        } else {
            None
        };
        scope_targets_executable(target.as_deref(), legacy.as_deref(), expected_exe)
    }
    pub fn install_context_menu(settings: &AppSettings) -> Result<()> {
        let exe = app_exe()?;
        let exe = exe.to_string_lossy().into_owned();
        for scope in SCOPES {
            let (menu, commands) = menu_paths(scope);
            check_owner_or_free(&menu)?;
            check_owner_or_free(&commands)?;
            for action in if scope == "*" {
                vec!["Commit"]
            } else {
                vec!["Commit", "Update"]
            } {
                check_owner_or_free(&format!(
                    "Software\\Classes\\{scope}\\shell\\MoaVcs.{action}"
                ))?;
            }
        }
        remove_owned_menus(None)?;
        for scope in SCOPES {
            let (menu, commands) = menu_paths(scope);
            let command_key = match scope {
                "Directory\\Background" => "Directory\\ContextMenus\\MoaVcs.BackgroundCommands",
                "*" => "*\\ContextMenus\\MoaVcs.FileCommands",
                _ => "Directory\\ContextMenus\\MoaVcs.Commands",
            };
            let root_icon = exe
                .rsplit_once('\\')
                .map(|(base, _)| format!("{}\\icons\\explorer\\moavcs.ico", base))
                .unwrap_or_else(|| exe.clone());
            for (path, name, val) in [
                (&menu, "MUIVerb", "MoaVCS"),
                (&menu, "MoaVcsOwner", OWNER),
                (&menu, "ExtendedSubCommandsKey", command_key),
                (&menu, "Icon", &quote(&root_icon)),
                (&commands, "MoaVcsOwner", OWNER),
                (&commands, "MoaVcsExecutable", &exe),
            ] {
                set_menu_value(path, name, val)?;
            }
            if scope != "Directory\\Background" {
                set_menu_value(&menu, "MultiSelectModel", "Single")?;
            }
            let mut first = true;
            for (id, label, action, separator) in menu_entries(scope == "*") {
                let promoted = (action == "commit"
                    && settings.explorer_menu.show_commit_at_top_level)
                    || (action == "update" && settings.explorer_menu.show_update_at_top_level);
                let verb = if promoted {
                    format!(
                        "Software\\Classes\\{scope}\\shell\\MoaVcs.{}",
                        if action == "commit" {
                            "Commit"
                        } else {
                            "Update"
                        }
                    )
                } else {
                    format!("{commands}\\Shell\\{id}")
                };
                let verb_label = if promoted {
                    format!("MoaVCS {label}")
                } else {
                    label.to_owned()
                };
                set_menu_value(&verb, "MUIVerb", &verb_label)?;
                let icon = exe
                    .rsplit_once('\\')
                    .map(|(base, _)| format!("{}\\icons\\explorer\\{action}.ico", base))
                    .unwrap_or_else(|| exe.clone());
                set_menu_value(&verb, "Icon", &quote(&icon))?;
                if scope != "Directory\\Background" {
                    set_menu_value(&verb, "MultiSelectModel", "Single")?;
                }
                if promoted {
                    set_menu_value(&verb, "MoaVcsOwner", OWNER)?;
                    set_menu_value(&verb, "MoaVcsExecutable", &exe)?;
                } else {
                    if separator && !first {
                        set_menu_dword(&verb, "CommandFlags", 32)?;
                    }
                    first = false;
                }
                let target = if scope == "Directory\\Background" {
                    "%V"
                } else {
                    "%1"
                };
                set_menu_value(
                    &format!("{verb}\\command"),
                    "",
                    &format!(
                        "{} --shell-path \"{}|\" --action {action}",
                        quote(&exe),
                        target
                    ),
                )?;
            }
        }
        unsafe {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, None, None);
        }
        Ok(())
    }
    pub fn uninstall_context_menu() -> Result<()> {
        let exe = app_exe()?;
        let exe = exe.to_string_lossy().into_owned();
        remove_owned_menus(Some(&exe))?;
        unsafe {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, None, None);
        }
        Ok(())
    }

    fn object_prefix() -> String {
        unsafe {
            let mut token = HANDLE::default();
            if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).is_err() {
                return "Local\\MoaVcs.Overlays.unknown".into();
            }
            let mut needed = 0;
            let first = GetTokenInformation(token, TokenUser, None, 0, &mut needed);
            if first.is_ok() || needed == 0 {
                let _ = CloseHandle(token);
                return "Local\\MoaVcs.Overlays.unknown".into();
            }
            let mut data = vec![0u128; (needed as usize + 15) / 16];
            if GetTokenInformation(
                token,
                TokenUser,
                Some(data.as_mut_ptr() as *mut _),
                needed,
                &mut needed,
            )
            .is_err()
            {
                let _ = CloseHandle(token);
                return "Local\\MoaVcs.Overlays.unknown".into();
            }
            let user = &*(data.as_ptr() as *const TOKEN_USER);
            let sid = user.User.Sid.0 as *const u8;
            if sid.is_null() {
                let _ = CloseHandle(token);
                return "Local\\MoaVcs.Overlays.unknown".into();
            }
            let count = *sid.add(1) as usize;
            let authority = std::slice::from_raw_parts(sid.add(2), 6)
                .iter()
                .fold(0u64, |acc, b| (acc << 8) | *b as u64);
            let mut name = format!("Local\\MoaVcs.Overlays.S-{}-{}", *sid, authority);
            for i in 0..count {
                name.push('-');
                name.push_str(
                    &u32::from_le_bytes([
                        *sid.add(8 + i * 4),
                        *sid.add(9 + i * 4),
                        *sid.add(10 + i * 4),
                        *sid.add(11 + i * 4),
                    ])
                    .to_string(),
                );
            }
            let _ = CloseHandle(token);
            name
        }
    }
    fn signal(suffix: &str) {
        unsafe {
            let name = wide(&format!("{}.{suffix}", object_prefix()));
            if let Ok(h) = OpenEventW(EVENT_MODIFY_STATE, false, PCWSTR(name.as_ptr())) {
                let _ = SetEvent(h);
                let _ = CloseHandle(h);
            }
        }
    }
    fn wait_worker_stopped() -> Result<()> {
        let name = wide(&format!("{}.worker", object_prefix()));
        for _ in 0..50 {
            unsafe {
                if let Ok(handle) =
                    OpenMutexW(SYNCHRONIZATION_SYNCHRONIZE, false, PCWSTR(name.as_ptr()))
                {
                    let _ = CloseHandle(handle);
                } else {
                    return Ok(());
                }
            }
            std::thread::sleep(Duration::from_millis(100));
        }
        Err("상태 확인 프로세스가 아직 종료 중입니다. 잠시 후 다시 시도하세요.".into())
    }
    fn startup_command(exe: &Path) -> String {
        format!("{} --overlay-worker", quote(&exe.to_string_lossy()))
    }
    fn startup_owner(exe: &Path) -> bool {
        unsafe {
            let Ok(k) = open(RUN_KEY, KEY_READ) else {
                return false;
            };
            let found = read(k, RUN_NAME);
            close(k);
            found.is_some_and(|v| v.eq_ignore_ascii_case(&startup_command(exe)))
        }
    }
    fn startup_value() -> Option<String> {
        unsafe {
            let Ok(k) = open(RUN_KEY, KEY_READ) else {
                return None;
            };
            let result = read(k, RUN_NAME);
            close(k);
            result
        }
    }
    fn save_overlay(enabled: bool, priority: Option<bool>) -> Result<()> {
        let store = SettingsStore::default();
        store.update(|s| {
            s.explorer_overlays_enabled = enabled;
            if let Some(p) = priority {
                s.explorer_overlays_prefer_priority = p;
            }
            Ok(())
        })?;
        Ok(())
    }
    fn install_native_shell(setup: &Path, priority: bool) -> Result<()> {
        let file = wide(&setup.to_string_lossy());
        let verb = wide("runas");
        let args = wide(&format!(
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PRIORITY={}",
            if priority { "1" } else { "0" }
        ));
        unsafe {
            let mut info = SHELLEXECUTEINFOW::default();
            info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
            info.fMask = SEE_MASK_NOCLOSEPROCESS;
            info.lpVerb = PCWSTR(verb.as_ptr());
            info.lpFile = PCWSTR(file.as_ptr());
            info.lpParameters = PCWSTR(args.as_ptr());
            info.nShow = 0;
            ShellExecuteExW(&mut info).map_err(|e| {
                VcsError(format!(
                    "상태 배지 설치를 관리자 권한으로 시작하지 못했습니다: {e}"
                ))
            })?;
            if info.hProcess.is_invalid() {
                return Err("상태 배지 설치 프로그램의 프로세스를 열지 못했습니다.".into());
            }
            let _ = WaitForSingleObject(info.hProcess, u32::MAX);
            let mut code = 1u32;
            let exit = GetExitCodeProcess(info.hProcess, &mut code);
            let _ = CloseHandle(info.hProcess);
            exit.map_err(|e| VcsError(e.to_string()))?;
            if code != 0 {
                Err(format!("상태 배지 설치 프로그램이 종료 코드 {code}를 반환했습니다.").into())
            } else {
                Ok(())
            }
        }
    }
    pub fn configure_overlays(enabled: bool, prefer_priority: bool) -> Result<()> {
        let exe = app_exe()?;
        if startup_value().is_some() && !startup_owner(&exe) {
            return Err(
                "다른 설치본이 상태 배지를 관리하고 있습니다. 해당 설치본의 설정에서 먼저 끄세요."
                    .into(),
            );
        }
        let store = SettingsStore::default();
        let before = store.load()?;
        let shell = std::env::current_exe()?
            .parent()
            .unwrap_or(Path::new("."))
            .join("shell/MoaVcsShellSetup.exe");
        if enabled && !shell.exists() {
            return Err(format!(
                "상태 배지 설치 파일을 찾을 수 없습니다: {}",
                shell.display()
            )
            .into());
        }
        if enabled {
            install_native_shell(&shell, prefer_priority)?;
            signal("stop");
            save_overlay(true, Some(prefer_priority))?;
            let registered = unsafe {
                let k = open_or_create(RUN_KEY)?;
                let command = startup_command(&exe);
                let r = set(k, RUN_NAME, &command);
                close(k);
                r
            };
            if let Err(error) = registered {
                let _ = store.update(|s| {
                    s.explorer_overlays_enabled = before.explorer_overlays_enabled;
                    s.explorer_overlays_prefer_priority = before.explorer_overlays_prefer_priority;
                    Ok(())
                });
                return Err(error);
            }
            if let Err(error) = Command::new(&exe).arg("--overlay-worker").spawn() {
                if startup_owner(&exe) {
                    unsafe {
                        if let Ok(k) = open(RUN_KEY, KEY_WRITE) {
                            let n = wide(RUN_NAME);
                            let _ = RegDeleteValueW(k, PCWSTR(n.as_ptr()));
                            close(k);
                        }
                    }
                }
                let _ = store.update(|s| {
                    s.explorer_overlays_enabled = before.explorer_overlays_enabled;
                    s.explorer_overlays_prefer_priority = before.explorer_overlays_prefer_priority;
                    Ok(())
                });
                return Err(error.into());
            }
        } else {
            save_overlay(false, None)?;
            if startup_owner(&exe) {
                unsafe {
                    let k = open(RUN_KEY, KEY_WRITE)?;
                    let n = wide(RUN_NAME);
                    let _ = RegDeleteValueW(k, PCWSTR(n.as_ptr()));
                    close(k);
                }
            }
            signal("stop");
            let _ = std::fs::remove_file(cache_path());
            unsafe {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, None, None);
            }
        }
        Ok(())
    }
    fn cache_path() -> PathBuf {
        std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(std::env::temp_dir)
            .join("MoaVcs/overlays/status.bin")
    }
    fn notify_changed_path(path: &str) {
        let p = wide(path);
        unsafe {
            SHChangeNotify(
                SHCNE_UPDATEITEM,
                SHCNF_PATHW,
                Some(p.as_ptr() as *const _),
                None,
            );
        }
    }
    pub fn maybe_start_worker(settings: &AppSettings) -> Result<()> {
        if settings.explorer_overlays_enabled {
            let exe = app_exe()?;
            if startup_owner(&exe) {
                let _ = Command::new(exe).arg("--overlay-worker").spawn()?;
            }
        }
        Ok(())
    }
    pub fn handle_startup(options: &LaunchOptions) -> Result<bool> {
        if options.install_context_menu {
            install_context_menu(&SettingsStore::default().load()?)?;
            return Ok(true);
        }
        if options.uninstall_context_menu {
            uninstall_context_menu()?;
            return Ok(true);
        }
        match options.overlay.as_deref() {
            Some("--overlay-worker") => {
                run_worker()?;
                Ok(true)
            }
            Some("--pause-overlays") => {
                if startup_owner(&app_exe()?) {
                    signal("stop");
                    wait_worker_stopped()?;
                }
                Ok(true)
            }
            Some("--resume-overlays") => {
                maybe_start_worker(&SettingsStore::default().load()?)?;
                Ok(true)
            }
            Some("--disable-overlays") => {
                configure_overlays(false, false)?;
                signal("stop");
                wait_worker_stopped()?;
                Ok(true)
            }
            _ => Ok(false),
        }
    }
    fn run_worker() -> Result<()> {
        let prefix = object_prefix();
        let mn = wide(&format!("{prefix}.worker"));
        unsafe {
            let mutex = CreateMutexW(None, false, PCWSTR(mn.as_ptr()))
                .map_err(|e| VcsError(e.to_string()))?;
            if GetLastError().0 == 183 {
                let _ = CloseHandle(mutex);
                return Ok(());
            }
            let sn = wide(&format!("{prefix}.stop"));
            let wn = wide(&format!("{prefix}.wake"));
            let stop = CreateEventW(None, true, false, PCWSTR(sn.as_ptr()))
                .map_err(|e| VcsError(e.to_string()))?;
            let wake = CreateEventW(None, false, false, PCWSTR(wn.as_ptr()))
                .map_err(|e| VcsError(e.to_string()))?;
            let _ = ResetEvent(stop);
            let exe = app_exe()?;
            loop {
                if WaitForSingleObject(stop, 0).0 == 0
                    || !startup_owner(&exe)
                    || !SettingsStore::default().load()?.explorer_overlays_enabled
                {
                    let _ = std::fs::remove_file(cache_path());
                    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, None, None);
                    let _ = CloseHandle(stop);
                    let _ = CloseHandle(wake);
                    let _ = CloseHandle(mutex);
                    return Ok(());
                }
                scan_and_write(stop)?;
                let handles = [stop, wake];
                if WaitForMultipleObjects(&handles, false, 10_000).0 == 0 {
                    continue;
                }
            }
        }
    }
    fn scan_and_write(stop: HANDLE) -> Result<()> {
        let settings = SettingsStore::default().load()?;
        let repositories: Vec<_> = settings
            .recent_repositories
            .iter()
            .take(12)
            .filter_map(|r| {
                let root = PathBuf::from(&r.path);
                let text = root.to_string_lossy();
                let drive = text.get(..3).unwrap_or("");
                let drive_w = wide(drive);
                let fixed =
                    unsafe { !drive.is_empty() && GetDriveTypeW(PCWSTR(drive_w.as_ptr())) == 3 };
                (root.is_dir() && fixed).then_some((
                    root,
                    r.vcs_kind,
                    settings
                        .executable_paths
                        .get(&r.vcs_kind)
                        .cloned()
                        .filter(|v| !v.is_empty())
                        .unwrap_or_else(|| r.vcs_kind.executable().into()),
                ))
            })
            .collect();
        let scan_cancel = CancelToken::new();
        let monitor_cancel = scan_cancel.clone();
        let done = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
        let monitor_done = done.clone();
        // Win32 HANDLE is a raw pointer and is not Send. The worker owns the
        // handle for this call and joins the monitor before it returns, so pass
        // its pointer-sized value and reconstruct it only inside that thread.
        let stop_value = stop.0 as usize;
        let monitor = std::thread::spawn(move || unsafe {
            let stop = HANDLE(stop_value as *mut std::ffi::c_void);
            while !monitor_done.load(std::sync::atomic::Ordering::Acquire) {
                if WaitForSingleObject(stop, 100).0 == 0 {
                    monitor_cancel.cancel();
                    break;
                }
            }
        });
        let mut results: Vec<(PathBuf, Vec<OverlayEntry>)> = Vec::new();
        for batch in repositories.chunks(4) {
            if scan_cancel.cancelled() {
                break;
            }
            let mut pending = Vec::new();
            for (root, kind, exe) in batch {
                let root = root.clone();
                let kind = *kind;
                let exe = exe.clone();
                let cancel = scan_cancel.clone();
                let worker_cancel = cancel.clone();
                let (tx, rx) = mpsc::channel();
                std::thread::spawn(move || {
                    let result = (|| {
                        let provider = create_provider(kind, exe);
                        let repo = provider.repository(&root, &worker_cancel)?;
                        if !same_path(&repo.root.to_string_lossy(), &root.to_string_lossy()) {
                            return Err(VcsError("저장소 루트가 변경되었습니다.".into()));
                        }
                        let changes = provider.status(&repo.root, &worker_cancel)?;
                        overlay::build_snapshot(&repo.root, &changes)
                            .map(|entries| (repo.root, entries))
                    })();
                    let _ = tx.send(result);
                });
                pending.push((
                    cancel,
                    rx,
                    std::time::Instant::now() + Duration::from_secs(20),
                ));
            }
            for (cancel, rx, deadline) in pending {
                match rx.recv_timeout(deadline.saturating_duration_since(std::time::Instant::now()))
                {
                    Ok(Ok(item)) => results.push(item),
                    Ok(Err(_)) => {}
                    Err(mpsc::RecvTimeoutError::Timeout) => cancel.cancel(),
                    Err(mpsc::RecvTimeoutError::Disconnected) => {}
                }
            }
        }
        done.store(true, std::sync::atomic::Ordering::Release);
        let _ = monitor.join();
        if scan_cancel.cancelled() {
            return Ok(());
        }
        let roots: Vec<_> = results.iter().map(|(root, _)| root.clone()).collect();
        let mut merged: BTreeMap<OrdinalPath, OverlayEntry> = BTreeMap::new();
        for (root, items) in &results {
            for entry in items {
                let path = PathBuf::from(&entry.path);
                if roots.iter().any(|nested| {
                    !same_path(&nested.to_string_lossy(), &root.to_string_lossy())
                        && overlay::path_is_within(root, nested)
                        && overlay::path_is_within(nested, &path)
                }) {
                    continue;
                }
                let key = OrdinalPath(entry.path.clone());
                let rank = |s: OverlayState| match s {
                    OverlayState::Conflict => 0,
                    OverlayState::Modified => 1,
                    OverlayState::Untracked => 2,
                };
                merged
                    .entry(key)
                    .and_modify(|old| {
                        if rank(entry.state) < rank(old.state) {
                            old.state = entry.state
                        }
                    })
                    .or_insert_with(|| entry.clone());
            }
        }
        let entries: Vec<_> = merged.into_values().collect();
        let path = cache_path();
        let before = std::fs::File::open(&path)
            .ok()
            .and_then(|f| {
                overlay::read_snapshot(f, overlay::file_time_now())
                    .ok()
                    .map(|(_, v)| v)
            })
            .unwrap_or_default();
        if let Some(p) = path.parent() {
            std::fs::create_dir_all(p)?;
        }
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        let temp = path.with_extension(format!("{}.{}.tmp", std::process::id(), nonce));
        let mut file = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)?;
        overlay::write_snapshot(&mut file, overlay::file_time_now(), &entries)?;
        use std::io::Write;
        file.flush()?;
        file.sync_all()?;
        drop(file);
        let from = wide(&temp.to_string_lossy());
        let to = wide(&path.to_string_lossy());
        if let Err(error) = unsafe {
            MoveFileExW(
                PCWSTR(from.as_ptr()),
                PCWSTR(to.as_ptr()),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
        } {
            let _ = std::fs::remove_file(temp);
            return Err(VcsError(error.to_string()));
        }
        let before_map: BTreeMap<OrdinalPath, OverlayState> = before
            .into_iter()
            .map(|e| (OrdinalPath(e.path), e.state))
            .collect();
        let after_map: BTreeMap<OrdinalPath, OverlayState> = entries
            .into_iter()
            .map(|e| (OrdinalPath(e.path), e.state))
            .collect();
        let mut changed = Vec::new();
        for key in before_map.keys().chain(after_map.keys()) {
            if before_map.get(key) != after_map.get(key) {
                if let Some((path, _)) = after_map.get_key_value(key) {
                    changed.push(path.0.clone());
                } else if let Some((path, _)) = before_map.get_key_value(key) {
                    changed.push(path.0.clone());
                }
            }
        }
        if !changed.is_empty() {
            unsafe {
                let _ = WaitForSingleObject(stop, 2200);
            }
            for path in changed.into_iter().take(2000) {
                notify_changed_path(&path);
            }
        }
        Ok(())
    }
    #[derive(Clone, Debug)]
    struct OrdinalPath(String);
    impl PartialEq for OrdinalPath {
        fn eq(&self, other: &Self) -> bool {
            overlay::compare_paths(&self.0, &other.0) == std::cmp::Ordering::Equal
        }
    }
    impl Eq for OrdinalPath {}
    impl PartialOrd for OrdinalPath {
        fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
            Some(self.cmp(other))
        }
    }
    impl Ord for OrdinalPath {
        fn cmp(&self, other: &Self) -> std::cmp::Ordering {
            overlay::compare_paths(&self.0, &other.0)
        }
    }
    fn same_path(a: &str, b: &str) -> bool {
        overlay::compare_paths(
            a.replace('/', "\\").trim_end_matches('\\'),
            b.replace('/', "\\").trim_end_matches('\\'),
        ) == std::cmp::Ordering::Equal
    }

    #[cfg(test)]
    mod ownership_tests {
        use super::scope_targets_executable;

        #[test]
        fn executable_marker_is_case_insensitive_and_exact() {
            assert!(scope_targets_executable(
                Some(r"C:\Program Files\MoaVcs\moavcs.exe"),
                None,
                r"c:\program files\moavcs\MOAVCS.EXE"
            ));
            assert!(!scope_targets_executable(
                Some(r"C:\Program Files\MoaVcs\moavcs-next.exe"),
                None,
                r"C:\Program Files\MoaVcs\moavcs.exe"
            ));
            assert!(scope_targets_executable(
                Some(r"C:\Tools\Ütils\moavcs.exe"),
                None,
                r"c:\tools\üTILS\MOAVCS.EXE"
            ));
        }

        #[test]
        fn legacy_open_command_fallback_matches_quoted_target_prefix() {
            assert!(scope_targets_executable(
                None,
                Some(r#""C:\Program Files\MoaVcs\moavcs.exe" --shell-path "%1|" --action open"#),
                r"C:\Program Files\MoaVcs\moavcs.exe"
            ));
            assert!(!scope_targets_executable(
                None,
                Some(r#""C:\Program Files\MoaVcs\moavcs.exe.extra" --action open"#),
                r"C:\Program Files\MoaVcs\moavcs.exe"
            ));
        }

        #[test]
        fn present_foreign_marker_does_not_fall_back_to_legacy_command() {
            assert!(!scope_targets_executable(
                Some(r"C:\Other\moavcs.exe"),
                Some(r#""C:\Program Files\MoaVcs\moavcs.exe" --action open"#),
                r"C:\Program Files\MoaVcs\moavcs.exe"
            ));
        }
    }
}

#[cfg(windows)]
pub use win::{
    configure_overlays, handle_startup, install_context_menu, maybe_start_worker,
    uninstall_context_menu,
};
