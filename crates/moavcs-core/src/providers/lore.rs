//! Epic Games Lore 0.13+ adapter. Lore is intentionally handled with its own
//! status and JSON-lines protocols rather than pretending it is Git.
use crate::{
    models::*,
    process::{CancelToken, CommandRunner, CommandSpec},
    providers::VcsProvider,
    text,
};
use serde_json::Value;
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
    sync::Arc,
};

mod history;
mod inspection;

pub struct LoreProvider {
    executable: String,
    runner: Arc<dyn CommandRunner>,
}
impl LoreProvider {
    pub fn new(executable: String, runner: Arc<dyn CommandRunner>) -> Self {
        Self { executable, runner }
    }
    fn run(&self, root: &Path, args: Vec<String>, cancel: &CancelToken) -> Result<CommandResult> {
        cancel.check()?;
        self.runner.run(
            &CommandSpec::new(self.executable.clone(), root, args),
            cancel,
        )
    }
    fn req(&self, root: &Path, args: Vec<String>, cancel: &CancelToken) -> Result<CommandResult> {
        self.run(root, args, cancel)?.ensure()
    }
    fn repo_run(
        &self,
        root: &Path,
        args: impl IntoIterator<Item = String>,
        cancel: &CancelToken,
    ) -> Result<CommandResult> {
        let mut full = vec![
            "--repository".into(),
            absolute(root)?.to_string_lossy().into_owned(),
        ];
        full.extend(args);
        self.req(root, full, cancel)
    }
    fn root(&self, path: &Path) -> Result<PathBuf> {
        let mut p = absolute(path)?;
        if p.is_file() {
            p.pop();
        }
        loop {
            if p.join(".lore/config.toml").is_file() {
                return Ok(p);
            }
            if !p.pop() {
                break;
            }
        }
        Err("Lore working tree (.lore/config.toml)를 찾지 못했습니다.".into())
    }
    fn safe_path(&self, root: &Path, path: &str) -> Result<String> {
        let full = Path::new(path);
        let value = if full.is_absolute() {
            full.strip_prefix(root)
                .map_err(|_| VcsError("Lore 경로가 작업 트리 밖을 가리킵니다.".into()))?
                .to_string_lossy()
                .into_owned()
        } else {
            path.replace('\\', "/")
        };
        let value = value.trim_start_matches("./");
        text::validate_relative(root, value)
    }
    fn json_events(
        &self,
        root: &Path,
        args: &[String],
        post_author_lookup: bool,
        cancel: &CancelToken,
    ) -> Result<Vec<Value>> {
        let mut full = vec![
            "--repository".into(),
            absolute(root)?.to_string_lossy().into_owned(),
            "--json".into(),
            "--no-pager".into(),
        ];
        full.extend(args.iter().cloned());
        let result = self.run(root, full, cancel)?;
        if !result.success() {
            return Err(VcsError(format!(
                "Lore JSON 명령 실패 (Lore 0.13 이상 필요):\n{}",
                result.output()
            )));
        }
        let mut events = Vec::new();
        let (
            mut complete,
            mut succeeded,
            mut author_lookup_started,
            mut author_done,
            mut awaiting_failure,
        ) = (0, false, false, false, false);
        for line in result.stdout.lines().filter(|l| !l.trim().is_empty()) {
            cancel.check()?;
            let v: Value = serde_json::from_str(line)
                .map_err(|_| VcsError("Lore JSON-lines 출력 형식을 인식할 수 없습니다.".into()))?;
            let tag = v
                .get("tagName")
                .and_then(Value::as_str)
                .ok_or_else(|| VcsError("Lore 이벤트 tagName이 없습니다.".into()))?;
            let data = v
                .get("data")
                .filter(|x| x.is_object())
                .ok_or_else(|| VcsError("Lore 이벤트 data 개체가 없습니다.".into()))?;
            if succeeded && post_author_lookup {
                if author_done {
                    return Err("Lore 완료 뒤 예상하지 못한 추가 이벤트가 있습니다.".into());
                }
                if !awaiting_failure
                    && tag == "authUserInfo"
                    && data
                        .get("id")
                        .and_then(Value::as_str)
                        .is_some_and(|s| !s.is_empty())
                    && data.get("name").and_then(Value::as_str).is_some()
                {
                    author_lookup_started = true;
                    continue;
                }
                if !author_lookup_started
                    && tag == "log"
                    && data.get("level").and_then(Value::as_str) == Some("error")
                    && data.get("location").and_then(Value::as_str) == Some("lore_revision::relay")
                    && data
                        .get("message")
                        .and_then(Value::as_str)
                        .is_some_and(|s| {
                            s.starts_with("No remote configured\n")
                                && s.contains("lore\\src\\auth.rs:")
                        })
                {
                    author_lookup_started = true;
                    awaiting_failure = true;
                    continue;
                }
                if tag == "complete"
                    && author_lookup_started
                    && ((awaiting_failure
                        && data.get("status").and_then(Value::as_i64) == Some(111)
                        && data
                            .get("error")
                            .and_then(|e| e.get("errorCode"))
                            .and_then(Value::as_i64)
                            == Some(111)
                        && data
                            .get("error")
                            .and_then(|e| e.get("message"))
                            .and_then(Value::as_str)
                            == Some("No remote configured"))
                        || (!awaiting_failure
                            && data.get("status").and_then(Value::as_i64) == Some(0)
                            && data
                                .get("error")
                                .and_then(|e| e.get("errorCode"))
                                .and_then(Value::as_i64)
                                == Some(0)
                            && data
                                .get("error")
                                .and_then(|e| e.get("message"))
                                .and_then(Value::as_str)
                                == Some("")))
                {
                    awaiting_failure = false;
                    author_done = true;
                    continue;
                }
                return Err("Lore 작성자 조회 후속 이벤트 형식이 예상과 다릅니다.".into());
            }
            if tag == "error" {
                return Err(VcsError(format!("Lore 오류: {data}")));
            }
            if tag == "complete" {
                complete += 1;
                let status = data
                    .get("status")
                    .and_then(Value::as_i64)
                    .ok_or_else(|| VcsError("Lore 완료 이벤트 상태가 없습니다.".into()))?;
                if status != 0 {
                    return Err(VcsError(format!("Lore 명령이 성공하지 않았습니다: {data}")));
                }
                succeeded = true;
            }
            events.push(v);
        }
        if complete != 1
            || !succeeded
            || (post_author_lookup && author_lookup_started && !author_done)
            || awaiting_failure
        {
            return Err("Lore 완료 이벤트가 누락되었거나 중복되었습니다.".into());
        }
        Ok(events)
    }
}

impl VcsProvider for LoreProvider {
    fn kind(&self) -> VcsKind {
        VcsKind::Lore
    }
    fn executable(&self) -> &str {
        &self.executable
    }
    fn capabilities(&self) -> Capabilities {
        Capabilities {
            stage: true,
            unstage: true,
            commit: true,
            update: true,
            push: true,
            initialize: true,
            clone: true,
            blame: true,
            comparison: true,
            history: true,
            ..Default::default()
        }
    }
    fn repository(&self, path: &Path, cancel: &CancelToken) -> Result<RepositoryInfo> {
        let root = self.root(path)?;
        let out = self.repo_run(&root, ["status".into(), "--scan".into()], cancel)?;
        let (branch, _) = parse_status(&out.stdout, &root)?;
        let config = fs::read_to_string(root.join(".lore/config.toml")).unwrap_or_default();
        let remote = config
            .lines()
            .find_map(|l| {
                let (k, v) = l.split_once('=')?;
                if k.trim() == "remote_url" {
                    Some(v.trim().trim_matches('"').to_owned())
                } else {
                    None
                }
            })
            .unwrap_or_default();
        Ok(RepositoryInfo {
            kind: VcsKind::Lore,
            root,
            branch,
            summary: remote,
        })
    }
    fn status(&self, root: &Path, cancel: &CancelToken) -> Result<Vec<FileChange>> {
        let root = self.root(root)?;
        let out = self.repo_run(&root, ["status".into(), "--scan".into()], cancel)?;
        Ok(parse_status(&out.stdout, &root)?.1)
    }
    fn diff(
        &self,
        root: &Path,
        file: Option<&FileChange>,
        _staged: bool,
        cancel: &CancelToken,
    ) -> Result<String> {
        let root = self.root(root)?;
        let mut a = vec!["diff".into()];
        if let Some(f) = file {
            a.extend(["--".into(), self.safe_path(&root, &f.path)?])
        }
        Ok(self.repo_run(&root, a, cancel)?.stdout)
    }
    fn history(
        &self,
        root: &Path,
        _scope: HistoryScope,
        cancel: &CancelToken,
    ) -> Result<HistorySnapshot> {
        self.history_snapshot(root, cancel)
    }
    fn execute(&self, request: &VcsRequest, cancel: &CancelToken) -> Result<CommandResult> {
        let root = absolute(&request.working_directory)?;
        let args: Vec<String> = if request.paths.is_empty() {
            vec![]
        } else {
            request
                .paths
                .iter()
                .map(|p| self.safe_path(&root, p))
                .collect::<Result<_>>()?
        };
        match request.operation {
            Operation::Stage => {
                if args.is_empty() {
                    return Err("Lore stage에는 하나 이상의 선택 경로가 필요합니다.".into());
                }
                let mut a = vec!["stage".into(), "--scan".into(), "--".into()];
                a.extend(args);
                self.repo_run(&root, a, cancel)
            }
            Operation::Unstage => {
                if args.is_empty() {
                    return Err("Lore unstage에는 하나 이상의 선택 경로가 필요합니다.".into());
                }
                let mut a = vec!["unstage".into(), "--".into()];
                a.extend(args);
                self.repo_run(&root, a, cancel)
            }
            Operation::Commit => {
                if request.message.trim().is_empty() {
                    return Err("Lore 커밋 메시지를 입력하세요.".into());
                }
                self.repo_run(
                    &root,
                    ["commit".into(), "--".into(), request.message.clone()],
                    cancel,
                )
            }
            Operation::Update => self.repo_run(&root, ["sync".into()], cancel),
            Operation::Push => self.repo_run(&root, ["push".into()], cancel),
            Operation::Initialize => {
                fs::create_dir_all(&root)?;
                self.req(
                    &root,
                    vec!["--offline".into(), "repository".into(), "create".into()],
                    cancel,
                )
            }
            Operation::Clone => {
                let remote = text::validate_remote_url(&request.remote)?;
                if request.destination.as_os_str().is_empty() {
                    return Err("Lore clone 대상 폴더를 지정하세요.".into());
                }
                let dest = absolute(&request.destination)?;
                if dest.is_file() || (dest.is_dir() && fs::read_dir(&dest)?.next().is_some()) {
                    return Err("Lore clone 대상 폴더는 비어 있어야 합니다.".into());
                }
                let parent = dest
                    .parent()
                    .ok_or_else(|| VcsError("clone 대상 상위 경로가 없습니다.".into()))?;
                fs::create_dir_all(parent)?;
                self.req(
                    parent,
                    vec![
                        "clone".into(),
                        "--".into(),
                        remote,
                        dest.to_string_lossy().into_owned(),
                    ],
                    cancel,
                )
            }
            _ => Err(VcsError(format!(
                "Lore에서는 {:?} 작업을 지원하지 않습니다.",
                request.operation
            ))),
        }
    }
    fn comparison(
        &self,
        root: &Path,
        file: &FileChange,
        staged: bool,
        cancel: &CancelToken,
    ) -> Result<FileComparison> {
        self.comparison_impl(root, file, staged, cancel)
    }
    fn blame(&self, root: &Path, file: &FileChange, cancel: &CancelToken) -> Result<BlameResult> {
        self.blame_impl(root, file, cancel)
    }
    fn history_details(
        &self,
        root: &Path,
        revision: &str,
        cancel: &CancelToken,
    ) -> Result<HistoryDetails> {
        self.history_details_impl(root, revision, cancel)
    }
    fn history_diff(
        &self,
        root: &Path,
        revision: &str,
        file: Option<&FileChange>,
        cancel: &CancelToken,
    ) -> Result<String> {
        self.history_diff_impl(root, revision, file, cancel)
    }
}

fn absolute(p: &Path) -> Result<PathBuf> {
    Ok(if p.is_absolute() {
        p.to_path_buf()
    } else {
        std::env::current_dir()?.join(p)
    })
}
fn parse_status(output: &str, root: &Path) -> Result<(String, Vec<FileChange>)> {
    let mut branch = None;
    let mut section = None;
    let mut saw = false;
    let mut rows: BTreeMap<String, FileChange> = BTreeMap::new();
    for raw in output.lines() {
        let line = strip_ansi(raw).trim_end().to_owned();
        if line.is_empty() {
            continue;
        }
        if let Some(rest) = line.strip_prefix("On branch ") {
            let Some((name, tail)) = rest.rsplit_once(" revision ") else {
                return Err(status_error());
            };
            if name.is_empty() || tail.split_once(" -> ").is_none() {
                return Err(status_error());
            }
            branch = Some(name.to_owned());
            section = None;
            continue;
        }
        if [
            "Changes staged for commit:",
            "Changes in conflict:",
            "Changes not staged for commit:",
            "Untracked files:",
        ]
        .contains(&line.as_str())
        {
            section = Some(line);
            saw = true;
            continue;
        }
        if line.starts_with("Repository ")
            || line.starts_with("Local branch ")
            || line.starts_with("Pending merge, incoming revision ")
            || line.starts_with("Remote revision ")
            || line.starts_with("Remote reachable but could not read remote revision ")
            || line == "Remote branch does not exist"
            || line == "No tracked changes"
            || line.starts_with("Tracked changes:")
            || line.starts_with("Repository size:")
        {
            section = None;
            continue;
        }
        let sec = section.as_deref().ok_or_else(status_error)?;
        let content = raw.trim_start();
        let mut chars = content.chars();
        let action = chars
            .next()
            .filter(|c| c.is_ascii_uppercase())
            .ok_or_else(status_error)?;
        let rest = chars.as_str().trim_start();
        if !matches!(action, 'A' | 'D' | 'M' | 'V' | 'C') || rest.is_empty() {
            return Err(status_error());
        }
        let mut path = rest.to_owned();
        if sec == "Changes in conflict:" {
            if path.ends_with("?!") {
                path.truncate(path.len() - 2)
            } else if path.ends_with('!') {
                path.pop();
            }
        } else if sec == "Changes staged for commit:" && path.ends_with(" (M)") {
            path.truncate(path.len() - 4)
        }
        let original = if matches!(action, 'V' | 'C') {
            let i = path.find(" -> ").ok_or_else(status_error)?;
            (Some(path[..i].to_owned()), path[i + 4..].to_owned())
        } else {
            (None, path)
        };
        if original.1.is_empty() {
            return Err(status_error());
        }
        let rel = text::validate_relative(root, &original.1)?;
        let orig = original
            .0
            .map(|p| text::validate_relative(root, &p))
            .transpose()?;
        let staged = sec == "Changes staged for commit:" || sec == "Changes in conflict:";
        let conflict = sec == "Changes in conflict:";
        let row = rows.entry(rel.clone()).or_insert_with(|| FileChange {
            path: rel.clone(),
            ..Default::default()
        });
        if staged {
            row.index_status = (action as char).to_string()
        } else {
            row.worktree_status = (action as char).to_string()
        }
        row.status = if conflict {
            "conflicted".into()
        } else {
            action.to_string()
        };
        row.original_path = orig.or(row.original_path.take());
    }
    if branch.is_none() || (!saw && !rows.is_empty()) {
        return Err(status_error());
    }
    Ok((branch.unwrap(), rows.into_values().collect()))
}
fn status_error() -> VcsError {
    VcsError("Lore status 출력 형식을 인식할 수 없습니다. 안전을 위해 상태를 clean으로 간주하지 않았습니다.".into())
}
fn strip_ansi(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = String::new();
    let mut i = 0;
    while i < b.len() {
        if b[i] == 0x1b && i + 1 < b.len() && b[i + 1] == b'[' {
            i += 2;
            while i < b.len() && !(0x40..=0x7e).contains(&b[i]) {
                i += 1
            }
            if i < b.len() {
                i += 1
            }
        } else {
            let c = s[i..].chars().next().unwrap();
            out.push(c);
            i += c.len_utf8()
        }
    }
    out
}
