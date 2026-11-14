use crate::{models::*, text::hash_bytes};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    fs,
    io::{Read, Write},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicU64, Ordering},
        Mutex, OnceLock,
    },
};

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct RecentRepository {
    pub path: String,
    pub vcs_kind: VcsKind,
    pub is_pinned: bool,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ExternalToolKind {
    Diff,
    Blame,
}
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct ExternalToolSettings {
    pub use_external: bool,
    pub executable_path: String,
    pub argument_templates: Vec<String>,
    #[serde(flatten)]
    pub extra: serde_json::Map<String, serde_json::Value>,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct ExplorerMenuSettings {
    pub show_commit_at_top_level: bool,
    pub show_update_at_top_level: bool,
}
impl Default for ExplorerMenuSettings {
    fn default() -> Self {
        Self {
            show_commit_at_top_level: true,
            show_update_at_top_level: true,
        }
    }
}
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct AppSettings {
    pub recent_repositories: Vec<RecentRepository>,
    pub executable_paths: HashMap<VcsKind, String>,
    pub external_tools: HashMap<ExternalToolKind, ExternalToolSettings>,
    pub explorer_menu: ExplorerMenuSettings,
    pub explorer_overlays_enabled: bool,
    pub explorer_overlays_prefer_priority: bool,
    #[serde(flatten)]
    pub extra: serde_json::Map<String, serde_json::Value>,
}
#[derive(Debug, Clone)]
pub struct SettingsStore {
    pub path: PathBuf,
}
impl Default for SettingsStore {
    fn default() -> Self {
        let local = std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(|| std::env::temp_dir());
        Self {
            path: local.join("MoaVcs/settings.json"),
        }
    }
}
static PROCESS_LOCK: OnceLock<Mutex<()>> = OnceLock::new();
static SAVE_ID: AtomicU64 = AtomicU64::new(0);
impl SettingsStore {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }
    pub fn load(&self) -> Result<AppSettings> {
        if !self.path.exists() {
            return Ok(AppSettings::default());
        }
        let source = fs::File::open(&self.path)?;
        if source.metadata()?.len() > 4 * 1024 * 1024 {
            return Err("설정 파일이 너무 큽니다. 원본을 보존했습니다.".into());
        }
        let mut bytes = Vec::new();
        source.take(4 * 1024 * 1024 + 1).read_to_end(&mut bytes)?;
        if bytes.len() > 4 * 1024 * 1024 {
            return Err("설정 파일이 너무 큽니다. 원본을 보존했습니다.".into());
        }
        let bytes = bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes);
        let mut value: serde_json::Value = serde_json::from_slice(bytes).map_err(|e| {
            VcsError(format!(
                "설정 파일을 읽지 못했습니다. 원본 파일은 보존됩니다: {e}"
            ))
        })?;
        normalize_keys(&mut value);
        if let Some(items) = value
            .get_mut("RecentRepositories")
            .and_then(serde_json::Value::as_array_mut)
        {
            items.retain(|item| {
                item.is_object()
                    && item
                        .get("Path")
                        .and_then(serde_json::Value::as_str)
                        .is_some_and(|path| !path.trim().is_empty())
            });
        }
        let mut settings: AppSettings = serde_json::from_value(value)?;
        normalize_history(&mut settings.recent_repositories);
        Ok(settings)
    }
    fn write(&self, settings: &AppSettings) -> Result<()> {
        let parent = self
            .path
            .parent()
            .ok_or_else(|| VcsError("설정 경로를 확인하세요.".into()))?;
        fs::create_dir_all(parent)?;
        let temp = parent.join(format!(
            "settings-{}-{}.tmp",
            std::process::id(),
            SAVE_ID.fetch_add(1, Ordering::Relaxed)
        ));
        let result = (|| {
            let mut file = fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&temp)?;
            file.write_all(&serde_json::to_vec_pretty(settings)?)?;
            file.sync_all()?;
            drop(file);
            fs::rename(&temp, &self.path)?;
            Ok(())
        })();
        let _ = fs::remove_file(temp);
        result
    }
    pub fn update(
        &self,
        change: impl FnOnce(&mut AppSettings) -> Result<()>,
    ) -> Result<AppSettings> {
        let _process = PROCESS_LOCK
            .get_or_init(|| Mutex::new(()))
            .lock()
            .map_err(|_| VcsError("설정 잠금을 얻지 못했습니다.".into()))?;
        #[cfg(windows)]
        let _cross = SettingsMutex::acquire(&self.path)?;
        let mut settings = self.load()?;
        change(&mut settings)?;
        normalize_history(&mut settings.recent_repositories);
        self.write(&settings)?;
        Ok(settings)
    }
    pub fn save_preferences(&self, requested: &AppSettings) -> Result<()> {
        self.update(|current| {
            current.executable_paths = requested.executable_paths.clone();
            for (kind, tool) in &requested.external_tools {
                let old = current.external_tools.entry(*kind).or_default();
                old.use_external = tool.use_external;
                old.executable_path = tool.executable_path.clone();
                old.argument_templates = tool.argument_templates.clone();
            }
            current.explorer_menu = requested.explorer_menu.clone();
            current.explorer_overlays_enabled = requested.explorer_overlays_enabled;
            current.explorer_overlays_prefer_priority = requested.explorer_overlays_prefer_priority;
            Ok(())
        })?;
        Ok(())
    }
    pub fn remember(&self, path: &Path, kind: VcsKind) -> Result<AppSettings> {
        let path = path.to_string_lossy().into_owned();
        self.update(|s| {
            let pinned = s
                .recent_repositories
                .iter()
                .any(|r| same_path(&r.path, &path) && r.is_pinned);
            s.recent_repositories.retain(|r| !same_path(&r.path, &path));
            s.recent_repositories.insert(
                0,
                RecentRepository {
                    path,
                    vcs_kind: kind,
                    is_pinned: pinned,
                },
            );
            Ok(())
        })
    }
    pub fn pin(&self, path: &str, pinned: bool) -> Result<AppSettings> {
        self.update(|s| {
            if let Some(r) = s
                .recent_repositories
                .iter_mut()
                .find(|r| same_path(&r.path, path))
            {
                r.is_pinned = pinned;
            }
            Ok(())
        })
    }
    pub fn remove(&self, path: &str) -> Result<AppSettings> {
        self.update(|s| {
            s.recent_repositories.retain(|r| !same_path(&r.path, path));
            Ok(())
        })
    }
    pub fn clear_recent(&self) -> Result<AppSettings> {
        self.update(|s| {
            s.recent_repositories.retain(|r| r.is_pinned);
            Ok(())
        })
    }
}
pub fn same_path(left: &str, right: &str) -> bool {
    crate::overlay::compare_paths(
        left.replace('/', "\\").trim_end_matches('\\'),
        right.replace('/', "\\").trim_end_matches('\\'),
    ) == std::cmp::Ordering::Equal
}
fn normalize_history(items: &mut Vec<RecentRepository>) {
    let mut seen = Vec::<String>::new();
    let mut recent_count = 0;
    items.retain(|item| {
        if item.path.trim().is_empty() || seen.iter().any(|p| same_path(p, &item.path)) {
            return false;
        }
        seen.push(item.path.clone());
        if item.is_pinned {
            return true;
        }
        recent_count += 1;
        recent_count <= 12
    });
}
fn normalize_keys(value: &mut serde_json::Value) {
    const KEYS: &[&str] = &[
        "RecentRepositories",
        "Path",
        "VcsKind",
        "IsPinned",
        "ExecutablePaths",
        "ExternalTools",
        "UseExternal",
        "ExecutablePath",
        "ArgumentTemplates",
        "ExplorerMenu",
        "ShowCommitAtTopLevel",
        "ShowUpdateAtTopLevel",
        "ExplorerOverlaysEnabled",
        "ExplorerOverlaysPreferPriority",
    ];
    match value {
        serde_json::Value::Object(map) => {
            let old = std::mem::take(map);
            for (key, mut value) in old {
                normalize_keys(&mut value);
                let normalized = KEYS
                    .iter()
                    .find(|k| k.eq_ignore_ascii_case(&key))
                    .map(|s| s.to_string())
                    .unwrap_or(key);
                if matches!(normalized.as_str(), "ExecutablePaths" | "ExternalTools") {
                    if value.is_null() {
                        value = serde_json::json!({});
                    }
                    if let Some(entries) = value.as_object_mut() {
                        let old = std::mem::take(entries);
                        for (key, mut value) in old {
                            if value.is_null() {
                                value = if normalized == "ExternalTools" {
                                    serde_json::json!({})
                                } else {
                                    serde_json::json!("")
                                };
                            }
                            let key = if normalized == "ExecutablePaths" {
                                VcsKind::parse(&key)
                                    .map(|kind| format!("{kind:?}"))
                                    .unwrap_or(key)
                            } else if key.eq_ignore_ascii_case("diff") {
                                "Diff".into()
                            } else if key.eq_ignore_ascii_case("blame") {
                                "Blame".into()
                            } else {
                                key
                            };
                            entries.insert(key, value);
                        }
                    }
                }
                if value.is_null() {
                    value = match normalized.as_str() {
                        "RecentRepositories" | "ArgumentTemplates" => serde_json::json!([]),
                        "ExecutablePath" => serde_json::json!(""),
                        "ExplorerMenu" => serde_json::json!({}),
                        _ => value,
                    };
                }
                map.insert(normalized, value);
            }
            if let Some(serde_json::Value::String(kind)) = map.get_mut("VcsKind") {
                if let Ok(parsed) = VcsKind::parse(kind) {
                    *kind = format!("{parsed:?}");
                }
            }
        }
        serde_json::Value::Array(items) => {
            for item in items {
                normalize_keys(item);
            }
        }
        _ => {}
    }
}
#[cfg(windows)]
struct SettingsMutex(windows::Win32::Foundation::HANDLE);
#[cfg(windows)]
impl SettingsMutex {
    fn acquire(path: &Path) -> Result<Self> {
        use windows::{
            core::PCWSTR,
            Win32::{Foundation::CloseHandle, System::Threading::*},
        };
        let name = format!(
            "Local\\MoaVcs.RepositoryHistory.{}",
            hash_bytes(
                std::path::absolute(path)
                    .unwrap_or_else(|_| path.to_path_buf())
                    .to_string_lossy()
                    .replace('/', "\\")
                    .to_uppercase()
                    .as_bytes()
            )
            .to_uppercase()
        );
        let wide = name.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
        unsafe {
            let handle = CreateMutexW(None, false, PCWSTR(wide.as_ptr()))
                .map_err(|e| VcsError(e.to_string()))?;
            let waited = WaitForSingleObject(handle, 10000);
            if waited.0 != 0 && waited.0 != 0x80 {
                let _ = CloseHandle(handle);
                return Err(
                    "다른 MoaVCS 창이 설정을 사용 중입니다. 잠시 후 다시 시도하세요.".into(),
                );
            }
            Ok(Self(handle))
        }
    }
}
#[cfg(windows)]
impl Drop for SettingsMutex {
    fn drop(&mut self) {
        unsafe {
            let _ = windows::Win32::System::Threading::ReleaseMutex(self.0);
            let _ = windows::Win32::Foundation::CloseHandle(self.0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn legacy_and_stale_preferences() {
        let root =
            std::env::temp_dir().join(format!("moavcs-settings-test-{}", std::process::id()));
        fs::create_dir_all(&root).unwrap();
        let store = SettingsStore::new(root.join("settings.json"));
        fs::write(&store.path,r#"{"recentRepositories":[null,{"Path":"D:\\work\\한글","VcsKind":"git","IsPinned":true}],"externalTools":{"diff":null},"executablePaths":{"git":null},"FutureSetting":42}"#).unwrap();
        let old = store.load().unwrap();
        assert!(old.recent_repositories[0].is_pinned);
        assert!(old.external_tools.contains_key(&ExternalToolKind::Diff));
        assert_eq!(
            old.executable_paths.get(&VcsKind::Git).map(String::as_str),
            Some("")
        );
        store.remove("D:\\work\\한글").unwrap();
        store.save_preferences(&old).unwrap();
        let current = store.load().unwrap();
        assert!(current.recent_repositories.is_empty());
        assert_eq!(current.extra["FutureSetting"], 42);
        fs::write(&store.path, b"bad-json").unwrap();
        assert!(store.save_preferences(&old).is_err());
        assert_eq!(fs::read(&store.path).unwrap(), b"bad-json");
        fs::remove_dir_all(root).unwrap();
    }
}
