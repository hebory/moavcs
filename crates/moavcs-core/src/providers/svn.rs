use crate::{
    models::*,
    process::{CancelToken, CommandRunner, CommandSpec},
    providers::VcsProvider,
    text,
};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::Arc,
};

pub struct SvnProvider {
    executable: String,
    runner: Arc<dyn CommandRunner>,
}
impl SvnProvider {
    pub fn new(executable: String, runner: Arc<dyn CommandRunner>) -> Self {
        Self { executable, runner }
    }
    fn run(&self, root: &Path, args: Vec<String>, cancel: &CancelToken) -> Result<CommandResult> {
        cancel.check()?;
        let mut full = vec!["--non-interactive".to_string()];
        full.extend(args);
        self.runner.run(
            &CommandSpec::new(self.executable.clone(), root, full),
            cancel,
        )
    }
    fn req(&self, root: &Path, args: Vec<String>, cancel: &CancelToken) -> Result<CommandResult> {
        self.run(root, args, cancel)?.ensure()
    }
    fn root(&self, path: &Path, cancel: &CancelToken) -> Result<(PathBuf, String, String)> {
        let full = absolute(path)?;
        let out = self.req(
            &full,
            vec![
                "info".into(),
                "--xml".into(),
                peg(full.to_string_lossy().as_ref()),
            ],
            cancel,
        )?;
        let info: InfoDoc = xml(&out.stdout)?;
        let entry = info
            .entries
            .first()
            .ok_or_else(|| VcsError("svn info에 작업 사본 정보가 없습니다.".into()))?;
        let root = entry
            .wc
            .as_ref()
            .and_then(|w| w.root.clone())
            .ok_or_else(|| VcsError("선택한 경로는 SVN 작업 사본이 아닙니다.".into()))?;
        Ok((
            PathBuf::from(root),
            entry.url.clone().unwrap_or_default(),
            entry.relative_url.clone().unwrap_or_default(),
        ))
    }
    fn path(&self, root: &Path, path: &str) -> Result<String> {
        text::validate_relative(root, path)
    }
    fn conflict_state(
        &self,
        expected: &ConflictFile,
        cancel: &CancelToken,
    ) -> Result<(PathBuf, String, String)> {
        let (root, _, _) = self.root(&expected.root, cancel)?;
        let path = self.path(&root, &expected.path)?;
        let full = root.join(&path);
        let out = self.req(
            &root,
            vec![
                "info".into(),
                "--xml".into(),
                "--".into(),
                peg(full.to_string_lossy().as_ref()),
            ],
            cancel,
        )?;
        let doc: InfoDoc = xml(&out.stdout)?;
        let conflicts = doc
            .entries
            .first()
            .and_then(|e| e.wc.as_ref())
            .and_then(|w| w.conflicts.as_ref())
            .ok_or_else(|| VcsError("SVN 충돌 메타데이터가 없습니다.".into()))?;
        let c = conflicts.items.first().ok_or_else(|| {
            VcsError("선택 파일의 SVN 충돌 메타데이터를 찾을 수 없습니다.".into())
        })?;
        let token = hash(&format!(
            "{}\0{}\0{}",
            c.base.as_deref().unwrap_or_default(),
            c.mine.as_deref().unwrap_or_default(),
            c.theirs.as_deref().unwrap_or_default()
        ));
        Ok((root, path, token))
    }
    fn history_repository_url(&self, root: &Path, cancel: &CancelToken) -> Result<String> {
        let full = absolute(root)?;
        let result = self.req(
            &full,
            vec![
                "info".into(),
                "--xml".into(),
                "--".into(),
                peg(full.to_string_lossy().as_ref()),
            ],
            cancel,
        )?;
        let doc: InfoDoc = xml(&result.stdout)?;
        let url = doc
            .entries
            .first()
            .and_then(|e| e.repository.as_ref())
            .and_then(|r| r.root.as_deref())
            .ok_or_else(|| VcsError("SVN 저장소의 기준 URL을 확인할 수 없습니다.".into()))?;
        if url.chars().any(char::is_control)
            || !(url.starts_with("https://")
                || url.starts_with("http://")
                || url.starts_with("svn://")
                || url.starts_with("svn+ssh://"))
        {
            return Err(VcsError(
                "SVN 저장소 기준 URL 형식이 안전하지 않습니다.".into(),
            ));
        }
        Ok(url.trim_end_matches('/').to_owned())
    }
    fn conflict_snapshot(
        &self,
        root: &Path,
        file: &FileChange,
        cancel: &CancelToken,
    ) -> Result<ConflictFile> {
        if !file.conflicted() {
            return Err(VcsError("선택한 파일은 SVN 충돌 상태가 아닙니다.".into()));
        }
        let (root, _, _) = self.root(root, cancel)?;
        let path = self.path(&root, &file.path)?;
        let full = root.join(&path);
        let out = self.req(
            &root,
            vec![
                "info".into(),
                "--xml".into(),
                "--".into(),
                peg(full.to_string_lossy().as_ref()),
            ],
            cancel,
        )?;
        let doc: InfoDoc = xml(&out.stdout)?;
        let c = doc
            .entries
            .first()
            .and_then(|e| e.wc.as_ref())
            .and_then(|w| w.conflicts.as_ref())
            .and_then(|c| c.items.first())
            .ok_or_else(|| {
                VcsError("SVN 텍스트 충돌 파일의 메타데이터를 찾을 수 없습니다.".into())
            })?;
        let base = read_conflict_side(&root, c.base.as_deref(), &path)?;
        let mine = read_conflict_side(&root, c.mine.as_deref(), &path)?
            .ok_or_else(|| VcsError("SVN의 내 변경 스냅샷 파일이 없습니다.".into()))?;
        let theirs = read_conflict_side(&root, c.theirs.as_deref(), &path)?
            .ok_or_else(|| VcsError("SVN의 받은 변경 스냅샷 파일이 없습니다.".into()))?;
        let working = if full.is_file() {
            Some(text::read_snapshot(&full)?)
        } else {
            None
        };
        let token = hash(&format!(
            "{}\0{}\0{}",
            c.base.as_deref().unwrap_or_default(),
            c.mine.as_deref().unwrap_or_default(),
            c.theirs.as_deref().unwrap_or_default()
        ));
        Ok(ConflictFile {
            root,
            path,
            base_text: base,
            left_text: mine,
            right_text: theirs,
            working_text: working.as_ref().map(|s| s.text.clone()).unwrap_or_default(),
            state_token: token,
            file_hash: working.as_ref().map(|s| s.hash.clone()).unwrap_or_default(),
            has_utf8_bom: working.as_ref().is_some_and(|s| s.bom),
            newline: working.map(|s| s.newline).unwrap_or_else(|| "\n".into()),
            left_label: "내 변경".into(),
            right_label: "받은 변경".into(),
            backup_path: None,
        })
    }
    fn add_paths(&self, args: &mut Vec<String>, root: &Path, paths: &[String]) -> Result<()> {
        args.push("--".into());
        if paths.is_empty() {
            args.push(peg(root.to_string_lossy().as_ref()));
            return Ok(());
        }
        for p in paths {
            let p = self.path(root, p)?;
            args.push(peg(root.join(p).to_string_lossy().as_ref()))
        }
        Ok(())
    }
}
impl VcsProvider for SvnProvider {
    fn kind(&self) -> VcsKind {
        VcsKind::Svn
    }
    fn executable(&self) -> &str {
        &self.executable
    }
    fn capabilities(&self) -> Capabilities {
        Capabilities {
            stage: true,
            commit: true,
            update: true,
            clone: true,
            conflict_resolution: true,
            blame: true,
            comparison: true,
            history: true,
            ..Default::default()
        }
    }
    fn repository(&self, path: &Path, cancel: &CancelToken) -> Result<RepositoryInfo> {
        let (root, url, relative) = self.root(path, cancel)?;
        let parts: Vec<_> = relative
            .trim_start_matches('^')
            .trim_start_matches('/')
            .split('/')
            .filter(|x| !x.is_empty())
            .collect();
        let branch = if parts.first() == Some(&"trunk") {
            "trunk".into()
        } else if parts.len() > 1 && matches!(parts[0], "branches" | "tags") {
            parts[1].into()
        } else {
            url.trim_end_matches('/')
                .rsplit('/')
                .next()
                .unwrap_or("작업 사본")
                .into()
        };
        Ok(RepositoryInfo {
            kind: VcsKind::Svn,
            root,
            branch,
            summary: url,
        })
    }
    fn status(&self, root: &Path, cancel: &CancelToken) -> Result<Vec<FileChange>> {
        let (root, _, _) = self.root(root, cancel)?;
        let out = self.req(
            &root,
            vec![
                "status".into(),
                "--xml".into(),
                peg(root.to_string_lossy().as_ref()),
            ],
            cancel,
        )?;
        let doc: StatusDoc = xml(&out.stdout)?;
        let mut rows = Vec::new();
        for target in doc.targets {
            for e in target.entries {
                let Some(s) = e.status else { continue };
                let state = s.item.as_deref().unwrap_or("normal");
                let props = s.props.as_deref().unwrap_or("none");
                let conflict = s.tree_conflicted.as_deref() == Some("true")
                    || state == "conflicted"
                    || props == "conflicted";
                if state == "normal" && matches!(props, "none" | "normal") && !conflict {
                    continue;
                }
                let path = PathBuf::from(e.path);
                let full = if path.is_absolute() {
                    path
                } else {
                    root.join(path)
                };
                let rel = relative_checked(&root, &full)?;
                let label = if conflict {
                    "conflicted"
                } else if state == "normal" && props == "modified" {
                    "property modified"
                } else {
                    state
                };
                rows.push(FileChange {
                    path: rel,
                    status: label.into(),
                    index_status: String::new(),
                    worktree_status: label.into(),
                    original_path: None,
                })
            }
        }
        Ok(rows)
    }
    fn diff(
        &self,
        root: &Path,
        file: Option<&FileChange>,
        _staged: bool,
        cancel: &CancelToken,
    ) -> Result<String> {
        let (root, _, _) = self.root(root, cancel)?;
        let mut a = vec!["diff".into()];
        if let Some(f) = file {
            let p = self.path(&root, &f.path)?;
            a.extend(["--".into(), peg(root.join(p).to_string_lossy().as_ref())])
        }
        Ok(self.run(&root, a, cancel)?.stdout)
    }
    fn comparison(
        &self,
        root: &Path,
        file: &FileChange,
        _staged: bool,
        cancel: &CancelToken,
    ) -> Result<FileComparison> {
        let (root, _, _) = self.root(root, cancel)?;
        let path = self.path(&root, &file.path)?;
        let full = root.join(&path);
        let is_new = matches!(file.status.as_str(), "unversioned" | "added");
        let left = if is_new {
            None
        } else {
            let r = self.req(
                &root,
                vec![
                    "cat".into(),
                    "--revision".into(),
                    "BASE".into(),
                    "--".into(),
                    peg(full.to_string_lossy().as_ref()),
                ],
                cancel,
            )?;
            Some(text::decode_snapshot(r.stdout.as_bytes(), &path)?)
        };
        let right = if full.is_file() {
            Some(text::read_snapshot(&full)?.text)
        } else {
            None
        };
        Ok(FileComparison {
            path,
            left_label: if left.is_none() {
                "SVN BASE (파일 없음)".into()
            } else {
                "SVN BASE".into()
            },
            right_label: if right.is_none() {
                "작업 복사본 (삭제됨)".into()
            } else {
                "작업 복사본".into()
            },
            left_text: left.unwrap_or_default(),
            right_text: right.unwrap_or_default(),
        })
    }
    fn blame(&self, root: &Path, file: &FileChange, cancel: &CancelToken) -> Result<BlameResult> {
        let (root, _, _) = self.root(root, cancel)?;
        let path = self.path(&root, &file.path)?;
        if matches!(
            file.status.as_str(),
            "unversioned" | "added" | "deleted" | "missing"
        ) {
            return Err(VcsError(
                "SVN Blame은 BASE에 존재하는 파일에서만 볼 수 있습니다.".into(),
            ));
        }
        let full = root.join(&path);
        let base = self.req(
            &root,
            vec![
                "cat".into(),
                "--revision".into(),
                "BASE".into(),
                "--".into(),
                peg(full.to_string_lossy().as_ref()),
            ],
            cancel,
        )?;
        let content = text::decode_snapshot(base.stdout.as_bytes(), &path)?;
        let source: Vec<_> = content.lines().collect();
        if source.len() > 50_000 {
            return Err(VcsError(
                "50,000줄을 초과해 Blame을 표시할 수 없습니다.".into(),
            ));
        }
        let out = self.req(
            &root,
            vec![
                "blame".into(),
                "--xml".into(),
                "--revision".into(),
                "BASE".into(),
                "--".into(),
                peg(full.to_string_lossy().as_ref()),
            ],
            cancel,
        )?;
        let doc: BlameDoc = xml(&out.stdout)?;
        if doc.entries.len() != source.len() {
            return Err(VcsError(
                "SVN blame 줄 수와 BASE 파일이 일치하지 않습니다.".into(),
            ));
        }
        let mut lines = vec![None; source.len()];
        for e in doc.entries {
            let n = e
                .line_number
                .parse::<usize>()
                .map_err(|_| VcsError("SVN blame 줄 번호 형식이 잘못되었습니다.".into()))?;
            if n == 0 || n > lines.len() || lines[n - 1].is_some() {
                return Err(VcsError(
                    "SVN blame 줄 번호가 잘못되었거나 중복되었습니다.".into(),
                ));
            }
            let c = e
                .commit
                .ok_or_else(|| VcsError("SVN blame 커밋 메타데이터가 없습니다.".into()))?;
            lines[n - 1] = Some(BlameLine {
                line_number: n,
                revision: c.revision,
                author: c.author.unwrap_or_default(),
                date: c.date.unwrap_or_default(),
                text: source[n - 1].into(),
            })
        }
        let lines: Option<Vec<_>> = lines.into_iter().collect();
        Ok(BlameResult {
            path: file.path.clone(),
            revision_label: "SVN BASE".into(),
            lines: lines.ok_or_else(|| VcsError("SVN blame 줄 정보가 빠졌습니다.".into()))?,
        })
    }
    fn history(
        &self,
        root: &Path,
        _scope: HistoryScope,
        cancel: &CancelToken,
    ) -> Result<HistorySnapshot> {
        let (root, _, _) = self.root(root, cancel)?;
        let out = self.req(
            &root,
            vec![
                "log".into(),
                "--xml".into(),
                "--verbose".into(),
                "--limit".into(),
                "50".into(),
                peg(root.to_string_lossy().as_ref()),
            ],
            cancel,
        )?;
        let doc: LogDoc = xml(&out.stdout)?;
        let entries = doc
            .entries
            .into_iter()
            .map(|e| HistoryEntry {
                id: e.revision,
                author: e.author.unwrap_or_default(),
                date: e.date.unwrap_or_default(),
                message: e.message.unwrap_or_default(),
                ..Default::default()
            })
            .collect();
        Ok(HistorySnapshot {
            entries,
            scope_label: "현재 SVN 작업 사본 경로의 서버 리비전 이력".into(),
            ..Default::default()
        })
    }
    fn history_details(
        &self,
        root: &Path,
        revision: &str,
        cancel: &CancelToken,
    ) -> Result<HistoryDetails> {
        let (root, _, _) = self.root(root, cancel)?;
        let rev = revision_number(revision)?;
        let info = self.req(
            &root,
            vec![
                "info".into(),
                "--xml".into(),
                "--".into(),
                peg(root.to_string_lossy().as_ref()),
            ],
            cancel,
        )?;
        let doc: InfoDoc = xml(&info.stdout)?;
        let url = doc
            .entries
            .first()
            .and_then(|e| e.repository.as_ref())
            .and_then(|r| r.root.as_ref())
            .ok_or_else(|| VcsError("SVN 저장소 URL을 얻지 못했습니다.".into()))?;
        let out = self.req(
            &root,
            vec![
                "log".into(),
                "--xml".into(),
                "--verbose".into(),
                "-r".into(),
                rev.clone(),
                "--".into(),
                format!("{url}@{rev}"),
            ],
            cancel,
        )?;
        let doc: LogDoc = xml(&out.stdout)?;
        let e = doc
            .entries
            .first()
            .ok_or_else(|| VcsError("선택한 SVN 리비전 정보를 찾을 수 없습니다.".into()))?;
        let mut files = Vec::new();
        for p in e.paths.iter().flat_map(|p| p.items.iter()) {
            let path = validate_repo_path(&p.path)?;
            let action = p.action.as_deref().unwrap_or("M");
            let original = p
                .copyfrom_path
                .as_deref()
                .map(validate_repo_path)
                .transpose()?;
            files.push(FileChange {
                path,
                status: match action {
                    "A" => "added",
                    "D" => "deleted",
                    "R" => "replaced",
                    _ => "modified",
                }
                .into(),
                original_path: original,
                ..Default::default()
            })
        }
        let entry = HistoryEntry {
            id: rev.clone(),
            author: e.author.clone().unwrap_or_default(),
            date: e.date.clone().unwrap_or_default(),
            message: e.message.clone().unwrap_or_default(),
            ..Default::default()
        };
        Ok(HistoryDetails {
            entry,
            files,
            comparison_label: format!(
                "저장소 전체 리비전 · r{} → r{rev}",
                rev.parse::<u64>().unwrap_or(1).saturating_sub(1)
            ),
        })
    }
    fn history_diff(
        &self,
        root: &Path,
        revision: &str,
        file: Option<&FileChange>,
        cancel: &CancelToken,
    ) -> Result<String> {
        let details = self.history_details(root, revision, cancel)?;
        let rev = details.entry.id.clone();
        let (root, _, _) = self.root(root, cancel)?;
        let repository = self.history_repository_url(&root, cancel)?;
        let target = if let Some(selected) = file {
            let item = details
                .files
                .iter()
                .find(|item| item.path == selected.path)
                .ok_or_else(|| {
                    VcsError("선택한 파일은 이 SVN 리비전의 변경 목록에 없습니다.".into())
                })?;
            let peg_revision = if item.status == "deleted" {
                rev.parse::<u64>()
                    .unwrap_or(1)
                    .saturating_sub(1)
                    .to_string()
            } else {
                rev.clone()
            };
            format!(
                "{}{}@{}",
                repository,
                escape_repo_path(&item.path)?,
                peg_revision
            )
        } else {
            format!("{}@{}", repository, rev)
        };
        self.req(
            &root,
            vec![
                "diff".into(),
                "--internal-diff".into(),
                "--notice-ancestry".into(),
                "-c".into(),
                details.entry.id,
                "--".into(),
                target,
            ],
            cancel,
        )
        .map(|result| result.stdout)
    }
    fn execute(&self, request: &VcsRequest, cancel: &CancelToken) -> Result<CommandResult> {
        let root = absolute(&request.working_directory)?;
        match request.operation {
            Operation::Stage => {
                if request.paths.is_empty() {
                    return Err(VcsError("SVN에 등록할 파일을 선택하세요.".into()));
                }
                let mut a = vec!["add".into()];
                self.add_paths(&mut a, &root, &request.paths)?;
                self.req(&root, a, cancel)
            }
            Operation::Commit => {
                if request.message.trim().is_empty() || request.paths.is_empty() {
                    return Err(VcsError("SVN 커밋 메시지와 파일을 선택하세요.".into()));
                }
                let mut a = vec![
                    "commit".into(),
                    "--message".into(),
                    request.message.clone(),
                    "--depth".into(),
                    "empty".into(),
                ];
                self.add_paths(&mut a, &root, &request.paths)?;
                self.req(&root, a, cancel)
            }
            Operation::Update => {
                let mut a = vec!["update".into()];
                self.add_paths(&mut a, &root, &request.paths)?;
                self.req(&root, a, cancel)
            }
            Operation::Clone => {
                let remote = text::validate_remote_url(&request.remote)?;
                if request.destination.as_os_str().is_empty() {
                    return Err(VcsError(
                        "SVN checkout 주소 또는 대상 폴더가 잘못되었습니다.".into(),
                    ));
                }
                let d = absolute(&request.destination)?;
                if d.is_file() || (d.is_dir() && fs::read_dir(&d)?.next().is_some()) {
                    return Err(VcsError(
                        "SVN checkout 대상은 비어 있는 폴더여야 합니다.".into(),
                    ));
                }
                self.req(
                    &root,
                    vec![
                        "checkout".into(),
                        "--".into(),
                        remote,
                        peg(d.to_string_lossy().as_ref()),
                    ],
                    cancel,
                )
            }
            _ => Err(VcsError(format!(
                "Subversion에서는 {:?} 작업을 지원하지 않습니다.",
                request.operation
            ))),
        }
    }
    fn conflict(
        &self,
        root: &Path,
        file: &FileChange,
        cancel: &CancelToken,
    ) -> Result<ConflictFile> {
        self.conflict_snapshot(root, file, cancel)
    }
    fn save_conflict(
        &self,
        expected: &ConflictFile,
        content: &str,
        cancel: &CancelToken,
    ) -> Result<ConflictFile> {
        let (_, _, token) = self.conflict_state(expected, cancel)?;
        if token != expected.state_token {
            return Err(VcsError(
                "SVN 충돌 정보가 변경되었습니다. 파일을 다시 여세요.".into(),
            ));
        }
        text::save_conflict_file(expected, content)
    }
    fn resolve_conflict(&self, expected: &ConflictFile, cancel: &CancelToken) -> Result<()> {
        let (root, path, token) = self.conflict_state(expected, cancel)?;
        if token != expected.state_token {
            return Err(VcsError(
                "SVN 충돌 정보가 변경되었습니다. 파일을 다시 여세요.".into(),
            ));
        }
        let full = root.join(&path);
        if !full.is_file() {
            return Err(VcsError("SVN 충돌 해결 파일이 없습니다.".into()));
        }
        let current = text::read_snapshot(&full)?;
        if current.hash != expected.file_hash {
            return Err(VcsError("SVN 충돌 파일이 저장된 뒤 변경되었습니다.".into()));
        }
        self.req(
            &root,
            vec![
                "resolve".into(),
                "--accept".into(),
                "working".into(),
                "--depth".into(),
                "empty".into(),
                "--".into(),
                peg(full.to_string_lossy().as_ref()),
            ],
            cancel,
        )?;
        Ok(())
    }
}

#[derive(Deserialize, Default)]
#[serde(rename = "info")]
struct InfoDoc {
    #[serde(rename = "entry", default)]
    entries: Vec<InfoEntry>,
}
#[derive(Deserialize, Default)]
struct InfoEntry {
    #[serde(rename = "url")]
    url: Option<String>,
    #[serde(rename = "relative-url")]
    relative_url: Option<String>,
    #[serde(rename = "wc-info")]
    wc: Option<WcInfo>,
    #[serde(rename = "repository")]
    repository: Option<Repo>,
}
#[derive(Deserialize, Default)]
struct WcInfo {
    #[serde(rename = "wcroot-abspath")]
    root: Option<String>,
    #[serde(rename = "conflicts")]
    conflicts: Option<ConflictList>,
}
#[derive(Deserialize, Default)]
struct ConflictList {
    #[serde(rename = "conflict", default)]
    items: Vec<ConflictMeta>,
}
#[derive(Deserialize, Default)]
struct Repo {
    #[serde(rename = "root")]
    root: Option<String>,
}
#[derive(Deserialize, Default)]
struct StatusDoc {
    #[serde(rename = "target", default)]
    targets: Vec<StatusTarget>,
}
#[derive(Deserialize, Default)]
struct StatusTarget {
    #[serde(rename = "entry", default)]
    entries: Vec<StatusEntry>,
}
#[derive(Deserialize, Default)]
struct StatusEntry {
    #[serde(rename = "@path", default)]
    path: String,
    #[serde(rename = "wc-status")]
    status: Option<WcStatus>,
}
#[derive(Deserialize, Default)]
struct WcStatus {
    #[serde(rename = "@item")]
    item: Option<String>,
    #[serde(rename = "@props")]
    props: Option<String>,
    #[serde(rename = "@tree-conflicted")]
    tree_conflicted: Option<String>,
}
#[derive(Deserialize, Default)]
struct LogDoc {
    #[serde(rename = "logentry", default)]
    entries: Vec<LogEntry>,
}
#[derive(Deserialize, Default)]
struct LogEntry {
    #[serde(rename = "@revision", default)]
    revision: String,
    #[serde(rename = "author")]
    author: Option<String>,
    #[serde(rename = "date")]
    date: Option<String>,
    #[serde(rename = "msg")]
    message: Option<String>,
    #[serde(rename = "paths")]
    paths: Option<LogPaths>,
}
#[derive(Deserialize, Default)]
struct LogPaths {
    #[serde(rename = "path", default)]
    items: Vec<LogPath>,
}
#[derive(Deserialize, Default)]
struct LogPath {
    #[serde(rename = "$text", default)]
    path: String,
    #[serde(rename = "@action")]
    action: Option<String>,
    #[serde(rename = "@copyfrom-path")]
    copyfrom_path: Option<String>,
}
#[derive(Deserialize, Default)]
struct BlameDoc {
    #[serde(rename = "entry", default)]
    entries: Vec<BlameEntry>,
}
#[derive(Deserialize, Default)]
struct BlameEntry {
    #[serde(rename = "@line-number", default)]
    line_number: String,
    #[serde(rename = "commit")]
    commit: Option<BlameCommit>,
}
#[derive(Deserialize, Default)]
struct BlameCommit {
    #[serde(rename = "@revision", default)]
    revision: String,
    #[serde(rename = "author")]
    author: Option<String>,
    #[serde(rename = "date")]
    date: Option<String>,
}
#[derive(Deserialize, Default)]
struct ConflictMeta {
    #[serde(rename = "prev-base-file")]
    base: Option<String>,
    #[serde(rename = "prev-wc-file")]
    mine: Option<String>,
    #[serde(rename = "cur-base-file")]
    theirs: Option<String>,
}
fn xml<T: for<'de> Deserialize<'de>>(s: &str) -> Result<T> {
    quick_xml::de::from_str(s).map_err(|e| VcsError(format!("SVN XML을 읽지 못했습니다: {e}")))
}
fn peg(s: &str) -> String {
    format!("{s}@")
}
fn absolute(p: &Path) -> Result<PathBuf> {
    Ok(if p.is_absolute() {
        p.into()
    } else {
        std::env::current_dir()?.join(p)
    })
}
fn revision_number(r: &str) -> Result<String> {
    let n = r
        .parse::<u64>()
        .map_err(|_| VcsError("SVN 리비전은 양의 숫자여야 합니다.".into()))?;
    if n == 0 {
        return Err(VcsError("SVN 리비전은 0보다 커야 합니다.".into()));
    }
    Ok(n.to_string())
}
fn validate_repo_path(p: &str) -> Result<String> {
    if !p.starts_with('/')
        || p.starts_with("//")
        || p.len() < 2
        || p.chars().any(char::is_control)
        || p.contains('\\')
        || p[1..]
            .split('/')
            .any(|s| s == ".." || s == "." || s.is_empty())
    {
        return Err(VcsError(
            "SVN 이력이 안전하지 않은 저장소 경로를 보고했습니다.".into(),
        ));
    }
    Ok(p.into())
}
fn escape_repo_path(p: &str) -> Result<String> {
    let p = validate_repo_path(p)?;
    let mut out = String::from("/");
    for (i, segment) in p[1..].split('/').enumerate() {
        if i > 0 {
            out.push('/')
        }
        for b in segment.bytes() {
            if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
                out.push(b as char)
            } else {
                out.push_str(&format!("%{b:02X}"))
            }
        }
    }
    Ok(out)
}
fn relative_checked(root: &Path, path: &Path) -> Result<String> {
    let r = text::validate_relative(
        root,
        path.strip_prefix(root)
            .map_err(|_| VcsError("SVN 경로가 작업 사본을 벗어났습니다.".into()))?
            .to_string_lossy()
            .as_ref(),
    )?;
    Ok(r)
}
fn hash(s: &str) -> String {
    format!("{:x}", Sha256::digest(s.as_bytes()))
}
fn read_conflict_side(root: &Path, reported: Option<&str>, target: &str) -> Result<Option<String>> {
    let Some(name) = reported else {
        return Ok(None);
    };
    let full = PathBuf::from(name);
    let full = if full.is_absolute() {
        full
    } else {
        root.join(full)
    };
    if !full.starts_with(root) {
        return Err(VcsError(format!(
            "'{target}'의 SVN 충돌 스냅샷이 작업 사본 밖을 가리킵니다."
        )));
    }
    if !full.is_file() {
        return Ok(None);
    }
    Ok(Some(text::read_snapshot(&full)?.text))
}
