use crate::{
    models::*,
    process::CancelToken,
    settings::ExternalToolSettings,
    text::{compare_lines, validate_relative},
};
use std::{
    collections::HashMap,
    fs,
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::atomic::{AtomicU64, Ordering},
};
static SNAPSHOT_ID: AtomicU64 = AtomicU64::new(0);
pub fn download_url(kind: VcsKind) -> &'static str {
    match kind {
        VcsKind::Git => "https://git-scm.com/install/windows",
        VcsKind::Svn => "https://sliksvn.com/download/",
        VcsKind::Perforce => "https://www.perforce.com/downloads/helix-command-line-client-p4",
        VcsKind::Lore => "https://github.com/EpicGames/lore/releases/latest",
    }
}
pub fn open_url(url: &str) -> Result<()> {
    if !url.starts_with("https://") || url.chars().any(char::is_control) {
        return Err("HTTPS 주소만 열 수 있습니다.".into());
    }
    shell_open(url)
}
pub fn open_folder(path: &Path) -> Result<()> {
    if !path.is_dir() {
        return Err("열 폴더를 찾을 수 없습니다.".into());
    }
    shell_open(&path.to_string_lossy())
}
fn shell_open(value: &str) -> Result<()> {
    #[cfg(windows)]
    {
        use windows::{
            core::PCWSTR,
            Win32::{
                Foundation::HWND,
                UI::{Shell::ShellExecuteW, WindowsAndMessaging::SW_SHOWNORMAL},
            },
        };
        let action = "open\0".encode_utf16().collect::<Vec<_>>();
        let wide = value.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
        let result = unsafe {
            ShellExecuteW(
                HWND::default(),
                PCWSTR(action.as_ptr()),
                PCWSTR(wide.as_ptr()),
                None,
                None,
                SW_SHOWNORMAL,
            )
        };
        if result.0 as usize <= 32 {
            return Err("외부 프로그램을 열지 못했습니다.".into());
        }
        Ok(())
    }
    #[cfg(not(windows))]
    {
        Command::new("xdg-open").arg(value).spawn()?;
        Ok(())
    }
}
pub fn render_arguments(
    templates: &[String],
    values: &HashMap<&str, String>,
) -> Result<Vec<String>> {
    let mut args = Vec::new();
    for template in templates {
        let mut rendered = String::new();
        let mut remaining = template.as_str();
        while let Some(start) = remaining.find('{') {
            rendered.push_str(&remaining[..start]);
            let end = remaining[start + 1..]
                .find('}')
                .ok_or_else(|| VcsError("외부 도구 인수의 중괄호가 닫히지 않았습니다.".into()))?
                + start
                + 1;
            let name = remaining[start + 1..end].to_ascii_lowercase();
            let value = values.get(name.as_str()).ok_or_else(|| {
                VcsError(format!("지원하지 않거나 값이 없는 인수입니다: {{{name}}}"))
            })?;
            if value.contains('\0') {
                return Err("외부 도구 인수에 NUL을 사용할 수 없습니다.".into());
            }
            rendered.push_str(value);
            remaining = &remaining[end + 1..];
        }
        rendered.push_str(remaining);
        args.push(rendered);
    }
    Ok(args)
}
fn snapshot_directory() -> Result<PathBuf> {
    let base = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir)
        .join("MoaVcs/snapshots");
    fs::create_dir_all(&base)?;
    for _ in 0..10 {
        let path = base.join(format!(
            "rust-{}-{}",
            std::process::id(),
            SNAPSHOT_ID.fetch_add(1, Ordering::Relaxed)
        ));
        match fs::create_dir(&path) {
            Ok(()) => return Ok(path),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(e) => return Err(e.into()),
        }
    }
    Err("비교 스냅샷 폴더를 만들지 못했습니다.".into())
}
fn launch(
    settings: &ExternalToolSettings,
    root: &Path,
    values: HashMap<&str, String>,
    snapshot: PathBuf,
    defaults: &[&str],
) -> Result<()> {
    let executable = PathBuf::from(settings.executable_path.trim());
    if !executable.is_absolute() || !executable.is_file() {
        let _ = fs::remove_dir_all(&snapshot);
        return Err("외부 도구의 실행 파일에 절대 경로를 지정하세요.".into());
    }
    let templates = if settings.argument_templates.is_empty() {
        defaults.iter().map(|s| s.to_string()).collect()
    } else {
        settings.argument_templates.clone()
    };
    let result = (|| {
        let args = render_arguments(&templates, &values)?;
        let mut command = Command::new(executable);
        command.args(args).current_dir(root).stdin(Stdio::null());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x08000000);
        }
        let mut child = command.spawn()?;
        let generated = snapshot.clone();
        std::thread::spawn(move || {
            let _ = child.wait();
            let _ = fs::remove_dir_all(generated);
        });
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_dir_all(snapshot);
    }
    result
}
pub fn launch_diff(
    settings: &ExternalToolSettings,
    comparison: &FileComparison,
    root: &Path,
) -> Result<()> {
    let relative = validate_relative(root, &comparison.path)?;
    let snapshot = snapshot_directory()?;
    let left = snapshot.join("left.txt");
    let right = snapshot.join("right.txt");
    let patch = snapshot.join("changes.patch");
    let prepare = (|| {
        fs::write(&left, &comparison.left_text)?;
        fs::write(&right, &comparison.right_text)?;
        let diff = compare_lines(
            &comparison.left_text,
            &comparison.right_text,
            &CancelToken::new(),
        )?;
        let mut text = format!(
            "--- {}\n+++ {}\n@@ -1,{} +1,{} @@\n",
            comparison.left_label,
            comparison.right_label,
            comparison.left_text.lines().count(),
            comparison.right_text.lines().count()
        );
        for row in diff.rows {
            if row.kind == "unchanged" {
                text.push_str(&format!(" {}\n", row.left_text));
            } else {
                if row.left_number.is_some() {
                    text.push_str(&format!("-{}\n", row.left_text));
                }
                if row.right_number.is_some() {
                    text.push_str(&format!("+{}\n", row.right_text));
                }
            }
        }
        fs::write(&patch, text)?;
        Ok::<(), VcsError>(())
    })();
    if let Err(e) = prepare {
        let _ = fs::remove_dir_all(snapshot);
        return Err(e);
    }
    let values = HashMap::from([
        ("repo", root.to_string_lossy().into_owned()),
        ("file", root.join(relative).to_string_lossy().into_owned()),
        ("left", left.to_string_lossy().into_owned()),
        ("right", right.to_string_lossy().into_owned()),
        ("patch", patch.to_string_lossy().into_owned()),
    ]);
    launch(settings, root, values, snapshot, &["{left}", "{right}"])
}
pub fn launch_blame(
    settings: &ExternalToolSettings,
    blame: &BlameResult,
    root: &Path,
) -> Result<()> {
    let relative = validate_relative(root, &blame.path)?;
    let snapshot = snapshot_directory()?;
    let path = snapshot.join("blame.tsv");
    let mut text = "line\trevision\tauthor\tdate\ttext\n".to_string();
    for line in &blame.lines {
        text.push_str(&format!(
            "{}\t{}\t{}\t{}\t{}\n",
            line.line_number,
            line.revision.replace(['\t', '\r', '\n'], " "),
            line.author.replace(['\t', '\r', '\n'], " "),
            line.date.replace(['\t', '\r', '\n'], " "),
            line.text.replace(['\t', '\r', '\n'], " ")
        ));
    }
    if let Err(e) = fs::write(&path, text) {
        let _ = fs::remove_dir_all(&snapshot);
        return Err(e.into());
    }
    let values = HashMap::from([
        ("repo", root.to_string_lossy().into_owned()),
        ("file", root.join(relative).to_string_lossy().into_owned()),
        ("blame", path.to_string_lossy().into_owned()),
    ]);
    launch(settings, root, values, snapshot, &["{blame}"])
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn argv_not_shell() {
        let values = HashMap::from([
            ("left", "D:\\한글 경로\\a;$(unsafe).txt".into()),
            ("right", "D:\\b.txt".into()),
        ]);
        assert_eq!(
            render_arguments(&["--left={left}".into(), "{right}".into()], &values).unwrap(),
            ["--left=D:\\한글 경로\\a;$(unsafe).txt", "D:\\b.txt"]
        );
        assert!(render_arguments(&["{unknown}".into()], &values).is_err());
    }
}
