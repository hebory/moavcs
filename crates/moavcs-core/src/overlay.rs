//! Version-one status snapshot shared with the in-process Explorer overlay DLL.
//! Paths are stored as UTF-16LE because the Explorer ABI uses Windows paths.

use crate::{FileChange, Result, VcsError};
use std::{
    collections::HashMap,
    io::{Read, Write},
    path::{Component, Path, PathBuf},
};

pub const MAGIC: u32 = 0x4D4F_4156;
pub const VERSION: u32 = 1;
pub const MAX_BYTES: usize = 16 * 1024 * 1024;
pub const MAX_ENTRIES: usize = 100_000;
pub const MAX_PATH_UNITS: usize = 32_767;
const HEADER_SIZE: usize = 20;
const MAX_AGE_TICKS: i64 = 90 * 10_000_000;
const FUTURE_TOLERANCE_TICKS: i64 = 5 * 10_000_000;
const WINDOWS_EPOCH_TICKS: i64 = 116_444_736_000_000_000;

#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum OverlayState {
    Conflict = 1,
    Modified = 2,
    Untracked = 3,
}

impl OverlayState {
    fn priority(self) -> u8 {
        match self {
            Self::Conflict => 0,
            Self::Modified => 1,
            Self::Untracked => 2,
        }
    }
    fn from_raw(value: u32) -> Result<Self> {
        match value {
            1 => Ok(Self::Conflict),
            2 => Ok(Self::Modified),
            3 => Ok(Self::Untracked),
            _ => Err(VcsError(
                "Explorer 상태 배지 종류가 올바르지 않습니다.".into(),
            )),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OverlayEntry {
    pub state: OverlayState,
    pub path: String,
}

/// Aggregate file states up each checkout's ancestor chain. More specific nested checkouts
/// may replace a parent checkout's badge for that subtree in the worker.
pub fn build_snapshot(root: &Path, changes: &[FileChange]) -> Result<Vec<OverlayEntry>> {
    let full_root = normalize_absolute(root)?;
    let mut states: HashMap<String, OverlayState> = HashMap::new();
    for change in changes {
        let Some(state) = state_for(change) else {
            continue;
        };
        add_path(&full_root, &change.path, state, &mut states)?;
        if let Some(old) = change
            .original_path
            .as_deref()
            .filter(|value| !value.trim().is_empty())
        {
            add_path(&full_root, old, OverlayState::Modified, &mut states)?;
        }
    }
    let mut entries: Vec<_> = states
        .into_iter()
        .map(|(path, state)| OverlayEntry { state, path })
        .collect();
    entries.sort_by(|a, b| path_cmp(&a.path, &b.path));
    let mut unique: Vec<OverlayEntry> = Vec::with_capacity(entries.len());
    for entry in entries {
        if let Some(last) = unique
            .last_mut()
            .filter(|last| path_cmp(&last.path, &entry.path) == std::cmp::Ordering::Equal)
        {
            if entry.state.priority() < last.state.priority() {
                last.state = entry.state;
            }
        } else {
            unique.push(entry);
        }
    }
    let entries = unique;
    if entries.len() > MAX_ENTRIES {
        return Err(VcsError(
            "Explorer 상태 배지 항목 수가 제한을 초과했습니다.".into(),
        ));
    }
    Ok(entries)
}

fn state_for(change: &FileChange) -> Option<OverlayState> {
    if change.conflicted() || matches_any(change, &["tree-conflicted"]) {
        return Some(OverlayState::Conflict);
    }
    if matches_any(change, &["untracked", "unversioned", "?"]) {
        return Some(OverlayState::Untracked);
    }
    if matches_any(
        change,
        &[
            "modified",
            "changed",
            "added",
            "deleted",
            "renamed",
            "copied",
            "missing",
            "replaced",
            "obstructed",
            "incomplete",
            "property modified",
            "add",
            "edit",
            "delete",
            "move/add",
            "move/delete",
            "branch",
            "integrate",
            "import",
            "retype",
            "purge",
            "archive",
            "A",
            "D",
            "M",
            "R",
            "C",
            "T",
            "U",
        ],
    ) || has_git_code(&change.index_status)
        || has_git_code(&change.worktree_status)
    {
        return Some(OverlayState::Modified);
    }
    None
}

fn matches_any(change: &FileChange, values: &[&str]) -> bool {
    values.iter().any(|value| {
        change.status.eq_ignore_ascii_case(value)
            || change.index_status.eq_ignore_ascii_case(value)
            || change.worktree_status.eq_ignore_ascii_case(value)
    })
}
fn has_git_code(value: &str) -> bool {
    value.len() == 1 && "ADMRCTU".contains(value)
}

fn add_path(
    root: &Path,
    relative: &str,
    state: OverlayState,
    states: &mut HashMap<String, OverlayState>,
) -> Result<()> {
    if relative.trim().is_empty() || relative.contains('\0') || has_unsafe_syntax(relative) {
        return Ok(());
    }
    let path = Path::new(relative);
    if path.components().any(|c| {
        matches!(
            c,
            Component::ParentDir | Component::CurDir | Component::Prefix(_)
        )
    }) && !path.is_absolute()
    {
        return Ok(());
    }
    let joined;
    let candidate = if path.is_absolute() {
        path
    } else {
        joined = root.join(path);
        &joined
    };
    let full = normalize_absolute(candidate)?;
    if !path_is_within(root, &full) || has_metadata_component(root, &full) {
        return Ok(());
    }
    let mut current = full.as_path();
    loop {
        let text = path_text(current)?;
        states
            .entry(text)
            .and_modify(|old| {
                if state.priority() < old.priority() {
                    *old = state
                }
            })
            .or_insert(state);
        if states.len() > MAX_ENTRIES {
            return Err(VcsError(
                "Explorer 상태 배지 항목 수가 제한을 초과했습니다.".into(),
            ));
        }
        if current == root {
            break;
        }
        current = current
            .parent()
            .ok_or_else(|| VcsError("Explorer 저장소 경로가 올바르지 않습니다.".into()))?;
    }
    Ok(())
}

fn normalize_absolute(path: &Path) -> Result<PathBuf> {
    if !path.is_absolute()
        || has_unsafe_syntax(&path.to_string_lossy())
        || path.components().any(|c| matches!(c, Component::ParentDir))
    {
        return Err(VcsError(
            "Explorer 경로는 안전한 절대 경로여야 합니다.".into(),
        ));
    }
    // Keep ordinary drive paths. Windows canonicalize may return an extended-length
    // `\\?\` spelling, which Explorer deliberately rejects in the cache protocol.
    let full = normalize_lexically(path)?;
    if !full.is_absolute() {
        return Err(VcsError("Explorer 경로는 절대 경로여야 합니다.".into()));
    }
    Ok(full)
}

fn normalize_lexically(path: &Path) -> std::io::Result<PathBuf> {
    let mut result = PathBuf::new();
    for component in path.components() {
        match component {
            Component::Prefix(p) => result.push(p.as_os_str()),
            Component::RootDir => result.push(component.as_os_str()),
            Component::CurDir => {}
            Component::ParentDir => {
                result.pop();
            }
            Component::Normal(p) => result.push(p),
        }
    }
    Ok(result)
}
fn has_unsafe_syntax(path: &str) -> bool {
    path.contains('\0')
        || path.starts_with("\\\\?\\")
        || path.starts_with("\\\\.\\")
        || path.chars().any(|c| "*?<>|\"".contains(c))
        || path.split(['\\', '/']).any(|part| part == "..")
        || path.split(['\\', '/']).enumerate().any(|(i, part)| {
            part.contains(':')
                && !(i == 0
                    && part.len() == 2
                    && part.as_bytes()[1] == b':'
                    && part.as_bytes()[0].is_ascii_alphabetic())
        })
}
pub fn path_is_within(root: &Path, path: &Path) -> bool {
    let mut candidate = path.components();
    root.components().all(|component| {
        candidate.next().is_some_and(|other| {
            compare_paths(
                &component.as_os_str().to_string_lossy(),
                &other.as_os_str().to_string_lossy(),
            ) == std::cmp::Ordering::Equal
        })
    })
}
fn has_metadata_component(root: &Path, path: &Path) -> bool {
    let count = root.components().count();
    path.components().skip(count).any(|c|matches!(c,Component::Normal(name) if [".git",".svn",".lore"].iter().any(|meta|name.to_string_lossy().eq_ignore_ascii_case(meta))))
}
fn path_text(path: &Path) -> Result<String> {
    path.to_str()
        .map(|v| v.to_owned())
        .ok_or_else(|| VcsError("Explorer 경로에 올바르지 않은 UTF-16이 있습니다.".into()))
}
pub fn compare_paths(left: &str, right: &str) -> std::cmp::Ordering {
    path_cmp(left, right)
}
fn path_cmp(left: &str, right: &str) -> std::cmp::Ordering {
    #[cfg(windows)]
    {
        use windows::Win32::{
            Foundation::BOOL,
            Globalization::{CompareStringOrdinal, CSTR_EQUAL, CSTR_GREATER_THAN, CSTR_LESS_THAN},
        };
        let l: Vec<u16> = left.encode_utf16().collect();
        let r: Vec<u16> = right.encode_utf16().collect();
        let result = unsafe { CompareStringOrdinal(&l, &r, BOOL(1)) };
        match result {
            CSTR_LESS_THAN => std::cmp::Ordering::Less,
            CSTR_EQUAL => std::cmp::Ordering::Equal,
            CSTR_GREATER_THAN => std::cmp::Ordering::Greater,
            _ => left.cmp(right),
        }
    }
    #[cfg(not(windows))]
    {
        let l = left.to_lowercase();
        let r = right.to_lowercase();
        l.cmp(&r).then_with(|| left.cmp(right))
    }
}

pub fn write_snapshot<W: Write>(
    mut destination: W,
    file_time_utc: i64,
    entries: &[OverlayEntry],
) -> Result<()> {
    if file_time_utc <= 0 || entries.len() > MAX_ENTRIES {
        return Err(VcsError(
            "Explorer 상태 배지 파일 정보가 올바르지 않습니다.".into(),
        ));
    }
    let mut normalized = entries.to_vec();
    normalized.sort_by(|a, b| path_cmp(&a.path, &b.path));
    let mut total = HEADER_SIZE;
    let mut previous: Option<&str> = None;
    for e in &normalized {
        validate_path(&e.path)?;
        if previous.is_some_and(|p| path_cmp(p, &e.path) == std::cmp::Ordering::Equal) {
            return Err(VcsError("Explorer 상태 배지 경로가 중복되었습니다.".into()));
        }
        previous = Some(&e.path);
        let units = e.path.encode_utf16().count();
        total = total
            .checked_add(8 + units * 2)
            .ok_or_else(|| VcsError("Explorer 상태 배지 크기가 너무 큽니다.".into()))?;
    }
    if total > MAX_BYTES {
        return Err(VcsError(
            "Explorer 상태 배지 파일이 16 MiB를 초과합니다.".into(),
        ));
    }
    destination.write_all(&MAGIC.to_le_bytes())?;
    destination.write_all(&VERSION.to_le_bytes())?;
    destination.write_all(&file_time_utc.to_le_bytes())?;
    destination.write_all(&(normalized.len() as u32).to_le_bytes())?;
    for e in normalized {
        destination.write_all(&(e.state as u32).to_le_bytes())?;
        let units = e.path.encode_utf16().collect::<Vec<_>>();
        destination.write_all(&(units.len() as u32).to_le_bytes())?;
        for unit in units {
            destination.write_all(&unit.to_le_bytes())?;
        }
    }
    Ok(())
}

pub fn read_snapshot<R: Read>(source: R, now_file_time: i64) -> Result<(i64, Vec<OverlayEntry>)> {
    let mut bytes = Vec::new();
    source
        .take((MAX_BYTES + 1) as u64)
        .read_to_end(&mut bytes)?;
    if bytes.len() > MAX_BYTES || bytes.len() < HEADER_SIZE {
        return Err(VcsError(
            "Explorer 상태 배지 파일 크기가 올바르지 않습니다.".into(),
        ));
    }
    let mut p = 0;
    let magic = take_u32(&bytes, &mut p)?;
    let version = take_u32(&bytes, &mut p)?;
    let timestamp = take_i64(&bytes, &mut p)?;
    let count = take_u32(&bytes, &mut p)? as usize;
    if magic != MAGIC
        || version != VERSION
        || timestamp <= 0
        || count > MAX_ENTRIES
        || (timestamp > now_file_time
            && timestamp.saturating_sub(now_file_time) > FUTURE_TOLERANCE_TICKS)
        || (now_file_time > timestamp && now_file_time.saturating_sub(timestamp) > MAX_AGE_TICKS)
    {
        return Err(VcsError(
            "Explorer 상태 배지 파일이 잘못되었거나 만료되었습니다.".into(),
        ));
    }
    let mut entries = Vec::with_capacity(count);
    for _ in 0..count {
        let state = OverlayState::from_raw(take_u32(&bytes, &mut p)?)?;
        let units = take_u32(&bytes, &mut p)? as usize;
        if units == 0
            || units > MAX_PATH_UNITS
            || p.checked_add(units * 2).is_none_or(|end| end > bytes.len())
        {
            return Err(VcsError(
                "Explorer 상태 배지 경로 크기가 올바르지 않습니다.".into(),
            ));
        }
        let mut wide = Vec::with_capacity(units);
        for pair in bytes[p..p + units * 2].chunks_exact(2) {
            wide.push(u16::from_le_bytes([pair[0], pair[1]]));
        }
        p += units * 2;
        let path = String::from_utf16(&wide).map_err(|_| {
            VcsError("Explorer 상태 배지 경로의 UTF-16이 올바르지 않습니다.".into())
        })?;
        validate_path(&path)?;
        entries.push(OverlayEntry { state, path });
    }
    if p != bytes.len() {
        return Err(VcsError(
            "Explorer 상태 배지 파일 끝에 알 수 없는 데이터가 있습니다.".into(),
        ));
    }
    entries.sort_by(|a, b| path_cmp(&a.path, &b.path));
    if entries
        .windows(2)
        .any(|pair| path_cmp(&pair[0].path, &pair[1].path) == std::cmp::Ordering::Equal)
    {
        return Err(VcsError("Explorer 상태 배지 경로가 중복되었습니다.".into()));
    }
    Ok((timestamp, entries))
}
fn take_u32(bytes: &[u8], p: &mut usize) -> Result<u32> {
    let end = p
        .checked_add(4)
        .ok_or_else(|| VcsError("Explorer 상태 배지 위치가 잘못되었습니다.".into()))?;
    let a = bytes
        .get(*p..end)
        .ok_or_else(|| VcsError("Explorer 상태 배지 파일이 잘렸습니다.".into()))?;
    *p = end;
    Ok(u32::from_le_bytes([a[0], a[1], a[2], a[3]]))
}
fn take_i64(bytes: &[u8], p: &mut usize) -> Result<i64> {
    let end = p
        .checked_add(8)
        .ok_or_else(|| VcsError("Explorer 상태 배지 위치가 잘못되었습니다.".into()))?;
    let a = bytes
        .get(*p..end)
        .ok_or_else(|| VcsError("Explorer 상태 배지 파일이 잘렸습니다.".into()))?;
    *p = end;
    Ok(i64::from_le_bytes([
        a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7],
    ]))
}
fn validate_path(path: &str) -> Result<()> {
    let units = path.encode_utf16().count();
    if units < 3
        || units > MAX_PATH_UNITS
        || !Path::new(path).is_absolute()
        || has_unsafe_syntax(path)
        || path.contains('\0')
        || Path::new(path)
            .components()
            .any(|c| matches!(c, Component::ParentDir | Component::CurDir))
    {
        return Err(VcsError(
            "Explorer 상태 배지 경로는 정규화한 Windows 절대 경로여야 합니다.".into(),
        ));
    }
    Ok(())
}

pub fn file_time_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| WINDOWS_EPOCH_TICKS + (d.as_nanos() / 100) as i64)
        .unwrap_or(WINDOWS_EPOCH_TICKS)
}

pub struct StatusCache {
    path: PathBuf,
    entries: Vec<OverlayEntry>,
    last_reload: std::time::Instant,
}
impl StatusCache {
    pub fn new(path: PathBuf) -> Self {
        Self {
            path,
            entries: Vec::new(),
            last_reload: std::time::Instant::now() - std::time::Duration::from_secs(3),
        }
    }
    pub fn contains(&mut self, state: OverlayState, path: &str) -> bool {
        if self.last_reload.elapsed() >= std::time::Duration::from_secs(2) {
            self.reload();
        }
        self.entries
            .binary_search_by(|e| path_cmp(&e.path, path))
            .ok()
            .is_some_and(|i| self.entries[i].state == state)
    }
    pub fn reload(&mut self) {
        self.last_reload = std::time::Instant::now();
        self.entries.clear();
        let Ok(file) = std::fs::File::open(&self.path) else {
            return;
        };
        if let Ok((_, entries)) = read_snapshot(file, file_time_now()) {
            self.entries = entries;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(windows)]
    #[test]
    fn current_file_time_matches_windows_api() {
        #[repr(C)]
        struct NativeFileTime {
            low: u32,
            high: u32,
        }
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn GetSystemTimeAsFileTime(value: *mut NativeFileTime);
        }
        let mut native = NativeFileTime { low: 0, high: 0 };
        unsafe {
            GetSystemTimeAsFileTime(&mut native);
        }
        let expected = ((native.high as u64) << 32) | native.low as u64;
        assert!(
            file_time_now().abs_diff(expected as i64) < 10_000_000,
            "Rust FILETIME must match GetSystemTimeAsFileTime within one second"
        );
    }

    #[test]
    fn v1_snapshot_roundtrips_windows_filetime() {
        let timestamp = 133_801_632_000_000_000i64;
        let path = if cfg!(windows) {
            r"C:\work\repo\file.txt".to_owned()
        } else {
            "/tmp/repo/file.txt".to_owned()
        };
        let entries = [OverlayEntry {
            state: OverlayState::Modified,
            path,
        }];
        let mut bytes = Vec::new();
        write_snapshot(&mut bytes, timestamp, &entries).unwrap();
        let (found, decoded) = read_snapshot(bytes.as_slice(), timestamp).unwrap();
        assert_eq!(found, timestamp);
        assert_eq!(decoded, entries);
    }

    #[test]
    fn snapshot_rejects_expired_future_oversized_and_duplicate_data() {
        let entry = OverlayEntry {
            state: OverlayState::Modified,
            path: if cfg!(windows) {
                r"C:\repo\file.txt".into()
            } else {
                "/repo/file.txt".into()
            },
        };
        let mut bytes = Vec::new();
        write_snapshot(&mut bytes, 1_000_000_000, &[entry.clone()]).unwrap();
        assert!(read_snapshot(bytes.as_slice(), 1_000_000_000 + MAX_AGE_TICKS + 1).is_err());
        assert!(
            read_snapshot(bytes.as_slice(), 1_000_000_000 - FUTURE_TOLERANCE_TICKS - 1).is_err()
        );
        assert!(read_snapshot(vec![0; MAX_BYTES + 1].as_slice(), file_time_now()).is_err());
        assert!(write_snapshot(Vec::new(), file_time_now(), &[entry.clone(), entry]).is_err());
    }

    #[cfg(windows)]
    #[test]
    fn snapshot_rejects_traversal_and_path_prefix_escape() {
        let root = Path::new(r"C:\repo");
        let changes = [FileChange {
            path: r"..\repo-sibling\secret.txt".into(),
            status: "modified".into(),
            ..Default::default()
        }];
        let entries = build_snapshot(root, &changes).unwrap();
        assert!(entries.is_empty());
        assert!(path_is_within(
            Path::new(r"C:\Repo"),
            Path::new(r"c:\repo\nested")
        ));
        assert!(!path_is_within(
            Path::new(r"C:\repo"),
            Path::new(r"C:\repo-sibling")
        ));
    }
}
