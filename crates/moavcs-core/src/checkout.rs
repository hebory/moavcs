use crate::models::*;
use std::path::{Path, PathBuf};
pub fn safe_name(name: &str) -> bool {
    if name.is_empty()
        || name == "."
        || name == ".."
        || name.encode_utf16().count() > 255
        || name.ends_with([' ', '.'])
        || name
            .chars()
            .any(|c| c.is_control() || "<>:\"/\\|?*".contains(c))
    {
        return false;
    }
    let base = name
        .split('.')
        .next()
        .unwrap_or("")
        .trim_end()
        .to_uppercase();
    !matches!(
        base.as_str(),
        "CON"
            | "PRN"
            | "AUX"
            | "NUL"
            | "COM1"
            | "COM2"
            | "COM3"
            | "COM4"
            | "COM5"
            | "COM6"
            | "COM7"
            | "COM8"
            | "COM9"
            | "LPT1"
            | "LPT2"
            | "LPT3"
            | "LPT4"
            | "LPT5"
            | "LPT6"
            | "LPT7"
            | "LPT8"
            | "LPT9"
            | "COM¹"
            | "COM²"
            | "COM³"
            | "LPT¹"
            | "LPT²"
            | "LPT³"
    )
}
pub fn repository_name(remote: &str) -> Option<String> {
    let remote = remote.trim();
    if remote.is_empty() || remote.starts_with('-') || remote.chars().any(char::is_control) {
        return None;
    }
    let value = if let Some((_, rest)) = remote.split_once("://") {
        rest.find('/').map(|at| &rest[at..]).unwrap_or("")
    } else {
        remote
    };
    let value = value
        .split(['?', '#'])
        .next()?
        .trim_end_matches(['/', '\\']);
    let mut parts = value.rsplit(['/', '\\']);
    let mut leaf = parts.next()?;
    if leaf == ".git" {
        leaf = parts.next()?;
    }
    // SCP-style host:path also has no path separator for a single project name.
    if !leaf.contains('\\') && leaf.contains(':') {
        leaf = leaf.rsplit(':').next()?;
    }
    let bytes = leaf.as_bytes();
    let mut decoded = Vec::new();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' {
            if i + 2 >= bytes.len() {
                return None;
            }
            let code = std::str::from_utf8(&bytes[i + 1..i + 3])
                .ok()
                .and_then(|s| u8::from_str_radix(s, 16).ok())?;
            decoded.push(code);
            i += 3;
        } else {
            decoded.push(bytes[i]);
            i += 1;
        }
    }
    let mut name = String::from_utf8(decoded).ok()?;
    if name.to_ascii_lowercase().ends_with(".git") {
        name.truncate(name.len() - 4);
    }
    safe_name(&name).then_some(name)
}
pub fn destination(parent: &Path, name: &str) -> Result<PathBuf> {
    if !parent.is_absolute() {
        return Err("상위 폴더에 절대 경로를 입력하세요.".into());
    }
    if !safe_name(name) {
        return Err("올바른 저장소 폴더 이름을 입력하세요.".into());
    }
    Ok(parent.join(name))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn names_and_paths() {
        for (url, name) in [
            ("https://github.com/team/project.git", "project"),
            ("git@host:team/한글.git", "한글"),
            ("https://host/%ED%95%9C%EA%B8%80.git?x=1", "한글"),
            ("https://host/team/repo/.git", "repo"),
        ] {
            assert_eq!(repository_name(url).as_deref(), Some(name));
        }
        for input in [
            "https://host/CON.git",
            "https://host/a%2fb.git",
            "-remote",
            "https://host/%ff",
        ] {
            assert!(repository_name(input).is_none());
        }
        for input in ["..", "CON.txt", "name.", "LPT1", "a/b"] {
            assert!(!safe_name(input));
        }
    }
}
