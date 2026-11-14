use super::*;

const EXTERNAL_ORIGIN_READ_ONLY:&str="origin URL이 전역, include 또는 worktree 설정에서 오므로 안전하게 보존할 수 없어 읽기 전용입니다.";
#[derive(Clone)]
struct ConfigEntry {
    scope: String,
    origin: String,
    value: String,
}

impl GitProvider {
    pub(super) fn remote_info(&self, root: &Path, cancel: &CancelToken) -> Result<RemoteInfo> {
        let root = self.root(root, cancel)?;
        let fetch_entries = self.config_entries(&root, "remote.origin.url", cancel)?;
        let push_entries = self.config_entries(&root, "remote.origin.pushurl", cancel)?;
        let section = self.run(
            &root,
            vec![
                "config".into(),
                "--null".into(),
                "--name-only".into(),
                "--get-regexp".into(),
                "^remote\\.origin\\.".into(),
            ],
            cancel,
        )?;
        if !matches!(section.exit_code, 0 | 1) {
            return section.ensure().map(|_| RemoteInfo::default());
        }
        let remote_list = self.required(&root, vec!["remote".into()], cancel)?;
        let has_section = section
            .stdout
            .split('\0')
            .filter(|s| !s.is_empty())
            .any(|key| key.starts_with("remote.origin."));
        let exists = !fetch_entries.is_empty()
            || !push_entries.is_empty()
            || has_section
            || remote_list.stdout.lines().any(|s| s.trim() == "origin");
        if !exists {
            return Ok(RemoteInfo {
                name: "origin".into(),
                ..Default::default()
            });
        }
        let fetch: Vec<String> = fetch_entries.iter().map(|e| e.value.clone()).collect();
        let push: Vec<String> = push_entries.iter().map(|e| e.value.clone()).collect();
        let mut reason = self.find_read_only_reason(
            &root,
            fetch_entries.iter().chain(push_entries.iter()),
            cancel,
        )?;
        if fetch.iter().any(|s| s.chars().any(char::is_control))
            || push.iter().any(|s| s.chars().any(char::is_control))
        {
            reason.get_or_insert_with(|| {
                "origin 설정에 줄바꿈 또는 제어 문자가 포함되어 안전하게 편집할 수 없습니다.".into()
            });
        }
        if fetch.is_empty() {
            reason.get_or_insert_with(|| {
                "origin 항목은 있지만 fetch 주소가 없어 안전하게 변경할 수 없습니다.".into()
            });
        } else if fetch.iter().filter(|v| *v == &fetch[0]).count() > 1 {
            reason.get_or_insert_with(|| {
                "origin의 첫 fetch 주소가 중복되어 하나만 안전하게 변경할 수 없습니다.".into()
            });
        }
        let (effective_fetch, effective_push) = if fetch.is_empty()
            || reason.as_deref() == Some(EXTERNAL_ORIGIN_READ_ONLY)
            || (fetch
                .iter()
                .chain(push.iter())
                .any(|s| s.chars().any(char::is_control)))
        {
            (vec![], vec![])
        } else {
            (
                self.effective_urls(
                    &root,
                    vec![
                        "remote".into(),
                        "get-url".into(),
                        "--all".into(),
                        "origin".into(),
                    ],
                    cancel,
                )?,
                self.effective_urls(
                    &root,
                    vec![
                        "remote".into(),
                        "get-url".into(),
                        "--push".into(),
                        "--all".into(),
                        "origin".into(),
                    ],
                    cancel,
                )?,
            )
        };
        Ok(RemoteInfo {
            name: "origin".into(),
            exists: true,
            fetch_urls: fetch,
            push_urls: push,
            effective_fetch_urls: effective_fetch,
            effective_push_urls: effective_push,
            read_only_reason: reason,
        })
    }

    pub(super) fn set_remote_info(
        &self,
        root: &Path,
        url: &str,
        expected: &RemoteInfo,
        cancel: &CancelToken,
    ) -> Result<()> {
        let url = text::validate_remote_url(url)?;
        let root = self.root(root, cancel)?;
        let current = self.remote_info(&root, cancel)?;
        if current != *expected {
            return Err(VcsError(
                "원격 설정이 창을 연 뒤 변경되었습니다. 다시 열어 확인하세요.".into(),
            ));
        }
        if let Some(reason) = &current.read_only_reason {
            return Err(VcsError(reason.clone()));
        }
        if !current.exists {
            return self
                .required(
                    &root,
                    vec!["remote".into(), "add".into(), "origin".into(), url],
                    cancel,
                )
                .map(|_| ());
        }
        let old = current
            .fetch_urls
            .first()
            .ok_or_else(|| VcsError("기본 fetch URL이 없어 안전하게 편집할 수 없습니다.".into()))?;
        // Replace only exact instances of the first configured fetch URL. Additional
        // distinct fetch URLs and every push URL remain in the local config.
        self.required(
            &root,
            vec![
                "config".into(),
                "--local".into(),
                "--fixed-value".into(),
                "--replace-all".into(),
                "--".into(),
                "remote.origin.url".into(),
                url,
                old.clone(),
            ],
            cancel,
        )?;
        Ok(())
    }

    fn config_entries(
        &self,
        root: &Path,
        key: &str,
        cancel: &CancelToken,
    ) -> Result<Vec<ConfigEntry>> {
        let result = self.run(
            root,
            vec![
                "config".into(),
                "--null".into(),
                "--show-origin".into(),
                "--show-scope".into(),
                "--get-all".into(),
                key.into(),
            ],
            cancel,
        )?;
        if result.exit_code == 1 {
            return Ok(Vec::new());
        }
        let result = result.ensure()?;
        let fields: Vec<_> = result.stdout.split('\0').collect();
        if fields.len() % 3 != 1 || fields.last() != Some(&"") {
            return Err(VcsError("Git 원격 설정을 안전하게 읽지 못했습니다.".into()));
        }
        let mut entries = Vec::with_capacity(fields.len() / 3);
        for chunk in fields[..fields.len() - 1].chunks_exact(3) {
            entries.push(ConfigEntry {
                scope: chunk[0].into(),
                origin: chunk[1].into(),
                value: chunk[2].into(),
            })
        }
        Ok(entries)
    }
    fn find_read_only_reason<'a>(
        &self,
        root: &Path,
        entries: impl Iterator<Item = &'a ConfigEntry>,
        cancel: &CancelToken,
    ) -> Result<Option<String>> {
        let entries: Vec<_> = entries.collect();
        if entries.is_empty() {
            return Ok(None);
        }
        let result = self.required(
            root,
            vec!["rev-parse".into(), "--git-path".into(), "config".into()],
            cancel,
        )?;
        let config_path = PathBuf::from(result.stdout.trim());
        let config_path = if config_path.is_absolute() {
            config_path
        } else {
            root.join(config_path)
        };
        let expected = fs::canonicalize(&config_path).unwrap_or(config_path);
        for entry in entries {
            if !entry.scope.eq_ignore_ascii_case("local") || !entry.origin.starts_with("file:") {
                return Ok(Some(EXTERNAL_ORIGIN_READ_ONLY.into()));
            }
            let origin = PathBuf::from(entry.origin.trim_start_matches("file:"));
            let origin = if origin.is_absolute() {
                origin
            } else {
                root.join(origin)
            };
            let actual = fs::canonicalize(&origin).unwrap_or(origin);
            if !actual
                .to_string_lossy()
                .eq_ignore_ascii_case(&expected.to_string_lossy())
            {
                return Ok(Some(EXTERNAL_ORIGIN_READ_ONLY.into()));
            }
        }
        Ok(None)
    }
    fn effective_urls(
        &self,
        root: &Path,
        args: Vec<String>,
        cancel: &CancelToken,
    ) -> Result<Vec<String>> {
        let result = self.run(root, args, cancel)?;
        if !result.success() {
            return Ok(Vec::new());
        }
        Ok(result
            .stdout
            .lines()
            .filter(|s| !s.is_empty())
            .map(str::to_owned)
            .collect())
    }
}
