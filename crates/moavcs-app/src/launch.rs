use moavcs_core::{Result, VcsError, VcsKind};
use std::path::PathBuf;

#[derive(Debug, Clone, Default)]
pub struct LaunchOptions {
    pub path: Option<PathBuf>,
    pub action: String,
    pub vcs: Option<VcsKind>,
    pub headless: bool,
    pub install_context_menu: bool,
    pub uninstall_context_menu: bool,
    pub overlay: Option<String>,
    pub ui_smoke: Option<PathBuf>,
}

impl LaunchOptions {
    pub fn parse(args: impl IntoIterator<Item = String>) -> Result<Self> {
        let args: Vec<String> = args.into_iter().collect();
        let mut result = Self {
            action: "open".into(),
            ..Self::default()
        };
        let mut i = 0;
        while i < args.len() {
            let token = &args[i];
            match token.as_str() {
                "--overlay-worker" | "--pause-overlays" | "--resume-overlays"
                | "--disable-overlays" => {
                    if result.overlay.replace(token.clone()).is_some() {
                        return Err(VcsError(
                            "상태 배지 작업을 동시에 실행할 수 없습니다.".into(),
                        ));
                    }
                }
                "--headless" => result.headless = true,
                "--install-context-menu" => result.install_context_menu = true,
                "--uninstall-context-menu" => result.uninstall_context_menu = true,
                "--path" | "--shell-path" | "--action" | "--vcs" | "--ui-smoke" => {
                    i += 1;
                    let value = args
                        .get(i)
                        .filter(|v| !v.trim().is_empty())
                        .ok_or_else(|| VcsError(format!("{token} 뒤에 값을 입력해야 합니다.")))?;
                    match token.as_str() {
                        "--path" => result.path = Some(PathBuf::from(value)),
                        "--shell-path" => {
                            let path = value.strip_suffix('|').ok_or_else(|| {
                                VcsError("탐색기 경로의 끝 표시가 없습니다.".into())
                            })?;
                            if path.is_empty() {
                                return Err(VcsError(
                                    "탐색기에서 전달된 경로가 비어 있습니다.".into(),
                                ));
                            }
                            result.path = Some(PathBuf::from(path));
                        }
                        "--action" => {
                            const ALLOWED: &[&str] = &[
                                "open", "status", "diff", "blame", "log", "commit", "update",
                                "push", "clone", "init", "remote", "settings",
                            ];
                            if !ALLOWED
                                .iter()
                                .any(|action| action.eq_ignore_ascii_case(value))
                            {
                                return Err(VcsError(format!("지원하지 않는 작업입니다: {value}")));
                            }
                            result.action = value.to_ascii_lowercase();
                        }
                        "--vcs" => result.vcs = Some(VcsKind::parse(value)?),
                        "--ui-smoke" => result.ui_smoke = Some(PathBuf::from(value)),
                        _ => unreachable!(),
                    }
                }
                _ => return Err(VcsError(format!("알 수 없는 시작 옵션입니다: {token}"))),
            }
            i += 1;
        }
        if result.install_context_menu && result.uninstall_context_menu {
            return Err(VcsError(
                "탐색기 메뉴 설치와 제거를 동시에 실행할 수 없습니다.".into(),
            ));
        }
        if result.overlay.is_some()
            && (result.install_context_menu
                || result.uninstall_context_menu
                || result.path.is_some()
                || result.action != "open"
                || result.vcs.is_some()
                || result.ui_smoke.is_some())
        {
            return Err(VcsError(
                "상태 배지 작업은 다른 작업과 함께 실행할 수 없습니다.".into(),
            ));
        }
        if result.headless
            && (result.overlay.is_none()
                && !result.install_context_menu
                && !result.uninstall_context_menu
                || result.path.is_some()
                || result.action != "open"
                || result.vcs.is_some()
                || result.ui_smoke.is_some())
        {
            return Err(VcsError(
                "--headless는 탐색기 메뉴 또는 상태 배지 작업에만 사용할 수 있습니다.".into(),
            ));
        }
        if result.ui_smoke.is_some()
            && (result.path.is_some()
                || result.headless
                || result.install_context_menu
                || result.uninstall_context_menu
                || result.overlay.is_some())
        {
            return Err(VcsError(
                "--ui-smoke는 저장소 경로나 시스템 통합 작업과 함께 사용할 수 없습니다.".into(),
            ));
        }
        Ok(result)
    }
}

#[cfg(test)]
mod tests {
    use super::LaunchOptions;
    use moavcs_core::VcsKind;
    use std::path::PathBuf;

    fn parse(args: &[&str]) -> moavcs_core::Result<LaunchOptions> {
        LaunchOptions::parse(args.iter().map(|arg| (*arg).to_owned()))
    }

    #[test]
    fn shell_path_preserves_spaces_and_korean_characters() {
        let expected = PathBuf::from(r"D:\작업 공간\모아 저장소\파일.txt");
        let options = parse(&["--shell-path", r"D:\작업 공간\모아 저장소\파일.txt|"])
            .expect("valid Explorer path should parse");

        assert_eq!(options.path.as_ref(), Some(&expected));
    }

    #[test]
    fn shell_path_preserves_drive_root_sentinel() {
        let options =
            parse(&["--shell-path", r"C:\|"]).expect("drive-root Explorer path should parse");

        assert_eq!(options.path.as_ref(), Some(&PathBuf::from(r"C:\")));
    }

    #[test]
    fn settings_clone_init_and_vcs_actions_parse() {
        for action in ["settings", "clone", "init"] {
            let options = parse(&["--action", action]).expect("supported action should parse");
            assert_eq!(options.action, action);
        }

        let options = parse(&["--action", "clone", "--vcs", "git"])
            .expect("VCS selection should parse with clone action");
        assert_eq!(options.action, "clone");
        assert_eq!(options.vcs, Some(VcsKind::Git));
    }

    #[test]
    fn headless_is_accepted_for_menu_and_overlay_workers() {
        assert!(parse(&["--install-context-menu", "--headless"]).is_ok());
        assert!(parse(&["--overlay-worker", "--headless"]).is_ok());
    }

    #[test]
    fn rejects_headless_without_a_supported_system_task() {
        assert!(parse(&["--headless"]).is_err());
        assert!(parse(&["--headless", "--path", r"D:\repo"]).is_err());
        assert!(parse(&["--headless", "--action", "status"]).is_err());
        assert!(parse(&["--headless", "--ui-smoke", r"D:\smoke"]).is_err());
    }

    #[test]
    fn rejects_overlay_combinations_with_other_work() {
        assert!(parse(&["--overlay-worker", "--ui-smoke", r"D:\smoke"]).is_err());
        assert!(parse(&["--overlay-worker", "--path", r"D:\repo"]).is_err());
        assert!(parse(&["--overlay-worker", "--action", "status"]).is_err());
        assert!(parse(&["--overlay-worker", "--pause-overlays"]).is_err());
    }

    #[test]
    fn rejects_ui_smoke_with_repository_or_system_integration_work() {
        assert!(parse(&["--ui-smoke", r"D:\smoke", "--path", r"D:\repo"]).is_err());
        assert!(parse(&["--ui-smoke", r"D:\smoke", "--install-context-menu"]).is_err());
    }
}
