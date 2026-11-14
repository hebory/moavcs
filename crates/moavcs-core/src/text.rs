use crate::{models::*, process::CancelToken};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::{Read, Write},
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};
pub const MAX_TEXT_BYTES: usize = 1024 * 1024;
pub const MAX_LINES: usize = 50_000;
static TEMP_ID: AtomicU64 = AtomicU64::new(0);
#[derive(Debug, Clone)]
pub struct TextSnapshot {
    pub text: String,
    pub bom: bool,
    pub newline: String,
    pub hash: String,
}
pub fn hash_bytes(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn ordinary_file(path: &Path) -> Result<fs::Metadata> {
    let metadata = fs::symlink_metadata(path)?;
    if !metadata.is_file() || reparse(&metadata) {
        return Err(
            "일반 파일만 편집하거나 비교할 수 있습니다. 링크·폴더는 지원하지 않습니다.".into(),
        );
    }
    Ok(metadata)
}
fn reparse(metadata: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        metadata.file_attributes() & 0x400 != 0
    }
    #[cfg(not(windows))]
    {
        metadata.file_type().is_symlink()
    }
}
pub fn decode_snapshot(bytes: &[u8], label: &str) -> Result<String> {
    if bytes.len() > MAX_TEXT_BYTES {
        return Err(VcsError(format!(
            "{label}: 내장 텍스트 도구는 파일당 1 MiB까지 지원합니다."
        )));
    }
    let bytes = bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(bytes);
    if bytes.contains(&0) {
        return Err(VcsError(format!(
            "{label}: 바이너리 또는 UTF-8이 아닌 파일입니다."
        )));
    }
    let text = std::str::from_utf8(bytes)
        .map_err(|_| VcsError(format!("{label}: UTF-8 텍스트만 지원합니다.")))?;
    if text.lines().count() > MAX_LINES {
        return Err("파일당 50,000줄까지 지원합니다.".into());
    }
    Ok(text.to_owned())
}
pub fn read_snapshot(path: &Path) -> Result<TextSnapshot> {
    let metadata = ordinary_file(path)?;
    if metadata.len() > MAX_TEXT_BYTES as u64 {
        return Err("내장 텍스트 도구는 파일당 1 MiB까지 지원합니다.".into());
    }
    let mut options = fs::OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows::Win32::Storage::FileSystem::{FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ};
        options
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT.0)
            .share_mode(FILE_SHARE_READ.0);
    }
    let source = options.open(path)?;
    let opened_metadata = source.metadata()?;
    if !opened_metadata.is_file() || reparse(&opened_metadata) {
        return Err("링크나 일반 파일이 아닌 경로는 읽을 수 없습니다.".into());
    }
    let mut bytes = Vec::new();
    source
        .take(MAX_TEXT_BYTES as u64 + 1)
        .read_to_end(&mut bytes)?;
    let text = decode_snapshot(&bytes, &path.display().to_string())?;
    let newline = if text.contains("\r\n") {
        "\r\n"
    } else if text.contains('\r') && !text.contains('\n') {
        "\r"
    } else {
        "\n"
    };
    Ok(TextSnapshot {
        text,
        newline: newline.into(),
        bom: bytes.starts_with(&[0xef, 0xbb, 0xbf]),
        hash: hash_bytes(&bytes),
    })
}
pub fn validate_relative(root: &Path, path: &str) -> Result<String> {
    let path = path.replace('\\', "/");
    if path.is_empty()
        || path.starts_with('/')
        || path.contains(':')
        || path.chars().any(char::is_control)
        || path
            .split('/')
            .any(|p| p.is_empty() || p == "." || p == "..")
    {
        return Err(VcsError(format!(
            "저장소 내부의 올바른 상대 경로가 아닙니다: {path}"
        )));
    }
    let mut candidate = PathBuf::from(root);
    for part in path.split('/') {
        candidate.push(part);
        if let Ok(metadata) = fs::symlink_metadata(&candidate) {
            if reparse(&metadata) {
                return Err(VcsError(format!("링크 경로를 사용할 수 없습니다: {path}")));
            }
        }
    }
    Ok(path)
}
pub fn validate_remote_url(url: &str) -> Result<String> {
    if url.chars().any(char::is_control) {
        return Err("주소에 제어 문자를 사용할 수 없습니다.".into());
    }
    let value = url.trim();
    if value.is_empty() || value.starts_with('-') {
        return Err(
            "원격 주소를 입력하세요. '-'로 시작하는 상대 경로는 ./를 앞에 붙이세요.".into(),
        );
    }
    Ok(value.into())
}
pub fn save_conflict_file(expected: &ConflictFile, text: &str) -> Result<ConflictFile> {
    let path = expected
        .root
        .join(validate_relative(&expected.root, &expected.path)?);
    let current = read_snapshot(&path)?;
    if current.hash != expected.file_hash {
        return Err("작업 파일이 변경되었습니다. 충돌 파일을 다시 여세요.".into());
    }
    let without_crlf = current.text.replace("\r\n", "");
    let newline_kinds = usize::from(current.text.contains("\r\n"))
        + usize::from(without_crlf.contains('\r'))
        + usize::from(without_crlf.contains('\n'));
    if newline_kinds > 1 {
        return Err(
            "줄바꿈 방식이 섞인 파일은 원문 보존을 위해 내장 충돌 편집으로 저장할 수 없습니다."
                .into(),
        );
    }
    // The editor keeps line endings verbatim; normalize only newly entered LF to the original convention.
    let normalized = if expected.newline == "\r\n" {
        text.replace("\r\n", "\n").replace('\n', "\r\n")
    } else if expected.newline == "\r" {
        text.replace("\r\n", "\n").replace('\n', "\r")
    } else {
        text.into()
    };
    let mut bytes = if expected.has_utf8_bom {
        vec![0xef, 0xbb, 0xbf]
    } else {
        vec![]
    };
    bytes.extend_from_slice(normalized.as_bytes());
    decode_snapshot(&bytes, &expected.path)?;
    let temp = path.with_file_name(format!(
        ".moavcs-{}-{}-{}.tmp",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos(),
        TEMP_ID.fetch_add(1, Ordering::Relaxed)
    ));
    let backup = temp.with_extension("bak");
    let result = (|| {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)?;
        file.write_all(&bytes)?;
        file.sync_all()?;
        drop(file);
        if read_snapshot(&path)?.hash != expected.file_hash {
            return Err("작업 파일이 변경되었습니다. 다시 열어 확인하세요.".into());
        }
        #[cfg(windows)]
        {
            use std::os::windows::ffi::OsStrExt;
            use windows::{
                core::PCWSTR,
                Win32::Storage::FileSystem::{ReplaceFileW, REPLACE_FILE_FLAGS},
            };
            let wide = |path: &Path| {
                path.as_os_str()
                    .encode_wide()
                    .chain(Some(0))
                    .collect::<Vec<_>>()
            };
            let (target, replacement, copy) = (wide(&path), wide(&temp), wide(&backup));
            unsafe { ReplaceFileW(PCWSTR(target.as_ptr()), PCWSTR(replacement.as_ptr()), PCWSTR(copy.as_ptr()), REPLACE_FILE_FLAGS(0), None, None) }
                .map_err(|e| VcsError(format!("충돌 결과를 저장하지 못했습니다. 원본 파일과 백업을 확인하세요: {e}\n백업 경로: {}", backup.display())))?;
        }
        #[cfg(not(windows))]
        {
            fs::copy(&path, &backup)?;
            fs::rename(&temp, &path)?;
        }
        let mut saved = expected.clone();
        saved.file_hash = hash_bytes(&bytes);
        saved.working_text = normalized;
        // ReplaceFile preserves the exact displaced file, including an edit in the
        // final check/replace interval. Keep it in the user's backup store.
        let stored = std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(std::env::temp_dir)
            .join("MoaVcs/ConflictBackups")
            .join(hash_bytes(expected.root.to_string_lossy().as_bytes()));
        let destination = stored.join(backup.file_name().unwrap_or_default());
        saved.backup_path = Some(
            if fs::create_dir_all(&stored)
                .and_then(|_| fs::rename(&backup, &destination))
                .is_ok()
            {
                destination
            } else {
                backup.clone()
            },
        );
        Ok(saved)
    })();
    let _ = fs::remove_file(&temp);
    result
}

#[derive(Debug, Clone)]
pub struct ConflictBlock {
    pub start: usize,
    pub length: usize,
    pub left_text: String,
    pub right_text: String,
    pub base_text: Option<String>,
    pub left_label: String,
    pub right_label: String,
}
fn marker(line: &str) -> Option<(u8, usize, String)> {
    let bytes = line.trim_end_matches(['\r', '\n']).as_bytes();
    let first = *bytes.first()?;
    if !b"<>=|".contains(&first) {
        return None;
    }
    let width = bytes.iter().take_while(|&&b| b == first).count();
    if width < 7 || bytes.get(width).is_some_and(|b| !b.is_ascii_whitespace()) {
        return None;
    }
    let label = std::str::from_utf8(&bytes[width..]).ok()?.trim();
    if first == b'=' && !label.is_empty() {
        return None;
    }
    Some((first, width, label.into()))
}
pub fn contains_markers(text: &str) -> bool {
    text.split(['\r', '\n']).any(|line| marker(line).is_some())
}
pub fn parse_conflicts(text: &str) -> Result<Vec<ConflictBlock>> {
    let mut lines = Vec::new();
    let mut offset = if text.starts_with('\u{feff}') { 3 } else { 0 };
    let bytes = text.as_bytes();
    while offset < bytes.len() {
        let start = offset;
        while offset < bytes.len() && !matches!(bytes[offset], b'\r' | b'\n') {
            offset += 1;
        }
        if offset < bytes.len() {
            let first = bytes[offset];
            offset += 1;
            if first == b'\r' && bytes.get(offset) == Some(&b'\n') {
                offset += 1;
            }
        }
        lines.push((start, offset, &text[start..offset]));
    }
    let malformed = || {
        VcsError(
            "충돌 표식이 일부 없거나 중첩되어 있습니다. 결과를 직접 편집해 표식을 정리하세요."
                .into(),
        )
    };
    let mut blocks = Vec::new();
    let mut i = 0;
    while i < lines.len() {
        let (start, left_start, line) = lines[i];
        let Some((kind, width, left_label)) = marker(line) else {
            i += 1;
            continue;
        };
        if kind != b'<' {
            return Err(malformed());
        }
        let mut left_end = None;
        let mut base_start = None;
        let mut base_end = None;
        let mut right_start = None;
        let mut found = false;
        i += 1;
        while i < lines.len() {
            let (line_start, line_end, line) = lines[i];
            if let Some((kind, w, label)) = marker(line) {
                if w != width {
                    return Err(malformed());
                }
                match kind {
                    b'|' if left_end.is_none() => {
                        left_end = Some(line_start);
                        base_start = Some(line_end);
                    }
                    b'=' if right_start.is_none() => {
                        if base_start.is_some() {
                            base_end = Some(line_start);
                        } else {
                            left_end = Some(line_start);
                        }
                        right_start = Some(line_end);
                    }
                    b'>' if right_start.is_some() => {
                        blocks.push(ConflictBlock {
                            start,
                            length: line_end - start,
                            left_text: text[left_start..left_end.ok_or_else(malformed)?].into(),
                            right_text: text[right_start.unwrap()..line_start].into(),
                            base_text: base_start.map(|s| text[s..base_end.unwrap_or(s)].into()),
                            left_label: left_label.clone(),
                            right_label: label,
                        });
                        found = true;
                        break;
                    }
                    _ => return Err(malformed()),
                }
            }
            i += 1;
        }
        if !found {
            return Err(malformed());
        }
        i += 1;
    }
    Ok(blocks)
}
#[derive(Debug, Clone)]
pub struct ComparisonRow {
    pub left_number: Option<usize>,
    pub left_text: String,
    pub right_number: Option<usize>,
    pub right_text: String,
    pub kind: String,
}
#[derive(Debug, Clone)]
pub struct LineDiff {
    pub rows: Vec<ComparisonRow>,
    pub simplified: bool,
}
pub fn compare_lines(left: &str, right: &str, cancel: &CancelToken) -> Result<LineDiff> {
    let normalized_left = left.replace("\r\n", "\n").replace('\r', "\n");
    let normalized_right = right.replace("\r\n", "\n").replace('\r', "\n");
    let left = normalized_left.lines().collect::<Vec<_>>();
    let right = normalized_right.lines().collect::<Vec<_>>();
    if left.len() > MAX_LINES || right.len() > MAX_LINES {
        return Err("좌우 비교는 파일당 50,000줄까지 지원합니다.".into());
    }
    let mut prefix = 0;
    while prefix < left.len().min(right.len()) && left[prefix] == right[prefix] {
        prefix += 1;
    }
    let mut suffix = 0;
    while suffix < left.len() - prefix
        && suffix < right.len() - prefix
        && left[left.len() - 1 - suffix] == right[right.len() - 1 - suffix]
    {
        suffix += 1;
    }
    let l = &left[prefix..left.len() - suffix];
    let r = &right[prefix..right.len() - suffix];
    let max = (l.len() + r.len()).min(512);
    let offset = max as isize + 1;
    let mut v = vec![-1isize; 2 * max + 3];
    v[(offset + 1) as usize] = 0;
    let mut trace = Vec::new();
    let mut edits = None;
    'search: for d in 0..=max {
        cancel.check()?;
        trace.push(v.clone());
        for k in (-(d as isize)..=d as isize).step_by(2) {
            let index = (offset + k) as usize;
            let mut x = if k == -(d as isize) || (k != d as isize && v[index - 1] < v[index + 1]) {
                v[index + 1]
            } else {
                v[index - 1] + 1
            };
            let mut y = x - k;
            while x >= 0
                && y >= 0
                && (x as usize) < l.len()
                && (y as usize) < r.len()
                && l[x as usize] == r[y as usize]
            {
                x += 1;
                y += 1;
            }
            v[index] = x;
            if x as usize >= l.len() && y as usize >= r.len() {
                let (mut bx, mut by) = (l.len() as isize, r.len() as isize);
                let mut result = Vec::new();
                for distance in (0..=d).rev() {
                    let prev = &trace[distance];
                    let key = bx - by;
                    let index = (offset + key) as usize;
                    let pk = if key == -(distance as isize)
                        || (key != distance as isize && prev[index - 1] < prev[index + 1])
                    {
                        key + 1
                    } else {
                        key - 1
                    };
                    let px = prev[(offset + pk) as usize];
                    let py = px - pk;
                    while bx > px && by > py {
                        result.push('=');
                        bx -= 1;
                        by -= 1;
                    }
                    if distance > 0 {
                        if bx == px {
                            result.push('+');
                            by -= 1;
                        } else {
                            result.push('-');
                            bx -= 1;
                        }
                    }
                }
                result.reverse();
                edits = Some(result);
                break 'search;
            }
        }
    }
    let simplified = edits.is_none();
    let edits = edits.unwrap_or_else(|| {
        std::iter::repeat_n('-', l.len())
            .chain(std::iter::repeat_n('+', r.len()))
            .collect()
    });
    let mut rows = Vec::new();
    let (mut li, mut ri) = (0, 0);
    let equal = |rows: &mut Vec<ComparisonRow>, li: &mut usize, ri: &mut usize| {
        rows.push(ComparisonRow {
            left_number: Some(*li + 1),
            left_text: left[*li].into(),
            right_number: Some(*ri + 1),
            right_text: right[*ri].into(),
            kind: "unchanged".into(),
        });
        *li += 1;
        *ri += 1;
    };
    for _ in 0..prefix {
        equal(&mut rows, &mut li, &mut ri);
    }
    let mut i = 0;
    while i < edits.len() {
        cancel.check()?;
        if edits[i] == '=' {
            equal(&mut rows, &mut li, &mut ri);
            i += 1;
            continue;
        }
        let (mut removed, mut added) = (0, 0);
        while i < edits.len() && edits[i] != '=' {
            if edits[i] == '-' {
                removed += 1;
            } else {
                added += 1;
            }
            i += 1;
        }
        for j in 0..removed.max(added) {
            let has_l = j < removed;
            let has_r = j < added;
            rows.push(ComparisonRow {
                left_number: has_l.then_some(li + 1),
                left_text: if has_l {
                    left[li].into()
                } else {
                    String::new()
                },
                right_number: has_r.then_some(ri + 1),
                right_text: if has_r {
                    right[ri].into()
                } else {
                    String::new()
                },
                kind: if has_l && has_r {
                    "modified"
                } else if has_l {
                    "removed"
                } else {
                    "added"
                }
                .into(),
            });
            li += usize::from(has_l);
            ri += usize::from(has_r);
        }
    }
    for _ in 0..suffix {
        equal(&mut rows, &mut li, &mut ri);
    }
    Ok(LineDiff { rows, simplified })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn alignment_and_unicode() {
        let diff =
            compare_lines("가\n나\n다\n", "가\n추가\n나\n다\n", &CancelToken::new()).unwrap();
        assert_eq!(diff.rows.len(), 4);
        assert_eq!(diff.rows[1].kind, "added");
        assert_eq!(diff.rows[2].left_number, Some(2));
        assert_eq!(diff.rows[2].right_number, Some(3));
    }
    #[test]
    fn markers() {
        let text="전\r\n<<<<<<< ours\r\n내 것\r\n||||||| base\r\n기준\r\n=======\r\n상대 것\r\n>>>>>>> theirs\r\n후\r\n";
        let blocks = parse_conflicts(text).unwrap();
        assert_eq!(blocks[0].left_text, "내 것\r\n");
        assert_eq!(blocks[0].base_text.as_deref(), Some("기준\r\n"));
        assert!(parse_conflicts("<<<<<<< ours\nx\n").is_err());
    }
    #[test]
    fn boundaries() {
        for path in ["../file", "C:/file", "a/../../f", "/file", "a:stream"] {
            assert!(validate_relative(Path::new("."), path).is_err());
        }
        assert!(validate_relative(Path::new("."), "한글/file[*].txt").is_ok());
        assert!(decode_snapshot(&[0xff], "bad").is_err());
        assert!(decode_snapshot(b"a\0b", "binary").is_err());
    }
}
